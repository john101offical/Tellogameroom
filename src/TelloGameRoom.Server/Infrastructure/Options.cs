namespace TelloGameRoom.Server.Infrastructure;

public sealed class StorageOptions
{
    public const string Section = "Storage";

    /// <summary>Directory for the database, generated keys, logs and backups.</summary>
    public string DataDirectory { get; set; } = "data";
}

public sealed class JwtOptions
{
    public const string Section = "Jwt";
    public const string Issuer = "TelloGameRoom.Server";
    public const string Audience = "TelloGameRoom.Staff";

    /// <summary>
    /// Base64 signing key (at least 32 bytes). Supply it through the environment (Jwt__SigningKey).
    /// When empty, a key is generated once and kept in the data directory.
    /// </summary>
    public string? SigningKey { get; set; }

    public int AccessTokenMinutes { get; set; } = 15;
    public int RefreshTokenDays { get; set; } = 30;
}

public sealed class SecurityOptions
{
    public const string Section = "Security";

    public int MaxFailedLogins { get; set; } = 5;
    public int LockoutMinutes { get; set; } = 15;

    /// <summary>Requests per minute per IP address on the login, refresh, setup and pairing endpoints.</summary>
    public int AuthRateLimitPerMinute { get; set; } = 20;

    public int PairingCodeMinutes { get; set; } = 15;

    /// <summary>Argon2id memory cost in KiB. 19 MiB / 2 passes / 1 lane is the OWASP minimum profile.</summary>
    public int Argon2MemoryKb { get; set; } = 19 * 1024;
    public int Argon2Iterations { get; set; } = 2;
    public int Argon2Parallelism { get; set; } = 1;

    /// <summary>Reverse proxies (IP addresses or CIDR ranges) whose X-Forwarded-* headers are trusted.</summary>
    public string[] TrustedProxies { get; set; } = [];
}
