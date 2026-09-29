using C64.Core.Memory;
using C64.Core.Vic;
using Xunit;

namespace C64.Core.Tests;

public class VicTests
{
    private sealed class Harness
    {
        public readonly byte[] Ram = new byte[65536];
        public readonly byte[] Charom = new byte[4096];
        public readonly byte[] ColorRam = new byte[1024];
        public long Cycles;
        public readonly VicIi Vic;

        public Harness()
        {
            Vic = new VicIi(Ram, Charom, ColorRam, getBank: () => 0)
            {
                GetCpuCycles = () => Cycles
            };
        }
    }

    [Fact]
    public void PowerOn_DefaultsMatchWhatTheKernalExpects()
    {
        var h = new Harness();
        // Raster is at line 0, so $D011 bit 7 reads back 0.
        Assert.Equal(0x1B, h.Vic.ReadRegister(0xD011));
        Assert.Equal(0x15, h.Vic.ReadRegister(0xD018));
        Assert.Equal(0x0E, h.Vic.ReadRegister(0xD020));
        Assert.Equal(0x06, h.Vic.ReadRegister(0xD021));
        Assert.Equal(0x00, h.Vic.ReadRegister(0xD015));
        Assert.Equal(0x00, h.Vic.ReadRegister(0xD000));
    }

    [Fact]
    public void Registers_MirrorEvery64Bytes()
    {
        var h = new Harness();
        h.Vic.WriteRegister(0xD020, 0x02);
        Assert.Equal(0x02, h.Vic.ReadRegister(0xD060));
        Assert.Equal(0x02, h.Vic.ReadRegister(0xD3E0));
        // ...but the bus only routes $D000-$D3FF to the VIC.
    }

    [Fact]
    public void UnconnectedRegion_ReadsFF()
    {
        var h = new Harness();
        Assert.Equal(0xFF, h.Vic.ReadRegister(0xD02F));
        Assert.Equal(0xFF, h.Vic.ReadRegister(0xD03F));
        h.Vic.WriteRegister(0xD02F, 0x00); // writes ignored
        Assert.Equal(0xFF, h.Vic.ReadRegister(0xD02F));
    }

    [Fact]
    public void D016_UnconnectedBitsReadAsOne()
    {
        var h = new Harness();
        h.Vic.WriteRegister(0xD016, 0x05); // what the Kernal leaves behind
        Assert.Equal(0xC5, h.Vic.ReadRegister(0xD016));
    }

    [Fact]
    public void D019_NoLatch_Reads70()
    {
        var h = new Harness();
        Assert.Equal(0x70, h.Vic.ReadRegister(0xD019));
    }

    [Fact]
    public void D01A_UnconnectedBitsReadAsOne()
    {
        var h = new Harness();
        Assert.Equal(0xF0, h.Vic.ReadRegister(0xD01A));
    }

    [Fact]
    public void Raster_AdvancesWithCpuCycles_PalTiming()
    {
        var h = new Harness();
        Assert.Equal(0, h.Vic.RasterLine);
        Assert.Equal(0, h.Vic.ReadRegister(0xD012));

        h.Cycles = 63;
        Assert.Equal(1, h.Vic.RasterLine);
        Assert.Equal(1, h.Vic.ReadRegister(0xD012));

        h.Cycles = 63L * 311;
        Assert.Equal(311, h.Vic.RasterLine);

        h.Cycles = 63L * 312; // wraps
        Assert.Equal(0, h.Vic.RasterLine);
    }

    [Fact]
    public void D011_Bit7_ReadsCurrentRasterBit8()
    {
        var h = new Harness();
        h.Cycles = 63L * 100;
        Assert.Equal(0x1B, h.Vic.ReadRegister(0xD011)); // line 100: bit 8 clear
        h.Cycles = 63L * 300;
        Assert.Equal(0x9B, h.Vic.ReadRegister(0xD011)); // line 300: bit 8 set
    }

    [Fact]
    public void RasterLatch_SetsOnCompareMatch_ClearsOnWrite()
    {
        var h = new Harness(); // compare = 0 (power-on)
        h.Cycles = 63L * 311;
        Assert.Equal(0x70, h.Vic.ReadRegister(0xD019)); // not yet

        h.Cycles = 63L * 312; // raster enters line 0 == compare
        Assert.Equal(0x71, h.Vic.ReadRegister(0xD019)); // latch set

        h.Vic.WriteRegister(0xD019, 0x01); // write 1 to clear
        Assert.Equal(0x70, h.Vic.ReadRegister(0xD019));

        // Writing 0 does not clear.
        h.Cycles = 63L * 312 * 2; // next frame, latch sets again
        Assert.Equal(0x71, h.Vic.ReadRegister(0xD019));
        h.Vic.WriteRegister(0xD019, 0x00);
        Assert.Equal(0x71, h.Vic.ReadRegister(0xD019));
    }

