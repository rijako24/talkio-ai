using System.Data;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Auraly.BuildingBlocks.Domain.Identifiers;
using Auraly.Contracts.Catalog;
using Auraly.Contracts.Parties;
using Auraly.Pos.Edge.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Auraly.Pos.Edge.Host;

public sealed record PosCustomerOutboxStatus(
    int PendingCount,
    DateTimeOffset? OldestPendingAt,
    string? LastError);

public sealed class PosCustomerOutboxStore(
    string connectionString,
    IAuralyIdGenerator ids,
    TimeProvider timeProvider,
    PosOperationalScope scope)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<PosCustomerPricing> QueueAsync(
        PosCreateCustomerInput input,
        Guid workSessionId,
        CancellationToken cancellationToken = default)
    {
        await PosUnifiedOutboxSchema.EnsureCreatedAsync(connectionString, cancellationToken);
        var customerId = ids.NewId();
        var request = new CreateCustomerRequest(
            customerId,
            scope.BusinessId,
            new PartyInput(
                input.PartyType,
                input.IdentificationCountryId,
                input.IdentificationTypeCode,
                input.Identification,
                input.VerificationDigit,
                input.DisplayName,
                input.LegalName,
                input.FirstName,
                input.LastName,
                input.Email,
                input.Phone),
            input.PrimarySite,
            null,
            RequestedCustomerId: customerId);
        var now = timeProvider.GetUtcNow();
        var local = new PosCustomerPricing(
            customerId,
            input.Identification.Trim(),
            input.DisplayName.Trim(),
            null,
            true,
            false);

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        await using (var duplicate = connection.CreateCommand())
        {
            duplicate.Transaction = transaction;
            duplicate.CommandText = """
                SELECT COUNT(1) FROM PosPricingCustomers
                WHERE IsActive=1 AND Identification=$identification;
                """;
            duplicate.Parameters.AddWithValue("$identification", local.Identification);
            if (Convert.ToInt32(await duplicate.ExecuteScalarAsync(cancellationToken)) > 0)
                throw new InvalidOperationException(
                    "Ya existe un cliente local con esa identificación.");
        }
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO PosPricingCustomers(
                  CustomerId,Identification,Name,PriceChannelId,RequiresElectronicInvoice,
                  IsActive,AppliesWithholding,TaxResponsibilities,TaxJurisdictionCode)
                VALUES($customer,$identification,$name,NULL,$electronic,1,0,'[]',NULL);
                INSERT INTO Outbox(
                  MessageId,DocumentId,WorkSessionId,Type,Payload,Status,AttemptCount,CreatedAt)
                VALUES($customer,$customer,$session,$type,$payload,'Pending',0,$now);
                """;
            insert.Parameters.AddWithValue("$customer", customerId.ToString("D"));
            insert.Parameters.AddWithValue("$identification", local.Identification);
            insert.Parameters.AddWithValue("$name", local.Name);
            insert.Parameters.AddWithValue("$electronic", local.RequiresElectronicInvoice ? 1 : 0);
            insert.Parameters.AddWithValue("$session", workSessionId.ToString("D"));
            insert.Parameters.AddWithValue("$type", PosOutboxMessageTypes.CustomerCreated);
            insert.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(request, Json));
            insert.Parameters.AddWithValue("$now", now.ToString("O"));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return local;
    }

    public async Task<(Guid CustomerId, string Payload, int Attempts)?> ClaimAsync(
        CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        await using var read = connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText = """
            SELECT DocumentId,Payload,AttemptCount FROM Outbox
            WHERE Type=$type
              AND ((Status IN ('Pending','RetryScheduled') AND
                    (NextAttemptAt IS NULL OR NextAttemptAt<=$now))
                   OR (Status='Uploading' AND LastAttemptAt<$stale))
              AND NOT EXISTS (
                  SELECT 1 FROM Outbox prior
                  WHERE prior.Status<>'Uploaded'
                    AND prior.LocalSequence<Outbox.LocalSequence)
            ORDER BY LocalSequence LIMIT 1;
            """;
        read.Parameters.AddWithValue("$type", PosOutboxMessageTypes.CustomerCreated);
        read.Parameters.AddWithValue("$now", now.ToString("O"));
        read.Parameters.AddWithValue("$stale", now.AddMinutes(-2).ToString("O"));
        await using var reader = await read.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }
        var item = (Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetInt32(2) + 1);
        await reader.DisposeAsync();
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE Outbox SET Status='Uploading',AttemptCount=AttemptCount+1,LastAttemptAt=$now
            WHERE DocumentId=$id AND Type=$type;
            """;
        update.Parameters.AddWithValue("$now", now.ToString("O"));
        update.Parameters.AddWithValue("$id", item.Item1.ToString("D"));
        update.Parameters.AddWithValue("$type", PosOutboxMessageTypes.CustomerCreated);
        await update.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return item;
    }

    public Task MarkUploadedAsync(Guid customerId, CancellationToken ct = default) =>
        UpdateAsync(customerId, PosOutboxStatus.Uploaded, null, null, removeLocal: false, ct);

    public Task ScheduleRetryAsync(
        Guid customerId, int attempts, string error, CancellationToken ct = default)
    {
        var seconds = Math.Min(300, 5 * Math.Pow(2, Math.Clamp(attempts - 1, 0, 6)));
        return UpdateAsync(customerId, PosOutboxStatus.RetryScheduled,
            timeProvider.GetUtcNow().AddSeconds(seconds), error, removeLocal: false, ct);
    }

    public Task MarkFailedAsync(Guid customerId, string error, CancellationToken ct = default) =>
        UpdateAsync(customerId, PosOutboxStatus.FailedPermanent, null, error, removeLocal: true, ct);

    public async Task<PosCustomerOutboxStatus> ReadStatusAsync(
        CancellationToken cancellationToken = default)
    {
        await PosUnifiedOutboxSchema.EnsureCreatedAsync(connectionString, cancellationToken);
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT CreatedAt,LastError FROM Outbox
            WHERE Type=$type AND Status<>'Uploaded' ORDER BY CreatedAt;
            """;
        command.Parameters.AddWithValue("$type", PosOutboxMessageTypes.CustomerCreated);
        var count = 0;
        DateTimeOffset? oldest = null;
        string? error = null;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            count++;
            oldest ??= DateTimeOffset.Parse(reader.GetString(0));
            if (!reader.IsDBNull(1)) error = reader.GetString(1);
        }
        return new(count, oldest, error);
    }

    private async Task UpdateAsync(
        Guid customerId,
        string status,
        DateTimeOffset? next,
        string? error,
        bool removeLocal,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE Outbox SET Status=$status,NextAttemptAt=$next,LastError=$error,
                    UploadedAt=CASE WHEN $status='Uploaded' THEN $now ELSE UploadedAt END
                WHERE DocumentId=$id AND Type=$type;
                """;
            command.Parameters.AddWithValue("$status", status);
            command.Parameters.AddWithValue("$next", (object?)next?.ToString("O") ?? DBNull.Value);
            command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
            command.Parameters.AddWithValue("$now", timeProvider.GetUtcNow().ToString("O"));
            command.Parameters.AddWithValue("$id", customerId.ToString("D"));
            command.Parameters.AddWithValue("$type", PosOutboxMessageTypes.CustomerCreated);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        if (removeLocal)
        {
            await using var remove = connection.CreateCommand();
            remove.Transaction = transaction;
            remove.CommandText = "DELETE FROM PosPricingCustomers WHERE CustomerId=$id;";
            remove.Parameters.AddWithValue("$id", customerId.ToString("D"));
            await remove.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }
}

