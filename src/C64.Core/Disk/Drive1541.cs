using C64.Core.Cpu;

namespace C64.Core.Disk;

/// <summary>
/// A 1541 disk drive: 6502 CPU + 2KB RAM + 2x VIA + 16KB DOS ROM.
/// </summary>
public sealed class Drive1541
{
    public DriveBus Bus { get; }
    public Cpu6510 Cpu { get; }
    public DiskHardware? Disk { get; private set; }

    public Drive1541(byte[] dosRom)
    {
        Bus = new DriveBus(dosRom);
        Cpu = new Cpu6510(Bus);
        Cpu.IrqLine = () => Bus.Via1.IrqAsserted || Bus.Via2.IrqAsserted;
    }

    /// <summary>Insert a .d64 image.</summary>
    public void MountDisk(D64Image d64)
    {
        Disk = new DiskHardware(d64);
        Disk.Mount();

        // VIA2: Port A = GCR data in, Port B = stepper/motor.
        Bus.Via2.ReadPortAExternal = () => Disk.ReadGcr();
        Bus.Via2.WritePortBExternal = (pb) => Disk.StepperWrite(pb);
    }

    public void Reset() => Cpu.Reset();

    /// <summary>Run the drive CPU for <paramref name="cycles"/> clocks.</summary>
    public void RunForCycles(long cycles)
    {
        Cpu.RunForCycles(cycles);
        // Clock the VIAs (timers for IEC timeouts, etc.).
        // The CPU runs in chunks; clock VIAs for the same cycles.
        Bus.Via1.Clock((int)Math.Min(cycles, int.MaxValue));
        Bus.Via2.Clock((int)Math.Min(cycles, int.MaxValue));
    }
}
