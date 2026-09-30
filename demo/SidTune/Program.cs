using SidChip = C64.Core.Sid.Sid;

// M7 demo: plays a simple tune on the SID and writes it to tune.wav.
// Programs the SID registers directly (like a BASIC POKE would).
//
// Usage: dotnet run --project demo/SidTune

var sid = new SidChip();

// "Mary Had a Little Lamb" in C major.
// Frequencies (Hz): E4=329.63, D4=293.66, C4=261.63, G4=392.00
var melody = new (double Freq, double Beats)[]
{
    (329.63, 1), (293.66, 1), (261.63, 1), (293.66, 1),
    (329.63, 1), (329.63, 1), (329.63, 2),
    (293.66, 1), (293.66, 1), (293.66, 2),
    (329.63, 1), (392.00, 1), (392.00, 2),
    (329.63, 1), (293.66, 1), (261.63, 1), (293.66, 1),
    (329.63, 1), (329.63, 1), (329.63, 1), (329.63, 1),
    (293.66, 1), (293.66, 1), (329.63, 1), (293.66, 1),
    (261.63, 4),
};

const int sampleRate = 44100;
const double beatSec = 0.35; // tempo
var samples = new System.Collections.Generic.List<short>();

Console.WriteLine("Playing...");

// Voice 1: triangle, fast attack, full sustain.
// Note: decay is set to 0 (fastest) deliberately. The SID's envelope rate
// counter never resets, so with a slow decay the counter can sit far above
// the fast release period at gate-off and the release then waits for the
// 16-bit wrap (the authentic ADSR delay quirk, up to ~65 ms) before starting.
sid.WriteRegister(0x05, 0x00); // attack=0, decay=0
sid.WriteRegister(0x06, 0xF0); // sustain=15, release=0
sid.WriteRegister(0x18, 0x0F); // volume max

foreach (var (freq, beats) in melody)
{
    // Set frequency.
    int f = (int)(freq * (1 << 24) / SidChip.ClockHz);
    sid.WriteRegister(0x00, (byte)(f & 0xFF));
    sid.WriteRegister(0x01, (byte)((f >> 8) & 0xFF));
    // Gate on (triangle).
    sid.WriteRegister(0x04, 0x11);

    int count = (int)(beats * beatSec * sampleRate);
    var buf = new short[count];
    sid.RenderSamples(buf, count, sampleRate);
    samples.AddRange(buf);

    // Gate off (short gap between notes).
    sid.WriteRegister(0x04, 0x10);
    var gap = new short[(int)(0.05 * sampleRate)];
    sid.RenderSamples(gap, gap.Length, sampleRate);
    samples.AddRange(gap);
}

WriteWav("tune.wav", samples.ToArray(), sampleRate);
Console.WriteLine($"Wrote tune.wav ({samples.Count} samples).");

static void WriteWav(string path, short[] samples, int sampleRate)
{
    using var fs = new FileStream(path, FileMode.Create);
    using var bw = new BinaryWriter(fs);
    int dataSize = samples.Length * 2;
    bw.Write("RIFF"u8.ToArray()); bw.Write(36 + dataSize); bw.Write("WAVE"u8.ToArray());
    bw.Write("fmt "u8.ToArray()); bw.Write(16); bw.Write((ushort)1); bw.Write((ushort)1);
    bw.Write(sampleRate); bw.Write(sampleRate * 2); bw.Write((ushort)2); bw.Write((ushort)16);
    bw.Write("data"u8.ToArray()); bw.Write(dataSize);
    foreach (var s in samples) bw.Write(s);
}
