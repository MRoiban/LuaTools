using System.Security.Cryptography;
namespace LuaTools.Agent;

public sealed record SubmittedFile(string Name, long Length, string Sha256);
public sealed record FileChunk(byte[] Bytes);
public static class FileSubmission
{
    public const int MaxFiles = 64;
    public const int MaxBytes = 64 * 1024 * 1024;
    public const int ChunkBytes = 24 * 1024;
    public static bool Valid(SubmittedFile f) => f.Length > 0 && f.Length <= MaxBytes && f.Name.Length is > 0 and <= 180
        && !f.Name.Any(c => c < 32 || "<>:\"/\\|?*".Contains(c)) && f.Name is not ("." or "..")
        && f.Sha256.Length == 64 && f.Sha256.All(Uri.IsHexDigit);
    public static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}
