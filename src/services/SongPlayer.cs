using System.Diagnostics;

namespace PipeDream.Services;

/// <summary>
/// Plays a song from the editor: the .spc MusicInsert makes of it, run in the interpreter with <see cref="SpcDsp"/> into a
/// .wav, which the system's own player plays (afplay, paplay or aplay, PowerShell's SoundPlayer). Rendering runs at about
/// 100 times real time, so the whole length is rendered before playing. ponytail: no audio library, so no streaming or
/// position; add one (and play as it renders) if the wait or the external player gets in the way.
/// </summary>
public sealed class SongPlayer
{
    private Process? playing;

    /// <summary>How long to render: the song's #length, else two minutes; never more than five.</summary>
    public static int Seconds(SpcFile spc) => Math.Min(spc.Seconds > 0 ? spc.Seconds : 120, 300);

    /// <summary>The .spc as a .wav file in the temp folder.</summary>
    public static string Render(SpcFile spc)
    {
        var cpu = Spc700.FromSpc(spc);
        cpu.DspWrites = null;
        var pcm = SpcDsp.Render(cpu, Seconds(spc) * 32000);
        string file = Path.Combine(Path.GetTempPath(), "pipedream-song.wav");
        File.WriteAllBytes(file, SpcDsp.Wav(pcm));
        return file;
    }

    /// <summary>Start the system player on <paramref name="wav"/>, stopping whatever was playing: null once it plays, else why not.</summary>
    public string? Play(string wav)
    {
        Stop();
        foreach (var (exe, args) in Players(wav))
        {
            try
            {
                var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
                foreach (var a in args) psi.ArgumentList.Add(a);
                playing = Process.Start(psi);
                if (playing is not null) return null;
            }
            catch (System.ComponentModel.Win32Exception) { }       // not installed: try the next
        }
        return "no audio player found (afplay, paplay, aplay or PowerShell)";
    }

    public void Stop()
    {
        try { if (playing is { HasExited: false }) playing.Kill(); }
        catch (InvalidOperationException) { }
        playing = null;
    }

    public bool IsPlaying => playing is { HasExited: false };

    private static IEnumerable<(string, string[])> Players(string wav)
    {
        if (OperatingSystem.IsMacOS()) yield return ("afplay", [wav]);
        if (OperatingSystem.IsWindows())
            yield return ("powershell", ["-NoProfile", "-Command", $"(New-Object Media.SoundPlayer '{wav.Replace("'", "''")}').PlaySync()"]);
        yield return ("paplay", [wav]);
        yield return ("aplay", ["-q", wav]);
    }
}
