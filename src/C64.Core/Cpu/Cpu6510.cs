using C64.Core.Memory;

namespace C64.Core.Cpu;

/// <summary>
/// 6502 processor status register flags. The C64's 6510 is a 6502 with an
/// on-chip I/O port; the instruction set is identical, so this core is a
/// complete 6502.
/// </summary>
[Flags]
public enum StatusFlags : byte
{
    Carry            = 1 << 0,
    Zero             = 1 << 1,
    InterruptDisable = 1 << 2,
    Decimal          = 1 << 3,
    Break            = 1 << 4,
    Unused           = 1 << 5,
    Overflow         = 1 << 6,
    Negative         = 1 << 7,
}

/// <summary>
/// MOS 6510 CPU core (6502 instruction set). Memory is accessed only through
/// <see cref="IMemoryBus"/>, so the flat-RAM test bus can later be swapped
/// for the real C64 memory map without changing this file.
/// </summary>
public sealed class Cpu6510
{
    public byte A;    // Accumulator
    public byte X;    // Index register X
    public byte Y;    // Index register Y
    public byte SP = 0xFD;
    public ushort PC; // Program counter
    public StatusFlags Status;
    public long TotalCycles;
    public bool Halted;

    /// <summary>
    /// IRQ line provider (level-sensitive). Polled before each instruction;
    /// the interrupt is taken when the line is asserted and I is clear.
    /// Wired by the machine to the combined CIA/VIC IRQ output.
    /// </summary>
    public Func<bool>? IrqLine { get; set; }

    /// <summary>
    /// Optional hook called with the PC before each instruction fetch.
    /// If it returns true, the instruction is skipped (the hook handled it).
    /// Used for Kernal traps (e.g. HLE drive LOAD at $FFD5).
    /// </summary>
    public Func<ushort, bool>? PcHook { get; set; }

    private volatile bool _nmiPending;

    /// <summary>Requests a non-maskable interrupt (edge-triggered). Thread-safe.</summary>
    public void RequestNmi() => _nmiPending = true;

    private readonly IMemoryBus _bus;

    public Cpu6510(IMemoryBus bus)
    {
        _bus = bus;
    }

    /// <summary>Resets the CPU: loads PC from the reset vector at $FFFC/$FFFD.</summary>
    public void Reset()
    {
        PC = ReadWord(0xFFFC);
        SP = 0xFD;
        Status = StatusFlags.Unused | StatusFlags.InterruptDisable;
        Halted = false;
    }

    /// <summary>Runs until BRK or <paramref name="maxInstructions"/> steps.</summary>
    public void Run(int maxInstructions = 1_000_000)
    {
        int executed = 0;
        while (!Halted && executed < maxInstructions)
        {
            Step();
            executed++;
        }
    }

    /// <summary>
    /// Runs until BRK or until <paramref name="cycles"/> more cycles have elapsed.
    /// Useful for frame pacing: run one frame's worth of cycles, render, repeat.
    /// </summary>
    public void RunForCycles(long cycles)
    {
        long target = TotalCycles + cycles;
        while (!Halted && TotalCycles < target)
            Step();
    }

