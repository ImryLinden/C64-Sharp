using SidChip = C64.Core.Sid.Sid;
using Xunit;

namespace C64.Core.Tests;

/// <summary>
/// Accuracy tests for the reSID-style SID: ADSR rate table, exponential
/// decay, the never-reset rate counter (ADSR delay quirk), combined
/// waveforms as wired-AND, exact noise LFSR, hard sync, ring modulation,
/// and the multimode filter (routing, stability, determinism).
/// Uses the chip's internal test hooks (InternalsVisibleTo).
/// </summary>
public class SidAccuracyTests
{
    /// <summary>Clocks the chip until the envelope predicate holds; returns clocks used.</summary>
    private static long ClocksUntil(SidChip sid, int v, Func<int, bool> done, long maxClocks = 60_000_000)
    {
        long n = 0;
        while (!done(sid.EnvelopeLevel(v)) && n < maxClocks) { sid.ClockChip(); n++; }
        Assert.True(n < maxClocks, "Envelope never reached target (timed out)");
        return n;
    }

    private static void ClockN(SidChip sid, long n)
    {
        for (long i = 0; i < n; i++) sid.ClockChip();
    }

    // ---------------- ADSR ----------------

    [Fact]
    public void Attack_Is_Linear_Per_Rate_Table()
    {
        var sid = new SidChip();
        sid.WriteRegister(0x05, 0x50); // A=5 (period 220), D=0
        sid.WriteRegister(0x04, 0x01); // gate on, no waveform

        long t1 = ClocksUntil(sid, 0, e => e >= 100); // 0 -> 100
        long t2 = ClocksUntil(sid, 0, e => e >= 200); // 100 -> 200

        // Linear attack: equal spans take equal time (100 steps x 220 clocks).
        Assert.InRange((double)t2, t1 * 0.9, t1 * 1.1);
        Assert.InRange(t1, 20000L, 24000L); // 100 * 220 = 22000
    }

    [Fact]
    public void Decay_Is_Exponential_Not_Linear()
    {
        var sid = new SidChip();
        sid.WriteRegister(0x05, 0x00); // A=0 (fast), D=0
        sid.WriteRegister(0x06, 0x00); // S=0
        sid.WriteRegister(0x04, 0x01); // gate on
        ClocksUntil(sid, 0, e => e >= 255); // attack to max

        // Decay spans: 200->150 (all divider 1) vs 150->50 (dividers 1,2,4).
        // Linear decay would take ~equal time for ~equal spans; the
        // exponential decay takes much longer as the counter sinks.
        ClocksUntil(sid, 0, e => e <= 200);
        long tHigh = ClocksUntil(sid, 0, e => e <= 150);
        long tLow = ClocksUntil(sid, 0, e => e <= 50);
        Assert.True(tLow > tHigh * 1.5,
            $"Decay not exponential: 200->150 took {tHigh} clocks, 100->50 took {tLow}");
    }

    [Fact]
    public void Sustain_Holds_At_Register_Level()
    {
        var sid = new SidChip();
        sid.WriteRegister(0x05, 0x00); // A=0, D=0
        sid.WriteRegister(0x06, 0x80); // S=8, R=0
        sid.WriteRegister(0x04, 0x01); // gate on

        ClockN(sid, 200_000); // attack + decay well past sustain
        int env = sid.EnvelopeLevel(0);
        Assert.True((env & 0xF0) == 0x80, $"Sustain level wrong: 0x{env:X2}");

        ClockN(sid, 2_000_000); // sustain must hold, not drift
        env = sid.EnvelopeLevel(0);
        Assert.True((env & 0xF0) == 0x80, $"Sustain drifted: 0x{env:X2}");
    }

    [Fact]
    public void Release_Reaches_Zero_And_Holds()
    {
        var sid = new SidChip();
        sid.WriteRegister(0x05, 0x00);
        sid.WriteRegister(0x06, 0x00); // S=0, R=0 (fast)
        sid.WriteRegister(0x04, 0x01); // gate on
        ClocksUntil(sid, 0, e => e >= 255);

        sid.WriteRegister(0x04, 0x00); // gate off -> release
        ClocksUntil(sid, 0, e => e <= 0);
        ClockN(sid, 1_000_000);
        Assert.Equal(0, sid.EnvelopeLevel(0)); // hold-zero: frozen at 0
    }

