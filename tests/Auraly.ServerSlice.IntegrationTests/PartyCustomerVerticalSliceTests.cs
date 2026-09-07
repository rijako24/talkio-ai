using System.Net;
using System.Net.Http.Json;
using Auraly.BuildingBlocks.Application.Synchronization;
using Auraly.Contracts.Parties;
using Auraly.Pos.Edge.Infrastructure;
using Microsoft.Data.SqlClient;

namespace Auraly.ServerSlice.IntegrationTests;

[Collection(ServerSliceCollection.Name)]
public sealed class PartyCustomerVerticalSliceTests(ServerSliceFixture fixture)
{
    [Fact]
    public async Task Geography_customer_identity_sites_and_pricing_are_connected_end_to_end()
    {
        fixture.DrainSynchronizationMessages();
        using var denied = fixture.CreateAdminClient(PartyPermissionCodes.GeographyRead);
        using var deniedMaster = await denied.PostAsJsonAsync(
            "/api/commerce/v1/masters/geography/countries",
            new SaveCountryRequest("CX", "Customer test country"));
        Assert.Equal(HttpStatusCode.Forbidden, deniedMaster.StatusCode);

        using var admin = fixture.CreateAdminClient(
            PartyPermissionCodes.GeographyRead,
            PartyPermissionCodes.GeographyManage,
            PartyPermissionCodes.CustomerRead,
            PartyPermissionCodes.CustomerCreate,
            PartyPermissionCodes.ManageSites,
            PartyPermissionCodes.ManagePricing);

        var country = await PostAndReadAsync<SaveCountryRequest, CountryItem>(
            admin,
            "/api/commerce/v1/masters/geography/countries",
            new SaveCountryRequest("CX", "Customer test country"));
        var division = await PostAndReadAsync<SaveAdministrativeDivisionRequest, AdministrativeDivisionItem>(
            admin,
            "/api/commerce/v1/masters/geography/divisions",
            new SaveAdministrativeDivisionRequest(
                country.CountryId,
                "D01",
                "Customer test department"));
        var city = await PostAndReadAsync<SaveCityRequest, CityItem>(
            admin,
            "/api/commerce/v1/masters/geography/cities",
            new SaveCityRequest(
                division.AdministrativeDivisionId,
                "C01",
                "Customer test city"));

        var hierarchy = await admin.GetFromJsonAsync<IReadOnlyCollection<GeographyHierarchyItem>>(
            "/api/commerce/v1/masters/geography/hierarchy?includeInactive=true");
        Assert.NotNull(hierarchy);
        Assert.Contains(hierarchy, item => item.Id == country.CountryId && item.Level == "Country");
        Assert.Contains(hierarchy, item => item.Id == division.AdministrativeDivisionId && item.ParentId == country.CountryId);
        Assert.Contains(hierarchy, item => item.Id == city.CityId && item.ParentId == division.AdministrativeDivisionId);

        using var updateCityResponse = await admin.PutAsJsonAsync(
            $"/api/commerce/v1/masters/geography/cities/{city.CityId:D}",
            new SaveCityRequest(division.AdministrativeDivisionId, "C01", "Customer test city edited", false));
        Assert.Equal(HttpStatusCode.OK, updateCityResponse.StatusCode);
        var inactiveCity = await updateCityResponse.Content.ReadFromJsonAsync<CityItem>();
        Assert.NotNull(inactiveCity);
        Assert.False(inactiveCity.IsActive);
        Assert.Equal("Customer test city edited", inactiveCity.Name);

        using var hiddenCitiesResponse = await admin.GetAsync(
            $"/api/commerce/v1/masters/geography/divisions/{division.AdministrativeDivisionId:D}/cities");
        var hiddenCities = await hiddenCitiesResponse.Content.ReadFromJsonAsync<IReadOnlyCollection<CityItem>>();
        Assert.DoesNotContain(hiddenCities!, item => item.CityId == city.CityId);

        using var reactivateCityResponse = await admin.PutAsJsonAsync(
            $"/api/commerce/v1/masters/geography/cities/{city.CityId:D}",
            new SaveCityRequest(division.AdministrativeDivisionId, "C01", "Customer test city", true));
        Assert.Equal(HttpStatusCode.OK, reactivateCityResponse.StatusCode);

        var priceChannelId = Guid.NewGuid();
        await ExecuteAsync(
            """
            INSERT dbo.PriceChannels(PriceChannelId,BusinessId,Code,Name,Strategy,IsActive,CreatedAt)
            VALUES(@PriceChannelId,@BusinessId,N'CLI-MAY',N'Mayorista clientes',N'TieredProductPrice',1,SYSDATETIMEOFFSET());
            """,
            new SqlParameter("@PriceChannelId", priceChannelId),
            new SqlParameter("@BusinessId", fixture.BusinessId));

        var operationId = Guid.NewGuid();
        var request = CustomerRequest(
            operationId,
            fixture.BusinessId,
            country.CountryId,
            division.AdministrativeDivisionId,
            city.CityId,
            "1.234.567-8",
            "Ada Cliente",
            "Principal",
            new CustomerPricingInput(priceChannelId));

        using var createdResponse = await admin.PostAsJsonAsync(
            "/api/commerce/v1/customers",
            request);
        Assert.Equal(HttpStatusCode.Created, createdResponse.StatusCode);
        var created = await createdResponse.Content.ReadFromJsonAsync<CustomerDetail>();
        Assert.NotNull(created);
        Assert.Equal("12345678", created.NormalizedIdentification);
        Assert.Equal(priceChannelId, created.PriceChannelId);
        Assert.Equal("Barrio escrito por el usuario", Assert.Single(created.Sites).Neighborhood);
        PosSynchronizationInvalidation customerSignal;
        do
        {
            customerSignal = await fixture.ReadSynchronizationMessageAsync();
        }
        while (customerSignal.Stream != "Customers");
        Assert.Equal(fixture.TenantId, customerSignal.TenantId);
        Assert.Equal(fixture.BusinessId, customerSignal.BusinessId);
        var customerOutboxCount = await ScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.PosSynchronizationOutboxMessages WHERE BusinessId=@BusinessId AND Stream=N'Customers';",
            new SqlParameter("@BusinessId", fixture.BusinessId));

