namespace C64.Demo.BouncingBall;

/// <summary>
/// The hand-assembled 6502 bouncing-ball program. Kept separate from
/// Program.cs so tests (and curious humans) can load it without running
/// the animation.
///
/// Assembly source (loaded at <see cref="LoadAddress"/>):
/// <code>
///   zp $00 = ballX (0-39)   $01 = ballY (0-24)   $02 = dx   $03 = dy
///   zp $04/$05 = screen pointer
///   $0900/$0919 = row offset tables (filled in by the host)
///
///           LDX #$00
///           LDA #$20              ; space
///   clear:  STA $0400,X           ; 4 pages x 256 bytes = whole screen
///           STA $0500,X
///           STA $0600,X
///           STA $0700,X
///           INX
///           BNE clear
///           LDA #16 : STA $00     ; ballX = 16
///           LDA #12 : STA $01     ; ballY = 12
///           LDA #1  : STA $02     ; dx = +1
///                     STA $03     ; dy = +1
///           LDA #'*' : JSR draw
///   frame:  LDA #' ' : JSR draw   ; erase ball at old position
///           LDA $00 : CLC : ADC $02 : STA $00   ; ballX += dx
///           CMP #40 : BNE chkL
///           LDA #$FF : STA $02    ; hit right wall -> dx = -1
///           DEC $00               ; pull back to 39
///           LDA $00               ; reload X: LDA #$FF clobbered A!
///   chkL:   CMP #$FF : BNE updY
///           LDA #1 : STA $02      ; hit left wall -> dx = +1
///           INC $00               ; $FF -> 0
///   updY:   LDA $01 : CLC : ADC $03 : STA $01   ; ballY += dy
///           CMP #25 : BNE chkT
///           LDA #$FF : STA $03 : DEC $01       ; hit bottom
///           LDA $01               ; reload Y for the same reason
///   chkT:   CMP #$FF : BNE drawIt
///           LDA #1 : STA $03 : INC $01          ; hit top
///   drawIt: LDA #'*' : JSR draw
///           JSR delay
///           JMP frame
///   draw:   PHA                   ; A = char to draw
///           LDY $01               ; Y = ballY
///           LDA rowLo,Y : CLC : ADC $00 : STA $04
///           LDA rowHi,Y : ADC #$00     : STA $05  ; ptr = $0400 + Y*40 + X
///           PLA : LDY #0
///           STA ($04),Y           ; write the char
///           RTS
///   delay:  LDX #$FF
///   dOut:   LDY #$FF
///   dIn:    DEY : BNE dIn
///           DEX : BNE dOut        ; ~330k cycles ~ 1/3 s at 1 MHz
///           RTS
/// </code>
/// </summary>
public static class BallProgram
{
    public const ushort LoadAddress = 0x0200;
    public const ushort RowTableLo = 0x0900;
    public const ushort RowTableHi = 0x0919;
    public const ushort ScreenAddress = 0x0400;
    public const int ScreenWidth = 40;
    public const int ScreenHeight = 25;