    [Fact]
    public void Adsr_Rate_Counter_Is_Never_Reset_On_Gate()
    {
        // The famous ADSR delay quirk: re-gating must NOT restart the
        // 16-bit rate counter. After 5 clocks, gate off/on, then 4 more
        // clocks the counter hits 9 (attack rate 0) and the envelope steps.
        // A naive reset-on-gate implementation would still read 0.
        var sid = new SidChip();
        sid.WriteRegister(0x05, 0x00); // A=0 (period 9)
        sid.WriteRegister(0x04, 0x01); // gate on
        ClockN(sid, 5);
        Assert.Equal(0, sid.EnvelopeLevel(0));

        sid.WriteRegister(0x04, 0x00); // gate off
        sid.WriteRegister(0x04, 0x01); // gate on again
        ClockN(sid, 4); // counter: 5 -> 9, fires
        Assert.Equal(1, sid.EnvelopeLevel(0));
    }

    [Fact]
    public void Adsr_Delay_Quirk_Short_Period_Waits_For_Wrap()
    {
        // Switch to a shorter attack period while the free-running counter
        // sits above it: the envelope must wait for the 16-bit wrap.
        var sid = new SidChip();
        sid.WriteRegister(0x05, 0xF0); // A=15 (period 31251)
        sid.WriteRegister(0x04, 0x01); // gate on
        ClockN(sid, 31250); // counter = 31250, envelope still 0
        Assert.Equal(0, sid.EnvelopeLevel(0));

        sid.WriteRegister(0x05, 0x00); // A=0 (period 9): 31250 > 9
        // Wrap wait: 31250 -> 65535 -> 0 -> 9 takes 34295 clocks.
        ClockN(sid, 34294);
        Assert.Equal(0, sid.EnvelopeLevel(0)); // still waiting
        sid.ClockChip(); // counter hits 9 -> first step
        Assert.Equal(1, sid.EnvelopeLevel(0));
    }

    // ---------------- Waveforms ----------------

    [Fact]
    public void Combined_TriSaw_Is_And_With_Pulldown()
    {
        var sid = new SidChip();
        sid.SetPhase(0, 0x7FF000); // acc12 = 0x7FF, msb = 0
        sid.WriteRegister(0x04, 0x30); // tri+saw, no gate (wave only)

        // tri = 0x7FF, saw = 0x7FF, wired-AND = 0x7FF, 6581 pulldown x3/8 = 767.
        // A naive sum would give 4094; clean digital only would give 2047.
        Assert.Equal(767 - 2048, sid.WaveOutput(0));
    }

    [Fact]
    public void Combined_TriPulse_Is_Clean_And()
    {
        var sid = new SidChip();
        sid.SetPhase(0, 0x7FF000); // acc12 = 0x7FF
        sid.WriteRegister(0x02, 0x00);
        sid.WriteRegister(0x03, 0x04); // pw = 0x400
        sid.WriteRegister(0x04, 0x50); // tri+pulse

        // tri = 0x7FF, pulse (0x7FF >= 0x400) = 0xFFF, AND = 0x7FF, no pulldown.
        Assert.Equal(2047 - 2048, sid.WaveOutput(0));
    }

    [Fact]
    public void Noise_Lfsr_Seed_And_Churn()
    {
        var sid = new SidChip();
        Assert.Equal(0x7FFFF8u, sid.NoiseLfsr(0)); // reset seed

        sid.WriteRegister(0x04, 0x88); // TEST + noise: LFSR reseeded and held
        sid.ClockChip();
        Assert.Equal(0x7FFFF8u, sid.NoiseLfsr(0));

        // Clear TEST, max freq: bit-19 edges churn the LFSR.
        sid.WriteRegister(0x04, 0x80);
        sid.WriteRegister(0x00, 0xFF);
        sid.WriteRegister(0x01, 0xFF);
        ClockN(sid, 100_000);
        Assert.NotEqual(0x7FFFF8u, sid.NoiseLfsr(0));
    }

