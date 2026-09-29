using C64.Core.Cpu;
using C64.Core.Memory;

namespace C64Sharp;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        var settings = AppSettings.Load();

        byte[]? basic = TryLoadRomFile(settings.Roms.Basic) ?? TryLoadRom(args, "basic");
        byte[]? kernal = TryLoadRomFile(settings.Roms.Kernal) ?? TryLoadRom(args, "kernal");
        byte[]? chargen = TryLoadRomFile(settings.Roms.Chargen) ?? TryLoadRom(args, "chargen");

        string? missing = basic == null ? "BASIC"
            : kernal == null ? "Kernal"
            : chargen == null ? "Character" : null;
        if (missing != null)
        {
            Application.Run(new RomSetupForm(settings, missing));
            return;
        }

        // DOS ROM is optional: without it, the emulator runs diskless.
        byte[]? dosRom = TryLoadRom(args, "dos1541", "1541", "c-drive-1541");
        using var emulator = new Emulator(basic!, kernal!, chargen!, dosRom);
        emulator.Start();
        Application.Run(new MainForm(emulator, settings));
    }

    static string RomDir(string[] args)
    {
        string romDir = args.Length > 0 ? args[0] :
            Path.Combine(AppContext.BaseDirectory, ".roms");
        if (!Directory.Exists(romDir))
            romDir = ".roms";
        return romDir;
    }

    /// <summary>Load a ROM from an explicit path (absolute or relative to the exe).</summary>
    static byte[]? TryLoadRomFile(string path)
    {
        try
        {
            string p = Path.IsPathRooted(path)
                ? path
                : Path.Combine(AppContext.BaseDirectory, path);
            if (File.Exists(p)) return File.ReadAllBytes(p);
        }
        catch { }
        return null;
    }

    static byte[]? TryLoadRom(string[] args, params string[] kinds)
    {
        string dir = RomDir(args);
        foreach (var kind in kinds)
        {
            foreach (var n in new[] { $"{kind}.rom", $"c-64-{kind}.rom", $"c-drive-{kind}.rom" })
            {
                string p = Path.Combine(dir, n);
                if (File.Exists(p)) return File.ReadAllBytes(p);
            }
        }
        return null;
    }
}