    /// <summary>Raw machine code of the program.</summary>
    public static readonly byte[] Bytes = new byte[]
    {
        0xA2, 0x00,                         // $0200: LDX #$00
        0xA9, 0x20,                         // $0202: LDA #$20 (space)
        0x9D, 0x00, 0x04,                   // $0204: STA $0400,X
        0x9D, 0x00, 0x05,                   // $0207: STA $0500,X
        0x9D, 0x00, 0x06,                   // $020A: STA $0600,X
        0x9D, 0x00, 0x07,                   // $020D: STA $0700,X
        0xE8,                               // $0210: INX
        0xD0, 0xF1,                         // $0211: BNE $0204 (clear)
        0xA9, 0x10, 0x85, 0x00,             // $0213: LDA #16 : STA $00 (ballX)
        0xA9, 0x0C, 0x85, 0x01,             // $0217: LDA #12 : STA $01 (ballY)
        0xA9, 0x01, 0x85, 0x02, 0x85, 0x03, // $021B: LDA #1 : STA $02 : STA $03 (dx, dy)
        0xA9, 0x2A, 0x20, 0x70, 0x02,       // $0221: LDA #'*' : JSR draw ($0270)
        // frame ($0226):
        0xA9, 0x20, 0x20, 0x70, 0x02,       // $0226: LDA #' ' : JSR draw (erase)
        0xA5, 0x00, 0x18, 0x65, 0x02, 0x85, 0x00, // $022B: ballX += dx
        0xC9, 0x28, 0xD0, 0x08,             // $0232: CMP #40 : BNE chkL ($023E)
        0xA9, 0xFF, 0x85, 0x02, 0xC6, 0x00, // $0236: dx=-1 : DEC ballX (right wall)
        0xA5, 0x00,                         // $023C: LDA $00 (reload X)
        0xC9, 0xFF, 0xD0, 0x06,             // $023E: CMP #$FF : BNE updY ($0248)
        0xA9, 0x01, 0x85, 0x02, 0xE6, 0x00, // $0242: dx=+1 : INC ballX (left wall)
        0xA5, 0x01, 0x18, 0x65, 0x03, 0x85, 0x01, // $0248: ballY += dy
        0xC9, 0x19, 0xD0, 0x08,             // $024F: CMP #25 : BNE chkT ($025B)
        0xA9, 0xFF, 0x85, 0x03, 0xC6, 0x01, // $0253: dy=-1 : DEC ballY (bottom wall)
        0xA5, 0x01,                         // $0259: LDA $01 (reload Y)
        0xC9, 0xFF, 0xD0, 0x06,             // $025B: CMP #$FF : BNE drawIt ($0265)
        0xA9, 0x01, 0x85, 0x03, 0xE6, 0x01, // $025F: dy=+1 : INC ballY (top wall)
        0xA9, 0x2A, 0x20, 0x70, 0x02,       // $0265: LDA #'*' : JSR draw
        0x20, 0x88, 0x02,                   // $026A: JSR delay ($0288)
        0x4C, 0x26, 0x02,                   // $026D: JMP frame ($0226)
        // draw ($0270): A = char to draw at (ballX, ballY)
        0x48,                               // $0270: PHA
        0xA4, 0x01,                         // $0271: LDY $01 (ballY)
        0xB9, 0x00, 0x09,                   // $0273: LDA $0900,Y (row lo)
        0x18, 0x65, 0x00, 0x85, 0x04,       // $0276: CLC : ADC ballX : STA $04
        0xB9, 0x19, 0x09,                   // $027B: LDA $0919,Y (row hi)
        0x69, 0x00, 0x85, 0x05,             // $027E: ADC #$00 : STA $05
        0x68, 0xA0, 0x00,                   // $0282: PLA : LDY #$00
        0x91, 0x04,                         // $0285: STA ($04),Y
        0x60,                               // $0287: RTS
        // delay ($0288): ~330k cycles
        0xA2, 0xFF,                         // $0288: LDX #$FF
        0xA0, 0xFF,                         // $028A: LDY #$FF
        0x88,                               // $028C: DEY
        0xD0, 0xFD,                         // $028D: BNE $028C
        0xCA,                               // $028F: DEX
        0xD0, 0xF8,                         // $0290: BNE $028A
        0x60,                               // $0292: RTS
    };

    /// <summary>
    /// Loads the program, its row tables and the reset vector into the bus.
    /// </summary>
    public static void Load(C64.Core.Memory.RamBus bus)
    {
        bus.LoadProgram(Bytes, LoadAddress);
        for (int r = 0; r < ScreenHeight; r++)
        {
            int addr = ScreenAddress + r * ScreenWidth;
            bus.Write((ushort)(RowTableLo + r), (byte)(addr & 0xFF));
            bus.Write((ushort)(RowTableHi + r), (byte)(addr >> 8));
        }
        bus.SetResetVector(LoadAddress);
    }
}
