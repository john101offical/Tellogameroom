using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using TelloGameRoom.Core.Client;
using TelloGameRoom.Core.Dtos;
using TelloGameRoom.Core.Enums;
using TelloGameRoom.Dashboard.Services;

namespace TelloGameRoom.Dashboard.ViewModels;

/// <summary>One machine card. It counts down locally between server updates; every update from the server replaces its numbers.</summary>
public sealed partial class MachineCardViewModel : ObservableObject
{
    private static readonly Stopwatch Monotonic = Stopwatch.StartNew();
    private TimeSpan _deadline;
    private long _pausedRemaining;
    private long _purchasedSeconds;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSession), nameof(IsRunning), nameof(IsPaused), nameof(IsFreeMachine), nameof(StatusText))]
    private MachineStatus _status;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private bool _isOnline;

    [ObservableProperty]
    private bool _isEnabled = true;

    [ObservableProperty]
    private string? _groupName;

    [ObservableProperty]
    private string _remainingText = string.Empty;

    [ObservableProperty]
    private double _fraction;

    [ObservableProperty]
    private bool _isLow;

    [ObservableProperty]
    private string _amountText = string.Empty;

    [ObservableProperty]
    private string _startedBy = string.Empty;

    [ObservableProperty]
    private string _rateText = string.Empty;

    public Guid Id { get; init; }

    public Guid? GroupId { get; private set; }

    public long HourlyRateCents { get; private set; }

    public SessionDto? Session { get; private set; }

    public bool HasSession => Status is MachineStatus.InUse or MachineStatus.Paused;

    public bool IsRunning => Status == MachineStatus.InUse;

    public bool IsPaused => Status == MachineStatus.Paused;

    /// <summary>No session on it (it may be online or offline).</summary>
    public bool IsFreeMachine => !HasSession;

    public long RemainingSeconds => !HasSession ? 0
        : IsPaused ? _pausedRemaining
        : Math.Max(0, (long)Math.Ceiling((_deadline - Monotonic.Elapsed).TotalSeconds));

    public string StatusText => Status switch
    {
        MachineStatus.InUse => IsOnline ? Loc.T("Status_InUse") : Loc.T("Status_InUseOffline"),
        MachineStatus.Paused => Loc.T("Status_Paused"),
        MachineStatus.Free => Loc.T("Status_Free"),
        _ => Loc.T("Status_Offline"),
    };

    public void Update(MachineStateDto dto, BranchPricingDto? pricing, int warningThresholdSeconds)
    {
        Name = dto.Name;
        IsOnline = dto.IsOnline;
        IsEnabled = dto.IsEnabled;
        GroupName = dto.GroupName;
        GroupId = dto.GroupId;
        HourlyRateCents = dto.HourlyRateCents;
        Session = dto.Session;
        RateText = Format.Money(dto.HourlyRateCents, pricing) + "/h";
        if (dto.Session is { } s)
        {
            _purchasedSeconds = Math.Max(1, s.PurchasedSeconds);
            _pausedRemaining = s.RemainingSeconds;
            _deadline = Monotonic.Elapsed + TimeSpan.FromSeconds(s.RemainingSeconds);
            AmountText = s.IsFree ? Loc.T("Time_Free") : Format.Money(s.AmountCents, pricing);
            StartedBy = s.StartedBy;
        }
        else
        {
            AmountText = string.Empty;
            StartedBy = string.Empty;
        }

        Status = dto.Status;
        OnPropertyChanged(nameof(StatusText));
        Tick(warningThresholdSeconds);
    }

    public void Tick(int warningThresholdSeconds)
    {
        if (!HasSession)
        {
            RemainingText = string.Empty;
            Fraction = 0;
            IsLow = false;
            return;
        }

        var remaining = RemainingSeconds;
        RemainingText = TimeText.Clock(remaining);
        Fraction = Math.Clamp(remaining / (double)_purchasedSeconds, 0, 1);
        IsLow = IsRunning && remaining <= warningThresholdSeconds;
    }
}

