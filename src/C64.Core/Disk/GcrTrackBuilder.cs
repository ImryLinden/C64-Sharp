using System;
using System.Collections.Generic;

namespace C64.Core.Disk;

/// <summary>
/// Builds GCR-encoded tracks from a .d64 image, in the 1541's on-disk format.
/// </summary>
public sealed class GcrTrackBuilder
{
    private readonly D64Image _d64;
    private readonly byte _id1, _id2;

    public GcrTrackBuilder(D64Image d64, byte id1 = 0x41, byte id2 = 0x41)
    {
        _d64 = d64;
        _id1 = id1; _id2 = id2;
    }

    /// <summary>Build the GCR byte stream for a track (1-35).</summary>
    public byte[] BuildTrack(int track)
    {
        var bytes = new List<byte>();
        int sectors = _d64.GetSectorCount(track);

        for (int s = 0; s < sectors; s++)
        {
            // Sync.
            for (int i = 0; i < 5; i++) bytes.Add(0xFF);

            // Header block: $08, T, S, ID1, ID2, CHK, $0F, $0F (8 bytes -> 10 GCR).
            byte[] header = {
                0x08, (byte)track, (byte)s, _id1, _id2,
                (byte)(0x08 ^ track ^ s ^ _id1 ^ _id2), 0x0F, 0x0F
            };
            byte[] headerGcr = new byte[10];
            for (int i = 0; i < 2; i++)
                GcrCodec.Encode4(header, i * 4, headerGcr, i * 5);
            bytes.AddRange(headerGcr);

            // Gap.
            for (int i = 0; i < 9; i++) bytes.Add(0x55);

            // Sync.
            for (int i = 0; i < 5; i++) bytes.Add(0xFF);

            // Data block: $07 + 256 data + CHK + $0F + $0F? 
            // Actually: 260 bytes -> 325 GCR.
            byte[] sector = _d64.ReadSector(track, s);
            byte chk = 0;
            foreach (byte b in sector) chk ^= b;
            var dataBlock = new byte[260];
            dataBlock[0] = 0x07;
            Array.Copy(sector, 0, dataBlock, 1, 256);
            dataBlock[257] = chk;
            dataBlock[258] = 0x0F;
            dataBlock[259] = 0x0F;
            byte[] dataGcr = new byte[325];
            for (int i = 0; i < 65; i++)
                GcrCodec.Encode4(dataBlock, i * 4, dataGcr, i * 5);
            bytes.AddRange(dataGcr);

            // Gap to next sector.
            for (int i = 0; i < 8; i++) bytes.Add(0x55);
        }

        return bytes.ToArray();
    }
}
