using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TelloGameRoom.Core.Dtos;
using TelloGameRoom.Core.Security;
using TelloGameRoom.Dashboard.Services;

namespace TelloGameRoom.Dashboard.ViewModels;

public sealed record AuditRow(string Time, string Action, string User, string Machine, string Details, string Reason, string Ip);

/// <summary>A filter option of the audit log ("Sessions", "Logins", ...). The key is an action prefix understood by the server.</summary>
public sealed record AuditCategory(string Key, string Label);

/// <summary>Owner-only: search, filter and export the append-only audit log.</summary>
public sealed partial class AuditViewModel(Session session, BranchContext context) : PageViewModel
{
    private const int PageSize = 50;

    [ObservableProperty]
    private string _search = string.Empty;

    [ObservableProperty]
    private AuditCategory? _category;

    [ObservableProperty]
    private DateTime? _from;

    [ObservableProperty]
    private DateTime? _to;

    [ObservableProperty]
    private bool _thisBranchOnly;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageText))]
    private int _page = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageText))]
    private int _total;

    public ObservableCollection<AuditRow> Entries { get; } = [];

    public IReadOnlyList<AuditCategory> Categories { get; } =
    [
        new(string.Empty, Loc.T("Audit_All")),
        new("session.", Loc.T("Audit_Sessions")),
        new("auth.", Loc.T("Audit_Logins")),
        new("remote.", Loc.T("Audit_Remote")),
        new("shift.", Loc.T("Audit_Shifts")),
        new("employee.", Loc.T("Audit_Employees")),
        new("settings.", Loc.T("Audit_Settings")),
        new("machine.", Loc.T("Audit_Machines")),
        new("backup.", Loc.T("Audit_Backups")),
    ];

    public string PageText => Loc.T("Audit_Page", Page, Math.Max(1, (int)Math.Ceiling(Total / (double)PageSize)), Total);

    public bool CanExport => session.Can(Permission.ReportsExport);

    public override Task ActivateAsync()
    {
        Category ??= Categories[0];
        return LoadAsync();
    }

    [RelayCommand]
    private Task SearchNowAsync()
    {
        Page = 1;
        return LoadAsync();
    }

    [RelayCommand]
    private Task NextAsync()
    {
        if (Page * PageSize >= Total)
        {
            return Task.CompletedTask;
        }

        Page++;
        return LoadAsync();
    }

    [RelayCommand]
    private Task PreviousAsync()
    {
        if (Page <= 1)
        {
            return Task.CompletedTask;
        }

        Page--;
        return LoadAsync();
    }

    [RelayCommand]
    private Task ExportAsync() => RunAsync(async () =>
    {
        var csv = await session.Api.GetBytesAsync($"/audit/export.csv?{Query()}&lang={Loc.Instance.Language}");
        Files.Save(csv, $"audit-log-{DateTime.Now:yyyyMMdd}.csv", Files.CsvFilter);
    });

    private Task LoadAsync() => RunAsync(async () =>
    {
        var result = await session.Api.GetAsync<PagedResult<AuditEntryDto>>(
            string.Create(CultureInfo.InvariantCulture, $"/audit?{Query()}&page={Page}&pageSize={PageSize}"));
        Total = result.Total;
        Entries.Clear();
        foreach (var e in result.Items)
        {
            var details = (e.OldValue, e.NewValue) switch
            {
                (not null, not null) => $"{e.OldValue} → {e.NewValue}",
                (null, not null) => e.NewValue,
                (not null, null) => e.OldValue,
                _ => string.Empty,
            };
            var key = "Action_" + e.Action.Replace('.', '_');
            Entries.Add(new AuditRow(Format.LocalTime(e.AtUtc), Loc.Instance.Has(key) ? Loc.T(key) : e.Action, e.Username ?? Loc.T("Audit_System"),
                e.MachineName ?? string.Empty, details, e.Reason ?? string.Empty, e.IpAddress ?? string.Empty));
        }
    });

    private string Query()
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(Search))
        {
            parts.Add("search=" + Uri.EscapeDataString(Search.Trim()));
        }

        if (!string.IsNullOrEmpty(Category?.Key))
        {
            parts.Add("action=" + Uri.EscapeDataString(Category.Key));
        }

        if (From is { } from)
        {
            parts.Add("fromUtc=" + Uri.EscapeDataString(from.Date.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture)));
        }

        if (To is { } to)
        {
            parts.Add("toUtc=" + Uri.EscapeDataString(to.Date.AddDays(1).ToUniversalTime().ToString("o", CultureInfo.InvariantCulture)));
        }

        if (ThisBranchOnly)
        {
            parts.Add("branchId=" + context.BranchId);
        }

        return string.Join("&", parts);
    }
}
