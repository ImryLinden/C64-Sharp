using C64.Core.Cia;
using C64.Core.Vic;

namespace C64Sharp;

/// <summary>
/// The C64 screen: draws the VIC-II framebuffer scaled with nearest-neighbor.
/// </summary>
public sealed class ScreenControl : Control
{
    private Bitmap? _bitmap;
    private readonly object _bitmapLock = new();

    public ScreenControl()
    {
        DoubleBuffered = true;
        // 384x272 at 2x = 768x544.
        Size = new Size(VicIi.FrameWidth * 2, VicIi.FrameHeight * 2);
    }

    protected override bool IsInputKey(Keys keyData)
    {
        // Ensure arrow keys are treated as input keys.
        if (keyData == Keys.Left || keyData == Keys.Right ||
            keyData == Keys.Up || keyData == Keys.Down)
            return true;
        return base.IsInputKey(keyData);
    }

    /// <summary>Update from VIC palette indices (called from emulator thread).</summary>
    public void Present(byte[] frame)
    {
        lock (_bitmapLock)
        {
            _bitmap ??= new Bitmap(VicIi.FrameWidth, VicIi.FrameHeight,
                System.Drawing.Imaging.PixelFormat.Format32bppRgb);
            var data = _bitmap.LockBits(
                new Rectangle(0, 0, _bitmap.Width, _bitmap.Height),
                System.Drawing.Imaging.ImageLockMode.WriteOnly,
                _bitmap.PixelFormat);
            // Convert palette indices to BGRA bytes (safe, no unsafe block).
            var bytes = new byte[frame.Length * 4];
            for (int i = 0; i < frame.Length; i++)
            {
                uint c = VicIi.Palette[frame[i] & 0x0F];
                bytes[i * 4] = (byte)(c & 0xFF);
                bytes[i * 4 + 1] = (byte)((c >> 8) & 0xFF);
                bytes[i * 4 + 2] = (byte)((c >> 16) & 0xFF);
                bytes[i * 4 + 3] = 0xFF;
            }
            System.Runtime.InteropServices.Marshal.Copy(bytes, 0, data.Scan0, bytes.Length);
            _bitmap.UnlockBits(data);
        }
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.InterpolationMode =
            System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
        e.Graphics.PixelOffsetMode =
            System.Drawing.Drawing2D.PixelOffsetMode.Half;
        lock (_bitmapLock)
        {
            if (_bitmap != null)
                e.Graphics.DrawImage(_bitmap, ClientRectangle);
            else
                e.Graphics.Clear(Color.Black);
        }
    }
}

/// <summary>Main window: menu, screen, status.</summary>
public sealed class MainForm : Form
{
    private readonly Emulator _emulator;
    private AppSettings _settings;
    private readonly ScreenControl _screen;
    private readonly AudioPlayer? _audio;
    private readonly System.Windows.Forms.Timer _uiTimer;
    private readonly ToolStripStatusLabel _diskStatus;
    private readonly Bitmap _diskIcon;       // floppy, idle
    private readonly Bitmap _diskIconGreen;  // floppy, drive active (blink phase)
    private readonly Bitmap _diskIconGray;   // floppy, no disk
    private int _lastDiskIconState = -1;
    private bool _joystickEnabled;   // numpad joystick, from settings
    private bool _soundEnabled;      // from settings
    private byte _joyBits = 0xFF; // Active-low joystick state
    private JoystickInput.DeviceInfo? _usbDevice; // selected USB joystick (winmm or HID)
    private const string AppVersion = "Alpha 3.9";
    private string _diskName = "";

