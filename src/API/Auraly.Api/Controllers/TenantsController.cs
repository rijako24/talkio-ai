using Auraly.Api.Authorization;
using Auraly.Api.Extensions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Auraly.Platform.Application.Common.DTOs;
using Auraly.Platform.Application.Common.Exceptions;
using Auraly.Contracts.Tenants;
using Auraly.Contracts.TenantBilling;
using Auraly.Platform.Application.Identity.DTOs;
using Auraly.Platform.Application.Identity.Interfaces;
using Auraly.Platform.Application.Identity.Services;

namespace Auraly.Api.Controllers;

[ApiController]
[Route("api/v1/tenants")]
[Authorize]
public sealed class TenantsController(
    ITenantService tenantService,
    ITenantDeviceAdminStore deviceAdmin,
    ITenantCommercialQuoteService commercialQuotes,
    IPlatformBillingPolicyStore billingPolicy,
    ITenantCommercialSubscriptionStore commercialSubscriptions,
    TenantRenewalOrderService tenantRenewalOrders,
    TenantSubscriptionCheckoutService subscriptionCheckout,
    Auraly.Application.Tenants.TenantInvitationService tenantInvitations) : ControllerBase
{
    private const long MaxLogoBytes = 4 * 1024 * 1024;
    private const long MaxLogoRequestBytes = MaxLogoBytes + 64 * 1024;

    [HttpGet]
    [PermissionAuthorize("tenants.read")]
    public async Task<ActionResult<PagedResponse<TenantDto>>> GetAll([FromQuery] PagedRequest request, CancellationToken ct) => Ok(await tenantService.GetPagedAsync(request, ct));

    [HttpGet("fiscal-certificate-expiry-alerts")]
    [PermissionAuthorize("platform.fiscal_certificates.expiry.read")]
    public async Task<ActionResult<IReadOnlyList<FiscalCertificateExpiryAlertDto>>> FiscalCertificateExpiryAlerts(
        CancellationToken ct) => Ok(await tenantService.GetFiscalCertificateExpiryAlertsAsync(
            User.GetTenantId(), ct));

    [HttpGet("{tenantId:guid}")]
    [PermissionAuthorize("tenants.read")]
    public async Task<ActionResult<TenantDto>> GetById(Guid tenantId, CancellationToken ct) => Ok(await tenantService.GetByIdAsync(tenantId, ct));

    [HttpGet("branding")]
    public async Task<ActionResult<TenantBrandingDto>> GetBranding(CancellationToken ct) =>
        Ok(await tenantService.GetBrandingAsync(User.GetTenantId(), ct));

    [HttpPost]
    [PermissionAuthorize("tenants.create")]
    public async Task<ActionResult<ProvisionTenantResult>> Create(
        [FromBody] WaivedTenantProvisioningRequest request,
        CancellationToken ct)
    {
        EnsurePermission("tenants.provisioning.payment.waive");
        var quote = await commercialQuotes.QuoteAsync(request.Quote, ct);
        var tenant = request.Tenant with
        {
            MaximumUsers = checked(quote.FullUserLimit + quote.SellerUserLimit),
            MaximumEnrolledDevices = quote.PosDeviceLimit
        };
        var result = await tenantService.ProvisionAsync(tenant, User.GetUserId(), quote, ct);
        return CreatedAtAction(nameof(GetById), new { tenantId = result.TenantId }, result);
    }

    [HttpPut("{tenantId:guid}")]
    public async Task<ActionResult<TenantDto>> Update(Guid tenantId, [FromBody] UpdateTenantRequest request, CancellationToken ct)
    {
        if (request.Name is not null || request.Email is not null || request.LegalName is not null
            || request.Nit is not null || request.VerificationDigit is not null
            || request.EntityType is not null || request.IdentificationTypeCode is not null
            || request.InventoryCostBasis is not null || request.AllowPromotionChannelCombination is not null)
            EnsurePermission("tenants.update");
        if (request.MaximumUsers.HasValue || request.MaximumEnrolledDevices.HasValue) EnsurePermission("tenants.capacity.update");
        return Ok(await tenantService.UpdateAsync(tenantId, request.Name, request.Email,
            request.MaximumUsers, request.MaximumEnrolledDevices, request.LegalName, request.Nit,
            request.VerificationDigit, request.EntityType, request.IdentificationTypeCode,
            request.InventoryCostBasis, request.AllowPromotionChannelCombination, ct));
    }

    [HttpPost("{tenantId:guid}/logo")]
    [PermissionAuthorize("tenants.update")]
    [RequestSizeLimit(MaxLogoRequestBytes)]
    public async Task<ActionResult<TenantDto>> UploadLogo(Guid tenantId, IFormFile file,
        CancellationToken ct)
    {
        if (file.Length is <= 0 or > MaxLogoBytes
            || !file.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = "El logo debe ser una imagen JPG, PNG o WEBP de máximo 4 MB." });
        await using var stream = file.OpenReadStream();
        return Ok(await tenantService.UploadLogoAsync(tenantId, stream, file.FileName, ct));
    }

    [HttpGet("{tenantId:guid}/devices")]
    [PermissionAuthorize("tenants.devices.read")]
    public async Task<ActionResult<IReadOnlyList<TenantEnrolledDeviceDto>>> GetDevices(Guid tenantId, CancellationToken ct) => Ok(await deviceAdmin.ListAsync(tenantId, ct));

    [HttpDelete("{tenantId:guid}/devices/{deviceId:guid}")]
    [PermissionAuthorize("tenants.devices.revoke")]
    public async Task<IActionResult> DeactivateDevice(Guid tenantId, Guid deviceId, CancellationToken ct)
    {
        await deviceAdmin.DeactivateAsync(tenantId, deviceId, ct);
        return NoContent();
    }

    [HttpPost("{tenantId:guid}/activate")]
    [PermissionAuthorize("tenants.status.update")]
    public async Task<IActionResult> Activate(Guid tenantId, CancellationToken ct)
    {
        await tenantService.ActivateAsync(tenantId, ct);
        return NoContent();
    }

    [HttpPost("{tenantId:guid}/administrator-invitation/resend")]
    [PermissionAuthorize("users.create")]
    public async Task<ActionResult<ResendTenantInvitationResult>> ResendAdministratorInvitation(
        Guid tenantId,
        CancellationToken ct)
    {
        if (tenantId != User.GetTenantId())
            throw new ForbiddenException(
                "Selecciona la organización antes de reenviar esta invitación.");
        try
        {
            return Ok(await tenantInvitations.ResendAsync(
                tenantId, User.GetUserId(), ct));
        }
        catch (Auraly.Application.Tenants.TenantInvitationException exception)
        {
            return Conflict(new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                Title = exception.Code,
                Detail = exception.Message
            });
        }
    }

    [HttpDelete("{tenantId:guid}")]
    [PermissionAuthorize("tenants.status.update")]
    public async Task<IActionResult> Deactivate(Guid tenantId, CancellationToken ct)
    {
        await tenantService.DeactivateAsync(tenantId, ct);
        return NoContent();
    }

    [HttpGet("billing-policy")]
    [PermissionAuthorize("tenants.billing.policy.manage")]
    public async Task<ActionResult<PlatformBillingPolicyDto>> GetBillingPolicy(CancellationToken ct) =>
        Ok(await billingPolicy.GetAsync(ct));

    [HttpPut("billing-policy")]
    [PermissionAuthorize("tenants.billing.policy.manage")]
    public async Task<ActionResult<PlatformBillingPolicyDto>> UpdateBillingPolicy(
        UpdatePlatformBillingPolicyRequest request, CancellationToken ct) =>
        Ok(await billingPolicy.UpdateAsync(User.GetTenantId(), User.GetUserId(), request, ct));

    [HttpGet("{tenantId:guid}/subscription")]
    [PermissionAuthorize("tenants.read")]
    public async Task<ActionResult<TenantCommercialSubscriptionDto?>> GetSubscription(
        Guid tenantId, CancellationToken ct) =>
        Ok(await commercialSubscriptions.GetAsync(tenantId, ct));

    [HttpGet("subscriptions")]
    [PermissionAuthorize("tenants.read")]
    public async Task<ActionResult<PlatformTenantSubscriptionPageDto>> GetSubscriptions(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? search = null,
        [FromQuery] string? status = null,
        CancellationToken ct = default) =>
        Ok(await commercialSubscriptions.ListPlatformAsync(
            Math.Max(1, page), Math.Clamp(pageSize, 1, 100), search, status, ct));

    [HttpGet("{tenantId:guid}/subscription/renewal-order")]
    [PermissionAuthorize("tenants.read")]
    public async Task<ActionResult<TenantRenewalOrderDto?>> GetRenewalOrder(
        Guid tenantId, CancellationToken ct) =>
        Ok(await tenantRenewalOrders.GetCurrentAsync(tenantId, ct));

    [HttpPost("{tenantId:guid}/subscription/renewal-orders/{renewalOrderId:guid}/record-payment")]
    [PermissionAuthorize("tenants.billing.payment.confirm_manual")]
    public async Task<ActionResult<TenantSubscriptionReceiptDto>> RecordSubscriptionPayment(
        Guid tenantId,
        Guid renewalOrderId,
        RecordTenantSubscriptionPaymentRequest request,
        CancellationToken ct) =>
        Ok(await subscriptionCheckout.RecordManualPaymentAsync(
            tenantId, User.GetUserId(), renewalOrderId, request, ct));

    private void EnsurePermission(string permission)
    {
        if (!User.HasPermission(permission)) throw new ForbiddenException($"Falta el permiso '{permission}'.");
    }
}
