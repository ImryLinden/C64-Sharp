using System;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("C64.Core.Tests")]

namespace C64.Core.Sid;

/// <summary>
/// The SID sound chip (MOS 6581/8580), cycle-clocked functional model.
///
/// Three voices, each with a 24-bit phase-accumulator oscillator (triangle,
/// sawtooth, pulse, noise — including combined waveforms as wired-AND with
/// 6581-style pulldown), an 8-bit reSID-style ADSR envelope generator
/// (free-running 16-bit rate counter that is never reset, the real 16-entry
/// period table, piecewise-exponential decay/release), ring modulation,
/// hard sync, and a shared state-variable multimode filter (LP/BP/HP/notch)
/// with resonance and an exponential 6581-ish cutoff curve.
///
/// The chip is clocked at the PAL PHI2 rate (985248 Hz). Audio is rendered
/// by calling <see cref="RenderSamples"/> which advances the chip and fills
/// a buffer with 16-bit PCM at the requested sample rate.
///
/// Behavioral notes (constants only — period table, exponential breakpoints,
/// LFSR taps/seed — reimplemented from scratch from the published reSID/
/// reSIDfp documentation; no reSID code is used):
/// <list type="bullet">
/// <item>The envelope rate counter is never reset — not by gate changes,
/// register writes, or the TEST bit. Switching to a shorter ADSR period
/// while the counter sits above it makes the envelope wait for the 16-bit
/// wrap (the famous ADSR delay quirk); this falls out of the model.</item>
/// <item>Decay/release slow down as the counter sinks (dividers 1,2,4,8,16,30
/// below breakpoints 93,54,26,14,6) — the SID's characteristic exponential
/// pluck. Attack is linear.</item>
/// <item>Noise uses a per-voice 23-bit LFSR (taps 22+17, seed 0x7FFFF8),
/// clocked on the rising edge of accumulator bit 19 so it pitch-tracks.</item>
/// </list>
/// </summary>
public sealed class Sid
{
    public const int ClockHz = 985248; // PAL PHI2

    private readonly byte[] _regs = new byte[32];

    // --- Per-voice oscillator state ---
    private readonly uint[] _phase = new uint[3];      // 24-bit accumulator
    private readonly uint[] _lfsr = new uint[3];       // 23-bit noise shift register
    private readonly bool[] _prevBit19 = new bool[3];  // for noise clocking
    private readonly bool[] _prevMsb = new bool[3];    // for hard sync

    // --- Per-voice envelope state (reSID model) ---
    private readonly byte[] _env = new byte[3];         // 8-bit envelope counter
    private readonly ushort[] _rateCounter = new ushort[3]; // 16-bit, free-running, never reset
    private readonly byte[] _expCounter = new byte[3];  // exponential divider counter
    private readonly byte[] _expPeriod = new byte[3];   // current divider (1,2,4,8,16,30)
    private readonly byte[] _envState = new byte[3];    // 0=attack, 1=decay/sustain, 2=release
    private readonly bool[] _holdZero = new bool[3];    // freeze at 0

    // ADSR rate table: 1 MHz clocks per envelope step (6581 datasheet).
    private static readonly int[] RatePeriods = {
         9,   32,   63,   95,  149,  220,  267,  313,
       392,  977, 1954, 3126, 3907, 11720, 19532, 31251,
    };

    // --- Filter state (TPT state-variable filter) ---
    private double _svfIc1, _svfIc2; // delayless-feedback states
    private int _tapLp, _tapBp, _tapHp;
    private int _filterSampleRate = 44100;

    // Combined-waveform 6581 pulldown (approximate; the 8580 is ~clean AND).
    private const int TriSawPulldownNum = 3; // tri+saw: x3/8
    private const int TriSawPulldownDen = 3; // (shift)
    private const int PulseSawShift = 2;     // pulse+saw: x1/4

