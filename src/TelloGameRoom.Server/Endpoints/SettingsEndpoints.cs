using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using TelloGameRoom.Core.Dtos;
using TelloGameRoom.Core.Enums;
using TelloGameRoom.Core.Protocol;
using TelloGameRoom.Core.Security;
using TelloGameRoom.Server.Data;
using TelloGameRoom.Server.Data.Entities;
using TelloGameRoom.Server.Infrastructure;
using TelloGameRoom.Server.Realtime;
using TelloGameRoom.Server.Reports;
using TelloGameRoom.Server.Security;
using TelloGameRoom.Server.Services;

namespace TelloGameRoom.Server.Endpoints;

public static partial class SettingsEndpoints
{
    private const int MaxLogoBytes = 1024 * 1024;
    private const long MaxRestoreBytes = 2L * 1024 * 1024 * 1024;
    private static readonly string[] Languages = ["en", "es"];

    [GeneratedRegex("^[0-9]{4,12}$")]
    private static partial Regex PinPattern();

    public static void MapSettingsEndpoints(this RouteGroupBuilder api)
    {
        MapShop(api);
        MapBranches(api);
        MapMachineAdmin(api);
        MapBackup(api);
    }

    private static void MapShop(RouteGroupBuilder api)
    {
        var shop = api.MapGroup("/settings/shop").WithTags("Settings");

        shop.MapGet("/", async (AppDbContext db, CancellationToken ct) =>
            await db.ShopSettings.AsNoTracking().FirstOrDefaultAsync(ct) is { } s ? Results.Ok(ToDto(s)) : ApiProblem.NotFound())
            .RequireAuthorization(Policies.Staff);

        shop.MapPut("/", async (UpdateShopSettingsRequest request, AppDbContext db, AuditService audit, CurrentUserAccessor accessor,
            RealtimeNotifier notifier, TimeProvider time, CancellationToken ct) =>
        {
            var v = new Validator()
                .Length(request.ShopName, nameof(request.ShopName), 1, 100)
                .Check(Languages.Contains(request.DefaultLanguage), nameof(request.DefaultLanguage), "Choose English or Spanish.");
            if (!v.IsValid)
            {
                return v.ToProblem();
            }

            var user = await accessor.RequireAsync(ct);
            var s = await db.ShopSettings.FirstAsync(ct);
            audit.Record(AuditActions.SettingsChanged, user.Id, user.Username, oldValue: $"{s.ShopName}; {s.DefaultLanguage}",
                newValue: $"{request.ShopName.Trim()}; {request.DefaultLanguage}");
            s.ShopName = request.ShopName.Trim();
            s.DefaultLanguage = request.DefaultLanguage;
            s.UpdatedAtUtc = time.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync(ct);
            await BroadcastConfigAsync(db, notifier, null, ct);
            return Results.Ok(ToDto(s));
        }).RequirePermission(Permission.SettingsManage);

        // The logo is public: the lock screen shows it to everyone who walks past anyway.
        shop.MapGet("/logo", async (AppDbContext db, CancellationToken ct) =>
            await db.ShopSettings.AsNoTracking().Select(s => new { s.Logo, s.LogoContentType }).FirstOrDefaultAsync(ct) is { Logo: { } logo } row
                ? Results.File(logo, row.LogoContentType ?? "image/png")
                : ApiProblem.NotFound());

        shop.MapPut("/logo", async (HttpContext http, AppDbContext db, AuditService audit, CurrentUserAccessor accessor,
            RealtimeNotifier notifier, TimeProvider time, CancellationToken ct) =>
        {
            if (http.Request.ContentLength is null or <= 0 or > MaxLogoBytes)
            {
                return new Validator().Check(false, "logo", "The logo must be a PNG or JPEG of at most 1 MB.").ToProblem();
            }

            using var buffer = new MemoryStream();
            await http.Request.Body.CopyToAsync(buffer, ct);
            var bytes = buffer.ToArray();
            var type = SniffImage(bytes);
            if (type is null)
            {
                return new Validator().Check(false, "logo", "The logo must be a PNG or JPEG of at most 1 MB.").ToProblem();
            }

            var user = await accessor.RequireAsync(ct);
            var s = await db.ShopSettings.FirstAsync(ct);
            s.Logo = bytes;
            s.LogoContentType = type;
            s.UpdatedAtUtc = time.GetUtcNow().UtcDateTime;
            audit.Record(AuditActions.SettingsChanged, user.Id, user.Username, newValue: "logo updated");
            await db.SaveChangesAsync(ct);
            await BroadcastConfigAsync(db, notifier, null, ct);
            return Results.Ok(ToDto(s));
        }).RequirePermission(Permission.SettingsManage);

        shop.MapDelete("/logo", async (AppDbContext db, AuditService audit, CurrentUserAccessor accessor, RealtimeNotifier notifier,
            TimeProvider time, CancellationToken ct) =>
        {
            var user = await accessor.RequireAsync(ct);
            var s = await db.ShopSettings.FirstAsync(ct);
            s.Logo = null;
            s.LogoContentType = null;
            s.UpdatedAtUtc = time.GetUtcNow().UtcDateTime;
            audit.Record(AuditActions.SettingsChanged, user.Id, user.Username, newValue: "logo removed");
            await db.SaveChangesAsync(ct);
            await BroadcastConfigAsync(db, notifier, null, ct);
            return Results.Ok(ToDto(s));
        }).RequirePermission(Permission.SettingsManage);
    }