    /// <summary>Fetches, decodes and executes a single instruction.</summary>
    /// <returns>The number of cycles the instruction took.</returns>
    public int Step()
    {
        // Kernal trap hook (HLE).
        if (PcHook != null && PcHook(PC))
            return 0;

        // Hardware interrupts are checked between instructions.
        if (_nmiPending)
        {
            _nmiPending = false;
            return TakeInterrupt(0xFFFA);
        }
        if (IrqLine != null && IrqLine() && !Status.HasFlag(StatusFlags.InterruptDisable))
            return TakeInterrupt(0xFFFE);

        byte opcode = Read(PC++);
        int cycles;

        switch (opcode)
        {
            // ---------- Loads ----------
            case 0xA9: A = Imm(); SetZN(A); cycles = 2; break;          // LDA #imm
            case 0xA5: A = Read(Zp()); SetZN(A); cycles = 3; break;     // LDA zp
            case 0xAD: A = Read(Abs()); SetZN(A); cycles = 4; break;    // LDA abs
            case 0xA2: X = Imm(); SetZN(X); cycles = 2; break;          // LDX #imm
            case 0xA6: X = Read(Zp()); SetZN(X); cycles = 3; break;     // LDX zp
            case 0xB6: X = Read(ZpY()); SetZN(X); cycles = 4; break;    // LDX zp,Y
            case 0xAE: X = Read(Abs()); SetZN(X); cycles = 4; break;    // LDX abs
            case 0xBE: X = Read(AbsY()); SetZN(X); cycles = 4; break;   // LDX abs,Y
            case 0xA0: Y = Imm(); SetZN(Y); cycles = 2; break;          // LDY #imm
            case 0xA4: Y = Read(Zp()); SetZN(Y); cycles = 3; break;     // LDY zp
            case 0xB4: Y = Read(ZpX()); SetZN(Y); cycles = 4; break;    // LDY zp,X
            case 0xAC: Y = Read(Abs()); SetZN(Y); cycles = 4; break;    // LDY abs
            case 0xBC: Y = Read(AbsX()); SetZN(Y); cycles = 4; break;   // LDY abs,X
            case 0xB5: A = Read(ZpX()); SetZN(A); cycles = 4; break;    // LDA zp,X
            case 0xBD: A = Read(AbsX()); SetZN(A); cycles = 4; break;   // LDA abs,X
            case 0xB9: A = Read(AbsY()); SetZN(A); cycles = 4; break;   // LDA abs,Y
            case 0xA1: A = Read(IndX()); SetZN(A); cycles = 6; break;   // LDA (zp,X)
            case 0xB1: A = Read(IndY()); SetZN(A); cycles = 5; break;   // LDA (zp),Y

            // ---------- Stores ----------
            case 0x85: Write(Zp(), A); cycles = 3; break;               // STA zp
            case 0x95: Write(ZpX(), A); cycles = 4; break;              // STA zp,X
            case 0x8D: Write(Abs(), A); cycles = 4; break;              // STA abs
            case 0x9D: Write(AbsX(), A); cycles = 5; break;             // STA abs,X
            case 0x99: Write(AbsY(), A); cycles = 5; break;             // STA abs,Y
            case 0x81: Write(IndX(), A); cycles = 6; break;             // STA (zp,X)
            case 0x91: Write(IndY(), A); cycles = 6; break;             // STA (zp),Y
            case 0x86: Write(Zp(), X); cycles = 3; break;               // STX zp
            case 0x96: Write(ZpY(), X); cycles = 4; break;              // STX zp,Y
            case 0x8E: Write(Abs(), X); cycles = 4; break;              // STX abs
            case 0x84: Write(Zp(), Y); cycles = 3; break;               // STY zp
            case 0x94: Write(ZpX(), Y); cycles = 4; break;              // STY zp,X
            case 0x8C: Write(Abs(), Y); cycles = 4; break;              // STY abs

            // ---------- Arithmetic ----------
            case 0x69: Adc(Imm()); cycles = 2; break;                   // ADC #imm
            case 0x65: Adc(Read(Zp())); cycles = 3; break;              // ADC zp
            case 0x75: Adc(Read(ZpX())); cycles = 4; break;             // ADC zp,X
            case 0x6D: Adc(Read(Abs())); cycles = 4; break;             // ADC abs
            case 0x7D: Adc(Read(AbsX())); cycles = 4; break;            // ADC abs,X
            case 0x79: Adc(Read(AbsY())); cycles = 4; break;            // ADC abs,Y
            case 0x61: Adc(Read(IndX())); cycles = 6; break;            // ADC (zp,X)
            case 0x71: Adc(Read(IndY())); cycles = 5; break;            // ADC (zp),Y
            case 0xE9: Sbc(Imm()); cycles = 2; break;                   // SBC #imm
            case 0xE5: Sbc(Read(Zp())); cycles = 3; break;              // SBC zp
            case 0xF5: Sbc(Read(ZpX())); cycles = 4; break;             // SBC zp,X
            case 0xED: Sbc(Read(Abs())); cycles = 4; break;             // SBC abs
            case 0xFD: Sbc(Read(AbsX())); cycles = 4; break;            // SBC abs,X
            case 0xF9: Sbc(Read(AbsY())); cycles = 4; break;            // SBC abs,Y
            case 0xE1: Sbc(Read(IndX())); cycles = 6; break;            // SBC (zp,X)
            case 0xF1: Sbc(Read(IndY())); cycles = 5; break;            // SBC (zp),Y

            // ---------- Logic ----------
            case 0x29: A &= Imm(); SetZN(A); cycles = 2; break;         // AND #imm
            case 0x25: A &= Read(Zp()); SetZN(A); cycles = 3; break;    // AND zp
            case 0x35: A &= Read(ZpX()); SetZN(A); cycles = 4; break;   // AND zp,X
            case 0x2D: A &= Read(Abs()); SetZN(A); cycles = 4; break;   // AND abs
            case 0x3D: A &= Read(AbsX()); SetZN(A); cycles = 4; break;  // AND abs,X
            case 0x39: A &= Read(AbsY()); SetZN(A); cycles = 4; break;  // AND abs,Y
            case 0x21: A &= Read(IndX()); SetZN(A); cycles = 6; break;  // AND (zp,X)
            case 0x31: A &= Read(IndY()); SetZN(A); cycles = 5; break;  // AND (zp),Y
            case 0x09: A |= Imm(); SetZN(A); cycles = 2; break;         // ORA #imm
            case 0x05: A |= Read(Zp()); SetZN(A); cycles = 3; break;    // ORA zp
            case 0x15: A |= Read(ZpX()); SetZN(A); cycles = 4; break;   // ORA zp,X
            case 0x0D: A |= Read(Abs()); SetZN(A); cycles = 4; break;   // ORA abs
            case 0x1D: A |= Read(AbsX()); SetZN(A); cycles = 4; break;  // ORA abs,X
            case 0x19: A |= Read(AbsY()); SetZN(A); cycles = 4; break;  // ORA abs,Y
            case 0x01: A |= Read(IndX()); SetZN(A); cycles = 6; break;  // ORA (zp,X)
            case 0x11: A |= Read(IndY()); SetZN(A); cycles = 5; break;  // ORA (zp),Y
            case 0x49: A ^= Imm(); SetZN(A); cycles = 2; break;         // EOR #imm
            case 0x45: A ^= Read(Zp()); SetZN(A); cycles = 3; break;    // EOR zp
            case 0x55: A ^= Read(ZpX()); SetZN(A); cycles = 4; break;   // EOR zp,X
            case 0x4D: A ^= Read(Abs()); SetZN(A); cycles = 4; break;   // EOR abs
            case 0x5D: A ^= Read(AbsX()); SetZN(A); cycles = 4; break;  // EOR abs,X
            case 0x59: A ^= Read(AbsY()); SetZN(A); cycles = 4; break;  // EOR abs,Y
            case 0x41: A ^= Read(IndX()); SetZN(A); cycles = 6; break;  // EOR (zp,X)
            case 0x51: A ^= Read(IndY()); SetZN(A); cycles = 5; break;  // EOR (zp),Y

            // ---------- Compares ----------
            case 0xC9: Cmp(A, Imm()); cycles = 2; break;                // CMP #imm
            case 0xC5: Cmp(A, Read(Zp())); cycles = 3; break;           // CMP zp
            case 0xD5: Cmp(A, Read(ZpX())); cycles = 4; break;          // CMP zp,X
            case 0xCD: Cmp(A, Read(Abs())); cycles = 4; break;          // CMP abs
            case 0xDD: Cmp(A, Read(AbsX())); cycles = 4; break;         // CMP abs,X
            case 0xD9: Cmp(A, Read(AbsY())); cycles = 4; break;         // CMP abs,Y
            case 0xC1: Cmp(A, Read(IndX())); cycles = 6; break;         // CMP (zp,X)
            case 0xD1: Cmp(A, Read(IndY())); cycles = 5; break;         // CMP (zp),Y
            case 0xE0: Cmp(X, Imm()); cycles = 2; break;                // CPX #imm
            case 0xE4: Cmp(X, Read(Zp())); cycles = 3; break;           // CPX zp
            case 0xEC: Cmp(X, Read(Abs())); cycles = 4; break;          // CPX abs
            case 0xC0: Cmp(Y, Imm()); cycles = 2; break;                // CPY #imm
            case 0xC4: Cmp(Y, Read(Zp())); cycles = 3; break;           // CPY zp
            case 0xCC: Cmp(Y, Read(Abs())); cycles = 4; break;          // CPY abs
            case 0x24: Bit(Read(Zp())); cycles = 3; break;              // BIT zp
            case 0x2C: Bit(Read(Abs())); cycles = 4; break;             // BIT abs

            // ---------- Increments / decrements / shifts ----------
            case 0xE8: X++; SetZN(X); cycles = 2; break;                // INX
            case 0xCA: X--; SetZN(X); cycles = 2; break;                // DEX
            case 0xC8: Y++; SetZN(Y); cycles = 2; break;                // INY
            case 0x88: Y--; SetZN(Y); cycles = 2; break;                // DEY
            case 0xE6: { ushort a = Zp(); byte v = (byte)(Read(a) + 1); Write(a, v); SetZN(v); cycles = 5; break; } // INC zp
            case 0xF6: { ushort a = ZpX(); byte v = (byte)(Read(a) + 1); Write(a, v); SetZN(v); cycles = 6; break; } // INC zp,X
            case 0xEE: { ushort a = Abs(); byte v = (byte)(Read(a) + 1); Write(a, v); SetZN(v); cycles = 6; break; } // INC abs
            case 0xFE: { ushort a = AbsX(); byte v = (byte)(Read(a) + 1); Write(a, v); SetZN(v); cycles = 7; break; } // INC abs,X
            case 0xC6: { ushort a = Zp(); byte v = (byte)(Read(a) - 1); Write(a, v); SetZN(v); cycles = 5; break; } // DEC zp
            case 0xD6: { ushort a = ZpX(); byte v = (byte)(Read(a) - 1); Write(a, v); SetZN(v); cycles = 6; break; } // DEC zp,X
            case 0xCE: { ushort a = Abs(); byte v = (byte)(Read(a) - 1); Write(a, v); SetZN(v); cycles = 6; break; } // DEC abs
            case 0xDE: { ushort a = AbsX(); byte v = (byte)(Read(a) - 1); Write(a, v); SetZN(v); cycles = 7; break; } // DEC abs,X
            case 0x0A: A = Asl(A); cycles = 2; break;                   // ASL A
            case 0x06: { ushort a = Zp(); Write(a, Asl(Read(a))); cycles = 5; break; }   // ASL zp
            case 0x16: { ushort a = ZpX(); Write(a, Asl(Read(a))); cycles = 6; break; }   // ASL zp,X
            case 0x0E: { ushort a = Abs(); Write(a, Asl(Read(a))); cycles = 6; break; }   // ASL abs
            case 0x1E: { ushort a = AbsX(); Write(a, Asl(Read(a))); cycles = 7; break; } // ASL abs,X
            case 0x4A: A = Lsr(A); cycles = 2; break;                   // LSR A
            case 0x46: { ushort a = Zp(); Write(a, Lsr(Read(a))); cycles = 5; break; }   // LSR zp
            case 0x56: { ushort a = ZpX(); Write(a, Lsr(Read(a))); cycles = 6; break; }   // LSR zp,X
            case 0x4E: { ushort a = Abs(); Write(a, Lsr(Read(a))); cycles = 6; break; }   // LSR abs
            case 0x5E: { ushort a = AbsX(); Write(a, Lsr(Read(a))); cycles = 7; break; } // LSR abs,X
            case 0x2A: A = Rol(A); cycles = 2; break;                   // ROL A
            case 0x26: { ushort a = Zp(); Write(a, Rol(Read(a))); cycles = 5; break; }   // ROL zp
            case 0x36: { ushort a = ZpX(); Write(a, Rol(Read(a))); cycles = 6; break; }   // ROL zp,X
            case 0x2E: { ushort a = Abs(); Write(a, Rol(Read(a))); cycles = 6; break; }   // ROL abs
            case 0x3E: { ushort a = AbsX(); Write(a, Rol(Read(a))); cycles = 7; break; } // ROL abs,X
            case 0x6A: A = Ror(A); cycles = 2; break;                   // ROR A
            case 0x66: { ushort a = Zp(); Write(a, Ror(Read(a))); cycles = 5; break; }   // ROR zp
            case 0x76: { ushort a = ZpX(); Write(a, Ror(Read(a))); cycles = 6; break; }   // ROR zp,X
            case 0x6E: { ushort a = Abs(); Write(a, Ror(Read(a))); cycles = 6; break; }   // ROR abs
            case 0x7E: { ushort a = AbsX(); Write(a, Ror(Read(a))); cycles = 7; break; } // ROR abs,X

            // ---------- Transfers ----------
            case 0xAA: X = A; SetZN(X); cycles = 2; break;              // TAX
            case 0x8A: A = X; SetZN(A); cycles = 2; break;              // TXA
            case 0xA8: Y = A; SetZN(Y); cycles = 2; break;              // TAY
            case 0x98: A = Y; SetZN(A); cycles = 2; break;              // TYA
            case 0xBA: X = SP; SetZN(X); cycles = 2; break;             // TSX
            case 0x9A: SP = X; cycles = 2; break;                       // TXS

            // ---------- Stack ----------
            case 0x48: Push(A); cycles = 3; break;                       // PHA
            case 0x68: A = Pull(); SetZN(A); cycles = 4; break;          // PLA
            case 0x08: Push((byte)(Status | StatusFlags.Break | StatusFlags.Unused)); cycles = 3; break; // PHP
            case 0x28: Status = (StatusFlags)((Pull() & 0xEF) | 0x20); cycles = 4; break; // PLP

            // ---------- Jumps ----------
            case 0x4C: PC = Abs(); cycles = 3; break;                    // JMP abs
            case 0x6C:                                                  // JMP (abs)
            {
                // The 6502 has a famous hardware bug: when the indirect pointer
                // sits at the end of a page ($xxFF), the high byte is fetched
                // from $xx00 instead of $xxFF + 1. We emulate the bug.
                ushort ptr = Abs();
                byte lo = Read(ptr);
                byte hi = Read((ushort)((ptr & 0xFF00) | ((ptr + 1) & 0xFF)));
                PC = (ushort)(lo | (hi << 8));
                cycles = 5;
                break;
            }
            case 0x20:                                                  // JSR abs
            {
                ushort target = Abs();
                PushWord((ushort)(PC - 1));
                PC = target;
                cycles = 6;
                break;
            }
            case 0x60: PC = (ushort)(PullWord() + 1); cycles = 6; break; // RTS
            case 0x40:                                                  // RTI
                // The B flag in the pulled status is ignored; Unused is forced.
                Status = (StatusFlags)((Pull() & 0xEF) | 0x20);
                PC = PullWord();
                cycles = 6;
                break;

            // ---------- Branches ----------
            case 0xF0: cycles = Branch(GetFlag(StatusFlags.Zero)); break;  // BEQ
            case 0xD0: cycles = Branch(!GetFlag(StatusFlags.Zero)); break; // BNE
            case 0x90: cycles = Branch(!GetFlag(StatusFlags.Carry)); break; // BCC
            case 0xB0: cycles = Branch(GetFlag(StatusFlags.Carry)); break;  // BCS
            case 0x10: cycles = Branch(!GetFlag(StatusFlags.Negative)); break; // BPL
            case 0x30: cycles = Branch(GetFlag(StatusFlags.Negative)); break;  // BMI
            case 0x50: cycles = Branch(!GetFlag(StatusFlags.Overflow)); break; // BVC
            case 0x70: cycles = Branch(GetFlag(StatusFlags.Overflow)); break;  // BVS

            // ---------- Flag operations ----------
            case 0x18: SetFlag(StatusFlags.Carry, false); cycles = 2; break;            // CLC
            case 0x38: SetFlag(StatusFlags.Carry, true); cycles = 2; break;             // SEC
            case 0x58: SetFlag(StatusFlags.InterruptDisable, false); cycles = 2; break; // CLI
            case 0x78: SetFlag(StatusFlags.InterruptDisable, true); cycles = 2; break;  // SEI
            case 0xB8: SetFlag(StatusFlags.Overflow, false); cycles = 2; break;        // CLV
            case 0xD8: SetFlag(StatusFlags.Decimal, false); cycles = 2; break;         // CLD
            case 0xF8: SetFlag(StatusFlags.Decimal, true); cycles = 2; break;          // SED

            // ---------- Misc ----------
            case 0xEA: cycles = 2; break;                                // NOP
            case 0x00:                                                  // BRK (software interrupt)
            {
                // BRK is two bytes (opcode + padding); the pushed PC skips both.
                ushort returnAddr = (ushort)(PC + 1);
                Push((byte)(returnAddr >> 8));
                Push((byte)(returnAddr & 0xFF));
                Push((byte)(Status | StatusFlags.Break | StatusFlags.Unused));
                SetFlag(StatusFlags.InterruptDisable, true);
                PC = ReadWord(0xFFFE);
                cycles = 7;
                break;
            }
            // JAM: $02 is an illegal opcode that freezes real hardware.
            // We use it as the halt instruction for our own test programs.
            case 0x02: Halted = true; cycles = 2; break;

            default:
                throw new InvalidOperationException(
                    "Unknown opcode 0x" + opcode.ToString("X2") +
                    " at 0x" + (PC - 1).ToString("X4"));
        }

        TotalCycles += cycles;
        return cycles;
    }