    [Fact]
    public void Hard_Sync_Resets_Oscillator_On_Modulator_Fall()
    {
        var sid = new SidChip();
        // Modulator voice 0: max freq -> MSB falls ~every 256 clocks.
        sid.WriteRegister(0x00, 0xFF);
        sid.WriteRegister(0x01, 0xFF);
        // Voice 1: slow freq + sync bit (chain: voice 2 <- voice 1).
        sid.WriteRegister(0x07, 0x00);
        sid.WriteRegister(0x08, 0x01); // freq 0x0100
        sid.WriteRegister(0x0B, 0x02); // sync, no waveform needed

        ClockN(sid, 100_000);
        // Synced every ~256 clocks at 256 clocks/clock: phase stays tiny.
        // Without sync it would have drifted to ~8.8M.
        Assert.True(sid.Phase(1) < 100_000, $"Sync failed, phase={sid.Phase(1)}");
    }

    [Fact]
    public void Ring_Mod_Xors_Triangle_Fold_Only()
    {
        var sid = new SidChip();
        sid.SetPhase(2, 0x800000); // modulator (voice 3 -> voice 1): MSB = 1
        sid.SetPhase(0, 0x7FF000); // acc12 = 0x7FF, own msb = 0

        sid.WriteRegister(0x04, 0x10); // triangle, no ring
        Assert.Equal(-1, sid.WaveOutput(0)); // 0x7FF - 2048

        sid.WriteRegister(0x04, 0x14); // triangle + ring: fold = 0^1
        Assert.Equal(0, sid.WaveOutput(0)); // (0x7FF^0xFFF)=0x800 - 2048

        // Ring mod must not touch the saw path.
        sid.WriteRegister(0x04, 0x20); // saw, no ring
        int sawPlain = sid.WaveOutput(0);
        sid.WriteRegister(0x04, 0x24); // saw + ring
        Assert.Equal(sawPlain, sid.WaveOutput(0));
    }

    // ---------------- Filter ----------------

    private static SidChip FilteredSaw(byte filterSelect, byte mode)
    {
        var sid = new SidChip();
        sid.WriteRegister(0x00, 0xFF);
        sid.WriteRegister(0x01, 0xFF); // ~3848 Hz saw
        sid.WriteRegister(0x05, 0x00); // A=0, D=0
        sid.WriteRegister(0x06, 0xF0); // S=max
        sid.WriteRegister(0x04, 0x21); // gate + saw
        sid.WriteRegister(0x17, filterSelect);
        sid.WriteRegister(0x18, mode);
        return sid;
    }

    private static int RenderMax(SidChip sid, int samples)
    {
        var buf = new short[samples];
        sid.RenderSamples(buf, samples, 44100);
        int max = 0;
        foreach (short s in buf) max = Math.Max(max, Math.Abs(s));
        return max;
    }

    [Fact]
    public void Filter_Lowpass_Attenuates_High_Frequency()
    {
        // Cutoff reg 100 -> ~40 Hz; 3848 Hz saw should be crushed.
        var sid = FilteredSaw(0x01, 0x1F);
        sid.WriteRegister(0x15, 100);
        sid.WriteRegister(0x16, 0x00);
        Assert.True(RenderMax(sid, 4410) < 500, "Lowpass did not attenuate");

        // Control: same voice unfiltered is loud.
        var direct = FilteredSaw(0x00, 0x0F);
        Assert.True(RenderMax(direct, 4410) > 5000, "Unfiltered voice unexpectedly quiet");
    }

    [Fact]
    public void Filter_Highpass_Passes_High_Frequency()
    {
        // 3848 Hz saw through a ~40 Hz highpass passes nearly untouched.
        var sid = FilteredSaw(0x01, 0x4F);
        sid.WriteRegister(0x15, 100);
        sid.WriteRegister(0x16, 0x00);
        Assert.True(RenderMax(sid, 4410) > 5000, "Highpass unexpectedly quiet");
    }

