using C64.Core.Cia;
using C64.Core.Cpu;
using C64.Core.Memory;
using Xunit;

namespace C64.Core.Tests;

/// <summary>
/// Joystick Port 2 shares CIA1 Port A ($DC00) with the keyboard columns.
/// The joystick switches overpower the CIA's high drive, so bits 0-4 of a
/// $DC00 read reflect the joystick (wired-AND with the column latch) even
/// though DDRA=$FF, which is how the Kernal leaves it and how games read it.
/// </summary>
public class JoystickTests
{
    private static C64Bus CreateBus()
    {
        var basic = File.ReadAllBytes("/home/hatch/workspace/user/files/c-64-basic.rom");
        var kernal = File.ReadAllBytes("/home/hatch/workspace/user/files/c-64-kernal.rom");
        var chargen = File.ReadAllBytes("/home/hatch/workspace/user/files/c-64-chargen.rom");
        return new C64Bus(basic, kernal, chargen);
    }

    [Fact]
    public void PortA_Read_Reflects_Joystick_With_Kernal_Default_Ddr()
    {
        var bus = CreateBus();
        // Kernal leaves DDRA=$FF (all outputs, driving keyboard columns).
        bus.Write(0xDC02, 0xFF);
        bus.Write(0xDC00, 0xFF); // latch: no column driven
        // Press Right (bit 3, active-low).
        bus.SetJoystickPort2(0xF7);
        Assert.Equal(0xF7, bus.Read(0xDC00));
        // Release.
        bus.SetJoystickPort2(0xFF);
        Assert.Equal(0xFF, bus.Read(0xDC00));
    }

    [Fact]
    public void PortA_Joystick_Bits_Wired_And_With_Column_Latch()
    {
        var bus = CreateBus();
        bus.Write(0xDC02, 0xFF);
        // Kernal drives column 0 (bit 0 low). Joystick untouched: the driven
        // column reads back low, like on real hardware.
        bus.Write(0xDC00, 0xFE);
        bus.SetJoystickPort2(0xFF);
        Assert.Equal(0xFE, bus.Read(0xDC00));
        // Joystick Up pressed too: still low (wired-AND).
        bus.SetJoystickPort2(0xFE);
        Assert.Equal(0xFE, bus.Read(0xDC00));
        // Column released, Up still pressed: bit 0 stays low from the joystick.
        bus.Write(0xDC00, 0xFF);
        Assert.Equal(0xFE, bus.Read(0xDC00));
    }

    [Fact]
    public void PortA_Bits5_7_Follow_Latch()
    {
        var bus = CreateBus();
        bus.Write(0xDC02, 0xFF);
        bus.Write(0xDC00, 0xDF); // bit 5 low in the latch
        bus.SetJoystickPort2(0xE0); // all joystick directions pressed
        // Bits 5-7 come from the latch; bits 0-4 from the joystick.
        Assert.Equal(0xC0, bus.Read(0xDC00) & 0xE0);
        Assert.Equal(0x00, bus.Read(0xDC00) & 0x1F);
    }

    [Fact]
    public void PortA_Joystick_Visible_When_Ddr_Selects_Input()
    {
        var bus = CreateBus();
        bus.Write(0xDC02, 0x00); // all inputs (power-on state)
        bus.SetJoystickPort2(0xEF); // Fire (bit 4) pressed
        // With DDRA=inputs the whole port reads the joystick byte.
        Assert.Equal(0xEF, bus.Read(0xDC00));
    }

    [Fact]
    public void Boot_Progresses_With_Joystick_Wired()
    {
        // Regression test: wiring the joystick into CIA1 Port A must not
        // hang the boot (Alpha 2.4). The headless harness never reaches
        // READY. (pre-existing: the Kernal stalls at $E5D4 here), so assert
        // the reset routine runs and progresses deep into Kernal init
        // instead of wedging at the reset entry.
        var bus = CreateBus();
        var cpu = new Cpu6510(bus);
        cpu.IrqLine = bus.GetIrq;
        bus.Vic.GetCpuCycles = () => cpu.TotalCycles;
        bus.GetCpuCycles = () => cpu.TotalCycles;
        cpu.Reset();
        cpu.RunForCycles(3000000);

        ushort pc = cpu.PC;
        Assert.NotEqual(0xFCE2, pc); // not stuck at the reset entry
        Assert.True(pc >= 0xE000, $"PC ${pc:X4} never reached the Kernal");
    }

    [Fact]
    public void Keyboard_Scan_Unaffected_By_Joystick()
    {
        var bus = CreateBus();
        var (col, row) = KeyboardMatrix.Key("A"); // (1, 2)
        bus.Keyboard.SetKey(col, row, true);
        bus.Write(0xDC03, 0x00); // DDRB = inputs (rows)
        bus.Write(0xDC02, 0xFF); // DDRA = outputs (columns)
        byte columnDrive = (byte)~(1 << col); // drive A's column low

        foreach (byte joy in new byte[] { 0xFF, 0xE0, 0x00 })
        {
            bus.SetJoystickPort2(joy);
            bus.Write(0xDC00, columnDrive);
            byte rows = bus.Read(0xDC01);
            // A's row bit must read low regardless of joystick state.
            Assert.Equal(0, rows & (1 << row));
        }

        bus.Keyboard.SetKey(col, row, false);
    }
}
