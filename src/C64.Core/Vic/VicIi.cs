using System;

namespace C64.Core.Vic;

/// <summary>
/// The VIC-II video chip (MOS 6569, PAL), functional model.
///
/// Covers the registers the Kernal and BASIC touch, a PAL raster counter
/// (63 cycles/line, 312 lines) driven lazily from the CPU cycle count,
/// the raster-compare latch in $D019, VIC-banked memory, and a renderer
/// for all graphics modes: standard/multicolor text, standard/multicolor
/// bitmap, ECM, and 8 sprites.
///
/// What is NOT modeled (M9): cycle-exact bus arbitration, badline CPU
/// stalls, light pen. Sprite rendering is functional (no cycle-exact
/// DMA timing); collisions are pixel-based.
/// </summary>
public sealed class VicIi
{
    public const int CyclesPerLine = 63;
    public const int LinesPerFrame = 312;

    public const int FrameWidth = 384;
    public const int FrameHeight = 272;
    // Framebuffer shows dots 0-383 and raster lines 16-287.
    private const int FirstVisibleLine = 16;

    // Display window (dots / lines), from the VIC-II spec.
    // CSEL=1: 40 cols, X in [24,344). CSEL=0: 38 cols, X in [31,335).
    // RSEL=1: 25 rows, Y in [51,251). RSEL=0: 24 rows, Y in [55,247).
    // The graphics (always 40x25 cells) start at dot 24+XSCROLL and at the
    // first badline; RSEL/CSEL only move where the border starts/stops.

    private readonly byte[] _regs = new byte[64];
    private readonly byte[] _ram;      // 64KB main RAM (VIC sees raw RAM, unbanked)
    private readonly byte[] _charom;   // 4KB character ROM
    private readonly byte[] _colorRam; // 1KB of 4-bit color RAM
    private readonly Func<int> _getBank; // VIC bank 0-3 (from CIA2 $DD00)

    /// <summary>
    /// Provides the CPU cycle count the raster is derived from.
    /// Wired up by the machine composition root; defaults to 0.
    /// </summary>
    public Func<long> GetCpuCycles { get; set; } = () => 0;

    private int _line;          // current raster line, 0-311
    private long _lastCycles = -1;
    private bool _rasterLatch;  // $D019 bit 0 (IRST)
    private byte _spriteSpriteColl; // $D01E, latched until read
    private byte _spriteBgColl;     // $D01F, latched until read

    public VicIi(byte[] ram, byte[] charom, byte[] colorRam, Func<int> getBank)
    {
        _ram = ram ?? throw new ArgumentNullException(nameof(ram));
        _charom = charom ?? throw new ArgumentNullException(nameof(charom));
        _colorRam = colorRam ?? throw new ArgumentNullException(nameof(colorRam));
        _getBank = getBank ?? throw new ArgumentNullException(nameof(getBank));

        // Power-on defaults the Kernal relies on (it never writes these):
        // screen on, 25 rows; screen RAM $0400, charset $1000 (CHAROM);
        // light-blue border, blue background.
        _regs[0x11] = 0x1B;
        _regs[0x18] = 0x15;
        _regs[0x20] = 0x0E;
        _regs[0x21] = 0x06;
    }

    // ---- raster ----

    /// <summary>Current PAL raster line (0-311), derived from CPU cycles.</summary>
    public int RasterLine { get { Sync(); return _line; } }

    private void Sync()
    {
        long cycles = GetCpuCycles();
        if (cycles == _lastCycles) return;
        if (_lastCycles >= 0 && cycles > _lastCycles)
        {
            // Latch IRST if the raster entered the compare line at any
            // point since the last sync. Line k starts at cycle k*63.
            int compare = _regs[0x12] | ((_regs[0x11] & 0x80) << 1);
            long k = _lastCycles / CyclesPerLine + 1; // first line boundary after last sync
            long dk = (compare - k) % LinesPerFrame;
            if (dk < 0) dk += LinesPerFrame;
            if ((k + dk) * CyclesPerLine <= cycles) _rasterLatch = true;
        }
        _lastCycles = cycles;
        _line = (int)((cycles / CyclesPerLine) % LinesPerFrame);
    }

    // ---- registers ($D000-$D3FF, mirrored every 64 bytes) ----

