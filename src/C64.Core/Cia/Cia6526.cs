namespace C64.Core.Cia;

/// <summary>
/// MOS 6526 Complex Interface Adapter (CIA).
/// Implements timers A/B, the interrupt control register, and the two
/// 8-bit I/O ports. TOD clock and the serial shift register are stubs
/// (registers store, reads return stored/latched values).
///
/// Timing model: the machine calls <see cref="Tick"/> with the current CPU
/// cycle count; timers advance lazily and the IRQ output reflects the
/// ICR flags &amp; mask. Port B input can be supplied by the host (used by
/// CIA1 for the keyboard matrix).
/// </summary>
public class Cia6526
{
    // Registers $00-$0F.
    private byte _pra, _prb, _ddra, _ddrb;
    private ushort _taLatch, _tbLatch;
    private int _taCounter, _tbCounter;
    private bool _taRunning, _tbRunning;
    private bool _taOneShot, _tbOneShot;
    private bool _tbCountsTa; // Timer B input mode: Timer A underflows.
    private byte _icrFlags;   // latched interrupt flags (bits 0-4)
    private byte _icrMask;    // interrupt mask (bits 0-4)
    private byte _cra, _crb;
    private long _lastTickCycles;

    /// <summary>
    /// Host-supplied Port B input. Called with the current Port A output
    /// byte; returns the 8 Port B input bits (1 = high/inactive).
    /// CIA1 wires this to the keyboard matrix.
    /// </summary>
    public Func<byte, byte>? PortBInput { get; set; }

    /// <summary>
    /// Host-supplied Port A input. Returns the 8 Port A input bits.
    /// CIA2 wires this to the IEC bus (CLK IN, DATA IN).
    /// </summary>
    public Func<byte>? PortAInput { get; set; }

    /// <summary>
    /// Called when Port A output changes. CIA2 wires this to the IEC bus
    /// (ATN, CLK OUT, DATA OUT).
    /// </summary>
    public Action<byte>? PortAOutput { get; set; }

    /// <summary>IRQ output: asserted when any enabled interrupt flag is set.</summary>
    public bool IrqAsserted => (_icrFlags & _icrMask) != 0;

    public byte Read(ushort address)
    {
        switch (address & 0x0F)
        {
            case 0x00: return PortARead();
            case 0x01: return PortBRead();
            case 0x02: return _ddra;
            case 0x03: return _ddrb;
            case 0x04: return (byte)(_taCounter & 0xFF);
            case 0x05: return (byte)((_taCounter >> 8) & 0xFF);
            case 0x06: return (byte)(_tbCounter & 0xFF);
            case 0x07: return (byte)((_tbCounter >> 8) & 0xFF);
            case 0x0D:
            {
                // Reading ICR returns flags (bit 7 = IRQ) and clears them.
                byte value = (byte)(_icrFlags | (IrqAsserted ? 0x80 : 0));
                _icrFlags = 0;
                return value;
            }
            case 0x0E: return _cra;
            case 0x0F: return _crb;
            default: return 0x00; // TOD / SDR stubs
        }
    }

    public void Write(ushort address, byte value)
    {
        switch (address & 0x0F)
        {
            case 0x00: _pra = value; PortAOutput?.Invoke(value); break;
            case 0x01: _prb = value; break;
            case 0x02: _ddra = value; break;
            case 0x03: _ddrb = value; break;
            case 0x04: _taLatch = (ushort)((_taLatch & 0xFF00) | value); break;
            case 0x05:
                _taLatch = (ushort)((_taLatch & 0x00FF) | (value << 8));
                if (!_taRunning) _taCounter = _taLatch; // reload while stopped
                break;
            case 0x06: _tbLatch = (ushort)((_tbLatch & 0xFF00) | value); break;
            case 0x07:
                _tbLatch = (ushort)((_tbLatch & 0x00FF) | (value << 8));
                if (!_tbRunning) _tbCounter = _tbLatch;
                break;
            case 0x0D:
                // Bit 7 = 1: set mask bits; 0: clear mask bits.
                if ((value & 0x80) != 0) _icrMask |= (byte)(value & 0x1F);
                else _icrMask &= (byte)~value;
                break;
            case 0x0E: WriteCra(value); break;
            case 0x0F: WriteCrb(value); break;
            default: break; // TOD / SDR stubs
        }
    }

    /// <summary>Advances timers to <paramref name="nowCycles"/>.</summary>
    public void Tick(long nowCycles)
    {
        long delta = nowCycles - _lastTickCycles;
        _lastTickCycles = nowCycles;
        if (delta <= 0) return;

        // Timer A counts phi2 cycles.
        if (_taRunning)
        {
            _taCounter -= (int)Math.Min(delta, int.MaxValue);
            while (_taCounter < 0)
                TimerAUnderflow();
        }

        // Timer B counts phi2, or Timer A underflows.
        if (_tbRunning && !_tbCountsTa)
        {
            _tbCounter -= (int)Math.Min(delta, int.MaxValue);
            while (_tbCounter < 0)
                TimerBUnderflow();
        }
    }

    private void TimerAUnderflow()
    {
        _icrFlags |= 0x01;
        if (_tbRunning && _tbCountsTa)
        {
            _tbCounter--;
            if (_tbCounter < 0) TimerBUnderflow();
        }
        if (_taOneShot)
        {
            _taRunning = false;
            _cra &= 0xFE; // hardware clears the Start bit
            _taCounter = 0;
        }
        else
        {
            _taCounter += _taLatch + 1; // reload; period = latch + 1
        }
    }

    private void TimerBUnderflow()
    {
        _icrFlags |= 0x02;
        if (_tbOneShot)
        {
            _tbRunning = false;
            _crb &= 0xFE; // hardware clears the Start bit
            _tbCounter = 0;
        }
        else
        {
            _tbCounter += _tbLatch + 1;
        }
    }

    private void WriteCra(byte value)
    {
        bool wasRunning = _taRunning;
        _cra = (byte)(value & 0xEF); // bit 4 (force load) is a strobe
        _taRunning = (value & 0x01) != 0;
        _taOneShot = (value & 0x08) != 0;
        if ((value & 0x10) != 0) _taCounter = _taLatch; // force load
        if (_taRunning && !wasRunning && _taCounter == 0 && (value & 0x10) == 0)
        {
            // Starting a zeroed timer loads the latch (power-on case).
            _taCounter = _taLatch;
        }
    }

    private void WriteCrb(byte value)
    {
        bool wasRunning = _tbRunning;
        _crb = (byte)(value & 0xEF);
        _tbRunning = (value & 0x01) != 0;
        _tbOneShot = (value & 0x08) != 0;
        _tbCountsTa = (value & 0x60) == 0x40 || (value & 0x60) == 0x60;
        if ((value & 0x10) != 0) _tbCounter = _tbLatch;
        if (_tbRunning && !wasRunning && _tbCounter == 0 && (value & 0x10) == 0)
            _tbCounter = _tbLatch;
    }

    private byte PortARead()
    {
        byte input = PortAInput != null ? PortAInput() : (byte)0xFF;
        return (byte)((_pra & _ddra) | (input & ~_ddra));
    }

    private byte PortBRead()
    {
        byte input = PortBInput != null ? PortBInput(_pra) : (byte)0xFF;
        return (byte)((_prb & _ddrb) | (input & ~_ddrb));
    }
}
