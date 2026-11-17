using TelloGameRoom.Core.Dtos;
using TelloGameRoom.Core.Security;
using TelloGameRoom.Server.Infrastructure;
using TelloGameRoom.Server.Security;
using TelloGameRoom.Server.Services;

namespace TelloGameRoom.Server.Endpoints;

public static class ShiftEndpoints
{
    public static void MapShiftEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/branches/{branchId:guid}/shifts").WithTags("Shifts");

        group.MapPost("/open", async (Guid branchId, OpenShiftRequest request, ShiftService shifts, CurrentUserAccessor accessor, CancellationToken ct) =>
                (await shifts.OpenAsync(branchId, request, await accessor.RequireAsync(ct), ct)).ToResult())
            .RequirePermission(Permission.ShiftsOwn).RequireBranchAccess();

        group.MapPost("/close", async (Guid branchId, CloseShiftRequest request, ShiftService shifts, CurrentUserAccessor accessor, CancellationToken ct) =>
                (await shifts.CloseOwnAsync(branchId, request, await accessor.RequireAsync(ct), ct)).ToResult())
            .RequirePermission(Permission.ShiftsOwn).RequireBranchAccess();

        group.MapGet("/current", async (Guid branchId, ShiftService shifts, CurrentUserAccessor accessor, CancellationToken ct) =>
                await shifts.GetCurrentAsync(branchId, (await accessor.RequireAsync(ct)).Id, ct) is { } shift ? Results.Ok(shift) : Results.NoContent())
            .RequirePermission(Permission.ShiftsOwn).RequireBranchAccess();

        group.MapGet("/", async (Guid branchId, DateTime? fromUtc, DateTime? toUtc, Guid? userId, ShiftService shifts,
                CurrentUserAccessor accessor, TimeProvider time, CancellationToken ct) =>
            {
                var to = toUtc?.ToUniversalTime() ?? time.GetUtcNow().UtcDateTime.AddDays(1);
                var from = fromUtc?.ToUniversalTime() ?? to.AddDays(-31);
                return Results.Ok(await shifts.ListAsync(branchId, from, to, userId, await accessor.RequireAsync(ct), ct));
            })
            .RequirePermission(Permission.ShiftsOwn).RequireBranchAccess();

        group.MapGet("/{shiftId:guid}", async (Guid branchId, Guid shiftId, ShiftService shifts, CurrentUserAccessor accessor, CancellationToken ct) =>
            {
                var user = await accessor.RequireAsync(ct);
                var shift = await shifts.GetAsync(shiftId, ct);
                return shift is null || shift.BranchId != branchId || (shift.UserId != user.Id && !user.Has(Permission.ShiftsViewAll))
                    ? ApiProblem.NotFound()
                    : Results.Ok(shift);
            })
            .RequirePermission(Permission.ShiftsOwn).RequireBranchAccess();

        group.MapPost("/{shiftId:guid}/close", async (Guid branchId, Guid shiftId, CloseShiftRequest request, ShiftService shifts,
                CurrentUserAccessor accessor, CancellationToken ct) =>
                (await shifts.CloseByIdAsync(branchId, shiftId, request, await accessor.RequireAsync(ct), ct)).ToResult())
            .RequirePermission(Permission.ShiftsViewAll).RequireBranchAccess();
    }
}
