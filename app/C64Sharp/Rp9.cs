using System.IO.Compression;

namespace C64Sharp;

/// <summary>
/// Support for .rp9 files (RetroPlatform packages, e.g. from C64 Forever).
/// An .rp9 is a plain ZIP (deflate) containing an rp9-manifest.xml plus media
/// images. We extract it under temp\rp9\ and mount the first .d64 found.
/// </summary>
internal static class Rp9
{
    /// <summary>
    /// Extract the .rp9 archive and return the raw bytes of the first .d64
    /// disk image inside (alphabetical order, so Disk1 comes before Disk2).
    /// Throws if the file is not a ZIP or contains no .d64 image.
    /// </summary>
    public static byte[] ExtractDiskBytes(string rp9Path)
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "temp", "rp9",
            Path.GetFileNameWithoutExtension(rp9Path));
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        Directory.CreateDirectory(dir);
        // Throws on invalid ZIP, and on entries that would escape dir (zip-slip).
        ZipFile.ExtractToDirectory(rp9Path, dir);
        string[] disks = Directory.GetFiles(dir, "*.d64", SearchOption.AllDirectories);
        Array.Sort(disks, StringComparer.OrdinalIgnoreCase);
        if (disks.Length == 0)
            throw new InvalidOperationException(
                "No .d64 disk image found inside the .rp9 file.");
        return File.ReadAllBytes(disks[0]);
    }
}