    // ================= Addressing modes =================

    private byte Imm() => Read(PC++);

    private ushort Zp() => Read(PC++);

    private ushort Abs()
    {
        byte lo = Read(PC++);
        byte hi = Read(PC++);
        return (ushort)(lo | (hi << 8));
    }

    // Zero page,X: the address wraps inside the zero page.
    private ushort ZpX() => (ushort)((Read(PC++) + X) & 0xFF);

    // Zero page,Y: the address wraps inside the zero page.
    private ushort ZpY() => (ushort)((Read(PC++) + Y) & 0xFF);

    // Absolute,X / Absolute,Y.
    // TODO: +1 cycle when indexing crosses a page boundary (loads only).
    private ushort AbsX() => (ushort)(Abs() + X);

    private ushort AbsY() => (ushort)(Abs() + Y);

    // Indexed indirect: (zp,X) — X offsets the zero-page pointer, which wraps.
    private ushort IndX() => ReadWord((ushort)((Read(PC++) + X) & 0xFF));

    // Indirect indexed: (zp),Y — Y offsets the 16-bit address (may cross pages).
    private ushort IndY() => (ushort)(ReadWord(Read(PC++)) + Y);

    // ================= Helpers =================

    private byte Read(ushort address) => _bus.Read(address);

    private void Write(ushort address, byte value) => _bus.Write(address, value);

