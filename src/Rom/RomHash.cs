using System.Security.Cryptography;

namespace PipeDream;

/// <summary>
/// ROM identity hashing for project base-ROM pinning. Always hashes the HEADERLESS
/// payload (512-byte copier header stripped, same detection rule as Rom.Load) so a
/// headered and headerless copy of the same ROM pin identically.
/// </summary>
public static class RomHash
{
    /// <summary>Super Mario World (U) [!], headerless payload SHA-256 (No-Intro).</summary>
    public const string VanillaUsSha256 =
        "0838e531fe22c077528febe14cb3ff7c492f1f5fa8de354192bdff7137c27f5b";

    public static string HeaderlessSha256(byte[] file)
    {
        int header = file.Length % 0x8000 == 512 ? 512 : 0;
        return Convert.ToHexStringLower(SHA256.HashData(file.AsSpan(header)));
    }

    public static string HeaderlessSha256File(string path) => HeaderlessSha256(File.ReadAllBytes(path));
}