/// <summary>
/// The branch the Dashboard is looking at: its pricing, machines and on-duty staff, kept current by the realtime
/// connection. Shared by all pages. Everything here is touched on the UI thread only.
/// </summary>
public sealed partial class BranchContext : ObservableObject
{
    [ObservableProperty]
    private Guid _branchId;

    [ObservableProperty]
    private string _branchName = string.Empty;

    [ObservableProperty]
    private string _shopName = "TelloGameRoom";

    [ObservableProperty]
    private BranchPricingDto? _pricing;

    [ObservableProperty]
    private string _onDutyText = string.Empty;

    [ObservableProperty]
    private string _summaryText = string.Empty;

    public ObservableCollection<MachineCardViewModel> Machines { get; } = [];

    public IReadOnlyList<OnDutyDto> OnDuty { get; private set; } = [];

    /// <summary>Raised after pricing or the on-duty list changed.</summary>
    public event Action? Changed;

    /// <summary>API path prefix of the current branch.</summary>
    public string Path => $"/branches/{BranchId}";

    public int WarningThreshold => Pricing?.WarningThresholdSeconds ?? 300;

    public string Money(long cents) => Format.Money(cents, Pricing);

    public void Apply(BranchStateDto state)
    {
        BranchId = state.BranchId;
        BranchName = state.BranchName;
        ShopName = state.ShopName;
        Pricing = state.Pricing;
        OnDuty = state.OnDuty;
        OnDutyText = state.OnDuty.Count == 0 ? Loc.T("Shell_NobodyOnDuty") : Loc.T("Shell_OnDuty", string.Join(", ", state.OnDuty.Select(o => o.DisplayName)));

        // Reuse existing cards so the grid does not flicker or lose keyboard focus on a resync.
        var incoming = state.Machines.ToDictionary(m => m.Id);
        for (var i = Machines.Count - 1; i >= 0; i--)
        {
            if (!incoming.ContainsKey(Machines[i].Id))
            {
                Machines.RemoveAt(i);
            }
        }

        foreach (var dto in state.Machines)
        {
            Upsert(dto, sort: false);
        }

        Sort();
        Changed?.Invoke();
    }

    public void Upsert(MachineStateDto dto, bool sort = true)
    {
        if (dto.BranchId != BranchId)
        {
            return;
        }

        var card = Machines.FirstOrDefault(m => m.Id == dto.Id);
        if (card is null)
        {
            card = new MachineCardViewModel { Id = dto.Id };
            Machines.Add(card);
        }

        card.Update(dto, Pricing, WarningThreshold);
        if (sort)
        {
            Sort();
        }

        RefreshSummary();
    }

    public void Remove(Guid machineId)
    {
        if (Machines.FirstOrDefault(m => m.Id == machineId) is { } card)
        {
            Machines.Remove(card);
            RefreshSummary();
        }
    }

    public void Clear()
    {
        Machines.Clear();
        OnDuty = [];
        OnDutyText = string.Empty;
        SummaryText = string.Empty;
    }

    public void Tick()
    {
        foreach (var card in Machines)
        {
            card.Tick(WarningThreshold);
        }
    }

    private void Sort()
    {
        var ordered = Machines.OrderBy(m => m.Name, NaturalComparer.Instance).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var current = Machines.IndexOf(ordered[i]);
            if (current != i)
            {
                Machines.Move(current, i);
            }
        }

        RefreshSummary();
    }

    private void RefreshSummary() => SummaryText = Loc.T("Machines_Summary",
        Machines.Count(m => m.HasSession), Machines.Count(m => m.Status == MachineStatus.Free), Machines.Count(m => m.Status == MachineStatus.Offline));

    /// <summary>Sorts "PC-2" before "PC-10".</summary>
    private sealed class NaturalComparer : IComparer<string>
    {
        public static readonly NaturalComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            static string Pad(string? s) => System.Text.RegularExpressions.Regex.Replace(s ?? string.Empty, @"\d+", m => m.Value.PadLeft(10, '0'));
            return string.Compare(Pad(x), Pad(y), StringComparison.OrdinalIgnoreCase);
        }
    }
}
