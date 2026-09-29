using C64.Core.Cpu;
using C64.Core.Memory;
using C64.Core.Via;

namespace C64.Core.Disk;

/// <summary>
/// The 1541 disk drive's memory bus: 2KB RAM, two 6522 VIAs, 16KB DOS ROM.
/// </summary>
public sealed class DriveBus : IMemoryBus
{
    private readonly byte[] _ram = new byte[0x800]; // $0000-$07FF
    private readonly byte[] _rom;                    // $C000-$FFFF (16KB)

    public Via6522 Via1 { get; } = new(); // $1800: IEC bus
    public Via6522 Via2 { get; } = new(); // $1C00: drive mechanism

    public DriveBus(byte[] dosRom)
    {
        if (dosRom.Length != 16384)
            throw new ArgumentException("1541 DOS ROM must be 16KB.", nameof(dosRom));
        _rom = dosRom;
    }

    public byte Read(ushort address)
    {
        if (address < 0x0800) return _ram[address];
        if (address >= 0x1800 && address < 0x1810) return Via1.Read(address & 0x0F);
        if (address >= 0x1C00 && address < 0x1C10) return Via2.Read(address & 0x0F);
        if (address >= 0xC000) return _rom[address - 0xC000];
        return 0xFF;
    }

    public void Write(ushort address, byte value)
    {
        if (address < 0x0800) { _ram[address] = value; return; }
        if (address >= 0x1800 && address < 0x1810) { Via1.Write(address & 0x0F, value); return; }
        if (address >= 0x1C00 && address < 0x1C10) { Via2.Write(address & 0x0F, value); return; }
        // ROM: ignore writes.
    }
}
