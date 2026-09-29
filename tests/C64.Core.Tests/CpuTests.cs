using C64.Core.Cpu;
using C64.Core.Memory;
using C64.Demo.BouncingBall;
using Xunit;

namespace C64.Core.Tests;

/// <summary>
/// Bring-up tests for the 6510 core. Each test assembles a tiny program by
/// hand (raw opcode bytes), runs it against a flat 64KB RAM, and asserts the
/// result. Programs are loaded at $0600 and terminated with BRK ($00).
/// </summary>
public sealed class CpuTests
{
    private const ushort LoadAddress = 0x0600;

    private static (Cpu6510 Cpu, RamBus Bus) CreateCpu()
    {
        var bus = new RamBus();
        var cpu = new Cpu6510(bus);
        bus.SetResetVector(LoadAddress);
        return (cpu, bus);
    }

    private static void LoadAndRun(RamBus bus, Cpu6510 cpu, byte[] program)
    {
        bus.LoadProgram(program, LoadAddress);
        cpu.Reset();
        cpu.Run();
    }

    [Fact]
    public void LdaImmediate_SetsZeroFlag_WhenValueIsZero()
    {
        var (cpu, bus) = CreateCpu();

        LoadAndRun(bus, cpu, new byte[]
        {
            0xA9, 0x00, // LDA #$00
            0x02        // JAM (halt)
        });

        Assert.True(cpu.Status.HasFlag(StatusFlags.Zero));
        Assert.False(cpu.Status.HasFlag(StatusFlags.Negative));
    }

    [Fact]
    public void AddTwoNumbers_StoresResultInMemory()
    {
        var (cpu, bus) = CreateCpu();

        LoadAndRun(bus, cpu, new byte[]
        {
            0xA9, 0x05,       // LDA #$05
            0x18,             // CLC
            0x69, 0x03,       // ADC #$03
            0x8D, 0x00, 0x02, // STA $0200
            0x02              // JAM (halt)
        });

        Assert.Equal(8, cpu.A);
        Assert.Equal(8, bus.Read(0x0200));
        Assert.False(cpu.Status.HasFlag(StatusFlags.Carry));
    }

    [Fact]
    public void Adc_SetsCarry_OnUnsignedOverflow()
    {
        var (cpu, bus) = CreateCpu();

        LoadAndRun(bus, cpu, new byte[]
        {
            0xA9, 0xFF, // LDA #$FF
            0x18,       // CLC
            0x69, 0x02, // ADC #$02
            0x02        // JAM (halt)
        });

        Assert.Equal(0x01, cpu.A);
        Assert.True(cpu.Status.HasFlag(StatusFlags.Carry));
    }

    [Fact]
    public void BranchLoop_CountsDownToZero()
    {
        var (cpu, bus) = CreateCpu();

        LoadAndRun(bus, cpu, new byte[]
        {
            0xA2, 0x03, // LDX #$03
                        // loop:
            0xCA,       // DEX
            0xD0, 0xFD, // BNE -3 (back to DEX)
            0x02        // JAM (halt)
        });

        Assert.Equal(0, cpu.X);
        Assert.True(cpu.Status.HasFlag(StatusFlags.Zero));
    }

    [Fact]
    public void JsrRts_CallsSubroutineAndReturns()
    {
        var (cpu, bus) = CreateCpu();

        LoadAndRun(bus, cpu, new byte[]
        {
            0x20, 0x07, 0x06, // JSR $0607
            0x8D, 0x00, 0x02, // STA $0200
            0x02,             // JAM (halt)        ($0606)
            0xA9, 0x2A,       // LDA #$2A   ($0607)
            0x60              // RTS
        });

        Assert.Equal(0x2A, bus.Read(0x0200));
    }

    [Fact]
    public void Stack_PushAndPull_RoundTrips()
    {
        var (cpu, bus) = CreateCpu();

        LoadAndRun(bus, cpu, new byte[]
        {
            0xA9, 0x42, // LDA #$42
            0x48,       // PHA
            0xA9, 0x00, // LDA #$00
            0x68,       // PLA
            0x02        // JAM (halt)
        });

        Assert.Equal(0x42, cpu.A);
    }

