using System.Globalization;
using System.Security.Cryptography;

namespace TelloGameRoom.Core.Security;

/// <summary>
/// Hashes the machine admin PIN with PBKDF2-SHA256. It lives in Core (no external dependency) because the Machine app
/// must be able to check the PIN while offline. Format: <c>pbkdf2$iterations$salt$hash</c>.
/// </summary>
public static class PinHasher
{
    private const int Iterations = 210_000;

    public static string Hash(string pin)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(pin, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return string.Create(CultureInfo.InvariantCulture, $"pbkdf2${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}");
    }

    public static bool Verify(string pin, string? stored)
    {
        var parts = stored?.Split('$');
        if (parts is not { Length: 4 } || parts[0] != "pbkdf2"
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var iterations)
            || iterations is < 1000 or > 10_000_000)
        {
            return false;
        }

        try
        {
            var expected = Convert.FromBase64String(parts[3]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(pin, Convert.FromBase64String(parts[2]), iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