    /// <summary>IRQ output: asserted when the raster latch is set and enabled.</summary>
    public bool IrqAsserted { get { Sync(); return _rasterLatch && (_regs[0x1A] & 0x01) != 0; } }

    public byte ReadRegister(int address)
    {
        int reg = address & 0x3F;
        if (reg > 0x2E) return 0xFF; // $D02F-$D03F: unconnected
        Sync();
        switch (reg)
        {
            case 0x11: // bit 7 reads back the CURRENT raster bit 8
                return (byte)((_regs[0x11] & 0x7F) | (_line >= 256 ? 0x80 : 0));
            case 0x12: // current raster, low 8 bits
                return (byte)(_line & 0xFF);
            case 0x16: // bits 7-6 unconnected, read as 1
                return (byte)(_regs[0x16] | 0xC0);
            case 0x19: // IRQ status: bit 7 = IRQ line, bits 6-4 read as 1
            {
                byte irq = (byte)((_rasterLatch && (_regs[0x1A] & 0x01) != 0) ? 0x80 : 0);
                return (byte)(0x70 | irq | (_rasterLatch ? 0x01 : 0x00));
            }
            case 0x1A: // bits 7-4 unconnected, read as 1
                return (byte)(_regs[0x1A] | 0xF0);
            case 0x1E: // sprite-sprite collision: read clears
            {
                byte v = _spriteSpriteColl;
                _spriteSpriteColl = 0;
                return v;
            }
            case 0x1F: // sprite-background collision: read clears
            {
                byte v = _spriteBgColl;
                _spriteBgColl = 0;
                return v;
            }
            default:
                return _regs[reg];
        }
    }

    public void WriteRegister(int address, byte value)
    {
        int reg = address & 0x3F;
        if (reg > 0x2E) return; // $D02F-$D03F: ignore writes
        if (reg == 0x19)
        {
            // Write 1 to clear latch bits.
            if ((value & 0x01) != 0) _rasterLatch = false;
            return;
        }
        if (reg == 0x1E || reg == 0x1F) return; // read-only
        _regs[reg] = value;
    }

    // ---- VIC memory access (the VIC's own 14-bit bus) ----

    private int Bank => _getBank() & 3;

    private int ScreenBase => ((_regs[0x18] >> 4) & 0x0F) << 10;
    private int BitmapBase => ((_regs[0x18] >> 3) & 0x01) << 13;

    private byte ReadVicRam(int vaddr) =>
        _ram[((Bank << 14) | (vaddr & 0x3FFF)) & 0xFFFF];

    private byte ReadScreenRam(int offset) => ReadVicRam(ScreenBase | (offset & 0x3FF));

    private byte ReadCharRow(int charCode, int row)
    {
        int charBase = ((_regs[0x18] >> 1) & 0x07) << 11;
        int vaddr = charBase | (charCode << 3) | (row & 7);
        // The character ROM answers when the VIC address is $1xxx/$9xxx.
        if ((vaddr & 0x3000) == 0x1000)
            return _charom[vaddr & 0x0FFF];
        return ReadVicRam(vaddr);
    }

    private byte ReadBitmapByte(int offset) => ReadVicRam(BitmapBase | (offset & 0x1FFF));

    // ---- renderer ----

    private bool BitmapMode => (_regs[0x11] & 0x20) != 0;
    private bool EcmMode => (_regs[0x11] & 0x40) != 0;
    private bool MulticolorMode => (_regs[0x16] & 0x10) != 0;