    private static void MapBranches(RouteGroupBuilder api)
    {
        var branches = api.MapGroup("/branches").WithTags("Settings");

        // Everyone sees the branches they can work in; the owner sees all, including inactive ones.
        branches.MapGet("/", async (AppDbContext db, CurrentUserAccessor accessor, CancellationToken ct) =>
        {
            var user = await accessor.RequireAsync(ct);
            var query = user.Role == UserRole.Owner
                ? db.Branches.AsNoTracking()
                : db.UserBranches.AsNoTracking().Where(ub => ub.UserId == user.Id && ub.Branch!.IsActive).Select(ub => ub.Branch!);
            return Results.Ok((await query.OrderBy(b => b.Name).ToListAsync(ct)).ConvertAll(ToDto));
        }).RequireAuthorization(Policies.Staff);

        branches.MapPost("/", async (CreateBranchRequest request, AppDbContext db, AuditService audit, CurrentUserAccessor accessor,
            TimeProvider time, CancellationToken ct) =>
        {
            var v = new Validator().Length(request.Name, nameof(request.Name), 1, 100);
            if (!v.IsValid)
            {
                return v.ToProblem();
            }

            var name = request.Name.Trim();
            if (await db.Branches.AnyAsync(b => b.Name == name, ct))
            {
                return ApiProblem.Conflict("A branch with this name already exists.");
            }

            // A new branch starts with the pricing and currency of the first one, which is almost always what is wanted.
            var template = await db.Branches.AsNoTracking().OrderBy(b => b.CreatedAtUtc).FirstAsync(ct);
            var branch = new Branch
            {
                Id = Guid.CreateVersion7(),
                Name = name,
                TimeZoneId = template.TimeZoneId,
                HourlyRateCents = template.HourlyRateCents,
                CurrencyCode = template.CurrencyCode,
                CurrencySymbol = template.CurrencySymbol,
                CurrencyDecimals = template.CurrencyDecimals,
                RoundingMode = template.RoundingMode,
                RoundingIncrementCents = template.RoundingIncrementCents,
                WarningThresholdSeconds = template.WarningThresholdSeconds,
                CreatedAtUtc = time.GetUtcNow().UtcDateTime,
            };
            db.Branches.Add(branch);
            var user = await accessor.RequireAsync(ct);
            audit.Record(AuditActions.BranchCreated, user.Id, user.Username, branch.Id, newValue: name);
            await db.SaveChangesAsync(ct);
            return Results.Ok(ToDto(branch));
        }).RequirePermission(Permission.BranchesManage);

        branches.MapPut("/{branchId:guid}/settings", async (Guid branchId, UpdateBranchSettingsRequest r, AppDbContext db, AuditService audit,
            CurrentUserAccessor accessor, RealtimeNotifier notifier, CancellationToken ct) =>
        {
            var v = new Validator()
                .Length(r.Name, nameof(r.Name), 1, 100)
                .Check(r.HourlyRateCents is >= 0 and <= 10_000_000_000, nameof(r.HourlyRateCents), "The rate cannot be negative.")
                .Check(r.CurrencyCode is { Length: 3 }, nameof(r.CurrencyCode), "Use a 3-letter currency code.")
                .Check(r.CurrencySymbol is { Length: <= 5 }, nameof(r.CurrencySymbol), "Use at most 5 characters.")
                .Check(r.CurrencyDecimals is >= 0 and <= 2, nameof(r.CurrencyDecimals), "Use 0, 1 or 2 decimals.")
                .Check(Enum.IsDefined(r.RoundingMode), nameof(r.RoundingMode), "Unknown rounding mode.")
                .Check(r.RoundingIncrementCents is >= 0 and <= 100_000_000, nameof(r.RoundingIncrementCents), "Invalid rounding step.")
                .Check(r.RoundingMode == RoundingMode.None || r.RoundingIncrementCents >= 1, nameof(r.RoundingIncrementCents), "Choose a rounding step.")
                .Check(r.WarningThresholdSeconds is >= 0 and <= 3600, nameof(r.WarningThresholdSeconds), "Use between 0 and 60 minutes.")
                .Check(r.TimeZoneId is not null && TimeZoneInfo.TryFindSystemTimeZoneById(r.TimeZoneId, out _), nameof(r.TimeZoneId), "Unknown time zone.")
                .Check(r.NewAdminPin is null or "" || PinPattern().IsMatch(r.NewAdminPin), nameof(r.NewAdminPin), "Use 4 to 12 digits.");
            if (!v.IsValid)
            {
                return v.ToProblem();
            }

            var branch = await db.Branches.FirstOrDefaultAsync(b => b.Id == branchId, ct);
            if (branch is null)
            {
                return ApiProblem.NotFound();
            }

            var name = r.Name.Trim();
            if (await db.Branches.AnyAsync(b => b.Id != branchId && b.Name == name, ct))
            {
                return ApiProblem.Conflict("A branch with this name already exists.");
            }

            var user = await accessor.RequireAsync(ct);
            if (branch.HourlyRateCents != r.HourlyRateCents)
            {
                audit.Record(AuditActions.PriceChanged, user.Id, user.Username, branchId,
                    oldValue: $"{branch.HourlyRateCents}c/h", newValue: $"{r.HourlyRateCents}c/h");
            }

            if (r.NewAdminPin is not null)
            {
                branch.AdminPinHash = r.NewAdminPin.Length == 0 ? null : PinHasher.Hash(r.NewAdminPin);
                audit.Record(AuditActions.AdminPinChanged, user.Id, user.Username, branchId, newValue: r.NewAdminPin.Length == 0 ? "removed" : "set");
            }

            var before = Describe(branch);
            branch.Name = name;
            branch.IsActive = r.IsActive;
            branch.TimeZoneId = r.TimeZoneId!;
            branch.HourlyRateCents = r.HourlyRateCents;
            branch.CurrencyCode = r.CurrencyCode.ToUpperInvariant();
            branch.CurrencySymbol = r.CurrencySymbol.Trim();
            branch.CurrencyDecimals = r.CurrencyDecimals;
            branch.RoundingMode = r.RoundingMode;
            branch.RoundingIncrementCents = r.RoundingIncrementCents;
            branch.WarningThresholdSeconds = r.WarningThresholdSeconds;
            var after = Describe(branch);
            if (before != after)
            {
                audit.Record(AuditActions.BranchUpdated, user.Id, user.Username, branchId, oldValue: before, newValue: after);
            }

            await db.SaveChangesAsync(ct);
            await notifier.BranchChangedAsync(branchId);
            await BroadcastConfigAsync(db, notifier, branchId, ct);
            return Results.Ok(ToDto(branch));
        }).RequirePermission(Permission.SettingsManage);

        var groups = api.MapGroup("/branches/{branchId:guid}/groups").WithTags("Settings");

        groups.MapGet("/", async (Guid branchId, AppDbContext db, CancellationToken ct) =>
            Results.Ok(await db.MachineGroups.AsNoTracking().Where(g => g.BranchId == branchId).OrderBy(g => g.Name)
                .Select(g => new MachineGroupDto(g.Id, g.Name, g.HourlyRateCents)).ToListAsync(ct)))
            .RequirePermission(Permission.MachinesView).RequireBranchAccess();

        groups.MapPost("/", async (Guid branchId, UpsertMachineGroupRequest request, AppDbContext db, AuditService audit,
            CurrentUserAccessor accessor, RealtimeNotifier notifier, CancellationToken ct) =>
            await UpsertGroupAsync(branchId, null, request, db, audit, accessor, notifier, ct))
            .RequirePermission(Permission.PricingManage).RequireBranchAccess();

        groups.MapPut("/{groupId:guid}", async (Guid branchId, Guid groupId, UpsertMachineGroupRequest request, AppDbContext db, AuditService audit,
            CurrentUserAccessor accessor, RealtimeNotifier notifier, CancellationToken ct) =>
            await UpsertGroupAsync(branchId, groupId, request, db, audit, accessor, notifier, ct))
            .RequirePermission(Permission.PricingManage).RequireBranchAccess();

        groups.MapDelete("/{groupId:guid}", async (Guid branchId, Guid groupId, AppDbContext db, AuditService audit,
            CurrentUserAccessor accessor, RealtimeNotifier notifier, CancellationToken ct) =>
        {
            var group = await db.MachineGroups.FirstOrDefaultAsync(g => g.Id == groupId && g.BranchId == branchId, ct);
            if (group is null)
            {
                return ApiProblem.NotFound();
            }

            var user = await accessor.RequireAsync(ct);
            db.MachineGroups.Remove(group);
            audit.Record(AuditActions.GroupChanged, user.Id, user.Username, branchId, oldValue: group.Name, newValue: "deleted");
            await db.SaveChangesAsync(ct);
            await notifier.BranchChangedAsync(branchId);
            return Results.NoContent();
        }).RequirePermission(Permission.PricingManage).RequireBranchAccess();
    }

