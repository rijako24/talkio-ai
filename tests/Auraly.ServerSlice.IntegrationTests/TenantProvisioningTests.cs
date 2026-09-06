using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Auraly.Contracts.Authentication;
using Microsoft.Data.SqlClient;
using Auraly.Contracts.Tenants;
using Auraly.Contracts.TenantBilling;
using Auraly.Platform.Application.Identity.Services;
using Microsoft.Extensions.DependencyInjection;
using Auraly.BuildingBlocks.Domain.Identifiers;
using Auraly.BuildingBlocks.Infrastructure.Identifiers;
using Auraly.BuildingBlocks.Infrastructure.Persistence;
using Auraly.Contracts.Fiscal;
using Auraly.Fiscal.Core;
using Auraly.Infrastructure.Persistence;
using Auraly.Platform.Application.Identity.DTOs;
using Auraly.Platform.Domain.Entities;
using Auraly.Platform.Domain.Enums;

namespace Auraly.ServerSlice.IntegrationTests;

[Collection(ServerSliceCollection.Name)]
public sealed class TenantProvisioningTests(ServerSliceFixture fixture)
{
    private static readonly Guid AuralyTenantId =
        Guid.Parse("A0A10000-0000-0000-0000-000000000000");
    private const string Password = "Auraly-New-Tenant-2026!";

    [Fact]
    public async Task Platform_subscription_list_includes_tenants_pending_commercial_assignment()
    {
        var tenantId = Guid.NewGuid();
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var email = $"legacy-{suffix}@auraly.test";
        await using (var connection = new SqlConnection(fixture.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new SqlCommand("""
                INSERT dbo.Tenants
                  (TenantId,TenantKey,Name,Email,IsActive,MaximumUsers,MaximumEnrolledDevices,CreatedAt)
                VALUES(@TenantId,@TenantKey,@Name,@Email,1,5,1,SYSUTCDATETIME());
                """, connection);
            command.Parameters.AddWithValue("@TenantId", tenantId);
            command.Parameters.AddWithValue("@TenantKey", $"@legacy-{suffix}");
            command.Parameters.AddWithValue("@Name", $"Empresa histórica {suffix}");
            command.Parameters.AddWithValue("@Email", email);
            await command.ExecuteNonQueryAsync();
        }

        using var admin = fixture.CreateAdminClient("tenants.read");
        var page = await admin.GetFromJsonAsync<PlatformTenantSubscriptionPageDto>(
            $"/api/v1/tenants/subscriptions?search={Uri.EscapeDataString(email)}");

        Assert.NotNull(page);
        var item = Assert.Single(page.Items);
        Assert.Equal(tenantId, item.TenantId);
        Assert.Null(item.SubscriptionId);
        Assert.Null(item.PlanCode);
        Assert.Null(item.Status);
    }

    [Fact]
    public async Task Paid_renewal_creates_one_shared_service_invoice_and_no_operational_work()
    {
        await UseCanonicalAuralyBillingBusinessAsync();
        var geography = await ReadGeographyAsync();
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var nit = $"89{Random.Shared.Next(10000000, 99999999)}";
        var request = new ProvisionTenantRequest(
            Guid.NewGuid(), $"Suscriptor {suffix} SAS", $"Suscriptor {suffix}",
            "Organization", "NIT",
            nit, TenantProvisioningRequestValidator.CalculateNitVerificationDigit(nit).ToString(),
            geography.CountryId, geography.DivisionId, geography.CityId,
            "Calle 10", "3001234567", $"billing-{suffix}@auraly.test", "R-99-PN",
            "Sede principal", "Calle 10", "3001234567", $"site-{suffix}@auraly.test",
            "America/Bogota", "LatestReceiptCost", $"owner-{suffix}@auraly.test", 1, 1);
        using var admin = fixture.CreateAdminClient(
            "tenants.create", "tenants.update", "tenants.provisioning.payment.waive");
        using var response = await admin.PostAsJsonAsync("/api/v1/tenants", Waived(request));
        var responseBody = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode,
            $"Expected tenant provisioning success, got {response.StatusCode}: {responseBody}");
        var tenant = (await response.Content.ReadFromJsonAsync<ProvisionTenantResult>())!;

        TenantRenewalOrderDto order;
        using (var scope = fixture.CreateScope())
        {
            order = await scope.ServiceProvider.GetRequiredService<TenantRenewalOrderService>()
                .ReviseAsync(tenant.TenantId, fixture.UserId,
                    new("starter", "Monthly", 0, 0, 0, 3), default);
        }
        var paymentId = Guid.NewGuid();
        var reference = $"TS-{order.RenewalOrderId:N}";
        var cents = checked((long)(order.Quote.PayableAmountCop * 100m));
        Guid billingBusinessId;
        using (var scope = fixture.CreateScope())
        {
            var checkout = scope.ServiceProvider.GetRequiredService<ITenantSubscriptionCheckoutStore>();
            billingBusinessId = await checkout.GetBillingBusinessIdAsync(default);
            await checkout.CreatePaymentAsync(tenant.TenantId, paymentId,
                order.RenewalOrderId, reference, cents,
                DateTimeOffset.UtcNow.AddHours(1), 1, default);
        }
        var fiscal = await ConfigurePlatformBillingFiscalAsync(billingBusinessId);
        await ConfirmPaymentAsync(paymentId, "wompi-renewal-test");
        var payment = new PaymentTransaction
        {
            PaymentTransactionId = paymentId,
            BusinessId = billingBusinessId,
            PaymentReferenceId = reference,
            ProviderTransactionId = "wompi-renewal-test",
            AmountInCents = cents,
            Currency = "COP",
            Status = PaymentTransactionStatus.Confirmed,
            CheckoutKind = CheckoutKind.TenantSubscription,
            SubjectType = "TenantSubscription",
            SubjectId = order.RenewalOrderId
        };
        var settlement = new SqlTenantSubscriptionSettlementService(
            new SqlServerConnectionFactory(new AuralySqlConnectionSource(fixture.ConnectionString)),
            new Uuid7AuralyIdGenerator(TimeProvider.System), TimeProvider.System,
            new FixedFiscalTechnicalKeyProvider(fiscal));

        var first = await settlement.SettleAsync(payment, default);
        var replay = await settlement.SettleAsync(payment, default);

        Assert.Equal(first.DocumentId, replay.DocumentId);
        Assert.True(replay.IsReplay);
        await using var connection = new SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("""
            SELECT
              (SELECT COUNT(*) FROM dbo.SalesDocuments WHERE DocumentId=@DocumentId AND DocumentType=N'ServiceInvoice' AND WarehouseId IS NULL AND DeviceId IS NULL),
              (SELECT COUNT(*) FROM sales.SalesDocumentServiceLines WHERE DocumentId=@DocumentId),
              (SELECT COUNT(*) FROM dbo.SalesDocumentLines WHERE DocumentId=@DocumentId),
              (SELECT COUNT(*) FROM dbo.DocumentProcessingJobs WHERE DocumentId=@DocumentId),
              (SELECT COUNT(*) FROM dbo.InventoryMovements WHERE DocumentId=@DocumentId),
              (SELECT COUNT(*) FROM dbo.AccountingPostingJobs WHERE SourceDocumentId=@DocumentId),
              (SELECT COUNT(*) FROM reporting.SalesReportingJobs WHERE SourceDocumentId=@DocumentId),
              (SELECT COUNT(*) FROM dbo.FiscalDocumentProcesses WHERE DocumentId=@DocumentId),
              (SELECT DianDocumentMonthlyLimit FROM billing.TenantSubscriptions WHERE TenantId=@TenantId);
            """, connection);
        command.Parameters.AddWithValue("@DocumentId", first.DocumentId);
        command.Parameters.AddWithValue("@TenantId", tenant.TenantId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1, reader.GetInt32(0));
        Assert.Equal(2, reader.GetInt32(1));
        Assert.Equal(0, reader.GetInt32(2));
        Assert.Equal(0, reader.GetInt32(3));
        Assert.Equal(0, reader.GetInt32(4));
        Assert.Equal(1, reader.GetInt32(5));
        Assert.Equal(1, reader.GetInt32(6));
        Assert.Equal(1, reader.GetInt32(7));
        Assert.Equal(3_100, reader.GetInt32(8));
    }

