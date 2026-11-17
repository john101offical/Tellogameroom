using System.Globalization;
using Microsoft.EntityFrameworkCore;
using TelloGameRoom.Core.Dtos;
using TelloGameRoom.Core.Protocol;
using TelloGameRoom.Core.Security;
using TelloGameRoom.Server.Data;
using TelloGameRoom.Server.Data.Entities;
using TelloGameRoom.Server.Infrastructure;
using TelloGameRoom.Server.Reports;
using TelloGameRoom.Server.Security;
using TelloGameRoom.Server.Services;

namespace TelloGameRoom.Server.Endpoints;

public static class AuditAndReportEndpoints
{
    private const int MaxExportRows = 50_000;

    public static void MapAuditEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/audit").WithTags("Audit").RequirePermission(Permission.AuditView);

        group.MapGet("/", async ([AsParameters] AuditQuery q, AppDbContext db, CancellationToken ct) =>
        {
            var page = Math.Max(1, q.Page ?? 1);
            var size = Math.Clamp(q.PageSize ?? 50, 1, 200);
            var query = Filter(db, q);
            var total = await query.CountAsync(ct);
            var items = await query.OrderByDescending(a => a.Id).Skip((page - 1) * size).Take(size).Select(Projection).ToListAsync(ct);
            return Results.Ok(new PagedResult<AuditEntryDto>(items, total, page, size));
        });

