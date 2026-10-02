using System.Collections.Concurrent;
using NAudio.Wave;
using NAudio.CoreAudioApi;

namespace RadCord.Core;

/// <summary>
/// WASAPI audio — mirrors audio.py exactly.
/// • WasapiCapture  → mic input with VAD (RMS threshold)
/// • WasapiOut      → multi-peer jitter buffer mixing
/// • WasapiLoopbackCapture → system audio for screen share (replaces Python C# helper EXE)
/// </summary>
public class AudioManager : IDisposable
{
    public bool InVoice   { get; set; }
    public bool Muted     { get; set; }
    public bool Deafened  { get; set; }
    public bool MicTestActive { get; set; }
    public float VadThreshold { get; set; } = Protocol.AudioRmsThreshold;

    private float _masterVolume = 1.0f;
    private float _peersVolume  = 1.0f;
    private float _streamVolume = 1.0f;
    private float _sysAudioVol = 1.0f;
    public float MasterVolume     { get => _masterVolume;  set => _masterVolume  = value; }
    public float PeersVolume      { get => _peersVolume;   set => _peersVolume   = value; }
    public float StreamVolume     { get => _streamVolume;  set => _streamVolume  = value; }
    public float SystemAudioVolume { get => _sysAudioVol;  set => _sysAudioVol   = value; }

    // Callbacks
    public Func<byte[], Task>? OnAudioFrame     { get; set; }  // mic → send
    public Action<bool>?       OnSpeakingChange { get; set; }  // VAD result
    public Action<float>?      OnRmsLevel       { get; set; }  // meter
    public Func<byte[], Task>? OnStreamAudioReady { get; set; } // loopback → send

    private const int JitterMax = 8;
    private readonly ConcurrentDictionary<string, ConcurrentQueue<short[]>> _peerQueues  = new();
    private readonly ConcurrentDictionary<string, float>                    _peerVolumes = new();
    private readonly ConcurrentDictionary<string, ConcurrentQueue<short[]>> _streamQueues  = new();
    private readonly ConcurrentDictionary<string, float>                    _streamVolumes = new();
    private readonly HashSet<string> _localMutes = new();
    private readonly object          _muteLock   = new();

    // Mic loopback for test
    private readonly ConcurrentQueue<short[]> _micTestQueue  = new();
    private readonly List<short>              _micTestBuffer = new();
    private float                             _smoothedRms;

    private WasapiCapture?         _capture;
    private WasapiOut?             _output;
    private WasapiLoopbackCapture?  _loopback;
    private ProcessLoopbackCapture? _processLoopback;
    private WaveFormat?             _loopbackFormat;

    private bool    _running;
    private bool    _testCaptureStarted;
    private volatile int _sessionId; // incremented on every StartStreams/Stop to kill stale loops
    private bool _speaking;
    private int  _speakHold;
    private int  _captureHold;
    private float _transGain;
    private float _envelopeGain;
    private const float FadeRelease = 0.92f;

    private string? _inputDeviceId;
    private string? _outputDeviceId;

    public void SetInputDevice(string? deviceId)  => _inputDeviceId  = deviceId;
    public void SetOutputDevice(string? deviceId) => _outputDeviceId = deviceId;
    public bool IsCapturing => _capture != null;

    public void EnsureCapture()
    {
        _testCaptureStarted = true;
        if (_capture != null) return;
        StartCapture();
        if (!_running)
        {
            _sessionId++;
            _running = true;
            StartPlayback();
            _ = Task.Run(CaptureLoopAsync);
        }
    }

    public void StopTestCapture()
    {
        if (!_testCaptureStarted) return;
        _testCaptureStarted = false;
        MicTestActive = false;
        if (InVoice) return;
        _sessionId++;  // kill running loops
        _running = false;
        try { _capture?.StopRecording(); _capture?.Dispose(); _capture = null; } catch { }
        try { _output?.Stop(); _output?.Dispose(); _output = null; } catch { }
    }


    public void StartStreams()
    {
        _sessionId++;  // kill any loops started by EnsureCapture or previous session
        _running = true;
        StartCapture();
        StartPlayback();
        _ = Task.Run(CaptureLoopAsync);
    }

