namespace C64Sharp;

/// <summary>About dialog with a clickable link to the GitHub project.</summary>
internal sealed class AboutForm : Form
{
    private const string RepoUrl = "https://github.com/ImryLinden/C64-Sharp";

    public AboutForm(string version)
    {
        Text = "About C64 Emulator";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(370, 195);

        var title = new Label
        {
            Text = $"C64 Emulator — {version}",
            Font = new Font(FontFamily.GenericSansSerif, 11, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(12, 12),
        };
        var desc = new Label
        {
            Text = "A Commodore 64 emulator in C# (.NET 8).\nCPU: 6510 | VIC-II | SID | CIA x2 | 1541 (HLE)",
            AutoSize = true,
            Location = new Point(12, 42),
        };
        var by = new Label
        {
            Text = "By Imry Linden and Muse.",
            AutoSize = true,
            Location = new Point(12, 86),
        };
        var link = new LinkLabel
        {
            Text = RepoUrl,
            AutoSize = true,
            Location = new Point(12, 110),
        };
        link.LinkClicked += (_, _) =>
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(RepoUrl)
                {
                    UseShellExecute = true,
                });
            }
            catch { }
        };
        var ok = new Button
        {
            Text = "OK",
            DialogResult = DialogResult.OK,
            Location = new Point(283, 157),
            Size = new Size(75, 25),
        };
        AcceptButton = ok;

        Controls.Add(title);
        Controls.Add(desc);
        Controls.Add(by);
        Controls.Add(link);
        Controls.Add(ok);
    }
}