    public MainForm(Emulator emulator, AppSettings settings)
    {
        _emulator = emulator;
        _settings = settings;
        _joystickEnabled = _settings.Joystick.KeyboardEnabled;
        _soundEnabled = _settings.Audio.Enabled;
        Text = $"C64-Sharp — {AppVersion}";
        BackColor = Color.Black;

        _screen = new ScreenControl { Dock = DockStyle.Fill };
        Controls.Add(_screen);

        var menu = new MenuStrip();
        var file = new ToolStripMenuItem("File");
        var openDisk = new ToolStripMenuItem("Open Disk / Tape Image...", null, OnOpenDisk);
        // HLE drive works without the DOS ROM; always enable.
        openDisk.Enabled = true;
        file.DropDownItems.Add(openDisk);
        file.DropDownItems.Add(new ToolStripMenuItem("Reset", null, (_, _) => Reset()));
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(new ToolStripMenuItem("Exit", null, (_, _) => Close()));
        menu.Items.Add(file);

        var options = new ToolStripMenuItem("Options");
        options.DropDownItems.Add(new ToolStripMenuItem("Settings...", null, (_, _) => OpenSettings()));
        menu.Items.Add(options);

        var help = new ToolStripMenuItem("Help");
        help.DropDownItems.Add(new ToolStripMenuItem("About...", null, (_, _) => ShowAbout()));
        menu.Items.Add(help);

        Controls.Add(menu);
        MainMenuStrip = menu;

        // Status bar: disk indicator.
        var status = new StatusStrip();
        status.BackColor = SystemColors.Control;
        _diskStatus = new ToolStripStatusLabel("No disk");
        _diskStatus.ForeColor = SystemColors.ControlText;
        _diskStatus.ImageScaling = ToolStripItemImageScaling.None;
        status.Items.Add(_diskStatus);
        Controls.Add(status);

        // Disk activity icon (embedded pixel-art floppy).
        _diskIcon = LoadEmbeddedIcon("C64Sharp.Resources.disk.png");
        _diskIconGreen = TintIcon(_diskIcon, new float[][]
        {
            new float[] { 0.2f, 0.2f, 0.2f, 0, 0 },
            new float[] { 0.7f, 0.7f, 0.7f, 0, 0 },
            new float[] { 0.2f, 0.2f, 0.2f, 0, 0 },
            new float[] { 0, 0, 0, 1, 0 },
            new float[] { 0, 0, 0, 0, 1 },
        });
        _diskIconGray = TintIcon(_diskIcon, new float[][]
        {
            new float[] { 0.35f, 0.35f, 0.35f, 0, 0 },
            new float[] { 0.35f, 0.35f, 0.35f, 0, 0 },
            new float[] { 0.35f, 0.35f, 0.35f, 0, 0 },
            new float[] { 0, 0, 0, 1, 0 },
            new float[] { 0, 0, 0, 0, 1 },
        });
        _diskStatus.Image = _diskIconGray;

        // Try audio; continue silently without it.
        try { _audio = new AudioPlayer(); }
        catch { _audio = null; }
        _emulator.SetAudioEnabled(_soundEnabled);

        _uiTimer = new System.Windows.Forms.Timer { Interval = 20 };
        _uiTimer.Tick += (_, _) => { PresentFrame(); PumpAudio(); PollUsbJoystick(); UpdateHookCount(); UpdateFps(); UpdateDiskIcon(); };
        _uiTimer.Start();

        ApplyJoystickDevice();

        // Raw Input for HID gamepads (sees pads winmm doesn't). Needs the window handle.
        try { HidJoystick.Register(this.Handle); } catch { }
        FormClosing += (_, _) => { try { HidJoystick.Unregister(); } catch { } };

        KeyPreview = true;
        KeyDown += OnKeyDown;
        KeyUp += OnKeyUp;

        // Size to fit the screen plus menu and status bar.
        // Note: status.Height is 0 until laid out; use a typical value.
        ClientSize = new Size(_screen.Width, _screen.Height + menu.Height + 24);
    }

    private void PresentFrame()
    {
        if (IsDisposed) return;
        // Poll the latest framebuffer; the emulator thread swaps it under lock.
        // No BeginInvoke queue buildup: if we're behind, we just skip frames.
        _screen.Present(_emulator.Framebuffer);
    }

    private void PumpAudio()
    {
        if (_audio == null || !_soundEnabled) 
        {
            // Drain the queue without playing.
            while (_emulator.DequeueAudio() is { } _) { }
            return;
        }
        while (_emulator.DequeueAudio() is { } samples)
        {
            try { _audio.Play(samples); }
            catch { }
        }
    }

    private void Reset()
    {
        _emulator.RequestReset();
    }

    /// <summary>Blink the disk icon green while the HLE drive is serving data.</summary>
    private void UpdateDiskIcon()
    {
        var drive = _emulator.HleDrive;
        int state;
        if (!drive.HasDisk)
            state = 0; // no disk: gray icon
        else if ((DateTime.UtcNow - drive.LastActivityUtc).TotalMilliseconds < 600)
            state = ((Environment.TickCount / 250) & 1) == 0 ? 3 : 2; // active: blink green
        else
            state = 1; // disk mounted, idle
        if (state == _lastDiskIconState) return;
        _lastDiskIconState = state;
        _diskStatus.Image = state switch
        {
            3 => _diskIconGreen,
            0 => _diskIconGray,
            _ => _diskIcon,
        };
    }

    private static Bitmap LoadEmbeddedIcon(string name)
    {
        using var s = typeof(MainForm).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Missing embedded resource {name}");
        using var tmp = new Bitmap(s);
        return new Bitmap(tmp); // independent copy; the stream can close
    }

