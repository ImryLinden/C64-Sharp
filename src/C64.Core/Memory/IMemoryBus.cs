namespace C64.Core.Memory;

/// <summary>
/// Abstraction over the C64 address bus. The CPU only ever talks to this
/// interface, which lets us plug in a flat 64KB RAM for tests today and the
/// real PLA/VIC-II/CIA memory map later without touching the CPU.
/// </summary>
public interface IMemoryBus
{
    byte Read(ushort address);
    void Write(ushort address, byte value);
}
