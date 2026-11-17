using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TelloGameRoom.Core.Client;
using TelloGameRoom.Core.Dtos;
using TelloGameRoom.Core.Enums;
using TelloGameRoom.Core.Protocol;
using TelloGameRoom.Core.Security;
using TelloGameRoom.Dashboard.Services;
using TelloGameRoom.Dashboard.Views;

namespace TelloGameRoom.Dashboard.ViewModels;

/// <summary>The machine grid: every session and remote-control action staff can take.</summary>
public sealed partial class MachinesViewModel(Session session, BranchContext context) : PageViewModel
{
    public BranchContext Context { get; } = context;

    // What the signed-in user may do. The server enforces all of it; this only hides what would be refused anyway.
    public bool CanStart => session.Can(Permission.SessionsStart);

    public bool CanAddTime => session.Can(Permission.SessionsAddTime);

    public bool CanRemoveTime => session.Can(Permission.SessionsRemoveTime);

    public bool CanPause => session.Can(Permission.SessionsPauseResume);

    public bool CanStop => session.Can(Permission.SessionsStop);

    public bool CanCancel => session.Can(Permission.SessionsCancelPaid);

    public bool CanTransfer => session.Can(Permission.SessionsTransfer);

    public bool CanMessage => session.Can(Permission.RemoteMessage);

    public bool CanLock => session.Can(Permission.RemoteLock);

    public bool CanPower => session.Can(Permission.RemotePower);

    public bool CanScreenshot => session.Can(Permission.RemoteScreenshot);

    public bool CanManage => session.Can(Permission.MachinesManage);

    private string M(MachineCardViewModel card) => $"{Context.Path}/machines/{card.Id}";

    public override Task ActivateAsync()
    {
        foreach (var name in new[] { nameof(CanStart), nameof(CanAddTime), nameof(CanRemoveTime), nameof(CanPause), nameof(CanStop), nameof(CanCancel),
                     nameof(CanTransfer), nameof(CanMessage), nameof(CanLock), nameof(CanPower), nameof(CanScreenshot), nameof(CanManage) })
        {
            OnPropertyChanged(name);
        }

        return Task.CompletedTask;
    }

    /// <summary>Click on a card: start a session on a free machine, add time on a busy one.</summary>
    [RelayCommand]
    private Task PrimaryAsync(MachineCardViewModel? card) =>
        card is null ? Task.CompletedTask : card.HasSession ? AddTimeAsync(card) : StartAsync(card);

    [RelayCommand]
    private async Task StartAsync(MachineCardViewModel? card)
    {
        if (card is null || Context.Pricing is null || !CanStart || card.HasSession)
        {
            return;
        }

        if (!card.IsEnabled)
        {
            Error = Loc.T("Err_machine_disabled");
            return;
        }

        var allowOffline = false;
        if (!card.IsOnline)
        {
            if (!PromptWindow.Confirm(Loc.T("Machines_OfflineTitle"), Loc.T("Machines_OfflineStart", card.Name), Loc.T("Machines_StartAnyway"), danger: true))
            {
                return;
            }

            allowOffline = true;
        }

        var dialog = new TimeDialogViewModel(TimeDialogMode.Start, card.Name, Context.Pricing, card.HourlyRateCents, session.Can(Permission.SessionsFreeTime));
        if (TimeDialog.Ask(dialog) is not { } result)
        {
            return;
        }

        await SessionCallAsync(card, "/session/start",
            new StartSessionRequest(result.Seconds, result.IsFree ? null : result.AmountCents, result.IsFree, result.Reason, allowOffline));
    }

    [RelayCommand]
    private async Task AddTimeAsync(MachineCardViewModel? card)
    {
        if (card?.Session is not { } current || Context.Pricing is null || !CanAddTime)
        {
            return;
        }

        var dialog = new TimeDialogViewModel(TimeDialogMode.AddTime, card.Name, Context.Pricing, current.HourlyRateCents, false, current.IsFree);
        if (TimeDialog.Ask(dialog) is { } result)
        {
            await SessionCallAsync(card, "/session/add-time", new AdjustTimeRequest(result.Seconds, current.IsFree ? null : result.AmountCents));
        }
    }

