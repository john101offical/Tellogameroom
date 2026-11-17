using System.Net.Http;
using System.Security.Authentication;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TelloGameRoom.Core.Client;
using TelloGameRoom.Core.Dtos;
using TelloGameRoom.Core.Protocol;
using TelloGameRoom.Dashboard.Services;

namespace TelloGameRoom.Dashboard.ViewModels;

/// <summary>Step 1 on a new computer: where is the server?</summary>
public sealed partial class ConnectViewModel : ObservableObject
{
    [ObservableProperty]
    private string _serverUrl = string.Empty;

    [ObservableProperty]
    private string _fingerprint = string.Empty;

    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    private bool _isBusy;

    public required Func<ConnectViewModel, Task> Submit { get; init; }

    [RelayCommand]
    private Task ConnectAsync() => Submit(this);
}

/// <summary>First run against a fresh server: create the owner account, the shop and its first branch.</summary>
public sealed partial class SetupViewModel : ObservableObject
{
    [ObservableProperty]
    private int _step = 1;

    [ObservableProperty]
    private string _shopName = string.Empty;

    [ObservableProperty]
    private string _branchName = string.Empty;

    [ObservableProperty]
    private string _hourlyRate = string.Empty;

    [ObservableProperty]
    private string _ownerName = string.Empty;

    [ObservableProperty]
    private string _username = string.Empty;

    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    private bool _isBusy;

    public required Func<SetupViewModel, string, string, Task> Submit { get; init; }

    public bool IsStep1 => Step == 1;

    public bool IsStep2 => Step == 2;

    [RelayCommand]
    private void Next()
    {
        if (string.IsNullOrWhiteSpace(ShopName) || string.IsNullOrWhiteSpace(BranchName))
        {
            Error = Loc.T("Setup_FillShop");
            return;
        }

        if (!string.IsNullOrWhiteSpace(HourlyRate) && Format.ParseMoney(HourlyRate) is null)
        {
            Error = Loc.T("Common_InvalidAmount");
            return;
        }

        Error = null;
        Step = 2;
    }

    [RelayCommand]
    private void Back()
    {
        Error = null;
        Step = 1;
    }

    /// <summary>Called by the view, which owns the password boxes.</summary>
    public Task FinishAsync(string password, string confirmation) => Submit(this, password, confirmation);

    partial void OnStepChanged(int value)
    {
        OnPropertyChanged(nameof(IsStep1));
        OnPropertyChanged(nameof(IsStep2));
    }
}

public sealed partial class LoginViewModel : ObservableObject
{
    [ObservableProperty]
    private string _username = string.Empty;

    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    private bool _isBusy;

    public required string ServerText { get; init; }

    public required Func<LoginViewModel, string, Task> Submit { get; init; }

    public required Action ChangeServer { get; init; }

    /// <summary>Called by the view, which owns the password box.</summary>
    public Task SignInAsync(string password) => Submit(this, password);

    [RelayCommand]
    private void SwitchServer() => ChangeServer();
}

/// <summary>
/// Decides which screen the main window shows: connect to a server, first-run setup, sign in, or the application.
/// </summary>
public sealed partial class RootViewModel(Session session, ShellViewModel shell) : ObservableObject
{
    [ObservableProperty]
    private object? _current;

    public async Task StartAsync()
    {
        shell.SignedOut += ShowLogin;
        session.Expired += () => Application.Current.Dispatcher.BeginInvoke(async () =>
        {
            await shell.StopAsync();
            ShowLogin();
            if (Current is LoginViewModel login)
            {
                login.Error = Loc.T("Login_Expired");
            }
        });

        if (string.IsNullOrWhiteSpace(session.Settings.ServerUrl) || !session.Configure(session.Settings.ServerUrl, session.Settings.PinnedCertificates))
        {
            ShowConnect(null);
            return;
        }

        await RouteAsync(null);
    }

    /// <summary>Asks the server whether it needs setup and shows the matching screen.</summary>
    private async Task RouteAsync(ConnectViewModel? connect)
    {
        try
        {
            var status = await session.GetSetupStatusAsync();
            if (status.SetupRequired)
            {
                Current = new SetupViewModel { Submit = SetupAsync };
            }
            else
            {
                ShowLogin();
            }
        }
        catch (Exception ex)
        {
            var message = DescribeConnectionError(ex);
            if (connect is not null)
            {
                connect.Error = message;
            }
            else
            {
                ShowConnect(message);
            }
        }
    }

