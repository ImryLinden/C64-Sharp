using System.Runtime.InteropServices;

namespace C64Emu;

/// <summary>
/// Streams 16-bit PCM audio via winmm.dll waveOut. No NuGet dependencies.
/// Buffers are freed when waveOut signals completion (WOM_DONE).
/// </summary>
public sealed class AudioPlayer : IDisposable
{
    private const int SampleRate = 44100;
    // No callback — we manage buffer lifetime manually to avoid native callback crashes.

    private IntPtr _hWaveOut;
    private bool _disposed;
    private readonly Queue<(IntPtr hdrPtr, IntPtr dataPtr)> _pending = new();
    private readonly object _lock = new();

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEFORMATEX
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEHDR
    {
        public IntPtr lpData;
        public uint dwBufferLength;
        public uint dwBytesRecorded;
        public IntPtr dwUser;
        public uint dwFlags;
        public uint dwLoops;
        public IntPtr lpNext;
        public IntPtr reserved;
    }

    [DllImport("winmm.dll")]
    private static extern int waveOutOpen(out IntPtr phwo, uint uDeviceID,
        ref WAVEFORMATEX pwfx, IntPtr dwCallback, IntPtr dwInstance, uint fdwOpen);

    [DllImport("winmm.dll")]
    private static extern int waveOutPrepareHeader(IntPtr hwo, IntPtr pwh, uint cbwh);

    [DllImport("winmm.dll")]
    private static extern int waveOutWrite(IntPtr hwo, IntPtr pwh, uint cbwh);

    [DllImport("winmm.dll")]
    private static extern int waveOutUnprepareHeader(IntPtr hwo, IntPtr pwh, uint cbwh);

    [DllImport("winmm.dll")]
    private static extern int waveOutClose(IntPtr hwo);

    public AudioPlayer()
    {
        var fmt = new WAVEFORMATEX
        {
            wFormatTag = 1, // PCM
            nChannels = 1,
            nSamplesPerSec = SampleRate,
            nAvgBytesPerSec = SampleRate * 2,
            nBlockAlign = 2,
            wBitsPerSample = 16,
        };
        // No callback (NULL) — fire and forget, we free old buffers manually.
        int rc = waveOutOpen(out _hWaveOut, 0xFFFFFFFF, ref fmt,
            IntPtr.Zero, IntPtr.Zero, 0);
        if (rc != 0)
            throw new InvalidOperationException($"waveOutOpen failed: {rc}");
    }

    /// <summary>Queue samples for playback. Buffers are freed after a delay (no callback).</summary>
    public void Play(short[] samples)
    {
        if (_disposed || samples == null || samples.Length == 0) return;
        try
        {
            IntPtr hdrPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WAVEHDR>());
            IntPtr dataPtr = Marshal.AllocHGlobal(samples.Length * 2);
            Marshal.Copy(samples, 0, dataPtr, samples.Length);
            var hdr = new WAVEHDR
            {
                lpData = dataPtr,
                dwBufferLength = (uint)(samples.Length * 2),
            };
            Marshal.StructureToPtr(hdr, hdrPtr, false);
            int rc1 = waveOutPrepareHeader(_hWaveOut, hdrPtr, (uint)Marshal.SizeOf<WAVEHDR>());
            if (rc1 != 0)
            {
                Marshal.FreeHGlobal(dataPtr);
                Marshal.FreeHGlobal(hdrPtr);
                return;
            }
            int rc2 = waveOutWrite(_hWaveOut, hdrPtr, (uint)Marshal.SizeOf<WAVEHDR>());
            if (rc2 != 0)
            {
                waveOutUnprepareHeader(_hWaveOut, hdrPtr, (uint)Marshal.SizeOf<WAVEHDR>());
                Marshal.FreeHGlobal(dataPtr);
                Marshal.FreeHGlobal(hdrPtr);
                return;
            }
            // Queue for later cleanup; free the oldest if we have too many.
            lock (_lock)
            {
                _pending.Enqueue((hdrPtr, dataPtr));
                // Keep max 20 buffers (~400ms of audio); free oldest.
                while (_pending.Count > 20)
                {
                    var (oldHdr, oldData) = _pending.Dequeue();
                    try
                    {
                        waveOutUnprepareHeader(_hWaveOut, oldHdr, (uint)Marshal.SizeOf<WAVEHDR>());
                    }
                    catch { }
                    Marshal.FreeHGlobal(oldData);
                    Marshal.FreeHGlobal(oldHdr);
                }
            }
        }
        catch
        {
            // Audio playback failed; skip.
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_lock)
        {
            while (_pending.Count > 0)
            {
                var (hdrPtr, dataPtr) = _pending.Dequeue();
                try { waveOutUnprepareHeader(_hWaveOut, hdrPtr, (uint)Marshal.SizeOf<WAVEHDR>()); }
                catch { }
                Marshal.FreeHGlobal(dataPtr);
                Marshal.FreeHGlobal(hdrPtr);
            }
        }
        waveOutClose(_hWaveOut);
    }
}