    [RelayCommand]
    private async Task RemoveTimeAsync(MachineCardViewModel? card)
    {
        if (card?.Session is not { } current || Context.Pricing is null)
        {
            return;
        }

        var dialog = new TimeDialogViewModel(TimeDialogMode.RemoveTime, card.Name, Context.Pricing, current.HourlyRateCents, false, current.IsFree, card.RemainingSeconds);
        if (TimeDialog.Ask(dialog) is { } result)
        {
            // The refund is capped by the server at what was collected, so no expected amount is sent here.
            await SessionCallAsync(card, "/session/remove-time", new AdjustTimeRequest(result.Seconds, null, result.Reason));
        }
    }

    [RelayCommand]
    private Task PauseAsync(MachineCardViewModel? card) => card is null ? Task.CompletedTask : SessionCallAsync(card, "/session/pause", new { });

    [RelayCommand]
    private Task ResumeAsync(MachineCardViewModel? card) => card is null ? Task.CompletedTask : SessionCallAsync(card, "/session/resume", new { });

    [RelayCommand]
    private Task TogglePauseAsync(MachineCardViewModel? card) => card is null ? Task.CompletedTask : card.IsPaused ? ResumeAsync(card) : PauseAsync(card);

    [RelayCommand]
    private async Task StopAsync(MachineCardViewModel? card)
    {
        if (card is null)
        {
            return;
        }

        var answer = PromptWindow.Ask(new PromptOptions
        {
            Title = Loc.T("Machines_StopTitle", card.Name),
            Message = Loc.T("Machines_StopMessage", card.RemainingText),
            ConfirmText = Loc.T("Machines_Stop"),
            Danger = true,
            InputLabel = Loc.T("Common_ReasonOptional"),
        });
        if (answer is not null)
        {
            await SessionCallAsync(card, "/session/stop", new EndSessionRequest(answer.Input));
        }
    }

    [RelayCommand]
    private async Task CancelAsync(MachineCardViewModel? card)
    {
        if (card?.Session is not { } current)
        {
            return;
        }

        var answer = PromptWindow.Ask(new PromptOptions
        {
            Title = Loc.T("Machines_CancelTitle", card.Name),
            Message = Loc.T("Machines_CancelMessage", Context.Money(current.AmountCents)),
            ConfirmText = Loc.T("Machines_CancelConfirm"),
            Danger = true,
            InputLabel = Loc.T("Common_Reason"),
            InputRequired = true,
            SecondInputLabel = Loc.T("Machines_RefundAmount"),
            Validate = o => string.IsNullOrWhiteSpace(o.SecondInput) || Format.ParseMoney(o.SecondInput) is not null ? null : Loc.T("Common_InvalidAmount"),
        });
        if (answer is not null)
        {
            await SessionCallAsync(card, "/session/cancel", new EndSessionRequest(answer.Input, Format.ParseMoney(answer.SecondInput)));
        }
    }

    [RelayCommand]
    private async Task TransferAsync(MachineCardViewModel? card)
    {
        if (card is null)
        {
            return;
        }

        var targets = Context.Machines.Where(m => m.Id != card.Id && !m.HasSession && m.IsEnabled)
            .Select(m => new PromptChoice(m.IsOnline ? m.Name : $"{m.Name} ({Loc.T("Status_Offline")})", m)).ToList();
        if (targets.Count == 0)
        {
            Error = Loc.T("Machines_NoFreeTarget");
            return;
        }

        var answer = PromptWindow.Ask(new PromptOptions
        {
            Title = Loc.T("Machines_TransferTitle", card.Name),
            Message = Loc.T("Machines_TransferMessage"),
            ConfirmText = Loc.T("Machines_Transfer"),
            ChoiceLabel = Loc.T("Machines_TransferTarget"),
            Choices = targets,
            SelectedChoice = targets.FirstOrDefault(t => ((MachineCardViewModel)t.Value).IsOnline),
            InputLabel = Loc.T("Common_ReasonOptional"),
        });
        if (answer?.SelectedChoice?.Value is MachineCardViewModel target)
        {
            await RunAsync(async () => Context.Upsert(await session.Api.PostAsync<MachineStateDto>($"{M(card)}/session/transfer",
                new TransferSessionRequest(target.Id, answer.Input, AllowOffline: !target.IsOnline))));
        }
    }

    [RelayCommand]
    private async Task MessageAsync(MachineCardViewModel? card)
    {
        if (AskMessage(card is null ? Loc.T("Machines_MessageAllTitle") : Loc.T("Machines_MessageTitle", card.Name)) is not { } text)
        {
            return;
        }

        await RunAsync(async () =>
        {
            if (card is null)
            {
                var result = await session.Api.PostAsync<BulkResult>($"{Context.Path}/message-all", new SendMessageRequest(text));
                Notice = Loc.T("Machines_SentTo", result.Sent, result.Offline);
            }
            else
            {
                await session.Api.PostAsync($"{M(card)}/message", new SendMessageRequest(text));
                Notice = Loc.T("Machines_MessageSent", card.Name);
            }
        });
    }

