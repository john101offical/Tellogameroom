using System.Collections.ObjectModel;
using System.Globalization;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TelloGameRoom.Core.Dtos;
using TelloGameRoom.Core.Enums;
using TelloGameRoom.Core.Security;
using TelloGameRoom.Dashboard.Services;
using TelloGameRoom.Dashboard.Views;

namespace TelloGameRoom.Dashboard.ViewModels;

public sealed record Option<T>(T Value, string Label);

public sealed record GroupRow(MachineGroupDto Group, string RateText);

/// <summary>
/// Settings. Everyone gets the "This computer" section (language, theme); shop, branch, pricing, groups and backup
/// are shown to the owner only.
/// </summary>
public sealed partial class SettingsViewModel(Session session, BranchContext context, ThemeService themes) : PageViewModel
{
    private BranchSettingsDto? _branch;

    // ---- This computer ----
    [ObservableProperty]
    private Option<string>? _language;

    [ObservableProperty]
    private Option<string>? _theme;

    // ---- Shop ----
    [ObservableProperty]
    private string _shopName = string.Empty;

    [ObservableProperty]
    private Option<string>? _shopLanguage;

    [ObservableProperty]
    private bool _hasLogo;

    // ---- Branch ----
    [ObservableProperty]
    private string _branchName = string.Empty;

    [ObservableProperty]
    private string _hourlyRate = string.Empty;

    [ObservableProperty]
    private string _currencyCode = "COP";

    [ObservableProperty]
    private string _currencySymbol = "$";

    [ObservableProperty]
    private Option<int>? _decimals;

    [ObservableProperty]
    private Option<RoundingMode>? _rounding;

    [ObservableProperty]
    private string _roundingStep = string.Empty;

    [ObservableProperty]
    private string _warningMinutes = "5";

    [ObservableProperty]
    private string _timeZone = "America/Bogota";

    [ObservableProperty]
    private string _adminPinStatus = string.Empty;

    [ObservableProperty]
    private GroupRow? _selectedGroup;

    public bool IsOwner => session.Can(Permission.SettingsManage);

    public bool CanBackup => session.Can(Permission.BackupManage);

    public string ServerText => session.Server?.ToString() ?? string.Empty;

    public string VersionText => Loc.T("Settings_Version", typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "0");

    public IReadOnlyList<Option<string>> Languages { get; } = [new("en", "English"), new("es", "Español")];

    public IReadOnlyList<Option<string>> Themes { get; } = [new("Dark", Loc.T("Settings_Dark")), new("Light", Loc.T("Settings_Light"))];

    public IReadOnlyList<Option<int>> DecimalOptions { get; } = [new(0, "0"), new(2, "2")];

    public IReadOnlyList<Option<RoundingMode>> RoundingOptions { get; } =
    [
        new(RoundingMode.None, Loc.T("Rounding_None")),
        new(RoundingMode.Nearest, Loc.T("Rounding_Nearest")),
        new(RoundingMode.Up, Loc.T("Rounding_Up")),
        new(RoundingMode.Down, Loc.T("Rounding_Down")),
    ];

    public ObservableCollection<GroupRow> Groups { get; } = [];

    /// <summary>Raised after the list of branches changed so the shell can refresh its branch picker.</summary>
    public event Action? BranchesChanged;

    public override async Task ActivateAsync()
    {
        Language = Languages.FirstOrDefault(l => l.Value == Loc.Instance.Language) ?? Languages[0];
        Theme = Themes.FirstOrDefault(t => t.Value == session.Settings.Theme) ?? Themes[0];
        OnPropertyChanged(nameof(IsOwner));
        if (IsOwner)
        {
            await LoadAsync();
        }
    }

    partial void OnLanguageChanged(Option<string>? value)
    {
        if (value is not null && value.Value != Loc.Instance.Language)
        {
            Loc.Instance.SetLanguage(value.Value);
            session.Settings.Language = value.Value;
            session.Settings.Save();
        }
    }

    partial void OnThemeChanged(Option<string>? value)
    {
        if (value is not null && value.Value != session.Settings.Theme)
        {
            session.Settings.Theme = value.Value;
            session.Settings.Save();
            themes.Apply(value.Value);
        }
    }

    [RelayCommand]
    private Task SaveShopAsync() => RunAsync(async () =>
    {
        var saved = await session.Api.PutAsync<ShopSettingsDto>("/settings/shop", new UpdateShopSettingsRequest(ShopName.Trim(), ShopLanguage?.Value ?? "en"));
        context.ShopName = saved.ShopName;
        Notice = Loc.T("Settings_Saved");
    });

