using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TelloGameRoom.Core.Dtos;
using TelloGameRoom.Core.Security;
using TelloGameRoom.Dashboard.Services;
using TelloGameRoom.Dashboard.Views;

namespace TelloGameRoom.Dashboard.ViewModels;

/// <summary>A shift as a table row, with amounts already formatted in the branch currency.</summary>
public sealed record ShiftRow(ShiftDto Shift, string Employee, string Opened, string Closed, string Opening, string Sales, string Refunds,
    string Expected, string Counted, string Difference, bool IsOpen, bool IsShort, bool IsOver);

/// <summary>Clock in and out, count the cash, and review past shifts.</summary>
public sealed partial class ShiftsViewModel(Session session, BranchContext context) : PageViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOpenShift), nameof(CurrentRow))]
    private ShiftDto? _current;

    [ObservableProperty]
    private ShiftRow? _selected;

    public ObservableCollection<ShiftRow> Shifts { get; } = [];

    public bool HasOpenShift => Current is not null;

    public ShiftRow? CurrentRow => Current is null ? null : ToRow(Current);

    public bool CanViewAll => session.Can(Permission.ShiftsViewAll);

    public bool CanExport => session.Can(Permission.ReportsExport);

    public override Task ActivateAsync() => RefreshAsync();

    [RelayCommand]
    private Task RefreshAsync() => RunAsync(async () =>
    {
        Current = await session.Api.GetOrDefaultAsync<ShiftDto>($"{context.Path}/shifts/current");
        var list = await session.Api.GetAsync<List<ShiftDto>>($"{context.Path}/shifts");
        Shifts.Clear();
        foreach (var shift in list)
        {
            Shifts.Add(ToRow(shift));
        }
    });

    [RelayCommand]
    private async Task OpenAsync()
    {
        var answer = PromptWindow.Ask(new PromptOptions
        {
            Title = Loc.T("Shifts_OpenTitle"),
            Message = Loc.T("Shifts_OpenMessage"),
            ConfirmText = Loc.T("Shifts_Open"),
            InputLabel = Loc.T("Shifts_OpeningCash"),
            Input = "0",
            InputRequired = true,
            Validate = o => Format.ParseMoney(o.Input) is null ? Loc.T("Common_InvalidAmount") : null,
        });
        if (answer is not null)
        {
            await RunAsync(() => session.Api.PostAsync<ShiftDto>($"{context.Path}/shifts/open", new OpenShiftRequest(Format.ParseMoney(answer.Input)!.Value)));
            await RefreshAsync();
        }
    }

    [RelayCommand]
    private Task CloseAsync() => Current is null ? Task.CompletedTask : CloseShiftAsync(Current, own: true);

    /// <summary>Owner closes a shift an employee forgot to close.</summary>
    [RelayCommand]
    private Task CloseSelectedAsync() =>
        Selected is { IsOpen: true } row ? CloseShiftAsync(row.Shift, own: row.Shift.UserId == session.User?.Id) : Task.CompletedTask;

    [RelayCommand]
    private async Task PrintSelectedAsync()
    {
        if ((Selected?.Shift ?? Current) is not { } shift)
        {
            return;
        }

        await RunAsync(async () =>
        {
            var pdf = await session.Api.GetBytesAsync($"{context.Path}/shifts/{shift.Id}/report.pdf?lang={Loc.Instance.Language}");
            Files.Save(pdf, $"shift-{shift.UserDisplayName}-{shift.OpenedAtUtc.ToLocalTime():yyyyMMdd}.pdf", Files.PdfFilter);
        });
    }

    [RelayCommand]
    private Task ExportAsync() => RunAsync(async () =>
    {
        var to = DateTime.UtcNow.AddDays(1);
        var from = to.AddDays(-32);
        var csv = await session.Api.GetBytesAsync(string.Create(CultureInfo.InvariantCulture,
            $"{context.Path}/shifts/export.csv?fromUtc={from:yyyy-MM-ddTHH:mm:ssZ}&toUtc={to:yyyy-MM-ddTHH:mm:ssZ}&lang={Loc.Instance.Language}"));
        Files.Save(csv, "shifts.csv", Files.CsvFilter);
    });

    private async Task CloseShiftAsync(ShiftDto shift, bool own)
    {
        var answer = PromptWindow.Ask(new PromptOptions
        {
            Title = Loc.T("Shifts_CloseTitle", shift.UserDisplayName),
            Message = Loc.T("Shifts_CloseMessage"),
            ConfirmText = Loc.T("Shifts_Close"),
            InputLabel = Loc.T("Shifts_CountedCash"),
            InputRequired = true,
            SecondInputLabel = Loc.T("Shifts_Notes"),
            Validate = o => Format.ParseMoney(o.Input) is null ? Loc.T("Common_InvalidAmount") : null,
        });
        if (answer is null)
        {
            return;
        }

        ShiftDto? closed = null;
        var path = own ? $"{context.Path}/shifts/close" : $"{context.Path}/shifts/{shift.Id}/close";
        if (await RunAsync(async () => closed = await session.Api.PostAsync<ShiftDto>(path,
                new CloseShiftRequest(Format.ParseMoney(answer.Input)!.Value, answer.SecondInput))) && closed is not null)
        {
            // Tell the employee the result of the count right away.
            var difference = closed.DifferenceCents ?? 0;
            var verdict = difference == 0 ? Loc.T("Shifts_Balanced")
                : difference < 0 ? Loc.T("Shifts_Short", context.Money(-difference))
                : Loc.T("Shifts_Over", context.Money(difference));
            PromptWindow.Info(Loc.T("Shifts_ClosedTitle"),
                Loc.T("Shifts_ClosedSummary", context.Money(closed.ExpectedCashCents), context.Money(closed.CountedCashCents ?? 0), verdict));
            await RefreshAsync();
        }
    }

    private ShiftRow ToRow(ShiftDto s) => new(
        s, s.UserDisplayName, Format.LocalTime(s.OpenedAtUtc), s.ClosedAtUtc is { } closed ? Format.LocalTime(closed) : Loc.T("Shifts_StillOpen"),
        context.Money(s.OpeningCashCents), context.Money(s.SalesCents), context.Money(s.RefundsCents), context.Money(s.ExpectedCashCents),
        s.CountedCashCents is { } counted ? context.Money(counted) : "-",
        s.DifferenceCents is { } diff ? (diff > 0 ? "+" : string.Empty) + context.Money(diff) : "-",
        s.ClosedAtUtc is null, s.DifferenceCents < 0, s.DifferenceCents > 0);
}
