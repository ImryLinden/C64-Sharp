using System.Text;
using C64.Core.Cpu;
using C64.Core.Disk;
using C64.Core.Memory;
using Xunit;

namespace C64.Core.Tests;

/// <summary>T64 tape image parsing and HLE tape LOAD via the $FFD5 trap.</summary>
public class T64Tests
{
    private static byte[] BuildT64(string description,
        params (string name, ushort start, byte[] data)[] files)
    {
        var bytes = new List<byte>();
        byte[] sig = Encoding.ASCII.GetBytes("C64 tape image file");
        bytes.AddRange(sig);
        bytes.AddRange(new byte[32 - sig.Length]);
        bytes.Add(0x01); bytes.Add(0x01); // version
        bytes.Add((byte)files.Length); bytes.Add(0);
        bytes.Add((byte)files.Length); bytes.Add(0);
        bytes.Add(0); bytes.Add(0); // reserved
        byte[] desc = Encoding.ASCII.GetBytes(description);
        bytes.AddRange(desc);
        bytes.AddRange(new byte[24 - desc.Length]);
        int dataOff = 64 + files.Length * 32;
        foreach (var (name, start, data) in files)
        {
            bytes.Add(1);    // entry type: normal file
            bytes.Add(0x82); // PRG
            bytes.Add((byte)(start & 0xFF)); bytes.Add((byte)(start >> 8));
            ushort end = (ushort)(start + data.Length);
            bytes.Add((byte)(end & 0xFF)); bytes.Add((byte)(end >> 8));
            bytes.Add(0); bytes.Add(0);
            bytes.Add((byte)(dataOff & 0xFF));
            bytes.Add((byte)((dataOff >> 8) & 0xFF));
            bytes.Add((byte)((dataOff >> 16) & 0xFF));
            bytes.Add((byte)((dataOff >> 24) & 0xFF));
            bytes.Add(0); bytes.Add(0); bytes.Add(0); bytes.Add(0);
            byte[] nb = Encoding.ASCII.GetBytes(name.PadRight(16, ' ').Substring(0, 16));
            bytes.AddRange(nb);
            dataOff += data.Length;
        }
        foreach (var (_, _, data) in files) bytes.AddRange(data);
        return bytes.ToArray();
    }

    [Fact]
    public void T64Image_Parses_Header_And_Entries()
    {
        var t64 = new T64Image(BuildT64("MY TAPE",
            ("FIRST", 0x0801, new byte[] { 1, 2, 3 }),
            ("SECOND", 0x1000, new byte[] { 4, 5 })));
        Assert.Equal("MY TAPE", t64.Description);
        Assert.Equal(2, t64.Entries.Count);
        Assert.Equal("FIRST", t64.Entries[0].Name);
        Assert.Equal(0x0801, t64.Entries[0].StartAddress);
        Assert.Equal(new byte[] { 1, 2, 3 }, t64.Entries[0].Data);
        Assert.Equal(2, t64.Entries[0].FileType); // PRG
        Assert.Equal("SECOND", t64.Entries[1].Name);
        Assert.Equal(0x1000, t64.Entries[1].StartAddress);
    }

    [Fact]
    public void T64Image_Rejects_Bad_Signature()
    {
        var bad = BuildT64("X", ("A", 0x0801, new byte[] { 1 }));
        bad[0] = (byte)'X';
        Assert.Throws<InvalidDataException>(() => new T64Image(bad));
    }

    [Fact]
    public void T64Image_Skips_Free_Slots()
    {
        var raw = BuildT64("X", ("A", 0x0801, new byte[] { 1, 2 }));
        raw[64] = 0; // mark the entry free
        var t64 = new T64Image(raw);
        Assert.Empty(t64.Entries);
    }

    [Fact]
    public void T64Image_Rejects_Truncated_Data()
    {
        var raw = BuildT64("X", ("A", 0x0801, new byte[] { 1, 2, 3, 4 }));
        Array.Resize(ref raw, raw.Length - 2); // chop file data
        Assert.Throws<InvalidDataException>(() => new T64Image(raw));
    }