    [Fact]
    public void Filter_Max_Resonance_Sweep_Stays_Stable()
    {
        // Worst case: loud saw, max resonance, cutoff swept across the full
        // 11-bit range. A blowup (NaN states) would pin the output constant;
        // real signal has thousands of distinct values.
        var sid = FilteredSaw(0x01, 0x1F);
        var buf = new short[44100 * 2];
        int pos = 0;
        for (int cut = 0; cut <= 2047 && pos < buf.Length; cut += 256)
        {
            sid.WriteRegister(0x15, (byte)(cut & 0xFF));
            sid.WriteRegister(0x16, (byte)(((cut >> 8) & 0x07) | 0xF0)); // res = 15
            int n = Math.Min(4410, buf.Length - pos);
            var tmp = new short[n];
            sid.RenderSamples(tmp, n, 44100);
            Array.Copy(tmp, 0, buf, pos, n);
            pos += n;
        }

        var distinct = new HashSet<short>(buf);
        Assert.True(distinct.Count > 100,
            $"Filter blew up under resonance sweep: only {distinct.Count} distinct values");
    }

    [Fact]
    public void Voice3_Off_Disconnects_Voice3()
    {
        var sid = new SidChip();
        sid.WriteRegister(0x0E, 0x45);
        sid.WriteRegister(0x0F, 0x1D); // voice 3 ~440 Hz
        sid.WriteRegister(0x13, 0x00);
        sid.WriteRegister(0x14, 0xF0);
        sid.WriteRegister(0x12, 0x21); // gate + saw
        sid.WriteRegister(0x18, 0x8F); // voice3-off + vol 15
        Assert.True(RenderMax(sid, 4410) < 500, "Voice 3 audible despite voice3-off");

        sid.WriteRegister(0x18, 0x0F); // clear voice3-off
        Assert.True(RenderMax(sid, 4410) > 5000, "Voice 3 silent after clearing voice3-off");
    }

    [Fact]
    public void Osc3_Env3_Readback()
    {
        var sid = new SidChip();
        sid.WriteRegister(0x0E, 0xFF);
        sid.WriteRegister(0x0F, 0xFF); // voice 3 fast
        sid.WriteRegister(0x13, 0x00);
        sid.WriteRegister(0x14, 0xF0);
        sid.WriteRegister(0x12, 0x21); // gate + saw

        var buf = new short[4410];
        bool oscSeen = false;
        for (int i = 0; i < 300; i++)
        {
            sid.RenderSamples(buf, 147, 44100);
            if (sid.ReadRegister(0x1D) != 0) oscSeen = true;
        }
        Assert.True(oscSeen, "OSC3 never nonzero");
        Assert.True(sid.ReadRegister(0x1E) > 200, "ENV3 not near max");
    }

    // ---------------- Stability ----------------

    private static short[] FuzzRender(int seed, int seconds)
    {
        var sid = new SidChip();
        var rnd = new Random(seed);
        var buf = new short[44100 * seconds];
        int pos = 0;
        while (pos < buf.Length)
        {
            // Random register writes (all 32, incl. filter/mode/volume).
            for (int i = 0; i < 8; i++)
                sid.WriteRegister(rnd.Next(0, 32), (byte)rnd.Next(256));
            int n = Math.Min(rnd.Next(50, 500), buf.Length - pos);
            var tmp = new short[n];
            sid.RenderSamples(tmp, n, 44100);
            Array.Copy(tmp, 0, buf, pos, n);
            pos += n;
        }
        return buf;
    }

    [Fact]
    public void Fuzz_Soak_Is_Deterministic_And_Stable()
    {
        // 2 seconds of random register abuse must not throw, must not lock
        // the output to a constant (NaN/overflow detector), and must be
        // bit-identical across runs (no hidden nondeterminism).
        short[] a = FuzzRender(1234, 2);
        short[] b = FuzzRender(1234, 2);
        Assert.Equal(a, b);

        var distinct = new HashSet<short>();
        foreach (short s in a)
        {
            distinct.Add(s);
            if (distinct.Count > 100) break;
        }
        Assert.True(distinct.Count > 100, "Fuzz output stuck at a constant value");
    }
}
