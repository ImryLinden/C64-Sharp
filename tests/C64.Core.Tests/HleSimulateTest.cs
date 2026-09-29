using C64.Core.Cpu;
using C64.Core.Disk;
using C64.Core.Memory;
using Xunit;

namespace C64.Core.Tests;

/// <summary>Verify HLE drive handles a simulated LOAD call.</summary>
public class HleSimulateTest
{
    [Fact]
    public void HleDrive_Handles_Simulated_Load()
    {
        var basic = File.ReadAllBytes("/home/hatch/workspace/user/files/c-64-basic.rom");
        var kernal = File.ReadAllBytes("/home/hatch/workspace/user/files/c-64-kernal.rom");
        var chargen = File.ReadAllBytes("/home/hatch/workspace/user/files/c-64-chargen.rom");
        var bus = new C64Bus(basic, kernal, chargen);
        var cpu = new Cpu6510(bus);
        var hle = new HleDrive();
        var d64 = new D64Image(File.ReadAllBytes("/home/hatch/workspace/hello.d64"));
        hle.MountDisk(d64);

        // Install the hook like Emulator does.
        cpu.PcHook = (pc) => (pc == 0xFFD5 || pc == 0xF49E) && hle.TryHandleLoad(cpu, bus);

        // Simulate BASIC having done SETLFS/SETNAM for LOAD"$",8.
        // $B7=FNLEN, $B8=SECADR, $B9=DEVNUM, $BB/$BC=FNADR
        bus.Write(0xB7, 1);      // FNLEN=1
        bus.Write(0xB8, 0);      // SECADR=0
        bus.Write(0xB9, 8);      // DEVNUM=8
        bus.Write(0xBB, 0x00);   // FNADR=$0200
        bus.Write(0xBC, 0x02);
        bus.Write(0x0200, (byte)'$'); // filename "$"

        // CPU: A=0 (LOAD), X/Y=$0801
        cpu.A = 0;
        cpu.X = 0x01;
        cpu.Y = 0x08;

        // Set up stack with return address.
        cpu.SP = 0xFD;
        bus.Write(0x01FD, 0x00); // hi
        bus.Write(0x01FC, 0x00); // lo
        cpu.SP = 0xFB;

        // Jump to $FFD5 (simulating JSR $FFD5).
        cpu.PC = 0xFFD5;
        cpu.Step();

        // Should have handled it: carry clear, X/Y = end address.
        Assert.False(cpu.GetFlag(StatusFlags.Carry));

        // Verify directory was loaded at $0801.
        byte nextLo = bus.Read(0x0801);
        byte nextHi = bus.Read(0x0802);
        Assert.NotEqual(0, nextLo | (nextHi << 8));
    }
}