    [RelayCommand]
    private Task MessageAllAsync() => MessageAsync(null);

    [RelayCommand]
    private Task LockAsync(MachineCardViewModel? card) => CommandAsync(card, MachineCommand.Lock, confirm: false);

    [RelayCommand]
    private Task RestartAsync(MachineCardViewModel? card) => CommandAsync(card, MachineCommand.Restart, confirm: true);

    [RelayCommand]
    private Task ShutdownAsync(MachineCardViewModel? card) => CommandAsync(card, MachineCommand.Shutdown, confirm: true);

    [RelayCommand]
    private Task LockAllAsync() => CommandAsync(null, MachineCommand.Lock, confirm: true);

    [RelayCommand]
    private Task RestartAllAsync() => CommandAsync(null, MachineCommand.Restart, confirm: true);

    [RelayCommand]
    private Task ShutdownAllAsync() => CommandAsync(null, MachineCommand.Shutdown, confirm: true);

    [RelayCommand]
    private async Task ScreenshotAsync(MachineCardViewModel? card)
    {
        if (card is null)
        {
            return;
        }

        byte[]? image = null;
        if (await RunAsync(async () => image = await session.Api.PostForBytesAsync($"{M(card)}/screenshot", new { })) && image is not null)
        {
            new ImageWindow(Loc.T("Machines_ScreenshotTitle", card.Name, DateTime.Now.ToString("g", Loc.Instance.Culture)), image)
            {
                Owner = System.Windows.Application.Current.MainWindow,
            }.ShowDialog();
        }
    }

    [RelayCommand]
    private async Task PairNewAsync()
    {
        var suggestion = $"PC-{Context.Machines.Count + 1:00}";
        var answer = PromptWindow.Ask(new PromptOptions
        {
            Title = Loc.T("Machines_PairTitle"),
            Message = Loc.T("Machines_PairMessage"),
            ConfirmText = Loc.T("Machines_PairCreate"),
            InputLabel = Loc.T("Machines_Name"),
            Input = suggestion,
            InputRequired = true,
            MaxLength = 50,
        });
        if (answer is null)
        {
            return;
        }

        PairingCodeResponse? code = null;
        if (await RunAsync(async () => code = await session.Api.PostAsync<PairingCodeResponse>($"{Context.Path}/machines/pairing-codes",
                new CreatePairingCodeRequest(answer.Input.Trim()))) && code is not null)
        {
            ShowPairingCode(code);
        }
    }

    [RelayCommand]
    private async Task RepairCodeAsync(MachineCardViewModel? card)
    {
        if (card is null || !PromptWindow.Confirm(Loc.T("Machines_RepairTitle", card.Name), Loc.T("Machines_RepairMessage"), Loc.T("Machines_PairCreate")))
        {
            return;
        }

        PairingCodeResponse? code = null;
        if (await RunAsync(async () => code = await session.Api.PostAsync<PairingCodeResponse>($"{M(card)}/repair-code", new { })) && code is not null)
        {
            ShowPairingCode(code);
        }
    }

    [RelayCommand]
    private async Task RenameAsync(MachineCardViewModel? card)
    {
        if (card is null)
        {
            return;
        }

        var answer = PromptWindow.Ask(new PromptOptions
        {
            Title = Loc.T("Machines_RenameTitle", card.Name),
            ConfirmText = Loc.T("Common_Save"),
            InputLabel = Loc.T("Machines_Name"),
            Input = card.Name,
            InputRequired = true,
            MaxLength = 50,
        });
        if (answer is not null)
        {
            await RunAsync(() => session.Api.PutAsync(M(card), new UpdateMachineRequest(answer.Input.Trim(), card.GroupId, card.IsEnabled)));
        }
    }

