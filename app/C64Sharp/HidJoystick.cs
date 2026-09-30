using System.Runtime.InteropServices;
using System.Text;

namespace C64Sharp;

/// <summary>
/// USB HID gamepad access via Raw Input (user32) + HID parsing (hid.dll).
/// This is the fallback for gamepads that Windows' Game Controllers panel
/// (DirectInput) sees but the legacy winmm joystick API does not report
/// (joyGetDevCaps returns JOYERR_PARMS for every id). No extra dependencies.
/// Produces the same <see cref="JoystickInput.State"/> shape as the winmm path.
/// </summary>
internal static class HidJoystick
{
    private const int WM_INPUT = 0x00FF;
    private const uint RIM_TYPEHID = 2;
    private const uint RIDI_DEVICENAME = 0x20000007;
    private const uint RIDI_DEVICEINFO = 0x2000000b;
    private const uint RID_INPUT = 0x10000003;
    private const uint RIDEV_INPUTSINK = 0x00000100;
    private const uint RIDEV_REMOVE = 0x00000001;
    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint OPEN_EXISTING = 3;
    private const int HidP_Input = 0;
    private const int HIDP_STATUS_SUCCESS = 0x00110000;

    // Generic Desktop page usages we care about.
    private const ushort Usage_X = 0x30;
    private const ushort Usage_Y = 0x31;
    private const ushort Usage_Hat = 0x39;
    private const ushort UsagePage_GenericDesktop = 0x01;
    private const ushort UsagePage_Button = 0x09;

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICELIST
    {
        public IntPtr hDevice;
        public uint dwType;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE
    {
        public ushort usUsagePage;
        public ushort usUsage;
        public uint dwFlags;
        public IntPtr hwndTarget;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTHEADER
    {
        public uint dwType;
        public uint dwSize;
        public IntPtr hDevice;
        public IntPtr wParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RID_DEVICE_INFO_HID
    {
        public uint dwVendorId;
        public uint dwProductId;
        public uint dwVersionNumber;
        public ushort usUsagePage;
        public ushort usUsage;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RID_DEVICE_INFO
    {
        public uint cbSize;
        public uint dwType;
        public RID_DEVICE_INFO_HID hid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDP_CAPS
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
        public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    // Native HIDP_BUTTON_CAPS / HIDP_VALUE_CAPS are both 72 bytes; only the
    // fields we need are mapped, but the size must match the native layout
    // exactly because the HidP_Get*Caps calls fill arrays of them.
    [StructLayout(LayoutKind.Explicit, Size = 72)]
    private struct HIDP_BUTTON_CAPS
    {
        [FieldOffset(0)] public ushort UsagePage;
        [FieldOffset(12)] public byte IsRange;
        [FieldOffset(56)] public ushort RangeUsageMin;
        [FieldOffset(58)] public ushort RangeUsageMax;
        [FieldOffset(56)] public ushort NotRangeUsage;
    }

    [StructLayout(LayoutKind.Explicit, Size = 72)]
    private struct HIDP_VALUE_CAPS
    {
        [FieldOffset(0)] public ushort UsagePage;
        [FieldOffset(12)] public byte IsRange;
        [FieldOffset(18)] public ushort BitSize;
        [FieldOffset(40)] public int LogicalMin;
        [FieldOffset(44)] public int LogicalMax;
        [FieldOffset(56)] public ushort RangeUsageMin;
        [FieldOffset(58)] public ushort RangeUsageMax;
        [FieldOffset(56)] public ushort NotRangeUsage;
    }

    [DllImport("user32.dll")]
    private static extern uint GetRawInputDeviceList(IntPtr pRawInputDeviceList, ref uint puiNumDevices, uint cbSize);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetRawInputDeviceInfo(IntPtr hDevice, uint uiCommand, IntPtr pData, ref uint pcbSize);

    [DllImport("user32.dll")]
    private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] pRawInputDevices, uint uiNumDevices, uint cbSize);

    [DllImport("user32.dll")]
    private static extern uint GetRawInputData(IntPtr hRawInput, uint uiCommand, IntPtr pData, ref uint pcbSize, uint cbSizeHeader);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("hid.dll")]
    private static extern bool HidD_GetPreparsedData(IntPtr hidDeviceObject, out IntPtr preparsedData);

    [DllImport("hid.dll")]
    private static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

    [DllImport("hid.dll", CharSet = CharSet.Unicode)]
    private static extern bool HidD_GetProductString(IntPtr hidDeviceObject, byte[] buffer, uint bufferLength);

    [DllImport("hid.dll")]
    private static extern int HidP_GetCaps(IntPtr preparsedData, out HIDP_CAPS capabilities);

    [DllImport("hid.dll")]
    private static extern int HidP_GetButtonCaps(int reportType, [Out] HIDP_BUTTON_CAPS[] buttonCaps,
        ref ushort buttonCapsLength, IntPtr preparsedData);

    [DllImport("hid.dll")]
    private static extern int HidP_GetValueCaps(int reportType, [Out] HIDP_VALUE_CAPS[] valueCaps,
        ref ushort valueCapsLength, IntPtr preparsedData);

    [DllImport("hid.dll")]
    private static extern int HidP_GetUsages(int reportType, ushort usagePage, ushort linkCollection,
        [Out] ushort[] usageList, ref uint usageLength, IntPtr preparsedData, byte[] report, uint reportLength);

    [DllImport("hid.dll")]
    private static extern int HidP_GetUsageValue(int reportType, ushort usagePage, ushort linkCollection,
        ushort usage, out uint usageValue, IntPtr preparsedData, byte[] report, uint reportLength);

    private sealed class DeviceContext : IDisposable
    {
        public string Path = "";
        public string Name = "";
        public IntPtr DeviceHandle = IntPtr.Zero;
        public IntPtr Preparsed = IntPtr.Zero;
        public ushort ButtonMin = 1;
        public ushort ButtonMax = 0; // 0 = no button caps found
        public bool HasX, HasY, HasHat;
        public ushort XBitSize = 8, YBitSize = 8;
        public int XMin, XMax = 255, YMin, YMax = 255;
        public readonly JoystickInput.State State = new();
        public bool HasReport;
        public bool LoggedError;

        public void Dispose()
        {
            if (Preparsed != IntPtr.Zero) { try { HidD_FreePreparsedData(Preparsed); } catch { } Preparsed = IntPtr.Zero; }
            if (DeviceHandle != IntPtr.Zero && DeviceHandle != new IntPtr(-1))
            { try { CloseHandle(DeviceHandle); } catch { } DeviceHandle = IntPtr.Zero; }
        }
    }

    private static readonly object _lock = new();
    private static readonly Dictionary<string, DeviceContext> _contexts = new(StringComparer.OrdinalIgnoreCase);

    public static int WmInputMessageId => WM_INPUT;

    /// <summary>Register to receive WM_INPUT for HID gamepads on the given window.</summary>
    public static void Register(IntPtr hwnd)
    {
        try
        {
            var devs = new RAWINPUTDEVICE[]
            {
                new() { usUsagePage = 1, usUsage = 4, dwFlags = RIDEV_INPUTSINK, hwndTarget = hwnd }, // Joystick
                new() { usUsagePage = 1, usUsage = 5, dwFlags = RIDEV_INPUTSINK, hwndTarget = hwnd }, // Game Pad
                new() { usUsagePage = 1, usUsage = 8, dwFlags = RIDEV_INPUTSINK, hwndTarget = hwnd }, // Multi-axis
            };
            bool ok = RegisterRawInputDevices(devs, (uint)devs.Length, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
            Emulator.DebugLog(ok ? "HidJoystick: raw input registered (usages 4/5/8)"
                                 : "HidJoystick: RegisterRawInputDevices FAILED");
        }
        catch (Exception ex)
        {
            try { Emulator.DebugLog("HidJoystick: register threw " + ex.GetType().Name); } catch { }
        }
    }

    public static void Unregister()
    {
        try
        {
            var devs = new RAWINPUTDEVICE[]
            {
                new() { usUsagePage = 1, usUsage = 4, dwFlags = RIDEV_REMOVE, hwndTarget = IntPtr.Zero },
                new() { usUsagePage = 1, usUsage = 5, dwFlags = RIDEV_REMOVE, hwndTarget = IntPtr.Zero },
                new() { usUsagePage = 1, usUsage = 8, dwFlags = RIDEV_REMOVE, hwndTarget = IntPtr.Zero },
            };
            RegisterRawInputDevices(devs, (uint)devs.Length, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
            lock (_lock)
            {
                foreach (var c in _contexts.Values) c.Dispose();
                _contexts.Clear();
            }
        }
        catch { }
    }

    /// <summary>List HID game controller devices (for the Settings dropdown).</summary>
    public static List<JoystickInput.DeviceInfo> GetDevices()
    {
        var list = new List<JoystickInput.DeviceInfo>();
        try
        {
            uint count = 0;
            uint sz = (uint)Marshal.SizeOf<RAWINPUTDEVICELIST>();
            if (GetRawInputDeviceList(IntPtr.Zero, ref count, sz) != 0) return list;
            if (count == 0 || count > 128) return list;
            IntPtr buf = Marshal.AllocHGlobal((int)(count * sz));
            try
            {
                uint n = count;
                if (GetRawInputDeviceList(buf, ref n, sz) == 0xFFFFFFFF) return list;
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < n; i++)
                {
                    var dev = Marshal.PtrToStructure<RAWINPUTDEVICELIST>(buf + i * (int)sz);
                    if (dev.dwType != RIM_TYPEHID) continue;
                    string path = GetDevicePath(dev.hDevice);
                    if (path.Length == 0 || !seen.Add(path)) continue;
                    var ctx = BuildContext(path);
                    if (ctx == null) continue;
                    string name = ctx.Name;
                    ctx.Dispose();
                    list.Add(new JoystickInput.DeviceInfo { Source = "hid", Path = path, Name = name });
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
            Emulator.DebugLog($"HidJoystick: enumerated {list.Count} HID game controller(s)");
            PruneContexts(); // drop cached contexts for unplugged devices
        }
        catch (Exception ex)
        {
            try { Emulator.DebugLog("HidJoystick: GetDevices threw " + ex.GetType().Name); } catch { }
        }
        return list;
    }

    /// <summary>Handle a WM_INPUT message (call from the window's WndProc).</summary>
    public static void OnRawInput(IntPtr hRawInput)
    {
        try
        {
            uint headerSize = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
            uint size = 0;
            GetRawInputData(hRawInput, RID_INPUT, IntPtr.Zero, ref size, headerSize);
            if (size == 0 || size > 1024) return;
            IntPtr buf = Marshal.AllocHGlobal((int)size);
            try
            {
                uint got = size;
                if (GetRawInputData(hRawInput, RID_INPUT, buf, ref got, headerSize) != size) return;
                var header = Marshal.PtrToStructure<RAWINPUTHEADER>(buf);
                if (header.dwType != RIM_TYPEHID) return;
                int off = (int)headerSize;
                uint hidCount = (uint)Marshal.ReadInt32(buf, off);
                uint hidSize = (uint)Marshal.ReadInt32(buf, off + 4);
                if (hidCount == 0 || hidSize == 0 || hidSize > 256) return;
                var report = new byte[hidSize];
                Marshal.Copy(buf + off + 8, report, 0, (int)hidSize);
                var ctx = GetOrCreateContext(header.hDevice);
                if (ctx != null) ParseReport(ctx, report);
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch { /* never let input handling crash the UI thread */ }
    }

    /// <summary>Latest state for an HID device path; false if no report seen yet.</summary>
    public static bool TryGetState(string path, out JoystickInput.State? state)
    {
        state = null;
        if (string.IsNullOrEmpty(path)) return false;
        try
        {
            lock (_lock)
            {
                if (_contexts.TryGetValue(path, out var ctx) && ctx.HasReport)
                {
                    state = ctx.State;
                    return true;
                }
            }
        }
        catch { }
        return false;
    }

    private static DeviceContext? GetOrCreateContext(IntPtr hDevice)
    {
        string path = GetDevicePath(hDevice);
        if (path.Length == 0) return null;
        lock (_lock)
        {
            if (_contexts.TryGetValue(path, out var existing)) return existing;
            var ctx = BuildContext(path);
            if (ctx != null) _contexts[path] = ctx;
            return ctx;
        }
    }

    /// <summary>Drop cached contexts for devices that are no longer present.</summary>
    private static void PruneContexts()
    {
        try
        {
            var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            uint count = 0;
            uint sz = (uint)Marshal.SizeOf<RAWINPUTDEVICELIST>();
            if (GetRawInputDeviceList(IntPtr.Zero, ref count, sz) == 0 && count > 0 && count <= 128)
            {
                IntPtr buf = Marshal.AllocHGlobal((int)(count * sz));
                try
                {
                    uint n = count;
                    if (GetRawInputDeviceList(buf, ref n, sz) != 0xFFFFFFFF)
                        for (int i = 0; i < n; i++)
                        {
                            var dev = Marshal.PtrToStructure<RAWINPUTDEVICELIST>(buf + i * (int)sz);
                            if (dev.dwType == RIM_TYPEHID) live.Add(GetDevicePath(dev.hDevice));
                        }
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            lock (_lock)
            {
                var dead = new List<string>();
                foreach (var kv in _contexts)
                    if (!live.Contains(kv.Key)) dead.Add(kv.Key);
                foreach (var k in dead) { _contexts[k].Dispose(); _contexts.Remove(k); }
            }
        }
        catch { }
    }

    private static string GetDevicePath(IntPtr hDevice)
    {
        try
        {
            uint size = 0;
            GetRawInputDeviceInfo(hDevice, RIDI_DEVICENAME, IntPtr.Zero, ref size);
            if (size == 0 || size > 512) return "";
            IntPtr buf = Marshal.AllocHGlobal((int)(size * 2));
            try
            {
                uint n = size;
                if (GetRawInputDeviceInfo(hDevice, RIDI_DEVICENAME, buf, ref n) == 0xFFFFFFFF) return "";
                return Marshal.PtrToStringUni(buf) ?? "";
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch { return ""; }
    }

    /// <summary>
    /// Open a device, verify it is a HID game controller, and learn its
    /// report layout (button range, X/Y/hat caps). Returns null if unusable.
    /// </summary>
    private static DeviceContext? BuildContext(string path)
    {
        var ctx = new DeviceContext { Path = path };
        try
        {
            IntPtr h = CreateFile(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (h == new IntPtr(-1))
                h = CreateFile(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
                    IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (h == new IntPtr(-1)) { ctx.Dispose(); return null; }
            ctx.DeviceHandle = h;

            if (!HidD_GetPreparsedData(h, out IntPtr ppd) || ppd == IntPtr.Zero) { ctx.Dispose(); return null; }
            ctx.Preparsed = ppd;
            if (HidP_GetCaps(ppd, out HIDP_CAPS caps) != HIDP_STATUS_SUCCESS) { ctx.Dispose(); return null; }
            if (caps.UsagePage != UsagePage_GenericDesktop ||
                (caps.Usage != 4 && caps.Usage != 5 && caps.Usage != 8))
            { ctx.Dispose(); return null; } // not a joystick / gamepad

            ctx.Name = GetProductString(h) is string s && s.Length > 0
                ? s : $"HID gamepad ({VidPid(path)})";

            // Buttons (usage page 0x09).
            if (caps.NumberInputButtonCaps > 0)
            {
                var bcaps = new HIDP_BUTTON_CAPS[caps.NumberInputButtonCaps];
                ushort len = (ushort)bcaps.Length;
                if (HidP_GetButtonCaps(HidP_Input, bcaps, ref len, ppd) == HIDP_STATUS_SUCCESS)
                {
                    foreach (var b in bcaps)
                    {
                        if (b.UsagePage != UsagePage_Button) continue;
                        if (b.IsRange != 0)
                        {
                            if (ctx.ButtonMax == 0 || b.RangeUsageMin < ctx.ButtonMin) ctx.ButtonMin = b.RangeUsageMin;
                            if (b.RangeUsageMax > ctx.ButtonMax) ctx.ButtonMax = b.RangeUsageMax;
                        }
                        else if (b.NotRangeUsage > ctx.ButtonMax) { ctx.ButtonMax = b.NotRangeUsage; ctx.ButtonMin = 1; }
                    }
                }
            }

            // Axes + hat (usage page 0x01).
            if (caps.NumberInputValueCaps > 0)
            {
                var vcaps = new HIDP_VALUE_CAPS[caps.NumberInputValueCaps];
                ushort len = (ushort)vcaps.Length;
                if (HidP_GetValueCaps(HidP_Input, vcaps, ref len, ppd) == HIDP_STATUS_SUCCESS)
                {
                    foreach (var v in vcaps)
                    {
                        if (v.UsagePage != UsagePage_GenericDesktop) continue;
                        ushort lo = v.IsRange != 0 ? v.RangeUsageMin : v.NotRangeUsage;
                        ushort hi = v.IsRange != 0 ? v.RangeUsageMax : v.NotRangeUsage;
                        if (lo <= Usage_X && Usage_X <= hi)
                        { ctx.HasX = true; ctx.XBitSize = v.BitSize; ctx.XMin = v.LogicalMin; ctx.XMax = v.LogicalMax; }
                        if (lo <= Usage_Y && Usage_Y <= hi)
                        { ctx.HasY = true; ctx.YBitSize = v.BitSize; ctx.YMin = v.LogicalMin; ctx.YMax = v.LogicalMax; }
                        if (lo <= Usage_Hat && Usage_Hat <= hi) ctx.HasHat = true;
                    }
                }
            }
            return ctx;
        }
        catch
        {
            ctx.Dispose();
            return null;
        }
    }

    private static string GetProductString(IntPtr h)
    {
        try
        {
            var buf = new byte[512];
            if (!HidD_GetProductString(h, buf, (uint)buf.Length)) return "";
            string s = Encoding.Unicode.GetString(buf).TrimEnd('\0').Trim();
            return s;
        }
        catch { return ""; }
    }

    private static string VidPid(string path)
    {
        try
        {
            int v = path.IndexOf("VID_", StringComparison.OrdinalIgnoreCase);
            int p = path.IndexOf("PID_", StringComparison.OrdinalIgnoreCase);
            if (v >= 0 && p > v)
                return (path.Substring(v, 8) + ":" + path.Substring(p, 8)).ToUpperInvariant();
        }
        catch { }
        return "unknown";
    }

    private static void ParseReport(DeviceContext ctx, byte[] report)
    {
        try
        {
            var st = ctx.State;

            // Buttons -> bitmask (button 1 = bit 0).
            int mask = 0;
            if (ctx.ButtonMax > 0)
            {
                var usages = new ushort[64];
                uint len = (uint)usages.Length;
                if (HidP_GetUsages(HidP_Input, UsagePage_Button, 0, usages, ref len, ctx.Preparsed, report, (uint)report.Length)
                    == HIDP_STATUS_SUCCESS)
                {
                    for (int i = 0; i < len && i < usages.Length; i++)
                    {
                        int b = usages[i]; // 1-based
                        if (b >= 1 && b <= 32) mask |= 1 << (b - 1);
                    }
                }
            }
            st.Buttons = mask;

            // X/Y axes normalized to 0..65535 (centered default when absent).
            if (ctx.HasX && HidP_GetUsageValue(HidP_Input, UsagePage_GenericDesktop, 0, Usage_X,
                    out uint xv, ctx.Preparsed, report, (uint)report.Length) == HIDP_STATUS_SUCCESS)
                st.X = NormalizeAxis(xv, ctx.XBitSize, ctx.XMin, ctx.XMax);
            else st.X = 32768;
            if (ctx.HasY && HidP_GetUsageValue(HidP_Input, UsagePage_GenericDesktop, 0, Usage_Y,
                    out uint yv, ctx.Preparsed, report, (uint)report.Length) == HIDP_STATUS_SUCCESS)
                st.Y = NormalizeAxis(yv, ctx.YBitSize, ctx.YMin, ctx.YMax);
            else st.Y = 32768;

            // Hat switch -> POV degrees, -1 when centered.
            st.Pov = -1;
            if (ctx.HasHat && HidP_GetUsageValue(HidP_Input, UsagePage_GenericDesktop, 0, Usage_Hat,
                    out uint hv, ctx.Preparsed, report, (uint)report.Length) == HIDP_STATUS_SUCCESS)
                st.Pov = HatToPov(hv);

            ctx.HasReport = true;
        }
        catch
        {
            if (!ctx.LoggedError)
            {
                ctx.LoggedError = true;
                try { Emulator.DebugLog($"HidJoystick: parse failed for '{ctx.Name}'"); } catch { }
            }
        }
    }

    /// <summary>Normalize a raw HID axis value to 0..65535 (pure; unit-testable).</summary>
    internal static int NormalizeAxis(uint raw, int bitSize, int logicalMin, int logicalMax)
    {
        long v = raw;
        // Sign-extend if the field is signed and the API didn't (idempotent).
        if (logicalMin < 0 && bitSize > 0 && bitSize < 32 &&
            (raw & (1u << (bitSize - 1))) != 0 && raw > (uint)logicalMax)
            v = (long)(int)(raw | ~((1u << bitSize) - 1));
        if (logicalMax <= logicalMin) return 32768;
        if (v < logicalMin) v = logicalMin;
        if (v > logicalMax) v = logicalMax;
        return (int)((v - logicalMin) * 65535 / (logicalMax - logicalMin));
    }

    /// <summary>HID hat value (0-7, else centered) to POV degrees or -1 (pure; unit-testable).</summary>
    internal static int HatToPov(uint hat)
    {
        return hat <= 7 ? (int)(hat * 4500) : -1;
    }
}
