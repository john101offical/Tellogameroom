using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using TelloGameRoom.Core.Dtos;
using TelloGameRoom.Core.Enums;
using TelloGameRoom.Core.Protocol;
using TelloGameRoom.Core.Security;
using TelloGameRoom.Server.Data;
using TelloGameRoom.Server.Security;
using TelloGameRoom.Server.Services;

namespace TelloGameRoom.Server.Realtime;

[Authorize(Policy = Policies.Staff)]
public sealed class DashboardHub(CurrentUserAccessor accessor, AppDbContext db, StateService state) : Hub<IDashboardClient>, IDashboardHub
{
    public async Task<BranchStateDto> Subscribe(Guid branchId)
    {
        // Hub invocations do not run endpoint filters, so the branch and permission checks are repeated here.
        var user = await accessor.GetAsync(Context.User, Context.ConnectionAborted)
            ?? throw new HubException(ErrorCodes.Forbidden);
        var allowed = user.Has(Permission.MachinesView) && (user.Role == UserRole.Owner
            ? await db.Branches.AnyAsync(b => b.Id == branchId)
            : await db.UserBranches.AnyAsync(ub => ub.UserId == user.Id && ub.BranchId == branchId && ub.Branch!.IsActive));
        if (!allowed)
        {
            throw new HubException(ErrorCodes.NotFound);
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, Realtime.Groups.Branch(branchId));
        return await state.GetBranchStateAsync(branchId, Context.ConnectionAborted) ?? throw new HubException(ErrorCodes.NotFound);
    }

    public Task Unsubscribe(Guid branchId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, Realtime.Groups.Branch(branchId));
}
