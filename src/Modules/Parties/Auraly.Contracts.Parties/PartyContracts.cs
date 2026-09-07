namespace Auraly.Contracts.Parties;

public static class PartyPermissionCodes
{
    public const string Read = "parties.read";
    public const string Create = "parties.create";
    public const string ManageSites = "parties.sites.manage";
    public const string CustomerRead = "customers.read";
    public const string CustomerCreate = "customers.create";
    public const string ManagePricing = "customers.pricing.manage";
    public const string GeographyRead = "masters.geography.read";
    public const string GeographyManage = "masters.geography.manage";
    public const string PosCustomerCreate = "pos.customer.create";
    public const string ManageUserAccounts = "security.users.link-party";
}

public static class PartyTypes
{
    public const string NaturalPerson = "NaturalPerson";
    public const string Organization = "Organization";
}

public sealed record PartyActorIdentity(
    Guid ActorId,
    Guid TenantId,
    Guid BusinessId,
    IReadOnlySet<string> Permissions,
    bool IsDevice = false);

public sealed record PartyInput(
    string PartyType,
    Guid IdentificationCountryId,
    string IdentificationTypeCode,
    string Identification,
    string? VerificationDigit,
    string DisplayName,
    string? LegalName,
    string? FirstName,
    string? LastName,
    string? Email,
    string? Phone);

public sealed record PartySiteInput(
    string Code,
    string Name,
    Guid CountryId,
    Guid AdministrativeDivisionId,
    Guid CityId,
    string AddressLine,
    string? Neighborhood,
    string? PostalCode,
    string? Email,
    string? Phone,
    bool IsPrimary = true,
    string? GoogleMapsUrl = null,
    string? GooglePlaceId = null,
    decimal? Latitude = null,
    decimal? Longitude = null);

public sealed record CustomerPricingInput(
    Guid? PriceChannelId,
    DateTimeOffset? ValidFrom = null,
    DateTimeOffset? ValidUntil = null);

public sealed record CreateCustomerRequest(
    Guid OperationId,
    Guid BusinessId,
    PartyInput Party,
    PartySiteInput PrimarySite,
    CustomerPricingInput? Pricing,
    bool RequiresElectronicInvoice = false,
    IReadOnlyCollection<PartySiteInput>? AdditionalSites = null,
    Guid? RequestedCustomerId = null);

public sealed record AddPartySiteRequest(Guid OperationId, PartySiteInput Site);
public sealed record UpdatePartySiteRequest(PartySiteInput Site, string RowVersion);
public sealed record LinkPartyUserAccountRequest(Guid UserId);
public sealed record PartyUserAccountLink(
    Guid UserId, Guid PartyId, string Username, string Email, bool IsActive);


public sealed record PartySiteDetail(
    Guid PartySiteId,
    string Code,
    string Name,
    Guid CountryId,
    string CountryCode,
    string CountryName,
    Guid AdministrativeDivisionId,
    string AdministrativeDivisionCode,
    string AdministrativeDivisionName,
    Guid CityId,
    string CityCode,
    string CityName,
    string AddressLine,
    string? Neighborhood,
    string? PostalCode,
    string? Email,
    string? Phone,
    bool IsPrimary,
    bool IsActive,
    string? GoogleMapsUrl = null,
    string? GooglePlaceId = null,
    decimal? Latitude = null,
    decimal? Longitude = null,
    string RowVersion = "");

public sealed record CustomerDetail(
    Guid CustomerId,
    Guid PartyId,
    Guid BusinessId,
    string PartyType,
    string? IdentificationTypeCode,
    string? Identification,
    string? NormalizedIdentification,
    string? VerificationDigit,
    string? DisplayName,
    string? LegalName,
    string? FirstName,
    string? LastName,
    string? Email,
    string? Phone,
    Guid? PriceChannelId,
    bool IsActive,
    IReadOnlyCollection<PartySiteDetail> Sites,
    bool RequiresElectronicInvoice = false);

public sealed record CustomerPageRequest(int PageSize = 50, string? Search = null, bool? IsActive = null);
public sealed record CustomerPage(IReadOnlyCollection<CustomerDetail> Items, int Page, int PageSize, int TotalCount);

public sealed record CountryItem(Guid CountryId, string Code, string Name, bool IsActive);
public sealed record AdministrativeDivisionItem(
    Guid AdministrativeDivisionId, Guid CountryId, string Code, string Name, string DivisionType, bool IsActive);
public sealed record CityItem(
    Guid CityId, Guid AdministrativeDivisionId, string Code, string Name, bool IsActive);
public sealed record GeographyHierarchyItem(
    Guid Id, Guid? ParentId, string Level, string Code, string Name, bool IsActive);

public sealed record SaveCountryRequest(string Code, string Name, bool IsActive = true);
public sealed record SaveAdministrativeDivisionRequest(
    Guid CountryId, string Code, string Name, string DivisionType = "Department", bool IsActive = true);
public sealed record SaveCityRequest(
    Guid AdministrativeDivisionId, string Code, string Name, bool IsActive = true);