    private void ShowConnect(string? error) => Current = new ConnectViewModel
    {
        ServerUrl = session.Settings.ServerUrl ?? string.Empty,
        Fingerprint = string.Join(" ", session.Settings.PinnedCertificates),
        Error = error,
        Submit = ConnectAsync,
    };

    private void ShowLogin() => Current = new LoginViewModel
    {
        Username = session.Settings.LastUsername ?? string.Empty,
        ServerText = session.Server?.Host ?? string.Empty,
        Submit = LoginAsync,
        ChangeServer = () => ShowConnect(null),
    };

    private async Task ConnectAsync(ConnectViewModel vm)
    {
        var pins = vm.Fingerprint.Split([' ', ',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(TelloHttp.NormalizeThumbprint).Where(p => p.Length == 64).ToArray();
        if (!session.Configure(vm.ServerUrl, pins))
        {
            vm.Error = Loc.T("Connect_InvalidAddress");
            return;
        }

        vm.IsBusy = true;
        vm.Error = null;
        await RouteAsync(vm);
        vm.IsBusy = false;
        if (vm.Error is null)
        {
            session.Settings.ServerUrl = session.Server!.ToString();
            session.Settings.PinnedCertificates = pins;
            session.Settings.Save();
        }
    }

    private async Task SetupAsync(SetupViewModel vm, string password, string confirmation)
    {
        if (string.IsNullOrWhiteSpace(vm.OwnerName) || string.IsNullOrWhiteSpace(vm.Username))
        {
            vm.Error = Loc.T("Setup_FillOwner");
            return;
        }

        if (password.Length < 8)
        {
            vm.Error = Loc.T("Setup_PasswordRule");
            return;
        }

        if (password != confirmation)
        {
            vm.Error = Loc.T("Setup_PasswordMismatch");
            return;
        }

        vm.IsBusy = true;
        vm.Error = null;
        try
        {
            var rate = Format.ParseMoney(vm.HourlyRate);
            await session.SetupAsync(new SetupRequest(vm.ShopName.Trim(), vm.BranchName.Trim(), vm.Username.Trim(), vm.OwnerName.Trim(), password,
                rate is null ? null : rate.Value / 100m));
            session.Settings.LastUsername = vm.Username.Trim();
            session.Settings.Save();
            await EnterAsync();
        }
        catch (ApiException ex) when (ex.Code == ErrorCodes.SetupAlreadyCompleted)
        {
            ShowLogin();
        }
        catch (ApiException ex)
        {
            vm.Error = Format.Error(ex);
        }
        finally
        {
            vm.IsBusy = false;
        }
    }

    private async Task LoginAsync(LoginViewModel vm, string password)
    {
        if (string.IsNullOrWhiteSpace(vm.Username) || password.Length == 0)
        {
            vm.Error = Loc.T("Login_Fill");
            return;
        }

        vm.IsBusy = true;
        vm.Error = null;
        try
        {
            await session.LoginAsync(vm.Username.Trim(), password);
            await EnterAsync();
        }
        catch (ApiException ex)
        {
            vm.Error = Format.Error(ex);
        }
        finally
        {
            vm.IsBusy = false;
        }
    }

    private async Task EnterAsync()
    {
        Current = shell;
        await shell.StartAsync();
    }

    private static string DescribeConnectionError(Exception ex) => ex switch
    {
        ApiException { Code: ErrorCodes.ProtocolMismatch } => Loc.T("Err_protocol_mismatch"),
        HttpRequestException { InnerException: AuthenticationException } or AuthenticationException => Loc.T("Connect_Certificate"),
        ApiException { IsNetwork: true } api when api.Message.Contains("SSL", StringComparison.OrdinalIgnoreCase)
            || api.Message.Contains("certificate", StringComparison.OrdinalIgnoreCase) => Loc.T("Connect_Certificate"),
        ApiException { IsNetwork: true } => Loc.T("Err_network"),
        _ => ex.Message,
    };
}
