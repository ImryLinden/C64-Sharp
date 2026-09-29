using C64.Core.Cia;
using C64.Core.Cpu;
using C64.Core.Memory;
using C64.Core.Vic;

// M6 demo: boots the real Kernal, types a BASIC program that switches
// to standard bitmap mode and fills the bitmap, then renders to bitmap.bmp.
//
// Usage: dotnet run --project demo/VicBitmap [-- <roms-folder>]

string romDir = args.Length > 0 ? args[0] : ".roms";
var bus = new C64Bus(LoadRom(romDir, "basic"), LoadRom(romDir, "kernal"), LoadRom(romDir, "chargen"));
var cpu = new Cpu6510(bus);
bus.Vic.GetCpuCycles = () => cpu.TotalCycles;
bus.GetCpuCycles = () => cpu.TotalCycles;
cpu.IrqLine = bus.GetIrq;
cpu.Reset();

Console.WriteLine("Booting...");
const long cyclesPerFrame = 63L * 312; // PAL
for (int i = 0; i < 120 && !ScreenHas(bus, "READY."); i++)
    cpu.RunForCycles(cyclesPerFrame);
Console.WriteLine("Booted. Typing bitmap program...");

// Type the BASIC program (holds each key ~100ms for the Kernal debounce).
string[] program = {
    "10 POKE53272,24:POKE53265,59",
    "20 FORI=8192TO16383:POKEI,85:NEXT",
    "RUN",
};
var kbd = bus.Keyboard;
foreach (var line in program)
{
    foreach (char c in line)
    {
        var (col, row) = MapChar(c);
        kbd.SetKey(col, row, true);
        for (int f = 0; f < 6; f++) cpu.RunForCycles(cyclesPerFrame);
        kbd.SetKey(col, row, false);
        for (int f = 0; f < 2; f++) cpu.RunForCycles(cyclesPerFrame);
    }
    // Return
    kbd.SetKey(0, 1, true);
    for (int f = 0; f < 6; f++) cpu.RunForCycles(cyclesPerFrame);
    kbd.SetKey(0, 1, false);
    for (int f = 0; f < 10; f++) cpu.RunForCycles(cyclesPerFrame);
}

Console.WriteLine("Running (filling 8KB bitmap)...");
for (int i = 0; i < 300; i++) cpu.RunForCycles(cyclesPerFrame);

// Render.
var frame = new byte[VicIi.FrameWidth * VicIi.FrameHeight];
bus.Vic.RenderFrame(frame);
WriteBmp("bitmap.bmp", frame, VicIi.FrameWidth, VicIi.FrameHeight);
Console.WriteLine("Wrote bitmap.bmp.");

// ---- helpers ----

static byte[] LoadRom(string dir, string kind)
{
    foreach (var n in new[] { $"{kind}.rom", $"c-64-{kind}.rom" })
    {
        string p = Path.Combine(dir, n);
        if (File.Exists(p)) return File.ReadAllBytes(p);
    }
    throw new FileNotFoundException($"Put {kind}.rom in '{dir}'.");
}

static bool ScreenHas(C64Bus bus, string text)
{
    for (int r = 0; r < 25; r++)
    {
        var sb = new System.Text.StringBuilder();
        for (int c = 0; c < 40; c++)
            sb.Append((char)bus.Read((ushort)(0x0400 + r * 40 + c)));
        if (sb.ToString().Contains(text)) return true;
    }
    return false;
}

// US keyboard -> (col,row) in the C64 matrix. Covers the chars we type.
static (int Col, int Row) MapChar(char c) => c switch
{
    '0' => (4, 3), '1' => (7, 0), '2' => (7, 3), '3' => (1, 0),
    '4' => (1, 3), '5' => (2, 0), '6' => (2, 3), '7' => (3, 0),
    '8' => (3, 3), '9' => (4, 0),
    'A' => (1, 2), 'B' => (3, 4), 'C' => (2, 4), 'D' => (2, 2),
    'E' => (1, 6), 'F' => (2, 5), 'G' => (3, 2), 'H' => (3, 5),
    'I' => (4, 1), 'J' => (4, 2), 'K' => (4, 5), 'L' => (5, 2),
    'M' => (4, 4), 'N' => (4, 7), 'O' => (4, 6), 'P' => (5, 1),
    'Q' => (7, 6), 'R' => (2, 1), 'S' => (1, 5), 'T' => (2, 6),
    'U' => (3, 6), 'V' => (3, 7), 'W' => (1, 1), 'X' => (2, 7),
    'Y' => (3, 1), 'Z' => (1, 4),
    ' ' => (7, 4), ':' => (5, 5), ',' => (5, 7), '=' => (6, 5),
    _ => throw new ArgumentException($"No mapping for '{c}'"),
};

static void WriteBmp(string path, byte[] frame, int w, int h)
{
    // 24-bit BMP, bottom-up.
    int rowSize = (w * 3 + 3) & ~3;
    int dataSize = rowSize * h;
    using var fs = new FileStream(path, FileMode.Create);
    using var bw = new BinaryWriter(fs);
    bw.Write((ushort)0x4D42); bw.Write(14 + 40 + dataSize); bw.Write(0); bw.Write(14 + 40);
    bw.Write(40); bw.Write(w); bw.Write(h); bw.Write((ushort)1); bw.Write((ushort)24);
    bw.Write(0); bw.Write(dataSize); bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0);
    var pad = new byte[rowSize - w * 3];
    for (int y = h - 1; y >= 0; y--)
    {
        for (int x = 0; x < w; x++)
        {
            uint rgb = VicIi.Palette[frame[y * w + x] & 0x0F];
            bw.Write((byte)(rgb & 0xFF)); bw.Write((byte)((rgb >> 8) & 0xFF)); bw.Write((byte)((rgb >> 16) & 0xFF));
        }
        bw.Write(pad);
    }
}
