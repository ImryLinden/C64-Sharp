namespace C64Sharp;

/// <summary>Settings dialog: Keyboard / Joystick / Audio / ROMs tabs.</summary>
public sealed class SettingsForm : Form
{
    private AppSettings _work;
    public AppSettings Result => _work;

    private sealed class KeyRow
    {
        public string C64Name = "";
        public Func<KeyboardSettings, int> Get = null!;
        public Action<KeyboardSettings, int> Set = null!;
        public Label Value = null!;
        public Button Change = null!;
    }

    private sealed class JoyRow
    {
        public string Label = "";
        public Func<JoystickSettings, string> Get = null!;
        public Action<JoystickSettings, string> Set = null!;
        public string[] Options = null!;
        public ComboBox Box = null!;
    }

    private sealed class RomRow
    {
        public string Id = "";
        public string Label = "";
        public TextBox Path = null!;
    }

    private readonly List<KeyRow> _keyRows = new();
    private readonly List<JoyRow> _joyRows = new();
    private readonly List<RomRow> _romRows = new();
    private readonly List<JoystickInput.DeviceInfo> _devices = new();
    private CheckBox _joyEnable = null!;
    private CheckBox _kbJoyEnable = null!;
    private CheckBox _audioEnable = null!;
    private ComboBox _joyDevice = null!;
    private int? _capturing;

