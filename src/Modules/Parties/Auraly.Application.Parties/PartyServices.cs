using Auraly.BuildingBlocks.Application.Synchronization;
using Auraly.BuildingBlocks.Domain.Identifiers;
using Auraly.Contracts.Parties;
using Auraly.Domain.Parties;

namespace Auraly.Application.Parties;

public interface IPartyStore
{
    Task<CustomerDetail> CreateCustomerAsync(
        PartyActorIdentity actor, Guid partyId, Guid customerId, Guid siteId,
        CreateCustomerRequest request, string normalizedIdentification,
        DateTimeOffset now, CancellationToken ct);
    Task<CustomerDetail?> FindCustomerAsync(
        Guid tenantId, Guid businessId, Guid countryId, string identificationType,
        string normalizedIdentification, CancellationToken ct);
    Task<CustomerDetail?> GetCustomerAsync(Guid tenantId, Guid businessId, Guid customerId, CancellationToken ct);
    Task<CustomerPage> PageCustomersAsync(
        Guid tenantId, Guid businessId, int page, CustomerPageRequest request, CancellationToken ct);
    Task<PartySiteDetail> AddSiteAsync(
        PartyActorIdentity actor, Guid customerId, Guid siteId,
        AddPartySiteRequest request, DateTimeOffset now, CancellationToken ct);
    Task<PartySiteDetail> UpdateSiteAsync(
        PartyActorIdentity actor, Guid customerId, Guid siteId,
        UpdatePartySiteRequest request, DateTimeOffset now, CancellationToken ct);
    Task<PartyUserAccountLink?> GetUserAccountAsync(
        Guid tenantId, Guid partyId, CancellationToken ct);
    Task<PartyUserAccountLink> LinkUserAccountAsync(
        Guid tenantId, Guid partyId, Guid userId, Guid assignedByUserId, DateTimeOffset now, CancellationToken ct);
    Task UnlinkUserAccountAsync(Guid tenantId, Guid partyId, DateTimeOffset now, CancellationToken ct);
    Task<IReadOnlyCollection<CountryItem>> CountriesAsync(bool includeInactive, CancellationToken ct);
    Task<IReadOnlyCollection<AdministrativeDivisionItem>> DivisionsAsync(
        Guid countryId, bool includeInactive, CancellationToken ct);
    Task<IReadOnlyCollection<CityItem>> CitiesAsync(
        Guid divisionId, bool includeInactive, CancellationToken ct);
    Task<IReadOnlyCollection<GeographyHierarchyItem>> GeographyHierarchyAsync(
        bool includeInactive, CancellationToken ct);
    Task<CountryItem> CreateCountryAsync(
        PartyActorIdentity actor, Guid id, SaveCountryRequest request, DateTimeOffset now, CancellationToken ct);
    Task<AdministrativeDivisionItem> CreateDivisionAsync(
        PartyActorIdentity actor, Guid id, SaveAdministrativeDivisionRequest request,
        DateTimeOffset now, CancellationToken ct);
    Task<CityItem> CreateCityAsync(
        PartyActorIdentity actor, Guid id, SaveCityRequest request, DateTimeOffset now, CancellationToken ct);
    Task<CountryItem> UpdateCountryAsync(
        PartyActorIdentity actor, Guid id, SaveCountryRequest request, DateTimeOffset now, CancellationToken ct);
    Task<AdministrativeDivisionItem> UpdateDivisionAsync(
        PartyActorIdentity actor, Guid id, SaveAdministrativeDivisionRequest request, DateTimeOffset now, CancellationToken ct);
    Task<CityItem> UpdateCityAsync(
        PartyActorIdentity actor, Guid id, SaveCityRequest request, DateTimeOffset now, CancellationToken ct);
}