        group.MapGet("/export.csv", async ([AsParameters] AuditQuery q, string? lang, AppDbContext db, AuditService audit,
            CurrentUserAccessor accessor, CancellationToken ct) =>
        {
            var user = await accessor.RequireAsync(ct);
            if (!user.Has(Permission.ReportsExport))
            {
                return ApiProblem.Forbidden();
            }

            var items = await Filter(db, q).OrderByDescending(a => a.Id).Take(MaxExportRows).Select(Projection).ToListAsync(ct);
            var format = await FormatAsync(db, q.BranchId, lang, ct);
            audit.Record(AuditActions.AuditExported, user.Id, user.Username, q.BranchId, newValue: $"{items.Count} rows");
            await db.SaveChangesAsync(ct);
            return Results.File(CsvExporter.Audit(items, format), "text/csv", "audit-log.csv");
        });
    }

    public static void MapReportEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/branches/{branchId:guid}").WithTags("Reports");

        group.MapGet("/reports/summary", async (Guid branchId, DateOnly from, DateOnly to, ReportService reports, CancellationToken ct) =>
                (await reports.BuildAsync(branchId, from, to, ct)).ToResult())
            .RequirePermission(Permission.ReportsView).RequireBranchAccess();

        group.MapGet("/reports/summary.{ext:regex(^(csv|pdf)$)}", async (Guid branchId, string ext, DateOnly from, DateOnly to, string? lang,
                ReportService reports, AppDbContext db, AuditService audit, CurrentUserAccessor accessor, CancellationToken ct) =>
            {
                var result = await reports.BuildAsync(branchId, from, to, ct);
                if (!result.IsSuccess)
                {
                    return result.Error!.ToResult();
                }

                var user = await accessor.RequireAsync(ct);
                var format = await FormatAsync(db, branchId, lang, ct);
                var name = string.Create(CultureInfo.InvariantCulture, $"income-{from:yyyyMMdd}-{to:yyyyMMdd}.{ext}");
                audit.Record(AuditActions.ReportExported, user.Id, user.Username, branchId, newValue: name);
                await db.SaveChangesAsync(ct);
                return ext == "pdf"
                    ? Results.File(PdfExporter.Report(result.Value!, format), "application/pdf", name)
                    : Results.File(CsvExporter.Report(result.Value!, format), "text/csv", name);
            })
            .RequirePermission(Permission.ReportsExport).RequireBranchAccess();

        group.MapGet("/shifts/export.csv", async (Guid branchId, DateTime fromUtc, DateTime toUtc, string? lang, ShiftService shifts,
                AppDbContext db, AuditService audit, CurrentUserAccessor accessor, CancellationToken ct) =>
            {
                var user = await accessor.RequireAsync(ct);
                var list = await shifts.ListAsync(branchId, fromUtc.ToUniversalTime(), toUtc.ToUniversalTime(), null, user, ct);
                audit.Record(AuditActions.ReportExported, user.Id, user.Username, branchId, newValue: "shifts.csv");
                await db.SaveChangesAsync(ct);
                return Results.File(CsvExporter.Shifts(list, await FormatAsync(db, branchId, lang, ct)), "text/csv", "shifts.csv");
            })
            .RequirePermission(Permission.ReportsExport).RequireBranchAccess();

        // An employee may print the report of their own shift; everything else needs ShiftsViewAll.
        group.MapGet("/shifts/{shiftId:guid}/report.pdf", async (Guid branchId, Guid shiftId, string? lang, ShiftService shifts,
                AppDbContext db, CurrentUserAccessor accessor, CancellationToken ct) =>
            {
                var user = await accessor.RequireAsync(ct);
                var shift = await shifts.GetAsync(shiftId, ct);
                if (shift is null || shift.BranchId != branchId || (shift.UserId != user.Id && !user.Has(Permission.ShiftsViewAll)))
                {
                    return ApiProblem.NotFound();
                }

                var branchName = await db.Branches.Where(b => b.Id == branchId).Select(b => b.Name).FirstAsync(ct);
                return Results.File(PdfExporter.Shift(shift, branchName, await FormatAsync(db, branchId, lang, ct)), "application/pdf", "shift-report.pdf");
            })
            .RequirePermission(Permission.ShiftsOwn).RequireBranchAccess();
    }

    private static readonly System.Linq.Expressions.Expression<Func<AuditLogEntry, AuditEntryDto>> Projection = a =>
        new AuditEntryDto(a.Id, a.AtUtc, a.Action, a.BranchId, a.UserId, a.Username, a.MachineId, a.MachineName, a.SessionId,
            a.OldValue, a.NewValue, a.Reason, a.IpAddress);

    private static IQueryable<AuditLogEntry> Filter(AppDbContext db, AuditQuery q)
    {
        var query = db.AuditLog.AsNoTracking();
        if (q.BranchId is { } branchId)
        {
            query = query.Where(a => a.BranchId == branchId);
        }

        if (q.FromUtc is { } from)
        {
            var f = from.ToUniversalTime();
            query = query.Where(a => a.AtUtc >= f);
        }

        if (q.ToUtc is { } to)
        {
            var t = to.ToUniversalTime();
            query = query.Where(a => a.AtUtc < t);
        }

        if (!string.IsNullOrWhiteSpace(q.Action))
        {
            // "session." matches every session action.
            query = q.Action.EndsWith('.') ? query.Where(a => a.Action.StartsWith(q.Action)) : query.Where(a => a.Action == q.Action);
        }

        if (q.UserId is { } userId)
        {
            query = query.Where(a => a.UserId == userId);
        }

        if (q.MachineId is { } machineId)
        {
            query = query.Where(a => a.MachineId == machineId);
        }

        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var s = q.Search.Trim();
            query = query.Where(a => a.Username!.Contains(s) || a.MachineName!.Contains(s) || a.Reason!.Contains(s)
                || a.NewValue!.Contains(s) || a.OldValue!.Contains(s) || a.Action.Contains(s));
        }

        return query;
    }

    private static async Task<ExportFormat> FormatAsync(AppDbContext db, Guid? branchId, string? lang, CancellationToken ct)
    {
        var shop = await db.ShopSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        var branch = branchId is null ? null : await db.Branches.AsNoTracking().FirstOrDefaultAsync(b => b.Id == branchId, ct);
        branch ??= await db.Branches.AsNoTracking().OrderBy(b => b.CreatedAtUtc).FirstOrDefaultAsync(ct);
        return new ExportFormat(new ExportText(lang ?? shop?.DefaultLanguage), branch?.CurrencySymbol ?? "$", branch?.CurrencyDecimals ?? 0,
            ReportService.Zone(branch?.TimeZoneId ?? "UTC"), shop?.ShopName ?? string.Empty);
    }
}

/// <summary>Audit log filters, bound from the query string.</summary>
public sealed record AuditQuery(Guid? BranchId, DateTime? FromUtc, DateTime? ToUtc, string? Action, Guid? UserId, Guid? MachineId,
    string? Search, int? Page, int? PageSize);
