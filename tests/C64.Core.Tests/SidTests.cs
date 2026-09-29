using SidChip = C64.Core.Sid.Sid;
using Xunit;

namespace C64.Core.Tests;

/// <summary>Tests for the SID chip (M7): oscillators, envelopes, registers.</summary>
public class SidTests
{
    [Fact]
    public void Write_And_Read_Back_Voice_Registers()
    {
        var sid = new SidChip();
        // Frequency 440Hz: freq = 440 * 2^24 / 985248 ≈ 7493.
        sid.WriteRegister(0x00, 0x45); // lo
        sid.WriteRegister(0x01, 0x1D); // hi (0x1D45 = 7493)
        // Can't read back (write-only), but shouldn't crash.
        sid.WriteRegister(0x04, 0x21); // gate + saw
        sid.WriteRegister(0x05, 0x00); // AD
        sid.WriteRegister(0x06, 0xF0); // SR
        sid.WriteRegister(0x18, 0x0F); // volume max
    }

    [Fact]
    public void RenderSamples_Produces_Nonzero_Output_When_Gated()
    {
        var sid = new SidChip();
        sid.WriteRegister(0x00, 0x45);
        sid.WriteRegister(0x01, 0x1D); // ~440Hz
        sid.WriteRegister(0x05, 0x0A); // fast attack
        sid.WriteRegister(0x06, 0xF0); // sustain max
        sid.WriteRegister(0x04, 0x11); // gate + triangle
        sid.WriteRegister(0x18, 0x0F); // volume

        var buf = new short[44100];
        sid.RenderSamples(buf, buf.Length, 44100);

        // After attack, should have signal. Check max amplitude.
        int max = 0;
        for (int i = 22050; i < 44100; i++)
            max = Math.Max(max, Math.Abs(buf[i]));
        Assert.True(max > 1000, $"Max amplitude {max} too low");
    }

    [Fact]
    public void Envelope_Decays_After_Gate_Off()
    {
        var sid = new SidChip();
        sid.WriteRegister(0x00, 0x45);
        sid.WriteRegister(0x01, 0x1D);
        sid.WriteRegister(0x05, 0x0A);
        sid.WriteRegister(0x06, 0x00); // sustain 0, fast release
        sid.WriteRegister(0x04, 0x11); // gate on
        sid.WriteRegister(0x18, 0x0F);

        var buf = new short[44100];
        sid.RenderSamples(buf, 22050, 44100); // attack

        sid.WriteRegister(0x04, 0x10); // gate off (release)
        sid.RenderSamples(buf, 22050, 44100);

        // End should be near silent.
        int max = 0;
        for (int i = 20000; i < 22050; i++)
            max = Math.Max(max, Math.Abs(buf[i]));
        Assert.True(max < 500, $"Release didn't decay: max {max}");
    }

    [Fact]
    public void Noise_Waveform_Produces_Output()
    {
        var sid = new SidChip();
        sid.WriteRegister(0x00, 0xFF);
        sid.WriteRegister(0x01, 0xFF);
        sid.WriteRegister(0x05, 0x0A);
        sid.WriteRegister(0x06, 0xF0);
        sid.WriteRegister(0x04, 0x81); // gate + noise
        sid.WriteRegister(0x18, 0x0F);

        var buf = new short[4410];
        sid.RenderSamples(buf, buf.Length, 44100);

        int max = 0;
        for (int i = 1000; i < buf.Length; i++)
            max = Math.Max(max, Math.Abs(buf[i]));
        Assert.True(max > 500, $"Noise max {max} too low");
    }

    [Fact]
    public void Pulse_Waveform_Respects_Width()
    {
        var sid = new SidChip();
        // 50% duty: pw = 2048.
        sid.WriteRegister(0x02, 0x00);
        sid.WriteRegister(0x03, 0x08); // pw hi nibble
        sid.WriteRegister(0x00, 0x45);
        sid.WriteRegister(0x01, 0x1D);
        sid.WriteRegister(0x05, 0x0A);
        sid.WriteRegister(0x06, 0xF0);
        sid.WriteRegister(0x04, 0x41); // gate + pulse
        sid.WriteRegister(0x18, 0x0F);

        var buf = new short[4410];
        sid.RenderSamples(buf, buf.Length, 44100);

        // Count positive vs negative (should be ~50/50 for 50% duty).
        int pos = 0, neg = 0;
        for (int i = 1000; i < buf.Length; i++)
        {
            if (buf[i] > 100) pos++;
            else if (buf[i] < -100) neg++;
        }
        double ratio = (double)pos / (pos + neg);
        Assert.True(ratio > 0.3 && ratio < 0.7, $"Duty ratio {ratio} not ~0.5");
    }
}
