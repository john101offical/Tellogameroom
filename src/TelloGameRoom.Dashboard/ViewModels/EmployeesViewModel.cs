using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TelloGameRoom.Core.Dtos;
using TelloGameRoom.Core.Enums;
using TelloGameRoom.Core.Security;
using TelloGameRoom.Dashboard.Services;
using TelloGameRoom.Dashboard.Views;

namespace TelloGameRoom.Dashboard.ViewModels;

public sealed record EmployeeRow(EmployeeDto Employee, string RoleText, string StatusText, string LastLogin, string Branches);

/// <summary>A switch for one configurable employee permission.</summary>
public sealed partial class PermissionToggle(Permission permission, bool allowed, Func<PermissionToggle, Task> onChanged) : ObservableObject
{
    private bool _loaded;

    [ObservableProperty]
    private bool _allowed = allowed;

    public Permission Permission { get; } = permission;

    public string Label => Loc.T("Perm_" + Permission);

    public void MarkLoaded() => _loaded = true;

    partial void OnAllowedChanged(bool value)
    {
        if (_loaded)
        {
            _ = onChanged(this);
        }
    }
}

/// <summary>A branch the employee may work in, as a check box in the editor.</summary>
public sealed partial class BranchCheck(Guid id, string name, bool isChecked) : ObservableObject
{
    [ObservableProperty]
    private bool _isChecked = isChecked;

    public Guid Id { get; } = id;

    public string Name { get; } = name;
}

/// <summary>State of the add/edit employee dialog.</summary>
public sealed partial class EmployeeEditorViewModel : ObservableObject
{
    [ObservableProperty]
    private string _username = string.Empty;

    [ObservableProperty]
    private string _displayName = string.Empty;

    [ObservableProperty]
    private bool _isOwner;

    [ObservableProperty]
    private bool _isActive = true;

    [ObservableProperty]
    private string? _error;

    public bool IsNew { get; init; }

    public string Title => IsNew ? Loc.T("Employees_AddTitle") : Loc.T("Employees_EditTitle", DisplayName);

    public ObservableCollection<BranchCheck> Branches { get; } = [];

    /// <summary>Saves; returns an error message or null. Set by the page.</summary>
    public Func<EmployeeEditorViewModel, string, Task<string?>>? Save { get; init; }
}

/// <summary>Owner-only: staff accounts and what employees are allowed to do.</summary>
public sealed partial class EmployeesViewModel(Session session) : PageViewModel
{
    private List<BranchSettingsDto> _branches = [];

    [ObservableProperty]
    private EmployeeRow? _selected;

    public ObservableCollection<EmployeeRow> Employees { get; } = [];

    public ObservableCollection<PermissionToggle> Permissions { get; } = [];

    public override Task ActivateAsync() => RefreshAsync();

    [RelayCommand]
    private Task RefreshAsync() => RunAsync(async () =>
    {
        _branches = await session.Api.GetAsync<List<BranchSettingsDto>>("/branches");
        var list = await session.Api.GetAsync<List<EmployeeDto>>("/employees");
        Employees.Clear();
        foreach (var e in list)
        {
            Employees.Add(ToRow(e));
        }

        var settings = await session.Api.GetAsync<List<PermissionSetting>>("/settings/employee-permissions");
        Permissions.Clear();
        foreach (var s in settings)
        {
            var toggle = new PermissionToggle(s.Permission, s.Allowed, SavePermissionAsync);
            toggle.MarkLoaded();
            Permissions.Add(toggle);
        }
    });

    [RelayCommand]
    private async Task AddAsync()
    {
        var editor = new EmployeeEditorViewModel
        {
            IsNew = true,
            Save = async (e, password) =>
            {
                try
                {
                    await session.Api.PostAsync<EmployeeDto>("/employees", new CreateEmployeeRequest(e.Username.Trim(), e.DisplayName.Trim(), password,
                        e.IsOwner ? UserRole.Owner : UserRole.Employee, [.. e.Branches.Where(b => b.IsChecked).Select(b => b.Id)]));
                    return null;
                }
                catch (Core.Client.ApiException ex)
                {
                    return Format.Error(ex);
                }
            },
        };
        foreach (var b in _branches.Where(b => b.IsActive))
        {
            editor.Branches.Add(new BranchCheck(b.Id, b.Name, _branches.Count == 1));
        }

        if (EmployeeWindow.Show(editor))
        {
            await RefreshAsync();
        }
    }

