using System;

namespace C64.Core.Via;

/// <summary>
/// MOS 6522 Versatile Interface Adapter, as used in the 1541 disk drive
/// (two per drive). Models ports A/B with DDR, timers T1/T2, and the
/// shift register (simplified). IRQ output is exposed for the drive CPU.
/// </summary>
public sealed class Via6522
{
    private readonly byte[] _regs = new byte[16];

    // Port state.
    private byte _portA, _portB;
    private byte _ddrA, _ddrB;

    // Timer 1 (16-bit, with latch).
    private ushort _t1Counter, _t1Latch;
    private bool _t1Running;

    // Timer 2 (16-bit).
    private ushort _t2Counter;
    private bool _t2Running;

    // Interrupt flags/enable.
    private byte _ifr; // bit 7 = IRQ
    private byte _ier; // bit 7 = set/clear on write

    /// <summary>Raised when the IRQ output changes.</summary>
    public bool IrqAsserted => (_ifr & 0x80) != 0;

    /// <summary>
    /// External trigger of CA1 (e.g., IEC ATN from the C64).
    /// Sets IFR bit 1 if enabled in IER.
    /// </summary>
    public void TriggerCa1() => SetIfr(0x02);

    /// <summary>External device driving port A pins (for IEC, etc.).</summary>
    public Func<byte> ReadPortAExternal { get; set; } = () => 0xFF;
    public Func<byte> ReadPortBExternal { get; set; } = () => 0xFF;
    public Action<byte> WritePortAExternal { get; set; } = _ => { };
    public Action<byte> WritePortBExternal { get; set; } = _ => { };

    public byte Read(int reg)
    {
        reg &= 0x0F;
        return reg switch
        {
            0x0 => ReadPort(0),   // ORB/IRB
            0x1 => ReadPort(1),   // ORA/IRA
            0x2 => _ddrB,
            0x3 => _ddrA,
            0x4 => (byte)(_t1Counter & 0xFF),
            0x5 => (byte)(_t1Counter >> 8),
            0x6 => (byte)(_t1Latch & 0xFF),
            0x7 => (byte)(_t1Latch >> 8),
            0x8 => (byte)(_t2Counter & 0xFF),
            0x9 => (byte)(_t2Counter >> 8),
            0xD => _ifr,
            0xE => (byte)(_ier | 0x80),
            _ => _regs[reg],
        };
    }

    public void Write(int reg, byte value)
    {
        reg &= 0x0F;
        _regs[reg] = value;
        switch (reg)
        {
            case 0x0: WritePort(0, value); break;
            case 0x1: WritePort(1, value); break;
            case 0x2: _ddrB = value; break;
            case 0x3: _ddrA = value; break;
            case 0x4: _t1Latch = (ushort)((_t1Latch & 0xFF00) | value); break;
            case 0x5:
                _t1Latch = (ushort)((value << 8) | (_t1Latch & 0xFF));
                _t1Counter = _t1Latch;
                _t1Running = true;
                ClearIfr(0x40);
                break;
            case 0x6: _t1Latch = (ushort)((_t1Latch & 0xFF00) | value); break;
            case 0x7: _t1Latch = (ushort)((value << 8) | (_t1Latch & 0xFF)); break;
            case 0x8: _t2Counter = (ushort)((_t2Counter & 0xFF00) | value); break;
            case 0x9:
                _t2Counter = (ushort)((value << 8) | (_t2Counter & 0xFF));
                _t2Running = true;
                ClearIfr(0x20);
                break;
            case 0xD: ClearIfr((byte)(value & 0x7F)); break;
            case 0xE:
                if ((value & 0x80) != 0) _ier |= (byte)(value & 0x7F);
                else _ier &= (byte)~value;
                UpdateIrq();
                break;
        }
    }

    private byte ReadPort(int port)
    {
        // Input pins read external; output pins read the latch.
        if (port == 0)
            return (byte)((_portA & _ddrA) | (ReadPortAExternal() & ~_ddrA));
        else
            return (byte)((_portB & _ddrB) | (ReadPortBExternal() & ~_ddrB));
    }

    private void WritePort(int port, byte value)
    {
        if (port == 0)
        {
            _portA = value;
            WritePortAExternal((byte)(value & _ddrA));
        }
        else
        {
            _portB = value;
            WritePortBExternal((byte)(value & _ddrB));
        }
    }

    private void ClearIfr(byte mask)
    {
        _ifr &= (byte)~mask;
        UpdateIrq();
    }

    private void SetIfr(byte mask)
    {
        if ((_ier & mask) != 0)
        {
            _ifr |= mask;
            UpdateIrq();
        }
    }

    private void UpdateIrq()
    {
        if ((_ifr & _ier & 0x7F) != 0)
            _ifr |= 0x80;
        else
            _ifr &= 0x7F;
    }

    /// <summary>Advance timers by <paramref name="cycles"/> clocks.</summary>
    public void Clock(int cycles)
    {
        if (_t1Running)
        {
            uint prev = _t1Counter;
            _t1Counter = (ushort)(_t1Counter - cycles);
            if (_t1Counter > prev) // underflow
            {
                _t1Counter = _t1Latch;
                SetIfr(0x40);
                // Continuous mode: keep running. One-shot: stop.
                // (Simplified: always continuous if ACR bit 6 set.)
                if ((_regs[0xB] & 0x40) == 0)
                    _t1Running = false;
            }
        }
        if (_t2Running)
        {
            uint prev = _t2Counter;
            _t2Counter = (ushort)(_t2Counter - cycles);
            if (_t2Counter > prev)
            {
                _t2Running = false;
                SetIfr(0x20);
            }
        }
    }
}
