namespace C64Sharp;

/// <summary>
/// Live joystick test dialog: shows the raw state of the selected device
/// (axes, POV, pressed buttons) and what the C64 sees on Port 2 with the
/// current mappings. Useful to find which physical button is "Button N".
/// </summary>
internal sealed class JoystickTestForm : Form
{
    private readonly JoystickInput.DeviceInfo _device;
    private readonly JoystickSettings _cfg;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly Label _rawLine;
    private readonly Label _buttonsLine;
    private readonly Label _c64Line;
    private readonly Label _dirsLine;
    private readonly Label _mapLine;

    public JoystickTestForm(JoystickInput.DeviceInfo device, JoystickSettings cfg)
    {
        _device = device;
        _cfg = cfg;
        Text = $"Joystick test — {device.Name}";
        Width = 460;
        Height = 300;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;

        int top = 12;
        Label Add(string text, int height)
        {
            var l = new Label { Text = text, Left = 12, Top = top, Width = 420, Height = height };
            Controls.Add(l);
            top += height + 8;
            return l;
        }

        Add("Move the stick and press buttons. Active items turn the C64 lamps on.", 30);
        _rawLine = Add("X: -  Y: -  POV: -", 20);
        _buttonsLine = Add("Buttons pressed: none", 40);
        _c64Line = Add("C64 Port 2 ($DC00): -", 20);
        _dirsLine = Add("", 20);
        _mapLine = Add($"Mappings — Up:{cfg.Up} Down:{cfg.Down} Left:{cfg.Left} Right:{cfg.Right} Fire:{cfg.Fire}", 40);

        var close = new Button { Text = "Close", Left = 370, Top = top + 4, Width = 60, DialogResult = DialogResult.OK };
        Controls.Add(close);
        AcceptButton = close;

        _timer = new System.Windows.Forms.Timer { Interval = 50 };
        _timer.Tick += (_, _) => Poll();
        _timer.Start();
        FormClosed += (_, _) => _timer.Stop();
        Poll();
    }

    private void Poll()
    {
        JoystickInput.State? st = null;
        bool ok;
        try
        {
            ok = _device.Source == "hid"
                ? HidJoystick.TryGetState(_device.Path, out st)
                : JoystickInput.TryGetState(_device.Id, out st);
        }
        catch { ok = false; }

        if (!ok || st == null)
        {
            _rawLine.Text = "X: -  Y: -  POV: -";
            _buttonsLine.Text = "No data yet — move the stick or press a button.";
            _c64Line.Text = "C64 Port 2 ($DC00): -";
            _dirsLine.Text = "";
            return;
        }

        string pov = st.Pov < 0 ? "centered" : (st.Pov / 100.0).ToString("0") + "°";
        _rawLine.Text = $"X: {st.X}  Y: {st.Y}  POV: {pov}";

        var pressed = new List<int>();
        for (int i = 1; i <= 32; i++)
            if ((st.Buttons & (1 << (i - 1))) != 0) pressed.Add(i);
        _buttonsLine.Text = "Buttons pressed: " +
            (pressed.Count == 0 ? "none" : string.Join(", ", pressed));

        byte c64 = JoystickInput.ToC64Byte(st, _cfg);
        _c64Line.Text = $"C64 Port 2 ($DC00): {c64}  (bits 0-3 dirs, bit 4 fire; 0 = pressed)";
        string Lamp(string name, int bit) => ((c64 & (1 << bit)) == 0 ? "[■]" : "[ ]") + " " + name;
        _dirsLine.Text = string.Join("   ",
            Lamp("Up", 0), Lamp("Down", 1), Lamp("Left", 2), Lamp("Right", 3), Lamp("Fire", 4));
    }
}
