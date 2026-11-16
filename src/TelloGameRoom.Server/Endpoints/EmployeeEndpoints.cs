using TelloGameRoom.Core.Dtos;
using TelloGameRoom.Core.Security;
using TelloGameRoom.Server.Security;
using TelloGameRoom.Server.Services;

namespace TelloGameRoom.Server.Endpoints;

public static class EmployeeEndpoints
{
    public static void MapEmployeeEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/employees").WithTags("Employees").RequirePermission(Permission.EmployeesManage);

        group.MapGet("/", async (EmployeeService employees, CancellationToken ct) => Results.Ok(await employees.ListAsync(ct)));

        group.MapPost("/", async (CreateEmployeeRequest request, EmployeeService employees, CurrentUserAccessor accessor, CancellationToken ct) =>
            (await employees.CreateAsync(request, await accessor.RequireAsync(ct), ct)).ToResult());

        group.MapPut("/{id:guid}", async (Guid id, UpdateEmployeeRequest request, EmployeeService employees, CurrentUserAccessor accessor, CancellationToken ct) =>
            (await employees.UpdateAsync(id, request, await accessor.RequireAsync(ct), ct)).ToResult());

        group.MapPost("/{id:guid}/reset-password", async (Guid id, ResetPasswordRequest request, EmployeeService employees,
            CurrentUserAccessor accessor, CancellationToken ct) =>
        {
            var result = await employees.ResetPasswordAsync(id, request, await accessor.RequireAsync(ct), ct);
            return result.IsSuccess ? Results.NoContent() : result.Error!.ToResult();
        });

        var permissions = api.MapGroup("/settings/employee-permissions").WithTags("Employees").RequirePermission(Permission.EmployeesManage);

        permissions.MapGet("/", async (EmployeeService employees, CancellationToken ct) => Results.Ok(await employees.GetPermissionsAsync(ct)));

        permissions.MapPut("/", async (UpdateEmployeePermissionsRequest request, EmployeeService employees, CurrentUserAccessor accessor, CancellationToken ct) =>
            (await employees.UpdatePermissionsAsync(request, await accessor.RequireAsync(ct), ct)).ToResult());
    }
}