    public Sid()
    {
        for (int i = 0; i < 3; i++)
        {
            _lfsr[i] = 0x7FFFF8;
            _envState[i] = 2;      // release...
            _holdZero[i] = true;   // ...held at zero = silent
            _expPeriod[i] = 1;
        }
    }

    public byte ReadRegister(int address)
    {
        int reg = address & 0x1F;
        // SID registers are write-only except pots and osc3/env3.
        if (reg == 0x1B) return 0; // pot X (not connected)
        if (reg == 0x1C) return 0; // pot Y (not connected)
        if (reg == 0x1D) return (byte)(_phase[2] >> 16); // osc3: top 8 bits of voice 3 phase
        if (reg == 0x1E) return _env[2];                 // env3
        return 0;
    }

    public void WriteRegister(int address, byte value)
    {
        int reg = address & 0x1F;
        if (reg > 0x18) return; // $D419-$D41F: read-only/NC
        byte old = _regs[reg];
        _regs[reg] = value;

        // Control registers are 4, 11, 18 (voice*7+4). Gate bit edge drives ADSR.
        if ((reg % 7) == 4)
        {
            int v = reg / 7;
            bool gate = (value & 0x01) != 0;
            bool oldGate = (old & 0x01) != 0;
            if (gate && !oldGate)
            {
                // Gate on: attack from the current level. The rate counter is
                // deliberately NOT reset (real chip behavior).
                _envState[v] = 0;
                _holdZero[v] = false;
            }
            else if (!gate && oldGate)
            {
                _envState[v] = 2;
                _holdZero[v] = false;
            }
        }
    }

    /// <summary>
    /// Renders <paramref name="count"/> 16-bit PCM samples at
    /// <paramref name="sampleRate"/> Hz into <paramref name="buffer"/>.
    /// </summary>
    public void RenderSamples(short[] buffer, int count, int sampleRate)
    {
        double clocksPerSample = (double)ClockHz / sampleRate;
        double clockFrac = 0;
        _filterSampleRate = sampleRate;

        for (int s = 0; s < count; s++)
        {
            clockFrac += clocksPerSample;
            int clocks = (int)clockFrac;
            clockFrac -= clocks;
            for (int c = 0; c < clocks; c++) Clock();

            int filtIn = 0, direct = 0;
            byte filtSel = _regs[0x17];
            bool v3off = (_regs[0x18] & 0x80) != 0;
            for (int v = 0; v < 3; v++)
            {
                if (v == 2 && v3off) continue; // voice 3 disconnected from output
                int wave = GenerateWave(v);    // -2048..2047
                int sv = (wave * _env[v]) >> 8;
                if ((filtSel & (1 << v)) != 0) filtIn += sv;
                else direct += sv;
            }

            int fcutReg = _regs[0x15] | ((_regs[0x16] & 0x07) << 8); // 11-bit cutoff
            int res = (_regs[0x16] >> 4) & 0x0F;
            FilterStep(filtIn, CutoffHz(fcutReg), res);

            int mode = _regs[0x18];
            int tap = 0;
            if ((mode & 0x10) != 0) tap += _tapLp;
            if ((mode & 0x20) != 0) tap += _tapBp;
            if ((mode & 0x40) != 0) tap += _tapHp;
            // LP+HP selected together = notch (falls out of the tap sum).

            int mix = direct + tap;

            // Volume: 4-bit master DAC.
            int vol = mode & 0x0F;
            mix = (mix * vol) >> 4;

            mix = Math.Clamp(mix << 4, -32768, 32767);
            buffer[s] = (short)mix;
        }
    }

    private void Clock()
    {
        for (int v = 0; v < 3; v++) ClockVoice(v);

        // Hard sync: a voice with the sync bit set resets its accumulator on
        // the 1->0 transition of the previous voice's accumulator MSB
        // (voice chain 1<-3, 2<-1, 3<-2).
        for (int v = 0; v < 3; v++)
        {
            if ((_regs[v * 7 + 4] & 0x02) == 0) continue;
            int m = (v + 2) % 3;
            bool msbNow = (_phase[m] & 0x800000) != 0;
            if (_prevMsb[m] && !msbNow) _phase[v] = 0;
        }
    }

