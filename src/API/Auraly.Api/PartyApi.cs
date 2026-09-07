using System.Security.Claims;
using Auraly.Application.Parties;
using Auraly.Contracts.Parties;

namespace Auraly.Api;

public static class PartyApi
{
    public static IEndpointRouteBuilder MapPartyApi(this IEndpointRouteBuilder endpoints)
    {
        var customers = endpoints.MapGroup("/api/commerce/v1/customers")
            .RequireAuthorization("parties.user");

        customers.MapPost("/", async (
            HttpContext context, PartyService service, CreateCustomerRequest request, CancellationToken ct) =>
            await Handle(async () =>
            {
                var customer = await service.CreateCustomerAsync(context.User.ToPartyUserIdentity(), request, ct);
                return Results.Created($"/api/commerce/v1/customers/{customer.CustomerId}", customer);
            }));

        customers.MapGet("/", async (
            HttpContext context, PartyService service, int? page, int? pageSize,
            string? search, bool? isActive, CancellationToken ct) =>
            await Handle(async () => Results.Ok(await service.PageCustomersAsync(
                context.User.ToPartyUserIdentity(),
                page ?? 1,
                new CustomerPageRequest(pageSize ?? 50, search, isActive),
                ct))));

        customers.MapGet("/{customerId:guid}", async (
            HttpContext context, PartyService service, Guid customerId, CancellationToken ct) =>
            await Handle(async () =>
            {
                var customer = await service.GetCustomerAsync(
                    context.User.ToPartyUserIdentity(), customerId, ct);
                return customer is null ? Results.NotFound() : Results.Ok(customer);
            }));

        customers.MapGet("/by-identification", async (
            HttpContext context, PartyService service, Guid countryId,
            string identificationType, string identification, CancellationToken ct) =>
            await Handle(async () =>
            {
                var customer = await service.FindCustomerAsync(
                    context.User.ToPartyUserIdentity(), countryId, identificationType, identification, ct);
                return customer is null ? Results.NotFound() : Results.Ok(customer);
            }));

        customers.MapPost("/{customerId:guid}/sites", async (
            HttpContext context, PartyService service, Guid customerId,
            AddPartySiteRequest request, CancellationToken ct) =>
            await Handle(async () => Results.Created(
                $"/api/commerce/v1/customers/{customerId}/sites",
                await service.AddSiteAsync(
                    context.User.ToPartyUserIdentity(), customerId, request, ct))));

        customers.MapPut("/{customerId:guid}/sites/{siteId:guid}", async (
            HttpContext context, PartyService service, Guid customerId, Guid siteId,
            UpdatePartySiteRequest request, CancellationToken ct) =>
            await Handle(async () => Results.Ok(await service.UpdateSiteAsync(
                context.User.ToPartyUserIdentity(), customerId, siteId, request, ct))));

        var geography = endpoints.MapGroup("/api/commerce/v1/masters/geography")
            .RequireAuthorization("parties.user");

        geography.MapGet("/countries", async (
            HttpContext context, GeographyService service, bool? includeInactive, CancellationToken ct) =>
            await Handle(async () => Results.Ok(await service.CountriesAsync(
                context.User.ToPartyUserIdentity(), includeInactive ?? false, ct))));

        geography.MapGet("/hierarchy", async (
            HttpContext context, GeographyService service, bool? includeInactive, CancellationToken ct) =>
            await Handle(async () => Results.Ok(await service.HierarchyAsync(
                context.User.ToPartyUserIdentity(), includeInactive ?? false, ct))));

        geography.MapGet("/countries/{countryId:guid}/divisions", async (
            HttpContext context, GeographyService service, Guid countryId,
            bool? includeInactive, CancellationToken ct) =>
            await Handle(async () => Results.Ok(await service.DivisionsAsync(
                context.User.ToPartyUserIdentity(), countryId, includeInactive ?? false, ct))));

        geography.MapGet("/divisions/{divisionId:guid}/cities", async (
            HttpContext context, GeographyService service, Guid divisionId,
            bool? includeInactive, CancellationToken ct) =>
            await Handle(async () => Results.Ok(await service.CitiesAsync(
                context.User.ToPartyUserIdentity(), divisionId, includeInactive ?? false, ct))));

        geography.MapPost("/countries", async (
            HttpContext context, GeographyService service, SaveCountryRequest request, CancellationToken ct) =>
            await Handle(async () => Results.Created(
                "/api/commerce/v1/masters/geography/countries",
                await service.CreateCountryAsync(context.User.ToPartyUserIdentity(), request, ct))));

        geography.MapPost("/divisions", async (
            HttpContext context, GeographyService service,
            SaveAdministrativeDivisionRequest request, CancellationToken ct) =>
            await Handle(async () => Results.Created(
                "/api/commerce/v1/masters/geography/divisions",
                await service.CreateDivisionAsync(context.User.ToPartyUserIdentity(), request, ct))));

        geography.MapPost("/cities", async (
            HttpContext context, GeographyService service, SaveCityRequest request, CancellationToken ct) =>
            await Handle(async () => Results.Created(
                "/api/commerce/v1/masters/geography/cities",
                await service.CreateCityAsync(context.User.ToPartyUserIdentity(), request, ct))));

        geography.MapPut("/countries/{countryId:guid}", async (
            HttpContext context, GeographyService service, Guid countryId, SaveCountryRequest request, CancellationToken ct) =>
            await Handle(async () => Results.Ok(await service.UpdateCountryAsync(context.User.ToPartyUserIdentity(), countryId, request, ct))));
        geography.MapPut("/divisions/{divisionId:guid}", async (
            HttpContext context, GeographyService service, Guid divisionId, SaveAdministrativeDivisionRequest request, CancellationToken ct) =>
            await Handle(async () => Results.Ok(await service.UpdateDivisionAsync(context.User.ToPartyUserIdentity(), divisionId, request, ct))));
        geography.MapPut("/cities/{cityId:guid}", async (
            HttpContext context, GeographyService service, Guid cityId, SaveCityRequest request, CancellationToken ct) =>
            await Handle(async () => Results.Ok(await service.UpdateCityAsync(context.User.ToPartyUserIdentity(), cityId, request, ct))));

        var pos = endpoints.MapGroup("/api/pos/v1/customers")
            .RequireAuthorization("pos.enrolled");

        pos.MapGet("/geography/countries", async (
            HttpContext context, GeographyService service, Guid businessId, CancellationToken ct) =>
            await Handle(async () => Results.Ok(await service.CountriesAsync(
                context.User.ToPartyDeviceIdentity(businessId), false, ct))));

        pos.MapGet("/geography/hierarchy", async (
            HttpContext context, GeographyService service, Guid businessId, CancellationToken ct) =>
            await Handle(async () => Results.Ok(await service.HierarchyAsync(
                context.User.ToPartyDeviceIdentity(businessId), false, ct))));

        pos.MapGet("/geography/countries/{countryId:guid}/divisions", async (
            HttpContext context, GeographyService service, Guid businessId, Guid countryId, CancellationToken ct) =>
            await Handle(async () => Results.Ok(await service.DivisionsAsync(
                context.User.ToPartyDeviceIdentity(businessId), countryId, false, ct))));

        pos.MapGet("/geography/divisions/{divisionId:guid}/cities", async (
            HttpContext context, GeographyService service, Guid businessId, Guid divisionId, CancellationToken ct) =>
            await Handle(async () => Results.Ok(await service.CitiesAsync(
                context.User.ToPartyDeviceIdentity(businessId), divisionId, false, ct))));
        pos.MapPost("/", async (
            HttpContext context, PartyService service, CreateCustomerRequest request, CancellationToken ct) =>
            await Handle(async () => Results.Ok(await service.CreateCustomerAsync(
                context.User.ToPartyDeviceIdentity(request.BusinessId), request, ct))));

        pos.MapGet("/by-identification", async (
            HttpContext context, PartyService service, Guid businessId, Guid countryId,
            string identificationType, string identification, CancellationToken ct) =>
            await Handle(async () =>
            {
                var customer = await service.FindCustomerAsync(
                    context.User.ToPartyDeviceIdentity(businessId), countryId, identificationType, identification, ct);
                return customer is null ? Results.NotFound() : Results.Ok(customer);
            }));

        return endpoints;
    }

