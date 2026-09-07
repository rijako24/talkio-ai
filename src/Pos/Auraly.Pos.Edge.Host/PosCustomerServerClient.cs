using System.Net.Http.Json;
using Auraly.Contracts.Catalog;
using Auraly.Contracts.Parties;
using Auraly.Pos.Edge.Infrastructure;

namespace Auraly.Pos.Edge.Host;

public sealed record PosCreateCustomerInput(
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
    string? Phone,
    PartySiteInput PrimarySite);

public sealed class PosCustomerServerClient(
    HttpClient http,
    PosDeviceCredentials credentials,
    PosOperationalScope scope,
    PosCustomerGeographyStore geography)
{
    public async Task<IReadOnlyCollection<CountryItem>> CountriesAsync(CancellationToken ct)
    {
        try
        {
            var values = await GetAsync<IReadOnlyCollection<CountryItem>>(
                $"/api/pos/v1/customers/geography/countries?businessId={scope.BusinessId:D}", ct);
            return values;
        }
        catch (HttpRequestException)
        {
            return await geography.CountriesAsync(ct);
        }
    }

    public async Task<IReadOnlyCollection<AdministrativeDivisionItem>> DivisionsAsync(Guid countryId, CancellationToken ct)
    {
        try
        {
            return await GetAsync<IReadOnlyCollection<AdministrativeDivisionItem>>(
                $"/api/pos/v1/customers/geography/countries/{countryId:D}/divisions?businessId={scope.BusinessId:D}", ct);
        }
        catch (HttpRequestException)
        {
            return await geography.DivisionsAsync(countryId, ct);
        }
    }

    public async Task<IReadOnlyCollection<CityItem>> CitiesAsync(Guid divisionId, CancellationToken ct)
    {
        try
        {
            return await GetAsync<IReadOnlyCollection<CityItem>>(
                $"/api/pos/v1/customers/geography/divisions/{divisionId:D}/cities?businessId={scope.BusinessId:D}", ct);
        }
        catch (HttpRequestException)
        {
            return await geography.CitiesAsync(divisionId, ct);
        }
    }

    public async Task RefreshGeographyAsync(CancellationToken ct)
    {
        var values = await GetAsync<IReadOnlyCollection<GeographyHierarchyItem>>(
            $"/api/pos/v1/customers/geography/hierarchy?businessId={scope.BusinessId:D}", ct);
        await geography.ReplaceAsync(values, ct);
    }

    private async Task<T> GetAsync<T>(string uri, CancellationToken ct)
    {
        using var request = DeviceRequest(HttpMethod.Get, uri);
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct)
            ?? throw new InvalidDataException("Auraly Server returned an empty response.");
    }

    private HttpRequestMessage DeviceRequest(HttpMethod method, string uri)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Add("X-Auraly-Device-Id", credentials.DeviceId.ToString("D"));
        request.Headers.Add("X-Auraly-Device-Secret", credentials.Secret);
        return request;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var detail = await response.Content.ReadAsStringAsync(ct);
        throw new HttpRequestException(
            string.IsNullOrWhiteSpace(detail) ? $"Auraly Server returned {(int)response.StatusCode}." : detail,
            null,
            response.StatusCode);
    }
}


