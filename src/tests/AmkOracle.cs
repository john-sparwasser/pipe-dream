using System.Diagnostics;
using Xunit;

namespace PipeDream.Tests;

/// <summary>
/// AddmusicK as a black box (AMK_PORT.md): our compiler and engine are checked against what its
/// <c>addmusick</c> produces, never against its source. PIPEDREAM_AMK names a folder laid out like AMK's
/// own <c>test/</c> (the binary, libasar, asm/, samples/, the manifests); it is copied for each run so the
/// original is never written to. CI has no AMK, so every test that needs it skips.
/// </summary>
internal static class AmkOracle
{
    public static string? Root
        => Environment.GetEnvironmentVariable("PIPEDREAM_AMK") is { Length: > 0 } r
           && File.Exists(Path.Combine(r, "addmusick")) ? r : null;

    /// <summary>AMK's own test songs that compile from a bare copy of its test/ folder. ww.mml, ww2.mml and
    /// ww3.mml are left out: they name a sample (ww_07.brr) that folder does not have.</summary>
    public static readonly string[] Corpus =
    [
        "cal.mml", "click.mml", "ct_fanfare_2.opt.mml", "dv.mml", "dv2.mml", "dv3.mml", "ek.mml",
        "exp.mml", "exp2.mml", "exp3.mml", "rt_ct.mml", "rt_exp2.mml", "ww_solo.mml",
    ];

    /// <summary>Compile <paramref name="songs"/> (paths under the folder's music/) with -norom and
    /// return the .spc each produced, keyed by the song's file name without its extension.</summary>
    public static Dictionary<string, SpcFile> Compile(string root, params string[] songs)
    {
        var work = Path.Combine(Path.GetTempPath(), "pipedream-amk-" + Guid.NewGuid().ToString("N"));
        CopyDir(root, work);
        foreach (var f in Directory.GetFiles(Path.Combine(work, "SPCs"), "*.spc")) File.Delete(f);
        var args = new List<string> { "-norom", "-noblock" };
        args.AddRange(songs);
        var psi = new ProcessStartInfo(Path.Combine(work, "addmusick"))
        {
            WorkingDirectory = work, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        if (!p.WaitForExit(120_000)) { p.Kill(); throw new TimeoutException("addmusick did not finish"); }
        if (!output.Contains("Success!")) throw new InvalidOperationException("addmusick failed:\n" + output);
        var result = new Dictionary<string, SpcFile>();
        foreach (var s in songs)
        {
            var name = Path.GetFileNameWithoutExtension(s);
            // AMK names the snapshot from the part before the first dot ("ct_fanfare_2.opt.mml" -> ct_fanfare_2.spc).
            var file = new[] { name, name.Split('.')[0] }.Select(n => Path.Combine(work, "SPCs", n + ".spc")).First(File.Exists);
            result[name] = SpcFile.Parse(File.ReadAllBytes(file));
        }
        Directory.Delete(work, true);
        return result;
    }

    /// <summary>Compile one song given as text (the file is written into the copy's music/ folder as t.mml).</summary>
    public static SpcFile CompileText(string root, string mml)
    {
        var work = Path.Combine(Path.GetTempPath(), "pipedream-amk-" + Guid.NewGuid().ToString("N"));
        CopyDir(root, work);
        try
        {
            foreach (var f in Directory.GetFiles(Path.Combine(work, "SPCs"), "*.spc")) File.Delete(f);
            File.WriteAllText(Path.Combine(work, "music", "t.mml"), mml);
            var psi = new ProcessStartInfo(Path.Combine(work, "addmusick")) { WorkingDirectory = work, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in new[] { "-norom", "-noblock", "t.mml" }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            if (!p.WaitForExit(60_000)) { p.Kill(); throw new TimeoutException("addmusick did not finish"); }
            if (!output.Contains("Success!")) throw new InvalidOperationException("addmusick failed:\n" + output);
            return SpcFile.Parse(File.ReadAllBytes(Path.Combine(work, "SPCs", "t.spc")));
        }
        finally { Directory.Delete(work, true); }
    }

    private static void CopyDir(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true);
        foreach (var d in Directory.GetDirectories(from)) CopyDir(d, Path.Combine(to, Path.GetFileName(d)));
        if (OperatingSystem.IsWindows()) return;
        var exe = Path.Combine(to, "addmusick");
        if (File.Exists(exe)) File.SetUnixFileMode(exe, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
}

/// <summary>Needs the AddmusicK oracle (PIPEDREAM_AMK).</summary>
public sealed class AmkFactAttribute : FactAttribute
{
    public AmkFactAttribute()
    {
        if (AmkOracle.Root is null) Skip = "AddmusicK oracle not present (set PIPEDREAM_AMK to a folder like AMK's test/)";
    }
}

public sealed class AmkTheoryAttribute : TheoryAttribute
{
    public AmkTheoryAttribute()
    {
        if (AmkOracle.Root is null) Skip = "AddmusicK oracle not present (set PIPEDREAM_AMK to a folder like AMK's test/)";
    }
}
