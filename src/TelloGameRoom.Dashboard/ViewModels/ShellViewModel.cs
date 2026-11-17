using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using TelloGameRoom.Core.Client;
using TelloGameRoom.Core.Dtos;
using TelloGameRoom.Core.Security;
using TelloGameRoom.Dashboard.Services;
using Wpf.Ui.Controls;

namespace TelloGameRoom.Dashboard.ViewModels;

/// <summary>An entry of the left sidebar.</summary>
public sealed class NavItem(string titleKey, SymbolRegular icon, PageViewModel page) : ObservableObject
{
    public string TitleKey { get; } = titleKey;

    public string Title => Loc.T(TitleKey);

    public SymbolRegular Icon { get; } = icon;

    public PageViewModel Page { get; } = page;

    public void RefreshTitle() => OnPropertyChanged(nameof(Title));
}

/// <summary>
/// The signed-in application: sidebar, top bar (shop, branch, who is on duty, connection state, user) and the
/// current page. It owns the realtime connection and feeds the shared <see cref="BranchContext"/>.
/// </summary>
public sealed partial class ShellViewModel : ObservableObject
{
    private readonly Session _session;
    private readonly Realtime _realtime;
    private readonly Dispatcher _dispatcher = Application.Current.Dispatcher;
    private readonly DispatcherTimer _timer;
    private readonly SettingsViewModel _settings;
    private bool _loadingBranches;

    [ObservableProperty]
    private NavItem? _selectedNav;

    [ObservableProperty]
    private PageViewModel? _currentPage;

