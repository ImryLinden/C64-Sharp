using System.IO.Compression;
using System.Xml.Linq;

namespace C64Sharp;

/// <summary>
/// Support for .rp9 files (RetroPlatform packages, e.g. from C64 Forever).
/// An .rp9 is a plain ZIP (deflate) containing an rp9-manifest.xml plus media
/// images. We extract it under temp\rp9\ and pick the media file the manifest
/// points at (a &lt;tape&gt; or &lt;disk&gt; entry), falling back to the first
/// supported image (.d64, then .t64) when there is no usable manifest.
/// </summary>
internal static class Rp9
{
    private static readonly string[] SupportedExtensions = { ".d64", ".t64" };

    private static bool IsSupported(string path)
    {
        string ext = Path.GetExtension(path);
        foreach (var s in SupportedExtensions)
            if (ext.Equals(s, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// Extract the .rp9 archive and return the full path of the media image
    /// to mount (.d64 or .t64). Throws if the file is not a ZIP or contains
    /// no supported media.
    /// </summary>
    public static string ExtractMediaPath(string rp9Path)
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "temp", "rp9",
            Path.GetFileNameWithoutExtension(rp9Path));
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        Directory.CreateDirectory(dir);
        // Throws on invalid ZIP, and on entries that would escape dir (zip-slip).
        ZipFile.ExtractToDirectory(rp9Path, dir);

        string? fromManifest = PickFromManifest(dir);
        if (fromManifest != null) return fromManifest;

        // Fallback: first supported image, .d64 preferred over .t64.
        foreach (var pattern in new[] { "*.d64", "*.t64" })
        {
            string[] files = Directory.GetFiles(dir, pattern, SearchOption.AllDirectories);
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            if (files.Length > 0) return files[0];
        }
        throw new InvalidOperationException(
            "No supported media (.d64/.t64) found inside the .rp9 file.");
    }

    /// <summary>
    /// Read rp9-manifest.xml and return the highest-priority &lt;tape&gt; or
    /// &lt;disk&gt; media file that exists on disk, or null.
    /// </summary>
    private static string? PickFromManifest(string dir)
    {
        string manifest = Path.Combine(dir, "rp9-manifest.xml");
        if (!File.Exists(manifest)) return null;
        try
        {
            var doc = XDocument.Load(manifest);
            var media = doc.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "media");
            if (media == null) return null;
            var candidates = media.Elements()
                .Where(e => e.Name.LocalName == "tape" || e.Name.LocalName == "disk")
                .Select(e => new
                {
                    File = e.Value.Trim(),
                    Priority = int.TryParse(e.Attribute("priority")?.Value, out int p) ? p : 999,
                })
                .OrderBy(c => c.Priority)
                .ToList();
            foreach (var c in candidates)
            {
                if (string.IsNullOrEmpty(c.File) || !IsSupported(c.File)) continue;
                string direct = Path.Combine(dir, c.File);
                if (File.Exists(direct)) return direct;
                // Case-insensitive fallback (archives don't always match case).
                string? match = Directory
                    .GetFiles(dir, "*", SearchOption.AllDirectories)
                    .FirstOrDefault(f => f.Substring(dir.Length).TrimStart(Path.DirectorySeparatorChar)
                        .Equals(c.File.Replace('/', Path.DirectorySeparatorChar),
                            StringComparison.OrdinalIgnoreCase));
                if (match != null) return match;
            }
        }
        catch { }
        return null;
    }
}
