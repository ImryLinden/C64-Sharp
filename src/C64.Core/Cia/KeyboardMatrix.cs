namespace C64.Core.Cia;

/// <summary>
/// The C64 keyboard matrix: 8 columns (CIA1 Port A) x 8 rows (CIA1 Port B).
/// A pressed key connects its column to its row; when the Kernal drives a
/// column low and reads the rows, a pressed key pulls its row low.
/// Column/row layout matches the C64 Programmer's Reference:
/// <code>
///           R0    R1    R2    R3    R4    R5    R6    R7
///   C0:    DEL   RET   CRSR  F7    F1    F3    F5    CRSR
///   C1:    3     W     A     4     Z     S     E     LSHF
///   C2:    5     R     D     6     C     F     T     X
///   C3:    7     Y     G     8     B     H     U     V
///   C4:    9     I     J     0     M     K     O     N
///   C5:    +     P     L     -     .     :     @     ,
///   C6:    POUND *     ;     CLR   RSHF  =     UP    /
///   C7:    1     <-    CTRL  2     SPC   C=    Q     RUN
/// </code>
/// (Columns C0-C7 = CIA1 Port A bits; rows R0-R7 = Port B bits.
/// Transcribed from the Kernal's decode table at $EB81.)
/// </summary>
public class KeyboardMatrix
{
    private readonly bool[,] _keys = new bool[8, 8];
    private readonly object _keyLock = new();

    public void SetKey(int col, int row, bool down)
    {
        if (col < 0 || col > 7 || row < 0 || row > 7)
            throw new ArgumentOutOfRangeException();
        lock (_keyLock)
        {
            _keys[col, row] = down;
        }
    }

    public void Clear()
    {
        lock (_keyLock)
        {
            Array.Clear(_keys, 0, _keys.Length);
        }
    }

    /// <summary>
    /// Computes the Port B row bits for a Port A column drive byte.
    /// 1 = high/inactive, 0 = low (key pressed on a driven column).
    /// </summary>
    public byte ReadRows(byte portA)
    {
        lock (_keyLock)
        {
            byte rows = 0xFF;
            for (int col = 0; col < 8; col++)
            {
                if ((portA & (1 << col)) != 0) continue; // column not driven low
                for (int row = 0; row < 8; row++)
                    if (_keys[col, row]) rows &= (byte)~(1 << row);
            }
            return rows;
        }
    }

    // Named key positions for the host.
    public static (int Col, int Row) Key(string name) => name.ToUpperInvariant() switch
    {
        // From the Kernal's decode table at $EB81 (indexed [column][row]).
        "DEL" => (0, 0), "RETURN" => (0, 1), "ENTER" => (0, 1),
        "F7" => (0, 3), "F1" => (0, 4), "F3" => (0, 5), "F5" => (0, 6),
        "CRSRUD" => (0, 7), "CURSORUD" => (0, 7), // Cursor Up/Down (Shift=Up)
        "CRSRLR" => (0, 2), "CURSORLR" => (0, 2), // Cursor Left/Right (Shift=Left)
        "3" => (1, 0), "W" => (1, 1), "A" => (1, 2), "4" => (1, 3),
        "Z" => (1, 4), "S" => (1, 5), "E" => (1, 6), "LSHIFT" => (1, 7),
        "5" => (2, 0), "R" => (2, 1), "D" => (2, 2), "6" => (2, 3),
        "C" => (2, 4), "F" => (2, 5), "T" => (2, 6), "X" => (2, 7),
        "7" => (3, 0), "Y" => (3, 1), "G" => (3, 2), "8" => (3, 3),
        "B" => (3, 4), "H" => (3, 5), "U" => (3, 6), "V" => (3, 7),
        "9" => (4, 0), "I" => (4, 1), "J" => (4, 2), "0" => (4, 3),
        "M" => (4, 4), "K" => (4, 5), "O" => (4, 6), "N" => (4, 7),
        "+" => (5, 0), "P" => (5, 1), "L" => (5, 2), "-" => (5, 3),
        "." => (5, 4), ":" => (5, 5), "@" => (5, 6), "," => (5, 7),
        "*" => (6, 1), ";" => (6, 2), "=" => (6, 5), "/" => (6, 7),
        "CLRHOME" => (6, 3), "CLR/HOME" => (6, 3),
        "RSHIFT" => (6, 4),
        "1" => (7, 0), "CTRL" => (7, 2), "2" => (7, 3), " " => (7, 4), "SPACE" => (7, 4),
        "COMMODORE" => (7, 5), "C=" => (7, 5),
        "Q" => (7, 6), "RUNSTOP" => (7, 7), "RUN/STOP" => (7, 7),
        _ => throw new ArgumentException($"Unknown key '{name}'."),
    };
}