    [Fact]
    public async Task Provision_and_accept_invitation_creates_a_complete_usable_tenant()
    {
        var geography = await ReadGeographyAsync();
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var email = $"admin-{suffix}@auraly.test";
        var nit = $"90{Random.Shared.Next(10000000, 99999999)}";
        var request = new ProvisionTenantRequest(
            Guid.NewGuid(), $"Empresa {suffix} SAS", $"Empresa {suffix}",
            "Organization", "NIT",
            nit, TenantProvisioningRequestValidator.CalculateNitVerificationDigit(nit).ToString(),
            geography.CountryId, geography.DivisionId, geography.CityId,
            "Calle 1 # 2-3", "3001234567", $"empresa-{suffix}@auraly.test", "R-99-PN",
            "Sede principal", "Calle 1 # 2-3", "3001234567", $"sede-{suffix}@auraly.test",
            "America/Bogota", "LatestReceiptCost", email, 10, 3);

        var platformUserId = await CreatePlatformAdministratorAsync();
        using var admin = fixture.CreateTenantUserClient(
            AuralyTenantId, platformUserId,
            "tenants.create", "tenants.update", "tenants.read", "users.create",
            "tenants.provisioning.payment.waive");
        using var created = await admin.PostAsJsonAsync("/api/v1/tenants", Waived(request));
        var creationBody = await created.Content.ReadAsStringAsync();
        Assert.True(created.StatusCode == HttpStatusCode.Created, $"Expected Created but received {created.StatusCode}: {creationBody}");
        var result = await created.Content.ReadFromJsonAsync<ProvisionTenantResult>();
        Assert.NotNull(result);

        Assert.Null(result!.AdministratorUserId);
        var state = await ReadProvisionedStateAsync(result.TenantId, null);
        Assert.Equal(1, state.Businesses);
        Assert.Equal(2, state.Warehouses);
        Assert.True(state.InventoryReasons >= 12);
        Assert.Equal(4, state.ProductUnits);
        Assert.Equal(1, state.DefaultCustomers);
        Assert.Equal(57, state.AccountingAccounts);
        Assert.Equal(57, state.AccountingMappings);
        Assert.Equal(1, await CountGeneralCashAccountsAsync(result.TenantId));
        Assert.Equal(0, state.UnmappedPosPaymentMethods);
        Assert.Equal(1, state.OpenAccountingPeriods);
        Assert.Equal(1, state.DefaultCostCenters);
        Assert.Equal(1, state.AccountingVoucherCursors);
        Assert.Equal("Configuring", await ReadAccountingStatusAsync(result.TenantId));
        Assert.Equal(6, state.Roles);
        Assert.Equal(3, state.OnlineSalesDocumentSeries);
        Assert.True(await RoleHasPermissionAsync(
            result.TenantId, "SUPERVISOR", "pos.approvals.receive_notifications"));
        Assert.False(await RoleHasPermissionAsync(
            result.TenantId, "CASHIER", "pos.approvals.receive_notifications"));
        Assert.True(await RoleHasPermissionAsync(
            result.TenantId, "CASHIER", "pos.synchronization.events.read"));
        Assert.True(await RoleHasPermissionAsync(
            result.TenantId, "CASHIER", "pos.inventory.availability.read"));
        Assert.False(await RoleHasPermissionAsync(
            result.TenantId, "CASHIER", "inventory.read"));
        Assert.False(await RoleHasPermissionAsync(
            result.TenantId, "CASHIER", "fiscal.configuration.read"));
        foreach (var permission in new[]
                 {
                     "agents.read", "agents.update", "conversations.read",
                     "leads.read", "campaigns.read", "reservations.read"
                 })
        {
            Assert.False(await RoleHasPermissionAsync(
                result.TenantId, "ADMINISTRATOR", permission));
            Assert.False(await RoleHasPermissionAsync(
                result.TenantId, "ADMINISTRATIVE", permission));
        }
        foreach (var permission in new[]
                 {
                     "accounting.configure", "accounting.manual.create",
                     "payroll.approve", "payroll.pay", "payables.payments.create",
                     "receivables.payments.create", "expenses.create",
                     "commerce.taxation.withholdings.manage",
                     "inventory.reasons.manage", "work-sessions.differences.read"
                 })
            Assert.True(await RoleHasPermissionAsync(
                result.TenantId, "ACCOUNTANT", permission),
                $"The provisioned accountant role is missing '{permission}'.");
        Assert.False(await RoleHasPermissionAsync(
            result.TenantId, "ACCOUNTANT", "users.assign_role"));
        Assert.False(await RoleHasPermissionAsync(
            result.TenantId, "ACCOUNTANT", "inventory.adjustments.confirm"));
        Assert.Equal(0, await MissingAdministratorPermissionsAsync(result.TenantId));
        Assert.Equal(0, await AccountantPermissionMismatchAsync(result.TenantId));
        Assert.Equal(0, state.UserRoles);
        Assert.Equal(0, await CountTenantUsersAsync(result.TenantId));
        Assert.Equal(new ProvisionedPrincipals(0, 1, 1, 0, 1),
            await ReadProvisionedPrincipalsAsync(result.TenantId));
        Assert.Null(state.UserActive);
        Assert.Null(state.PasswordHash);
        Assert.Equal("Pending", state.InvitationStatus);
        var commercial = await ReadCommercialSubscriptionAsync(result.TenantId);
        Assert.Equal("business", commercial.PlanCode);
        Assert.Equal("Annual", commercial.BillingPeriod);
        Assert.Equal("Active", commercial.Status);
        Assert.Equal(8, commercial.FullUsers);
        Assert.Equal(0, commercial.SellerUsers);
        Assert.Equal(3, commercial.PosDevices);
        Assert.Equal(1_500, commercial.DianDocuments);
        Assert.Equal(30, commercial.PayrollEmployees);
        Assert.Equal(1, commercial.UsagePeriods);

        using var attemptedKeyChange = await admin.PutAsJsonAsync(
            $"/api/v1/tenants/{result.TenantId:D}",
            new { tenantKey = $"@changed-{suffix}" });
        Assert.Equal(HttpStatusCode.OK, attemptedKeyChange.StatusCode);
        var unchangedTenant = await attemptedKeyChange.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(result.TenantKey, unchangedTenant.GetProperty("tenantKey").GetString());

        var naturalIdentification = $"10{Random.Shared.NextInt64(10000000, 99999999)}";
        using (var updated = await admin.PutAsJsonAsync(
                   $"/api/v1/tenants/{result.TenantId:D}",
                   new
                   {
                       Name = $"Persona {suffix}",
                       LegalName = $"Persona Natural {suffix}",
                       Nit = naturalIdentification,
                       VerificationDigit = (string?)null,
                       EntityType = "NaturalPerson",
                       IdentificationTypeCode = "CC"
                   }))
        {
            updated.EnsureSuccessStatusCode();
            var value = await updated.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("NaturalPerson", value.GetProperty("entityType").GetString());
            Assert.Equal("CC", value.GetProperty("identificationTypeCode").GetString());
            Assert.Equal(naturalIdentification, value.GetProperty("nit").GetString());
            Assert.Equal(JsonValueKind.Null, value.GetProperty("verificationDigit").ValueKind);
        }

        using (var logo = new MultipartFormDataContent())
        using (var image = new ByteArrayContent([0x89, 0x50, 0x4E, 0x47]))
        {
            image.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
            logo.Add(image, "file", "marca.png");
            using var uploaded = await admin.PostAsync(
                $"/api/v1/tenants/{result.TenantId:D}/logo", logo);
            uploaded.EnsureSuccessStatusCode();
            var value = await uploaded.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Contains("tenant-branding", value.GetProperty("logoUrl").GetString());
        }

        var token = await ReadInvitationTokenAsync(result.TenantId);
        var invitationWindow = await ReadInvitationWindowAsync(result.TenantId);
        Assert.Equal(TimeSpan.FromDays(3), invitationWindow.ExpiresAt - invitationWindow.CreatedAt);
        var username = $"admin-{suffix}";
        using var publicClient = fixture.CreateClient();
        await ExpireInvitationAsync(result.TenantId);
        using (var expired = await publicClient.PostAsJsonAsync(
                   "/api/v1/auth/invitations/accept",
                   new AcceptTenantInvitationRequest(
                       token, "CC", $"10{Random.Shared.Next(10000000, 99999999)}",
                       "Administrador", suffix, username, "3007654321", "Calle 1 # 2-3",
                       Password, Password)))
        {
            Assert.Equal(HttpStatusCode.Gone, expired.StatusCode);
            Assert.Contains("expiró", await expired.Content.ReadAsStringAsync(),
                StringComparison.OrdinalIgnoreCase);
        }
        admin.DefaultRequestHeaders.Add("X-Tenant-Id", result.TenantId.ToString("D"));
        using (var resent = await admin.PostAsync(
                   $"/api/v1/tenants/{result.TenantId:D}/administrator-invitation/resend",
                   null))
        {
            resent.EnsureSuccessStatusCode();
            var value = await resent.Content.ReadFromJsonAsync<ResendTenantInvitationResult>();
            Assert.NotNull(value);
            Assert.Equal(email, value!.DeliveryEmail);
            Assert.True(value.ExpiresAt >= DateTimeOffset.UtcNow.AddDays(3).AddMinutes(-1));
            Assert.Equal("Pending", value.Status);
        }
        Assert.Equal(token, await ReadInvitationTokenAsync(result.TenantId));
        Assert.Equal(2, await CountInvitationDeliveriesAsync(result.TenantId));
        using var accepted = await publicClient.PostAsJsonAsync(
            "/api/v1/auth/invitations/accept",
            new AcceptTenantInvitationRequest(
                token, "CC", $"10{Random.Shared.Next(10000000, 99999999)}",
                "Administrador", suffix, username, "3007654321", "Calle 1 # 2-3",
                Password, Password));
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

        var acceptedResult = await accepted.Content.ReadFromJsonAsync<AcceptTenantInvitationResult>();
        Assert.NotNull(acceptedResult);
        Assert.Equal(username, acceptedResult!.Username);
        Assert.Equal(result.TenantKey, acceptedResult.TenantKey);
        var acceptedState = await ReadProvisionedStateAsync(result.TenantId, acceptedResult!.UserId);
        Assert.True(acceptedState.UserActive);
        Assert.NotNull(acceptedState.PasswordHash);
        Assert.Equal("Accepted", acceptedState.InvitationStatus);
        Assert.Equal(1, await CountTenantUsersAsync(result.TenantId));
        Assert.Equal(new ProvisionedPrincipals(1, 1, 1, 0, 2),
            await ReadProvisionedPrincipalsAsync(result.TenantId));

        using (var scope = fixture.CreateScope())
        {
            var renewal = scope.ServiceProvider.GetRequiredService<TenantRenewalOrderService>();
            var firstOrder = await renewal.ReviseAsync(
                result.TenantId, acceptedResult.UserId,
                new TenantQuoteRequest("business", "Annual", 0, 0, 0, 0), default);
            Assert.Equal(1, firstOrder.Revision);
            Assert.Equal("Draft", firstOrder.Status);
            Assert.Equal(8, firstOrder.Quote.FullUserLimit);
            Assert.Equal(1, firstOrder.Usage.FullUsers);

            var revisedOrder = await renewal.ReviseAsync(
                result.TenantId, acceptedResult.UserId,
                new TenantQuoteRequest("corporate", "Annual", 1, 1, 1, 1, 1), default);
            Assert.Equal(2, revisedOrder.Revision);
            Assert.Equal("corporate", revisedOrder.Quote.PlanCode);
            Assert.Equal(13, revisedOrder.Quote.FullUserLimit);
            Assert.Equal(6, revisedOrder.Quote.PosDeviceLimit);
            Assert.Equal(4_000, revisedOrder.Quote.DianDocumentMonthlyLimit);
            Assert.Equal(110, revisedOrder.Quote.PayrollEmployeeLimit);
            Assert.Equal(1, await CountCurrentRenewalOrdersAsync(result.TenantId));
            Assert.Equal(0, await CountRenewalOrderSideEffectsAsync(result.TenantId));

            await AddActiveUsersAsync(result.TenantId, 3);
            var invalidReduction = () => renewal.ReviseAsync(
                result.TenantId, acceptedResult.UserId,
                new TenantQuoteRequest("essential", "Monthly", 0, 0, 0, 0), default);
            var reductionError = await Assert.ThrowsAsync<ArgumentException>(invalidReduction);
            Assert.Contains("usuarios completos", reductionError.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(2, (await renewal.GetCurrentAsync(result.TenantId, default))!.Revision);
        }

        using var reused = await publicClient.PostAsJsonAsync(
            "/api/v1/auth/invitations/accept",
            new AcceptTenantInvitationRequest(
                token, "CC", $"10{Random.Shared.Next(10000000, 99999999)}",
                "Administrador", suffix, username, "3007654321", "Calle 1 # 2-3",
                Password, Password));
        Assert.Equal(HttpStatusCode.Conflict, reused.StatusCode);

        using var resendAccepted = await admin.PostAsync(
            $"/api/v1/tenants/{result.TenantId:D}/administrator-invitation/resend",
            null);
        var resendAcceptedBody = await resendAccepted.Content.ReadAsStringAsync();
        Assert.True(
            resendAccepted.StatusCode == HttpStatusCode.Conflict,
            $"Expected Conflict but received {resendAccepted.StatusCode}: {resendAcceptedBody}");

        using var loginRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
        {
            Content = JsonContent.Create(new AuthenticationLoginRequest(username, result.TenantKey, Password))
        };
        loginRequest.Headers.Add(AuthenticationDefaults.ClientIdHeader, Guid.NewGuid().ToString("D"));
        using var login = await publicClient.SendAsync(loginRequest);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var authentication = await login.Content.ReadFromJsonAsync<AuthenticationResponse>();
        Assert.NotNull(authentication);
        Assert.Equal(result.TenantId, authentication!.User.TenantId);
        Assert.Equal(acceptedResult.UserId, authentication.User.UserId);
        Assert.Contains("Administrador", authentication.User.Roles);
        Assert.DoesNotContain(authentication.User.Permissions, permission =>
            permission.StartsWith("tenants.", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(authentication.User.Permissions, permission =>
            permission.StartsWith("platform.", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(authentication.User.Permissions, permission =>
            new[] { "agents.", "conversations.", "leads.", "campaigns.", "reservations." }
                .Any(prefix => permission.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(
            await TenantAdministratorPermissionCountAsync(),
            authentication.User.Permissions.Count);
    }

    [Fact]
    public async Task Administrator_activation_reuses_an_existing_customer_party_in_the_same_tenant()
    {
        var geography = await ReadGeographyAsync();
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var nit = $"9{Random.Shared.NextInt64(100000000, 999999999)}";
        var invitationEmail = $"kevin-{suffix}@auraly.test";
        var identification = $"10{Random.Shared.NextInt64(100000000, 999999999)}";
        var request = new ProvisionTenantRequest(
            Guid.NewGuid(), $"Migrada {suffix} SAS", $"Migrada {suffix}",
            "Organization", "NIT", nit,
            TenantProvisioningRequestValidator.CalculateNitVerificationDigit(nit).ToString(),
            geography.CountryId, geography.DivisionId, geography.CityId,
            "Calle empresa", "3001234567", $"empresa-{suffix}@auraly.test", "R-99-PN",
            "Sede principal", "Calle empresa", "3001234567", $"sede-{suffix}@auraly.test",
            "America/Bogota", "LatestReceiptCost", invitationEmail, 10, 3);
        using var admin = fixture.CreateAdminClient(
            "tenants.create", "tenants.update", "tenants.provisioning.payment.waive");
        using var provisioned = await admin.PostAsJsonAsync("/api/v1/tenants", Waived(request));
        provisioned.EnsureSuccessStatusCode();
        var tenant = await provisioned.Content.ReadFromJsonAsync<ProvisionTenantResult>()
            ?? throw new InvalidOperationException("Tenant provisioning response is missing.");
        var token = await ReadInvitationTokenAsync(tenant.TenantId);
        var existingPartyId = Guid.NewGuid();

        await using (var connection = new SqlConnection(fixture.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new SqlCommand("""
                INSERT dbo.Parties
                  (PartyId,TenantId,PartyType,IdentificationCountryId,IdentificationTypeCode,
                   Identification,NormalizedIdentification,DisplayName,FirstName,LastName,
                   CompletionStatus,IsActive,CreatedBy,CreatedAt)
                VALUES(@PartyId,@TenantId,N'NaturalPerson',@CountryId,N'CC',@Identification,
                       @Identification,N'Kevin cliente migrado',N'Kevin',N'Cliente',N'Complete',1,@ActorId,@Now);
                INSERT dbo.PartyContacts
                  (PartyContactId,PartyId,ContactType,Value,NormalizedValue,IsPrimary,IsActive,CreatedAt)
                VALUES(NEWID(),@PartyId,N'Email',N'correo-anterior@auraly.test',N'CORREO-ANTERIOR@AURALY.TEST',1,1,@Now);
                INSERT dbo.PartySites
                  (PartySiteId,PartyId,Code,Name,CountryId,AdministrativeDivisionId,CityId,
                   AddressLine,IsPrimary,IsActive,CreatedBy,CreatedAt)
                VALUES(NEWID(),@PartyId,N'PRINCIPAL',N'Principal',@CountryId,@DivisionId,@CityId,
                       N'Dirección anterior',1,1,@ActorId,@Now);
                INSERT dbo.Customers
                  (CustomerId,PartyId,BusinessId,RequiresElectronicInvoice,IsActive,CreatedBy,CreatedAt)
                VALUES(NEWID(),@PartyId,@BusinessId,0,1,@ActorId,@Now);
                """, connection);
            command.Parameters.AddWithValue("@PartyId", existingPartyId);
            command.Parameters.AddWithValue("@TenantId", tenant.TenantId);
            command.Parameters.AddWithValue("@CountryId", geography.CountryId);
            command.Parameters.AddWithValue("@DivisionId", geography.DivisionId);
            command.Parameters.AddWithValue("@CityId", geography.CityId);
            command.Parameters.AddWithValue("@Identification", identification);
            command.Parameters.AddWithValue("@BusinessId", tenant.BusinessId);
            command.Parameters.AddWithValue("@ActorId", fixture.UserId);
            command.Parameters.AddWithValue("@Now", DateTimeOffset.UtcNow);
            await command.ExecuteNonQueryAsync();
        }

        using var publicClient = fixture.CreateClient();
        using var accepted = await publicClient.PostAsJsonAsync(
            "/api/v1/auth/invitations/accept",
            new AcceptTenantInvitationRequest(
                token, "CC", identification, "Kevin", "Ramírez",
                $"kevin-{suffix}", "3007654321", "Calle nueva # 1-2",
                Password, Password));
        var acceptedBody = await accepted.Content.ReadAsStringAsync();
        Assert.True(accepted.StatusCode == HttpStatusCode.OK,
            $"Expected OK but received {accepted.StatusCode}: {acceptedBody}");
        var receipt = JsonSerializer.Deserialize<AcceptTenantInvitationResult>(
            acceptedBody, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException("Invitation acceptance response is missing.");

        await using (var connection = new SqlConnection(fixture.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new SqlCommand("""
                SELECT
                  (SELECT COUNT(*) FROM dbo.Parties WHERE TenantId=@TenantId AND NormalizedIdentification=@Identification),
                  (SELECT PartyId FROM dbo.AppUsers WHERE UserId=@UserId),
                  (SELECT COUNT(*) FROM dbo.Customers WHERE PartyId=@PartyId AND BusinessId=@BusinessId AND IsActive=1),
                  (SELECT COUNT(*) FROM dbo.UserRoles ur JOIN dbo.AppRoles r ON r.RoleId=ur.RoleId
                   WHERE ur.UserId=@UserId AND r.NormalizedName=N'ADMINISTRATOR'),
                  (SELECT COUNT(*) FROM dbo.PartyContacts WHERE PartyId=@PartyId AND ContactType=N'Email'
                   AND NormalizedValue=@Email AND IsPrimary=1 AND IsActive=1),
                  (SELECT COUNT(*) FROM dbo.PartySites WHERE PartyId=@PartyId AND AddressLine=N'Calle nueva # 1-2'
                   AND IsPrimary=1 AND IsActive=1);
                """, connection);
            command.Parameters.AddWithValue("@TenantId", tenant.TenantId);
            command.Parameters.AddWithValue("@Identification", identification);
            command.Parameters.AddWithValue("@UserId", receipt.UserId);
            command.Parameters.AddWithValue("@PartyId", existingPartyId);
            command.Parameters.AddWithValue("@BusinessId", tenant.BusinessId);
            command.Parameters.AddWithValue("@Email", invitationEmail.ToUpperInvariant());
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(1, reader.GetInt32(0));
            Assert.Equal(existingPartyId, reader.GetGuid(1));
            Assert.Equal(1, reader.GetInt32(2));
            Assert.Equal(1, reader.GetInt32(3));
            Assert.Equal(1, reader.GetInt32(4));
            Assert.Equal(1, reader.GetInt32(5));
        }
    }

    [Fact]
    public async Task Administrator_email_and_identification_are_unique_per_tenant()
    {
        var geography = await ReadGeographyAsync();
        var sharedEmail = $"shared-admin-{Guid.NewGuid():N}@auraly.test";
        var sharedIdentification = $"10{Random.Shared.NextInt64(100000000, 999999999)}";
        using var admin = fixture.CreateAdminClient("tenants.create", "tenants.update", "tenants.provisioning.payment.waive");

        async Task<(ProvisionTenantResult Result, string Token)> ProvisionAsync(string suffix)
        {
            var nit = $"9{Random.Shared.NextInt64(100000000, 999999999)}";
            var request = new ProvisionTenantRequest(
                Guid.NewGuid(), $"Empresa {suffix} SAS", $"Empresa {suffix}",
                "Organization", "NIT",
                nit, TenantProvisioningRequestValidator.CalculateNitVerificationDigit(nit).ToString(),
                geography.CountryId, geography.DivisionId, geography.CityId,
                "Calle 1 # 2-3", "3001234567", $"empresa-{suffix}@auraly.test", "R-99-PN",
                "Sede principal", "Calle 1 # 2-3", "3001234567", $"sede-{suffix}@auraly.test",
                "America/Bogota", "LatestReceiptCost", sharedEmail, 10, 3);
            using var response = await admin.PostAsJsonAsync("/api/v1/tenants", Waived(request));
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(
                response.StatusCode == HttpStatusCode.Created,
                $"Expected Created but received {response.StatusCode}: {body}");
            var result = await response.Content.ReadFromJsonAsync<ProvisionTenantResult>();
            Assert.NotNull(result);
            return (result!, await ReadInvitationTokenAsync(result!.TenantId));
        }

        var first = await ProvisionAsync($"one-{Guid.NewGuid():N}"[..14]);
        var second = await ProvisionAsync($"two-{Guid.NewGuid():N}"[..14]);
        Assert.NotEqual(first.Result.TenantId, second.Result.TenantId);

        using var publicClient = fixture.CreateClient();
        async Task<AcceptTenantInvitationResult> AcceptAsync(string token, string lastName)
        {
            using var response = await publicClient.PostAsJsonAsync(
                "/api/v1/auth/invitations/accept",
                new AcceptTenantInvitationRequest(
                    token, "CC", sharedIdentification, "Administrador", lastName,
                    "shared-admin", "3007654321", "Calle 1 # 2-3", Password, Password));
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(
                response.StatusCode == HttpStatusCode.OK,
                $"Expected OK but received {response.StatusCode}: {body}");
            return await response.Content.ReadFromJsonAsync<AcceptTenantInvitationResult>()
                ?? throw new InvalidOperationException("Invitation acceptance response is missing.");
        }

        var firstAccepted = await AcceptAsync(first.Token, "Primer tenant");
        var secondAccepted = await AcceptAsync(second.Token, "Segundo tenant");
        Assert.True((await ReadProvisionedStateAsync(
            first.Result.TenantId, firstAccepted.UserId)).UserActive);
        Assert.True((await ReadProvisionedStateAsync(
            second.Result.TenantId, secondAccepted.UserId)).UserActive);
    }

    private static WaivedTenantProvisioningRequest Waived(ProvisionTenantRequest request) =>
        new(request, new TenantQuoteRequest("business", "Annual", 0, 0, 0, 0));
    [Fact]
    public async Task Creating_any_later_business_also_provisions_sales_and_orders_warehouses()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        using var admin = fixture.CreateAdminClient("businesses.create");
        using var response = await admin.PostAsJsonAsync(
            "/api/v1/businesses",
            new
            {
                Name = $"Sede norte {suffix}",
                Description = "Segunda sede",
                Address = "Carrera 2 # 3-4",
                Phone = "3000000000",
                Email = $"norte-{suffix}@auraly.test",
                TimeZone = "America/Bogota"
            });
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(
            response.StatusCode == HttpStatusCode.Created,
            $"Expected Created but received {response.StatusCode}: {body}");
        var business = await response.Content.ReadFromJsonAsync<BusinessCreatedResponse>();
        Assert.NotNull(business);
        Assert.Equal(2, await CountDefaultWarehousesAsync(fixture.TenantId, business!.BusinessId));
        Assert.Equal(1, await CountDefaultCostCentersAsync(fixture.TenantId, business.BusinessId));
        Assert.Equal(3, await CountOnlineSalesDocumentSeriesAsync(fixture.TenantId, business.BusinessId));
    }

    private async Task<(Guid CountryId, Guid DivisionId, Guid CityId)> ReadGeographyAsync()
    {
        await using var connection = new SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("""
            SELECT TOP(1) c.CountryId,d.AdministrativeDivisionId,ci.CityId
            FROM dbo.Countries c
            INNER JOIN dbo.AdministrativeDivisions d ON d.CountryId=c.CountryId AND d.IsActive=1
            INNER JOIN dbo.Cities ci ON ci.AdministrativeDivisionId=d.AdministrativeDivisionId AND ci.IsActive=1
            WHERE c.IsActive=1 ORDER BY c.Name,d.Name,ci.Name;
            """, connection);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2));
    }

    private async Task<ProvisionedState> ReadProvisionedStateAsync(Guid tenantId, Guid? userId)
    {
        await using var connection = new SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("""
            SELECT
              (SELECT COUNT(*) FROM dbo.Businesses WHERE TenantId=@TenantId),
              (SELECT COUNT(*) FROM dbo.Warehouses w INNER JOIN dbo.Businesses b ON b.BusinessId=w.BusinessId WHERE b.TenantId=@TenantId AND w.Code IN(N'VEN',N'PED')),
              (SELECT COUNT(*) FROM dbo.BusinessReasons r INNER JOIN dbo.Businesses b ON b.BusinessId=r.BusinessId WHERE b.TenantId=@TenantId),
              (SELECT COUNT(*) FROM dbo.ProductUnits u INNER JOIN dbo.Businesses b ON b.BusinessId=u.BusinessId WHERE b.TenantId=@TenantId),
              (SELECT COUNT(*) FROM dbo.Customers c INNER JOIN dbo.Parties p ON p.PartyId=c.PartyId WHERE p.TenantId=@TenantId AND p.DisplayName=N'Consumidor final'),
              (SELECT COUNT(*) FROM dbo.AccountingAccounts WHERE TenantId=@TenantId),
              (SELECT COUNT(*) FROM dbo.AccountingAccountMappings WHERE TenantId=@TenantId AND BusinessId IS NULL),
              (SELECT COUNT(*) FROM dbo.AccountingPeriods WHERE TenantId=@TenantId AND Status=N'Open'),
              (SELECT COUNT(*) FROM dbo.AccountingCostCenters c INNER JOIN dbo.Businesses b ON b.BusinessId=c.BusinessId WHERE b.TenantId=@TenantId AND c.IsDefault=1 AND c.IsActive=1),
              (SELECT COUNT(*) FROM dbo.AccountingVoucherCursors WHERE TenantId=@TenantId),
              (SELECT COUNT(*) FROM reference.Options optionValue
               WHERE optionValue.CatalogCode=N'payment-method' AND optionValue.IsActive=1
                 AND NOT EXISTS(
                   SELECT 1
                   FROM dbo.AccountingConfigurationProfiles profile
                   INNER JOIN dbo.AccountingSourceCategoryMappings sourceMapping
                     ON sourceMapping.ProfileCode=profile.ProfileCode
                    AND sourceMapping.SourceType=N'PosPaymentMethod'
                    AND sourceMapping.SourceCode=optionValue.Code
                   INNER JOIN dbo.AccountingAccountMappings accountMapping
                     ON accountMapping.TenantId=@TenantId
                    AND accountMapping.Category=sourceMapping.Category
                    AND accountMapping.EffectiveTo IS NULL
                   WHERE profile.IsDefault=1 AND profile.IsActive=1)),
              (SELECT COUNT(*) FROM dbo.AppRoles WHERE TenantId=@TenantId AND NormalizedName IN(N'CASHIER',N'SUPERVISOR',N'SELLER',N'ADMINISTRATIVE',N'ACCOUNTANT',N'ADMINISTRATOR')),
              (SELECT COUNT(*) FROM dbo.DocumentSeries ds INNER JOIN dbo.Businesses b ON b.BusinessId=ds.BusinessId WHERE b.TenantId=@TenantId AND ds.DocumentType IN(N'SalesInvoice',N'SalesReceipt',N'SalesDebitNote') AND ds.DeviceId IS NULL AND ds.SeriesCode=N'00' AND ds.IsActive=1),
              (SELECT COUNT(*) FROM dbo.UserRoles WHERE UserId=@UserId),
              (SELECT IsActive FROM dbo.AppUsers WHERE TenantId=@TenantId AND UserId=@UserId),
              (SELECT PasswordHash FROM dbo.AppUsers WHERE TenantId=@TenantId AND UserId=@UserId),
              (SELECT TOP(1) Status FROM dbo.TenantUserInvitations WHERE TenantId=@TenantId ORDER BY CreatedAt DESC);
            """, connection);
        command.Parameters.AddWithValue("@TenantId", tenantId);
        command.Parameters.AddWithValue("@UserId", userId.HasValue ? userId.Value : DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new ProvisionedState(
            reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3),
            reader.GetInt32(4), reader.GetInt32(5), reader.GetInt32(6), reader.GetInt32(7),
            reader.GetInt32(8), reader.GetInt32(9), reader.GetInt32(10), reader.GetInt32(11),
            reader.GetInt32(12), reader.GetInt32(13), reader.IsDBNull(14) ? null : reader.GetBoolean(14),
            reader.IsDBNull(15) ? null : reader.GetString(15), reader.GetString(16));
    }

    private async Task<string> ReadInvitationTokenAsync(Guid tenantId)
    {
        await using var connection = new SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("""
            SELECT TOP(1) Payload FROM dbo.TenantProvisioningOutboxMessages
            WHERE TenantId=@TenantId AND Type=N'TenantAdministratorInvitation' ORDER BY OccurredAt DESC;
            """, connection);
        command.Parameters.AddWithValue("@TenantId", tenantId);
        var payload = (string?)await command.ExecuteScalarAsync();
        Assert.False(string.IsNullOrWhiteSpace(payload));
        using var json = JsonDocument.Parse(payload!);
        return json.RootElement.GetProperty("activationToken").GetString()
            ?? throw new InvalidOperationException("Invitation token is missing.");
    }

    private async Task<(DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt)>
        ReadInvitationWindowAsync(Guid tenantId)
    {
        await using var connection = new SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("""
            SELECT TOP(1) CreatedAt,ExpiresAt
            FROM dbo.TenantUserInvitations
            WHERE TenantId=@TenantId ORDER BY CreatedAt DESC;
            """, connection);
        command.Parameters.AddWithValue("@TenantId", tenantId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetFieldValue<DateTimeOffset>(0),
            reader.GetFieldValue<DateTimeOffset>(1));
    }

    private async Task ExpireInvitationAsync(Guid tenantId)
    {
        await using var connection = new SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("""
            UPDATE dbo.TenantUserInvitations
            SET ExpiresAt=DATEADD(minute,-1,SYSDATETIMEOFFSET())
            WHERE TenantId=@TenantId AND Status=N'Pending';
            """, connection);
        command.Parameters.AddWithValue("@TenantId", tenantId);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private async Task<int> CountInvitationDeliveriesAsync(Guid tenantId)
    {
        await using var connection = new SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("""
            SELECT COUNT(*) FROM dbo.TenantProvisioningOutboxMessages
            WHERE TenantId=@TenantId AND Type=N'TenantAdministratorInvitation';
            """, connection);
        command.Parameters.AddWithValue("@TenantId", tenantId);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private async Task<int> CountGeneralCashAccountsAsync(Guid tenantId)
    {
        await using var connection = new SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("""
            SELECT COUNT(*)
            FROM dbo.AccountingAccounts account
            INNER JOIN dbo.AccountingAccountMappings mapping
              ON mapping.TenantId=account.TenantId
             AND mapping.AccountId=account.AccountId
             AND mapping.BusinessId IS NULL
             AND mapping.Category=N'Cash'
             AND mapping.EffectiveTo IS NULL
            WHERE account.TenantId=@TenantId
              AND account.Code=N'110505'
              AND account.Name=N'Caja general'
              AND account.IsActive=1
              AND account.AllowsPosting=1;
            """, connection);
        command.Parameters.AddWithValue("@TenantId", tenantId);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private async Task<string> ReadAccountingStatusAsync(Guid tenantId)
    {
        await using var connection = new SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT Status FROM dbo.AccountingTenantSettings WHERE TenantId=@TenantId;",
            connection);
        command.Parameters.AddWithValue("@TenantId", tenantId);
        return (string)(await command.ExecuteScalarAsync()
            ?? throw new InvalidOperationException("Accounting settings were not provisioned."));
    }

    private async Task<int> CountTenantUsersAsync(Guid tenantId)
    {
        await using var connection = new SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT COUNT(*) FROM dbo.AppUsers WHERE TenantId=@TenantId;", connection);
        command.Parameters.AddWithValue("@TenantId", tenantId);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private async Task<ProvisionedPrincipals> ReadProvisionedPrincipalsAsync(Guid tenantId)
    {
        await using var connection = new SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("""
            SELECT
              (SELECT COUNT(*) FROM dbo.AppUsers WHERE TenantId=@TenantId),
              (SELECT COUNT(*) FROM dbo.Customers customerValue
               JOIN dbo.Parties partyValue ON partyValue.PartyId=customerValue.PartyId
               WHERE partyValue.TenantId=@TenantId
                 AND partyValue.DisplayName=N'Consumidor final'),
              (SELECT COUNT(*) FROM dbo.Suppliers supplierValue
               JOIN dbo.Businesses businessValue ON businessValue.BusinessId=supplierValue.BusinessId
               WHERE businessValue.TenantId=@TenantId
                 AND supplierValue.Identification=N'OCASIONAL'
                 AND supplierValue.Name=N'Gasto ocasional / sin proveedor'),
              (SELECT COUNT(*) FROM dbo.Employees employeeValue
               JOIN dbo.Businesses businessValue ON businessValue.BusinessId=employeeValue.BusinessId
               WHERE businessValue.TenantId=@TenantId),
              (SELECT COUNT(*) FROM dbo.Parties WHERE TenantId=@TenantId);
            """, connection);
        command.Parameters.AddWithValue("@TenantId", tenantId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new ProvisionedPrincipals(
            reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2),
            reader.GetInt32(3), reader.GetInt32(4));
    }

    private async Task<CommercialSubscriptionState> ReadCommercialSubscriptionAsync(Guid tenantId)
    {
        await using var connection = new SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("""
            SELECT serviceValue.Code,subscription.BillingPeriod,subscription.Status,
                   subscription.FullUserLimit,subscription.SellerUserLimit,
                   subscription.PosDeviceLimit,subscription.DianDocumentMonthlyLimit,
                   subscription.PayrollEmployeeLimit,
                   (SELECT COUNT(*) FROM billing.TenantSubscriptionUsagePeriods periodValue
                    WHERE periodValue.TenantSubscriptionId=subscription.TenantSubscriptionId)
            FROM billing.TenantSubscriptions subscription
            INNER JOIN billing.TenantCommercialPlans planValue
              ON planValue.TenantCommercialPlanId=subscription.TenantCommercialPlanId
            INNER JOIN billing.BillableServices serviceValue
              ON serviceValue.BillableServiceId=planValue.BillableServiceId
            WHERE subscription.TenantId=@TenantId;
            """, connection);
        command.Parameters.AddWithValue("@TenantId", tenantId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new CommercialSubscriptionState(
            reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3),
            reader.GetInt32(4), reader.GetInt32(5), reader.GetInt32(6), reader.GetInt32(7),
            reader.GetInt32(8));
    }


    private async Task<int> CountDefaultWarehousesAsync(Guid tenantId, Guid businessId)
    {
        await using var connection = new SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("""
            SELECT COUNT(*)
            FROM dbo.Warehouses w
            INNER JOIN dbo.Businesses b ON b.BusinessId=w.BusinessId
            WHERE b.TenantId=@TenantId AND b.BusinessId=@BusinessId
              AND w.Code IN(N'VEN',N'PED') AND w.IsActive=1;
            """, connection);
        command.Parameters.AddWithValue("@TenantId", tenantId);
        command.Parameters.AddWithValue("@BusinessId", businessId);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private async Task<int> CountDefaultCostCentersAsync(Guid tenantId, Guid businessId)
    {
        await using var connection = new SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("""
            SELECT COUNT(*)
            FROM dbo.AccountingCostCenters c
            INNER JOIN dbo.Businesses b ON b.BusinessId=c.BusinessId
            WHERE b.TenantId=@TenantId AND b.BusinessId=@BusinessId
              AND c.IsDefault=1 AND c.IsActive=1;
            """, connection);
        command.Parameters.AddWithValue("@TenantId", tenantId);
        command.Parameters.AddWithValue("@BusinessId", businessId);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private async Task<int> CountOnlineSalesDocumentSeriesAsync(Guid tenantId, Guid businessId)
    {
        await using var connection = new SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("""
            SELECT COUNT(*)
            FROM dbo.DocumentSeries ds
            INNER JOIN dbo.Businesses b ON b.BusinessId=ds.BusinessId
            WHERE b.TenantId=@TenantId AND b.BusinessId=@BusinessId
              AND ds.DocumentType IN(N'SalesInvoice',N'SalesReceipt',N'SalesDebitNote')
              AND ds.DeviceId IS NULL AND ds.SeriesCode=N'00' AND ds.IsActive=1;
            """, connection);
        command.Parameters.AddWithValue("@TenantId", tenantId);
        command.Parameters.AddWithValue("@BusinessId", businessId);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private async Task<bool> RoleHasPermissionAsync(
        Guid tenantId,
        string normalizedRoleName,
        string resource)
    {
        await using var connection = new SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("""
            SELECT COUNT(*)
            FROM dbo.AppRoles roleValue
            INNER JOIN dbo.RolePermissions assignment ON assignment.RoleId=roleValue.RoleId
            INNER JOIN dbo.Permissions permissionValue ON permissionValue.PermissionId=assignment.PermissionId
            WHERE roleValue.TenantId=@TenantId
              AND roleValue.NormalizedName=@RoleName
              AND permissionValue.Resource=@Resource;
            """, connection);
        command.Parameters.AddWithValue("@TenantId", tenantId);
        command.Parameters.AddWithValue("@RoleName", normalizedRoleName);
        command.Parameters.AddWithValue("@Resource", resource);
        return Convert.ToInt32(await command.ExecuteScalarAsync()) == 1;
    }

    private async Task<int> MissingAdministratorPermissionsAsync(Guid tenantId)
    {
        await using var connection = new SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("""
            SELECT COUNT(*)
            FROM dbo.Permissions permissionValue
            WHERE permissionValue.Resource NOT LIKE N'tenants.%'
              AND permissionValue.Resource NOT LIKE N'platform.%'
              AND permissionValue.Resource NOT LIKE N'agents.%'
              AND permissionValue.Resource NOT LIKE N'conversations.%'
              AND permissionValue.Resource NOT LIKE N'leads.%'
              AND permissionValue.Resource NOT LIKE N'campaigns.%'
              AND permissionValue.Resource NOT LIKE N'reservations.%'
              AND NOT EXISTS(
                SELECT 1
                FROM dbo.AppRoles roleValue
                INNER JOIN dbo.RolePermissions assignment
                  ON assignment.RoleId=roleValue.RoleId
                 AND assignment.PermissionId=permissionValue.PermissionId
                WHERE roleValue.TenantId=@TenantId
                  AND roleValue.NormalizedName=N'ADMINISTRATOR');
            """, connection);
        command.Parameters.AddWithValue("@TenantId", tenantId);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private async Task<int> AccountantPermissionMismatchAsync(Guid tenantId)
    {
        await using var connection = new SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("""
            WITH Expected AS
            (
              SELECT PermissionId
              FROM dbo.Permissions
              WHERE Resource LIKE N'accounting.%'
                 OR Resource LIKE N'payroll.%'
                 OR Resource LIKE N'payables.%'
                 OR Resource LIKE N'receivables.%'
                 OR Resource LIKE N'expenses.%'
                 OR Resource LIKE N'commerce.taxation.%'
                 OR Resource LIKE N'fiscal.configuration.%'
                 OR Resource IN(
                   N'businesses.read',N'dashboard.read',N'audit_logs.read',N'payments.read',N'payments.confirm_manual',
                   N'parties.read',N'customers.read',N'suppliers.read',N'catalog.read',N'catalog.costs.read',N'products.read',
                   N'inventory.read',N'inventory.costs.read',N'inventory.reasons.manage',
                   N'work-sessions.read',N'work-sessions.differences.read',N'work-sessions.cash-reasons.configure',
                   N'dispatches.read-all',N'dispatches.reports.view',N'dispatches.reports.export',
                   N'sales.reports.read',N'sales.reports.read-all',N'sales.returns.read',N'sales.debit-notes.read',
                   N'service-invoices.read',
                   N'purchasing.goods-receipts.read',N'purchasing.purchase-returns.read')
            ), Actual AS
            (
              SELECT assignment.PermissionId
              FROM dbo.AppRoles roleValue
              INNER JOIN dbo.RolePermissions assignment ON assignment.RoleId=roleValue.RoleId
              WHERE roleValue.TenantId=@TenantId AND roleValue.NormalizedName=N'ACCOUNTANT'
            )
            SELECT
              (SELECT COUNT(*) FROM Expected expectedValue
               WHERE NOT EXISTS(SELECT 1 FROM Actual actualValue
                                WHERE actualValue.PermissionId=expectedValue.PermissionId))
              +
              (SELECT COUNT(*) FROM Actual actualValue
               WHERE NOT EXISTS(SELECT 1 FROM Expected expectedValue
                                WHERE expectedValue.PermissionId=actualValue.PermissionId));
            """, connection);
        command.Parameters.AddWithValue("@TenantId", tenantId);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private async Task<int> TenantAdministratorPermissionCountAsync()
    {
        await using var connection = new SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("""
            SELECT COUNT(*)
            FROM dbo.Permissions
            WHERE Resource NOT LIKE N'tenants.%'
              AND Resource NOT LIKE N'platform.%'
              AND Resource NOT LIKE N'agents.%'
              AND Resource NOT LIKE N'conversations.%'
              AND Resource NOT LIKE N'leads.%'
              AND Resource NOT LIKE N'campaigns.%'
              AND Resource NOT LIKE N'reservations.%';
            """, connection);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private async Task<int> CountCurrentRenewalOrdersAsync(Guid tenantId)
    {
        await using var connection = new SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("""
            SELECT COUNT(*)
            FROM billing.TenantSubscriptionRenewalOrders renewal
            JOIN billing.TenantSubscriptions subscription
              ON subscription.TenantSubscriptionId=renewal.TenantSubscriptionId
            WHERE subscription.TenantId=@TenantId AND renewal.IsCurrent=1;
            """, connection);
        command.Parameters.AddWithValue("@TenantId", tenantId);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private async Task AddActiveUsersAsync(Guid tenantId, int count)
    {
        await using var connection = new SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        for (var index = 0; index < count; index++)
        {
            var value = $"renewal-{Guid.NewGuid():N}";
            await using var command = new SqlCommand("""
                INSERT dbo.AppUsers
                  (UserId,TenantId,Username,NormalizedUsername,Email,NormalizedEmail,
                   FirstName,LastName,EmailConfirmed,IsActive,CreatedAt)
                VALUES(NEWID(),@TenantId,@Value,UPPER(@Value),@Email,UPPER(@Email),
                       N'Prueba',N'Renovación',1,1,SYSUTCDATETIME());
                """, connection);
            command.Parameters.AddWithValue("@TenantId", tenantId);
            command.Parameters.AddWithValue("@Value", value);
            command.Parameters.AddWithValue("@Email", $"{value}@auraly.test");
            await command.ExecuteNonQueryAsync();
        }
    }

    private async Task<int> CountRenewalOrderSideEffectsAsync(Guid tenantId)
    {
        await using var connection = new SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("""
            SELECT
              (SELECT COUNT(*) FROM dbo.Receivables receivable
               JOIN dbo.Customers customerValue ON customerValue.CustomerId=receivable.CustomerId
               JOIN billing.TenantSubscriptions subscription ON subscription.BillingCustomerId=customerValue.CustomerId
               WHERE subscription.TenantId=@TenantId)
              +
              (SELECT COUNT(*) FROM dbo.PaymentTransactions paymentValue
               JOIN billing.TenantSubscriptions subscription ON subscription.TenantSubscriptionId=paymentValue.SubjectId
               WHERE subscription.TenantId=@TenantId AND paymentValue.SubjectType=N'TenantSubscription');
            """, connection);
        command.Parameters.AddWithValue("@TenantId", tenantId);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private async Task<FiscalVerificationMaterial> ConfigurePlatformBillingFiscalAsync(
        Guid businessId)
    {
        var authorizationId = Guid.NewGuid();
        var issuerId = Guid.NewGuid();
        var seriesId = Guid.NewGuid();
        const string supplierTaxId = "901777333";
        const string authorizationNumber = "18769999999";
        const string technicalVersion = "billing-test-v1";
        await using var connection = new SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("""
            INSERT dbo.FiscalAuthorizations
              (FiscalAuthorizationId,BusinessId,AuthorizationNumber,SupplierTaxId,
               Environment,QrValidationUrl,TechnicalKeyVersion,ValidFrom,ValidUntil,
               AuthorizedRangeStart,AuthorizedRangeEnd,IsActive,CreatedAt)
            VALUES(@AuthorizationId,@BusinessId,@AuthorizationNumber,@SupplierTaxId,2,
               @Qr,@Version,'2026-01-01','2028-12-31',1,100000,1,SYSDATETIMEOFFSET());
            INSERT dbo.FiscalIssuerConfigurations
              (FiscalIssuerConfigurationId,BusinessId,Version,SupplierTaxId,SupplierCheckDigit,
               LegalName,TradeName,TaxLevelCode,TaxSchemeId,TaxSchemeName,IdentificationTypeCode,
               AddressLine,CityCode,CityName,DepartmentCode,DepartmentName,CountryCode,CountryName,
               SoftwareIdentificationCode,SoftwarePinSecretReference,Environment,TestSetId,
               CertificateProvider,CertificateKeyReference,CertificateThumbprint,DianEndpoint,
               TechnicalAnnexVersion,GeneratorVersion,ValidFrom,IsActive,CreatedAt)
            VALUES(@IssuerId,@BusinessId,1,@SupplierTaxId,N'1',N'Auraly',N'Auraly',
               N'R-99-PN',N'01',N'IVA',N'31',N'Calle 1',N'11001',N'Bogotá',N'11',
               N'Bogotá D.C.',N'CO',N'Colombia',N'auraly-billing-test',N'test',2,
               '11111111-1111-1111-1111-111111111111',N'Test',N'Test',N'TEST',
               N'https://vpfe-hab.dian.gov.co/WcfDianCustomerServices.svc',N'1.9',
               N'Auraly.Tests','2026-01-01',1,SYSDATETIMEOFFSET());
            INSERT dbo.FiscalSeries
              (SeriesId,BusinessId,DeviceId,EmitterKind,FiscalAuthorizationId,
               DocumentType,Prefix,RangeStart,RangeEnd,IsActive,CreatedAt)
            VALUES(@SeriesId,@BusinessId,NULL,N'Server',@AuthorizationId,
               N'SalesInvoice',N'FSV',1,100000,1,SYSDATETIMEOFFSET());
            """, connection);
        command.Parameters.AddWithValue("@AuthorizationId", authorizationId);
        command.Parameters.AddWithValue("@IssuerId", issuerId);
        command.Parameters.AddWithValue("@SeriesId", seriesId);
        command.Parameters.AddWithValue("@BusinessId", businessId);
        command.Parameters.AddWithValue("@AuthorizationNumber", authorizationNumber);
        command.Parameters.AddWithValue("@SupplierTaxId", supplierTaxId);
        command.Parameters.AddWithValue("@Qr", ServerSliceFixture.QrValidationUrl);
        command.Parameters.AddWithValue("@Version", technicalVersion);
        await command.ExecuteNonQueryAsync();
        return new(new FiscalTechnicalKey("billing-test-technical-key", technicalVersion),
            supplierTaxId, FiscalEnvironment.Test, ServerSliceFixture.QrValidationUrl);
    }

    private async Task UseCanonicalAuralyBillingBusinessAsync()
    {
        await using var connection = new SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("""
            UPDATE billing.PlatformBillingSettings
            SET BillingBusinessId='A0A10000-0000-0000-0000-000000000001',
                UpdatedAt=SYSDATETIMEOFFSET()
            WHERE PlatformBillingSettingId=1;
            """, connection);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private async Task ConfirmPaymentAsync(Guid paymentId, string providerTransactionId)
    {
        await using var connection = new SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("""
            UPDATE dbo.PaymentTransactions
            SET Status=@Status,ProviderTransactionId=@Provider,ConfirmedAt=SYSUTCDATETIME()
            WHERE PaymentTransactionId=@PaymentId;
            """, connection);
        command.Parameters.AddWithValue("@PaymentId", paymentId);
        command.Parameters.AddWithValue("@Provider", providerTransactionId);
        command.Parameters.AddWithValue("@Status", (int)PaymentTransactionStatus.Confirmed);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private async Task<Guid> CreatePlatformAdministratorAsync()
    {
        var userId = Guid.NewGuid();
        await using var connection = new SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("""
            DECLARE @RoleId UNIQUEIDENTIFIER=(
                SELECT RoleId FROM dbo.AppRoles
                WHERE TenantId=@TenantId AND NormalizedName=N'ADMINISTRATOR');
            IF @RoleId IS NULL
                THROW 51000,N'No existe el administrador canónico de Auraly.',1;
            INSERT dbo.AppUsers(
                UserId,TenantId,Username,NormalizedUsername,Email,NormalizedEmail,
                FirstName,LastName,IsActive,EmailConfirmed,CreatedAt)
            VALUES(
                @UserId,@TenantId,CONCAT(N'platform-',@UserId),
                UPPER(CONCAT(N'platform-',@UserId)),CONCAT(@UserId,N'@auraly.test'),
                UPPER(CONCAT(@UserId,N'@auraly.test')),N'Administrador',N'Auraly',
                1,1,SYSUTCDATETIME());
            INSERT dbo.UserRoles(UserRoleId,UserId,RoleId,BusinessId,AssignedAt)
            VALUES(NEWID(),@UserId,@RoleId,NULL,SYSUTCDATETIME());
            """, connection);
        command.Parameters.AddWithValue("@UserId", userId);
        command.Parameters.AddWithValue("@TenantId", AuralyTenantId);
        await command.ExecuteNonQueryAsync();
        return userId;
    }

    private sealed class FixedFiscalTechnicalKeyProvider(FiscalVerificationMaterial value)
        : IFiscalTechnicalKeyProvider
    {
        public Task<FiscalVerificationMaterial?> ResolveAsync(
            FiscalKeyReference reference, CancellationToken cancellationToken) =>
            Task.FromResult<FiscalVerificationMaterial?>(value);
    }

    private sealed record BusinessCreatedResponse(Guid BusinessId);

    private sealed record ProvisionedState(
        int Businesses, int Warehouses, int InventoryReasons, int ProductUnits,
        int DefaultCustomers, int AccountingAccounts, int AccountingMappings,
        int OpenAccountingPeriods, int DefaultCostCenters, int AccountingVoucherCursors,
        int UnmappedPosPaymentMethods, int Roles, int OnlineSalesDocumentSeries, int UserRoles,
        bool? UserActive, string? PasswordHash, string InvitationStatus);

    private sealed record ProvisionedPrincipals(
        int Users, int FinalConsumers, int OccasionalSuppliers, int Employees, int Parties);

    private sealed record CommercialSubscriptionState(
        string PlanCode, string BillingPeriod, string Status, int FullUsers,
        int SellerUsers, int PosDevices, int DianDocuments, int PayrollEmployees,
        int UsagePeriods);
}
