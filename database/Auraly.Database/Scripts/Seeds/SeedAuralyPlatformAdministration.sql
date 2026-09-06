-- Canonical platform identity and authorization boundary for tenant @auraly.
SET NOCOUNT ON;

DECLARE @AuralyTenantId UNIQUEIDENTIFIER = 'A0A10000-0000-0000-0000-000000000000';
DECLARE @PlatformRoleId UNIQUEIDENTIFIER;

IF NOT EXISTS (SELECT 1 FROM dbo.Tenants WHERE TenantId=@AuralyTenantId AND TenantKey=N'@auraly')
    THROW 51000, 'SeedAuralyPlatformAdministration requiere el tenant canonico @auraly.', 1;

DECLARE @PlatformPermissions TABLE(Module NVARCHAR(50), Action NVARCHAR(50), Resource NVARCHAR(100), Description NVARCHAR(500));
INSERT INTO @PlatformPermissions VALUES
(N'Tenants',N'Read',N'tenants.read',N'Ver empresas'),
(N'Tenants',N'Create',N'tenants.create',N'Crear empresas'),
(N'Tenants',N'WaiveProvisioningPayment',N'tenants.provisioning.payment.waive',N'Omitir el pago inicial al aprovisionar una empresa'),
(N'Tenants',N'Update',N'tenants.update',N'Actualizar datos de empresas'),
(N'Tenants',N'UpdateCapacity',N'tenants.capacity.update',N'Modificar cupos de usuarios y cajas'),
(N'Tenants',N'UpdateStatus',N'tenants.status.update',N'Activar o inactivar empresas'),
(N'Tenants',N'ReadDevices',N'tenants.devices.read',N'Consultar cajas enroladas de otras empresas'),
(N'Tenants',N'RevokeDevices',N'tenants.devices.revoke',N'Desenrolar cajas de otras empresas'),
(N'Tenants',N'ManageBillingPolicy',N'tenants.billing.policy.manage',N'Configurar la política global de cobranza'),
(N'Tenants',N'ConfirmManualBillingPayment',N'tenants.billing.payment.confirm_manual',N'Confirmar recaudos externos de suscripciones'),
(N'Platform',N'AssignPermissions',N'platform.permissions.assign',N'Delegar permisos de plataforma'),
(N'Platform',N'ReadFiscalCertificateExpiry',N'platform.fiscal_certificates.expiry.read',N'Ver alertas de vencimiento de certificados DIAN');

INSERT dbo.Permissions(PermissionId,Module,Action,Resource,Description,CreatedAt)
SELECT NEWID(),source.Module,source.Action,source.Resource,source.Description,SYSUTCDATETIME()
FROM @PlatformPermissions source
WHERE NOT EXISTS(SELECT 1 FROM dbo.Permissions existing WHERE existing.Resource=source.Resource);

UPDATE existing
SET Module=source.Module,Action=source.Action,Description=source.Description
FROM dbo.Permissions existing
JOIN @PlatformPermissions source ON source.Resource=existing.Resource;

-- Los permisos de la vista Usuarios se reutilizan en el tenant seleccionado.
-- Retira la antigua segunda llave específica para usuarios de otros tenants.
DELETE assignment
FROM dbo.RolePermissions assignment
JOIN dbo.Permissions permissionValue ON permissionValue.PermissionId=assignment.PermissionId
WHERE permissionValue.Resource IN(N'tenants.users.read',N'tenants.users.manage');

DELETE FROM dbo.Permissions
WHERE Resource IN(N'tenants.users.read',N'tenants.users.manage');

-- Elimina autoridad de plataforma heredada por roles de clientes.
DELETE assignment
FROM dbo.RolePermissions assignment
JOIN dbo.AppRoles roleValue ON roleValue.RoleId=assignment.RoleId
JOIN dbo.Permissions permissionValue ON permissionValue.PermissionId=assignment.PermissionId
LEFT JOIN dbo.Tenants tenantValue ON tenantValue.TenantId=roleValue.TenantId
WHERE (permissionValue.Resource LIKE N'tenants.%' OR permissionValue.Resource LIKE N'platform.%')
  AND ISNULL(tenantValue.TenantKey,N'')<>N'@auraly';

