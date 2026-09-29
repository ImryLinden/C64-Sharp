using System;

namespace C64.Core.Disk;

/// <summary>
/// GCR (Group Code Recording) codec for the 1541. 4 data bits -> 5 GCR bits.
/// </summary>
public static class GcrCodec
{
    // 4-bit nibble -> 5-bit GCR code.
    private static readonly byte[] EncodeTable = {
        0x0A, 0x0B, 0x12, 0x13, // 0-3
        0x0E, 0x0F, 0x16, 0x17, // 4-7
        0x09, 0x19, 0x1A, 0x1B, // 8-11
        0x0D, 0x1D, 0x1E, 0x15, // 12-15
    };

    private static readonly byte[] DecodeTable = new byte[32];

    static GcrCodec()
    {
        for (int i = 0; i < 16; i++)
            DecodeTable[EncodeTable[i]] = (byte)i;
    }

    /// <summary>
    /// Encode 4 data bytes into 5 GCR bytes (the 1541's standard packing).
    /// </summary>
    public static void Encode4(byte[] src, int srcOffset, byte[] dst, int dstOffset)
    {
        byte b0 = src[srcOffset], b1 = src[srcOffset + 1];
        byte b2 = src[srcOffset + 2], b3 = src[srcOffset + 3];

        // 8 nibbles * 5 bits = 40 bits, needs ulong.
        ulong gcr = 0;
        gcr = (gcr << 5) | EncodeTable[b0 >> 4];
        gcr = (gcr << 5) | EncodeTable[b0 & 0x0F];
        gcr = (gcr << 5) | EncodeTable[b1 >> 4];
        gcr = (gcr << 5) | EncodeTable[b1 & 0x0F];
        gcr = (gcr << 5) | EncodeTable[b2 >> 4];
        gcr = (gcr << 5) | EncodeTable[b2 & 0x0F];
        gcr = (gcr << 5) | EncodeTable[b3 >> 4];
        gcr = (gcr << 5) | EncodeTable[b3 & 0x0F];

        dst[dstOffset] = (byte)(gcr >> 32);
        dst[dstOffset + 1] = (byte)(gcr >> 24);
        dst[dstOffset + 2] = (byte)(gcr >> 16);
        dst[dstOffset + 3] = (byte)(gcr >> 8);
        dst[dstOffset + 4] = (byte)gcr;
    }

    /// <summary>
    /// Decode 5 GCR bytes back into 4 data bytes.
    /// </summary>
    public static void Decode5(byte[] src, int srcOffset, byte[] dst, int dstOffset)
    {
        ulong gcr = ((ulong)src[srcOffset] << 32) | ((ulong)src[srcOffset + 1] << 24) |
                    ((ulong)src[srcOffset + 2] << 16) | ((ulong)src[srcOffset + 3] << 8) |
                    src[srcOffset + 4];
        byte[] nibbles = new byte[8];
        for (int i = 7; i >= 0; i--)
        {
            nibbles[i] = DecodeTable[gcr & 0x1F];
            gcr >>= 5;
        }
        dst[dstOffset] = (byte)((nibbles[0] << 4) | nibbles[1]);
        dst[dstOffset + 1] = (byte)((nibbles[2] << 4) | nibbles[3]);
        dst[dstOffset + 2] = (byte)((nibbles[4] << 4) | nibbles[5]);
        dst[dstOffset + 3] = (byte)((nibbles[6] << 4) | nibbles[7]);
    }

    /// <summary>Encode a 256-byte sector into 325 GCR bytes.</summary>
    public static byte[] EncodeSector(byte[] sector)
    {
        if (sector.Length != 256) throw new ArgumentException("Sector must be 256 bytes.");
        var gcr = new byte[325];
        for (int i = 0; i < 64; i++)
            Encode4(sector, i * 4, gcr, i * 5);
        // Last byte: checksum? Actually 256/4=64 groups -> 64*5=320 bytes.
        // The 1541 uses 325 GCR bytes (includes header/checksum in the block).
        // For now, return 320; the track builder adds framing.
        var result = new byte[320];
        Array.Copy(gcr, result, 320);
        return result;
    }
}
