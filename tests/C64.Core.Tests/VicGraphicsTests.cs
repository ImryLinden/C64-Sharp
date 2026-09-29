using C64.Core.Vic;
using Xunit;

namespace C64.Core.Tests;

/// <summary>Tests for VIC-II graphics modes (M6): bitmap, raster IRQ, sprites.</summary>
public class VicGraphicsTests
{
    private static (VicIi Vic, byte[] Ram) CreateVic()
    {
        var ram = new byte[65536];
        // VIC bank 0: VIC addresses map 1:1 to RAM.
        var vic = new VicIi(ram, new byte[4096], new byte[1024], () => 0);
        return (vic, ram);
    }

    private static byte[] Render(VicIi vic)
    {
        var frame = new byte[VicIi.FrameWidth * VicIi.FrameHeight];
        vic.RenderFrame(frame);
        return frame;
    }

    [Fact]
    public void StandardBitmap_Renders_Set_Bits_In_Foreground()
    {
        var (vic, ram) = CreateVic();
        // Bitmap at $2000 (bank 3 -> VIC $2000 = RAM $E000), screen RAM $0400.
        vic.WriteRegister(0x18, 0x18); // VM=1 ($0400), CB bit3=1 ($2000)
        vic.WriteRegister(0x11, 0x3B); // BMM=1, DEN=1, RSEL=1
        vic.WriteRegister(0x16, 0x08); // CSEL=1 (40 cols)
        ram[0x0400] = 0x10; // cell (0,0): fg=white(1), bg=black(0)
        ram[0x2000] = 0xFF; // bitmap row 0, col 0, line 0: all pixels set

        var frame = Render(vic);
        // Playfield origin: orgX=24, first badline orgY=51 -> fb (24, 35).
        Assert.Equal(0x01, frame[35 * VicIi.FrameWidth + 24]);
        // Next cell over (unset bitmap byte) -> background black.
        Assert.Equal(0x00, frame[35 * VicIi.FrameWidth + 32]);
    }

    [Fact]
    public void RasterIrq_Asserts_When_Enabled_And_Latched()
    {
        var (vic, _) = CreateVic();
        long cycles = 99 * 63;
        vic.GetCpuCycles = () => cycles;
        vic.WriteRegister(0x12, 100); // compare = 100
        vic.WriteRegister(0x1A, 0x01); // enable raster IRQ
        vic.ReadRegister(0x19); // sync at line 99
        cycles = 100 * 63; // advance into line 100

        Assert.True(vic.IrqAsserted);
        byte status = vic.ReadRegister(0x19);
        Assert.Equal(0x01, status & 0x01); // latch
        Assert.Equal(0x80, status & 0x80); // IRQ line

        vic.WriteRegister(0x19, 0x01); // write-1-to-clear
        Assert.False(vic.IrqAsserted);
    }

    [Fact]
    public void RasterIrq_Does_Not_Assert_When_Masked()
    {
        var (vic, _) = CreateVic();
        long cycles = 99 * 63;
        vic.GetCpuCycles = () => cycles;
        vic.WriteRegister(0x12, 100);
        vic.ReadRegister(0x19); // sync at line 99
        cycles = 100 * 63; // advance into line 100
        // $D01A = 0: masked -> no IRQ, but latch still sets.
        Assert.False(vic.IrqAsserted);
        Assert.Equal(0x01, vic.ReadRegister(0x19) & 0x01);
    }

    [Fact]
    public void Sprite_Renders_At_Position()
    {
        var (vic, ram) = CreateVic();
        ram[0x0400 + 0x3F8] = 0x80; // sprite 0 pointer -> $2000/64
        for (int i = 0; i < 63; i++) ram[0x2000 + i] = 0xFF; // solid block
        vic.WriteRegister(0x15, 0x01); // enable sprite 0
        vic.WriteRegister(0x00, 100);  // X
        vic.WriteRegister(0x01, 100);  // Y
        vic.WriteRegister(0x27, 0x02); // red

        var frame = Render(vic);
        // Sprite (100,100) -> fb (100, 84).
        Assert.Equal(0x02, frame[84 * VicIi.FrameWidth + 100]);
        // Far left of the window: border (power-on $0E).
        Assert.Equal(0x0E, frame[84 * VicIi.FrameWidth + 10]);
    }

    [Fact]
    public void Sprite_Sprite_Collision_Sets_D01E()
    {
        var (vic, ram) = CreateVic();
        ram[0x0400 + 0x3F8] = 0x80;
        ram[0x0400 + 0x3F9] = 0x81;
        for (int i = 0; i < 63; i++) { ram[0x2000 + i] = 0xFF; ram[0x2040 + i] = 0xFF; }
        vic.WriteRegister(0x15, 0x03); // enable sprites 0 and 1
        vic.WriteRegister(0x00, 100); vic.WriteRegister(0x01, 100);
        vic.WriteRegister(0x02, 110); vic.WriteRegister(0x03, 100); // overlap
        vic.WriteRegister(0x27, 0x02); vic.WriteRegister(0x28, 0x03);

        Render(vic);
        Assert.Equal(0x03, vic.ReadRegister(0x1E)); // sprites 0+1 collided
        Assert.Equal(0x00, vic.ReadRegister(0x1E)); // read clears
    }

    [Fact]
    public void MulticolorBitmap_Uses_Four_Colors()
    {
        var (vic, ram) = CreateVic();
        vic.WriteRegister(0x18, 0x18);
        vic.WriteRegister(0x11, 0x3B); // BMM
        vic.WriteRegister(0x16, 0x18); // MCM=1, CSEL=1 (40 cols)
        vic.WriteRegister(0x21, 0x00); // bg0 = black
        ram[0x0400] = 0x12; // screen: hi=1 (white), lo=2 (red)
        ram[0x2000] = 0b0110_0000; // pairs: 01 (white), 10 (red)

        var frame = Render(vic);
        int y = 35, x0 = 24;
        Assert.Equal(0x01, frame[y * VicIi.FrameWidth + x0]);     // 01 -> white
        Assert.Equal(0x02, frame[y * VicIi.FrameWidth + x0 + 2]); // 10 -> red
    }
}