    [Fact]
    public void IndexedStore_Load_AbsX_RoundTrips()
    {
        var (cpu, bus) = CreateCpu();

        LoadAndRun(bus, cpu, new byte[]
        {
            0xA2, 0x04,       // LDX #$04
            0xA9, 0x77,       // LDA #$77
            0x9D, 0x00, 0x03, // STA $0300,X  -> writes $0304
            0xA9, 0x00,       // LDA #$00
            0xBD, 0x00, 0x03, // LDA $0300,X  -> reads $0304
            0x02              // JAM (halt)
        });

        Assert.Equal(0x77, cpu.A);
        Assert.Equal(0x77, bus.Read(0x0304));
    }

    [Fact]
    public void IndirectIndexed_Sta_WritesThroughPointer()
    {
        var (cpu, bus) = CreateCpu();

        LoadAndRun(bus, cpu, new byte[]
        {
            0xA9, 0x00, // LDA #$00
            0x85, 0x10, // STA $10       ; pointer lo = $00
            0xA9, 0x04, // LDA #$04
            0x85, 0x11, // STA $11       ; pointer hi = $04  -> ($10) = $0400
            0xA0, 0x05, // LDY #$05
            0xA9, 0x99, // LDA #$99
            0x91, 0x10, // STA ($10),Y   -> writes $0405
            0xB1, 0x10, // LDA ($10),Y   -> reads $0405
            0x02        // JAM (halt)
        });

        Assert.Equal(0x99, cpu.A);
        Assert.Equal(0x99, bus.Read(0x0405));
    }

    [Fact]
    public void RolA_RotatesThroughCarry()
    {
        var (cpu, bus) = CreateCpu();

        LoadAndRun(bus, cpu, new byte[]
        {
            0x38,       // SEC
            0xA9, 0x41, // LDA #$41      ; 0100 0001
            0x2A,       // ROL A         ; -> 1000 0011, carry out = old bit7 = 0
            0x02        // JAM (halt)
        });

        Assert.Equal(0x83, cpu.A);
        Assert.False(cpu.Status.HasFlag(StatusFlags.Carry));
    }

    [Fact]
    public void IncDec_Memory_UpdatesInPlace()
    {
        var (cpu, bus) = CreateCpu();

        LoadAndRun(bus, cpu, new byte[]
        {
            0xA9, 0x05,       // LDA #$05
            0x8D, 0x00, 0x02, // STA $0200
            0xEE, 0x00, 0x02, // INC $0200  -> 6
            0xCE, 0x00, 0x02, // DEC $0200  -> 5
            0xAD, 0x00, 0x02, // LDA $0200
            0x02              // JAM (halt)
        });

        Assert.Equal(5, cpu.A);
        Assert.Equal(5, bus.Read(0x0200));
    }

    [Fact]
    public void JmpIndirect_FollowsPointer_WithPageWrapBug()
    {
        var (cpu, bus) = CreateCpu();

        // Pointer at $03FF: lo byte lives at $03FF, but the 6502 wraps the
        // high-byte fetch to $0300 instead of $0400 (hardware bug).
        bus.Write(0x03FF, 0x06); // target lo
        bus.Write(0x0300, 0x06); // target hi (wrapped)  -> $0606
        bus.Write(0x0400, 0xFF); // would be the hi byte without the bug -> $FF06

        LoadAndRun(bus, cpu, new byte[]
        {
            0x6C, 0xFF, 0x03, // JMP ($03FF)   ($0600)
            0xA9, 0x00,       // LDA #$00      ($0603, skipped)
            0x02,             // JAM (halt)           ($0605, skipped)
            0xA9, 0x42,       // LDA #$42      ($0606, landed here)
            0x02              // JAM (halt)
        });

        Assert.Equal(0x42, cpu.A);
    }

