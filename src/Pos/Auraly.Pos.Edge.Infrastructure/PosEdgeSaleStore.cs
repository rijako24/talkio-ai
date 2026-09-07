using System.Data;
using System.Data.Common;
using System.Text.Json.Serialization;
using Auraly.Application.Sales;
using Auraly.BuildingBlocks.Domain.Documents;
using Auraly.BuildingBlocks.Domain.Identifiers;
using Auraly.Contracts.Catalog;
using Auraly.Contracts.Fiscal;
using Auraly.Contracts.Organization;
using Auraly.Contracts.Sales;
using Auraly.Commerce.Taxation.Contracts;
using Auraly.Fiscal.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;

namespace Auraly.Pos.Edge.Infrastructure;

public sealed record PosEdgeSeriesProvision(
    Guid SeriesId,
    DeviceId DeviceId,
    string Prefix,
    string AuthorizationNumber,
    long RangeStart,
    long RangeEnd,
    DateOnly ValidUntil,
    Guid FiscalAuthorizationId = default,
    DateOnly? ValidFrom = null,
    long? AuthorizationRangeStart = null,
    long? AuthorizationRangeEnd = null,
    int ExpirationWarningDays = 3,
    long RemainingNumberWarningThreshold = 100,
    bool EmissionEnabled = true);

public sealed record PosEdgeFiscalCursorState(
    Guid SeriesId, long NextConsecutive, long RangeEnd);

public sealed record PosEdgeDocumentSeriesProvision(
    Guid SeriesId,
    DeviceId DeviceId,
    string DocumentType,
    string Prefix,
    string SeriesCode,
    int Padding,
    long RangeStart,
    long RangeEnd);

public sealed record OfflineSalePayment(
    string MethodCode,
    decimal Amount,
    string? Reference = null,
    string? CardFranchiseCode = null,
    string? ApprovalNumber = null,
    Guid? BankAccountId = null,
    string? Notes = null);

public sealed record PosEdgeIssueCommand(
    UserId UserId,
    DocumentId DocumentId,
    SalesExecutionContext Context,
    DateTimeOffset IssuedAt,
    string? SupplierTaxId,
    string CustomerIdentification,
    FiscalTechnicalKey? TechnicalKey,
    FiscalEnvironment? Environment,
    string? QrValidationUrl,
    IReadOnlyCollection<OfflineSaleLine> Lines,
    IReadOnlyCollection<OfflineSalePayment>? Payments = null,
    PosSaleUblSnapshotContract? UblSnapshot = null,
    Guid? CustomerId = null,
    Guid? SourceOrderId = null,
    string DocumentType = PosSaleDocumentTypes.Invoice,
    WithholdingCalculationSnapshot? Withholding = null);

public sealed record PosFiscalNumberPreview(
    Guid SeriesId,
    string Prefix,
    long Consecutive,
    string FullNumber,
    bool IsAvailable);

public sealed record PosDocumentNumberPreview(
    Guid SeriesId,
    string DocumentType,
    string Prefix,
    string SeriesCode,
    long Consecutive,
    string FullNumber,
    bool IsAvailable);

public sealed record PosEdgeIssueResult(
    DocumentId DocumentId,
    string DocumentNumber,
    string? FiscalNumber,
    string? Cufe,
    string? QrPayload,
    decimal Total,
    Guid OutboxMessageId,
    bool WasAlreadyIssued,
    [property: JsonIgnore] PosSaleUploadRequest Upload);

public sealed record PosEdgeOutboxItem(
    Guid MessageId,
    DocumentId DocumentId,
    string Type,
    string Payload,
    int AttemptCount,
    string Status = PosOutboxStatus.Pending,
    DateTimeOffset? NextAttemptAt = null,
    DateTimeOffset? LeaseAcquiredAt = null,
    string? LastError = null,
    string? RemoteStatus = null,
    Guid? ServerReceiptId = null,
    Guid? WorkSessionId = null);


public sealed record PosLocalFiscalStatus(
    DocumentId DocumentId,
    string FiscalNumber,
    string Cufe,
    string? Status,
    string? StatusCode,
    string? StatusDescription,
    DateTimeOffset? UpdatedAt);

public sealed record PosIssuedSaleSummary(
    DocumentId DocumentId,
    string DocumentType,
    string DocumentNumber,
    string FiscalNumber,
    DateTimeOffset IssuedAt,
    decimal Total,
    string CustomerIdentification,
    string CustomerName,
    string? FiscalStatus);

public sealed record PosLocalWorkSessionSale(
    DateTimeOffset IssuedAt,
    decimal Total,
    IReadOnlyList<PosSalePaymentContract> Payments,
    decimal CreditAmount);

public sealed record PosSaleOutboxStatus(
    int PendingCount,
    DateTimeOffset? OldestPendingAt,
    string? LastError);

public sealed class PosEdgeSaleStore
{
    private readonly DbContextOptions<PosEdgeDbContext> _options;
    private readonly ConfirmOfflineSaleService _confirmationService;

