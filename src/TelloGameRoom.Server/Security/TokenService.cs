using System.Globalization;
using System.Security.Claims;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using TelloGameRoom.Server.Data.Entities;
using TelloGameRoom.Server.Infrastructure;

namespace TelloGameRoom.Server.Security;

/// <summary>Claim names used in staff access tokens and machine identities.</summary>
public static class TelloClaims
{
    public const string UserId = "sub";
    public const string Username = "name";
    public const string Role = "role";
    public const string MachineId = "machine_id";
    public const string BranchId = "branch_id";
}

public sealed class TokenService(JwtKeyProvider keys, IOptions<JwtOptions> options, TimeProvider time)
{
    private readonly JsonWebTokenHandler _handler = new();

    /// <summary>Creates a short-lived access token. Permissions are deliberately not embedded: they are resolved on every request.</summary>
    public (string Token, DateTime ExpiresAtUtc) CreateAccessToken(User user)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var expires = now.AddMinutes(options.Value.AccessTokenMinutes);
        var token = _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = JwtOptions.Issuer,
            Audience = JwtOptions.Audience,
            IssuedAt = now,
            NotBefore = now,
            Expires = expires,
            Subject = new ClaimsIdentity(
            [
                new Claim(TelloClaims.UserId, user.Id.ToString()),
                new Claim(TelloClaims.Username, user.Username),
                new Claim(TelloClaims.Role, ((int)user.Role).ToString(CultureInfo.InvariantCulture)),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            ]),
            SigningCredentials = new SigningCredentials(keys.Key, SecurityAlgorithms.HmacSha256),
        });
        return (token, expires);
    }
}
