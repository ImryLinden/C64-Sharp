using System;

namespace C64.Core.Disk;

/// <summary>
/// The IEC serial bus (C64 <-> 1541). Three open-collector lines, wired-AND:
/// a line is LOW if ANY device pulls it low, HIGH otherwise.
/// </summary>
public sealed class IecBus
{
    // Each device's contribution: true = pulling low.
    private bool _c64AtnLow, _c64ClkLow, _c64DataLow;
    private bool _drvClkLow, _drvDataLow;

    // C64 side (CIA2 $DD00 bits).
    public void SetC64Lines(bool atnLow, bool clkLow, bool dataLow)
    {
        _c64AtnLow = atnLow; _c64ClkLow = clkLow; _c64DataLow = dataLow;
    }

    // Drive side (VIA1 PB bits + CA1).
    public void SetDriveLines(bool clkLow, bool dataLow)
    {
        _drvClkLow = clkLow; _drvDataLow = dataLow;
        // Drive never drives ATN.
    }

    // Bus state (what each side reads).
    public bool AtnLow => _c64AtnLow;
    public bool ClkLow => _c64ClkLow || _drvClkLow;
    public bool DataLow => _c64DataLow || _drvDataLow;

    // For the C64 to read (CIA2 $DD00 bits 6,7).
    public bool C64SeesClkLow => ClkLow;
    public bool C64SeesDataLow => DataLow;

    // For the drive to read (VIA1 PB0, PB2, CA1).
    public bool DriveSeesDataLow => DataLow;   // PB0
    public bool DriveSeesClkLow => ClkLow;     // PB2
    public bool DriveSeesAtnLow => AtnLow;     // CA1
}