    public PosEdgeSaleStore(string connectionString, ConfirmOfflineSaleService confirmationService)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("A SQLite connection string is required.", nameof(connectionString));
        }

        _options = new DbContextOptionsBuilder<PosEdgeDbContext>()
            .UseSqlite(connectionString)
            .Options;
        _confirmationService = confirmationService;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var context = new PosEdgeDbContext(_options);
        await context.Database.EnsureCreatedAsync(cancellationToken);
        await PosUnifiedOutboxSchema.EnsureCreatedAsync(
            context.Database.GetConnectionString()!, cancellationToken);
        await UpgradeDeviceIdentityAsync(context, cancellationToken);
        await UpgradeDocumentNumberingAsync(context, cancellationToken);
        await UpgradeFiscalSeriesAsync(context, cancellationToken);
        await UpgradeFiscalStatusAsync(context, cancellationToken);
    }

    public async Task ProvisionSeriesAsync(
        PosEdgeSeriesProvision provision,
        CancellationToken cancellationToken = default)
    {
        if (provision.RangeStart <= 0 || provision.RangeEnd < provision.RangeStart)
        {
            throw new ArgumentOutOfRangeException(nameof(provision));
        }

        await using var context = new PosEdgeDbContext(_options);
        var seriesIdText = provision.SeriesId.ToString("D");
        var current = await context.FiscalSeriesCursors
            .FromSqlInterpolated($"SELECT * FROM FiscalSeriesCursors WHERE lower(SeriesId) = lower({seriesIdText})")
            .SingleOrDefaultAsync(cancellationToken);

        if (current is null)
        {
            current = new FiscalSeriesCursorRow
            {
                SeriesId = provision.SeriesId,
                DeviceId = provision.DeviceId.Value,
                Prefix = provision.Prefix.Trim().ToUpperInvariant(),
                AuthorizationNumber = provision.AuthorizationNumber.Trim(),
                FiscalAuthorizationId = provision.FiscalAuthorizationId == Guid.Empty
                    ? provision.SeriesId
                    : provision.FiscalAuthorizationId,
                RangeStart = provision.RangeStart,
                NextConsecutive = provision.RangeStart,
                RangeEnd = provision.RangeEnd,
                ValidFrom = provision.ValidFrom ?? DateOnly.MinValue,
                ValidUntil = provision.ValidUntil,
                IsActive = true,
                AuthorizationRangeStart = provision.AuthorizationRangeStart ?? provision.RangeStart,
                AuthorizationRangeEnd = provision.AuthorizationRangeEnd ?? provision.RangeEnd,
                ExpirationWarningDays = provision.ExpirationWarningDays,
                RemainingNumberWarningThreshold = provision.RemainingNumberWarningThreshold,
                IsEmissionEnabled = provision.EmissionEnabled
            };
            context.FiscalSeriesCursors.Add(current);
        }
        else
        {
            if (current.RangeStart != provision.RangeStart || current.RangeEnd != provision.RangeEnd)
                throw new InvalidOperationException("The provisioned fiscal range differs from the durable local series.");
            current.DeviceId = provision.DeviceId.Value;
            await BackfillFiscalAuthorizationAsync(
                context,
                provision.SeriesId,
                provision.FiscalAuthorizationId == Guid.Empty
                    ? provision.SeriesId
                    : provision.FiscalAuthorizationId,
                cancellationToken);
            current.AuthorizationRangeStart = provision.AuthorizationRangeStart ?? provision.RangeStart;
            current.AuthorizationRangeEnd = provision.AuthorizationRangeEnd ?? provision.RangeEnd;
            current.ExpirationWarningDays = provision.ExpirationWarningDays;
            current.RemainingNumberWarningThreshold = provision.RemainingNumberWarningThreshold;
            current.IsActive = true;
            current.IsEmissionEnabled = provision.EmissionEnabled;
        }
        var conflicting = await context.FiscalSeriesCursors
            .Where(
            row => row.DeviceId == provision.DeviceId.Value &&
                   row.SeriesId != provision.SeriesId && row.IsActive)
            .ToListAsync(cancellationToken);
        foreach (var previous in conflicting)
        {
            previous.IsActive = false;
            previous.IsEmissionEnabled = false;
        }
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task DeactivateFiscalSeriesAsync(
        DeviceId deviceId,
        CancellationToken cancellationToken = default)
    {
        await using var context = new PosEdgeDbContext(_options);
        var active = await context.FiscalSeriesCursors
            .Where(row => row.DeviceId == deviceId.Value && row.IsActive)
            .ToListAsync(cancellationToken);
        foreach (var cursor in active)
        {
            cursor.IsActive = false;
            cursor.IsEmissionEnabled = false;
        }
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<PosEdgeFiscalCursorState?> GetFiscalCursorStateAsync(
        DeviceId deviceId, CancellationToken cancellationToken = default)
    {
        await using var context = new PosEdgeDbContext(_options);
        var cursor = await context.FiscalSeriesCursors.AsNoTracking()
            .Where(row => row.DeviceId == deviceId.Value && row.IsActive)
            .OrderBy(row => row.RangeStart)
            .FirstOrDefaultAsync(cancellationToken);
        return cursor is null ? null : new PosEdgeFiscalCursorState(
            cursor.SeriesId, cursor.NextConsecutive, cursor.RangeEnd);
    }

    public async Task<IReadOnlyList<string>> GetFiscalWarningsAsync(
        DeviceId deviceId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await using var context = new PosEdgeDbContext(_options);
        var cursor = await context.FiscalSeriesCursors.AsNoTracking()
            .Where(row => row.DeviceId == deviceId.Value && row.IsActive)
            .OrderBy(row => row.RangeStart)
            .FirstOrDefaultAsync(cancellationToken);
        if (cursor is null) return [];

        var warnings = new List<string>();
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var daysRemaining = cursor.ValidUntil.DayNumber - today.DayNumber;
        if (daysRemaining < 0)
            warnings.Add($"La resolución DIAN {cursor.AuthorizationNumber} está vencida.");
        else if (daysRemaining <= cursor.ExpirationWarningDays)
            warnings.Add(daysRemaining == 0
                ? $"La resolución DIAN {cursor.AuthorizationNumber} vence hoy."
                : $"La resolución DIAN {cursor.AuthorizationNumber} vence en {daysRemaining} días.");

        var remaining = Math.Max(0, cursor.RangeEnd - cursor.NextConsecutive + 1);
        if (remaining <= cursor.RemainingNumberWarningThreshold)
            warnings.Add(remaining == 0
                ? $"La resolución DIAN {cursor.AuthorizationNumber} agotó su numeración."
                : $"A la resolución DIAN {cursor.AuthorizationNumber} le quedan {remaining} números disponibles.");
        return warnings;
    }

    public async Task ProvisionDocumentSeriesAsync(
        PosEdgeDocumentSeriesProvision provision,
        CancellationToken cancellationToken = default)
    {
        if (provision.RangeStart <= 0 ||
            provision.RangeEnd < provision.RangeStart ||
            provision.RangeEnd > AuralyDocumentNumberAssignment.MaximumConsecutive ||
            provision.Padding != AuralyDocumentNumberAssignment.CanonicalPadding)
        {
            throw new ArgumentOutOfRangeException(nameof(provision));
        }

        var expectedPrefix = AuralyDocumentTypes.DefaultPrefix(provision.DocumentType);
        if (!string.Equals(expectedPrefix, provision.Prefix.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Document type '{provision.DocumentType}' requires Auraly prefix '{expectedPrefix}'.");
        }

        await using var context = new PosEdgeDbContext(_options);
        var deviceIdText = provision.DeviceId.Value.ToString("D");
        var seriesIdText = provision.SeriesId.ToString("D");
        var current = await context.DocumentSeriesCursors
            .FromSqlInterpolated($"SELECT * FROM DocumentSeriesCursors WHERE lower(DeviceId) = lower({deviceIdText}) AND DocumentType = {provision.DocumentType}")
            .SingleOrDefaultAsync(cancellationToken);
        if (current is not null && current.SeriesId != provision.SeriesId)
        {
            throw new InvalidOperationException(
                "The device already has another provisioned Auraly series for this document type.");
        }

        if (current is null)
        {
            current = await context.DocumentSeriesCursors
                .FromSqlInterpolated($"SELECT * FROM DocumentSeriesCursors WHERE lower(SeriesId) = lower({seriesIdText})")
                .SingleOrDefaultAsync(cancellationToken);
            if (current is not null)
            {
                if (!string.Equals(current.DocumentType, provision.DocumentType, StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        "The provisioned Auraly series belongs to another document type.");
                current.DeviceId = provision.DeviceId.Value;
                await context.SaveChangesAsync(cancellationToken);
            }
        }

        if (current is null)
        {
            context.DocumentSeriesCursors.Add(new DocumentSeriesCursorRow
            {
                SeriesId = provision.SeriesId,
                DeviceId = provision.DeviceId.Value,
                DocumentType = provision.DocumentType,
                Prefix = expectedPrefix,
                SeriesCode = provision.SeriesCode.Trim().ToUpperInvariant(),
                Padding = provision.Padding,
                NextConsecutive = provision.RangeStart,
                RangeEnd = provision.RangeEnd,
                IsActive = true
            });
            try
            {
                await context.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException exception) when (IsSqliteUniqueConstraint(exception))
            {
                // Two desktop processes can race while the launcher restarts Edge.
                // Keep the durable row created by the winner instead of crashing.
                context.ChangeTracker.Clear();
                var durable = await context.DocumentSeriesCursors
                    .FromSqlInterpolated($"SELECT * FROM DocumentSeriesCursors WHERE lower(SeriesId) = lower({seriesIdText})")
                    .SingleOrDefaultAsync(cancellationToken);
                if (durable is null ||
                    !string.Equals(durable.DocumentType, provision.DocumentType, StringComparison.Ordinal) ||
                    durable.DeviceId != provision.DeviceId.Value)
                {
                    throw;
                }
            }
        }
    }

    private static bool IsSqliteUniqueConstraint(DbUpdateException exception) =>
        exception.InnerException is SqliteException { SqliteErrorCode: 19 };

    public async Task<PosDocumentNumberPreview> PreviewNextDocumentNumberAsync(
        DeviceId deviceId,
        string documentType,
        CancellationToken cancellationToken = default)
    {
        await using var context = new PosEdgeDbContext(_options);
        var cursor = await context.DocumentSeriesCursors.AsNoTracking().SingleAsync(
            row => row.DeviceId == deviceId.Value && row.DocumentType == documentType,
            cancellationToken);
        var assignment = AuralyDocumentNumberAssignment.Create(
            cursor.SeriesId,
            cursor.DocumentType,
            cursor.Prefix,
            cursor.SeriesCode,
            cursor.NextConsecutive,
            cursor.Padding);
        return new PosDocumentNumberPreview(
            assignment.SeriesId,
            assignment.DocumentType,
            assignment.Prefix,
            assignment.SeriesCode,
            assignment.Consecutive,
            assignment.FullNumber,
            cursor.IsActive && cursor.NextConsecutive <= cursor.RangeEnd);
    }

    public async Task<PosFiscalNumberPreview> PreviewNextFiscalNumberAsync(
        DeviceId deviceId,
        DateTimeOffset issuedAt,
        CancellationToken cancellationToken = default)
    {
        await using var context = new PosEdgeDbContext(_options);
        var issueDate = DateOnly.FromDateTime(issuedAt.Date);
        var cursors = await context.FiscalSeriesCursors.AsNoTracking()
            .Where(row => row.DeviceId == deviceId.Value)
            .OrderByDescending(row => row.IsActive)
            .ThenBy(row => row.RangeStart)
            .ToListAsync(cancellationToken);
        if (cursors.Count == 0)
            return new PosFiscalNumberPreview(Guid.Empty, string.Empty, 0, string.Empty, false);
        var cursor = cursors.FirstOrDefault(row =>
                         row.IsActive &&
                         row.IsEmissionEnabled &&
                         issueDate >= row.ValidFrom && issueDate <= row.ValidUntil &&
                         row.NextConsecutive <= row.RangeEnd)
                     ?? cursors.First();
        var available = cursor.IsActive &&
                        cursor.IsEmissionEnabled &&
                        issueDate >= cursor.ValidFrom && issueDate <= cursor.ValidUntil &&
                        cursor.NextConsecutive <= cursor.RangeEnd;
        return new PosFiscalNumberPreview(
            cursor.SeriesId,
            cursor.Prefix,
            cursor.NextConsecutive,
            $"{cursor.Prefix}{cursor.NextConsecutive}",
            available);
    }

    public async Task<PosEdgeIssueResult> IssueAsync(
        PosEdgeIssueCommand command,
        CancellationToken cancellationToken = default)
    {
        await using var context = new PosEdgeDbContext(_options);
        var existing = await context.IssuedSales
            .AsNoTracking()
            .SingleOrDefaultAsync(
                row => row.DocumentId == command.DocumentId.Value,
                cancellationToken);
        if (existing is not null)
        {
            var existingUpload = PosSaleContractSerializer.Deserialize(existing.FiscalSnapshotJson);
            var existingOutbox = await context.Outbox
                .AsNoTracking()
                .SingleAsync(
                    row => row.DocumentId == command.DocumentId.Value,
                    cancellationToken);
            return new PosEdgeIssueResult(
                command.DocumentId,
                existing.DocumentNumber,
                string.IsNullOrEmpty(existing.FiscalNumber) ? null : existing.FiscalNumber,
                string.IsNullOrEmpty(existing.Cufe) ? null : existing.Cufe,
                existingUpload.FiscalSnapshot?.QrPayload,
                existing.Total,
                existingOutbox.MessageId,
                WasAlreadyIssued: true,
                existingUpload);
        }

        var deviceId = command.Context.DeviceId?.Value
            ?? throw new InvalidOperationException("An Edge sale requires DeviceId.");
        await using var transaction = await context.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        var documentCursor = await context.DocumentSeriesCursors.SingleAsync(
            row => row.DeviceId == deviceId &&
                   row.DocumentType == command.DocumentType,
            cancellationToken);
        if (!documentCursor.IsActive || documentCursor.NextConsecutive > documentCursor.RangeEnd)
        {
            throw new InvalidOperationException("The Auraly sales document series is inactive or exhausted.");
        }

        var documentNumber = AuralyDocumentNumberAssignment.Create(
            documentCursor.SeriesId,
            documentCursor.DocumentType,
            documentCursor.Prefix,
            documentCursor.SeriesCode,
            documentCursor.NextConsecutive,
            documentCursor.Padding);
        documentCursor.NextConsecutive++;

        if (!PosSaleDocumentTypes.IsSupported(command.DocumentType))
            throw new InvalidOperationException("The sale document type is not supported.");

        var isFiscal = PosSaleDocumentTypes.IsFiscal(command.DocumentType);
        FiscalNumberAssignment? fiscalNumber = null;
        ImmutableFiscalSnapshot? snapshot = null;
        Guid? fiscalAuthorizationId = null;
        SalesInvoice invoice;
        Guid outboxMessageId;
        string outboxType;
        if (isFiscal)
        {
            if (string.IsNullOrWhiteSpace(command.SupplierTaxId) ||
                command.TechnicalKey is null ||
                command.Environment is null ||
                string.IsNullOrWhiteSpace(command.QrValidationUrl))
                throw new InvalidOperationException(
                    "Electronic invoicing is not configured for this POS device.");
            var supplierTaxId = command.SupplierTaxId;
            var technicalKey = command.TechnicalKey;
            var environment = command.Environment.Value;
            var qrValidationUrl = command.QrValidationUrl;
            var cursor = await context.FiscalSeriesCursors
                .Where(row => row.DeviceId == deviceId && row.IsActive && row.IsEmissionEnabled)
                .OrderBy(row => row.RangeStart)
                .FirstOrDefaultAsync(cancellationToken);
            var issueDate = DateOnly.FromDateTime(command.IssuedAt.Date);
            if (cursor is null || !cursor.IsActive || issueDate < cursor.ValidFrom || issueDate > cursor.ValidUntil)
                throw new InvalidOperationException(
                    "La resolución fiscal no está vigente para la fecha de emisión.");
            if (cursor.NextConsecutive > cursor.RangeEnd)
                throw new InvalidOperationException(
                    "La resolución DIAN asignada a este equipo agotó su numeración autorizada.");

            ValidateUblSnapshot(command, cursor);
            var consecutive = cursor.NextConsecutive++;
            fiscalNumber = new FiscalNumberAssignment(
                cursor.SeriesId,
                cursor.Prefix,
                consecutive,
                $"{cursor.Prefix}{consecutive}",
                cursor.AuthorizationNumber);
            var confirmed = _confirmationService.Confirm(new ConfirmOfflineSaleCommand(
                command.UserId,
                command.DocumentId,
                command.Context,
                documentNumber,
                fiscalNumber,
                command.IssuedAt,
                supplierTaxId,
                command.CustomerIdentification,
                technicalKey,
                environment,
                qrValidationUrl,
                command.Lines));
            invoice = confirmed.Invoice;
            snapshot = invoice.FiscalSnapshot
                ?? throw new InvalidOperationException("The sale was not fiscally frozen.");
            fiscalAuthorizationId = cursor.FiscalAuthorizationId;
            outboxMessageId = confirmed.OutboxMessage.Id;
            outboxType = confirmed.OutboxMessage.Type;
        }
        else
        {
            invoice = _confirmationService.Prepare(new PrepareOfflineSaleCommand(
                command.UserId, command.DocumentId, command.Context, command.Lines));
            outboxMessageId = Guid.NewGuid();
            outboxType = "sales.receipt.confirmed";
        }
        var upload = BuildUploadContract(
            command,
            invoice,
            snapshot,
            documentNumber,
            fiscalNumber,
            fiscalAuthorizationId);
        var payload = PosSaleContractSerializer.Serialize(upload);

        context.IssuedSales.Add(new IssuedSaleRow
        {
            DocumentId = command.DocumentId.Value,
            DocumentNumber = documentNumber.FullNumber,
            FiscalNumber = fiscalNumber?.FullNumber ?? string.Empty,
            Cufe = snapshot?.Cufe ?? string.Empty,
            Total = invoice.PayableAmount,
            IssuedAt = command.IssuedAt,
            FiscalSnapshotJson = payload,
            RemoteFiscalStatus = isFiscal
                ? FiscalDocumentStatusCodes.LocallyIssuedPendingSync
                : PosSaleRemoteStatuses.CommercialAccepted,
            RemoteFiscalUpdatedAt = command.IssuedAt
        });
        context.Outbox.Add(new PosOutboxRow
        {
            MessageId = outboxMessageId,
            DocumentId = command.DocumentId.Value,
            WorkSessionId = command.Context.WorkSessionId.Value,
            Type = outboxType,
            Payload = payload,
            Status = PosOutboxStatus.Pending,
            CreatedAt = command.IssuedAt
        });

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new PosEdgeIssueResult(
            command.DocumentId,
            documentNumber.FullNumber,
            fiscalNumber?.FullNumber,
            snapshot?.Cufe,
            snapshot?.QrPayload,
            invoice.PayableAmount,
            outboxMessageId,
            WasAlreadyIssued: false,
            upload);
    }

    public async Task<IReadOnlyCollection<PosEdgeOutboxItem>> GetPendingOutboxAsync(
        CancellationToken cancellationToken = default)
    {
        await using var context = new PosEdgeDbContext(_options);
        return await context.Outbox
            .AsNoTracking()
            .Where(row => row.Type != PosOutboxMessageTypes.WorkSessionOpened &&
                          row.Type != PosOutboxMessageTypes.CashMovement &&
                          row.Type != PosOutboxMessageTypes.WorkSessionClosure &&
                          row.Type != PosOutboxMessageTypes.CustomerCreated &&
                          (row.Status == PosOutboxStatus.Pending ||
                           row.Status == PosOutboxStatus.RetryScheduled))
            .OrderBy(row => row.LocalSequence)
            .Select(ToOutboxItem)
            .ToArrayAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PosLocalWorkSessionSale>> ReadWorkSessionSalesAsync(
        Guid workSessionId,
        CancellationToken cancellationToken = default)
    {
        await using var context = new PosEdgeDbContext(_options);
        var payloads = await context.IssuedSales.AsNoTracking()
            .Select(row => row.FiscalSnapshotJson)
            .ToArrayAsync(cancellationToken);
        return payloads
            .Select(PosSaleContractSerializer.Deserialize)
            .Where(value => value.WorkSessionId == workSessionId)
            .OrderBy(value => value.CommercialSnapshot.IssuedAt)
            .Select(value => new PosLocalWorkSessionSale(
                value.CommercialSnapshot.IssuedAt,
                value.CommercialSnapshot.PayableAmount,
                value.Payments,
                value.Credit?.Amount ?? 0))
            .ToArray();
    }

    public async Task<PosSaleOutboxStatus> ReadOutboxStatusAsync(
        CancellationToken cancellationToken = default)
    {
        await using var context = new PosEdgeDbContext(_options);
        var rows = await context.Outbox.AsNoTracking()
            .Where(row => row.Type != PosOutboxMessageTypes.WorkSessionOpened &&
                          row.Type != PosOutboxMessageTypes.CashMovement &&
                          row.Type != PosOutboxMessageTypes.WorkSessionClosure &&
                          row.Type != PosOutboxMessageTypes.CustomerCreated &&
                          row.Status != PosOutboxStatus.Uploaded)
            .Select(row => new { row.CreatedAt, row.LastError })
            .ToArrayAsync(cancellationToken);
        rows = rows.OrderBy(row => row.CreatedAt).ToArray();
        return new PosSaleOutboxStatus(
            rows.Length,
            rows.FirstOrDefault()?.CreatedAt,
            rows.LastOrDefault(value => !string.IsNullOrWhiteSpace(value.LastError))?.LastError);
    }

    public async Task<bool> HasPendingOutboxForWorkSessionAsync(
        Guid workSessionId,
        CancellationToken cancellationToken = default)
    {
        await using var context = new PosEdgeDbContext(_options);
        return await context.Outbox.AsNoTracking()
            .Where(row => row.Type != PosOutboxMessageTypes.WorkSessionOpened &&
                          row.Type != PosOutboxMessageTypes.CashMovement &&
                          row.Type != PosOutboxMessageTypes.WorkSessionClosure &&
                          row.Type != PosOutboxMessageTypes.CustomerCreated &&
                          row.Status != PosOutboxStatus.Uploaded &&
                          row.WorkSessionId == workSessionId)
            .AnyAsync(cancellationToken);
    }

    public async Task<PosEdgeOutboxItem?> ClaimNextOutboxAsync(
        DateTimeOffset now,
        TimeSpan leaseTimeout,
        CancellationToken cancellationToken = default)
    {
        await using var context = new PosEdgeDbContext(_options);
        await using var transaction = await context.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        var staleBefore = now - leaseTimeout;
        var pending = await context.Outbox
            .Where(item => item.Status != PosOutboxStatus.Uploaded)
            .ToArrayAsync(cancellationToken);
        var row = pending
            .Where(item => PosOutboxMessageTypes.IsLocalSale(item.Type) &&
                (item.Status == PosOutboxStatus.Pending ||
                (item.Status == PosOutboxStatus.RetryScheduled &&
                 (item.NextAttemptAt == null || item.NextAttemptAt <= now)) ||
                (item.Status == PosOutboxStatus.Uploading &&
                 item.LeaseAcquiredAt != null &&
                 item.LeaseAcquiredAt <= staleBefore)) &&
                !pending.Any(prior => IsEarlier(prior, item)))
            .OrderBy(item => item.LocalSequence)
            .FirstOrDefault();
        if (row is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        row.Status = PosOutboxStatus.Uploading;
        row.AttemptCount++;
        row.LastAttemptAt = now;
        row.LeaseAcquiredAt = now;
        row.NextAttemptAt = null;
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ToOutboxItem.Compile().Invoke(row);
    }

    private static bool IsEarlier(PosOutboxRow prior, PosOutboxRow current) =>
        prior.LocalSequence < current.LocalSequence;

    public async Task<PosEdgeOutboxItem?> GetOutboxAsync(
        DocumentId documentId,
        CancellationToken cancellationToken = default)
    {
        await using var context = new PosEdgeDbContext(_options);
        return await context.Outbox
            .AsNoTracking()
            .Where(row => row.DocumentId == documentId.Value)
            .Select(ToOutboxItem)
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task MarkUploadedAsync(
        Guid messageId,
        DateTimeOffset uploadedAt,
        CancellationToken cancellationToken = default)
    {
        await MarkUploadedAsync(
            messageId,
            new PosSaleUploadResponse(
                Guid.Empty,
                Guid.Empty,
                PosSaleRemoteStatuses.FiscalVerified,
                string.Empty,
                null,
                false,
                uploadedAt,
                uploadedAt,
                null),
            uploadedAt,
            cancellationToken);
    }

    public async Task MarkUploadedAsync(
        Guid messageId,
        PosSaleUploadResponse response,
        DateTimeOffset uploadedAt,
        CancellationToken cancellationToken = default)
    {
        await using var context = new PosEdgeDbContext(_options);
        var row = await context.Outbox.SingleAsync(
            item => item.MessageId == messageId,
            cancellationToken);
        if (row.Status == PosOutboxStatus.Uploaded)
        {
            return;
        }

        if (row.AttemptCount == 0)
        {
            row.AttemptCount = 1;
        }

        row.Status = PosOutboxStatus.Uploaded;
        row.UploadedAt = uploadedAt;
        row.LeaseAcquiredAt = null;
        row.LastError = null;
        row.RemoteStatus = response.Status;
        row.ServerReceiptId = response.ReceiptId == Guid.Empty ? null : response.ReceiptId;
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task MarkFiscalIntegrityConflictAsync(
        Guid messageId,
        PosSaleUploadResponse response,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default)
    {
        await using var context = new PosEdgeDbContext(_options);
        var row = await context.Outbox.SingleAsync(
            item => item.MessageId == messageId,
            cancellationToken);
        row.Status = PosOutboxStatus.FiscalIntegrityConflict;
        row.LeaseAcquiredAt = null;
        row.NextAttemptAt = null;
        row.LastError = response.Detail;
        row.RemoteStatus = response.Status;
        row.ServerReceiptId = response.ReceiptId == Guid.Empty ? null : response.ReceiptId;
        row.UploadedAt = occurredAt;
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task ScheduleRetryAsync(
        Guid messageId,
        DateTimeOffset nextAttemptAt,
        string error,
        CancellationToken cancellationToken = default)
    {
        await using var context = new PosEdgeDbContext(_options);
        var row = await context.Outbox.SingleAsync(
            item => item.MessageId == messageId,
            cancellationToken);
        row.Status = PosOutboxStatus.RetryScheduled;
        row.LeaseAcquiredAt = null;
        row.NextAttemptAt = nextAttemptAt;
        row.LastError = Truncate(error);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task MarkFailedPermanentAsync(
        Guid messageId,
        string error,
        CancellationToken cancellationToken = default)
    {
        await using var context = new PosEdgeDbContext(_options);
        var row = await context.Outbox.SingleAsync(
            item => item.MessageId == messageId,
            cancellationToken);
        row.Status = PosOutboxStatus.FailedPermanent;
        row.LeaseAcquiredAt = null;
        row.NextAttemptAt = null;
        row.LastError = Truncate(error);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<string?> GetFiscalStatusCursorAsync(
        CancellationToken cancellationToken = default)
    {
        await using var context = new PosEdgeDbContext(_options);
        return await context.SyncState.AsNoTracking()
            .Where(row => row.Key == "FiscalStatusCursor")
            .Select(row => row.Cursor)
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task ApplyFiscalStatusPageAsync(
        PosFiscalStatusPage page,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (string.IsNullOrWhiteSpace(page.NextCursor))
            throw new ArgumentException("A durable next cursor is required.", nameof(page));

        await using var context = new PosEdgeDbContext(_options);
        await using var transaction = await context.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        foreach (var change in page.Items)
        {
            var sale = await context.IssuedSales.SingleOrDefaultAsync(
                row => row.DocumentId == change.DocumentId,
                cancellationToken);
            if (sale is null) continue;
            if (sale.FiscalNumber != change.FiscalNumber || sale.Cufe != change.Cufe)
                throw new InvalidOperationException(
                    "The server fiscal identity differs from the immutable local sale.");
            if (sale.RemoteFiscalUpdatedAt is not null &&
                sale.RemoteFiscalUpdatedAt > change.UpdatedAt)
                continue;
            sale.RemoteFiscalStatus = change.Status;
            sale.RemoteFiscalStatusCode = change.StatusCode;
            sale.RemoteFiscalStatusDescription = change.StatusDescription;
            sale.RemoteFiscalUpdatedAt = change.UpdatedAt;
        }

        var state = await context.SyncState.SingleOrDefaultAsync(
            row => row.Key == "FiscalStatusCursor",
            cancellationToken);
        if (state is null)
            context.SyncState.Add(new PosSyncStateRow
            {
                Key = "FiscalStatusCursor",
                Cursor = page.NextCursor
            });
        else
            state.Cursor = page.NextCursor;
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<PosLocalFiscalStatus?> GetFiscalStatusAsync(
        DocumentId documentId,
        CancellationToken cancellationToken = default)
    {
        await using var context = new PosEdgeDbContext(_options);
        var row = await context.IssuedSales.AsNoTracking().SingleOrDefaultAsync(
            item => item.DocumentId == documentId.Value,
            cancellationToken);
        return row is null ? null : new PosLocalFiscalStatus(
            documentId, row.FiscalNumber, row.Cufe, row.RemoteFiscalStatus,
            row.RemoteFiscalStatusCode, row.RemoteFiscalStatusDescription,
            row.RemoteFiscalUpdatedAt);
    }

    public async Task<IReadOnlyCollection<PosIssuedSaleSummary>> SearchIssuedSalesAsync(
        string search,
        int skip = 0,
        int take = 50,
        CancellationToken cancellationToken = default)
    {
        if (skip < 0) throw new ArgumentOutOfRangeException(nameof(skip));
        if (take is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(take));
        var normalized = search.Trim();
        await using var context = new PosEdgeDbContext(_options);
        var rows = await context.IssuedSales.AsNoTracking()
            .Where(row => normalized == string.Empty ||
                          row.DocumentNumber.Contains(normalized) ||
                          row.FiscalNumber.Contains(normalized))
            .OrderByDescending(row => row.DocumentNumber)
            .Skip(skip)
            .Take(take)
            .ToArrayAsync(cancellationToken);
        return rows.Select(row =>
        {
            var snapshot = PosSaleContractSerializer.Deserialize(row.FiscalSnapshotJson);
            return new PosIssuedSaleSummary(
                new DocumentId(row.DocumentId),
                snapshot.CommercialSnapshot.DocumentType,
                row.DocumentNumber,
                row.FiscalNumber,
                row.IssuedAt,
                row.Total,
                snapshot.CommercialSnapshot.CustomerIdentification,
                snapshot.UblSnapshot?.Customer.RegistrationName ?? "Consumidor final",
                row.RemoteFiscalStatus);
        }).ToArray();
    }


    public async Task<PosSaleUploadRequest?> GetIssuedUploadAsync(
        DocumentId documentId,
        CancellationToken cancellationToken = default)
    {
        await using var context = new PosEdgeDbContext(_options);
        var payload = await context.IssuedSales.AsNoTracking()
            .Where(row => row.DocumentId == documentId.Value)
            .Select(row => row.FiscalSnapshotJson)
            .SingleOrDefaultAsync(cancellationToken);
        return payload is null ? null : PosSaleContractSerializer.Deserialize(payload);
    }

    public async Task RecordReprintAsync(
        DocumentId documentId,
        UserId userId,
        DateTimeOffset reprintedAt,
        CancellationToken cancellationToken = default)
    {
        await using var context = new PosEdgeDbContext(_options);
        DbConnection connection = context.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO PosPrintAudit(DocumentId,ReprintedAt,UserId,FiscalStatus)
            SELECT $documentId,$reprintedAt,$userId,RemoteFiscalStatus
            FROM IssuedSales WHERE DocumentId=$documentId;
            """;
        var document = command.CreateParameter();
        document.ParameterName = "$documentId";
        document.Value = documentId.Value;
        command.Parameters.Add(document);
        var occurred = command.CreateParameter();
        occurred.ParameterName = "$reprintedAt";
        occurred.Value = reprintedAt;
        command.Parameters.Add(occurred);
        var user = command.CreateParameter();
        user.ParameterName = "$userId";
        user.Value = userId.Value;
        command.Parameters.Add(user);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new KeyNotFoundException("The issued sale to audit was not found.");
    }

    private static PosSaleUploadRequest BuildUploadContract(
        PosEdgeIssueCommand command,
        SalesInvoice invoice,
        ImmutableFiscalSnapshot? snapshot,
        AuralyDocumentNumberAssignment documentNumber,
        FiscalNumberAssignment? fiscalNumber,
        Guid? fiscalAuthorizationId)
    {
        var lines = command.Lines
            .Select((line, index) => new PosSaleLineContract(
                index + 1,
                line.Product.ProductId.Value,
                line.Product.Name,
                line.Product.TaxCode,
                line.Quantity,
                line.UnitPrice,
                line.TotalDiscount,
                line.TaxAmount,
                decimal.Round(
                    (line.Quantity * line.UnitPrice) - line.TotalDiscount,
                    2,
                    MidpointRounding.ToEven),
                decimal.Round(
                    (line.Quantity * line.UnitPrice) - line.TotalDiscount,
                    2,
                    MidpointRounding.ToEven) + line.TaxAmount,
                line.Product.TaxRate,
                line.DocumentUnitCost,
                line.PromotionDiscount))
            .ToArray();
        var payments = command.Payments is { Count: > 0 }
            ? command.Payments
                .Select((payment, index) => new PosSalePaymentContract(
                    index + 1,
                    payment.MethodCode,
                    payment.Amount,
                    payment.Reference,
                    payment.CardFranchiseCode,
                    payment.ApprovalNumber,
                    payment.BankAccountId,
                    payment.Notes))
                .ToArray()
            : [new PosSalePaymentContract(1, "Cash", command.Withholding?.NetAmount ?? invoice.PayableAmount, null)];
        var withholding = command.Withholding ??
            new WithholdingCalculationSnapshot(invoice.PayableAmount, 0m, invoice.PayableAmount, []);
        if (withholding.GrossAmount != invoice.PayableAmount ||
            withholding.WithholdingTotal != withholding.Lines.Sum(line => line.Amount) ||
            withholding.NetAmount + withholding.WithholdingTotal != invoice.PayableAmount)
            throw new InvalidOperationException("The withholding snapshot does not reconcile with the sale.");
        if (payments.Sum(payment => payment.Amount) != withholding.NetAmount)
        {
            throw new InvalidOperationException("Payments must equal the payable amount.");
        }
        if (payments.Any(payment =>
                (payment.MethodCode is "Card" or "DebitCard" or "CreditCard") !=
                (!string.IsNullOrWhiteSpace(payment.CardFranchiseCode) && !string.IsNullOrWhiteSpace(payment.ApprovalNumber))))
            throw new InvalidOperationException("Card payments require franchise and approval number.");
        if (payments.Any(payment => payment.Reference?.Length > 160 || payment.Notes?.Length > 500 ||
                (payment.MethodCode == "Transfer" && string.IsNullOrWhiteSpace(payment.Reference)) ||
                (payment.MethodCode != "Transfer" && (payment.BankAccountId is not null || payment.Notes is not null))))
            throw new InvalidOperationException("Transfer payments require valid evidence and settlement configuration.");

        var taxes = lines
            .GroupBy(line => line.TaxCode, StringComparer.Ordinal)
            .Select(group => new PosSaleTaxContract(
                group.Key,
                group.Sum(line => line.TaxAmount)))
            .OrderBy(tax => tax.Code, StringComparer.Ordinal)
            .ToArray();
        return new PosSaleUploadRequest(
            command.Context.TenantId.Value,
            command.Context.BusinessId.Value,
            command.Context.WarehouseId.Value,
            command.Context.DeviceId?.Value ?? throw new InvalidOperationException("An Edge sale requires DeviceId."),
            command.Context.WorkSessionId.Value,
            command.UserId.Value,
            command.DocumentId.Value,
            new PosSaleDocumentNumberContract(
                documentNumber.SeriesId,
                documentNumber.DocumentType,
                documentNumber.Prefix,
                documentNumber.SeriesCode,
                documentNumber.Consecutive,
                documentNumber.Padding,
                documentNumber.FullNumber),
            new PosSaleCommercialSnapshotContract(
                command.DocumentType,
                command.IssuedAt,
                command.CustomerIdentification,
                taxes,
                invoice.UntaxedAmount,
                invoice.TaxAmount,
                invoice.PayableAmount,
                withholding),
            snapshot is null || fiscalNumber is null || fiscalAuthorizationId is null
                ? null
                : new PosSaleFiscalSnapshotContract(
                    fiscalNumber.SeriesId,
                    fiscalAuthorizationId.Value,
                    fiscalNumber.AuthorizationNumber,
                    PosSaleDocumentTypes.Invoice,
                    snapshot.FiscalNumber,
                    snapshot.Prefix,
                    snapshot.Consecutive,
                    snapshot.IssuedAt,
                    command.SupplierTaxId!,
                    snapshot.CustomerIdentification,
                    (int)command.Environment!.Value,
                    command.TechnicalKey!.Version,
                    taxes,
                    snapshot.UntaxedAmount,
                    snapshot.TaxAmount,
                    snapshot.PayableAmount,
                    snapshot.Cufe,
                    snapshot.QrPayload),
            lines,
            payments,
            snapshot is null ? null : command.UblSnapshot,
            command.CustomerId,
            SourceOrderId: command.SourceOrderId);
    }

    private static readonly System.Linq.Expressions.Expression<Func<PosOutboxRow, PosEdgeOutboxItem>>
        ToOutboxItem = row => new PosEdgeOutboxItem(
            row.MessageId,
            new DocumentId(row.DocumentId),
            row.Type,
            row.Payload,
            row.AttemptCount,
            row.Status,
            row.NextAttemptAt,
            row.LeaseAcquiredAt,
            row.LastError,
            row.RemoteStatus,
            row.ServerReceiptId,
            row.WorkSessionId);

    private static string Truncate(string value) =>
        value.Length <= 2000 ? value : value[..2000];

    private static async Task BackfillFiscalAuthorizationAsync(
        PosEdgeDbContext context,
        Guid seriesId,
        Guid fiscalAuthorizationId,
        CancellationToken cancellationToken)
    {
        DbConnection connection = context.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }

        await using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE FiscalSeriesCursors " +
            "SET FiscalAuthorizationId = $authorizationId " +
            "WHERE SeriesId = $seriesId " +
            "AND FiscalAuthorizationId = '00000000-0000-0000-0000-000000000000';";
        var authorizationParameter = command.CreateParameter();
        authorizationParameter.ParameterName = "$authorizationId";
        authorizationParameter.Value = fiscalAuthorizationId.ToString("D").ToUpperInvariant();
        command.Parameters.Add(authorizationParameter);
        var seriesParameter = command.CreateParameter();
        seriesParameter.ParameterName = "$seriesId";
        seriesParameter.Value = seriesId.ToString("D").ToUpperInvariant();
        command.Parameters.Add(seriesParameter);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpgradeDeviceIdentityAsync(
        PosEdgeDbContext context,
        CancellationToken cancellationToken)
    {
        DbConnection connection = context.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(cancellationToken);

        foreach (var table in new[] { "DocumentSeriesCursors", "FiscalSeriesCursors" })
        {
            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using (var info = connection.CreateCommand())
            {
                info.CommandText = $"PRAGMA table_info('{table}');";
                await using var reader = await info.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                    columns.Add(reader.GetString(1));
            }

            var legacyDeviceColumn = "Register" + "Id";
            if (!columns.Contains(legacyDeviceColumn) || columns.Contains("DeviceId"))
                continue;
            await using var rename = connection.CreateCommand();
            rename.CommandText =
                $"ALTER TABLE {table} RENAME COLUMN {legacyDeviceColumn} TO DeviceId;";
            await rename.ExecuteNonQueryAsync(cancellationToken);
        }
    }
    private static async Task UpgradeDocumentNumberingAsync(
        PosEdgeDbContext context,
        CancellationToken cancellationToken)
    {
        DbConnection connection = context.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS DocumentSeriesCursors(
                    SeriesId TEXT NOT NULL PRIMARY KEY,
                    DeviceId TEXT NOT NULL,
                    DocumentType TEXT NOT NULL,
                    Prefix TEXT NOT NULL,
                    SeriesCode TEXT NOT NULL,
                    Padding INTEGER NOT NULL,
                    NextConsecutive INTEGER NOT NULL,
                    RangeEnd INTEGER NOT NULL,
                    IsActive INTEGER NOT NULL
                );
                CREATE UNIQUE INDEX IF NOT EXISTS
                    IX_DocumentSeriesCursors_DeviceId_DocumentType
                    ON DocumentSeriesCursors(DeviceId,DocumentType);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA table_info('IssuedSales');";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                columns.Add(reader.GetString(1));
            }
        }

        if (!columns.Contains("DocumentNumber"))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                ALTER TABLE IssuedSales
                    ADD COLUMN DocumentNumber TEXT NOT NULL DEFAULT '';
                UPDATE IssuedSales
                    SET DocumentNumber='UNASSIGNED-' || DocumentId
                    WHERE DocumentNumber='';
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task UpgradeFiscalSeriesAsync(
        PosEdgeDbContext context,
        CancellationToken cancellationToken)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        DbConnection connection = context.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA table_info('FiscalSeriesCursors');";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                columns.Add(reader.GetString(1));
            }
        }

        if (!columns.Contains("FiscalAuthorizationId"))

        {

            await using var command = connection.CreateCommand();

            command.CommandText =

                "ALTER TABLE FiscalSeriesCursors ADD COLUMN FiscalAuthorizationId TEXT NOT NULL " +

                "DEFAULT '00000000-0000-0000-0000-000000000000';";

            await command.ExecuteNonQueryAsync(cancellationToken);

        }
        if (!columns.Contains("RangeStart"))
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                "ALTER TABLE FiscalSeriesCursors ADD COLUMN RangeStart INTEGER NOT NULL DEFAULT 1;";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        if (!columns.Contains("ValidFrom"))
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                "ALTER TABLE FiscalSeriesCursors ADD COLUMN ValidFrom TEXT NOT NULL DEFAULT '0001-01-01';";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        var additions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["AuthorizationRangeStart"] = "INTEGER NOT NULL DEFAULT 1",
            ["AuthorizationRangeEnd"] = "INTEGER NOT NULL DEFAULT 1",
            ["ExpirationWarningDays"] = "INTEGER NOT NULL DEFAULT 3",
            ["RemainingNumberWarningThreshold"] = "INTEGER NOT NULL DEFAULT 100",
            ["IsEmissionEnabled"] = "INTEGER NOT NULL DEFAULT 0"
        };
        foreach (var addition in additions.Where(item => !columns.Contains(item.Key)))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"ALTER TABLE FiscalSeriesCursors ADD COLUMN {addition.Key} {addition.Value};";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                DROP INDEX IF EXISTS IX_FiscalSeriesCursors_DeviceId;
                CREATE INDEX IF NOT EXISTS IX_FiscalSeriesCursors_DeviceId_IsActive
                    ON FiscalSeriesCursors(DeviceId,IsActive);
                UPDATE FiscalSeriesCursors
                SET AuthorizationRangeStart=RangeStart
                WHERE AuthorizationRangeStart=1 AND RangeStart<>1;
                UPDATE FiscalSeriesCursors
                SET AuthorizationRangeEnd=RangeEnd
                WHERE AuthorizationRangeEnd=1 AND RangeEnd<>1;
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        // EnsureCreated does not update indexes in an existing enrolled checkout.
        // Reassert the schema invariant idempotently: empty fiscal numbers belong to
        // commercial receipts and must not collide with one another.
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                DROP INDEX IF EXISTS IX_IssuedSales_FiscalNumber;
                CREATE UNIQUE INDEX IF NOT EXISTS IX_IssuedSales_FiscalNumber
                    ON IssuedSales(FiscalNumber)
                    WHERE FiscalNumber <> '';
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task UpgradeFiscalStatusAsync(
        PosEdgeDbContext context,
        CancellationToken cancellationToken)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        DbConnection connection = context.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(cancellationToken);

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA table_info('IssuedSales');";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                columns.Add(reader.GetString(1));
        }

        var additions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["RemoteFiscalStatus"] = "TEXT NULL",
            ["RemoteFiscalStatusCode"] = "TEXT NULL",
            ["RemoteFiscalStatusDescription"] = "TEXT NULL",
            ["RemoteFiscalUpdatedAt"] = "TEXT NULL"
        };
        foreach (var addition in additions.Where(item => !columns.Contains(item.Key)))
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                $"ALTER TABLE IssuedSales ADD COLUMN {addition.Key} {addition.Value};";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE IssuedSales
                SET RemoteFiscalStatus='LocallyIssuedPendingSync', RemoteFiscalUpdatedAt=IssuedAt
                WHERE RemoteFiscalStatus IS NULL;
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS PosSyncState(
                  Key TEXT NOT NULL PRIMARY KEY,
                  Cursor TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS PosPrintAudit(
                  DocumentId TEXT NOT NULL,
                  ReprintedAt TEXT NOT NULL,
                  UserId TEXT NOT NULL,
                  FiscalStatus TEXT NULL,
                  PRIMARY KEY(DocumentId,ReprintedAt,UserId)
                );
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static void ValidateUblSnapshot(PosEdgeIssueCommand command, FiscalSeriesCursorRow cursor)
    {
        var snapshot = command.UblSnapshot;
        if (snapshot is null) return;
        if (snapshot.FiscalIssuerConfigurationId == Guid.Empty ||
            string.IsNullOrWhiteSpace(snapshot.SoftwareIdentificationCode))
            throw new InvalidOperationException("The UBL snapshot requires an issuer configuration version and software ID.");
        if (snapshot.Supplier.Identification != command.SupplierTaxId ||
            snapshot.Customer.Identification != command.CustomerIdentification)
            throw new InvalidOperationException("The UBL supplier or customer differs from the sale being issued.");
        if (snapshot.Authorization.Number != cursor.AuthorizationNumber ||
            snapshot.Authorization.Prefix != cursor.Prefix ||
            snapshot.Authorization.RangeStart != cursor.AuthorizationRangeStart ||
            snapshot.Authorization.RangeEnd != cursor.AuthorizationRangeEnd ||
            snapshot.Authorization.ValidUntil != cursor.ValidUntil)
            throw new InvalidOperationException("The UBL authorization differs from the provisioned fiscal series.");
        if (snapshot.Lines.Count != command.Lines.Count ||
            !snapshot.Lines.Select(line => line.LineNumber).Order()
                .SequenceEqual(Enumerable.Range(1, command.Lines.Count)))
            throw new InvalidOperationException("The UBL line metadata does not match the sale lines.");
        var fiscalLines = snapshot.Lines.ToDictionary(line => line.LineNumber);
        if (command.Lines.Select((line, index) => new { Line = line, Number = index + 1 })
            .Any(item => fiscalLines[item.Number].TaxPercent != item.Line.Product.TaxRate))
            throw new InvalidOperationException("The UBL tax rate differs from the immutable sale lines.");
    }}

