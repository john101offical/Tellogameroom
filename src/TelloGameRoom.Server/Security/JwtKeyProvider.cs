using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using TelloGameRoom.Server.Infrastructure;

namespace TelloGameRoom.Server.Security;

/// <summary>
/// Supplies the JWT signing key. Uses the configured key when present; otherwise generates one on first start
/// and stores it in the data directory (owner-only permissions on Linux) so tokens survive restarts.
/// </summary>
public sealed class JwtKeyProvider
{
    private const string KeyFileName = "jwt-signing.key";

    public JwtKeyProvider(IOptions<JwtOptions> jwt, IOptions<StorageOptions> storage, ILogger<JwtKeyProvider> logger)
    {
        byte[] key;
        if (!string.IsNullOrWhiteSpace(jwt.Value.SigningKey))
        {
            key = Convert.FromBase64String(jwt.Value.SigningKey);
        }
        else
        {
            var path = Path.Combine(storage.Value.DataDirectory, KeyFileName);
            if (File.Exists(path))
            {
                key = Convert.FromBase64String(File.ReadAllText(path).Trim());
            }
            else
            {
                key = RandomNumberGenerator.GetBytes(64);
                Directory.CreateDirectory(storage.Value.DataDirectory);
                File.WriteAllText(path, Convert.ToBase64String(key));
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }

                logger.LogInformation("Generated a new JWT signing key at {Path}", path);
            }
        }

        if (key.Length < 32)
        {
            throw new InvalidOperationException("The JWT signing key must be at least 32 bytes.");
        }

        Key = new SymmetricSecurityKey(key);
    }

    public SymmetricSecurityKey Key { get; }
}