    [RelayCommand]
    private Task UploadLogoAsync() => RunAsync(async () =>
    {
        if (Files.Open(Files.ImageFilter, 1024 * 1024) is { } bytes)
        {
            var type = bytes.Length > 1 && bytes[0] == 0x89 ? "image/png" : "image/jpeg";
            var saved = await session.Api.SendBytesAsync<ShopSettingsDto>(HttpMethod.Put, "/settings/shop/logo", bytes, type);
            HasLogo = saved?.LogoVersion is not null;
            Notice = Loc.T("Settings_Saved");
        }
    });

    [RelayCommand]
    private Task RemoveLogoAsync() => RunAsync(async () =>
    {
        await session.Api.DeleteAsync<ShopSettingsDto>("/settings/shop/logo");
        HasLogo = false;
    });

    [RelayCommand]
    private Task SaveBranchAsync() => SaveBranchCoreAsync(null);

    [RelayCommand]
    private async Task ChangePinAsync()
    {
        var answer = PromptWindow.Ask(new PromptOptions
        {
            Title = Loc.T("Settings_PinTitle"),
            Message = Loc.T("Settings_PinMessage"),
            ConfirmText = Loc.T("Common_Save"),
            InputLabel = Loc.T("Settings_PinLabel"),
            IsPassword = true,
            MaxLength = 12,
            Validate = o => o.Input.Length == 0 || (o.Input.Length >= 4 && o.Input.All(char.IsAsciiDigit)) ? null : Loc.T("Settings_PinRule"),
        });
        if (answer is not null)
        {
            await SaveBranchCoreAsync(answer.Input);
        }
    }

    [RelayCommand]
    private async Task AddBranchAsync()
    {
        var answer = PromptWindow.Ask(new PromptOptions
        {
            Title = Loc.T("Settings_AddBranch"),
            Message = Loc.T("Settings_AddBranchMessage"),
            ConfirmText = Loc.T("Common_Add"),
            InputLabel = Loc.T("Settings_BranchName"),
            InputRequired = true,
            MaxLength = 100,
        });
        if (answer is not null && await RunAsync(() => session.Api.PostAsync<BranchSettingsDto>("/branches", new CreateBranchRequest(answer.Input.Trim()))))
        {
            await session.ReloadUserAsync();
            BranchesChanged?.Invoke();
            Notice = Loc.T("Settings_BranchAdded");
        }
    }

    [RelayCommand]
    private Task AddGroupAsync() => EditGroupAsync(null);

    [RelayCommand]
    private Task EditSelectedGroupAsync() => SelectedGroup is null ? Task.CompletedTask : EditGroupAsync(SelectedGroup.Group);

    [RelayCommand]
    private async Task DeleteGroupAsync()
    {
        if (SelectedGroup is { Group: var group }
            && PromptWindow.Confirm(Loc.T("Settings_DeleteGroupTitle", group.Name), Loc.T("Settings_DeleteGroupMessage"), Loc.T("Common_Delete"), danger: true)
            && await RunAsync(() => session.Api.DeleteAsync($"{context.Path}/groups/{group.Id}")))
        {
            await LoadGroupsAsync();
        }
    }

    [RelayCommand]
    private Task BackupAsync() => RunAsync(async () =>
    {
        var bytes = await session.Api.GetBytesAsync("/admin/backup");
        if (Files.Save(bytes, $"tellogameroom-{DateTime.Now:yyyyMMdd-HHmm}.db", Files.BackupFilter, open: false))
        {
            Notice = Loc.T("Settings_BackupSaved");
        }
    });

    [RelayCommand]
    private async Task RestoreAsync()
    {
        if (!PromptWindow.Confirm(Loc.T("Settings_RestoreTitle"), Loc.T("Settings_RestoreMessage"), Loc.T("Settings_Restore"), danger: true))
        {
            return;
        }

        await RunAsync(async () =>
        {
            if (Files.Open(Files.BackupFilter, 2L * 1024 * 1024 * 1024) is { } bytes)
            {
                await session.Api.SendBytesAsync<object>(HttpMethod.Post, "/admin/restore", bytes, "application/octet-stream");
                PromptWindow.Info(Loc.T("Settings_RestoreTitle"), Loc.T("Settings_RestoreDone"));
            }
        });
    }

