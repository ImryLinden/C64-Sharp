using C64.Core.Cia;
using C64.Core.Memory;

namespace C64.Core.Disk;

/// <summary>
/// Wires the IEC bus between the C64 (CIA2 $DD00) and the 1541 (VIA1 $1800).
/// The C64 drives ATN/CLK/DATA; the drive drives CLK/DATA and reads ATN via CA1.
/// </summary>
public sealed class IecController
{
    private readonly IecBus _bus = new();
    private readonly Drive1541 _drive;
    private bool _atnWasLow;

    public IecController(C64Bus c64, Drive1541 drive)
    {
        _drive = drive;

        // C64 side: CIA2 Port A.
        // Outputs (via 7406 inverter, 1 = pull low): bit3=ATN, bit4=CLK, bit5=DATA.
        // Inputs (direct): bit6=CLK IN, bit7=DATA IN.
        c64.Cia2.PortAOutput = (pra) =>
        {
            bool atnLow = (pra & 0x08) != 0;
            bool clkLow = (pra & 0x10) != 0;
            bool dataLow = (pra & 0x20) != 0;
            _bus.SetC64Lines(atnLow, clkLow, dataLow);
            // ATN -> drive CA1 (interrupt).
            UpdateDriveAtn();
        };
        c64.Cia2.PortAInput = () =>
        {
            // Bits 6,7 are inputs (DDR=0): CLK IN, DATA IN from IEC bus.
            // Bits 0-5 are outputs: handled by PortARead via _pra & _ddra.
            byte v = 0xFF;
            if (_bus.C64SeesClkLow) v &= 0xBF;  // bit6 low
            if (_bus.C64SeesDataLow) v &= 0x7F; // bit7 low
            return v;
        };

        // Drive side: VIA1 Port B.
        // Outputs (inverting, 1 = pull low): bit1=DATA, bit3=CLK.
        // Inputs (direct): bit0=DATA IN, bit2=CLK IN.
        drive.Bus.Via1.WritePortBExternal = (val) =>
        {
            bool dataLow = (val & 0x02) != 0;
            bool clkLow = (val & 0x08) != 0;
            _bus.SetDriveLines(clkLow, dataLow);
        };
        drive.Bus.Via1.ReadPortBExternal = () =>
        {
            byte v = 0xFF;
            if (_bus.DriveSeesDataLow) v &= 0xFE; // bit0 low
            if (_bus.DriveSeesClkLow) v &= 0xFB;  // bit2 low
            // Bits 5-6: device # jumpers (assume device 8 = 0,0).
            v &= 0x9F;
            return v;
        };
    }

    private void UpdateDriveAtn()
    {
        // ATN falling edge -> trigger CA1 interrupt on drive VIA1.
        bool atnLow = _bus.DriveSeesAtnLow;
        if (atnLow && !_atnWasLow)
            _drive.Bus.Via1.TriggerCa1();
        _atnWasLow = atnLow;
    }

    /// <summary>Run the drive CPU for <paramref name="cycles"/>.</summary>
    public void ClockDrive(long cycles) => _drive.RunForCycles(cycles);
}
