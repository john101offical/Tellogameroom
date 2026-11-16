using System.Reflection;
using System.Text.RegularExpressions;
using TelloGameRoom.Core.Dtos;
using TelloGameRoom.Core.Protocol;
using TelloGameRoom.Server.Infrastructure;
using TelloGameRoom.Server.Services;

namespace TelloGameRoom.Server.Endpoints;

public static partial class SetupEndpoints
{
    public const int MinPasswordLength = 8;
    public const int MaxPasswordLength = 128;

    [GeneratedRegex("^[a-zA-Z0-9._-]{3,32}$")]
    public static partial Regex UsernamePattern();

    public static readonly string ServerVersion =
        typeof(SetupEndpoints).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    public static void MapSetupEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/setup").WithTags("Setup");

        group.MapGet("/status", async (SetupService setup, CancellationToken ct) =>
            Results.Ok(new SetupStatusResponse(await setup.IsRequiredAsync(ct), ProtocolInfo.Version, ServerVersion)));

        group.MapPost("/", async (SetupRequest request, SetupService setup, CancellationToken ct) =>
        {
            var v = new Validator()
                .Length(request.ShopName, nameof(request.ShopName), 1, 100)
                .Length(request.BranchName, nameof(request.BranchName), 1, 100)
                .Length(request.OwnerDisplayName, nameof(request.OwnerDisplayName), 1, 100)
                .Check(request.OwnerUsername is not null && UsernamePattern().IsMatch(request.OwnerUsername), nameof(request.OwnerUsername),
                    "Use 3 to 32 letters, digits, dots, dashes or underscores.")
                .Check(IsAcceptablePassword(request.OwnerPassword), nameof(request.OwnerPassword),
                    $"Must be between {MinPasswordLength} and {MaxPasswordLength} characters.")
                .Check(request.HourlyRate is null or (>= 0 and <= 100_000_000), nameof(request.HourlyRate), "Must be zero or more.");
            if (!v.IsValid)
            {
                return v.ToProblem();
            }

            var response = await setup.RunAsync(request, ct);
            return response is null
                ? ApiProblem.Create(StatusCodes.Status409Conflict, ErrorCodes.SetupAlreadyCompleted, "This server has already been set up.")
                : Results.Ok(response);
        }).RequireRateLimiting(RateLimits.Auth);
    }

    public static bool IsAcceptablePassword(string? password) =>
        password is { Length: >= MinPasswordLength and <= MaxPasswordLength };
}