    private static Bitmap TintIcon(Bitmap src, float[][] matrix)
    {
        var dst = new Bitmap(src.Width, src.Height,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(dst);
        using var attrs = new System.Drawing.Imaging.ImageAttributes();
        attrs.SetColorMatrix(new System.Drawing.Imaging.ColorMatrix(matrix));
        g.DrawImage(src, new Rectangle(0, 0, dst.Width, dst.Height),
            0, 0, src.Width, src.Height, GraphicsUnit.Pixel, attrs);
        return dst;
    }

    private void ShowAbout()
    {
        using var dlg = new AboutForm(AppVersion);
        dlg.ShowDialog(this);
    }

    private void OnOpenDisk(object? sender, EventArgs e)
    {
        using var dlg = new OpenFileDialog
        {
            Filter = "Disk/tape images (*.d64, *.t64, *.rp9)|*.d64;*.t64;*.rp9|" +
                     "D64 disk images (*.d64)|*.d64|" +
                     "T64 tape images (*.t64)|*.t64|" +
                     "RP9 packages (*.rp9)|*.rp9|" +
                     "All files (*.*)|*.*",
            Title = "Open Disk Image",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            // .rp9 is a ZIP of media images: extract and use the manifest's media.
            string mediaPath = Path.GetExtension(dlg.FileName)
                    .Equals(".rp9", StringComparison.OrdinalIgnoreCase)
                ? Rp9.ExtractMediaPath(dlg.FileName)
                : dlg.FileName;
            string mediaExt = Path.GetExtension(mediaPath);
            if (mediaExt.Equals(".t64", StringComparison.OrdinalIgnoreCase))
            {
                var t64 = new C64.Core.Disk.T64Image(File.ReadAllBytes(mediaPath));
                _emulator.HleDrive.MountTape(t64);
            }
            else if (mediaExt.Equals(".d64", StringComparison.OrdinalIgnoreCase))
            {
                var d64 = new C64.Core.Disk.D64Image(File.ReadAllBytes(mediaPath));
                // Mount to the HLE drive (used for LOAD via Kernal trap).
                _emulator.HleDrive.MountDisk(d64);
                // Also mount to the DOS drive if present (for authenticity).
                _emulator.Bus.Drive?.MountDisk(d64);
            }
            else
            {
                throw new InvalidOperationException(
                    $"Unsupported file type '{mediaExt}'.");
            }
            string name = Path.GetFileName(dlg.FileName);
            _diskName = name;
            UpdateTitle();
            _diskStatus.Text = $"Disk: {name}";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not mount disk:\n{ex.Message}",
                "C64-Sharp", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // ---- keyboard: Windows keys -> C64 matrix ----

    private void UpdateTitle()
    {
        Text = string.IsNullOrEmpty(_diskName)
            ? $"C64-Sharp — {AppVersion}"
            : $"C64-Sharp — {AppVersion} — {_diskName}";
    }

    private void UpdateHookCount()
    {
        // Disk name only (debug hook counter removed).
    }

    private long _fpsLastTime;
    private void UpdateFps()
    {
        // Show the actual emulation speed (frames emulated per second),
        // not the UI refresh rate. 50.0 = full PAL speed.
        long now = Environment.TickCount64;
        if (now - _fpsLastTime >= 1000)
        {
            double emuFps = _emulator.EmulationFps;
            _diskStatus.Text = $"Disk: {_diskName} ({emuFps:F1} fps)";
            _fpsLastTime = now;
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_joystickEnabled && HandleJoystickKey(e.KeyValue, true))
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }
        // Settings-owned special keys (RUN/STOP, RESTORE, CLR/HOME, COMMODORE, CTRL, F1-F8).
        if (HandleSpecialKey(e.KeyValue, true))
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }
        foreach (var (col, row) in MapKey(e, true))
            _emulator.Bus.Keyboard.SetKey(col, row, true);
        e.Handled = true;
        e.SuppressKeyPress = true;
    }

    private void OnKeyUp(object? sender, KeyEventArgs e)
    {
        if (_joystickEnabled && HandleJoystickKey(e.KeyValue, false))
            return;
        if (HandleSpecialKey(e.KeyValue, false))
            return;
        foreach (var (col, row) in MapKey(e, false))
            _emulator.Bus.Keyboard.SetKey(col, row, false);
    }

    /// <summary>Handle the user-configurable special keys. Returns true if handled.</summary>
    private bool HandleSpecialKey(int keyValue, bool down)
    {
        var kb = _settings.Keyboard;
        // RESTORE is not a matrix key on real hardware: it pulses the NMI line.
        if (keyValue == kb.Restore)
        {
            if (down) _emulator.PressRestore();
            return true;
        }
        string? name = null;
        bool shift = false;
        if (keyValue == kb.RunStop) name = "RUNSTOP";
        else if (keyValue == kb.ClrHome) name = "CLRHOME";
        else if (keyValue == kb.Commodore) name = "COMMODORE";
        else if (keyValue == kb.Ctrl) name = "CTRL";
        else
        {
            int[] f = { kb.F1, kb.F2, kb.F3, kb.F4, kb.F5, kb.F6, kb.F7, kb.F8 };
            for (int i = 0; i < 8; i++)
            {
                if (keyValue != f[i]) continue;
                if ((i & 1) == 1) { name = "F" + i; shift = true; } // F2 -> Shift+F1
                else name = "F" + (i + 1);
                break;
            }
        }
        if (name == null) return false;
        var matrix = _emulator.Bus.Keyboard;
        if (shift)
        {
            var (sc, sr) = KeyboardMatrix.Key("LSHIFT");
            matrix.SetKey(sc, sr, down);
        }
        var (c, r) = KeyboardMatrix.Key(name);
        matrix.SetKey(c, r, down);
        return true;
    }

    private void OpenSettings()
    {
        using var dlg = new SettingsForm(_settings);
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            _settings = dlg.Result;
            try { _settings.Save(); } catch { }
            _joystickEnabled = _settings.Joystick.KeyboardEnabled;
            if (!_joystickEnabled)
            {
                _joyBits = 0xFF;
                _emulator.SetJoystick(0xFF);
            }
            _soundEnabled = _settings.Audio.Enabled;
            _emulator.SetAudioEnabled(_soundEnabled);
            ApplyJoystickDevice();
        }
    }

