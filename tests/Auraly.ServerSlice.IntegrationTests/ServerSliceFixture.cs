using System.Collections.Concurrent;
using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Claims;
using System.Text;
using System.Net.Http.Json;
using Auraly.Api;
using Auraly.Application.DocumentProcessing;
using Auraly.Commerce.Accounting.Application;
using Auraly.Commerce.Accounting.Contracts;
using Auraly.Application.Fiscal;
using Auraly.BuildingBlocks.Application.Synchronization;
using Auraly.Contracts.Authentication;
using Auraly.Contracts.Catalog;
using Auraly.Contracts.Authorization;
using Auraly.Contracts.Fiscal;
using Auraly.Contracts.Sales;
using Auraly.Contracts.WorkSessions;
using Auraly.Fiscal.Core;
using Auraly.Infrastructure.Persistence;
using Auraly.Contracts.Parties;
using Auraly.Platform.Application.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;

namespace Auraly.ServerSlice.IntegrationTests;

public sealed record SalesDocumentTaxBreakdown(
    string TaxCode,
    decimal TaxRate,
    decimal TaxableAmount,
    decimal TaxAmount,
    decimal TotalAmount);

public sealed record SalesWorkResponsibility(
    Guid SoldByUserId,
    Guid WorkSessionId);

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ServerSliceCollection : ICollectionFixture<ServerSliceFixture>
{
    public const string Name = "Auraly server slice";
}

public sealed class ServerSliceFixture : IAsyncLifetime
{
    public const string TechnicalKeyValue = "AURALY-TEST-TECHNICAL-KEY";
    public const string TechnicalKeyVersion = "test-v1";
    public const string SupplierTaxId = "9001234567";
    public const string AuthorizationNumber = "18760000099";
    public const string Prefix = "FV99";
    public const string DeviceSecret = "Auraly-allowed-device-secret";
    public const string DeniedDeviceSecret = "Auraly-denied-device-secret";
    public const string JwtIssuer = "Auraly.Tests";
    public const string JwtAudience = "Auraly.Api.Tests";
    public const string JwtSigningKey = "Auraly-Catalog-Integration-Tests-Key-2026";
    public const string OfflineLeaseKeyId = "integration-test-offline-lease";
    public const string QrValidationUrl =
        "https://catalogo-vpfe.dian.gov.co/document/searchqr";
    public static readonly byte[] FiscalSecretProtectionKey =
        SHA256.HashData(Encoding.UTF8.GetBytes("Auraly.ServerSlice fiscal secret protection key"));

    private WebApplicationFactory<Program>? _factory;
    private string? _databaseName;
    private readonly object _authenticationSessionLock = new();
    private readonly Dictionary<Guid, TestAuthenticationSession> _authenticationSessions = [];
    private readonly Dictionary<string, string?> _originalEnvironment = new(StringComparer.Ordinal);
    private readonly RSA _offlineLeaseKey = RSA.Create(2048);
    public string OfflineLeasePublicKeyPem => _offlineLeaseKey.ExportSubjectPublicKeyInfoPem();

    public Guid TenantId { get; } = Guid.NewGuid();
    public Guid BusinessId { get; } = Guid.NewGuid();
    public Guid WarehouseId { get; } = Guid.NewGuid();
    public Guid DeviceId { get; } = Guid.NewGuid();
    public Guid OnlineDeviceId { get; } = Guid.NewGuid();
    public Guid WorkSessionId { get; } = Guid.NewGuid();
    public Guid DeniedDeviceId { get; } = Guid.NewGuid();
    public Guid UserId { get; } = Guid.NewGuid();
    public Guid RoleId { get; } = Guid.NewGuid();
    public Guid PriceChannelId { get; } = Guid.NewGuid();
    public Guid TaxProfileId { get; } = Guid.NewGuid();
    public Guid ProductId { get; } = Guid.NewGuid();
    public Guid DocumentSeriesId { get; } = Guid.NewGuid();
    public Guid SeriesId { get; } = Guid.NewGuid();
    public Guid SupplierId { get; } = Guid.NewGuid();
    public Guid SupplierPartyId { get; } = Guid.NewGuid();
    public Guid GoodsReceiptSeriesId { get; } = Guid.NewGuid();
    public Guid PurchaseOrderSeriesId { get; } = Guid.NewGuid();
    public Guid PurchaseReturnSeriesId { get; } = Guid.NewGuid();
    public Guid OnlineDocumentSeriesId { get; } = Guid.NewGuid();
    public Guid OnlineSalesReceiptSeriesId { get; } = Guid.NewGuid();
    public Guid SalesReturnSeriesId { get; } = Guid.NewGuid();
    public Guid SalesDebitNoteSeriesId { get; } = Guid.NewGuid();
    public Guid OnlineSeriesId { get; } = Guid.NewGuid();
    public Guid FiscalAuthorizationId { get; } = Guid.NewGuid();
    public Guid FiscalIssuerConfigurationId { get; } = Guid.NewGuid();

    public IServiceScope CreateScope() =>
        (_factory ?? throw new InvalidOperationException("The test host is not initialized."))
        .Services.CreateScope();
    public string SqlServer { get; } =
        Environment.GetEnvironmentVariable("AURALY_TEST_SQLSERVER") ?? @".\LOCAL";
    public string ConnectionString { get; private set; } = string.Empty;

    public HttpClient CreateClient() =>
        (_factory ?? throw new InvalidOperationException("The API fixture is not initialized."))
        .CreateClient();
    internal IServiceProvider Services =>
        (_factory ?? throw new InvalidOperationException(
            "The API fixture is not initialized.")).Services;

    internal TestDocumentProcessingSignalPublisher DocumentSignals =>
        Services.GetRequiredService<TestDocumentProcessingSignalPublisher>();

    internal void PauseDocumentProcessing() => DocumentSignals.PauseProcessing();

    internal void ResumeDocumentProcessing() => DocumentSignals.ResumeProcessing();

    internal IReadOnlyCollection<DocumentProcessingSignal> DrainDocumentSignals() =>

