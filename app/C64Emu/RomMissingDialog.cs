namespace C64Emu;

/// <summary>Shown at startup when a required C64 ROM file cannot be found.</summary>
internal sealed class RomMissingDialog : Form
{
    private const string RomUrl = "https://www.c64forever.com/";

    public RomMissingDialog(string which)
    {
        Text = "C64 Emulator — ROMs missing";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(460, 240);

        var msg = new Label
        {
            Left = 12, Top = 12, Width = 436, Height = 64,
            Text = $"The {which} ROM file was not found.\n\n" +
                   "This emulator needs the original Commodore 64 ROMs " +
                   "(BASIC, Kernal, Character) to run. You can obtain a legal copy at:",
        };
        var link = new LinkLabel
        {
            Left = 12, Top = 80, Width = 436,
            Text = RomUrl,
        };
        link.LinkClicked += (_, _) =>
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(RomUrl)
                {
                    UseShellExecute = true,
                });
            }
            catch { }
        };
        var msg2 = new Label
        {
            Left = 12, Top = 108, Width = 436, Height = 64,
            Text = "Place the ROM files in a .roms folder next to the emulator, " +
                   "or set custom locations in Options > Settings (ROMs tab).",
        };
        var ok = new Button
        {
            Text = "OK", DialogResult = DialogResult.OK,
            Left = 372, Top = 192, Width = 76,
        };

        Controls.Add(msg);
        Controls.Add(link);
        Controls.Add(msg2);
        Controls.Add(ok);
        AcceptButton = ok;
    }
}