        using var repeatedResponse = await admin.PostAsJsonAsync(
            "/api/commerce/v1/customers",
            request);
        Assert.Equal(HttpStatusCode.Created, repeatedResponse.StatusCode);
        var repeated = await repeatedResponse.Content.ReadFromJsonAsync<CustomerDetail>();
        Assert.NotNull(repeated);
        Assert.Equal(created.CustomerId, repeated.CustomerId);
        Assert.Equal(created.PartyId, repeated.PartyId);
        Assert.Equal(customerOutboxCount, await ScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.PosSynchronizationOutboxMessages WHERE BusinessId=@BusinessId AND Stream=N'Customers';",
            new SqlParameter("@BusinessId", fixture.BusinessId)));
        await Task.Delay(100);
        Assert.DoesNotContain(
            fixture.DrainSynchronizationMessages(),
            message => message.Stream == "Customers" &&
                       message.AvailableThroughCursor > customerSignal.AvailableThroughCursor);

        var sqlitePath = Path.Combine(
            Path.GetTempPath(), $"auraly-customer-sync-{Guid.NewGuid():N}.db");
        try
        {
            var local = new PosCatalogStore($"Data Source={sqlitePath}");
            var synchronization = new PosCatalogSynchronizer(
                fixture.CreateClient(), local,
                new PosDeviceCredentials(fixture.DeviceId, ServerSliceFixture.DeviceSecret),
                new PosOperationalScope(fixture.BusinessId, fixture.WarehouseId));
            await synchronization.SynchronizeAsync();
            var localCustomer = Assert.Single(
                await local.SearchCustomersAsync("12345678"));
            Assert.Equal(created.CustomerId, localCustomer.CustomerId);
            Assert.Equal(priceChannelId, localCustomer.PriceChannelId);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(sqlitePath)) File.Delete(sqlitePath);
        }

        var identification = Uri.EscapeDataString("1 234 567 8");
        var found = await admin.GetFromJsonAsync<CustomerDetail>(
            $"/api/commerce/v1/customers/by-identification?countryId={country.CountryId:D}" +
            $"&identificationType=cc&identification={identification}");
        Assert.NotNull(found);
        Assert.Equal(created.CustomerId, found.CustomerId);

        var siteOperationId = Guid.NewGuid();
        var secondSiteRequest = new AddPartySiteRequest(
            siteOperationId,
            new PartySiteInput(
                "NORTE",
                "Sede norte",
                country.CountryId,
                division.AdministrativeDivisionId,
                city.CityId,
                "Carrera 2 # 3-4",
                "Barrio Norte",
                null,
                "norte@auraly.test",
                "3001112233",
                false));
        using var secondSiteResponse = await admin.PostAsJsonAsync(
            $"/api/commerce/v1/customers/{created.CustomerId:D}/sites",
            secondSiteRequest);
        Assert.Equal(HttpStatusCode.Created, secondSiteResponse.StatusCode);
        var secondSite = await secondSiteResponse.Content.ReadFromJsonAsync<PartySiteDetail>();
        Assert.NotNull(secondSite);

        using var repeatedSiteResponse = await admin.PostAsJsonAsync(
            $"/api/commerce/v1/customers/{created.CustomerId:D}/sites",
            secondSiteRequest);
        Assert.Equal(HttpStatusCode.Created, repeatedSiteResponse.StatusCode);
        var repeatedSite = await repeatedSiteResponse.Content.ReadFromJsonAsync<PartySiteDetail>();
        Assert.NotNull(repeatedSite);
        Assert.Equal(secondSite.PartySiteId, repeatedSite.PartySiteId);

        var detailed = await admin.GetFromJsonAsync<CustomerDetail>(
            $"/api/commerce/v1/customers/{created.CustomerId:D}");
        Assert.NotNull(detailed);
        Assert.Equal(2, detailed.Sites.Count);

        var page = await admin.GetFromJsonAsync<CustomerPage>(
            "/api/commerce/v1/customers?page=1&pageSize=10&search=123456");
        Assert.NotNull(page);
        Assert.Contains(page.Items, item => item.CustomerId == created.CustomerId);

        var invalidPricing = request with
        {
            OperationId = Guid.NewGuid(),
            Party = request.Party with { Identification = "900111222" },
            Pricing = new CustomerPricingInput(Guid.NewGuid())
        };
        using var invalidPricingResponse = await admin.PostAsJsonAsync(
            "/api/commerce/v1/customers",
            invalidPricing);
        Assert.Equal(HttpStatusCode.BadRequest, invalidPricingResponse.StatusCode);

        using var noPricingPermission = fixture.CreateAdminClient(
            PartyPermissionCodes.CustomerCreate);
        using var pricingDeniedResponse = await noPricingPermission.PostAsJsonAsync(
            "/api/commerce/v1/customers",
            request with
            {
                OperationId = Guid.NewGuid(),
                Party = request.Party with { Identification = "900333444" }
            });
        Assert.Equal(HttpStatusCode.Forbidden, pricingDeniedResponse.StatusCode);

        Assert.Equal(
            1,
            await ScalarAsync<int>(
                "SELECT COUNT(*) FROM dbo.Parties WHERE PartyId=@PartyId;",
                new SqlParameter("@PartyId", created.PartyId)));
        Assert.Equal(
            1,
            await ScalarAsync<int>(
                "SELECT COUNT(*) FROM dbo.Customers WHERE CustomerId=@CustomerId;",
                new SqlParameter("@CustomerId", created.CustomerId)));
        Assert.Equal(
            2,
            await ScalarAsync<int>(
                "SELECT COUNT(*) FROM dbo.PartySites WHERE PartyId=@PartyId;",
                new SqlParameter("@PartyId", created.PartyId)));
        Assert.Equal(
            1,
            await ScalarAsync<int>(
                "SELECT COUNT(*) FROM dbo.PartySiteCreationReceipts WHERE BusinessId=@BusinessId AND OperationId=@OperationId;",
                new SqlParameter("@BusinessId", fixture.BusinessId),
                new SqlParameter("@OperationId", siteOperationId)));
    }

    [Fact]
    public async Task Pos_quick_creation_uses_device_scope_and_cannot_assign_pricing()
    {
        var countryId = Guid.NewGuid();
        var divisionId = Guid.NewGuid();
        var cityId = Guid.NewGuid();
        await ExecuteAsync(
            """
            INSERT dbo.Countries(CountryId,Code,Name,IsActive,CreatedAt)
            VALUES(@CountryId,N'PX',N'POS country',1,SYSDATETIMEOFFSET());
            INSERT dbo.AdministrativeDivisions
              (AdministrativeDivisionId,CountryId,Code,Name,DivisionType,IsActive,CreatedAt)
            VALUES(@DivisionId,@CountryId,N'PD',N'POS department',N'Department',1,SYSDATETIMEOFFSET());
            INSERT dbo.Cities(CityId,AdministrativeDivisionId,Code,Name,IsActive,CreatedAt)
            VALUES(@CityId,@DivisionId,N'PC',N'POS city',1,SYSDATETIMEOFFSET());
            """,
            new SqlParameter("@CountryId", countryId),
            new SqlParameter("@DivisionId", divisionId),
            new SqlParameter("@CityId", cityId));

        var request = CustomerRequest(
            Guid.NewGuid(),
            fixture.BusinessId,
            countryId,
            divisionId,
            cityId,
            "55.667.788",
            "Cliente creado en facturación",
            "Principal",
            null);

        using var denied = fixture.CreateClient();
        denied.DefaultRequestHeaders.Add("X-Auraly-Device-Id", fixture.DeniedDeviceId.ToString("D"));
        denied.DefaultRequestHeaders.Add("X-Auraly-Device-Secret", ServerSliceFixture.DeniedDeviceSecret);
        using var deniedResponse = await denied.PostAsJsonAsync(
            "/api/pos/v1/customers",
            request);
        Assert.Equal(HttpStatusCode.OK, deniedResponse.StatusCode);
        var created = await deniedResponse.Content.ReadFromJsonAsync<CustomerDetail>();
        Assert.NotNull(created);
        Assert.Equal(fixture.BusinessId, created.BusinessId);
        Assert.Null(created.PriceChannelId);

        using var pos = fixture.CreateClient();
        pos.DefaultRequestHeaders.Add("X-Auraly-Device-Id", fixture.DeviceId.ToString("D"));
        pos.DefaultRequestHeaders.Add("X-Auraly-Device-Secret", ServerSliceFixture.DeviceSecret);
        var countries = await pos.GetFromJsonAsync<IReadOnlyCollection<CountryItem>>(
            $"/api/pos/v1/customers/geography/countries?businessId={fixture.BusinessId:D}");
        Assert.Contains(countries!, item => item.CountryId == countryId);
        var hierarchy = await pos.GetFromJsonAsync<IReadOnlyCollection<GeographyHierarchyItem>>(
            $"/api/pos/v1/customers/geography/hierarchy?businessId={fixture.BusinessId:D}");
        Assert.Contains(hierarchy!, item => item.Id == countryId && item.Level == "Country");
        Assert.Contains(hierarchy!, item => item.Id == divisionId && item.ParentId == countryId);
        Assert.Contains(hierarchy!, item => item.Id == cityId && item.ParentId == divisionId);

        var found = await pos.GetFromJsonAsync<CustomerDetail>(
            $"/api/pos/v1/customers/by-identification?businessId={fixture.BusinessId:D}&countryId={countryId:D}" +
            "&identificationType=CC&identification=55667788");
        Assert.NotNull(found);
        Assert.Equal(created.CustomerId, found.CustomerId);

        var reservedCustomerId = Guid.NewGuid();
        var reservedRequest = request with
        {
            OperationId = reservedCustomerId,
            Party = request.Party with { Identification = "55667789" },
            RequestedCustomerId = reservedCustomerId
        };
        using var reservedResponse = await pos.PostAsJsonAsync(
            "/api/pos/v1/customers", reservedRequest);
        reservedResponse.EnsureSuccessStatusCode();
        var reserved = await reservedResponse.Content.ReadFromJsonAsync<CustomerDetail>();
        Assert.NotNull(reserved);
        Assert.Equal(reservedCustomerId, reserved.CustomerId);

        var priceChannelId = Guid.NewGuid();
        await ExecuteAsync(
            """
            INSERT dbo.PriceChannels(PriceChannelId,BusinessId,Code,Name,Strategy,IsActive,CreatedAt)
            VALUES(@PriceChannelId,@BusinessId,N'POS-NO',N'POS cannot assign',N'TieredProductPrice',1,SYSDATETIMEOFFSET());
            """,
            new SqlParameter("@PriceChannelId", priceChannelId),
            new SqlParameter("@BusinessId", fixture.BusinessId));
        using var pricingResponse = await pos.PostAsJsonAsync(
            "/api/pos/v1/customers",
            request with
            {
                OperationId = Guid.NewGuid(),
                Party = request.Party with { Identification = "55667799" },
                Pricing = new CustomerPricingInput(priceChannelId)
            });
        Assert.Equal(HttpStatusCode.Forbidden, pricingResponse.StatusCode);

        using var wrongBusinessResponse = await pos.PostAsJsonAsync(
            "/api/pos/v1/customers",
            request with { OperationId = Guid.NewGuid(), BusinessId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.Forbidden, wrongBusinessResponse.StatusCode);
    }

    [Fact]
    public async Task Customer_and_supplier_roles_share_one_party_and_workspace_is_concurrent_safe()
    {
        var synchronizationMessagesBefore = await ScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.PosSynchronizationOutboxMessages WHERE BusinessId=@BusinessId AND Stream=N'Customers';",
            new SqlParameter("@BusinessId", fixture.BusinessId));
        using var admin = fixture.CreateAdminClient(
            PartyPermissionCodes.GeographyRead,
            PartyPermissionCodes.GeographyManage,
            PartyPermissionCodes.CustomerRead,
            PartyPermissionCodes.CustomerCreate,
            PartyWorkspacePermissionCodes.Read,
            PartyWorkspacePermissionCodes.Update,
            PartyWorkspacePermissionCodes.Deactivate,
            PartyWorkspacePermissionCodes.SupplierRead,
            PartyWorkspacePermissionCodes.SupplierCreate,
            PartyWorkspacePermissionCodes.SellerCreate,
            PartyWorkspacePermissionCodes.CarrierCreate);

        var country = await PostAndReadAsync<SaveCountryRequest, CountryItem>(
            admin, "/api/commerce/v1/masters/geography/countries",
            new SaveCountryRequest("PW", "Party workspace country"));
        var division = await PostAndReadAsync<SaveAdministrativeDivisionRequest, AdministrativeDivisionItem>(
            admin, "/api/commerce/v1/masters/geography/divisions",
            new SaveAdministrativeDivisionRequest(country.CountryId, "PWD", "Party workspace division"));
        var city = await PostAndReadAsync<SaveCityRequest, CityItem>(
            admin, "/api/commerce/v1/masters/geography/cities",
            new SaveCityRequest(division.AdministrativeDivisionId, "PWC", "Party workspace city"));

        var customerRequest = CustomerRequest(
            Guid.NewGuid(), fixture.BusinessId, country.CountryId,
            division.AdministrativeDivisionId, city.CityId,
            "901.777.333-1", "Comercial unificada", "Principal", null) with
        {
            Party = new PartyInput(
                PartyTypes.Organization, country.CountryId, "NIT", "901.777.333-1", "4",
                "Comercial unificada", "Comercial unificada S.A.S.", null, null,
                "compras@unificada.test", "3007773311")
        };
        using var customerResponse = await admin.PostAsJsonAsync(
            "/api/commerce/v1/customers", customerRequest);
        Assert.Equal(HttpStatusCode.Created, customerResponse.StatusCode);
        var customer = await customerResponse.Content.ReadFromJsonAsync<CustomerDetail>();
        Assert.NotNull(customer);

        var supplierRequest = new CreateSupplierRequest(
            Guid.NewGuid(), fixture.BusinessId, customerRequest.Party, customerRequest.PrimarySite,
            PurchaseEvidencePolicy: null, DefaultPaymentDueDays: 15);
        using var supplierResponse = await admin.PostAsJsonAsync(
            "/api/commerce/v1/suppliers", supplierRequest);
        Assert.Equal(HttpStatusCode.Created, supplierResponse.StatusCode);
        var supplier = await supplierResponse.Content.ReadFromJsonAsync<SupplierAcceptance>();
        Assert.NotNull(supplier);
        Assert.Equal(customer.PartyId, supplier.PartyId);
        Assert.False(supplier.IdempotentReplay);

        using var replayResponse = await admin.PostAsJsonAsync(
            "/api/commerce/v1/suppliers", supplierRequest);
        Assert.Equal(HttpStatusCode.Created, replayResponse.StatusCode);
        var replay = await replayResponse.Content.ReadFromJsonAsync<SupplierAcceptance>();
        Assert.NotNull(replay);
        Assert.Equal(supplier.SupplierId, replay.SupplierId);
        Assert.True(replay.IdempotentReplay);

        var sellerRequest = new CreateSellerRequest(
            Guid.NewGuid(), fixture.BusinessId, customerRequest.Party, customerRequest.PrimarySite,
            "VEN-PW", 4.5m, "SaleAfterTax", "Sale");
        using var sellerResponse = await admin.PostAsJsonAsync("/api/commerce/v1/sellers", sellerRequest);
        Assert.Equal(HttpStatusCode.Created, sellerResponse.StatusCode);
        var seller = await sellerResponse.Content.ReadFromJsonAsync<CommercialRoleAcceptance>();
        Assert.NotNull(seller);
        Assert.Equal(customer.PartyId, seller.PartyId);

        var carrierRequest = new CreateCarrierRequest(
            Guid.NewGuid(), fixture.BusinessId, customerRequest.Party, customerRequest.PrimarySite,
            "TRA-PW", "Road");
        using var carrierResponse = await admin.PostAsJsonAsync("/api/commerce/v1/carriers", carrierRequest);
        Assert.Equal(HttpStatusCode.Created, carrierResponse.StatusCode);
        var carrier = await carrierResponse.Content.ReadFromJsonAsync<CommercialRoleAcceptance>();
        Assert.NotNull(carrier);
        Assert.Equal(customer.PartyId, carrier.PartyId);

        using var duplicateSeller = await admin.PostAsJsonAsync(
            "/api/commerce/v1/sellers", sellerRequest with { OperationId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.Conflict, duplicateSeller.StatusCode);

        var page = await admin.GetFromJsonAsync<PartyWorkspacePage>(
            "/api/commerce/v1/parties?page=1&pageSize=10&search=9017773331");
        Assert.NotNull(page);
        Assert.Equal(1, page.Page);
        Assert.Equal(10, page.PageSize);
        var item = Assert.Single(page.Items.Where(value => value.PartyId == customer.PartyId));
        Assert.Equal(new[] { "Carrier", "Customer", "Seller", "Supplier" }, item.Roles.OrderBy(value => value).ToArray());
        Assert.Equal("4", item.VerificationDigit);
        Assert.Equal(customer.CustomerId, item.CustomerId);
        Assert.Equal(supplier.SupplierId, item.SupplierId);
        Assert.Equal(15, item.SupplierDefaultPaymentDueDays);
        Assert.Equal(seller.RoleId, item.SellerId);
        Assert.Equal(carrier.RoleId, item.CarrierId);

        var multiTermPage = await admin.GetFromJsonAsync<PartyWorkspacePage>(
            "/api/commerce/v1/parties?page=1&pageSize=10&search=Comercial%203331");
        Assert.NotNull(multiTermPage);
        Assert.Contains(multiTermPage.Items, value => value.PartyId == customer.PartyId);

        var supplierPage = await admin.GetFromJsonAsync<PartyWorkspacePage>(
            $"/api/commerce/v1/parties?page=1&pageSize=1&role=Supplier&roleId={supplier.SupplierId:D}");
        Assert.Equal(customer.PartyId, Assert.Single(supplierPage!.Items).PartyId);
        var identityPage = await admin.GetFromJsonAsync<PartyWorkspacePage>(
            $"/api/commerce/v1/parties?page=1&pageSize=1&role=Supplier&partyId={customer.PartyId:D}");
        Assert.Equal(supplier.SupplierId, Assert.Single(identityPage!.Items).SupplierId);

        var update = new UpdatePartyRequest(
            PartyTypes.Organization, "Comercial unificada renovada",
            "Comercial unificada S.A.S.", null, null, "4",
            "compras@unificada.test", "3007773311", item.RowVersion,
            Supplier: new UpdateSupplierRoleRequest(null, 45));
        using var updateResponse = await admin.PutAsJsonAsync(
            $"/api/commerce/v1/parties/{item.PartyId:D}", update);
        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
        var updated = await updateResponse.Content.ReadFromJsonAsync<PartyWorkspaceItem>();
        Assert.NotNull(updated);
        Assert.Equal("Comercial unificada renovada", updated.DisplayName);
        Assert.Equal("4", updated.VerificationDigit);
        Assert.Equal(45, updated.SupplierDefaultPaymentDueDays);

        using var staleResponse = await admin.PutAsJsonAsync(
            $"/api/commerce/v1/parties/{item.PartyId:D}", update);
        Assert.Equal(HttpStatusCode.Conflict, staleResponse.StatusCode);

        using var inactiveResponse = await admin.PostAsJsonAsync(
            $"/api/commerce/v1/parties/{item.PartyId:D}/status",
            new SetPartyBusinessStatusRequest(false, updated.RowVersion));
        Assert.Equal(HttpStatusCode.OK, inactiveResponse.StatusCode);
        var inactive = await inactiveResponse.Content.ReadFromJsonAsync<PartyWorkspaceItem>();
        Assert.NotNull(inactive);
        Assert.False(inactive.IsActive);

        using var denied = fixture.CreateAdminClient(PartyWorkspacePermissionCodes.Read);
        using var deniedResponse = await denied.PostAsJsonAsync(
            "/api/commerce/v1/suppliers", supplierRequest with { OperationId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.Forbidden, deniedResponse.StatusCode);

        using var wrongBusiness = await admin.PostAsJsonAsync(
            "/api/commerce/v1/suppliers",
            supplierRequest with { OperationId = Guid.NewGuid(), BusinessId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.Forbidden, wrongBusiness.StatusCode);

        Assert.Equal(1, await ScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.Parties WHERE PartyId=@PartyId;",
            new SqlParameter("@PartyId", customer.PartyId)));
        Assert.Equal(1, await ScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.Customers WHERE PartyId=@PartyId AND BusinessId=@BusinessId;",
            new SqlParameter("@PartyId", customer.PartyId), new SqlParameter("@BusinessId", fixture.BusinessId)));
        Assert.Equal(1, await ScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.Suppliers WHERE PartyId=@PartyId AND BusinessId=@BusinessId;",
            new SqlParameter("@PartyId", customer.PartyId), new SqlParameter("@BusinessId", fixture.BusinessId)));
        Assert.Equal(1, await ScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.CommerceSellers WHERE PartyId=@PartyId AND BusinessId=@BusinessId;",
            new SqlParameter("@PartyId", customer.PartyId), new SqlParameter("@BusinessId", fixture.BusinessId)));
        Assert.Equal(1, await ScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.Carriers WHERE PartyId=@PartyId AND BusinessId=@BusinessId;",
            new SqlParameter("@PartyId", customer.PartyId), new SqlParameter("@BusinessId", fixture.BusinessId)));
        Assert.Equal(1, await ScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.PartySites WHERE PartyId=@PartyId AND Code=N'PRINCIPAL';",
            new SqlParameter("@PartyId", customer.PartyId)));
        Assert.True(await ScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.PosSynchronizationOutboxMessages WHERE BusinessId=@BusinessId AND Stream=N'Customers';",
            new SqlParameter("@BusinessId", fixture.BusinessId)) >= synchronizationMessagesBefore + 3);
    }
    private static CreateCustomerRequest CustomerRequest(
        Guid operationId,
        Guid businessId,
        Guid countryId,
        Guid divisionId,
        Guid cityId,
        string identification,
        string displayName,
        string siteName,
        CustomerPricingInput? pricing) =>
        new(
            operationId,
            businessId,
            new PartyInput(
                PartyTypes.NaturalPerson,
                countryId,
                "CC",
                identification,
                null,
                displayName,
                null,
                "Ada",
                "Cliente",
                "cliente@auraly.test",
                "3001234567"),
            new PartySiteInput(
                "PRINCIPAL",
                siteName,
                countryId,
                divisionId,
                cityId,
                "Calle 1 # 2-3",
                "Barrio escrito por el usuario",
                null,
                "sede@auraly.test",
                "3001234567"),
            pricing);

    private static async Task<TResponse> PostAndReadAsync<TRequest, TResponse>(
        HttpClient client,
        string uri,
        TRequest request)
    {
        using var response = await client.PostAsJsonAsync(uri, request);
        var responseBody = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Created,
            $"Expected 201 Created but received {(int)response.StatusCode} {response.StatusCode}: {responseBody}");
        return await response.Content.ReadFromJsonAsync<TResponse>()
            ?? throw new InvalidOperationException($"Endpoint '{uri}' returned an empty body.");
    }

    private async Task ExecuteAsync(string sql, params SqlParameter[] parameters)
    {
        await using var connection = new SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddRange(parameters);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<T> ScalarAsync<T>(string sql, params SqlParameter[] parameters)
    {
        await using var connection = new SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddRange(parameters);
        var value = await command.ExecuteScalarAsync();
        return value is T typed ? typed : (T)Convert.ChangeType(value!, typeof(T));
    }
}
