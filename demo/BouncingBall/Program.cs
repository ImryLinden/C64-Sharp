using System.Text;
using C64.Core.Cpu;
using C64.Core.Memory;
using C64.Demo.BouncingBall;

// ============================================================================
// BOUNCING BALL — a demo for the C64 emulator.
//
// A hand-assembled 6502 program (see BallProgram.cs) draws a ball bouncing
// around a 40x25 screen in emulated RAM ($0400-$07E7, exactly where a real
// C64 keeps its screen). This host just renders that RAM region to the
// console once per "frame". Everything the ball does — clearing the screen,
// computing the screen address, bouncing off the walls — is executed by the
// emulated CPU.
// ============================================================================

var bus = new RamBus();
BallProgram.Load(bus);

var cpu = new Cpu6510(bus);
cpu.Reset();

Console.CursorVisible = false;
try
{
    const int frames = 80;
    for (int f = 0; f < frames; f++)
    {
        // One frame's worth of emulated cycles (the delay loop dominates it),
        // then render what the 6502 drew into screen RAM.
        cpu.RunForCycles(350_000);
        Render(bus, f, cpu);
        Thread.Sleep(100);
    }
}
finally
{
    Console.CursorVisible = true;
}

Console.SetCursorPosition(0, 28);
Console.WriteLine($"Done — {cpu.TotalCycles:N0} emulated cycles, still looping happily.");

static void Render(RamBus bus, int frame, Cpu6510 cpu)
{
    var sb = new StringBuilder();
    sb.AppendLine($"  C64 BOUNCING BALL   frame {frame,3}   ball at ({bus.Read(0),2}, {bus.Read(1),2})   {cpu.TotalCycles,12:N0} cycles");
    sb.AppendLine("  ┌" + new string('─', 40) + "┐");
    for (int y = 0; y < BallProgram.ScreenHeight; y++)
    {
        sb.Append("  │");
        for (int x = 0; x < BallProgram.ScreenWidth; x++)
        {
            sb.Append(bus.Read((ushort)(BallProgram.ScreenAddress + y * BallProgram.ScreenWidth + x)) switch
            {
                0x20 => ' ',
                0x2A => '●',
                _ => '?',
            });
        }
        sb.AppendLine("│");
    }
    sb.Append("  └" + new string('─', 40) + "┘");
    Console.SetCursorPosition(0, 0);
    Console.Write(sb.ToString());
}
