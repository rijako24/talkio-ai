CREATE PROCEDURE [dbo].[ExecutionBusinessesList] @UserId UNIQUEIDENTIFIER,@TenantId UNIQUEIDENTIFIER AS
BEGIN
 SET NOCOUNT ON;
 DECLARE @CanReadAll bit=CASE WHEN EXISTS(SELECT 1 FROM dbo.AppUsers u JOIN dbo.Tenants identityTenant ON identityTenant.TenantId=u.TenantId AND identityTenant.TenantKey=N'@auraly' JOIN dbo.UserRoles ur ON ur.UserId=u.UserId JOIN dbo.AppRoles r ON r.RoleId=ur.RoleId AND r.IsActive=1 JOIN dbo.RolePermissions rp ON rp.RoleId=r.RoleId JOIN dbo.Permissions p ON p.PermissionId=rp.PermissionId WHERE u.UserId=@UserId AND p.Resource=N'tenants.read') THEN 1 ELSE 0 END;
 SELECT b.BusinessId,b.TenantId,b.Name FROM dbo.Businesses b WHERE b.TenantId=@TenantId AND b.IsActive=1 AND(@CanReadAll=1 OR EXISTS(SELECT 1 FROM dbo.UserRoles ur JOIN dbo.AppRoles r ON r.RoleId=ur.RoleId AND r.IsActive=1 WHERE ur.UserId=@UserId AND r.TenantId=@TenantId AND(ur.BusinessId IS NULL OR ur.BusinessId=b.BusinessId))) ORDER BY b.Name,b.BusinessId;
END
