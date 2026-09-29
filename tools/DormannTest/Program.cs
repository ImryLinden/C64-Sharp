// Headless runner for Klaus Dormann's 6502_functional_test.
//
// The binary (bin_files/6502_functional_test.bin in the upstream repo,
// GPL-3.0 by Klaus Dormann) is a 64KB memory image: load it at $0000 and
// start execution at $0400. Every pass/fail checkpoint in the test is a
// JMP-to-self "trap" -- the CPU's PC stops advancing. The success trap is
// at $3469; any other trap address is a failed subtest (look it up in the
// .lst file from the upstream repo to find the culprit).
//
// The binary is NOT shipped with this repo (see .gitignore). Download it
// yourself:
//
//   https://github.com/Klaus2m5/6502_65C02_functional_tests
//
// Usage: dotnet run --project tools/DormannTest -- <path to 6502_functional_test.bin>

using C64.Core.Cpu;
using C64.Core.Memory;

if (args.Length == 0)
{
    Console.WriteLine("usage: DormannTest <6502_functional_test.bin>");
    return 1;
}

byte[] image = File.ReadAllBytes(args[0]);
if (image.Length != 65536)
{
    Console.WriteLine($"expected a 65536-byte image, got {image.Length}");
    return 1;
}

var bus = new RamBus();
for (int i = 0; i < image.Length; i++)
    bus.Write((ushort)i, image[i]);

var cpu = new Cpu6510(bus)
{
    PC = 0x0400,
    SP = 0xFD,
    Status = StatusFlags.Unused | StatusFlags.InterruptDisable,
};

const ushort successTrap = 0x3469;
const long maxCycles = 500_000_000;
const long reportEvery = 20_000_000;

long nextReport = reportEvery;
long steps = 0;

while (!cpu.Halted && cpu.TotalCycles < maxCycles)
{
    ushort pcBefore = cpu.PC;
    cpu.Step();
    steps++;

    if (cpu.PC == pcBefore)
    {
        // JMP-to-self: the test's pass/fail signal.
        Console.WriteLine($"trapped at ${cpu.PC:X4} after {steps:N0} steps, {cpu.TotalCycles:N0} cycles");
        if (cpu.PC == successTrap)
        {
            Console.WriteLine("SUCCESS: all functional tests passed.");
            return 0;
        }
        Console.WriteLine($"FAILURE: subtest trap at ${cpu.PC:X4} (see 6502_functional_test.lst).");
        return 1;
    }

    if (cpu.TotalCycles >= nextReport)
    {
        Console.WriteLine($"... {cpu.TotalCycles:N0} cycles, PC=${cpu.PC:X4}");
        nextReport += reportEvery;
    }
}

Console.WriteLine(cpu.Halted ? "HALTED unexpectedly." : "TIMEOUT: cycle budget exhausted.");
return 2;