    /// <summary>Find the configured USB joystick device (winmm or HID).</summary>
    private void ApplyJoystickDevice()
    {
        _usbDevice = null;
        if (!_settings.Joystick.Enabled) return;
        try
        {
            var devices = new List<JoystickInput.DeviceInfo>();
            try { devices.AddRange(JoystickInput.GetDevices()); } catch { }
            try { devices.AddRange(HidJoystick.GetDevices()); } catch { }
            if (devices.Count == 0) return;
            string want = _settings.Joystick.DeviceName;
            string wantSrc = _settings.Joystick.DeviceSource;
            _usbDevice = devices.Find(d => d.Name == want && d.Source == wantSrc)
                      ?? devices.Find(d => d.Name == want)
                      ?? devices[0];
        }
        catch { }
    }

    /// <summary>Poll the USB joystick each UI tick (takes precedence over numpad).</summary>
    private void PollUsbJoystick()
    {
        if (!_settings.Joystick.Enabled) return;
        if (_usbDevice == null) { ApplyJoystickDevice(); return; }
        try
        {
            JoystickInput.State? st = null;
            bool ok = _usbDevice.Source == "hid"
                ? HidJoystick.TryGetState(_usbDevice.Path, out st)
                : JoystickInput.TryGetState(_usbDevice.Id, out st);
            if (ok && st != null)
                _emulator.SetJoystick(JoystickInput.ToC64Byte(st, _settings.Joystick));
            else
                _usbDevice = null; // device lost; re-enumerate next tick
        }
        catch { }
    }

    /// <summary>Map numpad keys to joystick Port 2 when enabled. Returns true if handled.</summary>
    private bool HandleJoystickKey(int keyValue, bool down)
    {
        // Numpad: 8=Up, 2=Down, 4=Left, 6=Right, 0=Fire, 7/9/1/3=diagonals
        int bit = keyValue switch
        {
            0x68 => 0, // NumPad8 Up
            0x62 => 1, // NumPad2 Down
            0x64 => 2, // NumPad4 Left
            0x66 => 3, // NumPad6 Right
            0x60 => 4, // NumPad0 Fire
            0x67 => -2, // NumPad7 Up+Left
            0x69 => -3, // NumPad9 Up+Right
            0x61 => -4, // NumPad1 Down+Left
            0x63 => -5, // NumPad3 Down+Right
            _ => -1,
        };
        if (bit == -1) return false;
        if (bit >= 0)
        {
            if (down) _joyBits &= (byte)~(1 << bit);
            else _joyBits |= (byte)(1 << bit);
        }
        else
        {
            // Diagonals: clear/set two bits
            (int b1, int b2) = bit switch
            {
                -2 => (0, 2), // Up+Left
                -3 => (0, 3), // Up+Right
                -4 => (1, 2), // Down+Left
                _ => (1, 3),  // Down+Right
            };
            if (down) _joyBits &= (byte)~((1 << b1) | (1 << b2));
            else _joyBits |= (byte)((1 << b1) | (1 << b2));
        }
        _emulator.SetJoystick(_joyBits);
        return true;
    }

