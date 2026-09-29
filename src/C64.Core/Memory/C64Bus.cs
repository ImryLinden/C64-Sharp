using System;
using C64.Core.Cia;
using C64.Core.Disk;
using C64.Core.Vic;
using SidChip = C64.Core.Sid.Sid;

namespace C64.Core.Memory;

/// <summary>
/// The Commodore 64 memory map (no cartridge: EXROM/GAME pulled high).
///
/// 64KB of RAM with PLA banking controlled by the 6510's on-chip port
/// ($0000 = direction, $0001 = data). Depending on the LORAM/HIRAM/CHAREN
/// bits, the BASIC ROM ($A000-$BFFF), Kernal ROM ($E000-$FFFF), character
/// ROM ($D000-$DFFF) or the I/O area ($D000-$DFFF) is switched in over
/// the underlying RAM. Writes to a ROM-selected area always reach the
/// RAM underneath ("RAM under ROM").
///
/// $D000-$DFFF with I/O selected decodes to the VIC-II, SID, 1KB of 4-bit
/// color RAM, the two CIAs, and the cartridge I/O areas. The VIC-II is a
/// functional text-mode implementation (M4); the SID and CIAs are register
/// stubs with write storage (M5/M7); color RAM is fully implemented.
/// </summary>
public sealed class C64Bus : IMemoryBus
{
    public const int BasicSize = 8192;
    public const int KernalSize = 8192;
    public const int CharomSize = 4096;

    private readonly byte[] _ram = new byte[65536];
    private readonly byte[] _colorRam = new byte[1024]; // 4-bit cells at $D800-$DBFF
    private readonly byte[] _basic;   // $A000-$BFFF
    private readonly byte[] _kernal;  // $E000-$FFFF
    private readonly byte[] _charom;  // $D000-$DFFF

    /// <summary>The VIC-II chip ($D000-$D3FF).</summary>
    public VicIi Vic { get; }

    /// <summary>The SID chip ($D400-$D41F).</summary>
    public SidChip Sid { get; } = new SidChip();

    /// <summary>CIA1: keyboard, joysticks, system timers, IRQ ($DC00-$DCFF).</summary>
    public Cia6526 Cia1 { get; }

    /// <summary>CIA2: serial bus, VIC bank, NMI ($DD00-$DDFF).</summary>
    public Cia6526 Cia2 { get; }

    /// <summary>The keyboard matrix, scanned through CIA1.</summary>
    public KeyboardMatrix Keyboard { get; } = new KeyboardMatrix();
    private byte _joystickPort2 = 0xFF; // Active-low: bit0=Up,1=Down,2=Left,3=Right,4=Fire
    /// <summary>Set joystick Port 2 state (bits 0-4, active-low).</summary>
    public void SetJoystickPort2(byte state) => _joystickPort2 = state;

    /// <summary>The 1541 disk drive (null if no DOS ROM was provided).</summary>
    public Drive1541? Drive { get; }

    /// <summary>The IEC bus controller wiring C64 to drive (null if no drive).</summary>
    public IecController? Iec { get; }

    /// <summary>
    /// Current CPU cycle count, supplied by the machine. Used to advance
    /// the CIA timers and sample the combined IRQ line.
    /// </summary>
    public Func<long> GetCpuCycles { get; set; } = () => 0;

    // 6510 on-chip port. Power-on state as left by the Kernal:
    // DDR=$2F (bits 0-2,3,5 = outputs), data=$37 (all ROMs + I/O visible).
    private byte _portDir = 0x2F;
    private byte _portData = 0x37;

