namespace C64.Core.Memory;

/// <summary>
/// A flat 64KB RAM used for CPU bring-up and unit tests.
/// Later milestones replace this with the real C64 memory map
/// (PLA banking, VIC-II, SID, CIAs, Kernal/BASIC ROMs).
/// </summary>
public sealed class RamBus : IMemoryBus
{
    private readonly byte[] _ram = new byte[65536];

    public byte Read(ushort address) => _ram[address];

    public void Write(ushort address, byte value) => _ram[address] = value;

    /// <summary>Copies a program into RAM at <paramref name="startAddress"/>.</summary>
    public void LoadProgram(byte[] program, ushort startAddress)
    {
        Array.Copy(program, 0, _ram, startAddress, program.Length);
    }

    /// <summary>Points the 6502 reset vector ($FFFC/$FFFD) at an address.</summary>
    public void SetResetVector(ushort address)
    {
        _ram[0xFFFC] = (byte)(address & 0xFF);
        _ram[0xFFFD] = (byte)(address >> 8);
    }
}
