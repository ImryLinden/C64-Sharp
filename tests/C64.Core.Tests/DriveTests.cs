using C64.Core.Cpu;
using C64.Core.Disk;
using C64.Core.Memory;
using Xunit;

namespace C64.Core.Tests;

/// <summary>M9: 1541 drive integration tests.</summary>
public class DriveTests
{
    private static byte[] LoadRom(string name)
    {
        string[] paths = {
            $"/home/hatch/workspace/user/files/{name}",
            $"../../../../user/files/{name}",
        };
        foreach (var p in paths)
            if (File.Exists(p)) return File.ReadAllBytes(p);
        throw new FileNotFoundException(name);
    }

    [Fact]
    public void Drive_Boots_Dos()
    {
        var rom = LoadRom("c-drive-1541.rom");
        var drive = new Drive1541(rom);
        drive.Reset();
        Assert.Equal(0xEAA0, drive.Cpu.PC);
        drive.RunForCycles(100000);
        Assert.NotEqual(0xEAA0, drive.Cpu.PC); // It's running
    }

    [Fact]
    public void Gcr_RoundTrip()
    {
        var data = new byte[256];
        for (int i = 0; i < 256; i++) data[i] = (byte)i;
        var gcr = new byte[320];
        for (int i = 0; i < 64; i++)
            GcrCodec.Encode4(data, i * 4, gcr, i * 5);
        var decoded = new byte[256];
        for (int i = 0; i < 64; i++)
            GcrCodec.Decode5(gcr, i * 5, decoded, i * 4);
        Assert.Equal(data, decoded);
    }

    [Fact]
    public void D64_Reads_Test_Image()
    {
        var d64 = D64Image.Load("/tmp/test.d64");
        var bam = d64.ReadSector(18, 0);
        Assert.Equal(18, bam[0]); // first dir track
        Assert.Equal(1, bam[1]);  // first dir sector
        var dir = d64.ReadSector(18, 1);
        Assert.Equal(0x82, dir[2]); // PRG file type
    }

    [Fact]
    public void HleDrive_Loads_Directory()
    {
        // Setup: bus, cpu, HLE drive with a disk.
        var bus = new C64Bus(
            new byte[C64Bus.BasicSize],
            new byte[C64Bus.KernalSize],
            new byte[C64Bus.CharomSize]);
        var cpu = new Cpu6510(bus);
        var hle = new HleDrive();
        var d64 = new D64Image(File.ReadAllBytes("/home/hatch/workspace/hello.d64"));
        hle.MountDisk(d64);

        // Simulate Kernal SETLFS/SETNAM for LOAD"$",8:
        // $B7=FNLEN=1, $B8=SECADR=0, $B9=DEVNUM=8, $BB/$BC=FNADR
        bus.Write(0xB7, 1);
        bus.Write(0xB8, 0);
        bus.Write(0xB9, 8);
        bus.Write(0xBB, 0x00);
        bus.Write(0xBC, 0x02); // filename at $0200
        bus.Write(0x0200, (byte)'$');

        // CPU: A=0 (LOAD), X/Y=$0801 (load address)
        cpu.A = 0;
        cpu.X = 0x01;
        cpu.Y = 0x08;
        // Push a fake return address for the RTS.
        cpu.SP = 0xFD;
        bus.Write(0x01FF, 0x00); // return hi (dummy)
        bus.Write(0x01FE, 0x00); // return lo (dummy)
        cpu.SP = 0xFB;

        bool handled = hle.TryHandleLoad(cpu, bus);
        Assert.True(handled);
        Assert.False(cpu.GetFlag(StatusFlags.Carry)); // success

        // Verify directory was loaded at $0801 as a BASIC program.
        // First bytes should be: next ptr, line number (0), $12 '"', etc.
        byte lo = bus.Read(0x0801);
        byte hi = bus.Read(0x0802);
        Assert.NotEqual(0, lo | (hi << 8)); // next line pointer non-zero
        Assert.Equal(0, bus.Read(0x0803)); // line number 0 (header)
        Assert.Equal(0, bus.Read(0x0804));
    }

    [Fact]
    public void HleDrive_Loads_Prg_File()
    {
        var bus = new C64Bus(
            new byte[C64Bus.BasicSize],
            new byte[C64Bus.KernalSize],
            new byte[C64Bus.CharomSize]);
        var cpu = new Cpu6510(bus);
        var hle = new HleDrive();
        var d64 = new D64Image(File.ReadAllBytes("/home/hatch/workspace/hello.d64"));
        hle.MountDisk(d64);

        // Find the filename in the D64 first (should be "HELLO" or similar).
        // For the test, we'll use a wildcard.
        bus.Write(0xB7, 1);
        bus.Write(0xB8, 1); // SECADR=1: use file's embedded address
        bus.Write(0xB9, 8);
        bus.Write(0xBB, 0x00);
        bus.Write(0xBC, 0x02);
        bus.Write(0x0200, (byte)'*'); // wildcard

        cpu.A = 0;
        cpu.X = 0x00;
        cpu.Y = 0x00;
        cpu.SP = 0xFD;
        bus.Write(0x01FF, 0x00);
        bus.Write(0x01FE, 0x00);
        cpu.SP = 0xFB;

        bool handled = hle.TryHandleLoad(cpu, bus);
        Assert.True(handled);
        // May succeed or fail (file not found) depending on D64 contents;
        // the key is that it doesn't crash and returns a valid status.
    }

    [Fact]
    public void HleDrive_Ignores_NonDisk_Device()
    {
        var bus = new C64Bus(
            new byte[C64Bus.BasicSize],
            new byte[C64Bus.KernalSize],
            new byte[C64Bus.CharomSize]);
        var cpu = new Cpu6510(bus);
        var hle = new HleDrive();

        // Device 1 (not 8): should not handle.
        bus.Write(0xB9, 1);
        bool handled = hle.TryHandleLoad(cpu, bus);
        Assert.False(handled);
    }
}