    private ushort ReadWord(ushort address) =>
        (ushort)(Read(address) | (Read((ushort)(address + 1)) << 8));

    public bool GetFlag(StatusFlags flag) => (Status & flag) != 0;

    public void SetFlag(StatusFlags flag, bool on)
    {
        if (on)
            Status |= flag;
        else
            Status &= ~flag;
    }

    private void SetZN(byte value)
    {
        SetFlag(StatusFlags.Zero, value == 0);
        SetFlag(StatusFlags.Negative, (value & 0x80) != 0);
    }

    private void Push(byte value)
    {
        Write((ushort)(0x0100 + SP), value);
        SP--;
    }

    private byte Pull()
    {
        SP++;
        return Read((ushort)(0x0100 + SP));
    }

    private void PushWord(ushort value)
    {
        Push((byte)(value >> 8));
        Push((byte)(value & 0xFF));
    }

    /// <summary>
    /// Hardware interrupt sequence (IRQ/NMI): pushes PC and status (B clear),
    /// sets I, and loads the vector. Takes 7 cycles.
    /// </summary>
    private int TakeInterrupt(ushort vector)
    {
        Push((byte)(PC >> 8));
        Push((byte)(PC & 0xFF));
        // B is clear for hardware interrupts (unlike BRK); U is always set.
        Push((byte)((Status & ~StatusFlags.Break) | StatusFlags.Unused));
        SetFlag(StatusFlags.InterruptDisable, true);
        PC = ReadWord(vector);
        TotalCycles += 7;
        return 7;
    }

