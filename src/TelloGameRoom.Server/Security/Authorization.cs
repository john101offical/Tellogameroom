using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using TelloGameRoom.Core.Enums;
using TelloGameRoom.Core.Security;
using TelloGameRoom.Server.Data;
using TelloGameRoom.Server.Infrastructure;

namespace TelloGameRoom.Server.Security;

/// <summary>The authenticated staff member, re-read from the database on every request.</summary>
public sealed record CurrentUser(Guid Id, string Username, UserRole Role, IReadOnlySet<Permission> Permissions)
{
    public bool Has(Permission permission) => Permissions.Contains(permission);
}

public static class PrincipalExtensions
{
    public static Guid? GetUserId(this ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue(TelloClaims.UserId), out var id) ? id : null;

    public static Guid? GetMachineId(this ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue(TelloClaims.MachineId), out var id) ? id : null;
}

/// <summary>Resolves effective permissions, caching the owner-configured employee overrides.</summary>
public sealed class PermissionService(IServiceScopeFactory scopes)
{
    private volatile IReadOnlyDictionary<Permission, bool>? _overrides;

    public async ValueTask<IReadOnlySet<Permission>> GetEffectiveAsync(UserRole role, CancellationToken ct = default)
    {
        if (role == UserRole.Owner)
        {
            return PermissionPolicy.All;
        }

        var overrides = _overrides;
        if (overrides is null)
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            overrides = await db.EmployeePermissionOverrides.AsNoTracking().ToDictionaryAsync(o => o.Permission, o => o.Allowed, ct);
            _overrides = overrides;
        }

        return PermissionPolicy.Resolve(role, overrides);
    }

    /// <summary>Call after the overrides table changes.</summary>
    public void Invalidate() => _overrides = null;
}

/// <summary>
/// Loads the caller from the database once per request. A deactivated user or a changed role takes effect
/// immediately, without waiting for the access token to expire.
/// </summary>
public sealed class CurrentUserAccessor(IHttpContextAccessor http, AppDbContext db, PermissionService permissions)
{
    private bool _loaded;
    private CurrentUser? _user;

    public async ValueTask<CurrentUser?> GetAsync(CancellationToken ct = default) =>
        await GetAsync(http.HttpContext?.User, ct);

    public async ValueTask<CurrentUser?> GetAsync(ClaimsPrincipal? principal, CancellationToken ct = default)
    {
        if (_loaded)
        {
            return _user;
        }

        _loaded = true;
        if (principal?.GetUserId() is not { } id)
        {
            return null;
        }

        var row = await db.Users.AsNoTracking()
            .Where(u => u.Id == id && u.IsActive)
            .Select(u => new { u.Id, u.Username, u.Role })
            .FirstOrDefaultAsync(ct);
        if (row is not null)
        {
            _user = new CurrentUser(row.Id, row.Username, row.Role, await permissions.GetEffectiveAsync(row.Role, ct));
        }

        return _user;
    }

    public async ValueTask<CurrentUser> RequireAsync(CancellationToken ct = default) =>
        await GetAsync(ct) ?? throw new InvalidOperationException("No authenticated staff user on this request.");
}

public sealed record PermissionRequirement(Permission? Permission) : IAuthorizationRequirement;

/// <summary>Succeeds when the caller is an active staff member who holds the permission (or any active staff member when none is named).</summary>
public sealed class PermissionHandler(CurrentUserAccessor accessor) : AuthorizationHandler<PermissionRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        var user = await accessor.GetAsync(context.User);
        if (user is not null && (requirement.Permission is null || user.Has(requirement.Permission.Value)))
        {
            context.Succeed(requirement);
        }
    }
}

public static class Policies
{
    /// <summary>Any active staff member.</summary>
    public const string Staff = "staff";

    /// <summary>A paired, enabled machine.</summary>
    public const string Machine = "machine";

    public static string For(Permission permission) => "perm:" + permission;

    public static void AddTelloPolicies(this AuthorizationOptions options)
    {
        options.AddPolicy(Staff, p => p
            .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
            .RequireAuthenticatedUser()
            .AddRequirements(new PermissionRequirement(null)));

        options.AddPolicy(Machine, p => p
            .AddAuthenticationSchemes(MachineAuthenticationHandler.SchemeName)
            .RequireAuthenticatedUser()
            .RequireClaim(TelloClaims.MachineId));

        foreach (var permission in PermissionPolicy.All)
        {
            options.AddPolicy(For(permission), p => p
                .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
                .RequireAuthenticatedUser()
                .AddRequirements(new PermissionRequirement(permission)));
        }
    }

    public static TBuilder RequirePermission<TBuilder>(this TBuilder builder, Permission permission)
        where TBuilder : IEndpointConventionBuilder => builder.RequireAuthorization(For(permission));

    /// <summary>
    /// Rejects the request unless the caller may act in the branch named by the <c>branchId</c> route value.
    /// Owners can use every active branch; employees only the branches they are assigned to.
    /// </summary>
    public static RouteHandlerBuilder RequireBranchAccess(this RouteHandlerBuilder builder) =>
        builder.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            if (!Guid.TryParse(http.Request.RouteValues["branchId"]?.ToString(), out var branchId))
            {
                return ApiProblem.NotFound();
            }

            var user = await http.RequestServices.GetRequiredService<CurrentUserAccessor>().RequireAsync(http.RequestAborted);
            var db = http.RequestServices.GetRequiredService<AppDbContext>();
            var allowed = user.Role == UserRole.Owner
                ? await db.Branches.AnyAsync(b => b.Id == branchId, http.RequestAborted)
                : await db.UserBranches.AnyAsync(ub => ub.UserId == user.Id && ub.BranchId == branchId && ub.Branch!.IsActive, http.RequestAborted);

            // 404 rather than 403: do not reveal which branch ids exist.
            return allowed ? await next(context) : ApiProblem.NotFound();
        });
}
