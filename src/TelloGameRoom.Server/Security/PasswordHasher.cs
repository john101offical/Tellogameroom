using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;
using Microsoft.Extensions.Options;
using TelloGameRoom.Server.Infrastructure;

namespace TelloGameRoom.Server.Security;

public interface IPasswordHasher
{
    string Hash(string password);

    /// <param name="needsRehash">True when the hash is valid but was made with weaker parameters than the current ones.</param>
    bool Verify(string password, string encodedHash, out bool needsRehash);

    /// <summary>Spends the same time as a real verification. Used for unknown usernames so timing does not reveal which accounts exist.</summary>
    void VerifyDummy(string password);
}

/// <summary>
/// Argon2id password hashing. Hashes are stored in the standard PHC string format
/// (<c>$argon2id$v=19$m=..,t=..,p=..$salt$hash</c>) so the cost parameters travel with each hash and can be raised later.
/// </summary>
public sealed class Argon2PasswordHasher(IOptions<SecurityOptions> options) : IPasswordHasher
{
    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private readonly SecurityOptions _options = options.Value;
    private string? _dummyHash;

    public void VerifyDummy(string password) => Verify(password, _dummyHash ??= Hash("dummy-password-for-timing"), out _);

    public string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Compute(password, salt, _options.Argon2MemoryKb, _options.Argon2Iterations, _options.Argon2Parallelism, HashBytes);
        return string.Create(CultureInfo.InvariantCulture,
            $"$argon2id$v=19$m={_options.Argon2MemoryKb},t={_options.Argon2Iterations},p={_options.Argon2Parallelism}${ToB64(salt)}${ToB64(hash)}");
    }

    public bool Verify(string password, string encodedHash, out bool needsRehash)
    {
        needsRehash = false;
        if (!TryParse(encodedHash, out var memory, out var iterations, out var parallelism, out var salt, out var expected))
        {
            return false;
        }

        var actual = Compute(password, salt, memory, iterations, parallelism, expected.Length);
        if (!CryptographicOperations.FixedTimeEquals(actual, expected))
        {
            return false;
        }

        needsRehash = memory < _options.Argon2MemoryKb || iterations < _options.Argon2Iterations;
        return true;
    }

    private static byte[] Compute(string password, byte[] salt, int memoryKb, int iterations, int parallelism, int length)
    {
        using var argon = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            MemorySize = memoryKb,
            Iterations = iterations,
            DegreeOfParallelism = parallelism,
        };
        return argon.GetBytes(length);
    }

    private static bool TryParse(string encoded, out int memory, out int iterations, out int parallelism, out byte[] salt, out byte[] hash)
    {
        memory = iterations = parallelism = 0;
        salt = hash = [];

        // ["", "argon2id", "v=19", "m=..,t=..,p=..", salt, hash]
        var parts = encoded.Split('$');
        if (parts.Length != 6 || parts[1] != "argon2id" || parts[2] != "v=19")
        {
            return false;
        }

        foreach (var pair in parts[3].Split(','))
        {
            var kv = pair.Split('=');
            if (kv.Length != 2 || !int.TryParse(kv[1], NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            {
                return false;
            }

            switch (kv[0])
            {
                case "m": memory = value; break;
                case "t": iterations = value; break;
                case "p": parallelism = value; break;
                default: return false;
            }
        }

        // Bound the parameters so a corrupted row cannot be used to exhaust memory or CPU.
        if (memory is < 8 or > 1024 * 1024 || iterations is < 1 or > 64 || parallelism is < 1 or > 16)
        {
            return false;
        }

        try
        {
            salt = FromB64(parts[4]);
            hash = FromB64(parts[5]);
        }
        catch (FormatException)
        {
            return false;
        }

        return salt.Length >= 8 && hash.Length >= 16;
    }

    private static string ToB64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=');

    private static byte[] FromB64(string value) =>
        Convert.FromBase64String(value.PadRight(value.Length + ((4 - (value.Length % 4)) % 4), '='));
}