    [RelayCommand]
    private async Task AssignGroupAsync(MachineCardViewModel? card)
    {
        if (card is null)
        {
            return;
        }

        List<MachineGroupDto> groups = [];
        if (!await RunAsync(async () => groups = await session.Api.GetAsync<List<MachineGroupDto>>($"{Context.Path}/groups")))
        {
            return;
        }

        var choices = new List<PromptChoice> { new(Loc.T("Machines_NoGroup"), Guid.Empty) };
        choices.AddRange(groups.Select(g => new PromptChoice(
            g.HourlyRateCents is { } rate ? $"{g.Name} ({Context.Money(rate)}/h)" : g.Name, g.Id)));
        var answer = PromptWindow.Ask(new PromptOptions
        {
            Title = Loc.T("Machines_GroupTitle", card.Name),
            Message = groups.Count == 0 ? Loc.T("Machines_GroupNone") : null,
            ConfirmText = Loc.T("Common_Save"),
            ChoiceLabel = Loc.T("Machines_Group"),
            Choices = choices,
            SelectedChoice = choices.FirstOrDefault(c => (Guid)c.Value == (card.GroupId ?? Guid.Empty)),
        });
        if (answer?.SelectedChoice is { } pick)
        {
            var groupId = (Guid)pick.Value;
            await RunAsync(() => session.Api.PutAsync(M(card), new UpdateMachineRequest(card.Name, groupId == Guid.Empty ? null : groupId, card.IsEnabled)));
        }
    }

    [RelayCommand]
    private async Task ToggleEnabledAsync(MachineCardViewModel? card)
    {
        if (card is not null)
        {
            await RunAsync(() => session.Api.PutAsync(M(card), new UpdateMachineRequest(card.Name, card.GroupId, !card.IsEnabled)));
        }
    }

    [RelayCommand]
    private async Task DeleteAsync(MachineCardViewModel? card)
    {
        if (card is not null && PromptWindow.Confirm(Loc.T("Machines_DeleteTitle", card.Name), Loc.T("Machines_DeleteMessage"), Loc.T("Common_Delete"), danger: true))
        {
            await RunAsync(async () =>
            {
                await session.Api.DeleteAsync(M(card));
                Context.Remove(card.Id);
            });
        }
    }

    private static void ShowPairingCode(PairingCodeResponse code) => PromptWindow.Ask(new PromptOptions
    {
        Title = Loc.T("Machines_PairCodeTitle", code.MachineName),
        Message = Loc.T("Machines_PairCodeMessage", code.ExpiresAtUtc.ToLocalTime().ToString("t", Loc.Instance.Culture)),
        BigText = code.Code,
        ShowCancel = false,
    });

    private static string? AskMessage(string title)
    {
        var answer = PromptWindow.Ask(new PromptOptions
        {
            Title = title,
            ConfirmText = Loc.T("Machines_Send"),
            InputLabel = Loc.T("Machines_MessageText"),
            InputRequired = true,
            MaxLength = 300,
            QuickTexts = [Loc.T("Quick_5Min"), Loc.T("Quick_Closing"), Loc.T("Quick_Volume"), Loc.T("Quick_Counter")],
        });
        return answer?.Input.Trim();
    }

    private async Task CommandAsync(MachineCardViewModel? card, MachineCommand command, bool confirm)
    {
        var action = Loc.T("Cmd_" + command);
        if (confirm)
        {
            var message = card is null ? Loc.T("Machines_ConfirmAll", action) : Loc.T("Machines_ConfirmOne", action, card.Name);
            if (!PromptWindow.Confirm(action, message, action, danger: command != MachineCommand.Lock))
            {
                return;
            }
        }

        await RunAsync(async () =>
        {
            if (card is null)
            {
                var result = await session.Api.PostAsync<BulkResult>($"{Context.Path}/command-all", new MachineCommandRequest(command));
                Notice = Loc.T("Machines_SentTo", result.Sent, result.Offline);
            }
            else
            {
                await session.Api.PostAsync($"{M(card)}/command", new MachineCommandRequest(command));
            }
        });
    }

    /// <summary>Calls a session endpoint and applies the returned state at once, without waiting for the push.</summary>
    private async Task SessionCallAsync(MachineCardViewModel card, string path, object body)
    {
        try
        {
            Error = null;
            Context.Upsert(await session.Api.PostAsync<MachineStateDto>(M(card) + path, body));
        }
        catch (ApiException ex) when (ex.Code == ErrorCodes.PriceChanged)
        {
            // Someone changed the rate while the dialog was open: load the new price so the next attempt shows it.
            Error = Format.Error(ex);
            await RunAsync(async () => Context.Apply(await session.Api.GetAsync<BranchStateDto>($"{Context.Path}/state")), showBusy: false);
            Error = Format.Error(ex);
        }
        catch (ApiException ex)
        {
            Error = Format.Error(ex);
        }
    }
}
