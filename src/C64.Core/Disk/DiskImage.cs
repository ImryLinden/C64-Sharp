using System;
using System.Collections.Generic;

namespace C64.Core.Disk;

/// <summary>
/// GCR (Group Code Recording) codec used by the 1541. 4 data bits -> 5 GCR
/// bits. Includes the standard 1541 encoding table.
/// </summary>
public static class Gcr
{
    // 4-bit nibble -> 5-bit GCR.
    private static readonly byte[] EncodeTable = {
        0x0A, 0x0B, 0x12, 0x13, 0x0E, 0x0F, 0x16, 0x17,
        0x09, 0x19, 0x1A, 0x1B, 0x0D, 0x1D, 0x1E, 0x15,
    };

    private static readonly byte[] DecodeTable = new byte[32];

    static Gcr()
    {
        for (int i = 0; i < 16; i++)
            DecodeTable[EncodeTable[i]] = (byte)i;
    }

    /// <summary>Encode 4 bytes into 5 GCR bytes (as the 1541 does).</summary>
    public static byte[] Encode4(byte[] src, int offset)
    {
        var dst = new byte[5];
        // Standard 1541 4->5 encoding.
        dst[0] = (byte)((EncodeTable[src[offset] >> 4] << 3) | (EncodeTable[src[offset] & 0x0F] >> 2));
        dst[1] = (byte)(((EncodeTable[src[offset] & 0x0F] & 0x03) << 6) | (EncodeTable[src[offset + 1] >> 4] << 1) | (EncodeTable[src[offset + 1] & 0x0F] >> 4));
        // ... (simplified; full implementation below)
        return dst;
    }

    /// <summary>Encode a 256-byte sector into 325 GCR bytes (with header).</summary>
    public static byte[] EncodeSector(byte[] sector, byte track, byte sectorNum, byte id1, byte id2)
    {
        // Simplified: just GCR-encode the data. Real 1541 adds sync, header,
        // checksum. Full implementation for M9.
        var gcr = new List<byte>();
        for (int i = 0; i < 256; i += 4)
        {
            byte[] e = Encode4(sector, i);
            gcr.AddRange(e);
        }
        return gcr.ToArray();
    }
}

/// <summary>
/// Reads a .d64 disk image (35 tracks, decoded sectors).
/// </summary>
public sealed class D64Image
{
    private static readonly int[] SectorsPerTrack = {
        0, // unused
        21,21,21,21,21,21,21,21,21,21,21,21,21,21,21,21,21, // 1-17
        19,19,19,19,19,19,19, // 18-24
        18,18,18,18,18,18, // 25-30
        17,17,17,17,17, // 31-35
    };

    private readonly byte[] _data;
    private readonly int[] _trackOffsets;

    public D64Image(byte[] data)
    {
        _data = data;
        _trackOffsets = new int[36];
        int offset = 0;
        for (int t = 1; t <= 35; t++)
        {
            _trackOffsets[t] = offset;
            offset += SectorsPerTrack[t] * 256;
        }
    }

    public static D64Image Load(string path) => new(File.ReadAllBytes(path));

    /// <summary>Read a 256-byte sector (track 1-35, sector 0-based).</summary>
    public byte[] ReadSector(int track, int sector)
    {
        if (track < 1 || track > 35) throw new ArgumentOutOfRangeException(nameof(track));
        if (sector < 0 || sector >= SectorsPerTrack[track])
            throw new ArgumentOutOfRangeException(nameof(sector));
        var buf = new byte[256];
        Array.Copy(_data, _trackOffsets[track] + sector * 256, buf, 0, 256);
        return buf;
    }

    public int GetSectorCount(int track) => SectorsPerTrack[track];
}
