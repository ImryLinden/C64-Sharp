using C64.Core.Cpu;
using C64.Core.Memory;
using C64.Core.Cia;

// MatrixTest: verifies that setting a key in the matrix causes the CIA
// to report the correct row bits when the Kernal scans each column.
// No IRQ needed — we drive the CIA ports directly like the Kernal does.
string romDir = args.Length > 0 ? args[0] : ".roms";

byte[] basic = LoadRom(romDir, "basic.rom", "c-64-basic.rom");
byte[] kernal = LoadRom(romDir, "kernal.rom", "c-64-kernal.rom");
byte[] chargen = LoadRom(romDir, "chargen.rom", "c-64-chargen.rom");

var bus = new C64Bus(basic, kernal, chargen);

// The Kernal's $EB81 table (from ROM dump).
byte[] table = new byte[64];
Array.Copy(kernal, 0xB81, table, 0, 64);

var tests = new (string Name, byte Expected)[]
{
    ("1", 0x31), ("2", 0x32), ("3", 0x33), ("0", 0x30),
    ("A", 0x41), ("Q", 0x51), ("SPACE", 0x20),
};

int pass = 0, fail = 0;
foreach (var (name, expected) in tests)
{
    var (col, row) = KeyboardMatrix.Key(name);
    bus.Keyboard.SetKey(col, row, true);

    // Simulate Kernal scan: for each column, drive it low and read rows.
    byte decoded = 0xFF;
    for (int c = 0; c < 8; c++)
    {
        byte pa = (byte)~(1 << c); // drive column c low
        byte rows = bus.Keyboard.ReadRows(pa);
        // Find which row bits are low.
        for (int r = 0; r < 8; r++)
        {
            if ((rows & (1 << r)) == 0)
            {
                // Key at (c, r). Look up in table.
                decoded = table[c * 8 + r];
            }
        }
    }
    bus.Keyboard.SetKey(col, row, false);

    bool ok = decoded == expected;
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}: '{name}' at ({col},{row}) -> " +
        $"decoded=${decoded:X2} expected=${expected:X2}");
    if (ok) pass++; else fail++;
}
Console.WriteLine($"\n{pass} passed, {fail} failed.");

static byte[] LoadRom(string dir, params string[] names)
{
    foreach (var name in names)
    {
        string p = Path.Combine(dir, name);
        if (File.Exists(p)) return File.ReadAllBytes(p);
    }
    throw new FileNotFoundException($"ROM not found in '{dir}'");
}
