using Microsoft.EntityFrameworkCore;
using TelloGameRoom.Core.Dtos;
using TelloGameRoom.Core.Protocol;
using TelloGameRoom.Server.Data;
using TelloGameRoom.Server.Infrastructure;
using TelloGameRoom.Server.Security;
using TelloGameRoom.Server.Services;

namespace TelloGameRoom.Server.Endpoints;

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/auth").WithTags("Auth");

        group.MapPost("/login", async (LoginRequest request, AuthService auth, CancellationToken ct) =>
            ToResult(await auth.LoginAsync(request, ct))).RequireRateLimiting(RateLimits.Auth);

        group.MapPost("/refresh", async (RefreshRequest request, AuthService auth, CancellationToken ct) =>
            ToResult(await auth.RefreshAsync(request.RefreshToken, ct))).RequireRateLimiting(RateLimits.Auth);

        group.MapPost("/logout", async (LogoutRequest request, AuthService auth, CancellationToken ct) =>
        {
            await auth.LogoutAsync(request.RefreshToken, ct);
            return Results.NoContent();
        });

        group.MapGet("/me", async (CurrentUserAccessor accessor, AuthService auth, AppDbContext db, CancellationToken ct) =>
        {
            var current = await accessor.RequireAsync(ct);
            var user = await db.Users.AsNoTracking().FirstAsync(u => u.Id == current.Id, ct);
            return Results.Ok(await auth.BuildUserInfoAsync(user, ct));
        }).RequireAuthorization(Policies.Staff);

        group.MapPost("/change-password", async (ChangePasswordRequest request, CurrentUserAccessor accessor, AuthService auth, CancellationToken ct) =>
        {
            if (!SetupEndpoints.IsAcceptablePassword(request.NewPassword))
            {
                return new Validator().Check(false, nameof(request.NewPassword),
                    $"Must be between {SetupEndpoints.MinPasswordLength} and {SetupEndpoints.MaxPasswordLength} characters.").ToProblem();
            }

            var current = await accessor.RequireAsync(ct);
            return await auth.ChangePasswordAsync(current.Id, request, ct)
                ? Results.NoContent()
                : ApiProblem.Create(StatusCodes.Status400BadRequest, ErrorCodes.InvalidCredentials, "The current password is wrong.");
        }).RequireAuthorization(Policies.Staff).RequireRateLimiting(RateLimits.Auth);
    }

    private static IResult ToResult(AuthOutcome outcome) => outcome.Failure switch
    {
        AuthFailure.None => Results.Ok(outcome.Response),
        AuthFailure.AccountLocked => Results.Problem(
            detail: "Too many failed attempts. Try again later.",
            statusCode: StatusCodes.Status423Locked,
            extensions: new Dictionary<string, object?> { ["code"] = ErrorCodes.AccountLocked, ["lockedUntilUtc"] = outcome.LockedUntilUtc }),
        AuthFailure.AccountDisabled => ApiProblem.Create(StatusCodes.Status403Forbidden, ErrorCodes.AccountDisabled, "This account has been deactivated."),
        AuthFailure.InvalidRefreshToken => ApiProblem.Create(StatusCodes.Status401Unauthorized, ErrorCodes.InvalidRefreshToken, "The session has expired. Sign in again."),
        _ => ApiProblem.Create(StatusCodes.Status401Unauthorized, ErrorCodes.InvalidCredentials, "Wrong username or password."),
    };
}
