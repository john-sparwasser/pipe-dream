using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace PipeDream.Services;

/// <summary>What a newer release looks like to the rest of the app.</summary>
/// <param name="Version">Normalised release version, for display and for remembering a skip.</param>
/// <param name="AssetName">The file to fetch — a Windows installer, or the Linux binary.</param>
public sealed record UpdateInfo(Version Version, string AssetName, string DownloadUrl, long Size, string? Notes)
{
    /// <summary>How the version is written down: config, dialogs, the skip list.</summary>
    public string Display => $"{Version.Major}.{Version.Minor}.{Version.Build}";
}

/// <summary>
/// Self-update: is there a newer release, and installing it over this one.
///
/// The feed is GitHub RELEASES, not CI artifacts. Artifacts need a logged-in GitHub session to
/// download and expire after 90 days, so they cannot be what a shipped binary points at; a
/// release asset is a plain public URL that stays put. Until the repo has its first tagged
/// release the check finds nothing and says so, which is the correct behaviour rather than a
/// failure.
///
/// How the install actually happens differs per platform, and both go through the packaging
/// that already exists rather than reinventing it:
///
///   Windows — download the Inno installer and run it silently. Its AppId is fixed, so it
///     recognises the install it is replacing and upgrades in place, keeping the Start Menu
///     entry and the .pdp association correct. A running exe cannot be overwritten on Windows,
///     so the app must exit for this; /relaunch=1 has the installer start the new one.
///   Linux — the artifact is one self-contained file, so the update IS that file. Written
///     beside the current binary and renamed over it: POSIX keeps the running process on the
///     old inode, so replacing a busy executable this way is safe, where writing into it is not.
/// </summary>
public static class UpdateCheck
{
    public const string Repo = "john-sparwasser/pipe-dream";

    /// <summary>Long enough for a slow link, short enough that a hung check is never what the
    /// user notices about startup.</summary>
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    static UpdateCheck() =>
        // GitHub rejects API calls with no User-Agent outright.
        Http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PipeDream", "1"));

    /// <summary>The version of the running build. Release builds get theirs from CI, which
    /// passes the same number the installer is stamped with; a dev build is whatever the csproj
    /// says and will not usually be newer than a release, so a developer sees update prompts —
    /// bump the csproj or turn the check off.</summary>
    public static Version Current =>
        Normalise(Assembly.GetEntryAssembly()?.GetName().Version) ?? new Version(0, 0, 0);

    /// <summary>
    /// Three parts, no revision. <see cref="Version"/> leaves unspecified components at -1, so
    /// a tag of "0.1.42" parses to 0.1.42 while the assembly reports 0.1.42.0 — and 0.1.42
    /// compares LESS than 0.1.42.0, which would offer the running build to itself forever.
    /// Normalising both sides is what stops that.
    /// </summary>
    internal static Version? Normalise(Version? v) =>
        v is null ? null : new Version(v.Major, v.Minor, Math.Max(v.Build, 0));

