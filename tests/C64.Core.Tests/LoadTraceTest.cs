using C64.Core.Cpu;
using C64.Core.Memory;
using Xunit;

namespace C64.Core.Tests;

/// <summary>Trace PC during LOAD to find the call path.</summary>
public class LoadTraceTest
{
    private static byte[] LoadRom(string name)
    {
        return File.ReadAllBytes($"/home/hatch/workspace/user/files/{name}");
    }

    [Fact]
    public void Trace_Load_Pc()
    {
        var basic = LoadRom("c-64-basic.rom");
        var kernal = LoadRom("c-64-kernal.rom");
        var chargen = LoadRom("c-64-chargen.rom");
        var bus = new C64Bus(basic, kernal, chargen);
        var cpu = new Cpu6510(bus);

        // Boot to READY (run ~2M cycles, should be plenty).
        cpu.Reset();
        cpu.RunForCycles(2000000);

        // Now install a PC logger that records JSR targets.
        var jsrTargets = new System.Collections.Generic.List<ushort>();
        ushort lastPc = 0;
        cpu.PcHook = (pc) =>
        {
            // Log if this PC is a JSR instruction.
            // We can't easily know without disassembling, so log all PCs
            // in the F4xx-F5xx range (Kernal LOAD area).
            if (pc >= 0xF490 && pc <= 0xF5B0)
            {
                jsrTargets.Add(pc);
            }
            // Also log FFD5 and F49E specifically.
            if (pc == 0xFFD5 || pc == 0xF49E)
            {
                jsrTargets.Add(pc);
            }
            return false;
        };

        // Simulate BASIC executing LOAD by directly jumping to the
        // BASIC LOAD statement handler. But we don't know where it is.
        // Instead, let's just run a bit more and see if we hit the Kernal LOAD
        // area during normal READY loop (we shouldn't).
        cpu.RunForCycles(100000);

        // If we got here without hitting F49E/FFD5, the hook works but
        // BASIC isn't calling LOAD (expected - we're at READY prompt).
        Assert.Empty(jsrTargets);

        // Now the real test: manually trigger a Kernal LOAD call.
        // Set up like BASIC would: SETLFS/SETNAM already done, just JSR $FFD5.
        // We'll push a return address and set PC to $FFD5.
        cpu.SP = 0xFD;
        // Push return address $1000 (dummy).
        bus.Write(0x01FD, 0x00);
        bus.Write(0x01FC, 0x10);
        cpu.SP = 0xFB;
        cpu.PC = 0xFFD5;

        jsrTargets.Clear();
        // Step once - should hit the hook at $FFD5.
        cpu.Step();

        // The hook should have logged $FFD5.
        Assert.Contains((ushort)0xFFD5, jsrTargets);
    }
}
