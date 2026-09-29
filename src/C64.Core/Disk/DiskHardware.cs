using System;

namespace C64.Core.Disk;

/// <summary>
/// Emulates the 1541's disk hardware: stepper motor (track position) and
/// GCR data read. Feeds pre-encoded GCR tracks to the DOS via VIA2.
/// </summary>
public sealed class DiskHardware
{
    private readonly GcrTrackBuilder _builder;
    private byte[][]? _tracks; // 1-35, GCR bytes
    private int _currentTrack = 18; // DOS starts at 18 (directory)
    private int _readPos;
    private int _stepperPhase;

    public DiskHardware(D64Image d64)
    {
        _builder = new GcrTrackBuilder(d64);
    }

    public void Mount()
    {
        _tracks = new byte[36][];
        for (int t = 1; t <= 35; t++)
            _tracks[t] = _builder.BuildTrack(t);
        _currentTrack = 18;
        _readPos = 0;
    }

    /// <summary>Called when DOS writes VIA2 PB (stepper/motor control).</summary>
    public void StepperWrite(byte pb)
    {
        int phase = pb & 0x03;
        // Detect step: phase changed.
        if (phase != _stepperPhase)
        {
            // Determine direction from phase sequence.
            // 00->01->11->10->00 = one direction, reverse = other.
            int diff = (phase - _stepperPhase) & 0x03;
            if (diff == 1 || diff == 3)
            {
                // Step (direction depends on sequence).
                // Simplified: assume outward/inward based on DOS behavior.
                // The DOS typically steps inward (toward higher tracks?) 
                // Actually track 1 is outer, 35 is inner.
                _currentTrack += (diff == 1) ? 1 : -1;
                _currentTrack = Math.Clamp(_currentTrack, 1, 35);
                _readPos = 0;
            }
            _stepperPhase = phase;
        }
        // Bit 2: motor on/off (ignore for now).
    }

    /// <summary>Called when DOS reads VIA2 PA (GCR data).</summary>
    public byte ReadGcr()
    {
        if (_tracks == null) return 0xFF;
        byte[] track = _tracks[_currentTrack];
        byte b = track[_readPos];
        _readPos = (_readPos + 1) % track.Length;
        return b;
    }

    public int CurrentTrack => _currentTrack;
}