public sealed class PartyService(IPartyStore store, IAuralyIdGenerator ids, TimeProvider time, IPosSynchronizationOutboxDispatcher synchronization)
{
    public Task<CustomerDetail> CreateCustomerAsync(
        PartyActorIdentity actor, CreateCustomerRequest request, CancellationToken ct)
    {
        RequireUserOrEnrolledDevice(actor, PartyPermissionCodes.CustomerCreate);
        if (request.BusinessId != actor.BusinessId)
            throw new PartyForbiddenException("The customer business does not match the authenticated identity.");
        ValidateParty(request.Party);
        ValidateSite(request.PrimarySite);
        foreach (var site in request.AdditionalSites ?? []) ValidateSite(site);
        var duplicatedSiteCode = (request.AdditionalSites ?? [])
            .Prepend(request.PrimarySite)
            .GroupBy(site => site.Code.Trim(), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        if (duplicatedSiteCode is not null)
            throw new PartyValidationException($"Site code '{duplicatedSiteCode}' is repeated.");
        Translate(() =>
            _ = new CustomerPricingAssignment(request.Pricing?.PriceChannelId));
        if (request.Pricing is not null && !actor.IsDevice) Require(actor, PartyPermissionCodes.ManagePricing);
        if (actor.IsDevice && request.Pricing is { PriceChannelId: not null })
            throw new PartyForbiddenException("POS quick creation cannot assign commercial pricing.");
        var normalized = string.Empty;
        Translate(() =>
            normalized = PartyIdentityNormalizer.Normalize(
                request.Party.IdentificationTypeCode,
                request.Party.Identification));
        return CreateAndNotifyAsync(actor, request, normalized, ct);
    }

    private async Task<CustomerDetail> CreateAndNotifyAsync(
        PartyActorIdentity actor, CreateCustomerRequest request, string normalized, CancellationToken ct)
    {
        if (request.RequestedCustomerId == Guid.Empty)
            throw new PartyValidationException("RequestedCustomerId must be null or a valid identifier.");
        if (!actor.IsDevice && request.RequestedCustomerId is not null)
            throw new PartyForbiddenException("Only an enrolled POS device can reserve a customer identifier.");
        var customerId = actor.IsDevice && request.RequestedCustomerId is { } requestedCustomerId
            ? requestedCustomerId
            : ids.NewId();
        var customer = await store.CreateCustomerAsync(
            actor, ids.NewId(), customerId, ids.NewId(), request, normalized, time.GetUtcNow(), ct);
        await synchronization.DispatchPendingAsync(actor.TenantId, actor.BusinessId, CancellationToken.None);
        return customer;
    }
    public Task<CustomerDetail?> FindCustomerAsync(
        PartyActorIdentity actor, Guid countryId, string type, string identification, CancellationToken ct)
    {
        RequireUserOrEnrolledDevice(actor, PartyPermissionCodes.CustomerRead);
        var normalized = string.Empty;
        Translate(() =>
            normalized = PartyIdentityNormalizer.Normalize(type, identification));
        return store.FindCustomerAsync(
            actor.TenantId, actor.BusinessId, countryId, type.Trim().ToUpperInvariant(),
            normalized, ct);
    }

    public Task<CustomerDetail?> GetCustomerAsync(PartyActorIdentity actor, Guid customerId, CancellationToken ct)
    {
        Require(actor, PartyPermissionCodes.CustomerRead);
        return store.GetCustomerAsync(actor.TenantId, actor.BusinessId, customerId, ct);
    }

    public Task<CustomerPage> PageCustomersAsync(
        PartyActorIdentity actor, int page, CustomerPageRequest request, CancellationToken ct)
    {
        Require(actor, PartyPermissionCodes.CustomerRead);
        if (page < 1 || request.PageSize is < 1 or > 200)
            throw new PartyValidationException("Page and PageSize are outside the allowed range.");
        return store.PageCustomersAsync(actor.TenantId, actor.BusinessId, page, request, ct);
    }

    public Task<PartySiteDetail> AddSiteAsync(
        PartyActorIdentity actor, Guid customerId, AddPartySiteRequest request, CancellationToken ct)
    {
        RequireUserOrEnrolledDevice(actor, PartyPermissionCodes.ManageSites);
        ValidateSite(request.Site);
        return store.AddSiteAsync(actor, customerId, ids.NewId(), request, time.GetUtcNow(), ct);
    }

    public Task<PartySiteDetail> UpdateSiteAsync(
        PartyActorIdentity actor, Guid customerId, Guid siteId,
        UpdatePartySiteRequest request, CancellationToken ct)
    {
        Require(actor, PartyPermissionCodes.ManageSites);
        if (customerId == Guid.Empty || siteId == Guid.Empty || string.IsNullOrWhiteSpace(request.RowVersion))
            throw new PartyValidationException("Customer, site and row version are required.");
        ValidateSite(request.Site);
        return store.UpdateSiteAsync(actor, customerId, siteId, request, time.GetUtcNow(), ct);
    }

    public Task<PartyUserAccountLink?> GetUserAccountAsync(
        PartyActorIdentity actor, Guid partyId, CancellationToken ct)
    {
        Require(actor, PartyPermissionCodes.ManageUserAccounts);
        if (partyId == Guid.Empty) throw new PartyValidationException("PartyId is required.");
        return store.GetUserAccountAsync(actor.TenantId, partyId, ct);
    }

    public Task<PartyUserAccountLink> LinkUserAccountAsync(
        PartyActorIdentity actor, Guid partyId, LinkPartyUserAccountRequest request, CancellationToken ct)
    {
        Require(actor, PartyPermissionCodes.ManageUserAccounts);
        if (partyId == Guid.Empty || request.UserId == Guid.Empty)
            throw new PartyValidationException("PartyId and UserId are required.");
        return store.LinkUserAccountAsync(
            actor.TenantId, partyId, request.UserId, actor.ActorId, time.GetUtcNow(), ct);
    }

    public Task UnlinkUserAccountAsync(
        PartyActorIdentity actor, Guid partyId, CancellationToken ct)
    {
        Require(actor, PartyPermissionCodes.ManageUserAccounts);
        if (partyId == Guid.Empty) throw new PartyValidationException("PartyId is required.");
        return store.UnlinkUserAccountAsync(actor.TenantId, partyId, time.GetUtcNow(), ct);
    }

    private static void ValidateParty(PartyInput party)
    {
        if (party.IdentificationCountryId == Guid.Empty)
            throw new PartyValidationException("Identification country is required.");
        if (party.PartyType is not PartyTypes.NaturalPerson and not PartyTypes.Organization)
            throw new PartyValidationException("Party type is invalid.");
        Translate(() => PartyValidation.RequireText(party.DisplayName, "DisplayName", 200));
        if (party.PartyType == PartyTypes.Organization && string.IsNullOrWhiteSpace(party.LegalName))
            throw new PartyValidationException("Legal name is required for an organization.");
    }

    private static void ValidateSite(PartySiteInput site)
    {
        if (site.CountryId == Guid.Empty || site.AdministrativeDivisionId == Guid.Empty || site.CityId == Guid.Empty)
            throw new PartyValidationException("Country, administrative division and city are required.");
        Translate(() => PartyValidation.NormalizeCode(site.Code, "SiteCode", 32));
        Translate(() => PartyValidation.RequireText(site.Name, "SiteName", 160));
        Translate(() => PartyValidation.RequireText(site.AddressLine, "AddressLine", 300));
        if ((site.Latitude is null) != (site.Longitude is null))
            throw new PartyValidationException("Latitude and longitude must be provided together.");
        if (site.Latitude is < -90 or > 90 || site.Longitude is < -180 or > 180)
            throw new PartyValidationException("The site coordinates are outside the valid range.");
        if (site.GoogleMapsUrl?.Length > 1000 || site.GooglePlaceId?.Length > 255)
            throw new PartyValidationException("The Google Maps location is too long.");
        if (site.Neighborhood?.Length > 120)
            throw new PartyValidationException("Neighborhood cannot exceed 120 characters.");
    }

    private static void Translate(Action action)
    {
        try { action(); }
        catch (ArgumentException exception) { throw new PartyValidationException(exception.Message); }
    }

    internal static void Require(PartyActorIdentity actor, string permission)
    {
        if (!actor.Permissions.Contains(permission))
            throw new PartyForbiddenException($"Permission '{permission}' is required.");
    }

    internal static void RequireUserOrEnrolledDevice(
        PartyActorIdentity actor,
        string userPermission)
    {
        if (actor.IsDevice)
        {
            if (actor.ActorId == Guid.Empty || actor.TenantId == Guid.Empty ||
                actor.BusinessId == Guid.Empty)
                throw new PartyForbiddenException(
                    "The enrolled POS device context is incomplete.");
            return;
        }
        Require(actor, userPermission);
    }
}

public sealed class PartyForbiddenException(string message) : Exception(message);
public sealed class PartyValidationException(string message) : Exception(message);
public sealed class PartyConflictException(string message) : Exception(message);