    /// <summary>
    /// Renders the current VIC mode into <paramref name="frame"/> as
    /// palette indices (384x272). Border fills everything outside the
    /// display window; DEN=0 blanks to border color.
    /// </summary>
    public void RenderFrame(byte[] frame)
    {
        if (frame.Length < FrameWidth * FrameHeight)
            throw new ArgumentException("Frame buffer too small.", nameof(frame));
        Sync();

        byte border = (byte)(_regs[0x20] & 0x0F);
        Array.Fill(frame, border);

        if ((_regs[0x11] & 0x10) == 0) return; // DEN=0: screen off

        bool wide = (_regs[0x16] & 0x08) != 0; // CSEL: 40 vs 38 columns
        bool tall = (_regs[0x11] & 0x08) != 0; // RSEL: 25 vs 24 rows
        int xscroll = _regs[0x16] & 0x07;
        int yscroll = _regs[0x11] & 0x07;

        int winX0 = wide ? 24 : 31, winX1 = wide ? 344 : 335;
        int winY0 = tall ? 51 : 55, winY1 = tall ? 251 : 247;

        // Graphics origin: dot 24+XSCROLL, first badline for YSCROLL.
        int orgX = 24 + xscroll;
        int orgY = 48;
        while ((orgY & 7) != yscroll) orgY++;

        // Playfield first, then sprites (which need it for priority/collision).
        var playfield = new byte[FrameWidth * FrameHeight];
        Array.Fill(playfield, border);
        if (BitmapMode)
            RenderBitmap(playfield, orgX, orgY, winX0, winX1, winY0, winY1);
        else
            RenderText(playfield, orgX, orgY, winX0, winX1, winY0, winY1);

        Array.Copy(playfield, frame, frame.Length);
        RenderSprites(frame, playfield, border);
    }

    private void RenderText(byte[] fb, int orgX, int orgY,
        int winX0, int winX1, int winY0, int winY1)
    {
        byte bg0 = (byte)(_regs[0x21] & 0x0F);
        byte mc1 = (byte)(_regs[0x22] & 0x0F);
        byte mc2 = (byte)(_regs[0x23] & 0x0F);
        bool ecm = EcmMode, mcm = MulticolorMode;

        for (int fbY = 0; fbY < FrameHeight; fbY++)
        {
            int line = fbY + FirstVisibleLine;
            if (line < winY0 || line >= winY1) continue;
            int gy = line - orgY;
            if (gy < 0 || gy >= 200) continue;
            int row = gy >> 3, py = gy & 7;
            int baseIdx = fbY * FrameWidth;
            for (int fbX = 0; fbX < FrameWidth; fbX++)
            {
                if (fbX < winX0 || fbX >= winX1) continue;
                int gx = fbX - orgX;
                if (gx < 0 || gx >= 320) continue;
                int col = gx >> 3, px = gx & 7;
                int cell = row * 40 + col;
                int code = ReadScreenRam(cell);
                int colorBits = _colorRam[cell & 0x3FF] & 0x0F;

                byte color;
                if (ecm)
                {
                    // Bits 6-7 of the code select the background.
                    byte bg = (byte)(_regs[0x21 + (code >> 6)] & 0x0F);
                    int bits = ReadCharRow(code & 0x3F, py);
                    color = ((bits >> (7 - px)) & 1) != 0 ? (byte)colorBits : bg;
                }
                else if (mcm && (colorBits & 0x08) != 0)
                {
                    // Multicolor char: 2 bits per pixel.
                    int bits = ReadCharRow(code, py);
                    int pair = (bits >> (6 - (px & 6))) & 3;
                    color = pair switch
                    {
                        0 => bg0,
                        1 => mc1,
                        2 => mc2,
                        _ => (byte)(colorBits & 0x07),
                    };
                }
                else
                {
                    int bits = ReadCharRow(code, py);
                    color = ((bits >> (7 - px)) & 1) != 0 ? (byte)colorBits : bg0;
                }
                fb[baseIdx + fbX] = color;
            }
        }
    }

    private void RenderBitmap(byte[] fb, int orgX, int orgY,
        int winX0, int winX1, int winY0, int winY1)
    {
        byte bg0 = (byte)(_regs[0x21] & 0x0F);
        bool mcm = MulticolorMode;

        for (int fbY = 0; fbY < FrameHeight; fbY++)
        {
            int line = fbY + FirstVisibleLine;
            if (line < winY0 || line >= winY1) continue;
            int gy = line - orgY;
            if (gy < 0 || gy >= 200) continue;
            int baseIdx = fbY * FrameWidth;
            for (int fbX = 0; fbX < FrameWidth; fbX++)
            {
                if (fbX < winX0 || fbX >= winX1) continue;
                int gx = fbX - orgX;
                if (gx < 0 || gx >= 320) continue;

                int cell = (gy >> 3) * 40 + (gx >> 3);
                byte screen = ReadScreenRam(cell);
                byte color;
                if (mcm)
                {
                    // 160x200: 2 bits per pixel.
                    int b = ReadBitmapByte((gy >> 3) * 320 + (gx >> 3) * 8 + (gy & 7));
                    int pair = (b >> (6 - (gx & 6))) & 3;
                    color = pair switch
                    {
                        0 => bg0,
                        1 => (byte)(screen >> 4),
                        2 => (byte)(screen & 0x0F),
                        _ => (byte)(_colorRam[cell & 0x3FF] & 0x0F),
                    };
                }
                else
                {
                    int b = ReadBitmapByte((gy >> 3) * 320 + (gx >> 3) * 8 + (gy & 7));
                    bool set = ((b >> (7 - (gx & 7))) & 1) != 0;
                    color = set ? (byte)(screen >> 4) : (byte)(screen & 0x0F);
                }
                fb[baseIdx + fbX] = color;
            }
        }
    }