    private ushort PullWord()
    {
        byte lo = Pull();
        byte hi = Pull();
        return (ushort)(lo | (hi << 8));
    }

    /// <summary>
    /// Simulates an RTS instruction: pulls the return address from the stack
    /// and sets PC to return+1. Used by Kernal traps (HLE) to return to the caller.
    /// </summary>
    public void SimulateRts()
    {
        PC = (ushort)(PullWord() + 1);
        TotalCycles += 6;
    }

    private void Adc(byte operand)
    {
        if (GetFlag(StatusFlags.Decimal))
            AdcDecimal(operand);
        else
            AdcBinary(operand);
    }

    private void AdcBinary(byte operand)
    {
        int sum = A + operand + (GetFlag(StatusFlags.Carry) ? 1 : 0);
        byte result = (byte)sum;
        SetFlag(StatusFlags.Carry, sum > 0xFF);
        SetFlag(StatusFlags.Overflow, (~(A ^ operand) & (A ^ result) & 0x80) != 0);
        A = result;
        SetZN(A);
    }

    /// <summary>BCD addition (6502 decimal mode). N/Z come from the BCD
    /// result, but V still reflects the binary addition, like the hardware.</summary>
    private void AdcDecimal(byte operand)
    {
        int carryIn = GetFlag(StatusFlags.Carry) ? 1 : 0;

        int lo = (A & 0x0F) + (operand & 0x0F) + carryIn;
        int hiCarry = 0;
        if (lo > 9)
        {
            lo += 6; // BCD adjust
            hiCarry = 1;
        }

        int hi = (A >> 4) + (operand >> 4) + hiCarry;
        bool carryOut = false;
        if (hi > 9)
        {
            hi += 6; // BCD adjust
            carryOut = true;
        }

        int binSum = A + operand + carryIn;
        SetFlag(StatusFlags.Overflow, (~(A ^ operand) & (A ^ binSum) & 0x80) != 0);

        SetFlag(StatusFlags.Carry, carryOut);
        A = (byte)(((hi & 0x0F) << 4) | (lo & 0x0F));
        SetZN(A);
    }