    public C64Bus(byte[] basic, byte[] kernal, byte[] charom, byte[]? dosRom = null)
    {
        if (basic == null || basic.Length != BasicSize)
            throw new ArgumentException($"BASIC ROM must be {BasicSize} bytes.", nameof(basic));
        if (kernal == null || kernal.Length != KernalSize)
            throw new ArgumentException($"Kernal ROM must be {KernalSize} bytes.", nameof(kernal));
        if (charom == null || charom.Length != CharomSize)
            throw new ArgumentException($"Character ROM must be {CharomSize} bytes.", nameof(charom));
        _basic = basic;
        _kernal = kernal;
        _charom = charom;
        Cia1 = new Cia6526();
        Cia2 = new Cia6526();
        // CIA1 Port B reads the keyboard matrix rows for the driven columns.
        Cia1.PortBInput = pra => Keyboard.ReadRows(pra);
        // Joystick Port 2 disabled for now (causes boot hang).
        // Cia1.PortAInput = () => _joystickPort2;
        Vic = new VicIi(_ram, _charom, _colorRam, getBank: () => (~Cia2.Read(0xDD00) & 3));

        // Optional 1541: wire the IEC bus between C64 CIA2 and drive VIA1.
        if (dosRom != null)
        {
            if (dosRom.Length != 16384)
                throw new ArgumentException("DOS ROM must be 16384 bytes.", nameof(dosRom));
            Drive = new Drive1541(dosRom);
            Iec = new IecController(this, Drive);
        }
    }

    /// <summary>
    /// Samples the combined IRQ line (CIA1 | CIA2 | VIC raster), advancing
    /// the CIA timers to the current CPU cycle first. Wire to Cpu6510.IrqLine.
    /// </summary>
    public bool GetIrq()
    {
        long now = GetCpuCycles();
        Cia1.Tick(now);
        Cia2.Tick(now);
        return Cia1.IrqAsserted || Cia2.IrqAsserted || Vic.IrqAsserted;
    }

    // PLA inputs from the port latch (banking follows the latch bits;
    // the DDR subtleties of floating pins are not modeled).
    private bool Loram => (_portData & 0x01) != 0;
    private bool Hiram => (_portData & 0x02) != 0;
    private bool Charen => (_portData & 0x04) != 0;

    public byte Read(ushort address)
    {
        if (address == 0x0000) return _portDir;
        if (address == 0x0001) return ReadPort();

        if (address < 0xA000) return _ram[address];

        // $A000-$BFFF: BASIC needs both LORAM and HIRAM.
        if (address < 0xC000)
            return (Loram && Hiram) ? _basic[address - 0xA000] : _ram[address];

        if (address < 0xD000) return _ram[address]; // $C000-$CFFF is always RAM

        // $D000-$DFFF: I/O vs character ROM needs HIRAM or LORAM;
        // otherwise the RAM underneath shows through.
        if (address < 0xE000)
        {
            if (Hiram || Loram)
                return Charen ? ReadIo(address) : _charom[address - 0xD000];
            return _ram[address];
        }

        // $E000-$FFFF: Kernal needs HIRAM.
        return Hiram ? _kernal[address - 0xE000] : _ram[address];
    }

    public void Write(ushort address, byte value)
    {
        if (address == 0x0000) { _portDir = value; return; }
        if (address == 0x0001) { _portData = value; return; }

        // Writes always reach the RAM underneath, even where a ROM or
        // the character ROM is currently switched in ("RAM under ROM").
        if (address < 0xC000) { _ram[address] = value; return; }
        if (address < 0xD000) { _ram[address] = value; return; }

        if (address < 0xE000)
        {
            if ((Hiram || Loram) && Charen) { WriteIo(address, value); return; }
            _ram[address] = value;
            return;
        }

        _ram[address] = value;
    }

    /// <summary>
    /// Reads the 6510 port. Output pins report the latch; input pins
    /// report emulated levels: cassette sense high (no button pressed),
    /// unused pins low. Matches the observed PEEK(1)=55.
    /// </summary>
    private byte ReadPort()
    {
        const byte inputLevels = 0x10; // bit 4 high, bits 6-7 low
        return (byte)((_portData & _portDir) | (inputLevels & (byte)~_portDir));
    }