    private async Task LoadAsync()
    {
        await RunAsync(async () =>
        {
            var shop = await session.Api.GetAsync<ShopSettingsDto>("/settings/shop");
            ShopName = shop.ShopName;
            ShopLanguage = Languages.FirstOrDefault(l => l.Value == shop.DefaultLanguage) ?? Languages[0];
            HasLogo = shop.LogoVersion is not null;

            var branches = await session.Api.GetAsync<List<BranchSettingsDto>>("/branches");
            _branch = branches.FirstOrDefault(b => b.Id == context.BranchId);
            if (_branch is { } b)
            {
                BranchName = b.Name;
                HourlyRate = Format.MoneyInput(b.HourlyRateCents, b.CurrencyDecimals);
                CurrencyCode = b.CurrencyCode;
                CurrencySymbol = b.CurrencySymbol;
                Decimals = DecimalOptions.FirstOrDefault(d => d.Value == b.CurrencyDecimals) ?? DecimalOptions[0];
                Rounding = RoundingOptions.First(r => r.Value == b.RoundingMode);
                RoundingStep = Format.MoneyInput(b.RoundingIncrementCents, b.CurrencyDecimals);
                WarningMinutes = (b.WarningThresholdSeconds / 60).ToString(CultureInfo.InvariantCulture);
                TimeZone = b.TimeZoneId;
                AdminPinStatus = b.HasAdminPin ? Loc.T("Settings_PinSet") : Loc.T("Settings_PinNotSet");
            }
        });
        await LoadGroupsAsync();
    }

    private Task LoadGroupsAsync() => RunAsync(async () =>
    {
        var groups = await session.Api.GetAsync<List<MachineGroupDto>>($"{context.Path}/groups");
        Groups.Clear();
        foreach (var g in groups)
        {
            Groups.Add(new GroupRow(g, g.HourlyRateCents is { } rate ? context.Money(rate) + "/h" : Loc.T("Settings_GroupBranchRate")));
        }
    }, showBusy: false);

    private async Task SaveBranchCoreAsync(string? newPin)
    {
        if (_branch is null)
        {
            return;
        }

        var rate = Format.ParseMoney(HourlyRate);
        var step = Format.ParseMoney(RoundingStep) ?? 0;
        if (rate is null || !int.TryParse(WarningMinutes, NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) || minutes > 60)
        {
            Error = Loc.T("Settings_InvalidNumbers");
            return;
        }

        await RunAsync(async () =>
        {
            _branch = await session.Api.PutAsync<BranchSettingsDto>($"/branches/{_branch.Id}/settings", new UpdateBranchSettingsRequest(
                BranchName.Trim(), _branch.IsActive, TimeZone.Trim(), rate.Value, CurrencyCode.Trim(), CurrencySymbol.Trim(), Decimals?.Value ?? 0,
                Rounding?.Value ?? RoundingMode.None, step, minutes * 60, newPin));
            AdminPinStatus = _branch.HasAdminPin ? Loc.T("Settings_PinSet") : Loc.T("Settings_PinNotSet");
            BranchesChanged?.Invoke();
            Notice = Loc.T("Settings_Saved");
        });
    }

    private async Task EditGroupAsync(MachineGroupDto? existing)
    {
        var answer = PromptWindow.Ask(new PromptOptions
        {
            Title = existing is null ? Loc.T("Settings_AddGroup") : Loc.T("Settings_EditGroup", existing.Name),
            Message = Loc.T("Settings_GroupMessage"),
            ConfirmText = Loc.T("Common_Save"),
            InputLabel = Loc.T("Settings_GroupName"),
            Input = existing?.Name ?? string.Empty,
            InputRequired = true,
            MaxLength = 50,
            SecondInputLabel = Loc.T("Settings_GroupRate"),
            SecondInput = existing?.HourlyRateCents is { } r ? Format.MoneyInput(r, context.Pricing?.CurrencyDecimals ?? 0) : string.Empty,
            Validate = o => string.IsNullOrWhiteSpace(o.SecondInput) || Format.ParseMoney(o.SecondInput) is not null ? null : Loc.T("Common_InvalidAmount"),
        });
        if (answer is null)
        {
            return;
        }

        var body = new UpsertMachineGroupRequest(answer.Input.Trim(), Format.ParseMoney(answer.SecondInput));
        if (await RunAsync(() => existing is null
                ? session.Api.PostAsync<MachineGroupDto>($"{context.Path}/groups", body)
                : session.Api.PutAsync<MachineGroupDto>($"{context.Path}/groups/{existing.Id}", body)))
        {
            await LoadGroupsAsync();
        }
    }
}