    /// <summary>
    /// Integration test: runs the bouncing-ball demo program headlessly and
    /// verifies the ball moves every frame, stays on screen, leaves exactly
    /// one ball character behind, and bounces off both axes.
    /// </summary>
    [Fact]
    public void BouncingBall_StaysInBounds_AndBounces()
    {
        var bus = new RamBus();
        BallProgram.Load(bus);
        var cpu = new Cpu6510(bus);
        cpu.Reset();

        int prevX = -1, prevY = -1, prevDx = 1, prevDy = 1;
        bool bouncedX = false, bouncedY = false;

        for (int f = 0; f < 120; f++)
        {
            cpu.RunForCycles(350_000);
            int x = bus.Read(0);
            int y = bus.Read(1);
            int dx = bus.Read(2) == 0xFF ? -1 : bus.Read(2);
            int dy = bus.Read(3) == 0xFF ? -1 : bus.Read(3);

            Assert.InRange(x, 0, BallProgram.ScreenWidth - 1);
            Assert.InRange(y, 0, BallProgram.ScreenHeight - 1);
            Assert.False(x == prevX && y == prevY, $"ball did not move at frame {f}");

            int balls = 0;
            for (int i = 0; i < BallProgram.ScreenWidth * BallProgram.ScreenHeight; i++)
                if (bus.Read((ushort)(BallProgram.ScreenAddress + i)) == 0x2A)
                    balls++;
            Assert.Equal(1, balls);

            if (dx != prevDx) bouncedX = true;
            if (dy != prevDy) bouncedY = true;
            prevX = x; prevY = y; prevDx = dx; prevDy = dy;
        }

        Assert.True(bouncedX, "ball never bounced horizontally");
        Assert.True(bouncedY, "ball never bounced vertically");
    }

    [Fact]
    public void AdcDecimal_AddsBcdCorrectly()
    {
        var (cpu, bus) = CreateCpu();

        LoadAndRun(bus, cpu, new byte[]
        {
            0xF8,        // SED
            0xA9, 0x45,  // LDA #$45
            0x69, 0x38,  // ADC #$38   ->  45 + 38 = 83 BCD
            0x02         // JAM (halt)
        });

        Assert.Equal(0x83, cpu.A);
        Assert.False(cpu.Status.HasFlag(StatusFlags.Carry));
        Assert.False(cpu.Status.HasFlag(StatusFlags.Zero));
    }

    [Fact]
    public void AdcDecimal_CarryOutOfBcd()
    {
        var (cpu, bus) = CreateCpu();

        LoadAndRun(bus, cpu, new byte[]
        {
            0xF8,        // SED
            0x38,        // SEC
            0xA9, 0x99,  // LDA #$99
            0x69, 0x99,  // ADC #$99   ->  99 + 99 + 1 = 199 -> $99, C=1
            0x02         // JAM (halt)
        });

        Assert.Equal(0x99, cpu.A);
        Assert.True(cpu.Status.HasFlag(StatusFlags.Carry));
    }

    [Fact]
    public void SbcDecimal_SubtractsBcdCorrectly()
    {
        var (cpu, bus) = CreateCpu();

        LoadAndRun(bus, cpu, new byte[]
        {
            0xF8,        // SED
            0x38,        // SEC
            0xA9, 0x83,  // LDA #$83
            0xE9, 0x38,  // SBC #$38   ->  83 - 38 = 45 BCD
            0x02         // JAM (halt)
        });

        Assert.Equal(0x45, cpu.A);
        Assert.True(cpu.Status.HasFlag(StatusFlags.Carry)); // no borrow
    }

    [Fact]
    public void SbcDecimal_BorrowOutOfBcd()
    {
        var (cpu, bus) = CreateCpu();

        LoadAndRun(bus, cpu, new byte[]
        {
            0xF8,        // SED
            0x38,        // SEC
            0xA9, 0x30,  // LDA #$30
            0xE9, 0x50,  // SBC #$50   ->  30 - 50 = -20 -> $80, C=0
            0x02         // JAM (halt)
        });

        Assert.Equal(0x80, cpu.A);
        Assert.False(cpu.Status.HasFlag(StatusFlags.Carry)); // borrow happened
    }

    [Fact]
    public void Brk_PushesStateAndJumpsToIrqVector_RtiRestores()
    {
        var (cpu, bus) = CreateCpu();

        // IRQ vector -> $0700. Main: LDA #$42, BRK, JAM. Handler: RTI.
        bus.Write(0xFFFE, 0x00);
        bus.Write(0xFFFF, 0x07);
        bus.LoadProgram(new byte[] { 0x40 }, 0x0700); // RTI
        LoadAndRun(bus, cpu, new byte[]
        {
            0xA9, 0x42,  // LDA #$42
            0x00, 0xEA,  // BRK (+ padding byte, skipped) -> pushes PC+2, jumps to ($FFFE)
            0x02         // JAM (halt) <- RTI returns here
        });

        Assert.Equal(0x42, cpu.A);
        // RTI returned to the JAM just past BRK; the fetch advanced PC one more.
        Assert.Equal(LoadAddress + 5, cpu.PC);
        Assert.False(cpu.Status.HasFlag(StatusFlags.Break)); // B is not restored from stack
    }
}