    public void StopStreams()
    {
        _sessionId++;  // kill loops
        _running = false;
        try { _capture?.StopRecording();  _capture?.Dispose();  _capture  = null; } catch { }
        try { _output?.Stop();            _output?.Dispose();   _output   = null; } catch { }
        try { _loopback?.StopRecording();        _loopback?.Dispose();        _loopback        = null; } catch { }
        try { _processLoopback?.StopRecording(); _processLoopback?.Dispose(); _processLoopback = null; } catch { }
    }

    private void StartCapture()
    {
        // Stop any existing capture to avoid double-capture
        try { _capture?.StopRecording(); _capture?.Dispose(); _capture = null; } catch { }
        try
        {
            MMDevice? dev = null;
            if (_inputDeviceId != null)
            {
                using var en = new MMDeviceEnumerator();
                try { dev = en.GetDevice(_inputDeviceId); } catch { }
            }

            _capture = dev != null ? new WasapiCapture(dev) : new WasapiCapture();
            _capture.DataAvailable    += CaptureDataAvailable;
            _capture.RecordingStopped += OnCaptureStopped;
            _capture.StartRecording();
            Log("Capture started");
        }
        catch (Exception ex)
        {
            Log($"Capture start failed: {ex.Message}");
        }
    }

    private void StartPlayback()
    {
        try
        {
            // AudioMixProvider is called directly by WasapiOut — no timer loop, no underrun
            using var en = new MMDeviceEnumerator();
            MMDevice dev;
            try
            {
                dev = _outputDeviceId != null
                    ? en.GetDevice(_outputDeviceId)
                    : en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            }
            catch { dev = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia); }

            _output = new WasapiOut(dev, AudioClientShareMode.Shared, false, 200);
            _output.Init(new AudioMixProvider(this));
            _output.Play();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Audio] Playback start failed: {ex.Message}");
        }
    }

    private void OnCaptureStopped(object? sender, StoppedEventArgs e)
    {
        Log($"Capture stopped unexpectedly: {e.Exception?.Message ?? "no error"}");
        if (!_running) return;
        // Restart after short delay to avoid tight loop on persistent failure
        Task.Delay(500).ContinueWith(_ =>
        {
            if (_running) StartCapture();
        });
    }

    private static void Log(string msg)
    {
        try { System.IO.File.AppendAllText("audio.log", $"[{DateTime.Now:HH:mm:ss}] {msg}\n"); } catch { }
        System.Diagnostics.Debug.WriteLine($"[Audio] {msg}");
    }


    private void CaptureDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0 || _capture == null) return;

        // Convert native format → int16 mono 48kHz
        var fmt    = _capture.WaveFormat;
        var mono16 = ConvertToInt16Mono48k(e.Buffer, e.BytesRecorded, fmt);
        if (mono16.Length == 0) return;

        // RMS → smooth over ~80 ms to eliminate VAD flicker
        double sumSq = 0;
        foreach (var s in mono16) sumSq += (double)s * s;
        var rms = (float)Math.Sqrt(sumSq / mono16.Length);
        // Asymmetric smoothing: fast attack, fast decay for responsive meter
        _smoothedRms = rms > _smoothedRms
            ? _smoothedRms * 0.4f + rms * 0.6f   // fast attack
            : _smoothedRms * 0.4f + rms * 0.6f;  // fast decay (same — no hold on display)
        OnRmsLevel?.Invoke(_smoothedRms);

        // Mic test: enqueue for MixLoop (avoids race with output buffer)
        if (MicTestActive && (VadThreshold == 0 || _smoothedRms > VadThreshold))
            _micTestQueue.Enqueue(mono16);

        // Speaking indicator — uses actual RMS even when VAD is disabled
        const float SpeakFloor = 150f;
        bool aboveThreshold = _smoothedRms > (VadThreshold == 0 ? SpeakFloor : VadThreshold);
        if (InVoice && !Muted)
        {
            if (aboveThreshold) _speakHold = 4; // ~80ms hold
            else _speakHold = Math.Max(0, _speakHold - 1);
            bool shouldSpeak = aboveThreshold || _speakHold > 0;
            if (shouldSpeak != _speaking)
            {
                _speaking = shouldSpeak;
                OnSpeakingChange?.Invoke(_speaking);
            }
        }
        else if (_speaking)
        {
            _speaking = false;
            _speakHold = 0;
            OnSpeakingChange?.Invoke(false);
        }

        // Noise gate: VAD=0 → always transmit; else use threshold + hold
        if (!InVoice || Muted) return;
        if (VadThreshold == 0) { EnqueueInChunks(mono16); return; }
        if (_smoothedRms > VadThreshold) _captureHold = 40;  // 800ms hold
        if (_smoothedRms > VadThreshold || _captureHold > 0)
        {
            EnqueueInChunks(mono16);
            if (_smoothedRms <= VadThreshold) _captureHold = Math.Max(0, _captureHold - 1);
        }
    }


    private readonly List<short> _captureSampleBuffer = new();

    private void EnqueueInChunks(short[] samples)
    {
        _captureSampleBuffer.AddRange(samples);
        while (_captureSampleBuffer.Count >= Protocol.AudioChunkFrames)
        {
            _captureFrames.Enqueue(_captureSampleBuffer.GetRange(0, Protocol.AudioChunkFrames).ToArray());
            _captureSampleBuffer.RemoveRange(0, Protocol.AudioChunkFrames);
        }
    }

    private static short[] ConvertToInt16Mono48k(byte[] buf, int len, WaveFormat fmt)
    {
        try
        {
            short[] mono;
            int srcRate;

            if (fmt.Encoding == WaveFormatEncoding.IeeeFloat)
            {
                // float32, possibly stereo
                int floatCount = len / 4;
                int frames     = floatCount / fmt.Channels;
                srcRate        = fmt.SampleRate;
                mono           = new short[frames];
                for (int i = 0; i < frames; i++)
                {
                    float sum = 0;
                    for (int c = 0; c < fmt.Channels; c++)
                        sum += BitConverter.ToSingle(buf, (i * fmt.Channels + c) * 4);
                    mono[i] = (short)Math.Clamp((int)(sum / fmt.Channels * 32767), -32768, 32767);
                }
            }
            else if (fmt.BitsPerSample == 16)
            {
                // int16, possibly stereo
                int frames = (len / 2) / fmt.Channels;
                srcRate    = fmt.SampleRate;
                mono       = new short[frames];
                for (int i = 0; i < frames; i++)
                {
                    int sum = 0;
                    for (int c = 0; c < fmt.Channels; c++)
                        sum += BitConverter.ToInt16(buf, (i * fmt.Channels + c) * 2);
                    mono[i] = (short)(sum / fmt.Channels);
                }
            }
            else
            {
                return Array.Empty<short>();
            }

            if (srcRate == Protocol.AudioSampleRate) return mono;

            // Linear resample to 48kHz
            double ratio  = (double)Protocol.AudioSampleRate / srcRate;
            int    outLen = (int)(mono.Length * ratio);
            var    result = new short[outLen];
            for (int i = 0; i < outLen; i++)
            {
                double pos = i / ratio;
                int    lo  = (int)pos;
                int    hi  = Math.Min(lo + 1, mono.Length - 1);
                double t   = pos - lo;
                result[i]  = (short)(mono[lo] * (1 - t) + mono[hi] * t);
            }
            return result;
        }
        catch { return Array.Empty<short>(); }
    }

    private readonly ConcurrentQueue<short[]> _captureFrames  = new();


    private async Task CaptureLoopAsync()
    {
        int mySession = _sessionId;
        while (_running && _sessionId == mySession)
        {
            await Task.Delay(5);
            if (!InVoice || Muted) { while (_captureFrames.TryDequeue(out _)) { } continue; }

            // Process ONE chunk per tick to prevent burst-sending
            if (!_captureFrames.TryDequeue(out var chunk)) continue;

            double sq = 0;
            foreach (var s in chunk) sq += (double)s * s;
            var rms      = (float)Math.Sqrt(sq / chunk.Length);
            var isActive = rms > VadThreshold;

            _transGain = isActive
                ? Math.Min(1f, _transGain + 0.3f)
                : _transGain * FadeRelease;

            if (_transGain > 0.001f && OnAudioFrame != null)
            {
                var faded = new short[chunk.Length];
                for (int i = 0; i < chunk.Length; i++)
                    faded[i] = (short)Math.Clamp((int)(chunk[i] * _transGain), -32768, 32767);
                var bytes = new byte[faded.Length * 2];
                Buffer.BlockCopy(faded, 0, bytes, 0, bytes.Length);
                _ = OnAudioFrame(bytes);
            }
        }
    }


    private sealed class AudioMixProvider : IWaveProvider
    {
        private readonly AudioManager _m;
        // Per-source leftover samples (single WASAPI callback thread — no lock needed)
        private readonly Dictionary<string, (short[] buf, int pos)> _pLo = new();
        private readonly Dictionary<string, (short[] buf, int pos)> _sLo = new();
        private (short[] buf, int pos)? _micLo;

        public WaveFormat WaveFormat { get; } =
            new WaveFormat(Protocol.AudioSampleRate, 16, Protocol.AudioChannels);

        public AudioMixProvider(AudioManager m) => _m = m;

        public int Read(byte[] buffer, int offset, int count)
        {
            var n   = count / 2;
            var mix = new int[n];

            if (!_m.Deafened)
            {
                if (_m.InVoice)
                    foreach (var (did, q) in _m._peerQueues)
                    {
                        bool muted;
                        lock (_m._muteLock) muted = _m._localMutes.Contains(did);
                        if (muted) continue;
                        var v = _m._peerVolumes.GetValueOrDefault(did, 1f) * _m._peersVolume;
                        Mix(q, did, _pLo, v, mix, n);
                    }

                foreach (var (did, q) in _m._streamQueues)
                {
                    var v = _m._streamVolumes.GetValueOrDefault(did, 0f) * _m._streamVolume * _m._sysAudioVol;
                    if (v > 0.001f) Mix(q, did, _sLo, v, mix, n);
                }
            }

            if (_m.MicTestActive)
            {
                int p = 0;
                if (_micLo.HasValue)
                {
                    var (b, lo) = _micLo.Value;
                    while (p < n && lo < b.Length) mix[p++] += b[lo++];
                    _micLo = lo < b.Length ? (b, lo) : null;
                }
                while (p < n && _m._micTestQueue.TryDequeue(out var c))
                {
                    int lo = 0;
                    while (p < n && lo < c.Length) mix[p++] += c[lo++];
                    if (lo < c.Length) _micLo = (c, lo);
                }
            }

            for (int i = 0; i < n; i++)
            {
                var s = (short)Math.Clamp((int)(mix[i] * _m._masterVolume), -32768, 32767);
                buffer[offset + i * 2]     = (byte)(s       & 0xFF);
                buffer[offset + i * 2 + 1] = (byte)((s >> 8) & 0xFF);
            }
            return count;
        }

        private static void Mix(
            ConcurrentQueue<short[]> q, string did,
            Dictionary<string, (short[] buf, int pos)> lo,
            float v, int[] mix, int n)
        {
            int p = 0;
            if (lo.TryGetValue(did, out var left))
            {
                var (b, lp) = left;
                while (p < n && lp < b.Length) mix[p++] += (int)(b[lp++] * v);
                if (lp < b.Length) lo[did] = (b, lp); else lo.Remove(did);
            }
            while (p < n && q.TryDequeue(out var c))
            {
                int cp = 0;
                while (p < n && cp < c.Length) mix[p++] += (int)(c[cp++] * v);
                if (cp < c.Length) lo[did] = (c, cp);
            }
        }
    }



    public void ReceivePeerAudio(string deviceId, byte[] audioBytes)
    {
        var chunk = new short[audioBytes.Length / 2];
        Buffer.BlockCopy(audioBytes, 0, chunk, 0, audioBytes.Length);
        if (chunk.Length != Protocol.AudioChunkFrames) return;

        var q = _peerQueues.GetOrAdd(deviceId, _ => new ConcurrentQueue<short[]>());
        if (q.Count >= JitterMax) q.TryDequeue(out _);
        q.Enqueue(chunk);
    }

    public void ReceivePeerStreamAudio(string deviceId, byte[] audioBytes)
    {
        var chunk = new short[audioBytes.Length / 2];
        Buffer.BlockCopy(audioBytes, 0, chunk, 0, audioBytes.Length);
        if (chunk.Length != Protocol.AudioChunkFrames) return;

        var q = _streamQueues.GetOrAdd(deviceId, _ => new ConcurrentQueue<short[]>());
        if (q.Count >= JitterMax) q.TryDequeue(out _);
        q.Enqueue(chunk);
    }

    public void RemovePeer(string deviceId)
    {
        _peerQueues.TryRemove(deviceId, out _);
        _peerVolumes.TryRemove(deviceId, out _);
    }

    public void SetPeerVolume(string deviceId, float v)     => _peerVolumes[deviceId]  = Math.Max(0, v);
    public float GetPeerVolume(string deviceId)             => _peerVolumes.GetValueOrDefault(deviceId, 1f);
    public void SetPeerStreamVolume(string deviceId, float v) => _streamVolumes[deviceId] = Math.Max(0, v);
    public float GetPeerStreamVolume(string deviceId)       => _streamVolumes.GetValueOrDefault(deviceId, 0f);

    public void SetLocalMute(string deviceId, bool muted)
    {
        lock (_muteLock) { if (muted) _localMutes.Add(deviceId); else _localMutes.Remove(deviceId); }
    }
    public bool IsLocalMuted(string deviceId)  { lock (_muteLock) return _localMutes.Contains(deviceId); }
    public bool IsLocallyMuted(string deviceId) => IsLocalMuted(deviceId);
    public void MuteLocal(string deviceId)      => SetLocalMute(deviceId, true);
    public void UnmuteLocal(string deviceId)    => SetLocalMute(deviceId, false);


    public void StartSystemAudio()
    {
        StopSystemAudio();
        // Windows 10 2004+ (build 19041): exclude our process from loopback
        if (ProcessLoopbackCapture.IsSupported())
        {
            try
            {
                var cap = new ProcessLoopbackCapture();
                cap.DataAvailable    += LoopbackDataAvailable;
                cap.RecordingStopped += (_, __) => { if (_running) StartSystemAudio(); };
                cap.StartRecording((uint)Environment.ProcessId);
                _processLoopback = cap;
                _loopbackFormat  = cap.WaveFormat;
                Log("Loopback: process-exclusion started");
                return;
            }
            catch (Exception ex)
            {
                Log($"Process loopback failed ({ex.Message}), falling back");
            }
        }
        try
        {
            _loopback = new WasapiLoopbackCapture();
            _loopbackFormat = _loopback.WaveFormat;
            _loopback.DataAvailable += LoopbackDataAvailable;
            _loopback.StartRecording();
            Log("Loopback: regular started");
        }
        catch (Exception ex)
        {
            Log($"Loopback start failed: {ex.Message}");
        }
    }

    public void StopSystemAudio()
    {
        try { _loopback?.StopRecording();        _loopback?.Dispose();        _loopback        = null; } catch { }
        try { _processLoopback?.StopRecording(); _processLoopback?.Dispose(); _processLoopback = null; } catch { }
        _loopbackFormat = null;
    }

    private readonly ConcurrentQueue<short[]> _loopbackBuf = new();
    private byte[] _loopbackRemainder = Array.Empty<byte>();

    private void LoopbackDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0 || OnStreamAudioReady == null) return;

        // Loopback format might be float32 stereo — convert to int16 mono 48kHz
        var fmt = _loopbackFormat;
        if (fmt == null) return;

        byte[] pcm16Mono;
        if (fmt.Encoding == WaveFormatEncoding.IeeeFloat && fmt.Channels == 2)
        {
            pcm16Mono = ConvertFloat32StereoToInt16Mono(e.Buffer, e.BytesRecorded, fmt.SampleRate);
        }
        else if (fmt.Encoding == WaveFormatEncoding.IeeeFloat && fmt.Channels == 1)
        {
            pcm16Mono = ConvertFloat32MonoToInt16(e.Buffer, e.BytesRecorded);
        }
        else
        {
            pcm16Mono = e.Buffer[..e.BytesRecorded];
        }

        // Combine with leftover from previous call
        var combined = new byte[_loopbackRemainder.Length + pcm16Mono.Length];
        _loopbackRemainder.CopyTo(combined, 0);
        pcm16Mono.CopyTo(combined, _loopbackRemainder.Length);

        int chunkBytes = Protocol.AudioChunkFrames * 2; // 1920 bytes
        int offset = 0;
        while (offset + chunkBytes <= combined.Length)
        {
            var chunk = combined[offset..(offset + chunkBytes)];
            if (_sysAudioVol != 1.0f)
            {
                var shorts = new short[chunk.Length / 2];
                Buffer.BlockCopy(chunk, 0, shorts, 0, chunk.Length);
                for (int i = 0; i < shorts.Length; i++)
                    shorts[i] = (short)Math.Clamp((int)(shorts[i] * _sysAudioVol), -32768, 32767);
                chunk = new byte[shorts.Length * 2];
                Buffer.BlockCopy(shorts, 0, chunk, 0, chunk.Length);
            }
            _ = OnStreamAudioReady(chunk);
            offset += chunkBytes;
        }
        _loopbackRemainder = combined[offset..];
    }

    private static byte[] ConvertFloat32StereoToInt16Mono(byte[] buf, int len, int srcRate)
    {
        int floatCount = len / 4;
        int frames     = floatCount / 2;
        var mono16     = new short[frames];
        for (int i = 0; i < frames; i++)
        {
            var l = BitConverter.ToSingle(buf, i * 8);
            var r = BitConverter.ToSingle(buf, i * 8 + 4);
            mono16[i] = (short)Math.Clamp((int)(((l + r) * 0.5f) * 32767), -32768, 32767);
        }
        // Simple resample if needed (linear)
        if (srcRate == Protocol.AudioSampleRate)
        {
            var out2 = new byte[mono16.Length * 2];
            Buffer.BlockCopy(mono16, 0, out2, 0, out2.Length);
            return out2;
        }
        double ratio = (double)Protocol.AudioSampleRate / srcRate;
        int outLen   = (int)(frames * ratio);
        var resampled = new short[outLen];
        for (int i = 0; i < outLen; i++)
        {
            double srcIdx = i / ratio;
            int    lo     = (int)srcIdx;
            int    hi     = Math.Min(lo + 1, frames - 1);
            double t      = srcIdx - lo;
            resampled[i]  = (short)(mono16[lo] * (1 - t) + mono16[hi] * t);
        }
        var result = new byte[resampled.Length * 2];
        Buffer.BlockCopy(resampled, 0, result, 0, result.Length);
        return result;
    }

    private static byte[] ConvertFloat32MonoToInt16(byte[] buf, int len)
    {
        int count  = len / 4;
        var result = new byte[count * 2];
        for (int i = 0; i < count; i++)
        {
            var f = BitConverter.ToSingle(buf, i * 4);
            var s = (short)Math.Clamp((int)(f * 32767), -32768, 32767);
            result[i * 2]     = (byte)(s & 0xFF);
            result[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
        }
        return result;
    }


    public static List<(string Id, string Name)> GetInputDevices()
    {
        var list = new List<(string, string)>();
        try
        {
            using var en = new MMDeviceEnumerator();
            foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
                list.Add((d.ID, d.FriendlyName));
        }
        catch { }
        return list;
    }

    public static List<(string Id, string Name)> GetOutputDevices()
    {
        var list = new List<(string, string)>();
        try
        {
            using var en = new MMDeviceEnumerator();
            foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                list.Add((d.ID, d.FriendlyName));
        }
        catch { }
        return list;
    }

    public void Dispose() => StopStreams();
}
