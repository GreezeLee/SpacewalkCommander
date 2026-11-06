using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace SpacewalkCommander.GoogleHealth;

/// <summary>
/// Keeps the Google refresh token on disk, encrypted with Windows DPAPI for the current Windows user, so that
/// other users of the PC and copies of the file cannot use it.
/// </summary>
internal sealed class TokenStore
{
  /// <summary>File name of the encrypted token.</summary>
  private const string TokenFileName = "google-refresh-token.bin";

  /// <summary>Path of the encrypted token.</summary>
  private readonly string _path;

  /// <summary>
  /// Creates a store in <paramref name="directory"/>.
  /// </summary>
  /// <param name="directory">Folder of the token file.</param>
  public TokenStore(string directory)
  {
    _path = Path.Combine(directory, TokenFileName);
  }

  /// <summary>
  /// Reads the refresh token.
  /// </summary>
  /// <returns>The token, or <see langword="null"/> if none is stored or it cannot be decrypted.</returns>
  public string? Load()
  {
    if (!File.Exists(_path))
    {
      return null;
    }

    try
    {
      byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(_path), null, DataProtectionScope.CurrentUser);
      return Encoding.UTF8.GetString(plain);
    }
    catch (CryptographicException)
    {
      return null;
    }
  }

  /// <summary>
  /// Stores a refresh token, replacing the previous one.
  /// </summary>
  /// <param name="refreshToken">The token.</param>
  public void Save(string refreshToken)
  {
    Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
    byte[] encrypted = ProtectedData.Protect(
      Encoding.UTF8.GetBytes(refreshToken),
      null,
      DataProtectionScope.CurrentUser);
    File.WriteAllBytes(_path, encrypted);
  }

  /// <summary>
  /// Removes the stored token, for example after Google rejected it.
  /// </summary>
  public void Delete()
  {
    File.Delete(_path);
  }
}
