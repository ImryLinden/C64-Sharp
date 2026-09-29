using C64.Core.Memory;
using Xunit;

namespace C64.Core.Tests;

public class C64BusTests
{
    private static byte[] Filled(int size, byte marker)
    {
        var rom = new byte[size];
        Array.Fill(rom, marker);
        return rom;
    }

    // Distinct markers so we can tell which chip answers a read.
    private static C64Bus CreateBus() => new(
        Filled(C64Bus.BasicSize, 0xBA),
        Filled(C64Bus.KernalSize, 0xBB),
        Filled(C64Bus.CharomSize, 0xCC));

    [Fact]
    public void DefaultConfig_ShowsBasicKernalAndIo()
    {
        var bus = CreateBus(); // $0001 = $37 after "power-on"

        Assert.Equal(0xBA, bus.Read(0xA000)); // BASIC
        Assert.Equal(0xBA, bus.Read(0xBFFF));
        Assert.Equal(0xBB, bus.Read(0xE000)); // Kernal
        Assert.Equal(0xBB, bus.Read(0xFFFF));
        Assert.Equal(0x00, bus.Read(0xD000)); // VIC register (sprite 0 X)
        Assert.Equal(0x00, bus.Read(0xD400)); // SID: write-only, reads 0
        Assert.Equal(0x00, bus.Read(0xC000)); // plain RAM
    }

    [Fact]
    public void CpuPort_ReadsBackDirectionAndData()
    {
        var bus = CreateBus();

        Assert.Equal(0x2F, bus.Read(0x0000)); // DDR
        Assert.Equal(0x37, bus.Read(0x0001)); // PEEK(1) = 55 on a real C64

        bus.Write(0x0001, 0x30); // all-RAM config
        Assert.Equal(0x30 & 0x2F, bus.Read(0x0001) & 0x2F); // output latch bits stick
    }

    [Fact]
    public void ClearCharen_ShowsCharacterRom()
    {
        var bus = CreateBus();
        bus.Write(0x0001, 0x33); // $37 & ~4: CHAROM instead of I/O

        Assert.Equal(0xCC, bus.Read(0xD000));
        Assert.Equal(0xCC, bus.Read(0xD800)); // char ROM, not color RAM here
        Assert.Equal(0xBA, bus.Read(0xA000)); // BASIC still on
        Assert.Equal(0xBB, bus.Read(0xE000)); // Kernal still on
    }

    [Fact]
    public void ClearHiram_BanksOutKernalAndBasic()
    {
        var bus = CreateBus();
        bus.Write(0x0001, 0x35); // HIRAM=0: Kernal off (and BASIC needs HIRAM too)

        bus.Write(0xE000, 0x11);
        bus.Write(0xA000, 0x22);
        Assert.Equal(0x11, bus.Read(0xE000)); // RAM underneath
        Assert.Equal(0x22, bus.Read(0xA000));
        Assert.Equal(0x00, bus.Read(0xD000)); // I/O still selected (VIC)
    }

    [Fact]
    public void AllRamConfig_MapsEntire64K()
    {
        var bus = CreateBus();
        bus.Write(0x0001, 0x30); // LORAM=HIRAM=CHAREN=0

        foreach (ushort addr in new ushort[] { 0xA000, 0xD000, 0xD800, 0xE000, 0xFFFC })
        {
            bus.Write(addr, 0x77);
            Assert.Equal(0x77, bus.Read(addr));
        }
    }

    [Fact]
    public void WritesFallThroughToRamUnderRom()
    {
        var bus = CreateBus(); // BASIC + Kernal banked in

        bus.Write(0xA000, 0x42);
        bus.Write(0xE000, 0x43);
        Assert.Equal(0xBA, bus.Read(0xA000)); // ROM still answers reads
        Assert.Equal(0xBB, bus.Read(0xE000));

        bus.Write(0x0001, 0x30); // bank everything out
        Assert.Equal(0x42, bus.Read(0xA000)); // ...and the writes landed in RAM
        Assert.Equal(0x43, bus.Read(0xE000));
    }

    [Fact]
    public void ColorRam_StoresFourBits()
    {
        var bus = CreateBus(); // I/O selected

        bus.Write(0xD800, 0x05);
        Assert.Equal(0xF5, bus.Read(0xD800)); // high nibble reads back set

        bus.Write(0xD800, 0x1A);
        Assert.Equal(0xFA, bus.Read(0xD800)); // only the low nibble is stored
    }

    [Fact]
    public void ColorRam_IsSeparateFromMainRam()
    {
        var bus = CreateBus();

        bus.Write(0xD800, 0x07);          // color RAM via I/O
        bus.Write(0x0001, 0x30);          // all-RAM: $D800 is main RAM now
        Assert.Equal(0x00, bus.Read(0xD800)); // untouched
        bus.Write(0xD800, 0x09);
        bus.Write(0x0001, 0x37);          // I/O back
        Assert.Equal(0xF7, bus.Read(0xD800)); // color RAM kept its value
    }

    [Fact]
    public void Constructor_RejectsBadRomSizes()
    {
        Assert.Throws<ArgumentException>(() =>
            new C64Bus(new byte[100], Filled(C64Bus.KernalSize, 0), Filled(C64Bus.CharomSize, 0)));
        Assert.Throws<ArgumentException>(() =>
            new C64Bus(Filled(C64Bus.BasicSize, 0), new byte[8191], Filled(C64Bus.CharomSize, 0)));
    }

    [Fact]
    public void KernalVectors_AreReadable()
    {
        // Fake Kernal with the standard reset vector ($FCE2).
        var kernal = Filled(C64Bus.KernalSize, 0xBB);
        kernal[0xFFFC - 0xE000] = 0xE2;
        kernal[0xFFFD - 0xE000] = 0xFC;
        var bus = new C64Bus(Filled(C64Bus.BasicSize, 0xBA), kernal, Filled(C64Bus.CharomSize, 0xCC));

        Assert.Equal(0xE2, bus.Read(0xFFFC));
        Assert.Equal(0xFC, bus.Read(0xFFFD));
    }

    [Fact]
    public void CpuCanRunFromKernalResetVector()
    {
        // Tiny "Kernal": reset vector -> $E000, which just spins (JMP self).
        var kernal = Filled(C64Bus.KernalSize, 0x00);
        kernal[0xFFFC - 0xE000] = 0x00;
        kernal[0xFFFD - 0xE000] = 0xE0;
        kernal[0] = 0x4C; // JMP $E000
        kernal[1] = 0x00;
        kernal[2] = 0xE0;
        var bus = new C64Bus(Filled(C64Bus.BasicSize, 0xBA), kernal, Filled(C64Bus.CharomSize, 0xCC));

        var cpu = new Cpu.Cpu6510(bus);
        cpu.Reset();
        Assert.Equal(0xE000, cpu.PC); // reset vector honored through the banking
        cpu.Step();
        Assert.Equal(0xE000, cpu.PC); // JMP self: the "ROM" executed
    }
}
