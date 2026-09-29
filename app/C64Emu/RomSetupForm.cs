namespace C64Emu;

/// <summary>
/// Plain host window shown when ROMs are missing at startup.
/// Opens the Settings dialog on the ROMs tab, then the missing-ROM message.
/// </summary>
internal sealed class RomSetupForm : Form
{
    private AppSettings _settings;
    private readonly string _missing;

    public RomSetupForm(AppSettings settings, string missing)
    {
        _settings = settings;
        _missing = missing;
        Text = "C64 Emulator";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(640, 480);
        // Intentionally the default window background color.
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        using (var dlg = new SettingsForm(_settings, initialTab: 2)) // ROMs tab
        {
            // Show the missing-ROM message on top of the settings dialog.
            dlg.Shown += (_, _) =>
            {
                using (var msg = new RomMissingDialog(_missing))
                    msg.ShowDialog(dlg);
            };
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                _settings = dlg.Result;
                try { _settings.Save(); } catch { }
                MessageBox.Show(this,
                    "Settings saved. Please restart the emulator.",
                    "C64 Emulator",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
        Close();
    }
}
