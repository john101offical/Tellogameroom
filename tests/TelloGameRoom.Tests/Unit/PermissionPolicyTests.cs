using TelloGameRoom.Core.Enums;
using TelloGameRoom.Core.Security;

namespace TelloGameRoom.Tests.Unit;

public class PermissionPolicyTests
{
    [Fact]
    public void Owner_has_every_permission() =>
        Assert.True(PermissionPolicy.Resolve(UserRole.Owner).SetEquals(Enum.GetValues<Permission>()));

    [Theory]
    [InlineData(Permission.SessionsStart)]
    [InlineData(Permission.SessionsAddTime)]
    [InlineData(Permission.SessionsPauseResume)]
    [InlineData(Permission.RemoteMessage)]
    [InlineData(Permission.MachinesView)]
    [InlineData(Permission.ShiftsOwn)]
    public void Employee_can_do_daily_work_by_default(Permission permission) =>
        Assert.Contains(permission, PermissionPolicy.Resolve(UserRole.Employee));

    [Theory]
    [InlineData(Permission.SessionsRemoveTime)]
    [InlineData(Permission.SessionsCancelPaid)]
    [InlineData(Permission.SessionsFreeTime)]
    [InlineData(Permission.PricingManage)]
    [InlineData(Permission.SettingsManage)]
    [InlineData(Permission.EmployeesManage)]
    [InlineData(Permission.AuditView)]
    [InlineData(Permission.ReportsView)]
    [InlineData(Permission.ReportsExport)]
    [InlineData(Permission.RemoteScreenshot)]
    [InlineData(Permission.MachinesManage)]
    public void Employee_cannot_do_owner_things_by_default(Permission permission) =>
        Assert.DoesNotContain(permission, PermissionPolicy.Resolve(UserRole.Employee));

    [Fact]
    public void Owner_can_grant_and_revoke_configurable_permissions()
    {
        var set = PermissionPolicy.Resolve(UserRole.Employee, new Dictionary<Permission, bool>
        {
            [Permission.SessionsRemoveTime] = true,
            [Permission.RemoteMessage] = false,
        });
        Assert.Contains(Permission.SessionsRemoveTime, set);
        Assert.DoesNotContain(Permission.RemoteMessage, set);
    }

    [Fact]
    public void Non_configurable_permissions_can_never_be_granted_to_employees()
    {
        var overrides = PermissionPolicy.All.Except(PermissionPolicy.EmployeeConfigurable).ToDictionary(p => p, _ => true);
        var set = PermissionPolicy.Resolve(UserRole.Employee, overrides);
        Assert.True(set.SetEquals(PermissionPolicy.EmployeeDefaults));
        Assert.DoesNotContain(Permission.SessionsFreeTime, set);
    }

    [Fact]
    public void Core_selling_permissions_cannot_be_revoked_by_accident()
    {
        // Starting sessions and viewing machines are what an employee is for; they are not configurable.
        var set = PermissionPolicy.Resolve(UserRole.Employee, new Dictionary<Permission, bool>
        {
            [Permission.SessionsStart] = false,
            [Permission.MachinesView] = false,
        });
        Assert.Contains(Permission.SessionsStart, set);
        Assert.Contains(Permission.MachinesView, set);
    }

    [Fact]
    public void Permission_values_are_unique_and_stable()
    {
        var values = Enum.GetValues<Permission>().Select(p => (int)p).ToList();
        Assert.Equal(values.Count, values.Distinct().Count());
        Assert.Equal(110, (int)Permission.SessionsStart); // persisted: must never be renumbered
    }
}