    private void ClockVoice(int v)
    {
        int baseReg = v * 7;
        ushort freq = (ushort)(_regs[baseReg] | (_regs[baseReg + 1] << 8));
        bool test = (_regs[baseReg + 4] & 0x08) != 0;

        if (test)
        {
            // TEST: oscillator forced to zero phase, noise LFSR reseeded+held.
            _phase[v] = 0;
            _lfsr[v] = 0x7FFFF8;
            _prevMsb[v] = false;
            _prevBit19[v] = false;
        }
        else
        {
            _prevMsb[v] = (_phase[v] & 0x800000) != 0;
            _prevBit19[v] = (_phase[v] & 0x080000) != 0;
            _phase[v] = (_phase[v] + freq) & 0xFFFFFF;

            // Noise LFSR clocked on rising edge of accumulator bit 19
            // (pitch-tracking noise).
            if (((_phase[v] & 0x080000) != 0) && !_prevBit19[v])
            {
                uint bit = ((_lfsr[v] >> 22) ^ (_lfsr[v] >> 17)) & 1;
                _lfsr[v] = ((_lfsr[v] << 1) | bit) & 0x7FFFFF;
            }
        }

        ClockEnvelope(v);
    }

    private void ClockEnvelope(int v)
    {
        int baseReg = v * 7;
        int state = _envState[v];
        int rate = state switch
        {
            0 => _regs[baseReg + 5] >> 4,   // attack
            1 => _regs[baseReg + 5] & 0x0F, // decay
            _ => _regs[baseReg + 6] & 0x0F, // release
        };

        // Free-running 16-bit rate counter: wraps at 65536 and is never
        // reset by gate changes or register writes (ADSR delay quirk).
        if (++_rateCounter[v] != RatePeriods[rate]) return;
        _rateCounter[v] = 0;

        if (state == 0)
        {
            // ATTACK: linear rise; at max, move to decay/sustain.
            if (_env[v] < 255)
            {
                _env[v]++;
                if (_env[v] == 255) _envState[v] = 1;
            }
            else _envState[v] = 1;
            return;
        }

        // DECAY/SUSTAIN and RELEASE: piecewise-exponential fall.
        if (_holdZero[v]) return;
        if (++_expCounter[v] < _expPeriod[v]) return;
        _expCounter[v] = 0;

        if (state == 1)
        {
            int sustain = _regs[baseReg + 6] >> 4;
            // Sustain holds when the top 4 bits match the sustain register.
            if ((_env[v] & 0xF0) == (sustain << 4)) return;
        }
        if (_env[v] > 0) _env[v]--;
        else _holdZero[v] = true;
        _expPeriod[v] = ExpPeriod(_env[v]);
    }

    /// <summary>Exponential divider from the envelope counter value.</summary>
    private static byte ExpPeriod(int env) =>
        env > 93 ? (byte)1 :
        env > 54 ? (byte)2 :
        env > 26 ? (byte)4 :
        env > 14 ? (byte)8 :
        env > 6 ? (byte)16 :
        env > 0 ? (byte)30 : (byte)1;

    /// <summary>
    /// 11-bit filter cutoff register -> Hz. The 6581's real curve is strongly
    /// nonlinear (measured by Lankila); approximated here as exponential over
    /// the datasheet 30 Hz .. 12 kHz range.
    /// </summary>
    private static double CutoffHz(int reg) =>
        30.0 * Math.Pow(400.0, reg / 2047.0);

