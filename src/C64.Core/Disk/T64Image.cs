using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace C64.Core.Disk;

/// <summary>
/// Parser for .t64 tape images (C64 tape image format, e.g. inside .rp9 files).
/// Layout: 64-byte header, then N 32-byte directory entries
/// (16 bytes metadata + 16 bytes filename), then the raw file data.
/// Unlike PRG files on disk, the stored data does NOT include the 2-byte
/// load address prefix; the load address comes from the directory entry,
/// and the data length is (EndAddress - StartAddress).
/// </summary>
public sealed class T64Image
{
    public sealed class Entry
    {
        /// <summary>CBM file type (low 3 bits of the type byte): 1=SEQ, 2=PRG, 3=USR.</summary>
        public byte FileType;
        public ushort StartAddress;
        /// <summary>Raw file bytes, without any load-address header.</summary>
        public byte[] Data = Array.Empty<byte>();
        /// <summary>Filename, trailing padding removed.</summary>
        public string Name = "";
    }

    /// <summary>User description from the tape header (may be empty).</summary>
    public string Description { get; }
    public IReadOnlyList<Entry> Entries { get; }

    public T64Image(byte[] data)
    {
        if (data == null) throw new ArgumentNullException(nameof(data));
        if (data.Length < 64) throw new InvalidDataException("T64 too small for header.");
        string sig = Encoding.ASCII.GetString(data, 0, 19);
        if (sig != "C64 tape image file")
            throw new InvalidDataException("Not a T64 tape image (bad signature).");

        Description = Encoding.ASCII.GetString(data, 0x28, 24).TrimEnd('\0', ' ');

        int numEntries = data[0x22] | (data[0x23] << 8);
        if (numEntries < 0 || numEntries > 1024)
            throw new InvalidDataException($"T64 bad entry count: {numEntries}.");
        if (data.Length < 64 + numEntries * 32)
            throw new InvalidDataException("T64 truncated directory.");

        var entries = new List<Entry>();
        for (int i = 0; i < numEntries; i++)
        {
            int base_ = 64 + i * 32;
            byte entryType = data[base_];
            if (entryType == 0) continue; // free slot

            byte fileType = (byte)(data[base_ + 1] & 0x07);
            ushort start = (ushort)(data[base_ + 2] | (data[base_ + 3] << 8));
            ushort end = (ushort)(data[base_ + 4] | (data[base_ + 5] << 8));
            int offset = data[base_ + 8] | (data[base_ + 9] << 8)
                       | (data[base_ + 10] << 16) | (data[base_ + 11] << 24);
            int length = end - start;
            if (length < 0 || offset < 0 || offset + length > data.Length)
                throw new InvalidDataException($"T64 entry {i} has bad offset/length.");

            var fileData = new byte[length];
            Array.Copy(data, offset, fileData, 0, length);

            string name = Encoding.ASCII.GetString(data, base_ + 16, 16).TrimEnd('\0', ' ');
            entries.Add(new Entry
            {
                FileType = fileType,
                StartAddress = start,
                Data = fileData,
                Name = name,
            });
        }
        Entries = entries;
    }
}
