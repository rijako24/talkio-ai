using System.Net;
using System.Net.Http.Json;
using Auraly.Platform.Application.Identity.DTOs;
using Microsoft.Data.SqlClient;

namespace Auraly.ServerSlice.IntegrationTests;

[Collection(ServerSliceCollection.Name)]
public sealed class PlatformAdministrationAuthorizationTests(ServerSliceFixture fixture)
{
    private static readonly Guid AuralyTenantId = Guid.Parse("A0A10000-0000-0000-0000-000000000000");

    [Fact]
    public async Task Platform_administration_is_delegable_inside_auraly_and_denied_without_explicit_permissions()
    {
        var rootUserId = Guid.NewGuid();
        var customerTenantId = Guid.NewGuid();
        var customerUserId = Guid.NewGuid();
        var customerRoleId = Guid.NewGuid();
        await SeedActorsAsync(rootUserId, customerTenantId, customerUserId, customerRoleId);

        var rootPermissions = new[]
        {
            "permissions.read", "roles.read", "roles.create", "roles.assign_permissions",
            "users.read", "users.create", "users.update", "users.delete", "users.assign_role",
            "tenants.read", "tenants.capacity.update", "tenants.status.update",
            "tenants.devices.read",
            "tenants.devices.revoke", "platform.permissions.assign",
            "platform.fiscal_certificates.expiry.read"
        };
        using var root = fixture.CreateTenantUserClient(AuralyTenantId, rootUserId, rootPermissions);

        var permissions = await GetPermissionsAsync(root);
        var restrictedResources = new[] { "users.read", "users.update", "users.delete", "roles.assign_permissions" };
        var restrictedRole = await CreateRoleAsync(root, "Soporte sin acceso de plataforma");
        await AssignPermissionsAsync(root, restrictedRole.RoleId, PermissionIds(permissions, restrictedResources), HttpStatusCode.NoContent);
        var restrictedUser = await CreateUserAsync(root, "restricted");
        await AssignRoleAsync(root, restrictedUser.UserId, restrictedRole.RoleId);

        using var restricted = fixture.CreateTenantUserClient(AuralyTenantId, restrictedUser.UserId, restrictedResources);
        using var crossTenantUsers = await restricted.GetAsync($"/api/v1/users?tenantId={fixture.TenantId:D}&page=1&pageSize=10");
        Assert.Equal(HttpStatusCode.Forbidden, crossTenantUsers.StatusCode);

        using var crossTenantUpdate = await restricted.PutAsJsonAsync($"/api/v1/users/{fixture.UserId:D}", new { firstName = "No autorizado" });
        Assert.Equal(HttpStatusCode.Forbidden, crossTenantUpdate.StatusCode);

        using var userWithoutManagement = fixture.CreateTenantUserClient(
            AuralyTenantId, rootUserId);
        using var forbiddenSameTenantCredentialReset = await userWithoutManagement.PostAsJsonAsync(
            $"/api/v1/users/{restrictedUser.UserId:D}/reset-password",
            new { password = "Nueva-Clave-Segura-2026!" });
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenSameTenantCredentialReset.StatusCode);

        using var allowedSameTenantCredentialReset = await restricted.PostAsJsonAsync(
            $"/api/v1/users/{restrictedUser.UserId:D}/reset-password",
            new { password = "Nueva-Clave-Segura-2026!" });
        Assert.Equal(HttpStatusCode.NoContent, allowedSameTenantCredentialReset.StatusCode);

        using var crossTenantDevices = await restricted.GetAsync($"/api/v1/tenants/{fixture.TenantId:D}/devices");
        Assert.Equal(HttpStatusCode.Forbidden, crossTenantDevices.StatusCode);

        using var crossTenantCapacity = await restricted.PutAsJsonAsync($"/api/v1/tenants/{fixture.TenantId:D}", new { maximumUsers = 513 });
        Assert.Equal(HttpStatusCode.Forbidden, crossTenantCapacity.StatusCode);

        var forbiddenEscalation = PermissionIds(permissions, restrictedResources.Append("tenants.read"));
        await AssignPermissionsAsync(restricted, restrictedRole.RoleId, forbiddenEscalation, HttpStatusCode.Forbidden);

        var delegatedRole = await CreateRoleAsync(root, "Consulta multitenant delegada");
        var delegatedResources = new[] { "users.read", "tenants.read" };
        await AssignPermissionsAsync(root, delegatedRole.RoleId, PermissionIds(permissions, delegatedResources), HttpStatusCode.NoContent);
        var delegatedUser = await CreateUserAsync(root, "delegated", delegatedRole.RoleId);
        Assert.Contains(delegatedUser.Roles, assignment => assignment.RoleId == delegatedRole.RoleId);