    private static async Task<IResult> Handle(Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (PartyForbiddenException exception)
        {
            return Results.Problem(exception.Message, statusCode: StatusCodes.Status403Forbidden);
        }
        catch (PartyValidationException exception)
        {
            return Results.Problem(exception.Message, statusCode: StatusCodes.Status400BadRequest);
        }
        catch (PartyConflictException exception)
        {
            return Results.Problem(
                exception.Message, statusCode: StatusCodes.Status409Conflict, title: "PartyConflict");
        }
    }
}

public static class PartyClaimsPrincipalExtensions
{
    public static PartyActorIdentity ToPartyUserIdentity(this ClaimsPrincipal principal) =>
        new(
            RequiredGuid(principal, ClaimTypes.NameIdentifier),
            RequiredGuid(principal, "tenant_id"),
            RequiredGuid(principal, "business_id"),
            principal.FindAll("permission")
                .Select(claim => claim.Value)
                .ToHashSet(StringComparer.Ordinal));

    public static PartyActorIdentity ToPartyDeviceIdentity(
        this ClaimsPrincipal principal,
        Guid businessId) =>
        new(
            RequiredGuid(principal, PosAuthenticationDefaults.DeviceIdClaim),
            RequiredGuid(principal, PosAuthenticationDefaults.TenantIdClaim),
            businessId,
            new HashSet<string>(StringComparer.Ordinal),
            true);

    private static Guid RequiredGuid(ClaimsPrincipal principal, string claimType) =>
        Guid.TryParse(principal.FindFirstValue(claimType), out var value)
            ? value
            : throw new PartyForbiddenException(
                $"The authenticated identity lacks claim '{claimType}'.");
}
