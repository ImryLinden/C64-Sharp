using C64.Core.Cpu;
using C64.Core.Memory;
using Xunit;

namespace C64.Core.Tests;

public class BootCheckTest
{
    [Fact]
    public void Check_Boot_PC()
    {
        var basic = File.ReadAllBytes("/home/hatch/workspace/user/files/c-64-basic.rom");
        var kernal = File.ReadAllBytes("/home/hatch/workspace/user/files/c-64-kernal.rom");
        var chargen = File.ReadAllBytes("/home/hatch/workspace/user/files/c-64-chargen.rom");
        var bus = new C64Bus(basic, kernal, chargen);
        var cpu = new Cpu6510(bus);
        cpu.IrqLine = bus.GetIrq;
        bus.Vic.GetCpuCycles = () => cpu.TotalCycles;
        bus.GetCpuCycles = () => cpu.TotalCycles;

        cpu.Reset();
        // Run 3M cycles (more than 107 frames).
        cpu.RunForCycles(3000000);

        ushort pc = cpu.PC;
        byte lo0330 = bus.Read(0x0330);
        byte hi0330 = bus.Read(0x0331);

        // Check screen memory for READY.
        // Screen RAM at $0400. READY. in PETSCII: R=$52, E=$45, A=$41, D=$44, Y=$59, .=$2E
        // But screen codes are different! R=18, E=5, A=1, D=4, Y=25, .=46
        string screen = "";
        for (int i = 0; i < 40; i++)
        {
            byte c = bus.Read((ushort)(0x0400 + i));
            if (c >= 1 && c <= 26) screen += (char)('A' + c - 1);
            else if (c == 46) screen += ".";
            else if (c == 32) screen += " ";
        }

        File.WriteAllText("/tmp/boot.txt",
            $"PC after 3M cycles: ${pc:X4}\n" +
            $"Vector $0330: ${hi0330:X2}{lo0330:X2}\n" +
            $"Screen line 0: '{screen}'\n");

        // BASIC main loop is around $A483. If we're there, boot worked.
        Assert.True(pc >= 0xA000 && pc <= 0xBFFF, $"PC ${pc:X4} not in BASIC ROM");
    }
}