    private void RenderSprites(byte[] fb, byte[] playfield, byte border)
    {
        int enable = _regs[0x15];
        if (enable == 0) return;

        byte mc0 = (byte)(_regs[0x25] & 0x0F);
        byte mc1 = (byte)(_regs[0x26] & 0x0F);
        int msbX = _regs[0x10];

        // Track sprite pixels for sprite-sprite collision.
        var drawn = new byte[FrameWidth * FrameHeight];

        // Sprites render back-to-front: sprite 7 first, sprite 0 last.
        for (int n = 7; n >= 0; n--)
        {
            if ((enable & (1 << n)) == 0) continue;
            int sx = _regs[n * 2] | (((msbX >> n) & 1) << 8);
            int sy = _regs[n * 2 + 1];
            byte color = (byte)(_regs[0x27 + n] & 0x0F);
            bool multicolor = (_regs[0x1C] & (1 << n)) != 0;
            bool xExpand = (_regs[0x1D] & (1 << n)) != 0;
            bool yExpand = (_regs[0x17] & (1 << n)) != 0;
            bool behindBg = (_regs[0x1B] & (1 << n)) != 0;

            int ptr = ReadScreenRam(0x3F8 + n) << 6;
            int w = xExpand ? 48 : 24;
            int h = yExpand ? 42 : 21;

            for (int py = 0; py < h; py++)
            {
                int srcY = yExpand ? py / 2 : py;
                int fbY = sy - FirstVisibleLine + py;
                if (fbY < 0 || fbY >= FrameHeight) continue;
                for (int px = 0; px < w; px++)
                {
                    int srcX = xExpand ? px / 2 : px;
                    int fbX = sx + px;
                    if (fbX < 0 || fbX >= FrameWidth) continue;

                    // Sprite data: 3 bytes per row, MSB first.
                    int byteAddr = ptr + srcY * 3 + srcX / 8;
                    int bit = 7 - (srcX % 8);
                    byte pixel;
                    if (multicolor)
                    {
                        // 2 bits per pixel, MSB first.
                        int shift = 6 - (srcX & 6);
                        int pair = (ReadVicRam(byteAddr) >> shift) & 3;
                        if (pair == 0) continue; // transparent
                        pixel = pair switch { 1 => mc0, 2 => color, _ => mc1 };
                    }
                    else
                    {
                        if (((ReadVicRam(byteAddr) >> bit) & 1) == 0) continue;
                        pixel = color;
                    }

                    int idx = fbY * FrameWidth + fbX;
                    // Sprite-sprite collision.
                    if (drawn[idx] != 0)
                    {
                        _spriteSpriteColl |= (byte)((1 << n) | drawn[idx]);
                    }
                    drawn[idx] |= (byte)(1 << n);

                    // Sprite-background collision (non-border playfield pixel).
                    if (playfield[idx] != border)
                        _spriteBgColl |= (byte)(1 << n);

                    // Priority: behind = only draw on background/border.
                    if (behindBg && playfield[idx] != border && playfield[idx] != (_regs[0x21] & 0x0F))
                        continue;
                    fb[idx] = pixel;
                }
            }
        }
    }

    /// <summary>C64 palette (approximate PAL RGB), index 0-15.</summary>
    public static readonly uint[] Palette = {
        0x000000, 0xFFFFFF, 0x813338, 0x75CEC8,
        0x8E3C97, 0x56AC4D, 0x2E2C9B, 0xEDF171,
        0x8E5029, 0x553800, 0xC46C71, 0x4A4A4A,
        0x7B7B7B, 0xA9FF9F, 0x706DEB, 0xB2B2B2,
    };
}
