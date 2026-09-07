using System.Security.Cryptography;
using System.Text;
using Auraly.BuildingBlocks.Domain.Identifiers;
using Auraly.BuildingBlocks.Infrastructure.Identifiers;
using Auraly.Contracts.Catalog;
using Auraly.Contracts.Authorization;
using Auraly.Contracts.Parties;
using Auraly.Contracts.Sales;
using Auraly.Contracts.WorkSessions;
using Auraly.Pos.Edge.Infrastructure;

namespace Auraly.Pos.Edge.Host;

public sealed class PosEdgeRuntimeContext(
    TenantId tenantId,
    BusinessId businessId,
    WarehouseId warehouseId,
    DeviceId deviceId,
    bool warehouseAllowsNegativeStock)
{
    private int allowsNegativeStock = warehouseAllowsNegativeStock ? 1 : 0;
    public TenantId TenantId { get; } = tenantId;
    public BusinessId BusinessId { get; } = businessId;
    public WarehouseId WarehouseId { get; } = warehouseId;
    public DeviceId DeviceId { get; } = deviceId;
    public bool WarehouseAllowsNegativeStock => Volatile.Read(ref allowsNegativeStock) == 1;

    public void ApplyWarehousePolicy(bool value) =>
        Volatile.Write(ref allowsNegativeStock, value ? 1 : 0);

    public PosDraftScope ScopeFor(PosLocalUserSession session) => new(
        BusinessId,
        WarehouseId,
        DeviceId,
        new WorkSessionId(session.WorkSessionId),
        new UserId(session.UserId));
}

public sealed record PosWorkstationIdentity(
    string DeviceSeriesCode,
    string BusinessName,
    string WarehouseName,
    string UserDisplayName,
    string CompanyName,
    string? CompanyLogoSource);

public sealed record CaptureRequest(string Value, Guid? CustomerId);
public sealed record QuantityRequest(decimal Quantity);
public sealed record DiscountRequest(decimal Discount);
public sealed record UpdateDraftLinesRequest(IReadOnlyList<UpdateDraftLineRequest> Lines);
public sealed record UpdateDraftLineRequest(Guid LineId, string Description, decimal UnitPrice, decimal Discount);
public sealed record SelectCustomerRequest(Guid? CustomerId);
public sealed record SaveTemporaryRequest(string Name, string? Reference, string? Observation);
public sealed record DirectPrintReceiptRequest(
    Guid DocumentId,
    string DocumentType,
    string DocumentNumber,
    string? FiscalNumber,
    DateTimeOffset IssuedAt,
    string CustomerIdentification,
    IReadOnlyCollection<PosReceiptLine> Lines,
    IReadOnlyCollection<OfflineSalePayment> Payments,
    decimal UntaxedAmount,
    decimal TaxAmount,
    decimal PayableAmount,
    string? Cufe,
    string? QrPayload,
    string? CompanyName = null,
    string? CompanyLogoSource = null,
    string? CustomerName = null,
    string? BusinessName = null,
    string? WarehouseName = null);

public static class PosEdgeHostApplication
{
    public static WebApplication Build(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Host.UseWindowsService(options =>
        {
            options.ServiceName = "Auraly POS Edge";
        });
        builder.WebHost.UseUrls(
            builder.Configuration["PosEdge:Url"] ?? "http://127.0.0.1:47831");

        var databasePath = builder.Configuration["PosEdge:DatabasePath"];
        if (string.IsNullOrWhiteSpace(databasePath))
            databasePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Auraly",
                "PosEdge",
                "auraly-pos.db");
        var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        var connectionString = $"Data Source={databasePath}";

