using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using NAudio.Wave;

namespace RadCord.Core;

/// <summary>
/// Captures system audio while excluding a specific process (Windows 10 2004 / build 19041+).
/// Use <see cref="IsSupported"/> to check availability before constructing.
/// </summary>
public sealed class ProcessLoopbackCapture : IDisposable
{

    // Virtual device path for process loopback activation
    private const string LoopbackDevicePath = "{2eef81be-33fa-4800-9670-1cd474972c3f}";

    private static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");

    private const uint AUDCLNT_STREAMFLAGS_LOOPBACK      = 0x00020000;
    private const uint AUDCLNT_STREAMFLAGS_EVENTCALLBACK = 0x00040000;
    private const uint AUDCLNT_SHAREMODE_SHARED          = 0;
    private const int  AUDCLNT_S_BUFFER_EMPTY            = 0x08890001;


    private enum AUDIOCLIENT_ACTIVATION_TYPE : uint { Default = 0, ProcessLoopback = 1 }
    private enum PROCESS_LOOPBACK_MODE : uint { IncludeTarget = 0, ExcludeTarget = 1 }

    [StructLayout(LayoutKind.Sequential)]
    private struct AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS
    {
        public uint TargetProcessId;
        public PROCESS_LOOPBACK_MODE ProcessLoopbackMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AUDIOCLIENT_ACTIVATION_PARAMS
    {
        public AUDIOCLIENT_ACTIVATION_TYPE ActivationType;
        public AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS ProcessLoopbackParams;
    }