        DocumentSignals.Drain();
    public async Task InitializeAsync()
    {
        _databaseName = $"AuralyServerSlice_{Guid.NewGuid():N}";
        ConnectionString =
            $"Server={SqlServer};Initial Catalog={_databaseName};Integrated Security=True;TrustServerCertificate=True;";
        await DeployDacpacAsync();
        await SeedAsync();
        ConfigureHostEnvironment();
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Auraly"] = ConnectionString,
                    ["Authentication:Jwt:Issuer"] = JwtIssuer,
                    ["Authentication:Jwt:Audience"] = JwtAudience,
                    ["Authentication:Jwt:SigningKey"] = JwtSigningKey,
                    ["Authentication:OfflineLeaseSigning:KeyId"] = OfflineLeaseKeyId,
                    ["Authentication:OfflineLeaseSigning:PrivateKeyPem"] =
                        _offlineLeaseKey.ExportPkcs8PrivateKeyPem(),
                    ["Authentication:OfflineLeaseSigning:DurationHours"] = "8",
                    ["Auraly:Fiscal:TechnicalKeys:0:TenantId"] = TenantId.ToString("D"),
                    ["Auraly:Fiscal:TechnicalKeys:0:BusinessId"] = BusinessId.ToString("D"),
                    ["Auraly:Fiscal:TechnicalKeys:0:AuthorizationNumber"] = AuthorizationNumber,
                    ["Auraly:Fiscal:TechnicalKeys:0:Version"] = TechnicalKeyVersion,
                    ["Auraly:Fiscal:TechnicalKeys:0:Environment"] = "2",
                    ["Auraly:Fiscal:TechnicalKeys:0:Value"] = TechnicalKeyValue,
                    ["Auraly:Fiscal:TechnicalKeys:0:SupplierTaxId"] = SupplierTaxId,
                    ["Auraly:Fiscal:TechnicalKeys:0:QrValidationUrl"] = QrValidationUrl,
                    ["Auraly:Fiscal:SecretProtectionKey"] =
                        Convert.ToBase64String(FiscalSecretProtectionKey),
                    ["Auraly:Fiscal:Worker:Enabled"] = "false",
                    ["Auraly:PosSynchronization:WebPubSub:ConnectionString"] =
                        $"Endpoint=https://push.auraly.test;" +
                        $"AccessKey={Convert.ToBase64String(new byte[32])};" +
                        "Version=1.0;",
                    ["Auraly:PosSynchronization:WebPubSub:Hub"] = "auraly_pos",
                    ["WhatsApp:Webhook:ApiBaseUrl"] = "https://graph.facebook.test/v25.0/",
                    ["WhatsApp:Webhook:VerifyToken"] = "auraly-integration-test"
                });
            });
            builder.ConfigureTestServices(services =>
            {
                services.AddHttpContextAccessor();
                services.AddSingleton<TestExecutionAccessRegistry>();
                services.RemoveAll<IExecutionAccessResolver>();
                services.AddScoped<IExecutionAccessResolver, TestExecutionAccessResolver>();
                services.RemoveAll<IDocumentProcessingSignalPublisher>();
                services.AddSingleton<TestDocumentProcessingSignalPublisher>();
                services.AddSingleton<IDocumentProcessingSignalPublisher>(provider =>
                    provider.GetRequiredService<TestDocumentProcessingSignalPublisher>());
                services.RemoveAll<IAccountingProcessingSignalPublisher>();
                services.AddSingleton<TestAccountingProcessingSignalPublisher>();
                services.AddSingleton<IAccountingProcessingSignalPublisher>(provider =>
                    provider.GetRequiredService<TestAccountingProcessingSignalPublisher>());
                services.RemoveAll<IFiscalProcessingSignalPublisher>();
                services.AddSingleton<TestFiscalProcessingSignalPublisher>();
                services.AddSingleton<IFiscalProcessingSignalPublisher>(provider => provider.GetRequiredService<TestFiscalProcessingSignalPublisher>());
                services.AddSingleton<TestPosSynchronizationPushGateway>();
                services.AddSingleton<IPosSynchronizationPushGateway>(provider =>
                    provider.GetRequiredService<
                        TestPosSynchronizationPushGateway>());
                services.RemoveAll<IBlobStorageService>();
                services.RemoveAll<IMediaUrlResolver>();
                services.AddSingleton<IBlobStorageService, TestBlobStorageService>();
                services.AddSingleton<IMediaUrlResolver, TestMediaUrlResolver>();
            });
        });
        using var client = CreateClient();
        using var response = await client.GetAsync("/health");
        response.EnsureSuccessStatusCode();
        await ConfigureAccountingAsync();
    }

    private async Task ConfigureAccountingAsync()
    {
        using var client = CreateAdminClient(
            AccountingPermissionCodes.Read,
            AccountingPermissionCodes.Configure,
            AccountingPermissionCodes.PeriodsManage,
            AccountingPermissionCodes.Activate);
        using (var defaults = await client.PutAsync(
                   "/api/commerce/v1/accounting/defaults", null))
            defaults.EnsureSuccessStatusCode();
        using var activation = await client.PostAsJsonAsync(
            "/api/commerce/v1/accounting/activate",
            new ActivateAccountingRequest(
                new DateOnly(2026, 1, 1), "COP", "ZeroDeclared"));
        activation.EnsureSuccessStatusCode();
    }

    public IReadOnlyCollection<PosSynchronizationInvalidation>
        DrainSynchronizationMessages() =>
        (_factory ?? throw new InvalidOperationException(
            "The API fixture is not initialized."))
        .Services.GetRequiredService<TestPosSynchronizationPushGateway>()
        .Drain();

    internal IReadOnlyCollection<PublishedFiscalSignal> DrainFiscalSignals() =>
        (_factory ?? throw new InvalidOperationException(
            "The API fixture is not initialized."))
        .Services.GetRequiredService<TestFiscalProcessingSignalPublisher>()
        .Drain();

    public async Task<PosSynchronizationInvalidation>
        ReadSynchronizationMessageAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        return await (_factory ?? throw new InvalidOperationException(
            "The API fixture is not initialized."))
            .Services
            .GetRequiredService<TestPosSynchronizationPushGateway>()
            .ReadAsync(timeout.Token);
    }

    public void FailNextSynchronizationPublication() =>
        (_factory ?? throw new InvalidOperationException(
            "The API fixture is not initialized."))
        .Services
        .GetRequiredService<TestPosSynchronizationPushGateway>()
        .FailNext();

    public HttpClient CreateAdminClient(params string[] permissions)
    {
        var session = EnsureAuthenticationSession(UserId);
        var executionAccessId = RegisterExecutionPermissions(permissions);
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, UserId.ToString("D")),
            new(ClaimTypes.NameIdentifier, UserId.ToString("D")),
            new("tenant_id", TenantId.ToString("D")),
            new("business_id", BusinessId.ToString("D")),
            new(AuthenticationDefaults.SessionIdClaim,
                session.AuthenticationSessionId.ToString("D")),
            new("full_name", "Cajero de pruebas")
        };
        claims.Add(new Claim(TestExecutionAccessResolver.AccessProfileClaim, executionAccessId.ToString("D")));
        claims.AddRange(permissions.Select(permission => new Claim("permission", permission)));
        var token = new JwtSecurityToken(
            JwtIssuer, JwtAudience, claims, expires: DateTime.UtcNow.AddMinutes(10),
            signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(JwtSigningKey)),
                SecurityAlgorithms.HmacSha256));
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        client.DefaultRequestHeaders.Add(
            AuthenticationDefaults.ClientIdHeader, session.ClientId.ToString("D"));
        client.DefaultRequestHeaders.Add("X-Business-Id", BusinessId.ToString("D"));
        return client;
    }
    public HttpClient CreateAdminClientWithBusinessHeader(
        Guid businessId,
        params string[] permissions)
    {
        var session = EnsureAuthenticationSession(UserId);
        var executionAccessId = RegisterExecutionPermissions(permissions);
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, UserId.ToString("D")),
            new(ClaimTypes.NameIdentifier, UserId.ToString("D")),
            new("tenant_id", TenantId.ToString("D")),
            new(AuthenticationDefaults.SessionIdClaim,
                session.AuthenticationSessionId.ToString("D")),
            new("full_name", "Cajero de pruebas")
        };
        claims.Add(new Claim(TestExecutionAccessResolver.AccessProfileClaim, executionAccessId.ToString("D")));
        claims.AddRange(permissions.Select(
            permission => new Claim("permission", permission)));
        var token = new JwtSecurityToken(
            JwtIssuer,
            JwtAudience,
            claims,
            expires: DateTime.UtcNow.AddMinutes(10),
            signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(JwtSigningKey)),
                SecurityAlgorithms.HmacSha256));
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        client.DefaultRequestHeaders.Add(
            AuthenticationDefaults.ClientIdHeader, session.ClientId.ToString("D"));
        client.DefaultRequestHeaders.Add("X-Business-Id", businessId.ToString("D"));
        return client;
    }

    public HttpClient CreateUserClient(Guid userId, params string[] permissions)
    {
        var session = EnsureAuthenticationSession(userId);
        var executionAccessId = RegisterExecutionPermissions(permissions);
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, userId.ToString("D")),
            new(ClaimTypes.NameIdentifier, userId.ToString("D")),
            new("tenant_id", TenantId.ToString("D")),
            new("business_id", BusinessId.ToString("D")),
            new(AuthenticationDefaults.SessionIdClaim,
                session.AuthenticationSessionId.ToString("D"))
        };
        claims.Add(new Claim(TestExecutionAccessResolver.AccessProfileClaim, executionAccessId.ToString("D")));
        claims.AddRange(permissions.Select(
            permission => new Claim("permission", permission)));
        var token = new JwtSecurityToken(
            JwtIssuer,
            JwtAudience,
            claims,
            expires: DateTime.UtcNow.AddMinutes(10),
            signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(JwtSigningKey)),
                SecurityAlgorithms.HmacSha256));
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        client.DefaultRequestHeaders.Add(
            AuthenticationDefaults.ClientIdHeader, session.ClientId.ToString("D"));
        client.DefaultRequestHeaders.Add("X-Business-Id", BusinessId.ToString("D"));
        return client;
    }
    public HttpClient CreateTenantUserClient(Guid tenantId, Guid userId, params string[] permissions)
    {
        var session = EnsureExistingUserAuthenticationSession(tenantId, userId);
        var executionAccessId = RegisterExecutionPermissions(permissions);
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, userId.ToString("D")),
            new(ClaimTypes.NameIdentifier, userId.ToString("D")),
            new("tenant_id", tenantId.ToString("D")),
            new(AuthenticationDefaults.SessionIdClaim, session.AuthenticationSessionId.ToString("D")),
            new(TestExecutionAccessResolver.AccessProfileClaim, executionAccessId.ToString("D"))
        };
        claims.AddRange(permissions.Select(permission => new Claim("permission", permission)));
        var token = new JwtSecurityToken(
            JwtIssuer, JwtAudience, claims, expires: DateTime.UtcNow.AddMinutes(10),
            signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(JwtSigningKey)),
                SecurityAlgorithms.HmacSha256));
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        client.DefaultRequestHeaders.Add(AuthenticationDefaults.ClientIdHeader, session.ClientId.ToString("D"));
        return client;
    }

    private TestAuthenticationSession EnsureExistingUserAuthenticationSession(Guid tenantId, Guid userId)
    {
        lock (_authenticationSessionLock)
        {
            if (_authenticationSessions.TryGetValue(userId, out var existing)) return existing;
            var session = new TestAuthenticationSession(Guid.NewGuid(), Guid.NewGuid());
            var now = DateTimeOffset.UtcNow;
            using var connection = new SqlConnection(ConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                IF NOT EXISTS(SELECT 1 FROM dbo.AppUsers WHERE UserId=@UserId AND TenantId=@TenantId)
                    THROW 51000,N'El usuario de prueba no pertenece al tenant solicitado.',1;
                INSERT dbo.AuthenticationSessions
                  (AuthenticationSessionId,TenantId,UserId,ClientId,ClientDescription,
                   RefreshTokenHash,IssuedAt,ExpiresAt,LastSeenAt,Status)
                VALUES
                  (@SessionId,@TenantId,@UserId,@ClientId,N'Platform authorization integration test',
                   @Hash,@Now,@ExpiresAt,@Now,N'Active');
                """;
            command.Parameters.AddWithValue("@SessionId", session.AuthenticationSessionId);
            command.Parameters.AddWithValue("@TenantId", tenantId);
            command.Parameters.AddWithValue("@UserId", userId);
            command.Parameters.AddWithValue("@ClientId", session.ClientId);
            command.Parameters.Add("@Hash", System.Data.SqlDbType.VarBinary, 32).Value = SHA256.HashData(session.AuthenticationSessionId.ToByteArray());
            command.Parameters.AddWithValue("@Now", now);
            command.Parameters.AddWithValue("@ExpiresAt", now.AddHours(1));
            command.ExecuteNonQuery();
            _authenticationSessions.Add(userId, session);
            return session;
        }
    }
    public async Task<WorkSessionView> OpenWorkSessionAsync(
        HttpClient client,
        Guid? deviceId = null)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/commerce/v1/work-sessions/current",
            new OpenWorkSessionRequest(BusinessId, WarehouseId, deviceId));
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<WorkSessionView>()
            ?? throw new InvalidOperationException("Empty work-session response.");
    }
    public async Task DisposeAsync()
    {
        _factory?.Dispose();
        _offlineLeaseKey.Dispose();
        RestoreHostEnvironment();
        if (_databaseName is null)
        {
            return;
        }

        SqlConnection.ClearAllPools();
        var master =
            $"Server={SqlServer};Initial Catalog=master;Integrated Security=True;TrustServerCertificate=True;";
        await using var connection = new SqlConnection(master);
        await connection.OpenAsync();
        var escaped = _databaseName.Replace("]", "]]", StringComparison.Ordinal);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
             IF DB_ID(N'{_databaseName}') IS NOT NULL
             BEGIN
                 ALTER DATABASE [{escaped}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                 DROP DATABASE [{escaped}];
             END;
             """;
        await command.ExecuteNonQueryAsync();
    }

    public PosSaleUploadRequest CreateValidRequest(long consecutive, Guid? documentId = null)
    {
        var issuedAt = new DateTimeOffset(
            2026,
            7,
            27,
            14,
            35,
            checked((int)(consecutive % 60)),
            TimeSpan.FromHours(-5));
        const decimal quantity = 1m;
        const decimal unitPrice = 10_000m;
        const decimal discount = 0m;
        const decimal tax = 1_900m;
        const decimal untaxed = 10_000m;
        const decimal payable = 11_900m;
        var fiscalNumber = $"{Prefix}{consecutive}";
        var cufe = CufeCalculator.Calculate(
            new CufeInput(
                fiscalNumber,
                issuedAt,
                untaxed,
                payable,
                SupplierTaxId,
                "222222222",
                new FiscalTechnicalKey(TechnicalKeyValue, TechnicalKeyVersion),
                FiscalEnvironment.Test,
                [new FiscalTaxAmount("01", tax)]),
            QrValidationUrl);
        return new PosSaleUploadRequest(
            TenantId,
            BusinessId,
            WarehouseId,
            DeviceId,
            WorkSessionId,
            UserId,
            documentId ?? Guid.NewGuid(),
            new PosSaleDocumentNumberContract(
                DocumentSeriesId,
                PosSaleDocumentTypes.Invoice,
                "VTA",
                "03",
                consecutive,
                8,
                $"VTA03-{consecutive:D8}"),
            new PosSaleCommercialSnapshotContract(
                PosSaleDocumentTypes.Invoice,
                issuedAt,
                "222222222",
                [new PosSaleTaxContract("01", tax)],
                untaxed,
                tax,
                payable),
            new PosSaleFiscalSnapshotContract(
                SeriesId,
                FiscalAuthorizationId,
                AuthorizationNumber,
                PosSaleDocumentTypes.Invoice,
                fiscalNumber,
                Prefix,
                consecutive,
                issuedAt,
                SupplierTaxId,
                "222222222",
                (int)FiscalEnvironment.Test,
                TechnicalKeyVersion,
                [new PosSaleTaxContract("01", tax)],
                untaxed,
                tax,
                payable,
                cufe.Cufe,
                cufe.QrPayload),
            [
                new PosSaleLineContract(
                    1,
                    ProductId,
                    "Producto E2E",
                    "01",
                    quantity,
                    unitPrice,
                    discount,
                    tax,
                    untaxed,
                    payable,
                    19m)
            ],
            [new PosSalePaymentContract(1, "Cash", payable, null)]);
    }

    public PosSaleUploadRequest CreateMultiRateRequest(long consecutive)
    {
        var request = CreateValidRequest(consecutive);
        var first = new PosSaleLineContract(
            1, ProductId, "Producto IVA 5", "01",
            1m, 10_000m, 0m, 500m, 10_000m, 10_500m, 5m);
        var second = new PosSaleLineContract(
            2, ProductId, "Producto IVA 19", "01",
            1m, 20_000m, 0m, 3_800m, 20_000m, 23_800m, 19m);
        const decimal untaxed = 30_000m;
        const decimal tax = 4_300m;
        const decimal payable = 34_300m;
        var calculated = CufeCalculator.Calculate(
            new CufeInput(
                request.FiscalSnapshot!.FiscalNumber,
                request.FiscalSnapshot.IssuedAt,
                untaxed,
                payable,
                SupplierTaxId,
                request.FiscalSnapshot.CustomerIdentification,
                new FiscalTechnicalKey(TechnicalKeyValue, TechnicalKeyVersion),
                FiscalEnvironment.Test,
                [new FiscalTaxAmount("01", tax)]),
            QrValidationUrl);
        return request with
        {
            Lines = [first, second],
            Payments = [new PosSalePaymentContract(1, "Cash", payable, null)],
            CommercialSnapshot = request.CommercialSnapshot with
            {
                Taxes = [new PosSaleTaxContract("01", tax)],
                UntaxedAmount = untaxed,
                TaxAmount = tax,
                PayableAmount = payable
            },
            FiscalSnapshot = request.FiscalSnapshot with
            {
                Taxes = [new PosSaleTaxContract("01", tax)],
                UntaxedAmount = untaxed,
                TaxAmount = tax,
                PayableAmount = payable,
                Cufe = calculated.Cufe,
                QrPayload = calculated.QrPayload
            }
        };
    }

    public HttpRequestMessage CreateUploadMessage(
        PosSaleUploadRequest request,
        string? secret = DeviceSecret,
        Guid? deviceId = null,
        string? idempotencyKey = null)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, "/api/pos/v1/sales")
        {
            Content = JsonContent.Create(request)
        };
        if (secret is not null)
        {
            message.Headers.Add(
                "X-Auraly-Device-Id",
                (deviceId ?? request.DeviceId).ToString("D"));
            message.Headers.Add("X-Auraly-Device-Secret", secret);
        }

        message.Headers.Add(
            "Idempotency-Key",
            idempotencyKey ?? request.DocumentId.ToString("D"));
        return message;
    }

    public async Task<IReadOnlyList<SalesDocumentTaxBreakdown>> GetLineTaxBreakdownAsync(Guid documentId)
    {
        const string sql = """
            SELECT TaxCode, TaxRate,
                   SUM(UntaxedAmount), SUM(TaxAmount), SUM(LineTotal)
            FROM dbo.SalesDocumentLines
            WHERE DocumentId = @DocumentId
            GROUP BY TaxCode, TaxRate
            ORDER BY TaxCode, TaxRate;
            """;
        var rows = new List<SalesDocumentTaxBreakdown>();
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@DocumentId", documentId);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new SalesDocumentTaxBreakdown(
                reader.GetString(0),
                reader.GetDecimal(1),
                reader.GetDecimal(2),
                reader.GetDecimal(3),
                reader.GetDecimal(4)));
        }
        return rows;
    }

    public async Task<SalesWorkResponsibility> GetSalesWorkResponsibilityAsync(
        Guid documentId)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT SoldByUserId,WorkSessionId
            FROM dbo.SalesDocuments
            WHERE DocumentId=@DocumentId;
            """;
        command.Parameters.AddWithValue("@DocumentId", documentId);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidOperationException("The sales document was not found.");
        }

        return new SalesWorkResponsibility(
            reader.GetGuid(0),
            reader.GetGuid(1));
    }

    public async Task<int> CountAsync(string table, Guid documentId)
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "SalesDocuments",
            "SalesDocumentLines",
            "SalesPayments",
            "FiscalSnapshots",
            "FiscalDocumentProcesses",
            "DocumentProcessingJobs",
            "InventoryMovements",
            "WorkSessionMovements",
            "ServerOutboxMessages"
        };
        if (!allowed.Contains(table))
        {
            throw new ArgumentOutOfRangeException(nameof(table));
        }

        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM dbo.[{table}] WHERE DocumentId = @DocumentId;";
        command.Parameters.AddWithValue("@DocumentId", documentId);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private Guid RegisterExecutionPermissions(IReadOnlyCollection<string> permissions)
    {
        var accessProfileId = Guid.NewGuid();
        (_factory ?? throw new InvalidOperationException(
            "The API fixture is not initialized."))
            .Services.GetRequiredService<TestExecutionAccessRegistry>()
            .Register(accessProfileId, permissions);
        return accessProfileId;
    }
    private TestAuthenticationSession EnsureAuthenticationSession(Guid userId)
    {
        lock (_authenticationSessionLock)
        {
            if (_authenticationSessions.TryGetValue(userId, out var existing))
            {
                using var inspection = new SqlConnection(ConnectionString);
                inspection.Open();
                using var status = inspection.CreateCommand();
                status.CommandText = "SELECT Status FROM dbo.AuthenticationSessions WHERE AuthenticationSessionId=@SessionId";
                status.Parameters.AddWithValue("@SessionId", existing.AuthenticationSessionId);
                if (string.Equals(status.ExecuteScalar() as string, "Active", StringComparison.Ordinal))
                    return existing;
                _authenticationSessions.Remove(userId);
            }
            var session = new TestAuthenticationSession(Guid.NewGuid(), Guid.NewGuid());
            var now = DateTimeOffset.UtcNow;
            using var connection = new SqlConnection(ConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                IF NOT EXISTS (SELECT 1 FROM dbo.AppUsers WHERE UserId=@UserId)
                BEGIN
                    INSERT dbo.AppUsers
                      (UserId,TenantId,Username,NormalizedUsername,Email,NormalizedEmail,
                       FirstName,LastName,IsActive,CreatedAt)
                    VALUES
                      (@UserId,@TenantId,CONCAT(N'test-',@UserId),UPPER(CONCAT(N'test-',@UserId)),
                       CONCAT(@UserId,N'@test.local'),UPPER(CONCAT(@UserId,N'@test.local')),N'Test',N'User',1,SYSUTCDATETIME());
                END;
                IF NOT EXISTS(
                    SELECT 1 FROM dbo.UserRoles
                    WHERE UserId=@UserId AND BusinessId=@BusinessId)
                  INSERT dbo.UserRoles(UserRoleId,UserId,RoleId,BusinessId,AssignedAt)
                  VALUES(NEWID(),@UserId,@RoleId,@BusinessId,SYSUTCDATETIME());
                INSERT dbo.AuthenticationSessions
                  (AuthenticationSessionId,TenantId,UserId,ClientId,
                   ClientDescription,RefreshTokenHash,IssuedAt,ExpiresAt,
                   LastSeenAt,Status)
                VALUES
                  (@SessionId,@TenantId,@UserId,@ClientId,N'Integration test',
                   @Hash,@Now,@ExpiresAt,@Now,N'Active');
                """;
            command.Parameters.AddWithValue("@SessionId", session.AuthenticationSessionId);
            command.Parameters.AddWithValue("@TenantId", TenantId);
            command.Parameters.AddWithValue("@UserId", userId);
            command.Parameters.AddWithValue("@RoleId", RoleId);
            command.Parameters.AddWithValue("@BusinessId", BusinessId);
            command.Parameters.AddWithValue("@ClientId", session.ClientId);
            command.Parameters.Add("@Hash", System.Data.SqlDbType.VarBinary, 32).Value =
                SHA256.HashData(session.AuthenticationSessionId.ToByteArray());
            command.Parameters.AddWithValue("@Now", now);
            command.Parameters.AddWithValue("@ExpiresAt", now.AddHours(1));
            command.ExecuteNonQuery();
            _authenticationSessions.Add(userId, session);
            return session;
        }
    }

    private void ConfigureHostEnvironment()
    {
        SetHostEnvironment("ConnectionStrings__Auraly", ConnectionString);
        SetHostEnvironment("Authentication__Jwt__Issuer", JwtIssuer);
        SetHostEnvironment("Authentication__Jwt__Audience", JwtAudience);
        SetHostEnvironment("Authentication__Jwt__SigningKey", JwtSigningKey);
        SetHostEnvironment("Authentication__OfflineLeaseSigning__KeyId", OfflineLeaseKeyId);
        SetHostEnvironment(
            "Authentication__OfflineLeaseSigning__PrivateKeyPem",
            _offlineLeaseKey.ExportPkcs8PrivateKeyPem());
        SetHostEnvironment(
            "Authentication__OfflineLeaseSigning__DurationHours", "8");
        SetHostEnvironment("Auraly__Fiscal__TechnicalKeys__0__TenantId", TenantId.ToString("D"));
        SetHostEnvironment("Auraly__Fiscal__TechnicalKeys__0__BusinessId", BusinessId.ToString("D"));
        SetHostEnvironment("Auraly__Fiscal__TechnicalKeys__0__AuthorizationNumber", AuthorizationNumber);
        SetHostEnvironment("Auraly__Fiscal__TechnicalKeys__0__Version", TechnicalKeyVersion);
        SetHostEnvironment("Auraly__Fiscal__TechnicalKeys__0__Environment", "2");
        SetHostEnvironment("Auraly__Fiscal__TechnicalKeys__0__Value", TechnicalKeyValue);
        SetHostEnvironment("Auraly__Fiscal__TechnicalKeys__0__SupplierTaxId", SupplierTaxId);
        SetHostEnvironment("Auraly__Fiscal__TechnicalKeys__0__QrValidationUrl", QrValidationUrl);
        SetHostEnvironment("Auraly__Fiscal__Worker__Enabled", "false");
        SetHostEnvironment(
            "Auraly__PosSynchronization__WebPubSub__ConnectionString",
            $"Endpoint=https://push.auraly.test;" +
            $"AccessKey={Convert.ToBase64String(new byte[32])};" +
            "Version=1.0;");
        SetHostEnvironment("Auraly__PosSynchronization__WebPubSub__Hub", "auraly_pos");
        SetHostEnvironment("Auraly__DocumentProcessing__Worker__Enabled", "false");
        SetHostEnvironment("WhatsApp__Webhook__ApiBaseUrl", "https://graph.facebook.test/v25.0/");
        SetHostEnvironment("WhatsApp__Webhook__VerifyToken", "auraly-integration-test");
    }

    private void SetHostEnvironment(string name, string value)
    {
        _originalEnvironment[name] = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
    }

    private void RestoreHostEnvironment()
    {
        foreach (var value in _originalEnvironment)
        {
            Environment.SetEnvironmentVariable(value.Key, value.Value);
        }

        _originalEnvironment.Clear();
    }

    private async Task SeedAsync()
    {
        var allowedCredential = PosDeviceCredentialHasher.Create(DeviceSecret);
        var deniedCredential = PosDeviceCredentialHasher.Create(DeniedDeviceSecret);
        const string sql = """
            DECLARE @BillingCustomerPartyId UNIQUEIDENTIFIER=NEWID(),
                    @BillingCustomerId UNIQUEIDENTIFIER=NEWID(),
                    @TenantSubscriptionId UNIQUEIDENTIFIER=NEWID(),
                    @TenantUsagePeriodId UNIQUEIDENTIFIER=NEWID(),
                    @Now DATETIMEOFFSET(7)=SYSDATETIMEOFFSET();

            INSERT INTO dbo.Tenants (TenantId, TenantKey, Name, Email, IsActive, MaximumUsers, MaximumEnrolledDevices, CreatedAt)
            VALUES (@TenantId, N'@auraly-e2e', N'Auraly E2E', @TenantEmail, 1, 512, 512, SYSUTCDATETIME());

            INSERT INTO dbo.Businesses
            (BusinessId, TenantId, Name, Description, Address, Phone, Email, Website, IsActive, CreatedAt)
            VALUES
            (@BusinessId, @TenantId, N'Auraly', N'Integration test billing business',
             N'Bogota', N'3000000000', @BusinessEmail, N'https://auraly.test', 1, SYSUTCDATETIME());

            INSERT dbo.AppUsers
              (UserId,TenantId,Username,NormalizedUsername,Email,NormalizedEmail,
               FirstName,LastName,IsActive,CreatedAt)
            VALUES
              (@UserId,@TenantId,@Username,@NormalizedUsername,@UserEmail,@NormalizedUserEmail,
               N'Cajero',N'E2E',1,SYSUTCDATETIME());

            INSERT dbo.Parties
              (PartyId,TenantId,PartyType,IdentificationCountryId,IdentificationTypeCode,
               Identification,NormalizedIdentification,VerificationDigit,DisplayName,LegalName,
               CompletionStatus,IsActive,CreatedBy,CreatedAt)
            SELECT @BillingCustomerPartyId,@TenantId,N'Organization',CountryId,N'31',
                   N'900000000',N'900000000',N'1',N'Auraly E2E',N'Auraly E2E SAS',
                   N'Complete',1,@UserId,@Now
            FROM dbo.Countries WHERE Code=N'CO';

            INSERT dbo.Customers
              (CustomerId,PartyId,BusinessId,RequiresElectronicInvoice,IsActive,CreatedBy,CreatedAt)
            VALUES(@BillingCustomerId,@BillingCustomerPartyId,@BusinessId,1,1,@UserId,@Now);

            INSERT billing.TenantSubscriptions
              (TenantSubscriptionId,TenantId,TenantCommercialPlanId,BillingCustomerId,
               BillingPeriod,Status,CurrentPeriodStart,CurrentPeriodEnd,BillingAnchorDay,
               FullUserLimit,SellerUserLimit,PosDeviceLimit,DianDocumentMonthlyLimit,
               PayrollEmployeeLimit,CreatedAt,UpdatedAt)
            VALUES(@TenantSubscriptionId,@TenantId,'11000000-0000-0000-0000-000000000000',
                   @BillingCustomerId,N'Monthly',N'Active',DATEADD(day,-1,@Now),DATEADD(year,1,@Now),
                   DAY(@Now),512,512,512,1000000,1000000,@Now,@Now);

            INSERT billing.TenantSubscriptionUsagePeriods
              (TenantSubscriptionUsagePeriodId,TenantSubscriptionId,PeriodStart,PeriodEnd,
               DianDocumentsUsed,CreatedAt,UpdatedAt)
            VALUES(@TenantUsagePeriodId,@TenantSubscriptionId,DATEADD(day,-1,@Now),
                   DATEADD(year,1,@Now),0,@Now,@Now);

            INSERT dbo.AppRoles
              (RoleId,TenantId,Name,NormalizedName,Description,IsActive,IsSystemRole,CreatedAt)
            VALUES
              (@RoleId,@TenantId,N'Integration user',N'INTEGRATION USER',N'Integration test role',1,0,SYSUTCDATETIME());
            INSERT dbo.UserRoles
              (UserRoleId,UserId,RoleId,BusinessId,AssignedAt)
            VALUES
              (NEWID(),@UserId,@RoleId,@BusinessId,SYSUTCDATETIME());

            IF NOT EXISTS(SELECT 1 FROM billing.PlatformBillingSettings WHERE PlatformBillingSettingId=1)
              INSERT billing.PlatformBillingSettings
                (PlatformBillingSettingId,BillingBusinessId,EmailRemindersEnabled,
                 PreDueReminderDays,OverdueReminderIntervalDays,GracePeriodDays,
                 UpdatedByUserId,UpdatedAt)
              VALUES(1,@BusinessId,1,5,3,10,@UserId,SYSDATETIMEOFFSET());

            INSERT INTO dbo.Warehouses
            (WarehouseId, BusinessId, Code, Name, AllowNegativeStockSales, IsActive, CreatedAt)
            VALUES (@WarehouseId, @BusinessId, N'B01', N'Bodega E2E', 1, 1, SYSDATETIMEOFFSET());

            INSERT dbo.BusinessReasons(
                ReasonId,BusinessId,ReasonType,Code,Name,Direction,
                CounterpartAccountingCategory,DefaultCostCenterId,RequiresReference,
                IsSystem,IsActive,DisplayOrder,CreatedAt,UpdatedAt)
            SELECT NEWID(),@BusinessId,t.ReasonType,t.Code,t.Name,t.Direction,
                   t.CounterpartAccountingCategory,NULL,t.RequiresReference,
                   1,1,t.DisplayOrder,SYSDATETIMEOFFSET(),SYSDATETIMEOFFSET()
            FROM dbo.AccountingConfigurationProfiles p
            INNER JOIN dbo.ReasonTemplates t ON t.ProfileCode=p.ProfileCode
            WHERE p.IsDefault=1 AND p.IsActive=1 AND t.IsActive=1;

            INSERT dbo.ProductUnits(
                ProductUnitId,BusinessId,Code,Name,Symbol,
                AllowsFractionalQuantity,DecimalPlaces,IsActive,CreatedAt)
            VALUES
              (NEWID(),@BusinessId,N'EA',N'Unidad',N'und',0,0,1,SYSDATETIMEOFFSET()),
              (NEWID(),@BusinessId,N'KG',N'Kilogramo',N'kg',1,3,1,SYSDATETIMEOFFSET()),
              (NEWID(),@BusinessId,N'M',N'Metro',N'm',1,3,1,SYSDATETIMEOFFSET()),
              (NEWID(),@BusinessId,N'L',N'Litro',N'L',1,3,1,SYSDATETIMEOFFSET());

            INSERT INTO dbo.EnrolledDevices
            (DeviceId, TenantId, Name,
             CredentialSalt, CredentialHash, CredentialIterations, IsActive, CreatedAt)
            VALUES
            (@DeviceId, @TenantId, N'POS permitido',
             @AllowedSalt, @AllowedHash, @AllowedIterations, 1, SYSDATETIMEOFFSET()),
            (@DeniedDeviceId, @TenantId, N'POS sin permiso',
             @DeniedSalt, @DeniedHash, @DeniedIterations, 1, SYSDATETIMEOFFSET());

            INSERT INTO dbo.WorkSessions
            (WorkSessionId, TenantId, BusinessId, WarehouseId, UserId, DeviceId,
             OpenedAt, LastActivityAt, Status)
            VALUES
            (@WorkSessionId, @TenantId, @BusinessId, @WarehouseId, @UserId, @DeviceId,
             SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET(), N'Open');

            INSERT INTO dbo.FiscalAuthorizations
            (FiscalAuthorizationId, BusinessId, AuthorizationNumber, SupplierTaxId,
             Environment, QrValidationUrl, TechnicalKeyVersion, ValidFrom, ValidUntil,
             AuthorizedRangeStart, AuthorizedRangeEnd, IsActive, CreatedAt)
            VALUES
            (@FiscalAuthorizationId, @BusinessId, @AuthorizationNumber, @SupplierTaxId,
             2, @QrValidationUrl, @TechnicalKeyVersion, '2026-01-01', '2028-12-31',
             1, 20000, 1, SYSDATETIMEOFFSET());

            INSERT INTO dbo.FiscalIssuerConfigurations
            (FiscalIssuerConfigurationId,BusinessId,Version,SupplierTaxId,SupplierCheckDigit,
             LegalName,TradeName,TaxLevelCode,TaxSchemeId,TaxSchemeName,IdentificationTypeCode,
             AddressLine,CityCode,CityName,DepartmentCode,DepartmentName,CountryCode,CountryName,
             SoftwareIdentificationCode,SoftwarePinSecretReference,Environment,TestSetId,
             CertificateProvider,CertificateKeyReference,CertificateThumbprint,DianEndpoint,
             TechnicalAnnexVersion,GeneratorVersion,ValidFrom,IsActive,CreatedAt)
            VALUES
            (@FiscalIssuerConfigurationId,@BusinessId,1,@SupplierTaxId,N'7',
             N'EMISOR MAESTRO',N'EMISOR MAESTRO',N'R-99-PN',N'01',N'IVA',N'31',
             N'CL 1 2 3',N'11001',N'Bogotá',N'11',N'Bogotá D.C.',N'CO',N'Colombia',
             N'auraly-test-software',N'env://AURALY_TEST_SOFTWARE_PIN',2,
             '11111111-1111-1111-1111-111111111111',N'Test',N'Test',N'TEST',
             N'https://vpfe-hab.dian.gov.co/WcfDianCustomerServices.svc',N'1.9',N'Auraly.Tests',
             '2026-01-01',1,SYSDATETIMEOFFSET());

            INSERT INTO dbo.DocumentSeries
            (DocumentSeriesId, BusinessId, DeviceId, DocumentType,
             Prefix, SeriesCode, Padding, RangeStart, RangeEnd,
             IsOfflineCapable, IsActive, CreatedAt)
            VALUES
            (@DocumentSeriesId, @BusinessId, @DeviceId, @DocumentType,
             N'VTA', N'03', 8, 1, 99999999, 1, 1, SYSDATETIMEOFFSET()),
            (@OnlineDocumentSeriesId, @BusinessId, NULL, @DocumentType,
             N'VTA', N'00', 8, 1, 99999999, 0, 1, SYSDATETIMEOFFSET()),
            (@OnlineSalesReceiptSeriesId, @BusinessId, NULL, N'SalesReceipt',
             N'CVI', N'00', 8, 1, 99999999, 0, 1, SYSDATETIMEOFFSET()),
            (@GoodsReceiptSeriesId, @BusinessId, NULL, N'GoodsReceipt',
             N'EMC', N'00', 8, 1, 99999999, 0, 1, SYSDATETIMEOFFSET()),
            (@PurchaseOrderSeriesId, @BusinessId, NULL, N'PurchaseOrder',
             N'OCP', N'00', 8, 1, 99999999, 0, 1, SYSDATETIMEOFFSET()),
            (@PurchaseReturnSeriesId, @BusinessId, NULL, N'PurchaseReturn',
             N'DCP', N'00', 8, 1, 99999999, 0, 1, SYSDATETIMEOFFSET()),
            (@SalesReturnSeriesId, @BusinessId, NULL, N'SalesReturn',
             N'DVT', N'00', 8, 1, 99999999, 0, 1, SYSDATETIMEOFFSET()),
            (@SalesDebitNoteSeriesId, @BusinessId, NULL, N'SalesDebitNote',
             N'NDB', N'00', 8, 1, 99999999, 0, 1, SYSDATETIMEOFFSET());

            INSERT INTO dbo.FiscalSeries
            (SeriesId, BusinessId, DeviceId, EmitterKind, FiscalAuthorizationId,
             DocumentType, Prefix, RangeStart, RangeEnd, IsActive, CreatedAt)
            VALUES
            (@SeriesId, @BusinessId, @DeviceId, N'Device', @FiscalAuthorizationId,
             @DocumentType, @Prefix, 1, 10000, 1, SYSDATETIMEOFFSET()),
            (@OnlineSeriesId, @BusinessId, NULL, N'Server', @FiscalAuthorizationId,
             @DocumentType, @Prefix, 10001, 20000, 1, SYSDATETIMEOFFSET());

            INSERT INTO dbo.Products
            (ProductId, TenantId, BusinessId, Source, Sku, Name, Currency, ManageStock, IsActive, CreatedAt)
            VALUES
            (@ProductId, @TenantId, @BusinessId, 0, N'P-E2E', N'Producto E2E', N'COP', 1, 1, SYSUTCDATETIME());

            INSERT dbo.ProductPrices
              (ProductPriceId,BusinessId,ProductId,Amount,CurrencyCode,ValidFrom,
               TargetMarginPercent,RoundingIncrement,RoundingMode,IsActive,CreatedAt)
            VALUES
              (NEWID(),@BusinessId,@ProductId,10000,N'COP','2026-01-01',
               30,1,N'Nearest',1,SYSDATETIMEOFFSET());

            INSERT dbo.Parties
              (PartyId,TenantId,PartyType,DisplayName,LegalName,CompletionStatus,IsActive,CreatedBy,CreatedAt)
            VALUES
              (@GoodsSupplierPartyId,@TenantId,N'Organization',N'Proveedor E2E',N'Proveedor E2E',N'Incomplete',1,@UserId,SYSDATETIMEOFFSET());

            INSERT INTO dbo.Suppliers
              (SupplierId,BusinessId,PartyId,Identification,Name,IsActive,CreatedAt)
            VALUES
              (@GoodsSupplierId,@BusinessId,@GoodsSupplierPartyId,N'900999001',N'Proveedor E2E',1,SYSDATETIMEOFFSET());

            INSERT INTO dbo.SupplierProducts
              (SupplierProductId,BusinessId,ProductId,SupplierId,SupplierProductCode,IsPrimary,IsActive,CreatedAt)
            VALUES
              (NEWID(),@BusinessId,@ProductId,@GoodsSupplierId,N'PROV-P-E2E',1,1,SYSDATETIMEOFFSET());
            """;
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@TenantId", TenantId);
        command.Parameters.AddWithValue("@TenantEmail", $"e2e-{TenantId:N}@auraly.test");
        command.Parameters.AddWithValue("@BusinessId", BusinessId);
        command.Parameters.AddWithValue("@BusinessEmail", $"e2e-{BusinessId:N}@auraly.test");
        command.Parameters.AddWithValue("@UserId", UserId);
        command.Parameters.AddWithValue("@RoleId", RoleId);
        command.Parameters.AddWithValue("@Username", $"cashier-{UserId:N}");
        command.Parameters.AddWithValue("@NormalizedUsername", $"CASHIER-{UserId:N}".ToUpperInvariant());
        command.Parameters.AddWithValue("@UserEmail", $"cashier-{UserId:N}@auraly.test");
        command.Parameters.AddWithValue("@NormalizedUserEmail", $"CASHIER-{UserId:N}@AURALY.TEST");
        command.Parameters.AddWithValue("@WarehouseId", WarehouseId);
        command.Parameters.AddWithValue("@DeviceId", DeviceId);
        command.Parameters.AddWithValue("@WorkSessionId", WorkSessionId);
        command.Parameters.AddWithValue("@OnlineDeviceId", OnlineDeviceId);
        command.Parameters.AddWithValue("@DeniedDeviceId", DeniedDeviceId);
        command.Parameters.AddWithValue("@AllowedSalt", allowedCredential.Salt);
        command.Parameters.AddWithValue("@AllowedHash", allowedCredential.Hash);
        command.Parameters.AddWithValue("@AllowedIterations", allowedCredential.Iterations);
        command.Parameters.AddWithValue("@DeniedSalt", deniedCredential.Salt);
        command.Parameters.AddWithValue("@DeniedHash", deniedCredential.Hash);
        command.Parameters.AddWithValue("@DeniedIterations", deniedCredential.Iterations);
        command.Parameters.AddWithValue("@FiscalAuthorizationId", FiscalAuthorizationId);
        command.Parameters.AddWithValue("@FiscalIssuerConfigurationId", FiscalIssuerConfigurationId);
        command.Parameters.AddWithValue("@AuthorizationNumber", AuthorizationNumber);
        command.Parameters.AddWithValue("@SupplierTaxId", SupplierTaxId);
        command.Parameters.AddWithValue("@TechnicalKeyVersion", TechnicalKeyVersion);
        command.Parameters.AddWithValue("@QrValidationUrl", QrValidationUrl);
        command.Parameters.AddWithValue("@DocumentSeriesId", DocumentSeriesId);
        command.Parameters.AddWithValue("@SeriesId", SeriesId);
        command.Parameters.AddWithValue("@OnlineDocumentSeriesId", OnlineDocumentSeriesId);
        command.Parameters.AddWithValue("@OnlineSalesReceiptSeriesId", OnlineSalesReceiptSeriesId);
        command.Parameters.AddWithValue("@OnlineSeriesId", OnlineSeriesId);
        command.Parameters.AddWithValue("@DocumentType", PosSaleDocumentTypes.Invoice);
        command.Parameters.AddWithValue("@GoodsReceiptSeriesId", GoodsReceiptSeriesId);
        command.Parameters.AddWithValue("@PurchaseOrderSeriesId", PurchaseOrderSeriesId);
        command.Parameters.AddWithValue("@PurchaseReturnSeriesId", PurchaseReturnSeriesId);
        command.Parameters.AddWithValue("@SalesReturnSeriesId", SalesReturnSeriesId);
        command.Parameters.AddWithValue("@SalesDebitNoteSeriesId", SalesDebitNoteSeriesId);
        command.Parameters.AddWithValue("@GoodsSupplierId", SupplierId);
        command.Parameters.AddWithValue("@GoodsSupplierPartyId", SupplierPartyId);
        command.Parameters.AddWithValue("@Prefix", Prefix);
        command.Parameters.AddWithValue("@ProductId", ProductId);
        await command.ExecuteNonQueryAsync();
    }

    private async Task DeployDacpacAsync()
    {
        var root = FindRepositoryRoot();
        var dacpac = Path.Combine(
            root,
            "database",
            "Auraly.Database",
            "bin",
            "Release",
            "Auraly.Database.dacpac");
        if (!File.Exists(dacpac))
        {
            throw new FileNotFoundException(
                "Build Auraly.Database in Release before running integration tests.",
                dacpac);
        }

        EnsureDacpacIsCurrent(root, dacpac);

        var sqlPackage = FindSqlPackage();
        var process = new Process
        {
            StartInfo = new ProcessStartInfo(sqlPackage)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        // SqlPackage can target a newer servicing patch than the SDK used by
        // the application tests. Roll forward only this child process so the
        // TestServer itself continues running on its declared .NET runtime.
        process.StartInfo.Environment["DOTNET_ROLL_FORWARD"] = "LatestMajor";
        process.StartInfo.ArgumentList.Add("/Action:Publish");
        process.StartInfo.ArgumentList.Add($"/SourceFile:{dacpac}");
        process.StartInfo.ArgumentList.Add($"/TargetConnectionString:{ConnectionString}");
        process.StartInfo.ArgumentList.Add("/v:DeploymentEnvironment=dev");
        process.StartInfo.ArgumentList.Add(
            "/v:BootstrapAdminPasswordHash=integration-test-placeholder-not-for-authentication");
        process.StartInfo.ArgumentList.Add("/p:CreateNewDatabase=True");
        process.StartInfo.ArgumentList.Add("/p:DropObjectsNotInSource=False");
        process.StartInfo.ArgumentList.Add("/p:BlockOnPossibleDataLoss=True");
        process.Start();
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            var diagnostic = string.Join(
                Environment.NewLine,
                new[] { await standardOutput, await standardError }
                    .Where(value => !string.IsNullOrWhiteSpace(value)));
            if (diagnostic.Length > 8_000)
                diagnostic = diagnostic[^8_000..];
            throw new InvalidOperationException(
                $"SqlPackage failed with exit code {process.ExitCode} while deploying the isolated SQL Server test database.{Environment.NewLine}{diagnostic}");
        }
    }

    private static void EnsureDacpacIsCurrent(string root, string dacpac)
    {
        var databaseProject = Path.Combine(root, "database", "Auraly.Database");
        var latestSchemaWrite = Directory
            .EnumerateFiles(databaseProject, "*", SearchOption.AllDirectories)
            .Where(path =>
                !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
                !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
                (path.EndsWith(".sql", StringComparison.OrdinalIgnoreCase) ||
                 path.EndsWith(".sqlproj", StringComparison.OrdinalIgnoreCase)))
            .Select(File.GetLastWriteTimeUtc)
            .DefaultIfEmpty(DateTime.MinValue)
            .Max();

        if (File.GetLastWriteTimeUtc(dacpac) < latestSchemaWrite)
        {
            throw new InvalidOperationException(
                "Auraly.Database.dacpac is older than the database schema sources. " +
                "Rebuild Auraly.Database in Release before running integration tests.");
        }
    }

    private static string FindSqlPackage()
    {
        var configured = Environment.GetEnvironmentVariable("SQLPACKAGE_PATH");
        var candidates = new[]
        {
            configured,
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".dotnet",
                "tools",
                "sqlpackage.exe"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Microsoft SQL Server",
                "160",
                "DAC",
                "bin",
                "SqlPackage.exe")
        };
        return candidates.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            ?? throw new FileNotFoundException(
                "SqlPackage was not found. Set SQLPACKAGE_PATH before running SQL integration tests.");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Auraly.Commerce.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate Auraly.Commerce.sln.");
    }
}


