using System.IO;
using C64.Core.Cpu;
using C64.Core.Disk;
using C64.Core.Memory;
using C64.Core.Vic;

namespace C64Sharp;

/// <summary>
/// The C64 machine: bus, CPU, VIC, SID, CIAs. Runs the emulation on a
/// dedicated thread, one PAL frame at a time, and exposes the rendered
/// framebuffer and audio for the UI.
/// </summary>
public sealed class Emulator : IDisposable
{
    private const long CyclesPerFrame = 63L * 312; // PAL
    private const int SampleRate = 44100;
    private const int SamplesPerFrame = SampleRate / 50; // ~882

    private static readonly string DebugLogPath = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "c64-debug.log");
    public static void DebugLog(string msg)
    {
        try
        {
            File.AppendAllText(DebugLogPath,
                $"{DateTime.Now:HH:mm:ss.fff} [{Thread.CurrentThread.ManagedThreadId}] {msg}\n");
        }
        catch { }
    }

    private readonly C64Bus _bus;
    private readonly Cpu6510 _cpu;
    private readonly HleDrive _hleDrive = new();
    private readonly Thread _thread;
    private volatile bool _running;
    private volatile bool _resetRequested;
    private readonly object _frameLock = new();
    private byte[] _framebuffer = new byte[VicIi.FrameWidth * VicIi.FrameHeight];
    private readonly Queue<short[]> _audioQueue = new();
    private bool _enableAudio = true;
    public void SetAudioEnabled(bool enabled) => _enableAudio = enabled;
    private readonly AutoResetEvent _frameReady = new(false);

    public event Action? FrameReady;

    public Emulator(byte[] basic, byte[] kernal, byte[] chargen, byte[]? dosRom = null)
    {
        _bus = new C64Bus(basic, kernal, chargen, dosRom);
        _cpu = new Cpu6510(_bus);
        _bus.Vic.GetCpuCycles = () => _cpu.TotalCycles;
        _bus.GetCpuCycles = () => _cpu.TotalCycles;
        _cpu.IrqLine = _bus.GetIrq;
        // HLE drive: intercept Kernal LOAD. Hook the vector ($FFD5),
        // the vector jump ($F49E/$F4A2), and the implementation ($F4A5).
        // Debug: count hook hits.
        _cpu.PcHook = (pc) =>
        {
            if (pc == 0xFFD5 || pc == 0xF49E || pc == 0xF4A2 || pc == 0xF4A5)
            {
                System.Threading.Interlocked.Increment(ref _hookHits);
                return _hleDrive.TryHandleLoad(_cpu, _bus);
            }
            return false;
        };
        _thread = new Thread(RunLoop) { IsBackground = true, Name = "C64" };
    }

    public C64Bus Bus => _bus;
    public HleDrive HleDrive => _hleDrive;
    private byte _joyState = 0xFF;
    /// <summary>Update joystick Port 2 (bits 0-4 active-low: Up,Down,Left,Right,Fire).</summary>
    public void SetJoystick(byte state)
    {
        _joyState = state;
        _bus.SetJoystickPort2(state);
    }
    public long HookHits => _hookHits;
    public byte LastDevNum => _hleDrive.LastDevNum;
    private long _hookHits;

    /// <summary>Request a reset; the emulator thread applies it at the next frame.</summary>
    public void RequestReset() => _resetRequested = true;

    /// <summary>Simulate the RESTORE key: pulses the CPU's NMI line like real hardware.</summary>
    public void PressRestore() => _cpu.RequestNmi();

    public void Start()
    {
        try { File.WriteAllText(DebugLogPath, $"--- C64 Emulator {DateTime.Now} ---\n"); } catch { }
        DebugLog("Start: CPU reset");
        _cpu.Reset();
        _bus.Drive?.Reset();
        _running = true;
        _thread.Start();
        DebugLog("Start: thread started");
    }

    public void Stop()
    {
        _running = false;
        _thread.Join(1000);
    }

    /// <summary>Latest rendered framebuffer (palette indices). Copy while holding FrameLock.</summary>
    public object FrameLock => _frameLock;

    public byte[] Framebuffer
    {
        get { lock (_frameLock) return (byte[])_framebuffer.Clone(); }
    }

    /// <summary>Dequeue pending audio samples (16-bit PCM), or null if empty.</summary>
    public short[]? DequeueAudio()
    {
        lock (_audioQueue)
            return _audioQueue.Count > 0 ? _audioQueue.Dequeue() : null;
    }

    private long _framesEmulated;
    private long _lastFpsCheck;
    private double _emulationFps;
    public double EmulationFps => _emulationFps;

    private void RunLoop()
    {
        DebugLog("RunLoop: entered");
        try
        {
            var frame = new byte[VicIi.FrameWidth * VicIi.FrameHeight];
            var audio = new short[SamplesPerFrame];
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int haltedFrames = 0;
            _lastFpsCheck = sw.ElapsedMilliseconds;
            DebugLog("RunLoop: buffers allocated, starting loop");

        while (_running)
        {
            try
            {
            // Apply a reset request from the UI thread.
            if (_resetRequested)
            {
                DebugLog("RunLoop: hard reset requested");
                _bus.HardReset();
                _cpu.Reset();
                _resetRequested = false;
                DebugLog("RunLoop: hard reset done");
            }

            long frameStartTicks = sw.ElapsedTicks;

            // Run one PAL frame.
            _cpu.RunForCycles(CyclesPerFrame);

            // Clock the 1541 drive CPU in lockstep (same 1MHz clock).
            _bus.Drive?.RunForCycles(CyclesPerFrame);

            // If the CPU jammed (illegal opcode), reset it instead of spinning.
            if (_cpu.Halted)
            {
                if (++haltedFrames > 10)
                {
                    _cpu.Reset();
                    haltedFrames = 0;
                }
            }
            else haltedFrames = 0;

            // Render video.
            _bus.Vic.RenderFrame(frame);
            lock (_frameLock)
            {
                var tmp = _framebuffer;
                _framebuffer = frame;
                frame = tmp;
            }

            // Render audio (skip if disabled).
            if (_enableAudio)
            {
                try
                {
                    _bus.Sid.RenderSamples(audio, audio.Length, SampleRate);
                    lock (_audioQueue)
                    {
                        // Drop if we're falling behind (max 4 frames buffered).
                        if (_audioQueue.Count < 4)
                            _audioQueue.Enqueue((short[])audio.Clone());
                    }
                }
                catch (Exception ex)
                {
                    DebugLog($"Audio render failed: {ex.GetType().Name}: {ex.Message}");
                }
            }

            FrameReady?.Invoke();

            // Track emulation speed (frames per second).
            _framesEmulated++;
            long nowMs = sw.ElapsedMilliseconds;
            if (nowMs - _lastFpsCheck >= 1000)
            {
                _emulationFps = _framesEmulated * 1000.0 / (nowMs - _lastFpsCheck);
                _framesEmulated = 0;
                _lastFpsCheck = nowMs;
            }

            // Pace to 50fps.
            // Windows Sleep quantizes to 15.6ms, so never sleep when less than
            // 16ms remains — spin instead for precise timing.
            long frameTicks = System.Diagnostics.Stopwatch.Frequency / 50;
            long elapsedTicks = sw.ElapsedTicks - frameStartTicks;
            long remainingTicks = frameTicks - elapsedTicks;
            if (remainingTicks > 0)
            {
                long sixteenMs = System.Diagnostics.Stopwatch.Frequency * 16 / 1000;
                if (remainingTicks > sixteenMs)
                {
                    int sleepMs = (int)((remainingTicks - sixteenMs) * 1000 / System.Diagnostics.Stopwatch.Frequency);
                    if (sleepMs > 0)
                        Thread.Sleep(sleepMs);
                }
                // Spin for the remainder (max 16ms) for precise timing.
                long target = frameStartTicks + frameTicks;
                while (sw.ElapsedTicks < target) { }
            }
            }
            catch (Exception ex)
            {
                try
                {
                    string tempDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "temp");
                    Directory.CreateDirectory(tempDir);
                    File.AppendAllText(Path.Combine(tempDir, "crash.log"), $"Emulation crashed: {ex}\n");
                }
                catch { }
                // Reset the CPU to a known-good state instead of spinning on garbage.
                try
                {
                    _cpu.Reset();
                    _bus.Write(0x0000, 0x2F);
                    _bus.Write(0x0001, 0x37);
                }
                catch (Exception ex2)
                {
                    DebugLog($"RunLoop halted-frame reset failed: {ex2}");
                }
                Thread.Sleep(100);
            }
        }
        } // Close outer try
        catch (Exception ex)
        {
            DebugLog($"RunLoop FATAL: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }
        DebugLog("RunLoop: exited");
    }

    public void Dispose()
    {
        Stop();
        _frameReady.Dispose();
    }
}