    [ObservableProperty]
    private BranchSummary? _selectedBranch;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConnectionText), nameof(IsConnected), nameof(IsReconnecting))]
    private ConnectionState _connection = ConnectionState.Connecting;

    public ShellViewModel(Session session, Realtime realtime, BranchContext context, ThemeService themes)
    {
        _session = session;
        _realtime = realtime;
        Context = context;
        _settings = new SettingsViewModel(session, context, themes);
        _settings.BranchesChanged += () => _ = ReloadBranchesAsync();

        _realtime.StateChanged += s => OnUi(() => Connection = s);
        _realtime.Snapshot += s => OnUi(() => Context.Apply(s));
        _realtime.MachineChanged += m => OnUi(() => Context.Upsert(m));
        _realtime.MachineRemoved += (_, id) => OnUi(() => Context.Remove(id));
        _realtime.BranchChanged += id => OnUi(() => _ = RefreshBranchAsync(id));
        Loc.Instance.PropertyChanged += (_, _) => OnUi(() =>
        {
            foreach (var item in Navigation)
            {
                item.RefreshTitle();
            }

            OnPropertyChanged(nameof(ConnectionText));
            OnPropertyChanged(nameof(RoleText));
        });

        _timer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Context.Tick();
    }

    public BranchContext Context { get; }

    public ObservableCollection<NavItem> Navigation { get; } = [];

    public ObservableCollection<BranchSummary> Branches { get; } = [];

    public bool HasSeveralBranches => Branches.Count > 1;

    public string UserName => _session.User?.DisplayName ?? string.Empty;

    public string UserInitial => UserName.Length > 0 ? UserName[..1].ToUpperInvariant() : "?";

    public string RoleText => _session.IsOwner ? Loc.T("Role_Owner") : Loc.T("Role_Employee");

    public bool IsConnected => Connection == ConnectionState.Connected;

    public bool IsReconnecting => Connection == ConnectionState.Reconnecting;

    public string ConnectionText => Connection switch
    {
        ConnectionState.Connected => Loc.T("Conn_Connected"),
        ConnectionState.Connecting => Loc.T("Conn_Connecting"),
        _ => Loc.T("Conn_Reconnecting"),
    };

    /// <summary>Raised when the user signs out.</summary>
    public event Action? SignedOut;

    public async Task StartAsync()
    {
        BuildNavigation();
        _loadingBranches = true;
        Branches.Clear();
        foreach (var branch in _session.User?.Branches ?? [])
        {
            Branches.Add(branch);
        }

        _loadingBranches = false;
        OnPropertyChanged(nameof(HasSeveralBranches));
        OnPropertyChanged(nameof(UserName));
        OnPropertyChanged(nameof(UserInitial));
        OnPropertyChanged(nameof(RoleText));

        _realtime.Start();
        _timer.Start();
        SelectedBranch = Branches.FirstOrDefault(b => b.Id == _session.Settings.LastBranchId) ?? Branches.FirstOrDefault();
        SelectedNav = Navigation.FirstOrDefault();
        await Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        _timer.Stop();
        await _realtime.StopAsync();
        Context.Clear();
    }

    [RelayCommand]
    private async Task SignOutAsync()
    {
        await StopAsync();
        await _session.LogoutAsync();
        SignedOut?.Invoke();
    }

    partial void OnSelectedNavChanged(NavItem? value)
    {
        if (value is null)
        {
            return;
        }

        CurrentPage = value.Page;

        // Right after sign-in the branch is still loading; SwitchBranchAsync activates the page once it has arrived.
        if (Context.BranchId != Guid.Empty)
        {
            _ = ActivateAsync(value.Page);
        }
    }

    partial void OnSelectedBranchChanged(BranchSummary? value)
    {
        if (value is null || _loadingBranches)
        {
            return;
        }

        _session.Settings.LastBranchId = value.Id;
        _session.Settings.Save();
        _ = SwitchBranchAsync(value);
    }

    private async Task SwitchBranchAsync(BranchSummary branch)
    {
        try
        {
            // Load over REST first so the grid appears even if the WebSocket is slow to connect.
            Context.Apply(await _session.Api.GetAsync<BranchStateDto>($"/branches/{branch.Id}/state"));
            await _realtime.SubscribeAsync(branch.Id);
            if (CurrentPage is not null)
            {
                await ActivateAsync(CurrentPage);
            }
        }
        catch (Exception ex) when (ex is ApiException or InvalidOperationException)
        {
            Log.Warning("Could not load branch {Branch}: {Message}", branch.Name, ex.Message);
            if (CurrentPage is not null)
            {
                CurrentPage.Error = Format.Error(ex);
            }
        }
    }

    private async Task RefreshBranchAsync(Guid branchId)
    {
        if (branchId != Context.BranchId)
        {
            return;
        }

        try
        {
            Context.Apply(await _session.Api.GetAsync<BranchStateDto>($"/branches/{branchId}/state"));
        }
        catch (ApiException ex)
        {
            Log.Information("Branch refresh failed: {Message}", ex.Message);
        }
    }

    private async Task ReloadBranchesAsync()
    {
        var current = SelectedBranch?.Id;
        _loadingBranches = true;
        Branches.Clear();
        foreach (var branch in _session.User?.Branches ?? [])
        {
            Branches.Add(branch);
        }

        SelectedBranch = Branches.FirstOrDefault(b => b.Id == current) ?? Branches.FirstOrDefault();
        _loadingBranches = false;
        OnPropertyChanged(nameof(HasSeveralBranches));
        if (SelectedBranch is { } selected)
        {
            await RefreshBranchAsync(selected.Id);
        }
    }

    private static async Task ActivateAsync(PageViewModel page)
    {
        try
        {
            await page.ActivateAsync();
        }
        catch (Exception ex) when (ex is ApiException or InvalidOperationException)
        {
            page.Error = Format.Error(ex);
        }
    }

    /// <summary>Only pages the user is allowed to use appear in the sidebar. The server enforces the same rules.</summary>
    private void BuildNavigation()
    {
        Navigation.Clear();
        if (_session.Can(Permission.ReportsView))
        {
            Navigation.Add(new NavItem("Nav_Overview", SymbolRegular.Home24, new ReportsViewModel(_session, Context, isOverview: true)));
        }

        // Daily work first for employees; owners land on the overview.
        var machines = new NavItem("Nav_Machines", SymbolRegular.Desktop24, new MachinesViewModel(_session, Context));
        if (_session.Can(Permission.ReportsView))
        {
            Navigation.Add(machines);
        }
        else
        {
            Navigation.Insert(0, machines);
        }

        if (_session.Can(Permission.ShiftsOwn))
        {
            Navigation.Add(new NavItem("Nav_Shifts", SymbolRegular.Clock24, new ShiftsViewModel(_session, Context)));
        }

        if (_session.Can(Permission.ReportsView))
        {
            Navigation.Add(new NavItem("Nav_Reports", SymbolRegular.ChartMultiple24, new ReportsViewModel(_session, Context, isOverview: false)));
        }

        if (_session.Can(Permission.EmployeesManage))
        {
            Navigation.Add(new NavItem("Nav_Employees", SymbolRegular.People24, new EmployeesViewModel(_session)));
        }

        if (_session.Can(Permission.AuditView))
        {
            Navigation.Add(new NavItem("Nav_Audit", SymbolRegular.History24, new AuditViewModel(_session, Context)));
        }

        Navigation.Add(new NavItem("Nav_Settings", SymbolRegular.Settings24, _settings));
    }

    private void OnUi(Action action)
    {
        if (_dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            _dispatcher.BeginInvoke(action);
        }
    }
}