    private static void MapMachineAdmin(RouteGroupBuilder api)
    {
        var machines = api.MapGroup("/branches/{branchId:guid}/machines/{machineId:guid}").WithTags("Machines");

        machines.MapPut("/", async (Guid branchId, Guid machineId, UpdateMachineRequest request, AppDbContext db, AuditService audit,
            CurrentUserAccessor accessor, RealtimeNotifier notifier, CancellationToken ct) =>
        {
            var v = new Validator().Length(request.Name, nameof(request.Name), 1, 50);
            if (!v.IsValid)
            {
                return v.ToProblem();
            }

            var machine = await db.Machines.FirstOrDefaultAsync(m => m.Id == machineId && m.BranchId == branchId, ct);
            if (machine is null)
            {
                return ApiProblem.NotFound();
            }

            var name = request.Name.Trim();
            if (await db.Machines.AnyAsync(m => m.BranchId == branchId && m.Id != machineId && m.Name == name, ct))
            {
                return ApiProblem.Conflict("A machine with this name already exists in the branch.");
            }

            if (request.GroupId is { } groupId && !await db.MachineGroups.AnyAsync(g => g.Id == groupId && g.BranchId == branchId, ct))
            {
                return ApiProblem.NotFound("The machine group does not exist.");
            }

            var user = await accessor.RequireAsync(ct);
            audit.Record(AuditActions.MachineUpdated, user.Id, user.Username, branchId, machineId, name,
                oldValue: $"{machine.Name}; enabled={machine.IsEnabled}", newValue: $"{name}; enabled={request.IsEnabled}");
            machine.Name = name;
            machine.GroupId = request.GroupId;
            machine.IsEnabled = request.IsEnabled;
            await db.SaveChangesAsync(ct);
            await notifier.MachineChangedAsync(machineId, ct);
            await notifier.MachineConfigChangedAsync(machineId, ct);
            return Results.NoContent();
        }).RequirePermission(Permission.MachinesManage).RequireBranchAccess();

        // A machine with history cannot be deleted (the ledger refers to it); disable it instead.
        machines.MapDelete("/", async (Guid branchId, Guid machineId, AppDbContext db, AuditService audit, CurrentUserAccessor accessor,
            RealtimeNotifier notifier, CancellationToken ct) =>
        {
            var machine = await db.Machines.FirstOrDefaultAsync(m => m.Id == machineId && m.BranchId == branchId, ct);
            if (machine is null)
            {
                return ApiProblem.NotFound();
            }

            if (await db.Sessions.AnyAsync(s => s.MachineId == machineId, ct) || await db.SessionEvents.AnyAsync(e => e.MachineId == machineId, ct))
            {
                return ApiProblem.Conflict("This machine has session history and cannot be deleted. Disable it instead.");
            }

            var user = await accessor.RequireAsync(ct);
            db.Machines.Remove(machine);
            audit.Record(AuditActions.MachineDeleted, user.Id, user.Username, branchId, machineId, machine.Name);
            await db.SaveChangesAsync(ct);
            await notifier.MachineRemovedAsync(branchId, machineId);
            return Results.NoContent();
        }).RequirePermission(Permission.MachinesManage).RequireBranchAccess();

        // Issues a pairing code that re-registers an existing machine (e.g. after reinstalling Windows). Its old token stops working once the code is used.
        machines.MapPost("/repair-code", async (Guid branchId, Guid machineId, PairingService pairing, CurrentUserAccessor accessor, CancellationToken ct) =>
            await pairing.CreateRepairCodeAsync(branchId, machineId, await accessor.RequireAsync(ct), ct) is { } code
                ? Results.Ok(code)
                : ApiProblem.NotFound())
            .RequirePermission(Permission.MachinesManage).RequireBranchAccess();
    }

