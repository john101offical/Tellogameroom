using TelloGameRoom.Core.Dtos;
using TelloGameRoom.Core.Enums;
using TelloGameRoom.Core.Security;
using TelloGameRoom.Server.Infrastructure;
using TelloGameRoom.Server.Security;
using TelloGameRoom.Server.Services;

namespace TelloGameRoom.Server.Endpoints;

public static class RemoteEndpoints
{
    private const int MaxScreenshotBytes = 4 * 1024 * 1024;

    public static void MapRemoteEndpoints(this RouteGroupBuilder api)
    {
        var branch = api.MapGroup("/branches/{branchId:guid}").WithTags("Remote");

        branch.MapPost("/machines/{machineId:guid}/message", async (Guid branchId, Guid machineId, SendMessageRequest request,
            RemoteService remote, CurrentUserAccessor accessor, CancellationToken ct) =>
        {
            if (ValidateMessage(request) is { } problem)
            {
                return problem;
            }

            var result = await remote.SendMessageAsync(branchId, machineId, request.Text.Trim(), await accessor.RequireAsync(ct), ct);
            return result.IsSuccess ? Results.NoContent() : result.Error!.ToResult();
        }).RequirePermission(Permission.RemoteMessage).RequireBranchAccess();

        branch.MapPost("/message-all", async (Guid branchId, SendMessageRequest request, RemoteService remote,
            CurrentUserAccessor accessor, CancellationToken ct) =>
            ValidateMessage(request) ?? Results.Ok(await remote.SendMessageToAllAsync(branchId, request.Text.Trim(), await accessor.RequireAsync(ct), ct)))
            .RequirePermission(Permission.RemoteMessage).RequireBranchAccess();

        branch.MapPost("/machines/{machineId:guid}/command", async (Guid branchId, Guid machineId, MachineCommandRequest request,
            RemoteService remote, CurrentUserAccessor accessor, CancellationToken ct) =>
        {
            var user = await accessor.RequireAsync(ct);
            if (CheckCommand(request, user) is { } problem)
            {
                return problem;
            }

            var result = await remote.SendCommandAsync(branchId, machineId, request.Command, user, ct);
            return result.IsSuccess ? Results.NoContent() : result.Error!.ToResult();
        }).RequireAuthorization(Policies.Staff).RequireBranchAccess();

        branch.MapPost("/command-all", async (Guid branchId, MachineCommandRequest request, RemoteService remote,
            CurrentUserAccessor accessor, CancellationToken ct) =>
        {
            var user = await accessor.RequireAsync(ct);
            return CheckCommand(request, user) ?? Results.Ok(await remote.SendCommandToAllAsync(branchId, request.Command, user, ct));
        }).RequireAuthorization(Policies.Staff).RequireBranchAccess();

        branch.MapPost("/machines/{machineId:guid}/screenshot", async (Guid branchId, Guid machineId, RemoteService remote,
            CurrentUserAccessor accessor, CancellationToken ct) =>
        {
            var result = await remote.CaptureScreenshotAsync(branchId, machineId, await accessor.RequireAsync(ct), ct);
            return result.IsSuccess ? Results.File(result.Value!, "image/jpeg") : result.Error!.ToResult();
        }).RequirePermission(Permission.RemoteScreenshot).RequireBranchAccess();

        // Machine side: upload the captured image.
        api.MapPost("/machines/me/screenshots/{requestId}", async (string requestId, HttpContext http, ScreenshotBroker broker, CancellationToken ct) =>
        {
            if (http.Request.ContentLength is null or <= 0 or > MaxScreenshotBytes)
            {
                return ApiProblem.Create(StatusCodes.Status413PayloadTooLarge, Core.Protocol.ErrorCodes.Validation, "The screenshot is missing or too large.");
            }

            using var buffer = new MemoryStream((int)http.Request.ContentLength.Value);
            await http.Request.Body.CopyToAsync(buffer, ct);
            var image = buffer.ToArray();

            // Only accept real JPEG data; this ends up in front of the owner.
            if (image.Length < 4 || image[0] != 0xFF || image[1] != 0xD8)
            {
                return ApiProblem.Create(StatusCodes.Status400BadRequest, Core.Protocol.ErrorCodes.Validation, "The screenshot must be a JPEG image.");
            }

            return broker.Complete(requestId, http.User.GetMachineId()!.Value, image) ? Results.NoContent() : ApiProblem.NotFound();
        }).WithTags("Remote").RequireAuthorization(Policies.Machine);
    }

    private static IResult? ValidateMessage(SendMessageRequest request) =>
        string.IsNullOrWhiteSpace(request.Text) || request.Text.Trim().Length > RemoteService.MaxMessageLength
            ? new Validator().Check(false, nameof(request.Text), $"Write a message of at most {RemoteService.MaxMessageLength} characters.").ToProblem()
            : null;

    /// <summary>Lock needs RemoteLock; restart and shutdown need RemotePower.</summary>
    private static IResult? CheckCommand(MachineCommandRequest request, CurrentUser user)
    {
        if (!Enum.IsDefined(request.Command))
        {
            return new Validator().Check(false, nameof(request.Command), "Unknown command.").ToProblem();
        }

        var needed = request.Command == MachineCommand.Lock ? Permission.RemoteLock : Permission.RemotePower;
        return user.Has(needed) ? null : ApiProblem.Forbidden();
    }
}
