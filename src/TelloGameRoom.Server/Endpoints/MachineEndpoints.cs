using Microsoft.EntityFrameworkCore;
using TelloGameRoom.Core.Dtos;
using TelloGameRoom.Core.Protocol;
using TelloGameRoom.Core.Security;
using TelloGameRoom.Server.Data;
using TelloGameRoom.Server.Infrastructure;
using TelloGameRoom.Server.Security;
using TelloGameRoom.Server.Services;

namespace TelloGameRoom.Server.Endpoints;

public static class MachineEndpoints
{
    public static void MapMachineEndpoints(this RouteGroupBuilder api)
    {
        // Staff side, scoped to a branch.
        var branch = api.MapGroup("/branches/{branchId:guid}/machines").WithTags("Machines");

        branch.MapGet("/", async (Guid branchId, AppDbContext db, CancellationToken ct) =>
            Results.Ok(await db.Machines.AsNoTracking()
                .Where(m => m.BranchId == branchId)
                .OrderBy(m => m.Name)
                .Select(m => new MachineSummary(m.Id, m.BranchId, m.Name, m.GroupId, m.IsEnabled, m.CreatedAtUtc, m.LastSeenAtUtc))
                .ToListAsync(ct)))
            .RequirePermission(Permission.MachinesView).RequireBranchAccess();

        branch.MapPost("/pairing-codes", async (Guid branchId, CreatePairingCodeRequest request, PairingService pairing,
            CurrentUserAccessor accessor, CancellationToken ct) =>
        {
            var v = new Validator().Length(request.MachineName, nameof(request.MachineName), 1, 50);
            if (!v.IsValid)
            {
                return v.ToProblem();
            }

            var (response, error) = await pairing.CreateCodeAsync(branchId, request, await accessor.RequireAsync(ct), ct);
            return error switch
            {
                PairingError.NameTaken => ApiProblem.Conflict("A machine with this name already exists in the branch."),
                PairingError.GroupNotFound => ApiProblem.NotFound("The machine group does not exist."),
                _ => Results.Ok(response),
            };
        }).RequirePermission(Permission.MachinesManage).RequireBranchAccess();

        // Machine side.
        var machines = api.MapGroup("/machines").WithTags("Machines");

        machines.MapPost("/pair", async (PairMachineRequest request, PairingService pairing, CancellationToken ct) =>
        {
            var (response, error) = await pairing.PairAsync(request, ct);
            return error switch
            {
                PairingError.None => Results.Ok(response),
                PairingError.NameTaken => ApiProblem.Conflict("A machine with this name was registered in the meantime. Create a new pairing code."),
                _ => ApiProblem.Create(StatusCodes.Status400BadRequest, ErrorCodes.InvalidPairingCode, "The pairing code is wrong, expired or already used."),
            };
        }).RequireRateLimiting(RateLimits.Auth);

        machines.MapGet("/me", async (HttpContext http, AppDbContext db, TimeProvider time, CancellationToken ct) =>
        {
            var machineId = http.User.GetMachineId()!.Value;
            var shopName = await db.ShopSettings.Select(s => s.ShopName).FirstAsync(ct);
            var identity = await db.Machines.AsNoTracking()
                .Where(m => m.Id == machineId)
                .Select(m => new MachineIdentity(m.Id, m.Name, m.BranchId, m.Branch!.Name, shopName, time.GetUtcNow().UtcDateTime))
                .FirstAsync(ct);
            return Results.Ok(identity);
        }).RequireAuthorization(Policies.Machine);
    }
}
