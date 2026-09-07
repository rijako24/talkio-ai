using Auraly.Pos.Edge.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Auraly.Pos.Edge.Host;

public enum PosUnifiedOutboxRoute
{
    WorkSessionOpened,
    Sale,
    CashMovement,
    WorkSessionClosure,
    CustomerCreated
}

public sealed class PosUnifiedOutboxDispatcher(
    string connectionString,
    TimeProvider timeProvider)
{
    public async Task<PosUnifiedOutboxRoute?> NextAsync(
        CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT current.Type
            FROM Outbox current
            WHERE ((current.Status IN ('Pending','RetryScheduled')
                    AND (current.NextAttemptAt IS NULL OR current.NextAttemptAt<=$now))
                   OR (current.Status='Uploading' AND current.LastAttemptAt<$stale))
              AND NOT EXISTS
              (
                SELECT 1 FROM Outbox prior
                WHERE prior.Status<>'Uploaded'
                  AND prior.LocalSequence<current.LocalSequence
              )
            ORDER BY current.LocalSequence
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        command.Parameters.AddWithValue("$stale", now.AddMinutes(-2).ToString("O"));
        var type = await command.ExecuteScalarAsync(cancellationToken) as string;
        return type switch
        {
            null => null,
            PosOutboxMessageTypes.WorkSessionOpened => PosUnifiedOutboxRoute.WorkSessionOpened,
            PosOutboxMessageTypes.CashMovement => PosUnifiedOutboxRoute.CashMovement,
            PosOutboxMessageTypes.WorkSessionClosure => PosUnifiedOutboxRoute.WorkSessionClosure,
            PosOutboxMessageTypes.CustomerCreated => PosUnifiedOutboxRoute.CustomerCreated,
            _ => PosUnifiedOutboxRoute.Sale
        };
    }

    public async Task<TimeSpan?> NextRetryDelayAsync(
        CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT MIN(NextAttemptAt) FROM Outbox
            WHERE Status='RetryScheduled' AND NextAttemptAt IS NOT NULL;
            """;
        var value = await command.ExecuteScalarAsync(cancellationToken) as string;
        if (value is null) return null;
        var dueAt = DateTimeOffset.Parse(value);
        return dueAt <= now ? TimeSpan.FromMilliseconds(100) : dueAt - now;
    }
}
