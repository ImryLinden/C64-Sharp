using System.Text.Json;

namespace C64Sharp;

/// <summary>Persistent user settings, stored in config\settings.json next to the exe.</summary>
public sealed class AppSettings
{
    public KeyboardSettings Keyboard { get; set; } = new();
    public JoystickSettings Joystick { get; set; } = new();
    public AudioSettings Audio { get; set; } = new();
    public RomSettings Roms { get; set; } = new();

    public static string ConfigDir =>
        Path.Combine(AppContext.BaseDirectory, "config");
    public static string ConfigPath =>
        Path.Combine(ConfigDir, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>Load settings; returns defaults if the file is missing or invalid.</summary>
    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(
                    File.ReadAllText(ConfigPath), JsonOptions);
                if (s != null)
                {
                    s.Keyboard ??= new KeyboardSettings();
                    s.Joystick ??= new JoystickSettings();
                    s.Audio ??= new AudioSettings();
                    s.Roms ??= new RomSettings();
                    return s;
                }
            }
        }
        catch { }
        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(ConfigDir);
        File.WriteAllText(ConfigPath,
            JsonSerializer.Serialize(this, JsonOptions));
    }

    public AppSettings Clone()
    {
        var s = JsonSerializer.Deserialize<AppSettings>(
            JsonSerializer.Serialize(this, JsonOptions), JsonOptions);
        return s ?? new AppSettings();
    }
}

/// <summary>PC virtual-key codes (KeyEventArgs.KeyValue) for special C64 keys.</summary>
public sealed class KeyboardSettings
{
    public int RunStop { get; set; } = 0x1B;   // Escape
    public int Restore { get; set; } = 0x78;   // F9
    public int ClrHome { get; set; } = 0x24;   // Home
    public int Commodore { get; set; } = 0x09; // Tab
    public int Ctrl { get; set; } = 0x11;      // Control (either side)
    public int F1 { get; set; } = 0x70;
    public int F2 { get; set; } = 0x71;
    public int F3 { get; set; } = 0x72;
    public int F4 { get; set; } = 0x73;
    public int F5 { get; set; } = 0x74;
    public int F6 { get; set; } = 0x75;
    public int F7 { get; set; } = 0x76;
    public int F8 { get; set; } = 0x77;
}

public sealed class JoystickSettings
{
    public bool Enabled { get; set; } = false;
    public bool KeyboardEnabled { get; set; } = false;
    public string DeviceName { get; set; } = "";
    public string Up { get; set; } = "POVUp";
    public string Down { get; set; } = "POVDown";
    public string Left { get; set; } = "POVLeft";
    public string Right { get; set; } = "POVRight";
    public string Fire { get; set; } = "Button1";
}

public sealed class AudioSettings
{
    public bool Enabled { get; set; } = true;
}

public sealed class RomSettings
{
    public string Basic { get; set; } = @".\.roms\basic.rom";
    public string Kernal { get; set; } = @".\.roms\kernal.rom";
    public string Chargen { get; set; } = @".\.roms\chargen.rom";
}