    private static (C64Bus bus, Cpu6510 cpu, HleDrive hle) SetupTape(
        string fileName, byte secAdr, byte devNum, string tapeFile, byte[] program)
    {
        var basic = File.ReadAllBytes("/home/hatch/workspace/user/files/c-64-basic.rom");
        var kernal = File.ReadAllBytes("/home/hatch/workspace/user/files/c-64-kernal.rom");
        var chargen = File.ReadAllBytes("/home/hatch/workspace/user/files/c-64-chargen.rom");
        var bus = new C64Bus(basic, kernal, chargen);
        var cpu = new Cpu6510(bus);
        var hle = new HleDrive();
        hle.MountTape(new T64Image(BuildT64("TAPE", (tapeFile, 0x0801, program))));
        cpu.PcHook = (pc) => (pc == 0xFFD5 || pc == 0xF49E) && hle.TryHandleLoad(cpu, bus);

        // Simulate BASIC having done SETLFS/SETNAM.
        bus.Write(0xB7, (byte)fileName.Length);
        bus.Write(0xB8, secAdr);
        bus.Write(0xB9, devNum);
        bus.Write(0xBB, 0x00);
        bus.Write(0xBC, 0x02);
        var name = Encoding.ASCII.GetBytes(fileName);
        for (int i = 0; i < name.Length; i++)
            bus.Write((ushort)(0x0200 + i), name[i]);

        cpu.A = 0; // LOAD
        cpu.X = 0x00;
        cpu.Y = 0x00;
        cpu.SP = 0xFD;
        bus.Write(0x01FD, 0x00);
        bus.Write(0x01FC, 0x00);
        cpu.SP = 0xFB;
        cpu.PC = 0xFFD5;
        cpu.Step();
        return (bus, cpu, hle);
    }

    [Fact]
    public void HleDrive_Handles_Tape_Load()
    {
        byte[] program = { 0x0B, 0x08, 0x01, 0x00, 0x9E, 0x00 };
        // LOAD"MYPROG",1,1 : tape device, SECADR=1 (use the tape's load address).
        var (bus, cpu, _) = SetupTape("MYPROG", 1, 1, "MYPROG", program);
        Assert.False(cpu.GetFlag(StatusFlags.Carry));
        for (int i = 0; i < program.Length; i++)
            Assert.Equal(program[i], bus.Read((ushort)(0x0801 + i)));
        ushort end = (ushort)(cpu.X | (cpu.Y << 8));
        Assert.Equal(0x0801 + program.Length, end);
    }

    [Fact]
    public void HleDrive_Tape_Load_Uses_Requested_Address_When_SecAdr_Zero()
    {
        byte[] program = { 0x01, 0x02, 0x03 };
        var basic = File.ReadAllBytes("/home/hatch/workspace/user/files/c-64-basic.rom");
        var kernal = File.ReadAllBytes("/home/hatch/workspace/user/files/c-64-kernal.rom");
        var chargen = File.ReadAllBytes("/home/hatch/workspace/user/files/c-64-chargen.rom");
        var bus = new C64Bus(basic, kernal, chargen);
        var cpu = new Cpu6510(bus);
        var hle = new HleDrive();
        hle.MountTape(new T64Image(BuildT64("TAPE", ("MYPROG", 0x0801, program))));
        cpu.PcHook = (pc) => (pc == 0xFFD5 || pc == 0xF49E) && hle.TryHandleLoad(cpu, bus);

        bus.Write(0xB7, 6); // FNLEN
        bus.Write(0xB8, 0); // SECADR=0 -> use requested address
        bus.Write(0xB9, 1);
        bus.Write(0xBB, 0x00);
        bus.Write(0xBC, 0x02);
        var name = Encoding.ASCII.GetBytes("MYPROG");
        for (int i = 0; i < name.Length; i++)
            bus.Write((ushort)(0x0200 + i), name[i]);

        cpu.A = 0;
        cpu.X = 0x00; // requested load address $1000 (not $0000: that's the CPU port)
        cpu.Y = 0x10;
        cpu.SP = 0xFD;
        bus.Write(0x01FD, 0x00);
        bus.Write(0x01FC, 0x00);
        cpu.SP = 0xFB;
        cpu.PC = 0xFFD5;
        cpu.Step();

        Assert.False(cpu.GetFlag(StatusFlags.Carry));
        for (int i = 0; i < program.Length; i++)
            Assert.Equal(program[i], bus.Read((ushort)(0x1000 + i)));
    }

    [Fact]
    public void HleDrive_Tape_File_Not_Found_Sets_Carry()
    {
        var (_, cpu, _) = SetupTape("NOPE", 1, 1, "MYPROG", new byte[] { 1 });
        Assert.True(cpu.GetFlag(StatusFlags.Carry));
        Assert.Equal(0x04, cpu.A);
    }

    [Fact]
    public void HleDrive_Tape_Directory_Lists_Files()
    {
        var (bus, cpu, _) = SetupTape("$", 1, 1, "MYPROG", new byte[] { 1, 2, 3 });
        Assert.False(cpu.GetFlag(StatusFlags.Carry));
        // A BASIC program: first line link is non-zero, title text present.
        Assert.NotEqual(0, bus.Read(0x0801) | (bus.Read(0x0802) << 8));
        bool found = false;
        for (int a = 0x0801; a < 0x0860 - 4; a++)
            if (bus.Read((ushort)a) == 'T' && bus.Read((ushort)(a + 1)) == 'A'
                && bus.Read((ushort)(a + 2)) == 'P' && bus.Read((ushort)(a + 3)) == 'E')
                found = true;
        Assert.True(found);
    }
}
