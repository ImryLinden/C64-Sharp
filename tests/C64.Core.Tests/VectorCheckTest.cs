using C64.Core.Cpu;
using C64.Core.Memory;
using Xunit;

namespace C64.Core.Tests;

public class VectorCheckTest
{
    [Fact]
    public void Check_0330_Vector()
    {
        var basic = File.ReadAllBytes("/home/hatch/workspace/user/files/c-64-basic.rom");
        var kernal = File.ReadAllBytes("/home/hatch/workspace/user/files/c-64-kernal.rom");
        var chargen = File.ReadAllBytes("/home/hatch/workspace/user/files/c-64-chargen.rom");
        var bus = new C64Bus(basic, kernal, chargen);
        var cpu = new Cpu6510(bus);
        cpu.IrqLine = bus.GetIrq;
        bus.Vic.GetCpuCycles = () => cpu.TotalCycles;

        cpu.Reset();
        cpu.RunForCycles(2000000);

        byte lo = bus.Read(0x0330);
        byte hi = bus.Read(0x0331);
        ushort vec = (ushort)(lo | (hi << 8));

        File.WriteAllText("/tmp/vector.txt", $"Vector at $0330: ${vec:X4} (lo=${lo:X2}, hi=${hi:X2})\n");

        // On a real C64, this should point to $F4A5 (the LOAD implementation).
        Assert.Equal(0xF4A5, vec);
    }
}
