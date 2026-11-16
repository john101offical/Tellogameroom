using TelloGameRoom.Core.Dtos;
using TelloGameRoom.Core.Security;
using TelloGameRoom.Server.Infrastructure;
using TelloGameRoom.Server.Security;
using TelloGameRoom.Server.Services;

namespace TelloGameRoom.Server.Endpoints;

public static class SessionEndpoints
{
    public static void MapSessionEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/branches/{branchId:guid}/state", async (Guid branchId, StateService state, CancellationToken ct) =>
                await state.GetBranchStateAsync(branchId, ct) is { } dto ? Results.Ok(dto) : ApiProblem.NotFound())
            .WithTags("Sessions").RequirePermission(Permission.MachinesView).RequireBranchAccess();

        var group = api.MapGroup("/branches/{branchId:guid}/machines/{machineId:guid}").WithTags("Sessions");

        group.MapGet("/quote", async (Guid branchId, Guid machineId, long seconds, SessionService sessions, CancellationToken ct) =>
                (await sessions.QuoteAsync(branchId, machineId, seconds, ct)).ToResult())
            .RequirePermission(Permission.SessionsStart).RequireBranchAccess();

        group.MapPost("/session/start", async (Guid branchId, Guid machineId, StartSessionRequest request, SessionService sessions,
                CurrentUserAccessor accessor, CancellationToken ct) =>
                (await sessions.StartAsync(branchId, machineId, request, await accessor.RequireAsync(ct), ct)).ToResult())
            .RequirePermission(Permission.SessionsStart).RequireBranchAccess();

        group.MapPost("/session/add-time", async (Guid branchId, Guid machineId, AdjustTimeRequest request, SessionService sessions,
                CurrentUserAccessor accessor, CancellationToken ct) =>
                (await sessions.AddTimeAsync(branchId, machineId, request, await accessor.RequireAsync(ct), ct)).ToResult())
            .RequirePermission(Permission.SessionsAddTime).RequireBranchAccess();

        group.MapPost("/session/remove-time", async (Guid branchId, Guid machineId, AdjustTimeRequest request, SessionService sessions,
                CurrentUserAccessor accessor, CancellationToken ct) =>
                (await sessions.RemoveTimeAsync(branchId, machineId, request, await accessor.RequireAsync(ct), ct)).ToResult())
            .RequirePermission(Permission.SessionsRemoveTime).RequireBranchAccess();

        group.MapPost("/session/pause", async (Guid branchId, Guid machineId, SessionService sessions, CurrentUserAccessor accessor, CancellationToken ct) =>
                (await sessions.PauseAsync(branchId, machineId, await accessor.RequireAsync(ct), ct)).ToResult())
            .RequirePermission(Permission.SessionsPauseResume).RequireBranchAccess();

        group.MapPost("/session/resume", async (Guid branchId, Guid machineId, SessionService sessions, CurrentUserAccessor accessor, CancellationToken ct) =>
                (await sessions.ResumeAsync(branchId, machineId, await accessor.RequireAsync(ct), ct)).ToResult())
            .RequirePermission(Permission.SessionsPauseResume).RequireBranchAccess();

        group.MapPost("/session/stop", async (Guid branchId, Guid machineId, EndSessionRequest request, SessionService sessions,
                CurrentUserAccessor accessor, CancellationToken ct) =>
                (await sessions.StopAsync(branchId, machineId, request, await accessor.RequireAsync(ct), ct)).ToResult())
            .RequirePermission(Permission.SessionsStop).RequireBranchAccess();

        group.MapPost("/session/cancel", async (Guid branchId, Guid machineId, EndSessionRequest request, SessionService sessions,
                CurrentUserAccessor accessor, CancellationToken ct) =>
                (await sessions.CancelAsync(branchId, machineId, request, await accessor.RequireAsync(ct), ct)).ToResult())
            .RequirePermission(Permission.SessionsCancelPaid).RequireBranchAccess();

        group.MapPost("/session/transfer", async (Guid branchId, Guid machineId, TransferSessionRequest request, SessionService sessions,
                CurrentUserAccessor accessor, CancellationToken ct) =>
                (await sessions.TransferAsync(branchId, machineId, request, await accessor.RequireAsync(ct), ct)).ToResult())
            .RequirePermission(Permission.SessionsTransfer).RequireBranchAccess();
    }
}