public sealed class PosCustomerOutboxUploader(
    PosCustomerOutboxStore store,
    HttpClient http,
    PosDeviceCredentials credentials,
    PosCatalogSynchronizer synchronization,
    PosSynchronizationEventLog events)
{
    public async Task<bool> UploadNextAsync(CancellationToken cancellationToken = default)
    {
        var item = await store.ClaimAsync(cancellationToken);
        if (item is null) return false;
        CreateCustomerRequest request;
        try
        {
            request = JsonSerializer.Deserialize<CreateCustomerRequest>(
                item.Value.Payload, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? throw new JsonException("The customer payload is empty.");
        }
        catch (JsonException exception)
        {
            await store.MarkFailedAsync(item.Value.CustomerId, exception.Message, cancellationToken);
            events.Record("Error", "Cliente", "Cliente local rechazado", exception.Message);
            return true;
        }

        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, "/api/pos/v1/customers");
            message.Headers.Add("X-Auraly-Device-Id", credentials.DeviceId.ToString("D"));
            message.Headers.Add("X-Auraly-Device-Secret", credentials.Secret);
            message.Content = JsonContent.Create(request);
            using var response = await http.SendAsync(message, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var created = await response.Content.ReadFromJsonAsync<CustomerDetail>(cancellationToken)
                    ?? throw new InvalidDataException("Auraly Server returned an empty customer.");
                if (created.CustomerId != item.Value.CustomerId)
                    throw new InvalidDataException("Auraly Server returned a different customer identifier.");
                await store.MarkUploadedAsync(item.Value.CustomerId, cancellationToken);
                await synchronization.SynchronizeAsync(cancellationToken);
                events.Record("Success", "Cliente", $"Cliente local subido: {created.DisplayName}",
                    created.Identification);
                return true;
            }

            var detail = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.StatusCode is HttpStatusCode.RequestTimeout ||
                (int)response.StatusCode == 429 || (int)response.StatusCode >= 500)
            {
                await store.ScheduleRetryAsync(item.Value.CustomerId, item.Value.Attempts,
                    $"HTTP {(int)response.StatusCode}: {detail}", cancellationToken);
                return true;
            }
            await store.MarkFailedAsync(item.Value.CustomerId,
                $"HTTP {(int)response.StatusCode}: {detail}", cancellationToken);
            events.Record("Error", "Cliente", "Cliente local rechazado por el servidor", detail);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            await store.ScheduleRetryAsync(
                item.Value.CustomerId, item.Value.Attempts, exception.Message, cancellationToken);
            events.Record("Warning", "Cliente", "Cliente local pendiente de sincronización",
                request.Party.Identification);
        }
        return true;
    }
}