    private int GenerateWave(int v)
    {
        int baseReg = v * 7;
        byte ctrl = _regs[baseReg + 4];
        uint phase = _phase[v];
        uint acc12 = (phase >> 12) & 0xFFF;

        int tri = 0, saw = 0, pulse = 0, noise = 0;
        bool hasTri = false, hasSaw = false, hasPulse = false, hasNoise = false;

        if ((ctrl & 0x10) != 0)
        {
            hasTri = true;
            // Ring modulation substitutes the previous voice's accumulator MSB
            // into the triangle fold (XOR) — it affects nothing else.
            int fold = (int)((phase >> 23) & 1);
            if ((ctrl & 0x04) != 0)
                fold ^= (int)((_phase[(v + 2) % 3] >> 23) & 1);
            tri = (int)(acc12 ^ (fold != 0 ? 0xFFFu : 0u));
        }
        if ((ctrl & 0x20) != 0) { hasSaw = true; saw = (int)acc12; }
        if ((ctrl & 0x40) != 0)
        {
            hasPulse = true;
            ushort pw = (ushort)(_regs[baseReg + 2] | ((_regs[baseReg + 3] & 0x0F) << 8));
            pulse = acc12 >= pw ? 0xFFF : 0x000;
        }
        if ((ctrl & 0x80) != 0) { hasNoise = true; noise = (int)((_lfsr[v] >> 11) & 0xFFF); }

        int wave;
        int selected = (hasTri ? 1 : 0) + (hasSaw ? 1 : 0) + (hasPulse ? 1 : 0) + (hasNoise ? 1 : 0);
        if (selected == 0)
        {
            wave = 0x800; // no waveform: floating DAC sits near mid-scale
        }
        else if (selected == 1)
        {
            wave = hasTri ? tri : hasSaw ? saw : hasPulse ? pulse : noise;
        }
        else
        {
            // Combined waveforms: wired-AND of the selected outputs (never sum).
            wave = 0xFFF;
            if (hasTri) wave &= tri;
            if (hasSaw) wave &= saw;
            if (hasPulse) wave &= pulse;
            if (hasNoise) wave &= noise;
            // 6581 selector pulldown attenuates some combinations strongly
            // (the 8580 is close to a clean AND). Approximate factors.
            if (hasTri && hasSaw) wave = (wave * TriSawPulldownNum) >> TriSawPulldownDen;
            else if (hasPulse && hasSaw) wave >>= PulseSawShift;
        }
        return wave - 2048; // center: -2048..2047
    }

    /// <summary>
    /// One sample through the topology-preserving-transform state-variable
    /// filter (stable for fc below Nyquist at any resonance). Sets the
    /// LP/BP/HP output taps. BP/HP taps get a gentle tanh saturation for a
    /// taste of 6581 NMOS-op-amp grit (transparent at normal levels).
    /// </summary>
    private void FilterStep(int input, double fc, int res)
    {
        double fs = _filterSampleRate;
        if (fc > fs * 0.49) fc = fs * 0.49;
        double g = Math.Tan(Math.PI * fc / fs);
        double q = 0.5 + res * 0.5; // resonance 0..15 -> Q 0.5..8 (plausible 6581-ish)
        double k = 1.0 / q;
        double a1 = 1.0 / (1.0 + g * (g + k));
        double a2 = g * a1;
        double a3 = g * a2;

        double v0 = input;
        double v3 = v0 - _svfIc2;
        double v1 = a1 * _svfIc1 + a2 * v3;
        double v2 = _svfIc2 + a2 * _svfIc1 + a3 * v3;
        _svfIc1 = 2.0 * v1 - _svfIc1;
        _svfIc2 = 2.0 * v2 - _svfIc2;

        _tapLp = (int)v2;
        // 6581 grit on the BP/HP path (NMOS inverters as op-amps distort).
        double v1sat = 6000.0 * Math.Tanh(v1 / 6000.0);
        _tapBp = (int)v1sat;
        _tapHp = (int)(v0 - k * v1sat - v2);
    }

    // --- Internal test hooks ---
    internal void ClockChip() => Clock();
    internal int EnvelopeLevel(int v) => _env[v];
    internal int WaveOutput(int v) => GenerateWave(v);
    internal uint Phase(int v) => _phase[v];
    internal uint NoiseLfsr(int v) => _lfsr[v];
    internal void SetPhase(int v, uint phase) => _phase[v] = phase & 0xFFFFFF;
}
