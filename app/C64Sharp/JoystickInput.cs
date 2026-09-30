using System.Runtime.InteropServices;
using System.Text;

namespace C64Sharp;

/// <summary>USB joystick access via winmm.dll (no extra dependencies).</summary>
internal static class JoystickInput
{
    private const int JoyReturnAll = 0xFF;
    private const int PovCentered = 0xFFFF;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOYINFOEX
    {
        public int dwSize;
        public int dwFlags;
        public int dwXpos;
        public int dwYpos;
        public int dwZpos;
        public int dwRpos;
        public int dwUpos;
        public int dwVpos;
        public int dwButtons;
        public int dwButtonNumber;
        public int dwPOV;
        public int dwReserved1;
        public int dwReserved2;
    }

    [DllImport("winmm.dll")]
    private static extern int joyGetNumDevs();

    [DllImport("winmm.dll")]
    private static extern int joyGetDevCaps(int uJoyID, byte[] pjc, int cbjc);

    [DllImport("winmm.dll")]
    private static extern int joyGetPosEx(int uJoyID, ref JOYINFOEX pji);

    public sealed class DeviceInfo
    {
        public int Id;
        public string Name = "";
        public string Source = "winmm"; // "winmm" or "hid"
        public string Path = "";        // HID device path when Source == "hid"
    }

    public static List<DeviceInfo> GetDevices()
    {
        var list = new List<DeviceInfo>();
        int n;
        try { n = joyGetNumDevs(); }
        catch { return list; }
        for (int i = 0; i < n; i++)
        {
            string? name = GetDeviceName(i);
            if (name != null)
                list.Add(new DeviceInfo { Id = i, Name = name });
        }
        return list;
    }

    private static string? GetDeviceName(int id)
    {
        try
        {
            var buf = new byte[512];
            if (joyGetDevCaps(id, buf, buf.Length) != 0) return null;
            string name = ParseDeviceName(buf);
            return string.IsNullOrEmpty(name) ? $"Joystick {id}" : name;
        }
        catch { return null; }
    }

    private static string ParseDeviceName(byte[] buf)
    {
        // JOYCAPS layout: wMid(0), wPid(2), szPname[32] at offset 4 (ANSI).
        int len = 0;
        while (len < 32 && buf[4 + len] != 0) len++;
        string name = Encoding.ASCII.GetString(buf, 4, len).Trim();
        return name;
    }

    /// <summary>
    /// Describe what the winmm joystick API reports, for troubleshooting
    /// devices that show up in Windows' Game Controllers panel but not in
    /// our list. The caller writes the result to the debug log.
    /// </summary>
    public static string Diagnose()
    {
        var sb = new StringBuilder();
        int n;
        try { n = joyGetNumDevs(); }
        catch (Exception ex)
        {
            return $"Joystick diagnose: joyGetNumDevs threw {ex.GetType().Name}: {ex.Message}";
        }
        sb.AppendLine($"Joystick diagnose: joyGetNumDevs() = {n}");
        for (int i = 0; i < n; i++)
        {
            try
            {
                var buf = new byte[512];
                int rc = joyGetDevCaps(i, buf, buf.Length);
                string name = rc == 0 ? ParseDeviceName(buf) : "(no device)";
                if (string.IsNullOrEmpty(name)) name = $"Joystick {i}";
                sb.AppendLine($"  id {i}: joyGetDevCaps -> {rc}, name=\"{name}\"");
            }
            catch (Exception ex)
            {
                sb.AppendLine($"  id {i}: threw {ex.GetType().Name}: {ex.Message}");
            }
        }
        return sb.ToString().TrimEnd();
    }

    public sealed class State
    {
        public int X;       // 0..65535
        public int Y;       // 0..65535
        public int Pov;     // 0..35900, or -1 when centered
        public int Buttons; // bitmask
    }

    public static bool TryGetState(int id, out State? state)
    {
        state = null;
        try
        {
            var ji = new JOYINFOEX
            {
                dwSize = Marshal.SizeOf<JOYINFOEX>(),
                dwFlags = JoyReturnAll,
            };
            if (joyGetPosEx(id, ref ji) != 0) return false;
            state = new State
            {
                X = ji.dwXpos,
                Y = ji.dwYpos,
                Pov = ji.dwPOV == PovCentered ? -1 : ji.dwPOV,
                Buttons = ji.dwButtons,
            };
            return true;
        }
        catch { return false; }
    }

    /// <summary>Convert a polled state + mapping into the C64 joystick byte (active-low).</summary>
    public static byte ToC64Byte(State st, JoystickSettings cfg)
    {
        byte b = 0xFF;
        if (IsDirActive(st, cfg.Up)) b &= 0xFE;    // bit 0: Up
        if (IsDirActive(st, cfg.Down)) b &= 0xFD;  // bit 1: Down
        if (IsDirActive(st, cfg.Left)) b &= 0xFB;  // bit 2: Left
        if (IsDirActive(st, cfg.Right)) b &= 0xF7; // bit 3: Right
        if (IsButtonActive(st.Buttons, cfg.Fire)) b &= 0xEF; // bit 4: Fire
        return b;
    }

    private static bool IsDirActive(State st, string mapping) => mapping switch
    {
        "POVUp" => st.Pov >= 0 && (st.Pov <= 4500 || st.Pov >= 31500),
        "POVRight" => st.Pov >= 4500 && st.Pov <= 13500,
        "POVDown" => st.Pov >= 13500 && st.Pov <= 22500,
        "POVLeft" => st.Pov >= 22500 && st.Pov <= 31500,
        "AxisX-" => st.X < 20000,
        "AxisX+" => st.X > 45535,
        "AxisY-" => st.Y < 20000,
        "AxisY+" => st.Y > 45535,
        _ => false,
    };

    private static bool IsButtonActive(int buttons, string mapping)
    {
        if (mapping.StartsWith("Button", StringComparison.Ordinal) &&
            int.TryParse(mapping.Substring(6), out int n) && n >= 1 && n <= 32)
            return (buttons & (1 << (n - 1))) != 0;
        return false;
    }
}
