using System.IO;
using System.Security.Cryptography;
using System.Text;
using GameShelf.Services;

namespace GameShelf.Launchers;

/// <summary>Epic answered, and refused (an unknown or expired code or token), as opposed to not being reachable.</summary>
internal sealed class EpicRefusedException(string message) : EpicException(message);

/// <summary>
/// The Epic refresh token, kept so the library can be refreshed without signing in again. It is encrypted with
/// Windows' DPAPI for the current user: another Windows account, or the file copied to another PC, cannot read it.
/// Only this one token is stored, never the password and never an access token. Anyone who runs code as this
/// Windows user could still use it, which is why <see cref="EpicClient.SignOut"/> deletes it.
/// </summary>
internal static class EpicCredentials
{
    const string FileName = "epic-auth.bin";
    static readonly byte[] Entropy = Encoding.UTF8.GetBytes("GameShelf.Epic.RefreshToken.v1");

    public static void Save(string refreshToken)
    {
        var path = AppData.PathOf(FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, ProtectedData.Protect(Encoding.UTF8.GetBytes(refreshToken), Entropy, DataProtectionScope.CurrentUser));
    }

    /// <summary>The stored token, or null if there is none or it cannot be decrypted (the file is then deleted).</summary>
    public static string? Load()
    {
        var path = AppData.PathOf(FileName);
        if (!File.Exists(path)) return null;
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser));
        }
        catch (CryptographicException)
        {
            Clear();
            return null;
        }
    }

    public static void Clear()
    {
        var path = AppData.PathOf(FileName);
        if (File.Exists(path)) File.Delete(path);
    }
}
