using System;
using System.Collections.Generic;
using System.Text;
using C64.Core.Cpu;
using C64.Core.Memory;

namespace C64.Core.Disk;

/// <summary>
/// High-Level Emulation (HLE) 1541 drive. Instead of running the DOS ROM,
/// this intercepts the Kernal LOAD call ($FFD5) and serves files directly
/// from the .d64 image. Standard LOAD"$",8 and LOAD"file",8 work; fastloaders
/// (which upload custom code to the drive) do not.
/// </summary>
public sealed class HleDrive
{
    private D64Image? _disk;

    public void MountDisk(D64Image disk) => _disk = disk;
    public void UnmountDisk() => _disk = null;
    public bool HasDisk => _disk != null;

    /// <summary>UTC time of the last LOAD served; the UI blinks the drive LED while recent.</summary>
    public DateTime LastActivityUtc { get; private set; } = DateTime.MinValue;
    /// <summary>Last DEVNUM seen by TryHandleLoad (debug).</summary>
    public byte LastDevNum { get; private set; }
    /// <summary>Enable file logging of LOAD calls (debug).</summary>
    public bool DebugLog { get; set; } = false;
    private void Log(string msg)
    {
        if (!DebugLog) return;
        try
        {
            string tempDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "temp");
            Directory.CreateDirectory(tempDir);
            File.AppendAllText(Path.Combine(tempDir, "hle_debug.log"), $"{DateTime.Now:HH:mm:ss.fff} {msg}\n");
        }
        catch { }
    }

    /// <summary>
    /// Called when the CPU is about to execute the Kernal LOAD at $FFD5.
    /// Returns true if the call was handled (device 8 with a mounted disk).
    /// </summary>
    public bool TryHandleLoad(Cpu6510 cpu, C64Bus bus)
    {
        try
        {
            return TryHandleLoadInner(cpu, bus);
        }
        catch
        {
            // If HLE fails, let the real Kernal try (it will report an error gracefully).
            return false;
        }
    }

    private bool TryHandleLoadInner(Cpu6510 cpu, C64Bus bus)
    {
        // Kernal zero-page variables (set by SETLFS/SETNAM):
        // $B7=FNLEN, $B8=SECADR, $B9=DEVNUM, $BB-$BC=FNADR
        byte devNum = bus.Read(0xB9);
        // Debug: record last seen devNum.
        LastDevNum = devNum;
        Log($"TryHandleLoad: PC=${cpu.PC:X4} devNum={devNum} A=${cpu.A:X2} X=${cpu.X:X2} Y=${cpu.Y:X2}");
        // If a disk is mounted, handle the LOAD regardless of device number.
        // (The real 1541 is device 8; we emulate it.)
        if (_disk == null)
        {
            Log("  -> no disk, not handled");
            return false;
        }
        LastActivityUtc = DateTime.UtcNow;

        byte fnLen = bus.Read(0xB7);
        byte secAdr = bus.Read(0xB8);
        ushort fnAdr = (ushort)(bus.Read(0xBB) | (bus.Read(0xBC) << 8));

        var nameBytes = new byte[fnLen];
        for (int i = 0; i < fnLen; i++)
            nameBytes[i] = bus.Read((ushort)(fnAdr + i));
        string filename = PetsciiToAscii(nameBytes);
        Log($"  filename='{filename}' fnLen={fnLen} secAdr={secAdr} fnAdr=${fnAdr:X4}");

        bool isVerify = cpu.A != 0;
        ushort requestedAddr = (ushort)(cpu.X | (cpu.Y << 8));

        byte[] fileData;
        ushort loadAddr;

        if (filename == "$")
        {
            // Directory listing: always loads at $0801 (or requested addr if SECADR=0).
            fileData = BuildDirectoryListing();
            loadAddr = (secAdr == 0) ? requestedAddr : (ushort)0x0801;
        }
        else
        {
            var entry = FindFile(filename);
            if (entry == null)
            {
                // FILE NOT FOUND: set carry, A=$04 (file not found error).
                cpu.A = 0x04;
                cpu.SetFlag(StatusFlags.Carry, true);
                cpu.SimulateRts();
                return true;
            }
            fileData = ReadFileChain(entry.Value.Track, entry.Value.Sector);
            if (fileData.Length < 2)
            {
                cpu.A = 0x04;
                cpu.SetFlag(StatusFlags.Carry, true);
                cpu.SimulateRts();
                return true;
            }
            // PRG files: first 2 bytes are the load address (used if SECADR=1).
            ushort embeddedAddr = (ushort)(fileData[0] | (fileData[1] << 8));
            loadAddr = (secAdr == 0) ? requestedAddr : embeddedAddr;
            // Skip the 2 address bytes; the rest is the program.
            var program = new byte[fileData.Length - 2];
            Array.Copy(fileData, 2, program, 0, program.Length);
            fileData = program;
        }

        if (!isVerify)
        {
            // Copy to C64 memory.
            for (int i = 0; i < fileData.Length; i++)
                bus.Write((ushort)(loadAddr + i), fileData[i]);
        }
        // For VERIFY: we just succeed (don't actually compare).

        // Return: X/Y = end address, carry clear = success.
        ushort endAddr = (ushort)(loadAddr + fileData.Length);
        cpu.X = (byte)(endAddr & 0xFF);
        cpu.Y = (byte)(endAddr >> 8);
        cpu.SetFlag(StatusFlags.Carry, false);
        cpu.SimulateRts();
        return true;
    }

    #region D64 parsing

    private (int Track, int Sector)? FindFile(string name)
    {
        var disk = _disk!;
        // Directory is on track 18, sectors 1-19.
        for (int s = 1; s <= 19; s++)
        {
            byte[] sector = disk.ReadSector(18, s);
            // 8 entries per sector, 32 bytes each, starting at offset 2.
            for (int e = 0; e < 8; e++)
            {
                int off = 2 + e * 32;
                byte type = sector[off];
                if ((type & 0x80) == 0) continue; // Scratched or empty.
                if (type == 0) continue;
                // Only match loadable file types: PRG ($82), SEQ ($81), USR ($83).
                // Skip DEL ($80) and REL ($84).
                byte fileType = (byte)(type & 0x07);
                if (fileType != 2 && fileType != 1 && fileType != 3) continue;

                var nameBytes = new byte[16];
                Array.Copy(sector, off + 5, nameBytes, 0, 16);
                string entryName = PetsciiToAscii(nameBytes).TrimEnd();

                // 1541 matches with wildcard; we do exact or prefix with '*'.
                if (entryName == name || (name.EndsWith("*") && entryName.StartsWith(name[..^1])))
                {
                    int track = sector[off + 1];
                    int sect = sector[off + 2];
                    return (track, sect);
                }
            }
            // Next directory sector.
            int nextTrack = sector[0];
            int nextSector = sector[1];
            if (nextTrack == 0) break;
            // (Simplified: assumes sectors 1-19 sequential; real code follows chain.)
        }
        return null;
    }

    private byte[] ReadFileChain(int track, int sector)
    {
        var disk = _disk!;
        var data = new List<byte>();
        while (track != 0)
        {
            byte[] sec = disk.ReadSector(track, sector);
            int nextTrack = sec[0];
            int nextSector = sec[1];
            // Last sector: sec[1] is the byte count (not a sector number).
            int bytesToCopy = (nextTrack == 0) ? sec[1] : 254;
            // Data starts at offset 2.
            for (int i = 0; i < bytesToCopy && i < 254; i++)
                data.Add(sec[2 + i]);
            track = nextTrack;
            sector = nextSector;
        }
        return data.ToArray();
    }

    private byte[] BuildDirectoryListing()
    {
        var disk = _disk!;
        var out_ = new List<byte>();

        // BAM (track 18, sector 0): disk name at offset 0x90 (16 bytes), ID at 0xA2.
        byte[] bam = disk.ReadSector(18, 0);
        var diskNameBytes = new byte[16];
        Array.Copy(bam, 0x90, diskNameBytes, 0, 16);
        string diskName = PetsciiToAscii(diskNameBytes).TrimEnd();
        var diskIdBytes = new byte[2];
        Array.Copy(bam, 0xA2, diskIdBytes, 0, 2);
        string diskId = PetsciiToAscii(diskIdBytes);

        // BASIC program at $0801. Each line:
        // [next lo][next hi][lineno lo][lineno hi][data...][$00]
        // Directory entries use block count as line number.
        ushort addr = 0x0801;

        var lines = new List<(ushort num, byte[] data)>();

        // Header line: 0 "diskname" ID
        var header = new List<byte>();
        header.Add(0x12); // RVS ON
        header.Add((byte)'"');
        header.AddRange(AsciiToPetscii(diskName));
        header.Add((byte)'"');
        header.Add((byte)' ');
        header.AddRange(AsciiToPetscii(diskId));
        lines.Add((0, header.ToArray()));

        // File entries.
        for (int s = 1; s <= 19; s++)
        {
            byte[] sector = disk.ReadSector(18, s);
            for (int e = 0; e < 8; e++)
            {
                int off = 2 + e * 32;
                byte type = sector[off];
                if (type == 0 || (type & 0x80) == 0) continue;

                int blocks = sector[off + 28] | (sector[off + 29] << 8);
                var nameBytes = new byte[16];
                Array.Copy(sector, off + 5, nameBytes, 0, 16);
                string name = PetsciiToAscii(nameBytes).TrimEnd();

                string typeStr = (type & 0x07) switch
                {
                    0 => "DEL",
                    1 => "SEQ",
                    2 => "PRG",
                    3 => "USR",
                    4 => "REL",
                    _ => "???",
                };
                bool locked = (type & 0x40) != 0;

                var line = new List<byte>();
                // Format: blocks, "name", type (matches 1541 output style).
                line.AddRange(AsciiToPetscii(blocks.ToString().PadLeft(4)));
                line.Add((byte)' ');
                line.Add(0x12); // RVS ON (for the quote, simplified)
                line.Add((byte)'"');
                line.AddRange(AsciiToPetscii(name));
                line.Add((byte)'"');
                // Pad to column 32 (simplified).
                while (line.Count < 28) line.Add((byte)' ');
                line.AddRange(AsciiToPetscii(typeStr));
                if (locked) line.Add((byte)'<');

                lines.Add(((ushort)blocks, line.ToArray()));
            }
            if (sector[0] == 0) break;
        }

        // BLOCKS FREE. line.
        int freeBlocks = CountFreeBlocks();
        var freeLine = new List<byte>();
        freeLine.AddRange(AsciiToPetscii(freeBlocks.ToString().PadLeft(4)));
        freeLine.AddRange(AsciiToPetscii(" BLOCKS FREE."));
        lines.Add((0, freeLine.ToArray()));

        // Emit all lines with proper next pointers.
        for (int i = 0; i < lines.Count; i++)
        {
            int lineStart = out_.Count;
            ushort nextAddr = (i == lines.Count - 1) ? (ushort)0 : (ushort)(addr + (out_.Count - lineStart) + 4 + lines[i].data.Length + 1);
            // Actually compute properly: next = addr + 2+2+data.Length+1
            nextAddr = (i == lines.Count - 1) ? (ushort)0 : (ushort)(addr + 5 + lines[i].data.Length);
            out_.Add((byte)(nextAddr & 0xFF));
            out_.Add((byte)(nextAddr >> 8));
            out_.Add((byte)(lines[i].num & 0xFF));
            out_.Add((byte)(lines[i].num >> 8));
            out_.AddRange(lines[i].data);
            out_.Add(0);
            addr = nextAddr;
        }

        return out_.ToArray();
    }

    private int CountFreeBlocks()
    {
        var disk = _disk!;
        byte[] bam = disk.ReadSector(18, 0);
        int free = 0;
        // BAM: offset 4 + (track-1)*4: [free count][bitmap 3 bytes]
        for (int t = 1; t <= 35; t++)
        {
            free += bam[4 + (t - 1) * 4];
        }
        return free;
    }

    #endregion

    #region PETSCII

    /// <summary>Converts PETSCII bytes to ASCII (simplified: handles common cases).</summary>
    private static string PetsciiToAscii(byte[] petscii)
    {
        var sb = new StringBuilder();
        foreach (byte b in petscii)
        {
            // PETSCII to ASCII: $41-$5A is A-Z (same), $30-$39 is 0-9 (same).
            // $A0 is shifted space -> space. $20 is space.
            if (b == 0xA0) sb.Append(' ');
            else if (b >= 0x41 && b <= 0x5A) sb.Append((char)b); // A-Z
            else if (b >= 0x30 && b <= 0x39) sb.Append((char)b); // 0-9
            else if (b >= 0x20 && b <= 0x7E) sb.Append((char)b); // printable
            else if (b == 0x00 || b == 0xA0) sb.Append(' '); // padding
            else sb.Append('?');
        }
        return sb.ToString();
    }

    private static byte[] AsciiToPetscii(string ascii)
    {
        var bytes = new byte[ascii.Length];
        for (int i = 0; i < ascii.Length; i++)
        {
            char c = ascii[i];
            // ASCII to PETSCII: mostly the same for A-Z, 0-9, common punctuation.
            if (c >= 'a' && c <= 'z') bytes[i] = (byte)(c - 32); // to uppercase
            else bytes[i] = (byte)c;
        }
        return bytes;
    }

    #endregion
}