    /// <summary>Release tags are written "v0.1.42" or "0.1.42"; anything else is not ours.</summary>
    internal static Version? ParseTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        string t = tag.Trim();
        if (t.StartsWith('v') || t.StartsWith('V')) t = t[1..];
        return Version.TryParse(t, out var v) ? Normalise(v) : null;
    }

    /// <summary>The platforms build.yml publishes an asset for. Nothing else is offered an update:
    /// WantsAsset's non-Windows branch means Linux, and on a Mac it would have picked the Linux
    /// binary and Apply() would have moved it over the running app. macOS stays out until there
    /// is an Apple developer licence to sign a build with.</summary>
    public static bool Supported => OperatingSystem.IsWindows() || OperatingSystem.IsLinux();

    /// <summary>Which release asset this platform installs. Windows takes the installer, Linux
    /// the bare executable; both are matched on the names build.yml actually uploads.</summary>
    internal static bool WantsAsset(string name, bool isWindows) =>
        isWindows
            ? name.StartsWith("PipeDream-Setup", StringComparison.OrdinalIgnoreCase)
              && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            : name.Contains("linux", StringComparison.OrdinalIgnoreCase)
              && !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether a startup check should go out at all. A user who picks "check now" always gets
    /// one; an automatic check is once a day, because a newer build appearing is not worth a
    /// request on every launch.
    /// </summary>
    internal static bool Due(bool userAsked, bool enabled, DateTime? lastUtc, DateTime nowUtc)
    {
        if (userAsked) return true;
        if (!enabled) return false;
        return lastUtc is null || nowUtc - lastUtc.Value >= TimeSpan.FromHours(24);
    }

    /// <summary>
    /// The newest release worth offering, or null for "nothing to do" — up to date, no release
    /// published yet, this version already skipped, or the network did not answer. A failed
    /// check is deliberately indistinguishable from "up to date" to the caller: there is
    /// nothing a user can do about it, and an error box on startup because GitHub was briefly
    /// unreachable is worse than silence.
    /// </summary>
    public static async Task<UpdateInfo?> Latest(Version current, string? skipped,
                                                 bool isWindows, CancellationToken ct = default)
    {
        try
        {
            string json = await Http.GetStringAsync(
                $"https://api.github.com/repos/{Repo}/releases/latest", ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("draft", out var d) && d.GetBoolean()) return null;
            var version = ParseTag(root.TryGetProperty("tag_name", out var t) ? t.GetString() : null);
            if (version is null || version <= current) return null;
            if (skipped is not null && ParseTag(skipped) == version) return null;

            if (!root.TryGetProperty("assets", out var assets)) return null;
            foreach (var a in assets.EnumerateArray())
            {
                string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                string? url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                if (name is null || url is null || !WantsAsset(name, isWindows)) continue;
                long size = a.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
                return new UpdateInfo(version, name, url, size,
                    root.TryGetProperty("body", out var b) ? b.GetString() : null);
            }
            return null;                     // released, but nothing this platform can install
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException
                                   or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Fetch the asset to a temp file and hand back the path. Reports 0..1 when the server
    /// gives a length, which GitHub's asset redirect does.
    /// </summary>
    public static async Task<string> Download(UpdateInfo u, IProgress<double>? progress = null,
                                              CancellationToken ct = default)
    {
        string dir = Path.Combine(Path.GetTempPath(), "PipeDream-update");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, u.AssetName);

        using var rsp = await Http.GetAsync(u.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        rsp.EnsureSuccessStatusCode();
        long total = rsp.Content.Headers.ContentLength ?? u.Size;

        await using (var src = await rsp.Content.ReadAsStreamAsync(ct))
        await using (var dst = File.Create(path))
        {
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                if (total > 0) progress?.Report(Math.Min(1.0, (double)done / total));
            }
        }
        return path;
    }

    /// <summary>
    /// Install what <see cref="Download"/> fetched. The caller must shut the editor down right
    /// after this returns — on Windows the installer cannot replace an exe that is still
    /// running, and on Linux the process would keep running a binary that is no longer there.
    /// Returns a problem string, or null when the update is under way.
    /// </summary>
    public static string? Apply(string downloadedFile)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                // /relaunch=1 is ours: the installer's own postinstall run is flagged
                // skipifsilent, so without it a silent upgrade would leave the editor closed.
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = downloadedFile,
                    Arguments = "/SILENT /SUPPRESSMSGBOXES /NORESTART /relaunch=1",
                    UseShellExecute = true,
                });
                return null;
            }

            string? exe = Environment.ProcessPath;
            if (exe is null) return "cannot tell where this build is installed";

            // Rename over the running binary: the old inode stays alive for this process, so
            // this is safe in a way that writing into the file in place is not.
            File.Copy(downloadedFile, exe + ".new", overwrite: true);
            File.SetUnixFileMode(exe + ".new",
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            File.Move(exe + ".new", exe, overwrite: true);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
            });
            return null;
        }
        catch (Exception e)
        {
            return e.Message;
        }
    }
}
