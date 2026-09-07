using Microsoft.EntityFrameworkCore;

namespace Auraly.Pos.Edge.Infrastructure;

internal sealed class PosEdgeDbContext(DbContextOptions<PosEdgeDbContext> options)
    : DbContext(options)
{
    public DbSet<DocumentSeriesCursorRow> DocumentSeriesCursors => Set<DocumentSeriesCursorRow>();
    public DbSet<FiscalSeriesCursorRow> FiscalSeriesCursors => Set<FiscalSeriesCursorRow>();
    public DbSet<IssuedSaleRow> IssuedSales => Set<IssuedSaleRow>();
    public DbSet<PosOutboxRow> Outbox => Set<PosOutboxRow>();
    public DbSet<PosSyncStateRow> SyncState => Set<PosSyncStateRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DocumentSeriesCursorRow>(entity =>
        {
            entity.ToTable("DocumentSeriesCursors");
            entity.HasKey(row => row.SeriesId);
            entity.HasIndex(row => new { row.DeviceId, row.DocumentType }).IsUnique();
            entity.Property(row => row.DocumentType).HasMaxLength(32);
            entity.Property(row => row.Prefix).HasMaxLength(8);
            entity.Property(row => row.SeriesCode).HasMaxLength(16);
        });

        modelBuilder.Entity<FiscalSeriesCursorRow>(entity =>
        {
            entity.ToTable("FiscalSeriesCursors");
            entity.HasKey(row => row.SeriesId);
            entity.HasIndex(row => new { row.DeviceId, row.IsActive });
            entity.Property(row => row.Prefix).HasMaxLength(16);
            entity.Property(row => row.AuthorizationNumber).HasMaxLength(64);
        });

        modelBuilder.Entity<IssuedSaleRow>(entity =>
        {
            entity.ToTable("IssuedSales");
            entity.HasKey(row => row.DocumentId);
            entity.HasIndex(row => row.DocumentNumber).IsUnique();
            // Commercial receipts do not have a DIAN number and are stored with an
            // empty value. Keep uniqueness only for actual fiscal documents so more
            // than one receipt can be issued while the checkout is offline.
            entity.HasIndex(row => row.FiscalNumber)
                .IsUnique()
                .HasFilter("FiscalNumber <> ''");
            entity.Property(row => row.DocumentNumber).HasMaxLength(64);
            entity.Property(row => row.FiscalNumber).HasMaxLength(64);
            entity.Property(row => row.Cufe).HasMaxLength(96);
            entity.Property(row => row.RemoteFiscalStatus).HasMaxLength(48);
            entity.Property(row => row.RemoteFiscalStatusCode).HasMaxLength(64);
            entity.Property(row => row.RemoteFiscalStatusDescription).HasMaxLength(2000);
        });

        modelBuilder.Entity<PosOutboxRow>(entity =>
        {
            entity.ToTable("Outbox");
            entity.HasKey(row => row.MessageId);
            entity.HasIndex(row => row.DocumentId).IsUnique();
            entity.HasIndex(row => row.LocalSequence).IsUnique();
            entity.HasIndex(row => new { row.WorkSessionId, row.CreatedAt });
            entity.Property(row => row.Type).HasMaxLength(128);
            entity.Property(row => row.Status).HasMaxLength(32);
            entity.Property(row => row.RemoteStatus).HasMaxLength(40);
            entity.Property(row => row.LastError).HasMaxLength(2000);
        });

        modelBuilder.Entity<PosSyncStateRow>(entity =>
        {
            entity.ToTable("PosSyncState");
            entity.HasKey(row => row.Key);
            entity.Property(row => row.Key).HasMaxLength(64);
        });
    }
}

internal sealed class DocumentSeriesCursorRow
{
    public Guid SeriesId { get; set; }
    public Guid DeviceId { get; set; }
    public string DocumentType { get; set; } = string.Empty;
    public string Prefix { get; set; } = string.Empty;
    public string SeriesCode { get; set; } = string.Empty;
    public int Padding { get; set; }
    public long NextConsecutive { get; set; }
    public long RangeEnd { get; set; }
    public bool IsActive { get; set; }
}

internal sealed class FiscalSeriesCursorRow
{
    public Guid SeriesId { get; set; }
    public Guid DeviceId { get; set; }
    public string Prefix { get; set; } = string.Empty;
    public string AuthorizationNumber { get; set; } = string.Empty;
    public Guid FiscalAuthorizationId { get; set; }
    public long RangeStart { get; set; }
    public long NextConsecutive { get; set; }
    public long RangeEnd { get; set; }
    public DateOnly ValidFrom { get; set; }
    public DateOnly ValidUntil { get; set; }
    public bool IsActive { get; set; }
    public long AuthorizationRangeStart { get; set; }
    public long AuthorizationRangeEnd { get; set; }
    public int ExpirationWarningDays { get; set; }
    public long RemainingNumberWarningThreshold { get; set; }
    public bool IsEmissionEnabled { get; set; }
}

internal sealed class IssuedSaleRow
{
    public Guid DocumentId { get; set; }
    public string DocumentNumber { get; set; } = string.Empty;
    public string FiscalNumber { get; set; } = string.Empty;
    public string Cufe { get; set; } = string.Empty;
    public decimal Total { get; set; }
    public DateTimeOffset IssuedAt { get; set; }
    public string FiscalSnapshotJson { get; set; } = string.Empty;
    public string? RemoteFiscalStatus { get; set; }
    public string? RemoteFiscalStatusCode { get; set; }
    public string? RemoteFiscalStatusDescription { get; set; }
    public DateTimeOffset? RemoteFiscalUpdatedAt { get; set; }
}

internal sealed class PosOutboxRow
{
    public Guid MessageId { get; set; }
    public Guid DocumentId { get; set; }
    public long? LocalSequence { get; set; }
    public Guid? WorkSessionId { get; set; }
    public string Type { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;
    public string Status { get; set; } = PosOutboxStatus.Pending;
    public int AttemptCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UploadedAt { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
    public DateTimeOffset? LeaseAcquiredAt { get; set; }
    public DateTimeOffset? LastAttemptAt { get; set; }
    public string? LastError { get; set; }
    public string? RemoteStatus { get; set; }
    public Guid? ServerReceiptId { get; set; }
}

internal sealed class PosSyncStateRow
{
    public string Key { get; set; } = string.Empty;
    public string Cursor { get; set; } = string.Empty;
}
public static class PosOutboxStatus
{
    public const string Pending = "Pending";
    public const string Uploading = "Uploading";
    public const string Uploaded = "Uploaded";
    public const string FiscalIntegrityConflict = "FiscalIntegrityConflict";
    public const string RetryScheduled = "RetryScheduled";
    public const string FailedPermanent = "FailedPermanent";
}

public static class PosOutboxMessageTypes
{
    public const string WorkSessionOpened = "work-session.opened";
    public const string CashMovement = "cash.movement.confirmed";
    public const string WorkSessionClosure = "work-session.closed";
    public const string CustomerCreated = "customer.created";

    public static bool IsLocalSale(string type) =>
        !string.Equals(type, WorkSessionOpened, StringComparison.Ordinal) &&
        !string.Equals(type, CashMovement, StringComparison.Ordinal) &&
        !string.Equals(type, WorkSessionClosure, StringComparison.Ordinal) &&
        !string.Equals(type, CustomerCreated, StringComparison.Ordinal);
}