internal sealed record TestAuthenticationSession(
    Guid AuthenticationSessionId, Guid ClientId);

internal sealed class TestExecutionAccessRegistry
{
    private readonly ConcurrentDictionary<Guid, IReadOnlyList<string>> permissions = new();

    public void Register(Guid accessProfileId, IEnumerable<string> values) =>
        permissions[accessProfileId] = values.Distinct(StringComparer.Ordinal).ToArray();

    public bool TryGet(Guid accessProfileId, out IReadOnlyList<string> values) =>
        permissions.TryGetValue(accessProfileId, out values!);
}

internal sealed class TestExecutionAccessResolver(
    SqlExecutionContextDirectory sql,
    TestExecutionAccessRegistry registry,
    IHttpContextAccessor httpContextAccessor) : IExecutionAccessResolver
{
    public const string AccessProfileClaim = "test_execution_access_id";

    public async Task<ResolvedExecutionAccess> ResolveAccessAsync(
        Guid userId,
        Guid tenantId,
        Guid? businessId,
        CancellationToken cancellationToken)
    {
        var access = await sql.ResolveAccessAsync(
            userId, tenantId, businessId, cancellationToken);
        if (!access.IsAllowed)
            return access;

        var profileValue = httpContextAccessor.HttpContext?.User
            .FindFirst(AccessProfileClaim)?.Value;
        return Guid.TryParse(profileValue, out var accessProfileId) &&
               registry.TryGet(accessProfileId, out var profilePermissions)
            ? access with { Permissions = profilePermissions }
            : access;
    }
}

internal sealed class TestBlobStorageService : IBlobStorageService
{
    public Task<string> UploadImageAsync(Guid businessId, Stream imageStream, string fileName) =>
        Task.FromResult(fileName);

    public Task<string> GetImageUrlAsync(Guid businessId, string fileName) =>
        Task.FromResult($"https://media.auraly.test/{businessId:D}/{fileName}");

    public Task<bool> ImageExistsAsync(Guid businessId, string fileName) =>
        Task.FromResult(true);
}

internal sealed class TestMediaUrlResolver : IMediaUrlResolver
{
    public Task<string> ResolveAsync(
        Guid businessId,
        string mediaRef,
        CancellationToken ct = default) =>
        Task.FromResult($"https://media.auraly.test/{businessId:D}/{mediaRef}");
}