        using var delegated = fixture.CreateTenantUserClient(AuralyTenantId, delegatedUser.UserId, delegatedResources);
        delegated.DefaultRequestHeaders.Add("X-Tenant-Id", fixture.TenantId.ToString("D"));
        using var allowedUsers = await delegated.GetAsync($"/api/v1/users?tenantId={fixture.TenantId:D}&page=1&pageSize=10");
        Assert.Equal(HttpStatusCode.OK, allowedUsers.StatusCode);

        using var rootInCustomerTenant = fixture.CreateTenantUserClient(
            AuralyTenantId, rootUserId, rootPermissions);
        rootInCustomerTenant.DefaultRequestHeaders.Add(
            "X-Tenant-Id", customerTenantId.ToString("D"));
        using var allowedCrossTenantReset = await rootInCustomerTenant.PostAsJsonAsync(
            $"/api/v1/users/{customerUserId:D}/reset-password",
            new { password = "Nueva-Clave-Segura-2026!" });
        Assert.Equal(HttpStatusCode.NoContent, allowedCrossTenantReset.StatusCode);

        using var allowedDevices = await root.GetAsync($"/api/v1/tenants/{fixture.TenantId:D}/devices");
        Assert.Equal(HttpStatusCode.OK, allowedDevices.StatusCode);
        using var allowedCertificateAlerts = await root.GetAsync(
            "/api/v1/tenants/fiscal-certificate-expiry-alerts");
        Assert.Equal(HttpStatusCode.OK, allowedCertificateAlerts.StatusCode);
        using var allowedCapacity = await root.PutAsJsonAsync($"/api/v1/tenants/{fixture.TenantId:D}", new { maximumUsers = 513 });
        var allowedCapacityBody = await allowedCapacity.Content.ReadAsStringAsync();
        Assert.True(allowedCapacity.StatusCode == HttpStatusCode.OK,
            $"Expected OK, got {allowedCapacity.StatusCode}: {allowedCapacityBody}");

