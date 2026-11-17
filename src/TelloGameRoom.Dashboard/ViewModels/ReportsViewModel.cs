using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TelloGameRoom.Core.Dtos;
using TelloGameRoom.Core.Security;
using TelloGameRoom.Dashboard.Controls;
using TelloGameRoom.Dashboard.Services;

namespace TelloGameRoom.Dashboard.ViewModels;

/// <summary>A row of a "by employee" or "by machine" table.</summary>
public sealed record TotalRow(string Name, string Income, int Sessions, string Hours, double Share);

/// <summary>
/// Income reports for a date range, with charts. The same view model backs the owner's Overview page (fixed to the
/// last seven days) and the Reports page (any range, with exports).
/// </summary>
public sealed partial class ReportsViewModel(Session session, BranchContext context, bool isOverview) : PageViewModel
{
    [ObservableProperty]
    private DateTime _from = DateTime.Today.AddDays(-6);

    [ObservableProperty]
    private DateTime _to = DateTime.Today;

    [ObservableProperty]
    private string _incomeText = "-";

    [ObservableProperty]
    private string _sessionsText = "-";

    [ObservableProperty]
    private string _hoursText = "-";

    [ObservableProperty]
    private string _refundsText = "-";

    [ObservableProperty]
    private string _todayIncomeText = "-";

    [ObservableProperty]
    private string _periodText = string.Empty;

    [ObservableProperty]
    private IReadOnlyList<BarPoint> _incomeByDay = [];

    [ObservableProperty]
    private IReadOnlyList<BarPoint> _sessionsByHour = [];

    public bool IsOverview { get; } = isOverview;

    public BranchContext Context { get; } = context;

    public ObservableCollection<TotalRow> ByEmployee { get; } = [];

    public ObservableCollection<TotalRow> ByMachine { get; } = [];

    public bool CanExport => session.Can(Permission.ReportsExport);

    public override Task ActivateAsync()
    {
        if (IsOverview)
        {
            From = DateTime.Today.AddDays(-6);
            To = DateTime.Today;
        }

        return LoadAsync();
    }

    [RelayCommand]
    private Task TodayAsync() => SetRangeAsync(DateTime.Today, DateTime.Today);

    [RelayCommand]
    private Task Last7Async() => SetRangeAsync(DateTime.Today.AddDays(-6), DateTime.Today);

    [RelayCommand]
    private Task ThisMonthAsync() => SetRangeAsync(new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1), DateTime.Today);

    [RelayCommand]
    private Task LastMonthAsync()
    {
        var first = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).AddMonths(-1);
        return SetRangeAsync(first, first.AddMonths(1).AddDays(-1));
    }

    [RelayCommand]
    private Task LoadAsync() => RunAsync(async () =>
    {
        var report = await session.Api.GetAsync<ReportDto>($"{Context.Path}/reports/summary?{Range()}");
        IncomeText = Context.Money(report.IncomeCents);
        SessionsText = report.FreeSessions > 0
            ? Loc.T("Reports_SessionsWithFree", report.Sessions, report.FreeSessions)
            : report.Sessions.ToString(Loc.Instance.Culture);
        HoursText = Format.Hours(report.SecondsSold);
        RefundsText = Context.Money(report.RefundsCents);
        PeriodText = $"{report.From.ToString("d", Loc.Instance.Culture)} – {report.To.ToString("d", Loc.Instance.Culture)}";
        TodayIncomeText = Context.Money(report.ByDay.LastOrDefault()?.IncomeCents ?? 0);

        var dayFormat = report.ByDay.Count <= 8 ? "ddd d" : "d/M";
        IncomeByDay = [.. report.ByDay.Select(d => new BarPoint(d.Date.ToString(dayFormat, Loc.Instance.Culture), d.IncomeCents / 100d, Context.Money(d.IncomeCents)))];
        SessionsByHour = [.. report.ByHour.Select(h => new BarPoint(h.Hour.ToString("00", CultureInfo.InvariantCulture), h.Sessions,
            Loc.T("Reports_SessionsCount", h.Sessions)))];

        Fill(ByEmployee, report.ByEmployee);
        Fill(ByMachine, report.ByMachine);
    });

    [RelayCommand]
    private Task ExportCsvAsync() => ExportAsync("csv", Files.CsvFilter);

    [RelayCommand]
    private Task ExportPdfAsync() => ExportAsync("pdf", Files.PdfFilter);

    private Task SetRangeAsync(DateTime from, DateTime to)
    {
        From = from;
        To = to;
        return LoadAsync();
    }

    private Task ExportAsync(string extension, string filter) => RunAsync(async () =>
    {
        var bytes = await session.Api.GetBytesAsync($"{Context.Path}/reports/summary.{extension}?{Range()}&lang={Loc.Instance.Language}");
        Files.Save(bytes, $"income-{From:yyyyMMdd}-{To:yyyyMMdd}.{extension}", filter);
    });

    private string Range() => string.Create(CultureInfo.InvariantCulture, $"from={From:yyyy-MM-dd}&to={To:yyyy-MM-dd}");

    private void Fill(ObservableCollection<TotalRow> target, IReadOnlyList<NamedTotal> totals)
    {
        target.Clear();
        var max = Math.Max(1, totals.Count == 0 ? 1 : totals.Max(t => t.IncomeCents));
        foreach (var t in totals)
        {
            target.Add(new TotalRow(t.Name, Context.Money(t.IncomeCents), t.Sessions, Format.Hours(t.SecondsSold), Math.Clamp(t.IncomeCents / (double)max, 0, 1)));
        }
    }
}
