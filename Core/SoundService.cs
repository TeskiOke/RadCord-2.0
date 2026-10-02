using System.IO;
using System.Reflection;
using NAudio.Wave;

namespace RadCord.Core;

public class SoundService
{
    private readonly string _dir;
    public float Volume { get; set; } = 1.0f;

    private static readonly Assembly _asm = Assembly.GetExecutingAssembly();
    private static readonly string[] _resNames = _asm.GetManifestResourceNames();

    public SoundService(string soundDir) => _dir = soundDir;

    private Stream? OpenStream(string fileName)
    {
        // Try embedded resource first (match by filename suffix)
        var res = _resNames.FirstOrDefault(r =>
            r.EndsWith(fileName.Replace(' ', '_'), StringComparison.OrdinalIgnoreCase) ||
            r.EndsWith(fileName,                   StringComparison.OrdinalIgnoreCase));
        if (res != null) return _asm.GetManifestResourceStream(res);

        // Fallback: filesystem (dev mode)
        var path = System.IO.Path.Combine(_dir, fileName);
        return System.IO.File.Exists(path) ? System.IO.File.OpenRead(path) : null;
    }

    public void Play(string fileName)
    {
        var vol = Math.Clamp(Volume, 0f, 1f);
        Task.Run(() =>
        {
            try
            {
                using var stream = OpenStream(fileName);
                if (stream == null) return;
                using var reader = new Mp3FileReader(stream);
                using var output = new WaveOutEvent();
                output.Volume = vol;
                output.Init(reader);
                output.Play();
                while (output.PlaybackState == PlaybackState.Playing)
                    Thread.Sleep(50);
            }
            catch { }
        });
    }
}
