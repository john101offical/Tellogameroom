using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TelloGameRoom.Core.Protocol;
using TelloGameRoom.Server.Data;

namespace TelloGameRoom.Server.Security;

/// <summary>
/// Authenticates machine apps with <c>Authorization: Machine {machineId}.{secret}</c>.
/// WebSocket clients cannot set headers in every environment, so the machine hub also accepts the same value
/// in the <c>access_token</c> query parameter.
/// </summary>
public sealed class MachineAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    AppDbContext db)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = ProtocolInfo.MachineAuthScheme;
    private const string HeaderPrefix = SchemeName + " ";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string? credential = null;
        var header = Request.Headers.Authorization.ToString();
        if (header.StartsWith(HeaderPrefix, StringComparison.OrdinalIgnoreCase))
        {
            credential = header[HeaderPrefix.Length..].Trim();
        }
        else if (Request.Path.StartsWithSegments(ProtocolInfo.MachineHubPath))
        {
            credential = Request.Query["access_token"];
        }

        if (string.IsNullOrEmpty(credential))
        {
            return AuthenticateResult.NoResult();
        }

        var separator = credential.IndexOf('.', StringComparison.Ordinal);
        if (separator <= 0 || !Guid.TryParse(credential.AsSpan(0, separator), out var machineId))
        {
            return AuthenticateResult.Fail("Malformed machine credential.");
        }

        var secret = credential[(separator + 1)..];
        var machine = await db.Machines.AsNoTracking()
            .Where(m => m.Id == machineId)
            .Select(m => new { m.Id, m.BranchId, m.Name, m.TokenHash, m.IsEnabled })
            .FirstOrDefaultAsync(Context.RequestAborted);

        if (machine is null || !SecretTokens.HashEquals(secret, machine.TokenHash))
        {
            return AuthenticateResult.Fail("Unknown machine or wrong secret.");
        }

        if (!machine.IsEnabled)
        {
            return AuthenticateResult.Fail("Machine is disabled.");
        }

        var identity = new ClaimsIdentity(
        [
            new Claim(TelloClaims.MachineId, machine.Id.ToString()),
            new Claim(TelloClaims.BranchId, machine.BranchId.ToString()),
            new Claim(TelloClaims.Username, machine.Name),
        ], SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }
}
