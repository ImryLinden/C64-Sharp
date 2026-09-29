using C64.Core.Cia;
using C64.Core.Cpu;
using C64.Core.Memory;

// M5 demo: interactive C64 terminal. Boots the real Kernal, then lets you
// type at the READY. prompt. The console mirrors the 40x25 text screen.
//
// Usage: dotnet run --project demo/VicTerm [-- <roms-folder>]

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
Console.WriteLine($"Booted after {cpu.TotalCycles} cycles.");

var kbd = bus.Keyboard;
string? prevScreen = null;
var heldKeys = new List<(int Col, int Row, int ReleaseFrame)>();
int frame = 0;

Console.WriteLine("Type at the prompt. Esc quits.");
Console.WriteLine();
DrawScreen(bus, ref prevScreen);

while (true)
{
    cpu.RunForCycles(cyclesPerFrame);
    frame++;

    // Release keys whose hold time expired.
    for (int i = heldKeys.Count - 1; i >= 0; i--)
        if (frame >= heldKeys[i].ReleaseFrame)
        {
            kbd.SetKey(heldKeys[i].Col, heldKeys[i].Row, false);
            heldKeys.RemoveAt(i);
        }

    // Poll for host keys (non-blocking).
    while (Console.KeyAvailable)
    {
        var ki = Console.ReadKey(intercept: true);
        if (ki.Key == ConsoleKey.Escape) return;
        foreach (var (col, row) in MapKey(ki))
        {
            kbd.SetKey(col, row, true);
            heldKeys.Add((col, row, frame + 6)); // hold ~100ms for debounce
        }
    }

    DrawScreen(bus, ref prevScreen);
    System.Threading.Thread.Sleep(2); // don't spin the host CPU
}

static byte[] LoadRom(string dir, string kind)
{
    foreach (var n in new[] { $"{kind}.rom", $"c-64-{kind}.rom" })
    {
        string p = Path.Combine(dir, n);
        if (File.Exists(p)) return File.ReadAllBytes(p);
    }
    throw new FileNotFoundException($"Put {kind}.rom in '{dir}'.");
}

static void DrawScreen(C64Bus bus, ref string? prev)
{
    var sb = new System.Text.StringBuilder();
    for (int row = 0; row < 25; row++)
    {
        for (int col = 0; col < 40; col++)
            sb.Append(ScreenChar(bus.Read((ushort)(0x0400 + row * 40 + col))));
        if (row < 24) sb.Append('\n');
    }
    string screen = sb.ToString();
    if (screen == prev) return;
    prev = screen;
    Console.SetCursorPosition(0, 3);
    Console.Write(screen);
}

static bool ScreenHas(C64Bus bus, string text)
{
    var sb = new System.Text.StringBuilder();
    for (int i = 0; i < 1000; i++)
        sb.Append(ScreenChar(bus.Read((ushort)(0x0400 + i))));
    return sb.ToString().Contains(text);
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

/// <summary>Maps a host key to C64 matrix positions (US layout).</summary>
static List<(int Col, int Row)> MapKey(ConsoleKeyInfo ki)
{
    var keys = new List<(int Col, int Row)>();
    bool shift = (ki.Modifiers & ConsoleModifiers.Shift) != 0;
    if (shift) keys.Add(KeyboardMatrix.Key("LSHIFT"));

    string? name = ki.Key switch
    {
        ConsoleKey.Spacebar => "SPACE",
        ConsoleKey.Enter => "RETURN",
        ConsoleKey.Backspace => "DEL",
        >= ConsoleKey.A and <= ConsoleKey.Z => ki.Key.ToString(),
        >= ConsoleKey.D0 and <= ConsoleKey.D9 => ((char)('0' + ki.Key - ConsoleKey.D0)).ToString(),
        _ => null,
    };

    // Punctuation via KeyChar for the OEM keys.
    if (name == null && ki.KeyChar != 0)
    {
        name = ki.KeyChar switch
        {
            '+' => "+", '-' => "-", '*' => "*", '/' => "/",
            '.' => ".", ':' => ":", ';' => ";", ',' => ",",
            '=' => "=", '@' => "@", '"' => "2", '\'' => "7",
            '!' => "1", '#' => "3", '$' => "4", '%' => "5",
            '&' => "6", '(' => "8", ')' => "9",
            _ => null,
        };
        // Shifted punctuation needs shift held.
        if (name != null && "\"'!#$%&()".Contains(ki.KeyChar) && !shift)
            keys.Add(KeyboardMatrix.Key("LSHIFT"));
    }

    if (name != null)
    {
        try { keys.Add(KeyboardMatrix.Key(name)); }
        catch (ArgumentException) { /* unmapped */ }
    }
    return keys;
}