    private static void MapBackup(RouteGroupBuilder api)
    {
        var admin = api.MapGroup("/admin").WithTags("Admin").RequirePermission(Permission.BackupManage);

        admin.MapGet("/backup", async (BackupService backups, AppDbContext db, AuditService audit, CurrentUserAccessor accessor, CancellationToken ct) =>
        {
            var user = await accessor.RequireAsync(ct);
            audit.Record(AuditActions.BackupCreated, user.Id, user.Username);
            await db.SaveChangesAsync(ct);
            var path = await backups.CreateAsync("manual", ct);
            backups.Prune("manual", keep: 10);
            return Results.File(path, "application/octet-stream", Path.GetFileName(path));
        });

        admin.MapPost("/restore", async (HttpContext http, BackupService backups, AppDbContext db, AuditService audit,
            CurrentUserAccessor accessor, IHostApplicationLifetime lifetime, ILoggerFactory loggers, CancellationToken ct) =>
        {
            if (http.Request.ContentLength is null or <= 0 or > MaxRestoreBytes)
            {
                return new Validator().Check(false, "file", "Upload a backup file.").ToProblem();
            }

            if (await backups.StageRestoreAsync(http.Request.Body, ct) is { } error)
            {
                return new Validator().Check(false, "file", error).ToProblem();
            }

            var user = await accessor.RequireAsync(ct);
            audit.Record(AuditActions.BackupRestored, user.Id, user.Username);
            await db.SaveChangesAsync(ct);
            loggers.CreateLogger("Backup").LogWarning("Restore staged by {User}; the server is restarting to apply it", user.Username);

            // Stop shortly after the response is sent; the service manager (Docker or systemd) starts the server again.
            http.Response.OnCompleted(() =>
            {
                _ = Task.Delay(TimeSpan.FromMilliseconds(500), CancellationToken.None).ContinueWith(_ => lifetime.StopApplication(), TaskScheduler.Default);
                return Task.CompletedTask;
            });
            return Results.Accepted();
        }).DisableRequestTimeout().WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(MaxRestoreBytes));
    }

    private static async Task<IResult> UpsertGroupAsync(Guid branchId, Guid? groupId, UpsertMachineGroupRequest request, AppDbContext db,
        AuditService audit, CurrentUserAccessor accessor, RealtimeNotifier notifier, CancellationToken ct)
    {
        var v = new Validator()
            .Length(request.Name, nameof(request.Name), 1, 50)
            .Check(request.HourlyRateCents is null or (>= 0 and <= 10_000_000_000), nameof(request.HourlyRateCents), "The rate cannot be negative.");
        if (!v.IsValid)
        {
            return v.ToProblem();
        }

        var name = request.Name.Trim();
        if (await db.MachineGroups.AnyAsync(g => g.BranchId == branchId && g.Id != groupId && g.Name == name, ct))
        {
            return ApiProblem.Conflict("A group with this name already exists.");
        }

        MachineGroup? group;
        string? before = null;
        if (groupId is null)
        {
            group = new MachineGroup { Id = Guid.CreateVersion7(), BranchId = branchId, Name = name };
            db.MachineGroups.Add(group);
        }
        else
        {
            group = await db.MachineGroups.FirstOrDefaultAsync(g => g.Id == groupId && g.BranchId == branchId, ct);
            if (group is null)
            {
                return ApiProblem.NotFound();
            }

            before = $"{group.Name}; {group.HourlyRateCents}c/h";
        }

        group.Name = name;
        group.HourlyRateCents = request.HourlyRateCents;
        var user = await accessor.RequireAsync(ct);
        audit.Record(AuditActions.GroupChanged, user.Id, user.Username, branchId, oldValue: before, newValue: $"{name}; {request.HourlyRateCents}c/h");
        await db.SaveChangesAsync(ct);
        await notifier.BranchChangedAsync(branchId);
        return Results.Ok(new MachineGroupDto(group.Id, group.Name, group.HourlyRateCents));
    }

    /// <summary>Pushes fresh branding and settings to the machines of one branch, or of all branches.</summary>
    private static async Task BroadcastConfigAsync(AppDbContext db, RealtimeNotifier notifier, Guid? branchId, CancellationToken ct)
    {
        var ids = await db.Machines.Where(m => branchId == null || m.BranchId == branchId).Select(m => m.Id).ToListAsync(ct);
        foreach (var id in ids)
        {
            await notifier.MachineConfigChangedAsync(id, ct);
        }
    }

    private static string? SniffImage(byte[] b) =>
        b.Length > 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47 ? "image/png"
        : b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF ? "image/jpeg"
        : null;

    private static string Describe(Branch b) =>
        $"{b.Name}; active={b.IsActive}; {b.TimeZoneId}; {b.CurrencyCode} {b.CurrencySymbol} d{b.CurrencyDecimals}; {b.RoundingMode}/{b.RoundingIncrementCents}; warn={b.WarningThresholdSeconds}s";

    private static ShopSettingsDto ToDto(ShopSettings s) => new(s.ShopName, s.DefaultLanguage,
        s.Logo is null ? null : s.UpdatedAtUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private static BranchSettingsDto ToDto(Branch b) => new(b.Id, b.Name, b.IsActive, b.TimeZoneId, b.HourlyRateCents, b.CurrencyCode,
        b.CurrencySymbol, b.CurrencyDecimals, b.RoundingMode, b.RoundingIncrementCents, b.WarningThresholdSeconds, b.AdminPinHash is not null);
}
