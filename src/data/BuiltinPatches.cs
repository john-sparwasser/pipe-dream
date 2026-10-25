using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace PipeDream;

/// <summary>A patch Pipe Dream offers by name (reference/SA1_PORT.md, "Bundled patches"): where its
/// release comes from, the hash that release must have, and the file in it Asar assembles.</summary>
/// <param name="Licence">Why it is downloaded rather than shipped, for the confirmation and the notices.</param>
public sealed record BuiltinPatch(string Id, string Name, string Version, string Url, string Sha256, string Entry, string Licence);

/// <summary>
/// The catalogue of built-in patches, and the per-user cache they are downloaded into. A project
/// names an entry by id and pinned version (<see cref="ProjectFile.BuiltinPatchRef"/>); the build
/// asks <see cref="Ensure"/> for the folder, which downloads the release once, refuses it unless its
/// SHA-256 is the catalogue's, and extracts it. After that builds work offline.
///
/// Only versions listed here can be pinned: Pipe Dream's SA-1 forms are generated and measured
/// against a pack release, so an unmeasured release is not one a build can take.
/// </summary>
public static class BuiltinPatches
{
    /// <summary>The SA-1 Pack. It declares no licence (GitHub reports none, the README has credits
    /// and no terms), so it cannot be redistributed: each user downloads their own copy.</summary>
    public static readonly BuiltinPatch Sa1 = new(
        "sa1", "SA-1 Pack", "1.40",
        "https://github.com/VitorVilela7/SMW-SA1-Pack/releases/download/v1.40/SA1-Pack-140.zip",
        "ffd31d1ef68a35af5b2f91d754f4b792cad05ac1e6445b8191bb7a3ddec905a2",
        "sa1.asm",
        "Vitor Vilela's SA-1 Pack declares no licence, so Pipe Dream does not ship it: it is downloaded from its GitHub release.");

    public static IReadOnlyList<BuiltinPatch> All { get; } = [Sa1];

    /// <summary>The catalogue entry a project's reference names, or null when this build of Pipe
    /// Dream does not know that id and version.</summary>
    public static BuiltinPatch? Find(string id, string version) => All.FirstOrDefault(p => p.Id == id && p.Version == version);

    /// <summary>The cache: a folder per id and version, under the per-user local data folder.
    /// PIPEDREAM_BUILTIN_DIR overrides it, as PIPEDREAM_CONFIG_DIR does the config.</summary>
    internal static string CacheDir =>
        Environment.GetEnvironmentVariable("PIPEDREAM_BUILTIN_DIR") is { Length: > 0 } d
            ? d : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PipeDream", "builtin");

    /// <summary>Where <paramref name="p"/> is extracted; it is installed when this folder exists.</summary>
    public static string Folder(BuiltinPatch p) => Path.Combine(CacheDir, p.Id, p.Version);

    public static bool Installed(BuiltinPatch p) => Directory.Exists(Folder(p));

    private static readonly HttpClient Http = MakeHttp();

    private static HttpClient MakeHttp()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        h.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PipeDream", "1"));   // GitHub refuses requests without one
        return h;
    }

    /// <summary>Download and extract <paramref name="p"/> unless it is already installed. Null when
    /// it is in place, else what went wrong, in words for the user.</summary>
    public static async Task<string?> Install(BuiltinPatch p, CancellationToken ct = default)
    {
        if (Installed(p)) return null;
        byte[] zip;
        try { zip = await Http.GetByteArrayAsync(p.Url, ct); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        { return $"could not download the {p.Name} v{p.Version} ({e.Message})"; }
        return Install(p, zip);
    }

    /// <summary>Extract <paramref name="zip"/> as <paramref name="p"/>'s release, if its hash is the
    /// catalogue's. Into a temporary folder first and renamed into place, so a half-written folder
    /// is never taken for an installed one.</summary>
    internal static string? Install(BuiltinPatch p, byte[] zip)
    {
        string got = Convert.ToHexString(SHA256.HashData(zip)).ToLowerInvariant();
        if (got != p.Sha256)
            return $"the {p.Name} v{p.Version} download is not the release Pipe Dream knows (SHA-256 {got}, expected {p.Sha256}) — not installed";
        string dir = Folder(p), tmp = dir + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var archive = new ZipArchive(new MemoryStream(zip)))
                archive.ExtractToDirectory(tmp);
            if (!File.Exists(Path.Combine(tmp, p.Entry)))
                return $"the {p.Name} v{p.Version} release has no {p.Entry}";
            Directory.CreateDirectory(Path.GetDirectoryName(dir)!);
            if (!Directory.Exists(dir)) Directory.Move(tmp, dir);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        { return $"could not install the {p.Name} v{p.Version} ({e.Message})"; }
        finally { if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true); }
    }

    /// <summary>The folder of the built-in patch a project's reference names, downloading it on a
    /// machine that does not have it yet; or why it cannot be had.</summary>
    public static (string? Folder, string? Error) Ensure(ProjectFile.BuiltinPatchRef r)
    {
        if (Find(r.Id, r.Version) is not { } p)
            return (null, $"this project uses built-in patch '{r.Id}' v{r.Version}, which this version of Pipe Dream does not know — update Pipe Dream");
        if (!string.Equals(r.Sha256, p.Sha256, StringComparison.OrdinalIgnoreCase))
            return (null, $"this project pins the {p.Name} v{p.Version} with a different SHA-256 than Pipe Dream knows ({r.Sha256})");
        if (!Installed(p) && Task.Run(() => Install(p)).GetAwaiter().GetResult() is { } err) return (null, err);
        return (Folder(p), null);
    }
}