SELECT @PlatformRoleId=RoleId
FROM dbo.AppRoles
WHERE TenantId=@AuralyTenantId AND NormalizedName=N'ADMINISTRATOR';

IF @PlatformRoleId IS NULL
BEGIN
    SET @PlatformRoleId=NEWID();
    INSERT dbo.AppRoles(RoleId,TenantId,Name,NormalizedName,Description,IsActive,IsSystemRole,CreatedAt)
    VALUES(@PlatformRoleId,@AuralyTenantId,N'Administrador de plataforma',N'ADMINISTRATOR',N'Administración integral y delegable de la plataforma Auraly.',1,1,SYSUTCDATETIME());
END
ELSE
BEGIN
    UPDATE dbo.AppRoles
    SET Name=N'Administrador de plataforma',Description=N'Administración integral y delegable de la plataforma Auraly.',IsActive=1,IsSystemRole=1,UpdatedAt=SYSUTCDATETIME()
    WHERE RoleId=@PlatformRoleId;
END;

-- El rol raíz recibe el catálogo completo. La única cuenta administradora inicial
-- es la persona que acepta la invitación del aprovisionamiento.
INSERT dbo.RolePermissions(RolePermissionId,RoleId,PermissionId,AssignedAt)
SELECT NEWID(),@PlatformRoleId,permissionValue.PermissionId,SYSUTCDATETIME()
FROM dbo.Permissions permissionValue
WHERE NOT EXISTS(SELECT 1 FROM dbo.RolePermissions existing WHERE existing.RoleId=@PlatformRoleId AND existing.PermissionId=permissionValue.PermissionId);

-- Retira sin destruir auditoría las identidades técnicas obsoletas. Nunca debe
-- coincidir con una cuenta creada por una invitación real.
DECLARE @RetiredUsers TABLE(UserId UNIQUEIDENTIFIER PRIMARY KEY);
INSERT INTO @RetiredUsers(UserId)
SELECT UserId
FROM dbo.AppUsers
WHERE NormalizedUsername=N'ADMIN2222'
   OR (TenantId=@AuralyTenantId
       AND NormalizedUsername=N'ADMIN'
       AND NormalizedEmail=N'ADMIN@AURALY.AI');

UPDATE sessionValue
SET Status=N'Revoked',RevokedAt=SYSUTCDATETIME(),RevocationReason=N'IdentityRetired',UpdatedAt=SYSUTCDATETIME()
FROM dbo.AuthenticationSessions sessionValue
JOIN @RetiredUsers retired ON retired.UserId=sessionValue.UserId
WHERE sessionValue.Status=N'Active';

UPDATE tokenValue
SET RevokedAt=GETUTCDATE()
FROM dbo.RefreshTokens tokenValue
JOIN @RetiredUsers retired ON retired.UserId=tokenValue.UserId
WHERE tokenValue.RevokedAt IS NULL;

DELETE assignment
FROM dbo.UserRoles assignment
JOIN @RetiredUsers retired ON retired.UserId=assignment.UserId;

UPDATE userValue
SET Username=CONCAT(N'retired-',LEFT(REPLACE(CONVERT(NVARCHAR(36),userValue.UserId),N'-',N''),12)),
    NormalizedUsername=UPPER(CONCAT(N'retired-',LEFT(REPLACE(CONVERT(NVARCHAR(36),userValue.UserId),N'-',N''),12))),
    Email=CONCAT(N'retired+',REPLACE(CONVERT(NVARCHAR(36),userValue.UserId),N'-',N''),N'@invalid.auraly.local'),
    NormalizedEmail=UPPER(CONCAT(N'retired+',REPLACE(CONVERT(NVARCHAR(36),userValue.UserId),N'-',N''),N'@invalid.auraly.local')),
    IsActive=0,AccessFailedCount=0,LockoutEnd=NULL,UpdatedAt=SYSUTCDATETIME()
FROM dbo.AppUsers userValue
JOIN @RetiredUsers retired ON retired.UserId=userValue.UserId;
PRINT N'SeedAuralyPlatformAdministration: rol y permisos de plataforma listos; identidades técnicas retiradas.';
GO