    // Minimal PROPVARIANT — VT_BLOB (0x41) carrying our activation struct
    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariantBlob
    {
        public ushort vt;        // VARTYPE
        public ushort reserved1;
        public ushort reserved2;
        public ushort reserved3;
        public int    cbSize;    // blob.cbSize
        public IntPtr pBlobData; // blob.pBlobData (allocated via Marshal)
    }


    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceCompletionHandler
    {
        void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
    }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceAsyncOperation
    {
        void GetActivateResult(out int activateResult,
            [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    // Minimal IAudioClient — only the methods we need
    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClientMinimal
    {
        [PreserveSig] int Initialize(uint shareMode, uint streamFlags,
            long hnsBufferDuration, long hnsPeriodicity,
            IntPtr pFormat, IntPtr audioSessionGuid);
        [PreserveSig] int GetBufferSize(out uint pNumBufferFrames);
        [PreserveSig] int GetStreamLatency(out long phnsLatency);
        [PreserveSig] int GetCurrentPadding(out uint pNumPaddingFrames);
        [PreserveSig] int IsFormatSupported(uint shareMode, IntPtr pFormat, out IntPtr ppClosestMatch);
        [PreserveSig] int GetMixFormat(out IntPtr ppDeviceFormat);
        [PreserveSig] int GetDevicePeriod(out long phnsDefaultDevicePeriod, out long phnsMinimumDevicePeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr eventHandle);
        [PreserveSig] int GetService(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClientMinimal
    {
        [PreserveSig] int GetBuffer(out IntPtr ppData, out uint pNumFramesToRead,
            out uint pdwFlags, out ulong pu64DevicePosition, out ulong pu64QPCPosition);
        [PreserveSig] int ReleaseBuffer(uint numFramesRead);
        [PreserveSig] int GetNextPacketSize(out uint pNumFramesInNextPacket);
    }

    [DllImport("Mmdevapi.dll", ExactSpelling = true)]
    private static extern int ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        ref Guid riid,
        ref PropVariantBlob activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);


    public static bool IsSupported() =>
        Environment.OSVersion.Version >= new Version(10, 0, 19041);

    public WaveFormat? WaveFormat { get; private set; }
    public event EventHandler<WaveInEventArgs>? DataAvailable;
    public event EventHandler<StoppedEventArgs>? RecordingStopped;

    private IAudioClientMinimal?       _client;
    private IAudioCaptureClientMinimal? _captureClient;
    private Thread? _thread;
    private volatile bool _running;


    public void StartRecording(uint excludePid)
    {
        if (_running) return;
        _client = ActivateProcessLoopback(excludePid);
        if (_client == null) throw new InvalidOperationException("Process loopback activation failed");

        // Get mix format
        _client.GetMixFormat(out var pFmt);
        WaveFormat = WaveFormat.MarshalFromPtr(pFmt);
        Marshal.FreeCoTaskMem(pFmt);

        // Initialize with LOOPBACK flag
        var pFmt2 = NAudio.Wave.WaveFormat.MarshalToPtr(WaveFormat);
        _client.Initialize(AUDCLNT_SHAREMODE_SHARED, AUDCLNT_STREAMFLAGS_LOOPBACK,
            0, 0, pFmt2, IntPtr.Zero);
        Marshal.FreeHGlobal(pFmt2);

        var iidCapture = new Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317");
        _client.GetService(ref iidCapture, out var svc);
        _captureClient = (IAudioCaptureClientMinimal)svc;

        _client.Start();
        _running = true;
        _thread = new Thread(CaptureLoop) { IsBackground = true };
        _thread.Start();
    }

    public void StopRecording()
    {
        _running = false;
        _thread?.Join(2000);
        _client?.Stop();
    }

    public void Dispose()
    {
        StopRecording();
        if (_client        != null) Marshal.ReleaseComObject(_client);
        if (_captureClient != null) Marshal.ReleaseComObject(_captureClient);
    }


    private IAudioClientMinimal? ActivateProcessLoopback(uint excludePid)
    {
        try
        {
            var aparams = new AUDIOCLIENT_ACTIVATION_PARAMS
            {
                ActivationType = AUDIOCLIENT_ACTIVATION_TYPE.ProcessLoopback,
                ProcessLoopbackParams = new AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS
                {
                    TargetProcessId = excludePid,
                    ProcessLoopbackMode = PROCESS_LOOPBACK_MODE.ExcludeTarget,
                }
            };

            int structSize = Marshal.SizeOf<AUDIOCLIENT_ACTIVATION_PARAMS>();
            var pStruct = Marshal.AllocCoTaskMem(structSize);
            Marshal.StructureToPtr(aparams, pStruct, false);

            var propVar = new PropVariantBlob { vt = 0x41, cbSize = structSize, pBlobData = pStruct };

            var completed = new CompletionHandler();
            var riid = IID_IAudioClient;

            int hr = ActivateAudioInterfaceAsync(LoopbackDevicePath, ref riid,
                ref propVar, completed, out _);

            Marshal.FreeCoTaskMem(pStruct);

            if (hr != 0) return null;

            // Wait for completion (max 5 seconds)
            completed.Event.WaitOne(5000);

            return completed.Client;
        }
        catch { return null; }
    }

    private void CaptureLoop()
    {
        Exception? error = null;
        try
        {
            while (_running)
            {
                Thread.Sleep(10);
                if (_captureClient == null || WaveFormat == null) continue;

                _captureClient.GetNextPacketSize(out uint nextSize);
                while (nextSize > 0)
                {
                    _captureClient.GetBuffer(out var pData, out uint numFrames,
                        out uint flags, out _, out _);

                    int bytesPerFrame = WaveFormat.BlockAlign;
                    int byteCount = (int)(numFrames * bytesPerFrame);
                    var data = new byte[byteCount];

                    if ((flags & 2) == 0) // AUDCLNT_BUFFERFLAGS_SILENT = 2
                        Marshal.Copy(pData, data, 0, byteCount);

                    _captureClient.ReleaseBuffer(numFrames);
                    DataAvailable?.Invoke(this, new WaveInEventArgs(data, byteCount));

                    _captureClient.GetNextPacketSize(out nextSize);
                }
            }
        }
        catch (Exception ex) { error = ex; }
        finally { RecordingStopped?.Invoke(this, new StoppedEventArgs(error)); }
    }


    private sealed class CompletionHandler : IActivateAudioInterfaceCompletionHandler
    {
        public ManualResetEvent Event { get; } = new(false);
        public IAudioClientMinimal? Client { get; private set; }

        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation op)
        {
            try
            {
                op.GetActivateResult(out int hr, out var obj);
                if (hr == 0) Client = obj as IAudioClientMinimal;
            }
            catch { }
            Event.Set();
        }
    }
}
