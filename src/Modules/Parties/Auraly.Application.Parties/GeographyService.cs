using Auraly.BuildingBlocks.Domain.Identifiers;
using Auraly.Contracts.Parties;
using Auraly.Domain.Parties;

namespace Auraly.Application.Parties;

public sealed class GeographyService(IPartyStore store, IAuralyIdGenerator ids, TimeProvider time)
{
    public Task<IReadOnlyCollection<CountryItem>> CountriesAsync(
        PartyActorIdentity actor, bool includeInactive, CancellationToken ct)
    {
        PartyService.RequireUserOrEnrolledDevice(actor, PartyPermissionCodes.GeographyRead);
        return store.CountriesAsync(includeInactive, ct);
    }

    public Task<IReadOnlyCollection<AdministrativeDivisionItem>> DivisionsAsync(
        PartyActorIdentity actor, Guid countryId, bool includeInactive, CancellationToken ct)
    {
        PartyService.RequireUserOrEnrolledDevice(actor, PartyPermissionCodes.GeographyRead);
        return store.DivisionsAsync(countryId, includeInactive, ct);
    }

    public Task<IReadOnlyCollection<CityItem>> CitiesAsync(
        PartyActorIdentity actor, Guid divisionId, bool includeInactive, CancellationToken ct)
    {
        PartyService.RequireUserOrEnrolledDevice(actor, PartyPermissionCodes.GeographyRead);
        return store.CitiesAsync(divisionId, includeInactive, ct);
    }

    public Task<IReadOnlyCollection<GeographyHierarchyItem>> HierarchyAsync(
        PartyActorIdentity actor, bool includeInactive, CancellationToken ct)
    {
        PartyService.RequireUserOrEnrolledDevice(actor, PartyPermissionCodes.GeographyRead);
        return store.GeographyHierarchyAsync(includeInactive, ct);
    }

    public Task<CountryItem> CreateCountryAsync(
        PartyActorIdentity actor, SaveCountryRequest request, CancellationToken ct)
    {
        PartyService.Require(actor, PartyPermissionCodes.GeographyManage);
        ValidateCountry(request.Code, request.Name);
        return store.CreateCountryAsync(actor, ids.NewId(), request, time.GetUtcNow(), ct);
    }

    public Task<AdministrativeDivisionItem> CreateDivisionAsync(
        PartyActorIdentity actor, SaveAdministrativeDivisionRequest request, CancellationToken ct)
    {
        PartyService.Require(actor, PartyPermissionCodes.GeographyManage);
        Validate(request.Code, request.Name);
        if (request.CountryId == Guid.Empty) throw new PartyValidationException("Country is required.");
        return store.CreateDivisionAsync(actor, ids.NewId(), request, time.GetUtcNow(), ct);
    }

    public Task<CityItem> CreateCityAsync(
        PartyActorIdentity actor, SaveCityRequest request, CancellationToken ct)
    {
        PartyService.Require(actor, PartyPermissionCodes.GeographyManage);
        Validate(request.Code, request.Name);
        if (request.AdministrativeDivisionId == Guid.Empty)
            throw new PartyValidationException("Administrative division is required.");
        return store.CreateCityAsync(actor, ids.NewId(), request, time.GetUtcNow(), ct);
    }

    public Task<CountryItem> UpdateCountryAsync(PartyActorIdentity actor, Guid id, SaveCountryRequest request, CancellationToken ct)
    {
        PartyService.Require(actor, PartyPermissionCodes.GeographyManage);
        ValidateCountry(request.Code, request.Name);
        return store.UpdateCountryAsync(actor, id, request, time.GetUtcNow(), ct);
    }

    public Task<AdministrativeDivisionItem> UpdateDivisionAsync(PartyActorIdentity actor, Guid id, SaveAdministrativeDivisionRequest request, CancellationToken ct)
    {
        PartyService.Require(actor, PartyPermissionCodes.GeographyManage);
        Validate(request.Code, request.Name);
        if (request.CountryId == Guid.Empty) throw new PartyValidationException("Country is required.");
        return store.UpdateDivisionAsync(actor, id, request, time.GetUtcNow(), ct);
    }

    public Task<CityItem> UpdateCityAsync(PartyActorIdentity actor, Guid id, SaveCityRequest request, CancellationToken ct)
    {
        PartyService.Require(actor, PartyPermissionCodes.GeographyManage);
        Validate(request.Code, request.Name);
        if (request.AdministrativeDivisionId == Guid.Empty) throw new PartyValidationException("Administrative division is required.");
        return store.UpdateCityAsync(actor, id, request, time.GetUtcNow(), ct);
    }

    private static void ValidateCountry(string code, string name)
    {
        var normalizedCode = code?.Trim().ToUpperInvariant() ?? string.Empty;
        if (normalizedCode.Length != 2 || normalizedCode.Any(character => character is < 'A' or > 'Z'))
            throw new PartyValidationException("Country code must use the two-letter ISO format, for example CO or VE.");

        Validate(normalizedCode, name);
    }

    private static void Validate(string code, string name)
    {
        try
        {
            PartyValidation.NormalizeCode(code, "Code", 16);
            PartyValidation.RequireText(name, "Name", 120);
        }
        catch (ArgumentException exception)
        {
            throw new PartyValidationException(exception.Message);
        }
    }
}
