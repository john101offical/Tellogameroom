using System.Collections.Frozen;
using TelloGameRoom.Core.Enums;

namespace TelloGameRoom.Core.Security;

/// <summary>
/// The permission model shared by server and clients. The server is the only place where it is enforced;
/// clients use it to hide controls the current user could not use anyway.
/// </summary>
public static class PermissionPolicy
{
    /// <summary>All permissions that exist.</summary>
    public static readonly FrozenSet<Permission> All = Enum.GetValues<Permission>().ToFrozenSet();

    /// <summary>Permissions an employee has unless the owner changes them.</summary>
    public static readonly FrozenSet<Permission> EmployeeDefaults = new[]
    {
        Permission.MachinesView,
        Permission.SessionsStart,
        Permission.SessionsAddTime,
        Permission.SessionsPauseResume,
        Permission.SessionsStop,
        Permission.SessionsTransfer,
        Permission.RemoteMessage,
        Permission.RemoteLock,
        Permission.ShiftsOwn,
    }.ToFrozenSet();

    /// <summary>
    /// Permissions the owner is allowed to grant to or revoke from employees.
    /// Anything outside this set (settings, employees, audit log, free time, ...) is always owner-only.
    /// </summary>
    public static readonly FrozenSet<Permission> EmployeeConfigurable = new[]
    {
        Permission.SessionsAddTime,
        Permission.SessionsRemoveTime,
        Permission.SessionsPauseResume,
        Permission.SessionsStop,
        Permission.SessionsCancelPaid,
        Permission.SessionsTransfer,
        Permission.RemoteMessage,
        Permission.RemoteLock,
        Permission.RemotePower,
    }.ToFrozenSet();

    /// <summary>
    /// Resolves the effective permission set for a role.
    /// </summary>
    /// <param name="role">The user's role.</param>
    /// <param name="employeeOverrides">
    /// Owner-configured overrides for employees: <c>true</c> grants, <c>false</c> revokes.
    /// Entries for permissions outside <see cref="EmployeeConfigurable"/> are ignored.
    /// </param>
    public static IReadOnlySet<Permission> Resolve(
        UserRole role,
        IReadOnlyDictionary<Permission, bool>? employeeOverrides = null)
    {
        if (role == UserRole.Owner)
        {
            return All;
        }

        var set = new HashSet<Permission>(EmployeeDefaults);
        if (employeeOverrides is not null)
        {
            foreach (var (permission, allowed) in employeeOverrides)
            {
                if (!EmployeeConfigurable.Contains(permission))
                {
                    continue;
                }

                if (allowed)
                {
                    set.Add(permission);
                }
                else
                {
                    set.Remove(permission);
                }
            }
        }

        return set;
    }
}