    [Fact]
    public void RasterLatch_UsesBit8OfCompare()
    {
        var h = new Harness();
        // Compare = 256: $D011 bit 7 (RST8) = 1, $D012 = 0. Keep DEN on.
        h.Vic.WriteRegister(0xD011, 0x90);
        h.Vic.WriteRegister(0xD012, 0x00);

        h.Cycles = 63L * 255;
        Assert.Equal(0x70, h.Vic.ReadRegister(0xD019));

        h.Cycles = 63L * 256; // raster enters line 256 == compare
        Assert.Equal(0x71, h.Vic.ReadRegister(0xD019));
    }

    [Fact]
    public void RasterLatch_SurvivesLongGapsBetweenReads()
    {
        var h = new Harness(); // compare = 0
        h.Cycles = 0;
        _ = h.Vic.ReadRegister(0xD012);
        h.Cycles = 63L * 312 * 10 + 123; // ten frames later, mid-frame
        Assert.Equal(0x71, h.Vic.ReadRegister(0xD019));
    }

    private static byte[] Frame(Harness h)
    {
        var frame = new byte[VicIi.FrameWidth * VicIi.FrameHeight];
        h.Vic.RenderFrame(frame);
        return frame;
    }

    [Fact]
    public void Render_TextMode_DrawsCharWithFgBgAndBorder()
    {
        var h = new Harness();
        // Screen code 1, row 0 all pixels set, rest clear.
        h.Charom[1 * 8 + 0] = 0xFF;
        h.Ram[0x0400] = 1;      // screen RAM $0400 (bank 0, $D018=$15)
        h.ColorRam[0] = 0x02;   // red foreground

        var f = Frame(h);
        // Power-on: $D016=$00 -> 38-col (CSEL=0), xscroll=0 -> orgX=24,
        // window X starts at 31; yscroll=3 -> orgY=51 (fbY=35).
        Assert.Equal(0x02, f[35 * VicIi.FrameWidth + 31]); // fg pixel
        Assert.Equal(0x06, f[43 * VicIi.FrameWidth + 31]); // bg pixel (row 1)
        Assert.Equal(0x0E, f[0]);                          // border (top-left)
        Assert.Equal(0x0E, f[35 * VicIi.FrameWidth + 24]); // border, left of window
    }

    [Fact]
    public void Render_38ColumnMode_ClipsLeftEdge()
    {
        var h = new Harness();
        h.Charom[1 * 8 + 0] = 0xFF;
        h.Ram[0x0400] = 1;
        h.ColorRam[0] = 0x02;
        h.Vic.WriteRegister(0xD016, 0x05); // 38 cols, XSCROLL=5 (Kernal state)

        var f = Frame(h);
        // Window now starts at dot 31; graphics at dot 24+5=29.
        Assert.Equal(0x0E, f[35 * VicIi.FrameWidth + 24]); // clipped: border
        Assert.Equal(0x0E, f[35 * VicIi.FrameWidth + 30]); // clipped: border
        Assert.Equal(0x02, f[35 * VicIi.FrameWidth + 31]); // first visible text pixel
    }

    [Fact]
    public void Render_DenOff_BlanksToBorder()
    {
        var h = new Harness();
        h.Vic.WriteRegister(0xD011, 0x0B); // DEN=0
        var f = Frame(h);
        Assert.All(f, p => Assert.Equal(0x0E, p));
    }

    [Fact]
    public void Bus_RoutesVicAndStoresCia()
    {
        var bus = new C64Bus(
            new byte[C64Bus.BasicSize], new byte[C64Bus.KernalSize], new byte[C64Bus.CharomSize]);

        // VIC is live, not a stub.
        Assert.Equal(0x1B, bus.Read(0xD011));
        bus.Write(0xD020, 0x02);
        Assert.Equal(0x02, bus.Read(0xD3E0)); // mirror

        // CIA is live: with DDRA=$FF the port latch reads back;
        // untouched CIA1 port A (all inputs) reads $FF (pull-ups).
        bus.Write(0xDD02, 0xFF);
        bus.Write(0xDD00, 0x07);
        Assert.Equal(0x07, bus.Read(0xDD00));
        Assert.Equal(0xFF, bus.Read(0xDC00));
        bus.Write(0xDC0E, 0x08);
        Assert.Equal(0x08, bus.Read(0xDC0E));
        Assert.Equal(0x00, bus.Read(0xDC0D));
        Assert.Equal(0x00, bus.Read(0xDD0D));
    }
}
