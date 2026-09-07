using System.Net;
using System.Net.Http.Json;
using Auraly.Contracts.Parties;
using Auraly.Pos.Edge.Host;
using Auraly.Pos.Edge.Infrastructure;
using Xunit;

namespace Auraly.Pos.Edge.Host.Tests;

public sealed class PosCustomerServerClientTests
{
    [Fact]
    public async Task Geography_is_cached_for_offline_customer_creation()
    {
        var database = Path.Combine(Path.GetTempPath(), $"auraly-pos-customer-{Guid.NewGuid():N}.db");
        try
        {
            var businessId = Guid.NewGuid();
            var warehouseId = Guid.NewGuid();
            var deviceId = Guid.NewGuid();
            var countryId = Guid.NewGuid();
            var divisionId = Guid.NewGuid();
            var cityId = Guid.NewGuid();
            var handler = new CustomerServerHandler(
                countryId, divisionId, cityId);
            using var http = new HttpClient(handler) { BaseAddress = new Uri("https://auraly.test") };
            var credentials = new PosDeviceCredentials(deviceId, "device-secret");
            var scope = new PosOperationalScope(businessId, warehouseId);
            var geography = new PosCustomerGeographyStore($"Data Source={database}");
            var client = new PosCustomerServerClient(
                http, credentials, scope, geography);

            var countries = await client.CountriesAsync(default);
            Assert.Equal(countryId, Assert.Single(countries).CountryId);
            var divisions = await client.DivisionsAsync(countryId, default);
            Assert.Equal(divisionId, Assert.Single(divisions).AdministrativeDivisionId);
            var cities = await client.CitiesAsync(divisionId, default);
            Assert.Equal(cityId, Assert.Single(cities).CityId);
            await client.RefreshGeographyAsync(default);
            handler.IsOffline = true;
            Assert.Equal(countryId, Assert.Single(
                await client.CountriesAsync(default)).CountryId);
            Assert.Equal(divisionId, Assert.Single(
                await client.DivisionsAsync(countryId, default)).AdministrativeDivisionId);
            Assert.Equal(cityId, Assert.Single(
                await client.CitiesAsync(divisionId, default)).CityId);
            Assert.All(handler.DeviceRequests, request =>
            {
                Assert.Equal(deviceId.ToString("D"), request.DeviceId);
                Assert.Equal("device-secret", request.Secret);
            });
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var path in new[] { database, database + "-wal", database + "-shm" })
                if (File.Exists(path)) File.Delete(path);
        }
    }

    private sealed class CustomerServerHandler(
        Guid countryId,
        Guid divisionId,
        Guid cityId) : HttpMessageHandler
    {
        public bool IsOffline { get; set; }
        public List<(string? DeviceId, string? Secret)> DeviceRequests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            if (IsOffline) throw new HttpRequestException("offline");
            DeviceRequests.Add((
                request.Headers.TryGetValues("X-Auraly-Device-Id", out var ids) ? ids.Single() : null,
                request.Headers.TryGetValues("X-Auraly-Device-Secret", out var secrets) ? secrets.Single() : null));
            var path = request.RequestUri!.PathAndQuery;
            if (path.StartsWith("/api/pos/v1/customers/geography/countries?", StringComparison.Ordinal))
                return Ok<IReadOnlyCollection<CountryItem>>([
                    new CountryItem(countryId, "CO", "Colombia", true)
                ]);
            if (path.StartsWith($"/api/pos/v1/customers/geography/countries/{countryId:D}/divisions?", StringComparison.Ordinal))
                return Ok<IReadOnlyCollection<AdministrativeDivisionItem>>([
                    new AdministrativeDivisionItem(divisionId, countryId, "ANT", "Antioquia", "Department", true)
                ]);
            if (path.StartsWith($"/api/pos/v1/customers/geography/divisions/{divisionId:D}/cities?", StringComparison.Ordinal))
                return Ok<IReadOnlyCollection<CityItem>>([
                    new CityItem(cityId, divisionId, "MED", "Medell�n", true)
                ]);
            if (path.StartsWith("/api/pos/v1/customers/geography/hierarchy?", StringComparison.Ordinal))
                return Ok<IReadOnlyCollection<GeographyHierarchyItem>>([
                    new GeographyHierarchyItem(countryId, null, "Country", "CO", "Colombia", true),
                    new GeographyHierarchyItem(divisionId, countryId, "Division", "ANT", "Antioquia", true),
                    new GeographyHierarchyItem(cityId, divisionId, "City", "MED", "Medellín", true)
                ]);
            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = JsonContent.Create(new { path })
            };
        }

        private static HttpResponseMessage Ok<T>(T value) => new(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(value)
        };
    }
}


