namespace Auraly.Platform.Application.Identity;

public static class PlatformPermissions
{
    public const string PlatformTenantKey = "@auraly";
    public const string Assign = "platform.permissions.assign";
    public const string TenantsRead = "tenants.read";
    public const string TenantsCreate = "tenants.create";
    public const string TenantsUpdate = "tenants.update";
    public const string TenantCapacityUpdate = "tenants.capacity.update";
    public const string TenantStatusUpdate = "tenants.status.update";
    public const string TenantDevicesRead = "tenants.devices.read";
    public const string TenantDevicesRevoke = "tenants.devices.revoke";
    public const string TenantBillingPolicyManage = "tenants.billing.policy.manage";
    public const string TenantBillingPaymentConfirmManual = "tenants.billing.payment.confirm_manual";
    public const string FiscalCertificateExpiryRead = "platform.fiscal_certificates.expiry.read";

    public static bool IsNonDelegable(string resource) =>
        string.Equals(resource, TenantBillingPolicyManage, StringComparison.Ordinal)
        || string.Equals(resource, TenantBillingPaymentConfirmManual, StringComparison.Ordinal);

    public static bool IsPlatformPermission(string resource) =>
        resource.StartsWith("tenants.", StringComparison.Ordinal)
        || resource.StartsWith("platform.", StringComparison.Ordinal);
}
