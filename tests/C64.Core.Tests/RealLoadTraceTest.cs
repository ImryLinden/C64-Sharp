using C64.Core.Cpu;
using C64.Core.Disk;
using C64.Core.Memory;
using Xunit;

namespace C64.Core.Tests;

/// <summary>Trace PC during a real BASIC LOAD"$",8 to find the call path.</summary>
public class RealLoadTraceTest
{
    [Fact]
    public void Trace_Real_Load()
    {
        var basic = File.ReadAllBytes("/home/hatch/workspace/user/files/c-64-basic.rom");
        var kernal = File.ReadAllBytes("/home/hatch/workspace/user/files/c-64-kernal.rom");
        var chargen = File.ReadAllBytes("/home/hatch/workspace/user/files/c-64-chargen.rom");
        var bus = new C64Bus(basic, kernal, chargen);
        var cpu = new Cpu6510(bus);
        // Wire IRQ like the Emulator does.
        cpu.IrqLine = bus.GetIrq;
        bus.Vic.GetCpuCycles = () => cpu.TotalCycles;
        var hle = new HleDrive();
        var d64 = new D64Image(File.ReadAllBytes("/home/hatch/workspace/hello.d64"));
        hle.MountDisk(d64);

        // Boot to READY.
        cpu.Reset();
        // Run until we see the READY prompt (PC in BASIC main loop).
        // Just run 2M cycles.
        cpu.RunForCycles(2000000);

        // Install PC tracer: log all PCs in Kernal ROM range.
        var pcs = new System.Collections.Generic.HashSet<ushort>();
        var hitFFD5 = false;
        var hitF49E = false;
        var hitF4A5 = false;
        cpu.PcHook = (pc) =>
        {
            if (pc == 0xFFD5) hitFFD5 = true;
            if (pc == 0xF49E) hitF49E = true;
            if (pc == 0xF4A5) hitF4A5 = true;
            // Log all Kernal PCs to find the LOAD path.
            if (pc >= 0xE000 && pc <= 0xFFFF)
                pcs.Add(pc);
            return false;
        };

        // Type LOAD"$",8 + Return via keyboard matrix.
        // L=(5,2), O=(4,6), A=(1,2), D=(2,2), " = Shift+2, $ = Shift+4, ,=(5,7), 8=(3,3), RETURN=(0,1)
        void PressKey(int col, int row)
        {
            bus.Keyboard.SetKey(col, row, true);
            // Run enough cycles for the CIA to scan (one frame).
            cpu.RunForCycles(20000);
            bus.Keyboard.SetKey(col, row, false);
            cpu.RunForCycles(20000);
        }

        void PressShifted(int col, int row)
        {
            bus.Keyboard.SetKey(1, 7, true); // LSHIFT
            cpu.RunForCycles(5000);
            bus.Keyboard.SetKey(col, row, true);
            cpu.RunForCycles(20000);
            bus.Keyboard.SetKey(col, row, false);
            cpu.RunForCycles(5000);
            bus.Keyboard.SetKey(1, 7, false);
            cpu.RunForCycles(20000);
        }

        PressKey(5, 2); // L
        PressKey(4, 6); // O
        PressKey(1, 2); // A
        PressKey(2, 2); // D
        PressShifted(7, 3); // " (Shift+2)
        PressShifted(1, 3); // $ (Shift+4)
        PressShifted(7, 3); // " (Shift+2)
        PressKey(5, 7); // ,
        PressKey(3, 3); // 8

        // Clear the hit flags before pressing Return.
        hitFFD5 = hitF49E = hitF4A5 = false;
        pcs.Clear();

        PressKey(0, 1); // RETURN

        // Run more to let LOAD execute.
        cpu.RunForCycles(500000);

        // Report.
        var output = $"Hit FFD5: {hitFFD5}, Hit F49E: {hitF49E}, Hit F4A5: {hitF4A5}\n";
        output += $"Unique Kernal PCs: {pcs.Count}\n";
        // Find PCs in the F4xx-F5xx range (LOAD area).
        var loadPcs = pcs.Where(p => p >= 0xF400 && p <= 0xF600).OrderBy(p => p).ToList();
        output += $"PCs in F400-F600: {string.Join(", ", loadPcs.Select(p => $"${p:X4}"))}\n";
        File.WriteAllText("/tmp/load_trace.txt", output);

        // The test passes if we hit at least one of them (or we learn which one).
        Assert.True(hitFFD5 || hitF49E || hitF4A5 || loadPcs.Count > 0,
            $"No LOAD PC hit. Trace:\n{output}");
    }
}
