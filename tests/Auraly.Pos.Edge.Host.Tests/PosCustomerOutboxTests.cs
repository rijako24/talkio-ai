using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Auraly.BuildingBlocks.Domain.Identifiers;
using Auraly.Contracts.Catalog;
using Auraly.Contracts.Parties;
using Auraly.Pos.Edge.Host;
using Auraly.Pos.Edge.Infrastructure;
using Xunit;

namespace Auraly.Pos.Edge.Host.Tests;

public sealed class PosCustomerOutboxTests
{
    [Fact]
    public async Task Customer_is_local_and_durable_offline_then_uploads_with_the_same_id()
    {
        var database = Path.Combine(
            Path.GetTempPath(), $"auraly-pos-offline-customer-{Guid.NewGuid():N}.db");
        try
        {
            var businessId = Guid.NewGuid();
            var warehouseId = Guid.NewGuid();
            var deviceId = Guid.NewGuid();
            var workSessionId = Guid.NewGuid();
            var countryId = Guid.NewGuid();
            var divisionId = Guid.NewGuid();
            var cityId = Guid.NewGuid();
            var connectionString = $"Data Source={database}";
            var catalog = new PosCatalogStore(connectionString);
            await ReadyAsync(catalog);
            var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
            var scope = new PosOperationalScope(businessId, warehouseId);
            var store = new PosCustomerOutboxStore(
                connectionString, new SequentialIds(), clock, scope);
            var input = new PosCreateCustomerInput(
                PartyTypes.NaturalPerson, countryId, "CC", "9.001", null,
                "Cliente offline", null, "Cliente", "Offline",
                "offline@auraly.test", "3001234567",
                new PartySiteInput(
                    "MAIN", "Principal", countryId, divisionId, cityId,
                    "Calle offline", null, null, null, "3001234567"));

            var local = await store.QueueAsync(input, workSessionId);

            var persisted = await catalog.GetCustomerAsync(local.CustomerId);
            Assert.NotNull(persisted);
            Assert.Equal(local.CustomerId, persisted.CustomerId);
            Assert.Equal(local.Identification, persisted.Identification);
            Assert.Equal(local.Name, persisted.Name);
            Assert.Equal(1, (await store.ReadStatusAsync()).PendingCount);
            var dispatcher = new PosUnifiedOutboxDispatcher(connectionString, clock);
            Assert.Equal(PosUnifiedOutboxRoute.CustomerCreated, await dispatcher.NextAsync());

            var credentials = new PosDeviceCredentials(deviceId, "device-secret");
            var events = new PosSynchronizationEventLog(clock);
            using (var disconnectedHttp = new HttpClient(new DisconnectedHandler())
                   { BaseAddress = new Uri("https://offline.test") })
            {
                var sync = new PosCatalogSynchronizer(
                    disconnectedHttp, catalog, credentials, scope, events);
                var uploader = new PosCustomerOutboxUploader(
                    store, disconnectedHttp, credentials, sync, events);
                Assert.True(await uploader.UploadNextAsync());
            }
            var pending = await store.ReadStatusAsync();
            Assert.Equal(1, pending.PendingCount);
            Assert.NotNull(pending.LastError);

            clock.Advance(TimeSpan.FromSeconds(6));
            var connectedHandler = new ConnectedHandler(
                businessId, local.CustomerId, local.Identification);
            using (var connectedHttp = new HttpClient(connectedHandler)
                   { BaseAddress = new Uri("https://auraly.test") })
            {
                var sync = new PosCatalogSynchronizer(
                    connectedHttp, catalog, credentials, scope, events);
                var uploader = new PosCustomerOutboxUploader(
                    store, connectedHttp, credentials, sync, events);
                Assert.True(await uploader.UploadNextAsync());
            }

            Assert.Equal(0, (await store.ReadStatusAsync()).PendingCount);
            Assert.Null(await dispatcher.NextAsync());
            var authoritative = await catalog.GetCustomerAsync(local.CustomerId);
            Assert.NotNull(authoritative);
            Assert.Equal("Cliente offline autoritativo", authoritative.Name);
            Assert.Equal(local.CustomerId, connectedHandler.RequestedCustomerId);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var path in new[] { database, database + "-wal", database + "-shm" })
                if (File.Exists(path)) File.Delete(path);
        }
    }

    private static async Task ReadyAsync(PosCatalogStore store)
    {
        await store.InitializeAsync();
        var sessionId = Guid.NewGuid();
        var items = Array.Empty<PosCatalogItem>();
        var hash = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(items)))).ToLowerInvariant();
        await store.BeginBootstrapAsync(new CatalogSyncSessionResponse(
            sessionId, 0, 0, DateTimeOffset.UtcNow.AddHours(1)));
        await store.ApplyBootstrapPageAsync(new CatalogBootstrapPage(
            sessionId, 0, null, false, hash, items));
        await store.PromoteBootstrapAsync();
    }

    private sealed class SequentialIds : IAuralyIdGenerator
    {
        public Guid NewId() => Guid.NewGuid();
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset value = now;
        public override DateTimeOffset GetUtcNow() => value;
        public void Advance(TimeSpan duration) => value += duration;
    }

    private sealed class DisconnectedHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("offline");
    }

    private sealed class ConnectedHandler(
        Guid businessId,
        Guid customerId,
        string identification) : HttpMessageHandler
    {
        public Guid? RequestedCustomerId { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.PathAndQuery;
            if (path == "/api/pos/v1/customers" && request.Method == HttpMethod.Post)
            {
                var input = await request.Content!.ReadFromJsonAsync<CreateCustomerRequest>(
                    cancellationToken);
                RequestedCustomerId = input!.RequestedCustomerId;
                return Ok(new CustomerDetail(
                    customerId, Guid.NewGuid(), businessId, PartyTypes.NaturalPerson,
                    "CC", identification, identification, null,
                    "Cliente offline autoritativo", null, "Cliente", "Offline",
                    "offline@auraly.test", "3001234567", null, true, []));
            }
            if (path.StartsWith("/api/pos/v1/pricing/snapshot?", StringComparison.Ordinal))
                return Ok(new PosPricingSnapshot([], [], [], [
                    new PosCustomerPricing(
                        customerId, identification, "Cliente offline autoritativo", null, true)
                ]));
            if (path.StartsWith("/api/commerce/v1/reference-options/", StringComparison.Ordinal))
                return Ok<IReadOnlyList<ReferenceOption>>([]);
            if (path == "/api/pos/v1/accounting/settlement-configuration")
                return Ok(new PosAccountingSettlementConfiguration(false, []));
            if (path.StartsWith("/api/pos/v1/catalog/changes?", StringComparison.Ordinal))
                return Ok(new CatalogDeltaPage(0, 0, false, []));
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
