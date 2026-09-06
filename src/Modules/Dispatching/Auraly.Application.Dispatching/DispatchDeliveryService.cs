using Auraly.Contracts.Dispatching;

namespace Auraly.Application.Dispatching;

public interface IDispatchDeliveryStore
{
    Task<IReadOnlyList<DispatchReasonOption>> ReasonsAsync(DispatchActorIdentity actor, string reasonType, CancellationToken ct);
    Task<DispatchExecutionDetail?> GetAsync(DispatchActorIdentity actor, Guid dispatchId, CancellationToken ct);
    Task<DispatchExecutionDetail> RecordAsync(DispatchActorIdentity actor, Guid dispatchId, RecordDispatchDeliveryRequest request, CancellationToken ct);
    Task<DispatchExecutionDetail> ReorderAsync(DispatchActorIdentity actor, Guid dispatchId, ReorderDispatchDocumentsRequest request, byte[] rowVersion, CancellationToken ct);
    Task<DispatchExecutionDetail> RecordExpenseAsync(DispatchActorIdentity actor, Guid dispatchId, DispatchExpenseInput request, CancellationToken ct);
    Task<DispatchExecutionDetail> ReviewExpenseAsync(DispatchActorIdentity actor, Guid dispatchId, Guid expenseId, ReviewDispatchExpenseRequest request, CancellationToken ct);
    Task<DispatchExecutionDetail> CloseRouteAsync(DispatchActorIdentity actor, Guid dispatchId, CloseDispatchRouteRequest request, CancellationToken ct);
    Task<DispatchExecutionDetail> SettleAsync(DispatchActorIdentity actor, Guid dispatchId, SettleDispatchRequest request, CancellationToken ct);
}

public sealed class DispatchDeliveryService(IDispatchDeliveryStore store)
{
    public Task<IReadOnlyList<DispatchReasonOption>> ReasonsAsync(DispatchActorIdentity actor, string reasonType, CancellationToken ct)
    {
        RequireAny(actor, DispatchPermissionCodes.Read, DispatchPermissionCodes.ExecuteDeliveries, DispatchPermissionCodes.Settle);
        if (reasonType is not ("NotDelivered" or "DeliveryReturn")) throw new DispatchValidationException("El tipo de motivo no es válido.");
        return store.ReasonsAsync(actor, reasonType, ct);
    }

    public async Task<DispatchExecutionDetail> GetAsync(DispatchActorIdentity actor, Guid dispatchId, CancellationToken ct)
    {
        RequireAny(actor, DispatchPermissionCodes.Read, DispatchPermissionCodes.ExecuteDeliveries, DispatchPermissionCodes.Settle);
        Required(dispatchId, "DispatchId");
        return await store.GetAsync(actor, dispatchId, ct)
            ?? throw new DispatchNotFoundException("The dispatch is not assigned or does not exist.");
    }

