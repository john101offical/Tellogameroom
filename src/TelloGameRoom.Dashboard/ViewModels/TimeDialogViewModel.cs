using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TelloGameRoom.Core.Billing;
using TelloGameRoom.Core.Client;
using TelloGameRoom.Core.Dtos;
using TelloGameRoom.Core.Sessions;
using TelloGameRoom.Dashboard.Services;

namespace TelloGameRoom.Dashboard.ViewModels;

public enum TimeDialogMode
{
    Start,
    AddTime,
    RemoveTime,
}

/// <summary>What the staff member confirmed in the time dialog.</summary>
public sealed record TimeDialogResult(long Seconds, long AmountCents, bool IsFree, string? Reason);

/// <summary>
/// Drives the start-session / add-time / remove-time dialog. The amount is computed locally with the same
/// <see cref="PriceCalculator"/> the server uses, so it appears instantly; the server recomputes and rejects a mismatch.
/// </summary>
public sealed partial class TimeDialogViewModel : ObservableObject
{
    private readonly BranchPricingDto _pricing;
    private readonly long _hourlyRateCents;
    private readonly long _maxRemovableSeconds;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AmountText), nameof(DurationText), nameof(CanConfirm), nameof(IsPreset30), nameof(IsPreset60), nameof(IsPreset120), nameof(IsPreset180))]
    private long _seconds;

    [ObservableProperty]
    private string _customMinutes = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AmountText), nameof(CanConfirm), nameof(NeedsReason))]
    private bool _isFree;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirm))]
    private string _reason = string.Empty;

    public TimeDialogViewModel(TimeDialogMode mode, string machineName, BranchPricingDto pricing, long hourlyRateCents, bool canGiveFree,
        bool sessionIsFree = false, long maxRemovableSeconds = 0)
    {
        Mode = mode;
        MachineName = machineName;
        _pricing = pricing;
        _hourlyRateCents = hourlyRateCents;
        _maxRemovableSeconds = maxRemovableSeconds;
        CanGiveFree = canGiveFree && mode == TimeDialogMode.Start;
        _isFree = sessionIsFree;
        SessionIsFree = sessionIsFree;

        // One hour is by far the most common sale: preselect it so "click machine, Enter" starts a session.
        _seconds = mode == TimeDialogMode.RemoveTime ? 0 : 3600;
    }

    public TimeDialogMode Mode { get; }

    public string MachineName { get; }

    public bool CanGiveFree { get; }

    public bool SessionIsFree { get; }

    public TimeDialogResult? Result { get; private set; }

    public event Action? Confirmed;

    public string Title => Mode switch
    {
        TimeDialogMode.Start => Loc.T("Time_StartTitle", MachineName),
        TimeDialogMode.AddTime => Loc.T("Time_AddTitle", MachineName),
        _ => Loc.T("Time_RemoveTitle", MachineName),
    };

    public string ConfirmText => Mode switch
    {
        TimeDialogMode.Start => Loc.T("Time_StartConfirm"),
        TimeDialogMode.AddTime => Loc.T("Time_AddConfirm"),
        _ => Loc.T("Time_RemoveConfirm"),
    };

    public string AmountLabel => Mode == TimeDialogMode.RemoveTime ? Loc.T("Time_Refund") : Loc.T("Time_Collect");

    public string RateText => Loc.T("Time_Rate", Format.Money(_hourlyRateCents, _pricing));

    public bool IsRemove => Mode == TimeDialogMode.RemoveTime;

    public bool NeedsReason => IsRemove || (IsFree && Mode == TimeDialogMode.Start);

    public long AmountCents => IsFree || Seconds <= 0 ? 0 : PriceCalculator.AmountDue(_hourlyRateCents, Seconds, _pricing.RoundingMode, _pricing.RoundingIncrementCents);

    public string AmountText => IsFree ? Loc.T("Time_Free") : Format.Money(AmountCents, _pricing);

    public string DurationText => Seconds > 0 ? TimeText.Short(Seconds) : "-";

    public bool IsPreset30 => Seconds == 1800;

    public bool IsPreset60 => Seconds == 3600;

    public bool IsPreset120 => Seconds == 7200;

    public bool IsPreset180 => Seconds == 10800;

    public bool CanConfirm =>
        Seconds >= (IsRemove ? 60 : SessionClock.MinDurationSeconds)
        && Seconds <= SessionClock.MaxDurationSeconds
        && (!IsRemove || Seconds < _maxRemovableSeconds)
        && (!NeedsReason || !string.IsNullOrWhiteSpace(Reason));

    [RelayCommand]
    private void Preset(string minutes)
    {
        CustomMinutes = string.Empty;
        Seconds = long.Parse(minutes, CultureInfo.InvariantCulture) * 60;
    }

    [RelayCommand]
    private void Confirm()
    {
        if (!CanConfirm)
        {
            return;
        }

        Result = new TimeDialogResult(Seconds, AmountCents, IsFree, string.IsNullOrWhiteSpace(Reason) ? null : Reason.Trim());
        Confirmed?.Invoke();
    }

    partial void OnCustomMinutesChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        Seconds = int.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) && minutes is > 0 and <= 1440
            ? minutes * 60L
            : 0;
    }
}
