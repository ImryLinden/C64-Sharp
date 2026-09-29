using C64.Core.Cpu;
using C64.Core.Memory;
using C64.Core.Vic;

// M4 demo: boots the real C64 Kernal to the READY. prompt and renders
// the VIC-II text mode to boot.bmp (plus a PETSCII text dump).
//
// Usage: dotnet run --project demo/VicBoot [-- <roms-folder>]
// Drop your BASIC/Kernal/character ROMs in <roms-folder> (default .roms/).
// Accepted names: basic.rom/kernal.rom/chargen.rom or the C64 Forever
// c-64-basic.rom/c-64-kernal.rom/c-64-chargen.rom variants.
string romDir = args.Length > 0 ? args[0] : ".roms";

byte[] basic = LoadRom(romDir, "basic.rom", "c-64-basic.rom");
byte[] kernal = LoadRom(romDir, "kernal.rom", "c-64-kernal.rom");
byte[] chargen = LoadRom(romDir, "chargen.rom", "c-64-chargen.rom");

var bus = new C64Bus(basic, kernal, chargen);
var cpu = new Cpu6510(bus);
bus.Vic.GetCpuCycles = () => cpu.TotalCycles;
cpu.Reset();

Console.WriteLine($"Reset -> PC=${cpu.PC:X4}");

// Screen codes for "READY." (uppercase set).
byte[] readySeq = { 0x12, 0x05, 0x01, 0x04, 0x19, 0x2E };

const long cyclesPerFrame = 63L * 312; // PAL
const int maxFrames = 600;
int readyFrame = -1;
for (int frame = 0; frame < maxFrames; frame++)
{
    cpu.RunForCycles(cyclesPerFrame);
    if (ScreenContains(bus, readySeq)) { readyFrame = frame; break; }
}

Console.WriteLine(readyFrame >= 0
    ? $"READY. after {readyFrame} frames ({cpu.TotalCycles} cycles), PC=${cpu.PC:X4}"
    : $"Timed out after {maxFrames} frames, PC=${cpu.PC:X4}");

// PETSCII text dump of screen RAM.
Console.WriteLine("--- screen ---");
for (int row = 0; row < 25; row++)
{
    var sb = new System.Text.StringBuilder();
    for (int col = 0; col < 40; col++)
        sb.Append(ScreenChar(bus.Read((ushort)(0x0400 + row * 40 + col))));
    Console.WriteLine(sb.ToString().TrimEnd());
}

// Render the VIC framebuffer to a BMP.
var pixels = new byte[VicIi.FrameWidth * VicIi.FrameHeight];
bus.Vic.RenderFrame(pixels);
WriteBmp("boot.bmp", pixels, VicIi.FrameWidth, VicIi.FrameHeight, VicIi.Palette);
Console.WriteLine("Wrote boot.bmp");

static byte[] LoadRom(string dir, params string[] names)
{
    foreach (var name in names)
    {
        string path = Path.Combine(dir, name);
        if (File.Exists(path)) return File.ReadAllBytes(path);
    }
    throw new FileNotFoundException(
        $"ROM not found in '{dir}'. Tried: {string.Join(", ", names)}. " +
        "Copy your BASIC/Kernal/character ROMs there first.");
}

static bool ScreenContains(C64Bus bus, byte[] seq)
{
    for (int i = 0; i <= 1000 - seq.Length; i++)
    {
        bool match = true;
        for (int j = 0; j < seq.Length; j++)
            if (bus.Read((ushort)(0x0400 + i + j)) != seq[j]) { match = false; break; }
        if (match) return true;
    }
    return false;
}

static char ScreenChar(byte code)
{
    int c = code & 0x7F;
    if (c >= 64) c &= 0x3F;
    return c switch
    {
        0 => '@',
        >= 1 and <= 26 => (char)('A' + c - 1),
        27 => '[', 28 => '#', 29 => ']', 30 => '^', 31 => '_',
        32 => ' ',
        >= 33 and <= 47 => (char)('!' + c - 33),
        >= 48 and <= 57 => (char)('0' + c - 48),
        58 => ':', 59 => ';', 60 => '<', 61 => '=', 62 => '>', 63 => '?',
        _ => '?',
    };
}

static void WriteBmp(string path, byte[] indices, int w, int h, uint[] palette)
{
    int rowSize = ((w * 3 + 3) / 4) * 4;
    using var bw = new BinaryWriter(File.Create(path));
    bw.Write((ushort)0x4D42);
    bw.Write(54 + rowSize * h);
    bw.Write(0);
    bw.Write(54);
    bw.Write(40); bw.Write(w); bw.Write(h);
    bw.Write((ushort)1); bw.Write((ushort)24);
    bw.Write(0); bw.Write(rowSize * h);
    bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0);
    var pad = new byte[rowSize - w * 3];
    for (int y = h - 1; y >= 0; y--)
    {
        for (int x = 0; x < w; x++)
        {
            uint rgb = palette[indices[y * w + x] & 15];
            bw.Write((byte)(rgb & 0xFF));
            bw.Write((byte)((rgb >> 8) & 0xFF));
            bw.Write((byte)((rgb >> 16) & 0xFF));
        }
        bw.Write(pad);
    }
}