        var sessionToken = Required(builder.Configuration, "PosEdge:SessionToken");
        if (Encoding.UTF8.GetByteCount(sessionToken) < 32)
            throw new InvalidOperationException("PosEdge:SessionToken must contain at least 32 bytes.");
        var allowedOrigin = Required(builder.Configuration, "PosEdge:AllowedOrigin");
        var serverUrl = Required(builder.Configuration, "PosEdge:ServerUrl");
        var keyDirectory = builder.Configuration["PosEdge:SecretKeyDirectory"];
        if (string.IsNullOrWhiteSpace(keyDirectory))
            keyDirectory = Path.Combine(
                Path.GetDirectoryName(Path.GetFullPath(databasePath))!, "keys");
        var packagePath = builder.Configuration["PosEdge:EnrollmentPackagePath"];
        if (string.IsNullOrWhiteSpace(packagePath))
            packagePath = Path.Combine(
                Path.GetDirectoryName(Path.GetFullPath(databasePath))!,
                "enrollment.protected");
        var enrollmentStore = new PosEdgeEnrollmentStore(packagePath, keyDirectory);
        var identityRecovery = new PosLocalDeviceIdentityRecovery(databasePath);
        var enrollment = enrollmentStore.Load();
        if (enrollment is null &&
            string.IsNullOrWhiteSpace(builder.Configuration["PosEdge:DeviceId"]))
            return BuildEnrollmentRequired(
                builder,
                sessionToken,
                allowedOrigin,
                serverUrl,
                enrollmentStore,
                identityRecovery,
                databasePath);
        if (enrollment is not null)
            builder.Configuration.AddInMemoryCollection(
                PosEdgeEnrollmentStore.ToConfiguration(
                    enrollment,
                    keyDirectory,
                    databasePath));
        var credentials = new PosDeviceCredentials(
            RequiredGuid(builder.Configuration, "PosEdge:DeviceId"),
            Required(builder.Configuration, "PosEdge:DeviceSecret"));
        var tenantId = RequiredGuid(builder.Configuration, "PosEdge:TenantId");
        var runtime = new PosEdgeRuntimeContext(
            new TenantId(tenantId),
            new BusinessId(RequiredGuid(builder.Configuration, "PosEdge:BusinessId")),
            new WarehouseId(RequiredGuid(builder.Configuration, "PosEdge:WarehouseId")),
            new DeviceId(RequiredGuid(builder.Configuration, "PosEdge:DeviceId")),
            builder.Configuration.GetValue<bool>("PosEdge:WarehouseAllowsNegativeStock"));

        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<IAuralyIdGenerator, Uuid7AuralyIdGenerator>();
        builder.Services.AddSingleton(runtime);
        builder.Services.AddSingleton(new PosOperationalScope(
            runtime.BusinessId.Value,
            runtime.WarehouseId.Value));
        builder.Services.AddSingleton<PosLocalSessionAccessor>();
        builder.Services.AddSingleton(enrollmentStore);
        builder.Services.AddSingleton(identityRecovery);
        builder.Services.AddSingleton<PosEdgeEnrollmentClient>();
        builder.Services.AddSingleton(sp => new PosLocalIdentityStore(
            connectionString,
            keyDirectory,
            sp.GetRequiredService<IAuralyIdGenerator>(),
            sp.GetRequiredService<TimeProvider>()));
        builder.Services.Configure<PosOfflineLeaseTrustOptions>(
            builder.Configuration.GetSection(PosOfflineLeaseTrustOptions.SectionName));
        builder.Services.AddSingleton<PosOfflineLeaseVerifier>();
        builder.Services.AddSingleton(sp => new PosOfflineLeaseStore(
            connectionString,
            tenantId,
            credentials.DeviceId,
            sp.GetRequiredService<PosOfflineLeaseVerifier>(),
            sp.GetRequiredService<TimeProvider>()));
        builder.Services.AddSingleton<PosOfflineLeaseClient>();
        builder.Services.AddSingleton<PosEdgeAuthenticationService>();
        builder.Services.AddSingleton<PosEnrollmentSessionCompleter>();
        builder.Services.AddSingleton<PosEnrollmentRevocationHandler>();
        builder.Services.AddSingleton(new PosWorkstationIdentity(
            Required(builder.Configuration, "PosEdge:Documents:SalesInvoice:SeriesCode"),
            OptionalLabel(builder.Configuration, "PosEdge:BusinessName", "Negocio sin nombre"),
            OptionalLabel(builder.Configuration, "PosEdge:WarehouseName", "Bodega sin nombre"),
            OptionalLabel(builder.Configuration, "PosEdge:UserDisplayName", "Usuario sin nombre"),
            OptionalLabel(builder.Configuration, "PosEdge:CompanyName",
                OptionalLabel(builder.Configuration, "PosEdge:BusinessName", "Negocio sin nombre")),
            builder.Configuration["PosEdge:CompanyLogoSource"]));
        builder.Services.AddSingleton(new PosCatalogStore(connectionString));
        builder.Services.AddSingleton(sp => new PosDraftStore(
            connectionString,
            sp.GetRequiredService<IAuralyIdGenerator>(),
            sp.GetRequiredService<TimeProvider>()));
        builder.Services.AddSingleton<PosServerConnectionState>();
        builder.Services.AddSingleton<PosPushConnectionState>();
        builder.Services.AddSingleton(sp => new HttpClient(
            new PosServerConnectionHandler(
                new HttpClientHandler(),
                sp.GetRequiredService<PosServerConnectionState>()))
        {
            BaseAddress = new Uri(serverUrl)
        });
        builder.Services.AddSingleton(credentials);
        builder.Services.AddSingleton<PosSynchronizationEventLog>();
        builder.Services.AddSingleton<IPosSynchronizationEventSink>(sp =>
            sp.GetRequiredService<PosSynchronizationEventLog>());
        builder.Services.AddSingleton<PosWarehousePolicySink>();
        builder.Services.AddSingleton<IPosWarehousePolicySink>(sp =>
            sp.GetRequiredService<PosWarehousePolicySink>());
        builder.Services.AddSingleton<PosCatalogSynchronizer>();
        builder.Services.AddSingleton<PosIdentitySynchronizer>();
        builder.Services.AddSingleton(new PosCustomerGeographyStore(connectionString));
        builder.Services.AddSingleton<PosCustomerServerClient>();
        builder.Services.AddSingleton(sp => new PosCustomerOutboxStore(
            connectionString,
            sp.GetRequiredService<IAuralyIdGenerator>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<PosOperationalScope>()));
        builder.Services.AddSingleton<PosCustomerOutboxUploader>();
        builder.Services.AddSingleton<PosProductAvailabilityServerClient>();
        builder.Services.AddSingleton<PosRemoteApprovalClient>();
        builder.Services.AddSingleton<PosSensitiveActionAuthorizer>();
        builder.Services.AddSingleton<PosOrderServerClient>();
        builder.Services.AddSingleton<PosOrderRecoveryService>();
        builder.Services.AddSingleton<IPosInventoryAvailabilityClient>(
            sp => sp.GetRequiredService<PosCatalogSynchronizer>());
        builder.Services.AddSingleton<PosCaptureService>();
        builder.Services.AddSingleton<PosDraftPricingService>();
        builder.Services.AddSingleton<PosCustomerSelectionService>();
        builder.Services.AddPosSaleCompletion(
            builder.Configuration,
            connectionString,
            databasePath,
            runtime,
            credentials);
        // The unified Outbox schema must exist before document-specific local
        // stores begin accepting cash movements or work-session closures.
        builder.Services.AddHostedService<PosEdgeStorageInitializer>();
        builder.Services.AddSingleton(sp => new PosCashMovementStore(
            connectionString,
            sp.GetRequiredService<TimeProvider>()));
        builder.Services.AddSingleton<PosCashMovementServerClient>();
        builder.Services.AddSingleton<PosWorkSessionClosureServerClient>();
        builder.Services.AddSingleton(sp => new PosOfflineWorkSessionClosureStore(
            connectionString,
            sp.GetRequiredService<TimeProvider>()));
        builder.Services.AddSingleton<PosOfflineWorkSessionClosureService>();
        builder.Services.AddSingleton<PosWorkSessionClosureUploader>();
        builder.Services.AddSingleton(sp => new PosLocalWorkSessionStore(
            connectionString,
            sp.GetRequiredService<IAuralyIdGenerator>(),
            sp.GetRequiredService<TimeProvider>(),
            runtime));
        builder.Services.AddSingleton(sp => new PosWorkSessionOpenUploader(
            connectionString,
            sp.GetRequiredService<HttpClient>(),
            credentials,
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<PosSynchronizationEventLog>()));
        builder.Services.AddSingleton(sp => new PosUnifiedOutboxDispatcher(
            connectionString,
            sp.GetRequiredService<TimeProvider>()));
        builder.Services.AddSingleton<PosPendingClosureAuthorizationStore>();
        builder.Services.AddHostedService<PosCashMovementStorageInitializer>();
        builder.Services.AddHostedService<PosWorkSessionClosureStorageInitializer>();
        builder.Services.AddSingleton<IPosSaleUploadClient>(sp =>
            new HttpPosSaleUploadClient(
                sp.GetRequiredService<HttpClient>(),
                credentials.Secret));
        builder.Services.AddSingleton<PosEdgeOutboxUploader>();
        builder.Services.AddSingleton<IPosFiscalStatusClient, HttpPosFiscalStatusClient>();
        builder.Services.AddSingleton<PosFiscalStatusSynchronizer>();
        builder.Services.AddSingleton<PosFiscalProvisioningSynchronizer>();
        builder.Services.AddSingleton<PosSynchronizationSignal>();
        builder.Services.AddSingleton<PosUiStateSignal>();
        builder.Services.AddSingleton<PosSynchronizationState>();
        builder.Services.AddSingleton<PosSynchronizationLaneExecutor>();
        builder.Services.AddSingleton<PosSynchronizationWork>();
        builder.Services.AddSingleton(sp => new PosWebPubSubConnection(
            sp.GetRequiredService<HttpClient>(),
            credentials,
            sp.GetRequiredService<PosSynchronizationSignal>(),
            sp.GetRequiredService<PosServerConnectionState>(),
            sp.GetRequiredService<PosPushConnectionState>(),
            sp.GetRequiredService<PosUiStateSignal>(),
            sp.GetRequiredService<PosSynchronizationEventLog>(),
            sp.GetRequiredService<PosEnrollmentRevocationHandler>(),
            tenantId,
            runtime.BusinessId.Value));
        builder.Services.AddHostedService<PosEventDrivenSynchronizationHostedService>();
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            if (!IsLoopback(context.Connection.RemoteIpAddress))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            var origin = context.Request.Headers.Origin.ToString();
            if (!string.IsNullOrEmpty(origin) &&
                !string.Equals(origin, allowedOrigin, StringComparison.Ordinal))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            if (HttpMethods.IsOptions(context.Request.Method))
            {
                if (string.IsNullOrEmpty(origin))
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    return;
                }
                SetCorsHeaders(context.Response, allowedOrigin);
                context.Response.Headers.AccessControlAllowMethods = "GET,POST,PUT,DELETE,OPTIONS";
                context.Response.Headers.AccessControlAllowHeaders = "Content-Type,X-Auraly-Edge-Session,X-Auraly-User-Session,X-Auraly-Supervisor-Secret,X-Auraly-Approval-Id,X-Auraly-Operation-Id";
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                return;
            }
            var presented = context.Request.Headers["X-Auraly-Edge-Session"].ToString();
            if (!FixedEquals(sessionToken, presented))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            if (!string.IsNullOrEmpty(origin))
            {
                SetCorsHeaders(context.Response, allowedOrigin);
            }
            PosLocalSessionAccessor? localSessions = null;
            if (RequiresLocalUserSession(context.Request.Path))
            {
                var userToken = context.Request.Headers["X-Auraly-User-Session"].ToString();
                var identities = context.RequestServices
                    .GetRequiredService<PosLocalIdentityStore>();
                var userSession = await identities.ResolveAsync(
                    userToken, context.RequestAborted);
                if (userSession is not null)
                {
                    var leases = context.RequestServices
                        .GetRequiredService<PosOfflineLeaseStore>();
                    var leaseId = await leases.ActiveLeaseIdForUserAsync(
                        userSession.UserId, context.RequestAborted);
                    // A login completed while the enrolled POS is genuinely
                    // offline has no server lease to validate yet. It remains a
                    // valid local login; only a lease that the server has
                    // actually issued can later be reported as replaced.
                    var loginIsActive = true;
                    if (leaseId is not null)
                    {
                        try
                        {
                            var server = context.RequestServices
                                .GetRequiredService<PosOfflineLeaseClient>();
                            loginIsActive = await server.IsActiveAsync(
                                leaseId.Value,
                                userSession.UserId,
                                context.RequestAborted);
                        }
                        catch (HttpRequestException exception)
                            when (exception.StatusCode is null)
                        {
                            // A signed local lease remains authoritative while the
                            // enrolled POS is genuinely offline.
                            loginIsActive = true;
                        }
                        catch (TaskCanceledException)
                            when (!context.RequestAborted.IsCancellationRequested)
                        {
                            loginIsActive = true;
                        }
                    }
                    if (!loginIsActive)
                    {
                        await identities.RevokeActiveSessionsAsync(
                            "ReplacedByNewLogin", context.RequestAborted);
                        userSession = null;
                    }
                }
                if (userSession is null)
                {
                    var endReason = await identities.SessionEndReasonAsync(
                        userToken, context.RequestAborted);
                    var replaced = string.Equals(
                        endReason, "ReplacedByNewLogin", StringComparison.Ordinal);
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    await context.Response.WriteAsJsonAsync(new
                    {
                        code = replaced ? "LoginReplaced" : "LocalLoginRequired",
                        detail = replaced
                            ? "Tu usuario inició sesión en otro navegador o caja. Esta sesión se cerrará."
                            : "Inicia sesión en este dispositivo para continuar."
                    });
                    return;
                }
                if (userSession.WorkSessionId == Guid.Empty &&
                    RequiresOperationalWorkSession(context.Request.Path))
                {
                    context.Response.StatusCode = StatusCodes.Status409Conflict;
                    await context.Response.WriteAsJsonAsync(new
                    {
                        code = "WorkSessionRequired",
                        detail = "Entra nuevamente al punto de venta para abrir la sesión de caja local."
                    });
                    return;
                }
                localSessions = context.RequestServices
                    .GetRequiredService<PosLocalSessionAccessor>();
                localSessions.Current = userSession;
            }
            try
            {
                await next(context);
            }
            catch (PosLocalApprovalException error)
            {
                context.Response.StatusCode = error.Code == "ApprovalRequired"
                    ? StatusCodes.Status428PreconditionRequired
                    : StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new
                {
                    code = error.Code,
                    detail = error.Message
                });
            }
            catch (PosOrderServerException error)
            {
                context.Response.StatusCode = error.StatusCode is >= 400 and <= 599
                    ? error.StatusCode
                    : StatusCodes.Status502BadGateway;
                await context.Response.WriteAsJsonAsync(new
                {
                    code = "OrderServerRejected",
                    detail = "El servidor rechaz\u00F3 la operaci\u00F3n de pedidos."
                });
            }            catch (InvalidOperationException error)
                when (string.Equals(
                    error.Message,
                    "The sale was already issued and is locked until its receipt is printed.",
                    StringComparison.Ordinal))
            {
                context.Response.StatusCode = StatusCodes.Status409Conflict;
                await context.Response.WriteAsJsonAsync(new
                {
                    code = "IssuedPendingPrint",
                    detail = "La factura ya fue emitida y estÃ¡ pendiente de imprimir la tirilla. Presiona F1 para reintentar la impresiÃ³n."
        });
            }
            finally
            {
                if (localSessions is not null) localSessions.Current = null;
            }
        });

        var edge = app.MapGroup("/edge/v1");
        edge.MapPosPeripheralEndpoints();
        edge.MapPost("/cash-drawer/open", async (
            HttpContext http,
            PosCashDrawer cashDrawer,
            PosLocalIdentityStore identities,
            CancellationToken ct) =>
        {
            // In enrolled/offline operation the local identity is authoritative.
            // In online operation the web application only exposes this call after
            // the authenticated server permission has been resolved.
            var user = await identities.ResolveAsync(
                http.Request.Headers["X-Auraly-User-Session"].ToString(), ct);
            if (user is null)
                return Results.Problem(
                    "Inicia sesión en este dispositivo para abrir el cajón.",
                    statusCode: StatusCodes.Status401Unauthorized,
                    title: "LocalLoginRequired");
            if (user is not null &&
                !user.Permissions.Contains(WorkSessionPermissionCodes.OpenCashDrawer))
                return Results.Problem(
                    "Tu usuario no tiene permiso para abrir el cajón de dinero.",
                    statusCode: StatusCodes.Status403Forbidden,
                    title: "PermissionDenied");
            try
            {
                cashDrawer.Open();
                return Results.NoContent();
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException)
            {
                return Results.Problem(
                    exception.Message,
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });
        edge.MapPost("/enrollment/redeem", async (
            LocalPosEnrollmentRequest request,
            PosEdgeEnrollmentClient client,
            IHostApplicationLifetime lifetime,
            CancellationToken ct) =>
        {
            try
            {
                var result = await client.RedeemAsync(request, ct);
                _ = Task.Run(async () =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(1));
                    lifetime.StopApplication();
                });
                return Results.Ok(result);
            }
            catch (PosEnrollmentServerException exception)
            {
                return Results.Problem(
                    exception.Message,
                    statusCode: exception.StatusCode,
                    title: exception.Title);
            }
            catch (HttpRequestException exception)
            {
                return Results.Problem(
                    exception.Message,
                    statusCode: StatusCodes.Status502BadGateway,
                    title: "EnrollmentServerUnavailable");
            }
        });
        edge.MapGet("/events", async (
            HttpContext context,
            PosUiStateSignal uiState,
            CancellationToken ct) =>
        {
            context.Response.Headers.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache";
            context.Response.Headers.Connection = "keep-alive";
            var (subscriptionId, reader) = uiState.Subscribe();
            try
            {
                await context.Response.WriteAsync("event: state\ndata: ready\n\n", ct);
                await context.Response.Body.FlushAsync(ct);
                await foreach (var _ in reader.ReadAllAsync(ct))
                {
                    await context.Response.WriteAsync("event: state\ndata: changed\n\n", ct);
                    await context.Response.Body.FlushAsync(ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
            finally
            {
                uiState.Unsubscribe(subscriptionId);
            }
        });
        edge.MapPost("/auth/login", async (
            PosLocalLoginRequest request,
            PosEdgeAuthenticationService authentication,
            CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await authentication.LoginAsync(request, ct));
            }
            catch (PosLocalLoginException error)
            {
                var status = error.Code == "Locked"
                    ? StatusCodes.Status423Locked
                    : error.Code == "IdentityUnavailable"
                        ? StatusCodes.Status503ServiceUnavailable
                        : error.Code == "OfflineLeaseConflict"
                            ? StatusCodes.Status409Conflict
                        : StatusCodes.Status401Unauthorized;
                return Results.Json(
                    new { code = error.Code, detail = error.Message },
                    statusCode: status);
            }
        });
        edge.MapGet("/cash-movement-reasons", async (
            string? direction,
            PosCashMovementStore store,
            PosEdgeRuntimeContext context,
            CancellationToken ct) =>
        {
            if (!CashMovementDirections.IsSupported(direction ?? string.Empty))
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    [nameof(direction)] = ["La direccion debe ser In u Out."]
                });
            return Results.Ok(await store.ListReasonsAsync(
                context.BusinessId.Value, direction!, ct));
        });
        edge.MapPost("/cash-movements", async (
            QueueLocalCashMovementRequest request,
            PosCashMovementStore store,
            PosSynchronizationSignal synchronization,
            PosEdgeRuntimeContext context,
            PosLocalSessionAccessor sessions,
            CancellationToken ct) =>
        {
            try
            {
                var user = sessions.Required();
                var acceptance = await store.QueueAsync(
                    context.BusinessId.Value,
                    user.WorkSessionId,
                    user.UserId,
                    request,
                    ct);
                synchronization.Signal(PosSynchronizationTrigger.LocalOutbox);
                return Results.Accepted(
                    "/edge/v1/cash-movements/" + request.DocumentId.ToString("D"),
                    acceptance);
            }
            catch (ArgumentException exception)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    [nameof(request)] = [exception.Message]
                });
            }
        });

        edge.MapPost("/approvals", async (
            CreatePosApprovalRequest request,
            PosRemoteApprovalClient approvals,
            PosLocalSessionAccessor sessions,
            CancellationToken ct) =>
        {
            var user=sessions.Required();
            if(request.BusinessId!=runtime.BusinessId.Value || request.DeviceId!=runtime.DeviceId.Value ||
               request.WorkSessionId!=user.WorkSessionId)
                return Results.BadRequest(new { code="InvalidScope", detail="La solicitud no coincide con el contexto local." });
            try
            {
                return Results.Ok(await approvals.CreateAsync(
                    user, request.DraftId, request.LineId, request.PermissionResource, request.ContextJson, ct));
            }
            catch (PosRemoteApprovalException exception)
            {
                return Results.Problem(
                    exception.Message,
                    statusCode: StatusCodes.Status409Conflict,
                    title: exception.Code);
            }
        });
        edge.MapPost("/work-sessions/current", async (
            PosLocalSessionAccessor sessions,
            PosLocalIdentityStore identities,
            PosLocalWorkSessionStore workSessions,
            PosSynchronizationSignal synchronization,
            CancellationToken ct) =>
        {
            var authenticated = sessions.Required();
            var active = await workSessions.OpenOrResumeAsync(
                authenticated.UserId, ct);
            if (authenticated.WorkSessionId != active.WorkSessionId)
                await identities.AssignWorkSessionAsync(
                    authenticated.SessionId, active.WorkSessionId, ct);
            synchronization.Signal(PosSynchronizationTrigger.LocalOutbox);
            return Results.Ok(authenticated with
            {
                WorkSessionId = active.WorkSessionId
            });
        });
        edge.MapGet("/approvals/{approvalRequestId:guid}", async (
            Guid approvalRequestId,
            PosRemoteApprovalClient approvals,
            PosLocalSessionAccessor sessions,
            CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await approvals.GetAsync(
                    approvalRequestId, sessions.Required(), ct));
            }
            catch (PosRemoteApprovalException exception)
            {
                return Results.Problem(
                    exception.Message,
                    statusCode: StatusCodes.Status409Conflict,
                    title: exception.Code);
            }
        });
        edge.MapGet("/auth/session", (
            PosLocalSessionAccessor sessions) =>
            Results.Ok(sessions.Required()));
        edge.MapPost("/auth/logout", async (
            HttpContext http,
            PosEdgeAuthenticationService authentication,
            CancellationToken ct) =>
        {
            await authentication.LogoutAsync(
                http.Request.Headers["X-Auraly-User-Session"].ToString(), ct);
            return Results.NoContent();
        });
        edge.MapPost("/synchronization/refresh", (PosSynchronizationSignal synchronization) => { synchronization.Signal(PosSynchronizationTrigger.All); return Results.Accepted(); });
        edge.MapGet("/synchronization/events", async (
            int? take,
            PosSynchronizationEventLog events,
            PosLocalIdentityStore identities,
            HttpContext http,
            CancellationToken ct) =>
        {
            var user = await identities.ResolveAsync(
                http.Request.Headers["X-Auraly-User-Session"].ToString(), ct);
            if (user is null || !user.Permissions.Contains(PosSynchronizationPermissions.ReadEvents))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            return Results.Ok(events.Read(take ?? 100));
        });
        edge.MapPost("/auth/complete-enrollment", async (
            PosEnrollmentSessionCompleter completer,
            CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await completer.CompleteAsync(ct));
            }
            catch (PosLocalLoginException error)
            {
                var status = error.Code is "IdentityUnavailable" or "CloudLoginRequired"
                    ? StatusCodes.Status409Conflict
                    : StatusCodes.Status401Unauthorized;
                return Results.Json(
                    new { code = error.Code, detail = error.Message },
                    statusCode: status);
            }
        });
        edge.MapGet("/health", async (
            HttpContext http,
            PosServerConnectionState server,
            PosPushConnectionState push,
            PosWorkstationIdentity workstation,
            PosCatalogStore catalog,
            PosLocalIdentityStore identities,
            PosEdgeSaleStore sales,
            PosCashMovementStore cashMovements,
            PosOfflineWorkSessionClosureStore closures,
            PosCustomerOutboxStore customers,
            PosSaleHostSettings saleSettings,
            PosSynchronizationState synchronizationState,
            TimeProvider timeProvider,
            CancellationToken ct) =>
        {
            var catalogStatus = await catalog.StatusAsync(ct);
            var identityReady = await identities.HasIdentitySnapshotAsync(ct);
            var syncStatus = synchronizationState.Current;
            var saleOutbox = await sales.ReadOutboxStatusAsync(ct);
            var cashOutbox = await cashMovements.ReadOutboxStatusAsync(ct);
            var closureOutbox = await closures.ReadStatusAsync(ct);
            var customerOutbox = await customers.ReadStatusAsync(ct);
            var pendingSynchronizationCount =
                saleOutbox.PendingCount + cashOutbox.PendingCount + closureOutbox.PendingCount +
                customerOutbox.PendingCount;
            var oldestPendingSynchronizationAt = new[]
                {
                    saleOutbox.OldestPendingAt,
                    cashOutbox.OldestPendingAt,
                    closureOutbox.OldestPendingAt,
                    customerOutbox.OldestPendingAt
                }
                .Where(value => value is not null)
                .Min();
            var lastSynchronizationError = closureOutbox.LastError
                ?? cashOutbox.LastError
                ?? customerOutbox.LastError
                ?? saleOutbox.LastError;
            var user = await identities.ResolveAsync(
                http.Request.Headers["X-Auraly-User-Session"].ToString(), ct);
            var fiscalWarnings = await sales.GetFiscalWarningsAsync(
                runtime.DeviceId, timeProvider.GetUtcNow(), ct);
            var fiscalPreview = await sales.PreviewNextFiscalNumberAsync(
                runtime.DeviceId, timeProvider.GetUtcNow(), ct);
            var status = !identityReady
                ? "IdentitySynchronizing"
                : catalogStatus.Status != "Ready"
                    ? "Synchronizing"
                    : user is null
                        ? "LoginRequired"
                        : "Ready";
            return Results.Ok(new
            {
                status,
                serverConnected = server.IsConnected,
                pushConnected = push.IsConnected,
                deviceSeriesCode = workstation.DeviceSeriesCode,
                businessId = runtime.BusinessId.Value,
                warehouseId = runtime.WarehouseId.Value,
                businessName = workstation.BusinessName,
                warehouseName = workstation.WarehouseName,
                warehouseAllowsNegativeStockSales = runtime.WarehouseAllowsNegativeStock,
                userDisplayName = user?.DisplayName ?? string.Empty,
                userId = user?.UserId,
                workSessionId = user is null || user.WorkSessionId == Guid.Empty
                    ? (Guid?)null
                    : user.WorkSessionId,
                deviceId = runtime.DeviceId.Value,
                fiscalReady = saleSettings.Fiscal is not null && fiscalPreview.IsAvailable,
                fiscalWarnings,
                permissions = user?.Permissions ?? Array.Empty<string>(),
                identityReady,
                catalogStatus = catalogStatus.Status,
                catalogCursor = catalogStatus.Cursor,
                catalogUpdatedAt = catalogStatus.UpdatedAt,
                synchronizationInProgress = syncStatus.IsSynchronizing,
                lastSynchronizationAt = syncStatus.LastSuccessfulAt ?? catalogStatus.UpdatedAt,
                lastSynchronizationFailed = syncStatus.LastAttemptFailed ||
                    !string.IsNullOrWhiteSpace(lastSynchronizationError),
                pendingSynchronizationCount,
                oldestPendingSynchronizationAt,
                lastSynchronizationError
            });
        });
        edge.MapGet("/drafts/active", async (
            PosDraftStore drafts,
            PosEdgeRuntimeContext context,
            PosLocalSessionAccessor sessions,
            CancellationToken ct) =>
            Results.Ok(await drafts.GetOrCreateActiveAsync(
                context.ScopeFor(sessions.Required()), ct)));
        edge.MapGet("/catalog/products", async (
            string? search,
            int? skip,
            int? take,
            Guid? customerId,
            PosCatalogStore catalog,
            CancellationToken ct) =>
        {
            var pageSize = Math.Clamp(take ?? 50, 1, 50);
            var offset = Math.Max(skip ?? 0, 0);
            var values = (await catalog.SearchAsync(
                search ?? string.Empty,
                offset,
                pageSize + 1,
                ct)).ToArray();
            var hasMore = values.Length > pageSize;
            var priced = new List<object>(Math.Min(values.Length, pageSize));
            foreach (var value in values.Take(pageSize))
            {
                var resolved = await catalog.ResolvePriceAsync(value.ProductId, customerId, 1m, ct);
                priced.Add(new {
                    value.ProductId,value.ProductCode,value.Reference,value.Name,value.BaseUnitCode,
                    value.TaxCode,value.TaxRate,unitPrice=resolved.Amount,resolved.CurrencyCode,
                    value.IsActive,value.IsWeighable,value.AllowsFractionalSale,priceSource=resolved.Source
                });
            }
            return Results.Ok(new
            {
                items = priced,
                hasMore,
                nextOffset = hasMore ? offset + pageSize : (int?)null
            });
        });
        edge.MapGet("/catalog/products/{productId:guid}/warehouse-availability", async (
            Guid productId,
            PosProductAvailabilityServerClient availability,
            PosLocalSessionAccessor sessions,
            CancellationToken ct) =>
        {
            const string inventoryAvailabilityRead = "pos.inventory.availability.read";
            const string businessesRead = "businesses.read";
            var user = sessions.Required();
            if (!user.Permissions.Contains(inventoryAvailabilityRead)) return Results.Forbid();
            try
            {
                return Results.Ok(await availability.GetAsync(
                    productId, user.Permissions.Contains(businessesRead), ct));
            }
            catch (HttpRequestException exception)
            {
                return Results.Problem(
                    "No fue posible consultar las existencias del servidor.",
                    statusCode: StatusCodes.Status503ServiceUnavailable,
                    extensions: new Dictionary<string, object?> { ["remoteStatus"] = exception.StatusCode });
            }
        });
        edge.MapGet("/reference-options/{catalogCode}", async (
            string catalogCode,
            PosCatalogStore catalog,
            CancellationToken ct) =>
            Results.Ok(await catalog.ReferenceOptionsAsync(catalogCode, ct)));
        edge.MapGet("/settlement-configuration", async (
            PosCatalogStore catalog,
            CancellationToken ct) =>
            Results.Ok(await catalog.SettlementConfigurationAsync(ct)));
        edge.MapGet("/customers", async (
            string? search,
            int? skip,
            int? take,
            PosCatalogStore catalog,
            CancellationToken ct) =>
        {
            var pageSize = Math.Clamp(take ?? 50, 1, 50);
            var offset = Math.Max(skip ?? 0, 0);
            var values = (await catalog.SearchCustomersAsync(
                search ?? string.Empty,
                offset,
                pageSize + 1,
                ct)).ToArray();
            var hasMore = values.Length > pageSize;
            return Results.Ok(new
            {
                items = values.Take(pageSize),
                hasMore,
                nextOffset = hasMore ? offset + pageSize : (int?)null
            });
        });
        edge.MapGet("/customers/geography/countries", async (
            PosCustomerServerClient server,
            CancellationToken ct) => Results.Ok(await server.CountriesAsync(ct)));
        edge.MapGet("/customers/geography/countries/{countryId:guid}/divisions", async (
            Guid countryId,
            PosCustomerServerClient server,
            CancellationToken ct) => Results.Ok(await server.DivisionsAsync(countryId, ct)));
        edge.MapGet("/customers/geography/divisions/{divisionId:guid}/cities", async (
            Guid divisionId,
            PosCustomerServerClient server,
            CancellationToken ct) => Results.Ok(await server.CitiesAsync(divisionId, ct)));
        edge.MapPost("/customers", async (
            PosCreateCustomerInput request,
            PosCustomerOutboxStore customers,
            PosLocalSessionAccessor sessions,
            PosSynchronizationSignal synchronization,
            PosSynchronizationEventLog events,
            CancellationToken ct) =>
        {
            var user = sessions.Required();
            if (!user.Permissions.Contains(PartyPermissionCodes.PosCustomerCreate))
                return Results.Forbid();
            var customer = await customers.QueueAsync(request, user.WorkSessionId, ct);
            events.Record("Success", "Cliente", $"Cliente creado localmente: {customer.Name}",
                customer.Identification);
            synchronization.Signal(PosSynchronizationTrigger.LocalOutbox);
            return Results.Accepted(
                $"/edge/v1/customers/{customer.CustomerId:D}", customer);
        });
        edge.MapGet("/customers/{customerId:guid}", async (
            Guid customerId,
            PosCatalogStore catalog,
            CancellationToken ct) =>
        {
            var customer = await catalog.GetCustomerAsync(customerId, ct);
            return customer is null ? Results.NotFound() : Results.Ok(customer);
        });
        edge.MapGet("/sales", async (
            string? search,
            int? skip,
            int? take,
            PosEdgeSaleStore sales,
            CancellationToken ct) =>
        {
            var pageSize = Math.Clamp(take ?? 50, 1, 50);
            var offset = Math.Max(skip ?? 0, 0);
            var values = (await sales.SearchIssuedSalesAsync(
                search ?? string.Empty,
                offset,
                pageSize + 1,
                ct)).ToArray();
            var hasMore = values.Length > pageSize;
            return Results.Ok(new
            {
                items = values.Take(pageSize),
                hasMore,
                nextOffset = hasMore ? offset + pageSize : (int?)null
            });
        });

        edge.MapPost("/capture", async (
            CaptureRequest request,
            PosCaptureService capture,
            PosEdgeRuntimeContext context,
            PosServerConnectionState connection,
            IAuralyIdGenerator ids,
            PosLocalSessionAccessor sessions,
            CancellationToken ct) =>
        {
            var result = await capture.CaptureAsync(
                request.Value,
                context.ScopeFor(sessions.Required()),
                null,
                context.WarehouseAllowsNegativeStock || !connection.IsConnected,
                ids.NewId(),
                ct);
            return result.Status switch
            {
                PosCaptureStatus.Added => Results.Ok(result),
                PosCaptureStatus.NotFound => Results.NotFound(result),
                PosCaptureStatus.InsufficientInventory => Results.Conflict(result),
                _ => Results.Problem("Unknown POS capture result.")
            };
        });
        edge.MapPut("/drafts/{draftId:guid}/lines/{lineId:guid}/quantity", async (
            Guid draftId,
            Guid lineId,
            QuantityRequest request,
            PosCaptureService capture,
            PosEdgeRuntimeContext context,
            PosServerConnectionState connection,
            IAuralyIdGenerator ids,
            CancellationToken ct) =>
        {
            var result = await capture.ChangeQuantityAsync(
                new DraftId(draftId),
                lineId,
                request.Quantity,
                context.WarehouseAllowsNegativeStock || !connection.IsConnected,
                ids.NewId(),
                ct);
            return result.Status == PosCaptureStatus.Added
                ? Results.Ok(result)
                : Results.Conflict(result);
        });
        edge.MapGet("/drafts/{draftId:guid}/inventory-validation", async (
            Guid draftId,
            PosCaptureService capture,
            PosEdgeRuntimeContext context,
            PosServerConnectionState connection,
            IAuralyIdGenerator ids,
            CancellationToken ct) => Results.Ok(await capture.ValidateDraftInventoryAsync(
                new DraftId(draftId),
                context.WarehouseAllowsNegativeStock || !connection.IsConnected,
                ids.NewId(), ct)));
        edge.MapPut("/drafts/{draftId:guid}/lines/{lineId:guid}/discount", async (
            Guid draftId,
            Guid lineId,
            DiscountRequest request,
            HttpContext http,
            PosDraftStore drafts,
            PosSensitiveActionAuthorizer authorizer,
            PosLocalSessionAccessor sessions,
            CancellationToken ct) =>
        {
            var authorization = await authorizer.AuthorizeAsync(
                sessions.Required(), CommercePermissionCodes.SalesDiscount, draftId, lineId,
                http.Request.Headers["X-Auraly-Approval-Id"],
                http.Request.Headers["X-Auraly-Operation-Id"],
                http.Request.Headers["X-Auraly-Supervisor-Secret"], ct);
            var result = await drafts.SetDiscountAsync(
                new DraftId(draftId), lineId, request.Discount, ct);
            await authorizer.CompleteAsync(authorization, ct);
            return Results.Ok(result);
        });
        edge.MapPut("/drafts/{draftId:guid}/lines", async (
            Guid draftId,
            UpdateDraftLinesRequest request,
            HttpContext http,
            PosDraftStore drafts,
            PosSensitiveActionAuthorizer authorizer,
            PosLocalSessionAccessor sessions,
            CancellationToken ct) =>
        {
            var authorization = await authorizer.AuthorizeAsync(
                sessions.Required(), CommercePermissionCodes.SalesChangePrice, draftId, null,
                http.Request.Headers["X-Auraly-Approval-Id"],
                http.Request.Headers["X-Auraly-Operation-Id"],
                http.Request.Headers["X-Auraly-Supervisor-Secret"], ct);
            var result = await drafts.UpdateLinesAsync(
                new DraftId(draftId),
                request.Lines.Select(line => new PosDraftLineDocumentUpdate(
                    line.LineId, line.Description, line.UnitPrice, line.Discount)).ToArray(),
                ct);
            await authorizer.CompleteAsync(authorization, ct);
            return Results.Ok(result);
        });
        edge.MapPut("/drafts/{draftId:guid}/customer", async (
            Guid draftId,
            SelectCustomerRequest request,
            PosCustomerSelectionService customers,
            CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await customers.SelectAsync(
                    new DraftId(draftId),
                    request.CustomerId,
                    ct));
            }
            catch (KeyNotFoundException error)
            {
                return Results.NotFound(new { detail = error.Message });
            }
        });
        edge.MapDelete("/drafts/{draftId:guid}/lines/{lineId:guid}", async (
            Guid draftId,
            Guid lineId,
            HttpContext http,
            PosDraftStore drafts,
            PosDraftPricingService pricing,
            PosSensitiveActionAuthorizer authorizer,
            PosLocalSessionAccessor sessions,
            CancellationToken ct) =>
        {
            var authorization = await authorizer.AuthorizeAsync(
                sessions.Required(), CommercePermissionCodes.SalesRemoveLine, draftId, lineId,
                http.Request.Headers["X-Auraly-Approval-Id"],
                http.Request.Headers["X-Auraly-Operation-Id"],
                http.Request.Headers["X-Auraly-Supervisor-Secret"], ct);
            var result = await drafts.RemoveLineAsync(new DraftId(draftId), lineId, ct);
            if (result.Lines.Count > 0)
                result = await pricing.RepriceAsync(result.DraftId, result.CustomerId, ct);
            await authorizer.CompleteAsync(authorization, ct);
            return Results.Ok(result);
        });
        edge.MapDelete("/drafts/{draftId:guid}", async (
            Guid draftId,
            HttpContext http,
            PosDraftStore drafts,
            PosOrderServerClient orderServer,
            PosEdgeRuntimeContext context,
            PosSensitiveActionAuthorizer authorizer,
            PosLocalSessionAccessor sessions,
            ILogger<PosOrderRecoveryService> logger,
            CancellationToken ct) =>
        {
            var user = sessions.Required();
            var authorization = await authorizer.AuthorizeAsync(
                user, CommercePermissionCodes.SalesRestartDraft, draftId, null,
                http.Request.Headers["X-Auraly-Approval-Id"],
                http.Request.Headers["X-Auraly-Operation-Id"],
                http.Request.Headers["X-Auraly-Supervisor-Secret"], ct);
            var sourceOrderId = (await drafts.GetAsync(new DraftId(draftId), ct))?.SourceOrderId;
            await drafts.CancelAsync(new DraftId(draftId), ct);
            if (sourceOrderId.HasValue)
            {
                try
                {
                    await orderServer.ReleaseAsync(user, sourceOrderId.Value, ct);
                }
                catch (Exception error) when (error is HttpRequestException or PosOrderServerException)
                {
                    logger.LogWarning(error,
                        "Order {OrderId} claim could not be released immediately; its server lease will expire.",
                        sourceOrderId.Value);
                }
            }
            var result = await drafts.GetOrCreateActiveAsync(context.ScopeFor(user), ct);
            await authorizer.CompleteAsync(authorization, ct);
            return Results.Ok(result);
        });
        edge.MapPost("/drafts/{draftId:guid}/temporary", async (
            Guid draftId,
            SaveTemporaryRequest request,
            PosDraftStore drafts,
            CancellationToken ct) =>
            Results.Ok(await drafts.SaveTemporaryAsync(
                new DraftId(draftId),
                request.Name,
                request.Reference,
                request.Observation,
                ct)));
        edge.MapGet("/temporaries", async (
            string? search,
            PosDraftStore drafts,
            PosEdgeRuntimeContext context,
            CancellationToken ct) =>
            Results.Ok(await drafts.ListTemporariesAsync(
                context.BusinessId,
                new PosTemporaryFilter(Search: search),
                ct)));
        edge.MapPost("/temporaries/{draftId:guid}/recover", async (
            Guid draftId,
            PosDraftStore drafts,
            PosEdgeRuntimeContext context,
            PosLocalSessionAccessor sessions,
            CancellationToken ct) =>
            Results.Ok(await drafts.RecoverTemporaryAsync(
                new DraftId(draftId),
                context.ScopeFor(sessions.Required()),
                ct)));
        edge.MapDelete("/temporaries/{draftId:guid}", async (
            Guid draftId,
            PosDraftStore drafts,
            PosEdgeRuntimeContext context,
            CancellationToken ct) =>
        {
            await drafts.DeleteTemporaryAsync(
                new DraftId(draftId),
                context.BusinessId,
                ct);
            return Results.NoContent();
        });
        edge.MapPosSaleCompletion();
        edge.MapPosOrders();
        edge.MapPosWorkSessionClosure();
        return app;
    }

    private static WebApplication BuildEnrollmentRequired(
        WebApplicationBuilder builder,
        string sessionToken,
        string allowedOrigin,
        string serverUrl,
        PosEdgeEnrollmentStore store,
        PosLocalDeviceIdentityRecovery identityRecovery,
        string databasePath)
    {
        if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var serverUri) ||
            (serverUri.Scheme != Uri.UriSchemeHttps && !serverUri.IsLoopback))
            throw new InvalidOperationException(
                "PosEdge:ServerUrl must use HTTPS except for a loopback development server.");
        builder.Services.AddSingleton(store);
        builder.Services.AddSingleton(identityRecovery);
        builder.Services.AddSingleton(new HttpClient { BaseAddress = serverUri });
        builder.Services.AddSingleton<PosEdgeEnrollmentClient>();
        builder.Services.AddSingleton<PosUiStateSignal>();
        builder.Services.AddPosPeripherals(builder.Configuration, databasePath);
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            if (!IsLoopback(context.Connection.RemoteIpAddress))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            var origin = context.Request.Headers.Origin.ToString();
            if (!string.IsNullOrEmpty(origin) &&
                !string.Equals(origin, allowedOrigin, StringComparison.Ordinal))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            if (HttpMethods.IsOptions(context.Request.Method))
            {
                if (string.IsNullOrEmpty(origin))
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    return;
                }
                SetCorsHeaders(context.Response, allowedOrigin);
                context.Response.Headers.AccessControlAllowMethods = "GET,POST,PUT,OPTIONS";
                context.Response.Headers.AccessControlAllowHeaders =
                    "Content-Type,X-Auraly-Edge-Session,X-Auraly-User-Session";
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                return;
            }
            if (!FixedEquals(
                    sessionToken,
                    context.Request.Headers["X-Auraly-Edge-Session"].ToString()))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            if (!string.IsNullOrEmpty(origin)) SetCorsHeaders(context.Response, allowedOrigin);
            await next(context);
        });
        var edge = app.MapGroup("/edge/v1");
        edge.MapPosPeripheralEndpoints();
        edge.MapPost("/cash-drawer/open", (
            PosCashDrawer cashDrawer) =>
        {
            try
            {
                cashDrawer.Open();
                return Results.NoContent();
            }
            catch (Exception exception) when (
                exception is IOException or InvalidOperationException)
            {
                return Results.Problem(
                    exception.Message,
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });
        edge.MapGet("/events", async (
            HttpContext context,
            PosUiStateSignal uiState,
            CancellationToken ct) =>
        {
            context.Response.Headers.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache";
            context.Response.Headers.Connection = "keep-alive";
            var (subscriptionId, reader) = uiState.Subscribe();
            try
            {
                await context.Response.WriteAsync("event: state\ndata: ready\n\n", ct);
                await context.Response.Body.FlushAsync(ct);
                await foreach (var _ in reader.ReadAllAsync(ct))
                {
                    await context.Response.WriteAsync("event: state\ndata: changed\n\n", ct);
                    await context.Response.Body.FlushAsync(ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
            finally
            {
                uiState.Unsubscribe(subscriptionId);
            }
        });
        edge.MapGet("/health", () => Results.Ok(new
        {
            status = "EnrollmentRequired",
            serverConnected = false,
            pushConnected = false,
            deviceSeriesCode = "",
            businessId = "",
            businessName = "",
            warehouseName = "",
            warehouseAllowsNegativeStockSales = false,
            userDisplayName = "",
            fiscalReady = false
        }));
        edge.MapPost("/enrollment/redeem", async (
            LocalPosEnrollmentRequest request,
            PosEdgeEnrollmentClient client,
            IHostApplicationLifetime lifetime,
            CancellationToken ct) =>
        {
            try
            {
                var result = await client.RedeemAsync(request, ct);
                _ = Task.Run(async () =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(1));
                    lifetime.StopApplication();
                });
                return Results.Ok(result);
            }
            catch (PosEnrollmentServerException exception)
            {
                return Results.Problem(
                    exception.Message,
                    statusCode: exception.StatusCode,
                    title: exception.Title);
            }
            catch (HttpRequestException exception)
            {
                return Results.Problem(
                    exception.Message,
                    statusCode: StatusCodes.Status502BadGateway,
                    title: "EnrollmentServerUnavailable");
            }
        });
        return app;
    }

    private static string OptionalLabel(IConfiguration configuration, string key, string fallback)
    {
        var value = configuration[key];
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    private static string Required(IConfiguration configuration, string key) =>
        string.IsNullOrWhiteSpace(configuration[key])
            ? throw new InvalidOperationException($"{key} is required.")
            : configuration[key]!;

    private static Guid RequiredGuid(IConfiguration configuration, string key) =>
        Guid.TryParse(Required(configuration, key), out var value) && value != Guid.Empty
            ? value
            : throw new InvalidOperationException($"{key} must be a non-empty GUID.");

    private static bool FixedEquals(string expected, string actual)
    {
        var left = Encoding.UTF8.GetBytes(expected);
        var right = Encoding.UTF8.GetBytes(actual);
        return left.Length == right.Length &&
               CryptographicOperations.FixedTimeEquals(left, right);
    }
    private static void SetCorsHeaders(HttpResponse response, string allowedOrigin)
    {
        response.Headers.AccessControlAllowOrigin = allowedOrigin;
        response.Headers.Vary = "Origin";
    }


    private static bool RequiresLocalUserSession(PathString path) =>
        path.StartsWithSegments("/edge/v1") &&
        !path.Equals("/edge/v1/health") &&
        !path.Equals("/edge/v1/auth/login") &&
        !path.Equals("/edge/v1/enrollment/redeem") &&
        !path.StartsWithSegments("/edge/v1/configuration/printers") &&
        !path.StartsWithSegments("/edge/v1/print") &&
        !path.Equals("/edge/v1/cash-drawer/open") &&
        !path.Equals("/edge/v1/scale/read");

    private static bool RequiresOperationalWorkSession(PathString path) =>
        !path.Equals("/edge/v1/work-sessions/current") &&
        !path.Equals("/edge/v1/auth/session") &&
        !path.Equals("/edge/v1/auth/logout") &&
        !path.Equals("/edge/v1/auth/complete-enrollment") &&
        !path.StartsWithSegments("/edge/v1/synchronization");

    private static bool IsLoopback(System.Net.IPAddress? address) =>
        address is null || System.Net.IPAddress.IsLoopback(address);
}

internal sealed class PosEdgeStorageInitializer(
    PosLocalIdentityStore identities,
    PosLocalWorkSessionStore workSessions,
    PosOfflineLeaseStore leases,
    PosCatalogStore catalog,
    PosDraftStore drafts) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await identities.InitializeAsync(cancellationToken);
        await workSessions.InitializeAsync(cancellationToken);
        await leases.InitializeAsync(cancellationToken);
        await catalog.InitializeAsync(cancellationToken);
        await drafts.InitializeAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class Program
{
    public static async Task Main(string[] args)
    {
        if (args.Contains("--initialize-storage", StringComparer.Ordinal))
        {
            var databasePath = ReadArgument(args, "--database-path") ??
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Auraly",
                    "PosEdge",
                    "auraly-pos.db");
            await PosStorageBootstrap.InitializeAsync(databasePath);
            return;
        }

        if (args.Contains("--protect-fiscal-key", StringComparer.Ordinal))
        {
            var hostArgs = args
                .Where(argument => !string.Equals(
                    argument,
                    "--protect-fiscal-key",
                    StringComparison.Ordinal))
                .ToArray();
            var builder = WebApplication.CreateBuilder(hostArgs);
            var keyDirectory = builder.Configuration["PosEdge:SecretKeyDirectory"];
            if (string.IsNullOrWhiteSpace(keyDirectory))
                throw new InvalidOperationException("PosEdge:SecretKeyDirectory is required.");
            var technicalKey = await Console.In.ReadLineAsync()
                ?? throw new InvalidOperationException("The technical key must be provided through standard input.");
            Console.Out.WriteLine(
                PosEdgeProtectedSecret.ProtectTechnicalKey(keyDirectory, technicalKey));
            return;
        }

        var app = PosEdgeHostApplication.Build(args);
        await app.RunAsync();
    }

    private static string? ReadArgument(string[] args, string name)
    {
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], name, StringComparison.Ordinal))
                return args[index + 1];
        }

        return null;
    }
}