    public Task<DispatchExecutionDetail> RecordAsync(DispatchActorIdentity actor, Guid dispatchId, RecordDispatchDeliveryRequest request, CancellationToken ct)
    {
        Require(actor, DispatchPermissionCodes.ExecuteDeliveries);
        Required(dispatchId, "DispatchId"); Required(request.DispatchSourceDocumentId, "DispatchSourceDocumentId");
        Idempotency(request.IdempotencyKey);
        if (request.DeliveryStatus is not (DeliveryStatuses.Delivered or DeliveryStatuses.PartiallyDelivered or DeliveryStatuses.NotDelivered))
            throw new DispatchValidationException("DeliveryStatus is invalid.");
        if (request.OccurredAt == default) throw new DispatchValidationException("OccurredAt is required.");
        if (request.Latitude is < -90 or > 90 || request.Longitude is < -180 or > 180)
            throw new DispatchValidationException("The delivery coordinates are invalid.");
        var reason = Text(request.Reason, 160);
        if (request.DeliveryStatus != DeliveryStatuses.Delivered && reason is null)
            throw new DispatchValidationException("A reason is required for a partial or failed delivery.");
        if (request.DeliveryStatus == DeliveryStatuses.NotDelivered && (request.Payments.Count > 0 || request.Returns.Count > 0))
            throw new DispatchValidationException("A non-delivery cannot include payments or returned merchandise.");
        if (request.Payments.Count > 20 || request.Returns.Count > 500)
            throw new DispatchValidationException("The delivery result exceeds the allowed number of details.");
        foreach (var payment in request.Payments) ValidatePayment(payment);
        if (request.Returns.Select(value => value.OriginalLineNumber).Distinct().Count() != request.Returns.Count)
            throw new DispatchValidationException("A returned line can only appear once.");
        foreach (var value in request.Returns)
        {
            if (value.OriginalLineNumber <= 0 || value.Quantity <= 0) throw new DispatchValidationException("Return line and quantity must be positive.");
            if (value.InventoryDisposition is not ("Sellable" or "NotReturned")) throw new DispatchValidationException("Return disposition is invalid.");
            if (string.IsNullOrWhiteSpace(value.ReasonCode) || string.IsNullOrWhiteSpace(value.ReasonDescription) || value.ReasonDescription.Trim().Length > 300)
                throw new DispatchValidationException("Every returned item requires a reason.");
        }
        return store.RecordAsync(actor, dispatchId, request with { Reason = reason, Notes = Text(request.Notes, 500), IdempotencyKey = request.IdempotencyKey.Trim() }, ct);
    }

    public Task<DispatchExecutionDetail> ReorderAsync(DispatchActorIdentity actor, Guid dispatchId, ReorderDispatchDocumentsRequest request, CancellationToken ct)
    {
        Require(actor, DispatchPermissionCodes.ExecuteDeliveries); Required(dispatchId, "DispatchId"); Idempotency(request.IdempotencyKey);
        if (request.OrderedDocumentIds.Count == 0 || request.OrderedDocumentIds.Any(id => id == Guid.Empty) || request.OrderedDocumentIds.Distinct().Count() != request.OrderedDocumentIds.Count)
            throw new DispatchValidationException("The dispatch order must contain unique valid documents.");
        return store.ReorderAsync(actor, dispatchId, request, RowVersion(request.RowVersion), ct);
    }

    public Task<DispatchExecutionDetail> CloseRouteAsync(DispatchActorIdentity actor, Guid dispatchId, CloseDispatchRouteRequest request, CancellationToken ct)
    {
        Require(actor, DispatchPermissionCodes.Settle); Required(dispatchId, "DispatchId"); Idempotency(request.IdempotencyKey);
        if (request.DeclaredCash < 0) throw new DispatchValidationException("DeclaredCash cannot be negative.");
        return store.CloseRouteAsync(actor, dispatchId, request with { DifferenceReason = Text(request.DifferenceReason, 500), IdempotencyKey = request.IdempotencyKey.Trim() }, ct);
    }

    public Task<DispatchExecutionDetail> RecordExpenseAsync(DispatchActorIdentity actor, Guid dispatchId, DispatchExpenseInput request, CancellationToken ct)
    {
        Require(actor, DispatchPermissionCodes.ExecuteDeliveries); Required(dispatchId, "DispatchId"); Idempotency(request.IdempotencyKey);
        if (request.Amount <= 0) throw new DispatchValidationException("Expense amount must be positive.");
        if (request.OccurredAt == default) throw new DispatchValidationException("OccurredAt is required.");
        var category = Text(request.Category, 64) ?? throw new DispatchValidationException("Expense category is required.");
        var description = Text(request.Description, 300);
        var evidence = Text(request.EvidenceUrl, 1000);
        return store.RecordExpenseAsync(actor, dispatchId, request with { Category = category, Description = description, EvidenceUrl = evidence, IdempotencyKey = request.IdempotencyKey.Trim() }, ct);
    }