    private void Sbc(byte operand)
    {
        if (GetFlag(StatusFlags.Decimal))
            SbcDecimal(operand);
        else
            SbcBinary(operand);
    }

    private void SbcBinary(byte operand)
    {
        // A - operand - (1 - C)  ==  A + ~operand + C
        int diff = A + (~operand & 0xFF) + (GetFlag(StatusFlags.Carry) ? 1 : 0);
        byte result = (byte)diff;
        SetFlag(StatusFlags.Carry, diff > 0xFF); // C set means no borrow
        SetFlag(StatusFlags.Overflow, ((A ^ operand) & (A ^ result) & 0x80) != 0);
        A = result;
        SetZN(A);
    }

    /// <summary>BCD subtraction (6502 decimal mode). N/Z come from the BCD
    /// result, V still reflects the binary subtraction, like the hardware.</summary>
    private void SbcDecimal(byte operand)
    {
        int borrowIn = GetFlag(StatusFlags.Carry) ? 0 : 1;

        int lo = (A & 0x0F) - (operand & 0x0F) - borrowIn;
        int hiBorrow = 0;
        if (lo < 0)
        {
            lo -= 6; // BCD adjust
            hiBorrow = 1;
        }

        int hi = (A >> 4) - (operand >> 4) - hiBorrow;
        bool borrowOut = false;
        if (hi < 0)
        {
            hi -= 6; // BCD adjust
            borrowOut = true;
        }

        int diff = A + (~operand & 0xFF) + (GetFlag(StatusFlags.Carry) ? 1 : 0);
        SetFlag(StatusFlags.Overflow, ((A ^ operand) & (A ^ (byte)diff) & 0x80) != 0);

        SetFlag(StatusFlags.Carry, !borrowOut); // C set means no borrow
        A = (byte)(((hi & 0x0F) << 4) | (lo & 0x0F));
        SetZN(A);
    }