    [RelayCommand]
    private async Task EditAsync()
    {
        if (Selected is not { Employee: var employee })
        {
            return;
        }

        var editor = new EmployeeEditorViewModel
        {
            Username = employee.Username,
            DisplayName = employee.DisplayName,
            IsOwner = employee.Role == UserRole.Owner,
            IsActive = employee.IsActive,
            Save = async (e, _) =>
            {
                try
                {
                    await session.Api.PutAsync<EmployeeDto>($"/employees/{employee.Id}", new UpdateEmployeeRequest(e.DisplayName.Trim(),
                        e.IsOwner ? UserRole.Owner : UserRole.Employee, e.IsActive, [.. e.Branches.Where(b => b.IsChecked).Select(b => b.Id)]));
                    return null;
                }
                catch (Core.Client.ApiException ex)
                {
                    return Format.Error(ex);
                }
            },
        };
        foreach (var b in _branches)
        {
            editor.Branches.Add(new BranchCheck(b.Id, b.Name, employee.BranchIds.Contains(b.Id)));
        }

        if (EmployeeWindow.Show(editor))
        {
            await RefreshAsync();
        }
    }

    [RelayCommand]
    private async Task ResetPasswordAsync()
    {
        if (Selected is not { Employee: var employee })
        {
            return;
        }

        var answer = PromptWindow.Ask(new PromptOptions
        {
            Title = Loc.T("Employees_ResetTitle", employee.DisplayName),
            Message = Loc.T("Employees_ResetMessage"),
            ConfirmText = Loc.T("Employees_ResetPassword"),
            InputLabel = Loc.T("Login_Password"),
            IsPassword = true,
            MaxLength = 128,
            Validate = o => o.Input.Length < 8 ? Loc.T("Setup_PasswordRule") : null,
        });
        if (answer is not null && await RunAsync(() => session.Api.PostAsync($"/employees/{employee.Id}/reset-password", new ResetPasswordRequest(answer.Input))))
        {
            Notice = Loc.T("Employees_ResetDone", employee.DisplayName);
            await RefreshAsync();
        }
    }

    [RelayCommand]
    private async Task ToggleActiveAsync()
    {
        if (Selected is not { Employee: var e })
        {
            return;
        }

        if (e.IsActive && !PromptWindow.Confirm(Loc.T("Employees_DeactivateTitle", e.DisplayName), Loc.T("Employees_DeactivateMessage"),
                Loc.T("Employees_Deactivate"), danger: true))
        {
            return;
        }

        if (await RunAsync(() => session.Api.PutAsync<EmployeeDto>($"/employees/{e.Id}", new UpdateEmployeeRequest(e.DisplayName, e.Role, !e.IsActive, e.BranchIds))))
        {
            await RefreshAsync();
        }
    }

    private Task SavePermissionAsync(PermissionToggle toggle) => RunAsync(() =>
        session.Api.PutAsync<List<PermissionSetting>>("/settings/employee-permissions",
            new UpdateEmployeePermissionsRequest(new Dictionary<Permission, bool> { [toggle.Permission] = toggle.Allowed })), showBusy: false);

    private EmployeeRow ToRow(EmployeeDto e) => new(
        e,
        e.Role == UserRole.Owner ? Loc.T("Role_Owner") : Loc.T("Role_Employee"),
        !e.IsActive ? Loc.T("Employees_Inactive") : e.IsLockedOut ? Loc.T("Employees_LockedOut") : Loc.T("Employees_Active"),
        e.LastLoginAtUtc is { } last ? Format.LocalTime(last) : "-",
        e.Role == UserRole.Owner ? Loc.T("Employees_AllBranches") : string.Join(", ", _branches.Where(b => e.BranchIds.Contains(b.Id)).Select(b => b.Name)));
}
