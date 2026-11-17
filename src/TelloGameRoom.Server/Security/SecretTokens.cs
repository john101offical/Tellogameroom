using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace TelloGameRoom.Server.Security;

/// <summary>
/// High-entropy random secrets (refresh tokens, machine tokens, pairing codes) and their storage hashes.
/// Because these secrets are random and at least 40 bits (pairing codes, short-lived and rate limited) or 256 bits
/// (tokens), a plain SHA-256 is the right storage hash: fast to verify, infeasible to reverse.
/// </summary>
public static class SecretTokens
{
    // No 0/O, 1/I/L: codes are read aloud and typed by hand.
    private const string PairingAlphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
    public const int PairingCodeLength = 8;

    public static string NewToken() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    public static string NewPairingCode() => RandomNumberGenerator.GetString(PairingAlphabet, PairingCodeLength);

    /// <summary>Formats a pairing code for display: ABCD-EFGH.</summary>
    public static string FormatPairingCode(string code) => $"{code[..4]}-{code[4..]}";

    /// <summary>Strips separators and normalizes case so "abcd-efgh" and "ABCDEFGH" match.</summary>
    public static string NormalizePairingCode(string input) =>
        new string([.. input.Where(char.IsLetterOrDigit)]).ToUpperInvariant();

    public static string Hash(string secret) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    public static bool HashEquals(string secret, string expectedHash) =>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Hash(secret)), Encoding.ASCII.GetBytes(expectedHash));
}
