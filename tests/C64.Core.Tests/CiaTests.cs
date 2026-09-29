using C64.Core.Cia;
using Xunit;

namespace C64.Core.Tests;

/// <summary>Tests for the MOS 6526 CIA: timers, ICR, ports, keyboard matrix.</summary>
public class CiaTests
{
    [Fact]
    public void TimerA_Underflow_SetsFlag_And_AssertsIrq_When_Masked()
    {
        var cia = new Cia6526();
        cia.Write(0x04, 0x09); // latch lo = 9
        cia.Write(0x05, 0x00); // latch hi = 0
        cia.Write(0x0D, 0x81); // enable Timer A interrupt (set bit 0)
        cia.Write(0x0E, 0x01); // start Timer A, continuous

        Assert.False(cia.IrqAsserted);
        cia.Tick(5);  // not yet
        Assert.False(cia.IrqAsserted);
        cia.Tick(10); // 10 cycles total -> underflow (latch 9 + 1)

        Assert.True(cia.IrqAsserted);
        byte icr = cia.Read(0x0D);
        Assert.Equal(0x81, icr); // flag bit 0 + IRQ bit 7
        // Reading ICR clears the flags and drops IRQ.
        Assert.False(cia.IrqAsserted);
        Assert.Equal(0x00, cia.Read(0x0D));
    }

    [Fact]
    public void TimerA_Continuous_Reloads_And_Fires_Again()
    {
        var cia = new Cia6526();
        cia.Write(0x04, 0x03);
        cia.Write(0x05, 0x00);
        cia.Write(0x0D, 0x81);
        cia.Write(0x0E, 0x01);

        cia.Tick(4); // first underflow
        Assert.True(cia.IrqAsserted);
        cia.Read(0x0D); // clear

        cia.Tick(8); // second period (4 more cycles)
        Assert.True(cia.IrqAsserted);
    }

    [Fact]
    public void TimerA_OneShot_Stops_After_Underflow()
    {
        var cia = new Cia6526();
        cia.Write(0x04, 0x03);
        cia.Write(0x05, 0x00);
        cia.Write(0x0D, 0x81);
        cia.Write(0x0E, 0x09); // start + one-shot

        cia.Tick(10);
        Assert.True(cia.IrqAsserted);
        cia.Read(0x0D);

        cia.Tick(100); // should not fire again
        Assert.False(cia.IrqAsserted);
        Assert.Equal(0x00, cia.Read(0x0E) & 0x01); // start bit cleared
    }

    [Fact]
    public void Icr_Write_Clears_Mask_Bits_When_Bit7_Zero()
    {
        var cia = new Cia6526();
        cia.Write(0x0D, 0x81); // set bit 0
        cia.Write(0x0D, 0x01); // clear bit 0 (bit 7 = 0)
        cia.Write(0x04, 0x01);
        cia.Write(0x05, 0x00);
        cia.Write(0x0E, 0x01);
        cia.Tick(10);
        // Flag set but masked out -> no IRQ.
        Assert.False(cia.IrqAsserted);
        Assert.Equal(0x01, cia.Read(0x0D)); // flag without IRQ bit
    }

    [Fact]
    public void Ports_Respect_Ddr()
    {
        var cia = new Cia6526();
        // Port A all outputs: reads back the latch.
        cia.Write(0x02, 0xFF); // DDRA
        cia.Write(0x00, 0x5A); // PRA
        Assert.Equal(0x5A, cia.Read(0x00));

        // Port B all inputs with host input $F0: reads the host.
        cia.PortBInput = _ => 0xF0;
        cia.Write(0x03, 0x00); // DDRB
        cia.Write(0x01, 0xAA); // PRB latch (ignored, all inputs)
        Assert.Equal(0xF0, cia.Read(0x01));

        // Mixed: upper nibble output, lower nibble input.
        cia.Write(0x03, 0xF0);
        cia.Write(0x01, 0xA0);
        Assert.Equal(0xA0, cia.Read(0x01));
    }

    [Fact]
    public void KeyboardMatrix_Pressed_Key_Pulls_Row_Low_On_Driven_Column()
    {
        var kbd = new KeyboardMatrix();
        // 'A' is column 1, row 2.
        kbd.SetKey(1, 2, true);

        // Drive column 1 low ($FD), others high.
        byte rows = kbd.ReadRows(0xFD);
        Assert.Equal(0xFB, rows); // row 2 low

        // Drive a different column: no key.
        Assert.Equal(0xFF, kbd.ReadRows(0xFE));

        // Release the key.
        kbd.SetKey(1, 2, false);
        Assert.Equal(0xFF, kbd.ReadRows(0xFD));
    }

    [Fact]
    public void KeyboardMatrix_Named_Keys_Match_Reference()
    {
        Assert.Equal((1, 2), KeyboardMatrix.Key("A"));
        Assert.Equal((7, 4), KeyboardMatrix.Key("SPACE"));
        Assert.Equal((0, 1), KeyboardMatrix.Key("RETURN"));
        Assert.Equal((7, 0), KeyboardMatrix.Key("1"));
    }

    [Fact]
    public void TimerB_Can_Count_TimerA_Underflows()
    {
        var cia = new Cia6526();
        // Timer A: period 2. Timer B: latch 1, counts TA underflows.
        cia.Write(0x04, 0x01); cia.Write(0x05, 0x00);
        cia.Write(0x06, 0x01); cia.Write(0x07, 0x00);
        cia.Write(0x0D, 0x82); // enable Timer B interrupt
        cia.Write(0x0E, 0x01); // start TA
        cia.Write(0x0F, 0x41); // start TB, input = TA underflow

        cia.Tick(4); // two TA underflows -> TB underflows once
        Assert.True(cia.IrqAsserted);
        // TA flag is latched too (unmasked); TB flag + IRQ bit.
        Assert.Equal(0x83, cia.Read(0x0D));
    }
}