    private void Cmp(byte register, byte operand)
    {
        SetFlag(StatusFlags.Carry, register >= operand);
        SetZN((byte)(register - operand));
    }

    private byte Asl(byte value)
    {
        SetFlag(StatusFlags.Carry, (value & 0x80) != 0);
        byte result = (byte)(value << 1);
        SetZN(result);
        return result;
    }

    private byte Lsr(byte value)
    {
        SetFlag(StatusFlags.Carry, (value & 0x01) != 0);
        byte result = (byte)(value >> 1);
        SetZN(result);
        return result;
    }

    private byte Rol(byte value)
    {
        bool newCarry = (value & 0x80) != 0;
        byte result = (byte)((value << 1) | (GetFlag(StatusFlags.Carry) ? 1 : 0));
        SetFlag(StatusFlags.Carry, newCarry);
        SetZN(result);
        return result;
    }

    private byte Ror(byte value)
    {
        bool newCarry = (value & 0x01) != 0;
        byte result = (byte)((value >> 1) | (GetFlag(StatusFlags.Carry) ? 0x80 : 0));
        SetFlag(StatusFlags.Carry, newCarry);
        SetZN(result);
        return result;
    }

    private void Bit(byte operand)
    {
        // BIT doesn't touch A: Z reflects A&M, N and V come from the operand.
        SetFlag(StatusFlags.Zero, (A & operand) == 0);
        SetFlag(StatusFlags.Negative, (operand & 0x80) != 0);
        SetFlag(StatusFlags.Overflow, (operand & 0x40) != 0);
    }

    private int Branch(bool take)
    {
        sbyte offset = (sbyte)Read(PC++);
        if (!take)
            return 2;
        // TODO: +1 cycle when the branch crosses a page boundary.
        PC = (ushort)(PC + offset);
        return 3;
    }
}