    public Task<DispatchExecutionDetail> ReviewExpenseAsync(DispatchActorIdentity actor, Guid dispatchId, Guid expenseId, ReviewDispatchExpenseRequest request, CancellationToken ct)
    {
        Require(actor, DispatchPermissionCodes.Settle); Required(dispatchId, "DispatchId"); Required(expenseId, "ExpenseId"); Idempotency(request.IdempotencyKey);
        if (request.Decision is not ("Approved" or "Rejected")) throw new DispatchValidationException("Expense decision is invalid.");
        if (request.Decision == "Approved" && request.ApprovedAmount is null or < 0) throw new DispatchValidationException("ApprovedAmount is required.");
        if (request.Decision == "Rejected" && request.ApprovedAmount is not null and not 0) throw new DispatchValidationException("A rejected expense cannot have an approved amount.");
        return store.ReviewExpenseAsync(actor, dispatchId, expenseId, request with { Notes = Text(request.Notes, 500), IdempotencyKey = request.IdempotencyKey.Trim() }, ct);
    }

    public Task<DispatchExecutionDetail> SettleAsync(DispatchActorIdentity actor, Guid dispatchId, SettleDispatchRequest request, CancellationToken ct)
    {
        Require(actor, DispatchPermissionCodes.Settle); Required(dispatchId, "DispatchId"); Idempotency(request.IdempotencyKey);
        if (request.CashReceived < 0) throw new DispatchValidationException("CashReceived cannot be negative.");
        if (request.WorkSessionId is null || request.WorkSessionId == Guid.Empty)
            throw new DispatchValidationException(
                "WorkSessionId is required to receive dispatch money.");
        return store.SettleAsync(actor, dispatchId, request with { Notes = Text(request.Notes, 500), IdempotencyKey = request.IdempotencyKey.Trim() }, ct);
    }

    private static void ValidatePayment(DispatchDeliveryPaymentInput value)
    {
        if (value.ApplicationType == DeliveryPaymentApplications.CreditDocument)
        {
            if (value.Amount != 0 || value.PaymentMethod is not null || string.IsNullOrWhiteSpace(value.EvidenceUrl))
                throw new DispatchValidationException("A credit delivery requires the signed invoice and cannot record money as the credit document.");
            return;
        }
        if (value.ApplicationType is not (DeliveryPaymentApplications.InvoicePayment or DeliveryPaymentApplications.CreditAdvance) || value.Amount <= 0)
            throw new DispatchValidationException("The payment application is invalid.");
        if (value.PaymentMethod is not ("Cash" or "Deposit")) throw new DispatchValidationException("Only cash and deposit are accepted during delivery.");
        if (value.Reference?.Trim().Length > 120 || value.EvidenceUrl?.Trim().Length > 1000) throw new DispatchValidationException("Payment evidence data is too long.");
    }

    private static string? Text(string? value, int max) { var text=value?.Trim(); if(string.IsNullOrEmpty(text))return null; if(text.Length>max)throw new DispatchValidationException($"Text cannot exceed {max} characters."); return text; }
    private static void Required(Guid value,string field){if(value==Guid.Empty)throw new DispatchValidationException($"{field} is required.");}
    private static void Idempotency(string value){if(string.IsNullOrWhiteSpace(value)||value.Trim().Length>128)throw new DispatchValidationException("A valid IdempotencyKey is required.");}
    private static byte[] RowVersion(string value){try{var bytes=Convert.FromBase64String(value);if(bytes.Length!=8)throw new FormatException();return bytes;}catch(FormatException){throw new DispatchValidationException("RowVersion is invalid.");}}
    private static void Require(DispatchActorIdentity actor,string permission){if(!actor.Permissions.Contains(permission))throw new DispatchForbiddenException($"Permission '{permission}' is required.");}
    private static void RequireAny(DispatchActorIdentity actor,params string[] permissions){if(!permissions.Any(actor.Permissions.Contains))throw new DispatchForbiddenException("A dispatch delivery permission is required.");}
}