    /// <summary>Map a Windows key event to C64 matrix positions.</summary>
    private static List<(int Col, int Row)> MapKey(KeyEventArgs e, bool down)
    {
        var result = new List<(int, int)>();
        // Shift is handled as a C64 key, not a modifier.
        // But for symbols that are dedicated C64 keys (not shifted digits),
        // map Windows Shift+digit to the C64 symbol key directly.
        string? name = e.KeyValue switch
        {
            // RUN/STOP, RESTORE, CLR/HOME, COMMODORE, CTRL, F1-F8 are owned by
            // the settings dialog (HandleSpecialKey) and intentionally absent here.
            0x20 => "SPACE",   // VK_SPACE
            0x0D => "RETURN",  // VK_RETURN
            0x08 => "DEL",     // VK_BACK
            0x2E => "DEL",     // VK_DELETE
            0x25 => "CRSRLR",  // VK_LEFT -> Cursor Left/Right (Shift for Left)
            0x26 => "CRSRUD",  // VK_UP -> Cursor Up/Down (Shift for Up)
            0x27 => "CRSRLR",  // VK_RIGHT -> Cursor Left/Right
            0x28 => "CRSRUD",  // VK_DOWN -> Cursor Up/Down
            >= 0x41 and <= 0x5A => ((char)e.KeyValue).ToString(), // A-Z
            >= 0x60 and <= 0x69 => ((char)('0' + e.KeyValue - 0x60)).ToString(), // Numpad 0-9
            0x6B => "+",       // VK_ADD
            0x6D => "-",       // VK_SUBTRACT
            0x6F => "/",       // VK_DIVIDE
            0x6A => "*",       // VK_MULTIPLY
            0x6E => ".",       // VK_DECIMAL
            0xBB => "+",       // VK_OEM_PLUS
            0xBD => "-",       // VK_OEM_MINUS
            0xBF => "/",       // VK_OEM_QUESTION
            0xBE => ".",       // VK_OEM_PERIOD
            0xBC => ",",       // VK_OEM_COMMA
            0xBA => ";",       // VK_OEM_1
            0xDE => "'",       // VK_OEM_7
            0xDB => "@",       // VK_OEM_4 (approximate)
            _ => null,
        };

        // Handle special keys that need Shift on the C64.
        bool needC64Shift = false;
        if (e.KeyValue == 0x25) needC64Shift = true; // VK_LEFT -> Shift+CRSR for Left
        else if (e.KeyValue == 0x26) needC64Shift = true; // VK_UP -> Shift+CRSR for Up

        if (needC64Shift)
            result.Add(KeyboardMatrix.Key("LSHIFT"));

        // Handle digits: if Shift is held, map to the symbol (C64 has dedicated keys).
        if (name == null && e.KeyValue >= 0x30 && e.KeyValue <= 0x39)
        {
            char digit = (char)e.KeyValue;
            if (e.Shift)
            {
                name = digit switch
                {
                    '8' => "*",
                    _ => digit.ToString(),
                };
            }
            else
            {
                name = digit.ToString();
            }
        }

        // Add LSHIFT if Shift is held and we didn't map to a dedicated symbol key.
        if (e.Shift && name != "*" && !needC64Shift)
            result.Add(KeyboardMatrix.Key("LSHIFT"));

        if (name != null)
        {
            try { result.Add(KeyboardMatrix.Key(name)); }
            catch (ArgumentException) { }
        }
        return result;
    }

    protected override bool IsInputKey(Keys keyData)
    {
        // Ensure arrow keys reach KeyDown/KeyUp instead of being used for navigation.
        if (keyData == Keys.Left || keyData == Keys.Right ||
            keyData == Keys.Up || keyData == Keys.Down)
            return true;
        return base.IsInputKey(keyData);
    }

    protected override void WndProc(ref Message m)
    {
        // Route raw HID input to the HID joystick parser.
        if (m.Msg == HidJoystick.WmInputMessageId)
        {
            try { HidJoystick.OnRawInput(m.LParam); } catch { }
        }
        base.WndProc(ref m);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _uiTimer.Stop();
        _emulator.Stop();
        _audio?.Dispose();
        base.OnFormClosed(e);
    }
}
