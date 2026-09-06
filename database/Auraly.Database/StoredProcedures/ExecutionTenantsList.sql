CREATE PROCEDURE [dbo].[ExecutionTenantsList] @UserId UNIQUEIDENTIFIER AS
BEGIN
 SET NOCOUNT ON;
 DECLARE @CanReadAll bit=CASE WHEN EXISTS(SELECT 1 FROM dbo.AppUsers u JOIN dbo.Tenants identityTenant ON identityTenant.TenantId=u.TenantId AND identityTenant.TenantKey=N'@auraly' JOIN dbo.UserRoles ur ON ur.UserId=u.UserId JOIN dbo.AppRoles r ON r.RoleId=ur.RoleId AND r.IsActive=1 JOIN dbo.RolePermissions rp ON rp.RoleId=r.RoleId JOIN dbo.Permissions p ON p.PermissionId=rp.PermissionId WHERE u.UserId=@UserId AND p.Resource=N'tenants.read') THEN 1 ELSE 0 END;
 SELECT DISTINCT t.TenantId,t.Name FROM dbo.Tenants t WHERE t.IsActive=1 AND(@CanReadAll=1 OR EXISTS(SELECT 1 FROM dbo.UserRoles ur JOIN dbo.AppRoles r ON r.RoleId=ur.RoleId AND r.IsActive=1 LEFT JOIN dbo.Businesses b ON b.BusinessId=ur.BusinessId WHERE ur.UserId=@UserId AND(r.TenantId=t.TenantId OR b.TenantId=t.TenantId))) ORDER BY t.Name,t.TenantId;
END