    public SettingsForm(AppSettings settings, int initialTab = 0)
    {
        _work = settings.Clone();

        Text = "Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        KeyPreview = true;
        ClientSize = new Size(480, 520);

        DefineKeyRows();
        DefineJoyRows();
        DefineRomRows();
        // Joystick devices are enumerated by RefreshDeviceList() when the
        // Joystick tab is built (and again on Refresh button clicks).

        var tabs = new TabControl { Dock = DockStyle.Fill };
        var tabKeys = new TabPage("Keyboard");
        var tabJoy = new TabPage("Joystick");
        var tabAudio = new TabPage("Audio");
        var tabRoms = new TabPage("ROMs");
        tabs.TabPages.Add(tabKeys);
        tabs.TabPages.Add(tabJoy);
        tabs.TabPages.Add(tabAudio);
        tabs.TabPages.Add(tabRoms);
        tabs.SelectedIndex = Math.Clamp(initialTab, 0, tabs.TabCount - 1);
        Controls.Add(tabs);

        BuildKeyboardTab(tabKeys);
        BuildJoystickTab(tabJoy);
        BuildAudioTab(tabAudio);
        BuildRomsTab(tabRoms);

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 44 };
        var btnCancel = new Button
        {
            Text = "Cancel", DialogResult = DialogResult.Cancel,
            Width = 80, Left = 388, Top = 10,
        };
        var btnOk = new Button
        {
            Text = "OK", DialogResult = DialogResult.OK,
            Width = 80, Left = 300, Top = 10,
        };
        var btnDefaults = new Button
        {
            Text = "Restore Defaults", Width = 110, Left = 12, Top = 10,
        };
        btnDefaults.Click += (_, _) =>
        {
            _work = new AppSettings();
            RefreshAll();
        };
        bottom.Controls.Add(btnCancel);
        bottom.Controls.Add(btnOk);
        bottom.Controls.Add(btnDefaults);
        Controls.Add(bottom);
        AcceptButton = btnOk;
        CancelButton = btnCancel;
    }

    private void DefineKeyRows()
    {
        void Add(string name, Func<KeyboardSettings, int> get, Action<KeyboardSettings, int> set)
            => _keyRows.Add(new KeyRow { C64Name = name, Get = get, Set = set });
        Add("RUN/STOP", k => k.RunStop, (k, v) => k.RunStop = v);
        Add("RESTORE", k => k.Restore, (k, v) => k.Restore = v);
        Add("CLR/HOME", k => k.ClrHome, (k, v) => k.ClrHome = v);
        Add("COMMODORE", k => k.Commodore, (k, v) => k.Commodore = v);
        Add("CTRL", k => k.Ctrl, (k, v) => k.Ctrl = v);
        Add("F1", k => k.F1, (k, v) => k.F1 = v);
        Add("F2", k => k.F2, (k, v) => k.F2 = v);
        Add("F3", k => k.F3, (k, v) => k.F3 = v);
        Add("F4", k => k.F4, (k, v) => k.F4 = v);
        Add("F5", k => k.F5, (k, v) => k.F5 = v);
        Add("F6", k => k.F6, (k, v) => k.F6 = v);
        Add("F7", k => k.F7, (k, v) => k.F7 = v);
        Add("F8", k => k.F8, (k, v) => k.F8 = v);
    }

    private void DefineJoyRows()
    {
        string[] dirs = { "None", "POVUp", "POVDown", "POVLeft", "POVRight",
            "AxisX-", "AxisX+", "AxisY-", "AxisY+" };
        string[] fire = { "None", "Button1", "Button2", "Button3", "Button4",
            "Button5", "Button6", "Button7", "Button8", "Button9", "Button10",
            "Button11", "Button12", "Button13", "Button14", "Button15", "Button16" };
        void Add(string label, Func<JoystickSettings, string> get,
            Action<JoystickSettings, string> set, string[] options)
            => _joyRows.Add(new JoyRow { Label = label, Get = get, Set = set, Options = options });
        Add("Up", j => j.Up, (j, v) => j.Up = v, dirs);
        Add("Down", j => j.Down, (j, v) => j.Down = v, dirs);
        Add("Left", j => j.Left, (j, v) => j.Left = v, dirs);
        Add("Right", j => j.Right, (j, v) => j.Right = v, dirs);
        Add("Fire", j => j.Fire, (j, v) => j.Fire = v, fire);
    }

    private void DefineRomRows()
    {
        _romRows.Add(new RomRow { Id = "basic", Label = "BASIC ROM" });
        _romRows.Add(new RomRow { Id = "kernal", Label = "Kernal ROM" });
        _romRows.Add(new RomRow { Id = "chargen", Label = "Character ROM" });
    }

    private static string GetRom(AppSettings s, string id) => id switch
    {
        "basic" => s.Roms.Basic,
        "kernal" => s.Roms.Kernal,
        _ => s.Roms.Chargen,
    };

    private static void SetRom(AppSettings s, string id, string value)
    {
        switch (id)
        {
            case "basic": s.Roms.Basic = value; break;
            case "kernal": s.Roms.Kernal = value; break;
            default: s.Roms.Chargen = value; break;
        }
    }

    public static string KeyName(int keyValue) => keyValue switch
    {
        0x11 => "Ctrl",
        _ => Enum.IsDefined(typeof(Keys), keyValue)
            ? ((Keys)keyValue).ToString()
            : $"0x{keyValue:X2}",
    };

    private void BuildKeyboardTab(TabPage tab)
    {
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = _keyRows.Count,
            Padding = new Padding(10),
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 95));
        for (int i = 0; i < _keyRows.Count; i++)
        {
            var row = _keyRows[i];
            int idx = i;
            table.Controls.Add(new Label
            {
                Text = row.C64Name, Anchor = AnchorStyles.Left, AutoSize = true,
            }, 0, i);
            row.Value = new Label
            {
                Text = KeyName(row.Get(_work.Keyboard)),
                Anchor = AnchorStyles.Left, AutoSize = true,
            };
            table.Controls.Add(row.Value, 1, i);
            row.Change = new Button { Text = "Change..." };
            row.Change.Click += (_, _) => StartCapture(idx);
            table.Controls.Add(row.Change, 2, i);
        }
        tab.Controls.Add(table);
    }

    private void StartCapture(int idx)
    {
        _capturing = idx;
        for (int i = 0; i < _keyRows.Count; i++)
            _keyRows[i].Change.Text = i == idx ? "Press a key..." : "Change...";
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (_capturing is int idx)
        {
            var row = _keyRows[idx];
            row.Set(_work.Keyboard, e.KeyValue);
            row.Value.Text = KeyName(e.KeyValue);
            row.Change.Text = "Change...";
            _capturing = null;
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }
        base.OnKeyDown(e);
    }

    private void BuildJoystickTab(TabPage tab)
    {
        var panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10) };
        _joyEnable = new CheckBox
        {
            Text = "Enable USB joystick",
            Checked = _work.Joystick.Enabled,
            Left = 10, Top = 10, Width = 300,
        };
        _joyEnable.CheckedChanged += (_, _) => _work.Joystick.Enabled = _joyEnable.Checked;
        panel.Controls.Add(_joyEnable);

        var devLabel = new Label { Text = "Device", Left = 10, Top = 42, Width = 80 };
        panel.Controls.Add(devLabel);
        _joyDevice = new ComboBox
        {
            Left = 100, Top = 38, Width = 250, DropDownStyle = ComboBoxStyle.DropDownList,
        };
        _joyDevice.SelectedIndexChanged += (_, _) =>
        {
            int i = _joyDevice.SelectedIndex;
            if (i >= 0 && i < _devices.Count)
                _work.Joystick.DeviceName = _devices[i].Name;
        };
        panel.Controls.Add(_joyDevice);
        var refreshBtn = new Button { Text = "Refresh", Left = 355, Top = 37, Width = 75 };
        refreshBtn.Click += (_, _) => RefreshDeviceList();
        panel.Controls.Add(refreshBtn);
        RefreshDeviceList();
        try { Emulator.DebugLog(JoystickInput.Diagnose()); } catch { }

        int top = 75;
        foreach (var row in _joyRows)
        {
            var lbl = new Label { Text = row.Label, Left = 10, Top = top + 4, Width = 80 };
            panel.Controls.Add(lbl);
            row.Box = new ComboBox
            {
                Left = 100, Top = top, Width = 200, DropDownStyle = ComboBoxStyle.DropDownList,
            };
            row.Box.Items.AddRange(row.Options);
            string cur = row.Get(_work.Joystick);
            int ci = Array.IndexOf(row.Options, cur);
            row.Box.SelectedIndex = ci >= 0 ? ci : 0;
            var captured = row;
            row.Box.SelectedIndexChanged += (_, _) =>
                captured.Set(_work.Joystick, (string)captured.Box.SelectedItem!);
            panel.Controls.Add(row.Box);
            top += 32;
        }

        _kbJoyEnable = new CheckBox
        {
            Text = "Use keyboard (numpad) as joystick",
            Checked = _work.Joystick.KeyboardEnabled,
            Left = 10, Top = top + 12, Width = 320,
        };
        _kbJoyEnable.CheckedChanged += (_, _) =>
            _work.Joystick.KeyboardEnabled = _kbJoyEnable.Checked;
        panel.Controls.Add(_kbJoyEnable);
        var kbNote = new Label
        {
            Text = "Numpad: 8/2/4/6 = directions, 0 = fire, 7/9/1/3 = diagonals.\n" +
                   "When a USB joystick is enabled it takes precedence.",
            Left = 10, Top = top + 38, Width = 440, Height = 40,
        };
        panel.Controls.Add(kbNote);
        tab.Controls.Add(panel);
    }

    private void RefreshDeviceList()
    {
        _devices.Clear();
        try { _devices.AddRange(JoystickInput.GetDevices()); } catch { }
        _joyDevice.Items.Clear();
        foreach (var d in _devices)
            _joyDevice.Items.Add(d.Name);
        if (_joyDevice.Items.Count == 0)
            _joyDevice.Items.Add("(no joystick found)");
        int sel = _devices.FindIndex(d => d.Name == _work.Joystick.DeviceName);
        _joyDevice.SelectedIndex = sel >= 0 ? sel : 0;
        try { Emulator.DebugLog(JoystickInput.Diagnose()); } catch { }
    }

    private void BuildAudioTab(TabPage tab)
    {
        var panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10) };
        _audioEnable = new CheckBox
        {
            Text = "Enable sound",
            Checked = _work.Audio.Enabled,
            Left = 10, Top = 10, Width = 300,
        };
        _audioEnable.CheckedChanged += (_, _) =>
            _work.Audio.Enabled = _audioEnable.Checked;
        panel.Controls.Add(_audioEnable);
        tab.Controls.Add(panel);
    }

    private void BuildRomsTab(TabPage tab)
    {
        var panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10) };
        var note = new Label
        {
            Text = "Defaults: .roms folder next to the exe. Changing ROMs requires a restart.",
            Left = 10, Top = 10, Width = 440, Height = 32,
        };
        panel.Controls.Add(note);
        int top = 48;
        foreach (var row in _romRows)
        {
            var lbl = new Label { Text = row.Label, Left = 10, Top = top + 4, Width = 100 };
            panel.Controls.Add(lbl);
            row.Path = new TextBox { Left = 115, Top = top, Width = 235 };
            row.Path.Text = GetRom(_work, row.Id);
            var captured = row;
            row.Path.TextChanged += (_, _) => SetRom(_work, captured.Id, captured.Path.Text);
            panel.Controls.Add(row.Path);
            var browse = new Button { Text = "Browse...", Left = 360, Top = top - 1, Width = 80 };
            browse.Click += (_, _) =>
            {
                using var dlg = new OpenFileDialog
                {
                    Filter = "ROM files (*.rom)|*.rom|All files (*.*)|*.*",
                    FileName = Path.GetFileName(captured.Path.Text),
                };
                string? dir = null;
                try { dir = Path.GetDirectoryName(captured.Path.Text); } catch { }
                if (dir != null && Directory.Exists(dir)) dlg.InitialDirectory = dir;
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    captured.Path.Text = dlg.FileName;
                    SetRom(_work, captured.Id, dlg.FileName);
                }
            };
            panel.Controls.Add(browse);
            top += 34;
        }
        tab.Controls.Add(panel);
    }

    private void RefreshAll()
    {
        for (int i = 0; i < _keyRows.Count; i++)
        {
            var row = _keyRows[i];
            row.Value.Text = KeyName(row.Get(_work.Keyboard));
            row.Change.Text = "Change...";
        }
        _capturing = null;
        _joyEnable.Checked = _work.Joystick.Enabled;
        _kbJoyEnable.Checked = _work.Joystick.KeyboardEnabled;
        _audioEnable.Checked = _work.Audio.Enabled;
        int sel = _devices.FindIndex(d => d.Name == _work.Joystick.DeviceName);
        _joyDevice.SelectedIndex = sel >= 0 ? sel : 0;
        foreach (var row in _joyRows)
        {
            int ci = Array.IndexOf(row.Options, row.Get(_work.Joystick));
            row.Box.SelectedIndex = ci >= 0 ? ci : 0;
        }
        foreach (var row in _romRows)
            row.Path.Text = GetRom(_work, row.Id);
    }
}
