using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Auraly.Platform.Application.Common.DTOs;
using Auraly.Platform.Application.Common.Exceptions;
using Auraly.Platform.Application.Identity.DTOs;
using Auraly.Platform.Application.Identity.Interfaces;
using Auraly.Api.Authorization;
using Auraly.Api.Extensions;

namespace Auraly.Api.Controllers;

[ApiController]
[Route("api/v1/users")]
[Authorize]
public class UsersController(IUserService userService) : ControllerBase
{
    [HttpGet]
    [PermissionAuthorize("users.read")]
    public async Task<ActionResult<PagedResponse<UserDto>>> GetAll([FromQuery] PagedRequest request, [FromQuery] Guid? tenantId, CancellationToken ct)
    {
        var ownTenantId = User.GetTenantId();
        var requestedTenantId = tenantId ?? ownTenantId;
        EnsureSelectedTenant(requestedTenantId);
        return Ok(await userService.GetPagedAsync(requestedTenantId, request, ct));
    }

    [HttpGet("{userId:guid}")]
    [PermissionAuthorize("users.read")]
    public async Task<ActionResult<UserDto>> GetById(Guid userId, CancellationToken ct)
    {
        var user = await userService.GetByIdAsync(userId, ct);
        EnsureScope(user);
        return Ok(user);
    }

    [HttpPost]
    [PermissionAuthorize("users.create")]
    public async Task<ActionResult<UserDto>> Create([FromBody] CreateUserRequest request, CancellationToken ct)
    {
        var result = await userService.CreateAsync(User.GetTenantId(), request, User.GetUserId(), ct);
        return CreatedAtAction(nameof(GetById), new { userId = result.UserId }, result);
    }

    [HttpPut("{userId:guid}")]
    [PermissionAuthorize("users.update")]
    public async Task<ActionResult<UserDto>> Update(Guid userId, [FromBody] UpdateUserRequest request, CancellationToken ct)
    {
        await EnsureScopeAsync(userId, ct);
        return Ok(await userService.UpdateAsync(userId, request, ct));
    }

    [HttpPost("{userId:guid}/reset-password")]
    [PermissionAuthorize("users.update")]
    public async Task<IActionResult> ResetPassword(Guid userId, [FromBody] ResetUserPasswordRequest request, CancellationToken ct)
    {
        await EnsureScopeAsync(userId, ct);
        await userService.ResetPasswordAsync(userId, request, ct);
        return NoContent();
    }

    [HttpPost("{userId:guid}/deactivate")]
    [PermissionAuthorize("users.delete")]
    public async Task<IActionResult> Deactivate(Guid userId, CancellationToken ct)
    {
        await EnsureScopeAsync(userId, ct);
        await userService.DeactivateAsync(userId, ct);
        return NoContent();
    }

    [HttpPost("{userId:guid}/activate")]
    [PermissionAuthorize("users.delete")]
    public async Task<IActionResult> Activate(Guid userId, CancellationToken ct)
    {
        await EnsureScopeAsync(userId, ct);
        await userService.ActivateAsync(userId, ct);
        return NoContent();
    }

    [HttpPost("{userId:guid}/roles")]
    [PermissionAuthorize("users.assign_role")]
    public async Task<IActionResult> AssignRole(Guid userId, [FromBody] AssignRoleRequest request, CancellationToken ct)
    {
        await EnsureOwnTenantAsync(userId, ct);
        await userService.AssignRoleAsync(userId, request, User.GetUserId(), ct);
        return NoContent();
    }

    [HttpDelete("{userId:guid}/roles/{roleId:guid}")]
    [PermissionAuthorize("users.remove_role")]
    public async Task<IActionResult> RemoveRole(Guid userId, Guid roleId, [FromQuery] Guid? businessId, CancellationToken ct)
    {
        await EnsureOwnTenantAsync(userId, ct);
        await userService.RemoveRoleAsync(userId, roleId, businessId, User.GetUserId(), ct);
        return NoContent();
    }

    [HttpGet("{userId:guid}/permissions")]
    [PermissionAuthorize("users.read")]
    public async Task<ActionResult<IReadOnlyList<string>>> GetPermissions(Guid userId, [FromQuery] Guid? businessId, CancellationToken ct)
    {
        await EnsureScopeAsync(userId, ct);
        return Ok(await userService.GetUserPermissionsAsync(userId, businessId, ct));
    }

    private void EnsureSelectedTenant(Guid requestedTenantId)
    {
        if (requestedTenantId != User.GetTenantId())
            throw new ForbiddenException(
                "Selecciona la organización antes de consultar sus usuarios.");
    }

    private async Task EnsureScopeAsync(Guid userId, CancellationToken ct) =>
        EnsureScope(await userService.GetByIdAsync(userId, ct));

    private async Task EnsureOwnTenantAsync(Guid userId, CancellationToken ct)
    {
        var target = await userService.GetByIdAsync(userId, ct);
        if (target.TenantId != User.GetTenantId())
            throw new ForbiddenException("Los roles se administran únicamente dentro de la organización que los define.");
    }

    private void EnsureScope(UserDto user)
    {
        if (user.TenantId == User.GetTenantId()) return;
        throw new ForbiddenException("No puede administrar usuarios de otra organización.");
    }
}