        using var customer = fixture.CreateTenantUserClient(
            customerTenantId,
            customerUserId,
            "roles.assign_permissions",
            "platform.permissions.assign",
            "tenants.read",
            "platform.fiscal_certificates.expiry.read");
        using var forbiddenCertificateAlerts = await customer.GetAsync(
            "/api/v1/tenants/fiscal-certificate-expiry-alerts");
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenCertificateAlerts.StatusCode);
        using var customerCrossTenant = fixture.CreateTenantUserClient(
            customerTenantId, customerUserId, "users.read", "tenants.read");
        customerCrossTenant.DefaultRequestHeaders.Add(
            "X-Tenant-Id", AuralyTenantId.ToString("D"));
        using var forbiddenCustomerCrossTenantUsers = await customerCrossTenant.GetAsync(
            $"/api/v1/users?tenantId={AuralyTenantId:D}&page=1&pageSize=10");
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenCustomerCrossTenantUsers.StatusCode);
        await AssignPermissionsAsync(
            customer,
            customerRoleId,
            PermissionIds(permissions, ["roles.assign_permissions", "platform.permissions.assign", "tenants.read"]),
            HttpStatusCode.Forbidden);
    }

    private async Task SeedActorsAsync(Guid rootUserId, Guid customerTenantId, Guid customerUserId, Guid customerRoleId)
    {
        await using var connection = new SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DECLARE @AuralyRoleId UNIQUEIDENTIFIER=(SELECT RoleId FROM dbo.AppRoles WHERE TenantId=@AuralyTenantId AND NormalizedName=N'ADMINISTRATOR');
            IF @AuralyRoleId IS NULL THROW 51000,N'No existe el rol administrador del tenant @auraly.',1;
            INSERT dbo.AppUsers(UserId,TenantId,Username,NormalizedUsername,Email,NormalizedEmail,FirstName,LastName,IsActive,EmailConfirmed,CreatedAt)
            VALUES(@RootUserId,@AuralyTenantId,CONCAT(N'root-',@RootUserId),UPPER(CONCAT(N'root-',@RootUserId)),CONCAT(@RootUserId,N'@auraly.test'),UPPER(CONCAT(@RootUserId,N'@auraly.test')),N'Root',N'Auraly',1,1,SYSUTCDATETIME());
            INSERT dbo.UserRoles(UserRoleId,UserId,RoleId,BusinessId,AssignedAt) VALUES(NEWID(),@RootUserId,@AuralyRoleId,NULL,SYSUTCDATETIME());

            INSERT dbo.Tenants(TenantId,TenantKey,Name,Email,IsActive,MaximumUsers,MaximumEnrolledDevices,CreatedAt)
            VALUES(@CustomerTenantId,CONCAT(N'@customer-',LEFT(REPLACE(CONVERT(NVARCHAR(36),@CustomerTenantId),N'-',N''),12)),N'Cliente sin autoridad',CONCAT(@CustomerTenantId,N'@customer.test'),1,10,1,SYSUTCDATETIME());
            INSERT dbo.AppUsers(UserId,TenantId,Username,NormalizedUsername,Email,NormalizedEmail,FirstName,LastName,IsActive,EmailConfirmed,CreatedAt)
            VALUES(@CustomerUserId,@CustomerTenantId,N'customer-admin',N'CUSTOMER-ADMIN',CONCAT(@CustomerUserId,N'@customer.test'),UPPER(CONCAT(@CustomerUserId,N'@customer.test')),N'Admin',N'Cliente',1,1,SYSUTCDATETIME());
            INSERT dbo.AppRoles(RoleId,TenantId,Name,NormalizedName,Description,IsActive,IsSystemRole,CreatedAt)
            VALUES(@CustomerRoleId,@CustomerTenantId,N'Administrador cliente',N'CUSTOMER PLATFORM ATTEMPT',N'No puede recibir autoridad de plataforma',1,0,SYSUTCDATETIME());
            INSERT dbo.UserRoles(UserRoleId,UserId,RoleId,BusinessId,AssignedAt) VALUES(NEWID(),@CustomerUserId,@CustomerRoleId,NULL,SYSUTCDATETIME());
            INSERT dbo.RolePermissions(RolePermissionId,RoleId,PermissionId,AssignedAt)
            SELECT NEWID(),@CustomerRoleId,PermissionId,SYSUTCDATETIME() FROM dbo.Permissions
            WHERE Resource IN(N'roles.assign_permissions',N'platform.permissions.assign',N'tenants.read');
            """;
        command.Parameters.AddWithValue("@AuralyTenantId", AuralyTenantId);
        command.Parameters.AddWithValue("@RootUserId", rootUserId);
        command.Parameters.AddWithValue("@CustomerTenantId", customerTenantId);
        command.Parameters.AddWithValue("@CustomerUserId", customerUserId);
        command.Parameters.AddWithValue("@CustomerRoleId", customerRoleId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<IReadOnlyList<PermissionDto>> GetPermissionsAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/api/v1/permissions");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<IReadOnlyList<PermissionDto>>() ?? throw new InvalidOperationException("Empty permission catalog.");
    }

    private static IReadOnlyList<Guid> PermissionIds(IReadOnlyList<PermissionDto> catalog, IEnumerable<string> resources)
    {
        var requested = resources.ToArray();
        var ids = catalog.Where(item => requested.Contains(item.Resource, StringComparer.Ordinal)).Select(item => item.PermissionId).ToArray();
        Assert.Equal(requested.Length, ids.Length);
        return ids;
    }

    private static async Task<RoleDto> CreateRoleAsync(HttpClient client, string name)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/roles", new { tenantId = AuralyTenantId, name = $"{name} {Guid.NewGuid():N}", description = "Regresión multitenant" });
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<RoleDto>() ?? throw new InvalidOperationException("Empty role response.");
    }

    private static async Task<UserDto> CreateUserAsync(HttpClient client, string prefix, Guid? roleId = null)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var roles = roleId.HasValue
            ? new[] { new { roleId = roleId.Value, businessId = (Guid?)null } }
            : [];
        using var response = await client.PostAsJsonAsync("/api/v1/users", new { username = $"{prefix}-{suffix}", email = $"{prefix}-{suffix}@auraly.test", password = "Auraly-Test-2026!", firstName = "Prueba", lastName = prefix, phoneNumber = (string?)null, roles });
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<UserDto>() ?? throw new InvalidOperationException("Empty user response.");
    }

    private static async Task AssignRoleAsync(HttpClient client, Guid userId, Guid roleId)
    {
        using var response = await client.PostAsJsonAsync($"/api/v1/users/{userId:D}/roles", new { roleId, businessId = (Guid?)null });
        response.EnsureSuccessStatusCode();
    }

    private static async Task AssignPermissionsAsync(HttpClient client, Guid roleId, IReadOnlyList<Guid> permissionIds, HttpStatusCode expected)
    {
        using var response = await client.PostAsJsonAsync($"/api/v1/roles/{roleId:D}/permissions", new { permissionIds });
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"Expected {expected}, got {response.StatusCode}: {body}");
    }
}