    private byte ReadIo(ushort address)
    {
        if (address < 0xD400) return ReadVic(address);  // $D000-$D3FF
        if (address < 0xD800) return ReadSid(address);  // $D400-$D7FF
        if (address < 0xDC00)
            return (byte)(0xF0 | _colorRam[address - 0xD800]); // $D800-$DBFF, 4-bit
        if (address < 0xDD00) return ReadCia1(address); // $DC00-$DCFF
        if (address < 0xDE00) return ReadCia2(address); // $DD00-$DDFF
        return 0xFF; // $DE00-$DFFF: cartridge areas, open bus
    }

    private void WriteIo(ushort address, byte value)
    {
        if (address < 0xD400) { WriteVic(address, value); return; }
        if (address < 0xD800) { WriteSid(address, value); return; }
        if (address < 0xDC00) { _colorRam[address - 0xD800] = (byte)(value & 0x0F); return; }
        if (address < 0xDD00) { WriteCia1(address, value); return; }
        if (address < 0xDE00) { WriteCia2(address, value); return; }
        // $DE00-$DFFF: ignore
    }

    // ---- I/O chips ----
    // VIC-II is fully implemented (M4). SID is still a stub (M7).
    // CIAs are register stubs with write storage (real timers/IRQ in M5).
    private byte ReadVic(ushort address) => Vic.ReadRegister(address & 0x3FF);
    private void WriteVic(ushort address, byte value) => Vic.WriteRegister(address & 0x3FF, value);
    private byte ReadSid(ushort address) => Sid.ReadRegister(address);
    private void WriteSid(ushort address, byte value) => Sid.WriteRegister(address, value);

    private byte ReadCia1(ushort address) => Cia1.Read(address);
    private void WriteCia1(ushort address, byte value) => Cia1.Write(address, value);
    private byte ReadCia2(ushort address) => Cia2.Read(address);
    private void WriteCia2(ushort address, byte value) => Cia2.Write(address, value);

    /// <summary>
    /// Host utility: loads bytes into the underlying RAM, bypassing ROM
    /// banking (handy for tests).
    /// </summary>
    public void LoadProgram(byte[] program, ushort address)
    {
        Array.Copy(program, 0, _ram, address, program.Length);
    }

    /// <summary>
    /// Host utility: sets the reset vector in the underlying RAM at
    /// $FFFC. Note it is shadowed while the Kernal ROM is banked in.
    /// </summary>
    public void SetResetVector(ushort address)
    {
        _ram[0xFFFC] = (byte)(address & 0xFF);
        _ram[0xFFFD] = (byte)(address >> 8);
    }

    /// <summary>Aggressive hard reset: clear RAM, reset all chips, clear keyboard/joystick.</summary>
    public void HardReset()
    {
        Array.Clear(_ram, 0, _ram.Length);
        Array.Clear(_colorRam, 0, _colorRam.Length);
        // Restore default PLA banking (6502 port).
        _ram[0x0000] = 0x2F;
        _ram[0x0001] = 0x37;
        Keyboard.Clear();
        _joystickPort2 = 0xFF;
        // Reset VIC, SID, CIAs by re-creating them (simplest full reset).
        // Note: VIC/SID/CIA don't have Reset methods; re-creation is cleanest.
        // For now, clear their register areas via the bus.
        for (int i = 0xD000; i < 0xD400; i++) Write((ushort)i, 0);
        for (int i = 0xD400; i < 0xD800; i++) Write((ushort)i, 0);
        for (int i = 0xDC00; i < 0xDD00; i++) Write((ushort)i, 0);
        for (int i = 0xDD00; i < 0xDE00; i++) Write((ushort)i, 0);
        // Re-apply default banking after clearing (writes above may have changed it).
        _ram[0x0000] = 0x2F;
        _ram[0x0001] = 0x37;
        Drive?.Reset();
    }
}
