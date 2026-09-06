using Auraly.Contracts.Inventory;
using Auraly.BuildingBlocks.Application.Synchronization;

namespace Auraly.Application.Inventory;

public interface IInventoryQueryStore
{
    Task<InventoryProductPage> GetProductsAsync(InventoryUserIdentity user, InventoryProductQuery query, bool includeCosts, CancellationToken token);
    Task<ProductConversionProductPage> GetConversionProductsAsync(InventoryUserIdentity user, ProductConversionProductQuery query, CancellationToken token);
    Task<IReadOnlyList<InventoryWarehouseOption>> GetWarehousesAsync(InventoryUserIdentity user, CancellationToken token);
    Task<IReadOnlyList<WarehouseMasterItem>> GetWarehouseMastersAsync(InventoryUserIdentity user, CancellationToken token);
    Task<WarehouseMasterItem> SaveWarehouseAsync(InventoryUserIdentity user, Guid? warehouseId, SaveWarehouseRequest request, CancellationToken token);
    Task<IReadOnlyList<InventoryReasonItem>> GetReasonsAsync(InventoryUserIdentity user, string? operationType, bool includeInactive, string? search, CancellationToken token);
    Task<InventoryReasonItem> SaveReasonAsync(InventoryUserIdentity user, Guid? inventoryReasonId, SaveInventoryReasonRequest request, CancellationToken token);
    Task<InventoryBalancePage> GetBalancesAsync(InventoryUserIdentity user, InventoryBalanceQuery query, bool includeCosts, CancellationToken token);
    Task<InventoryMovementPage> GetMovementsAsync(InventoryUserIdentity user, InventoryMovementQuery query, bool includeCosts, CancellationToken token);
    Task<InventoryOperationPage> GetOperationsAsync(InventoryUserIdentity user, InventoryOperationQuery query, bool includeCosts, CancellationToken token);
    Task<InventoryOperationDetail?> GetOperationDetailAsync(InventoryUserIdentity user, Guid documentId, bool includeCosts, CancellationToken token);
    Task<WarehouseTransferPendingPage> GetPendingTransfersAsync(InventoryUserIdentity user, WarehouseTransferPendingQuery query, CancellationToken token);
    Task<WarehouseTransferDetail?> GetTransferAsync(InventoryUserIdentity user, Guid transferId, CancellationToken token);
}

public sealed class InventoryQueryService(
    IInventoryQueryStore store,
    IPosSynchronizationOutboxDispatcher synchronization)
{
    public Task<InventoryProductPage> GetProductsAsync(InventoryUserIdentity user, InventoryProductQuery query, CancellationToken token = default)
    {
        Validate(user, query.BusinessId, query.Page, query.PageSize);
        if (query.WarehouseId == Guid.Empty) throw new InventoryValidationException("WarehouseId is required.");
        return store.GetProductsAsync(user, query with { Search = Normalize(query.Search) }, user.Permissions.Contains(InventoryPermissionCodes.ReadCosts), token);
    }

    public Task<ProductConversionProductPage> GetConversionProductsAsync(InventoryUserIdentity user, ProductConversionProductQuery query, CancellationToken token = default)
    {
        Validate(user, query.BusinessId, query.Page, query.PageSize);
        if (query.WarehouseId == Guid.Empty) throw new InventoryValidationException("WarehouseId is required.");
        if (query.FamilyRootProductId == Guid.Empty) throw new InventoryValidationException("FamilyRootProductId is invalid.");
        return store.GetConversionProductsAsync(user, query with { Search = Normalize(query.Search) }, token);
    }

    public Task<IReadOnlyList<InventoryWarehouseOption>> GetWarehousesAsync(InventoryUserIdentity user, CancellationToken token = default)
    {
        Validate(user, user.BusinessId, 1, 1);
        return store.GetWarehousesAsync(user, token);
    }

    public Task<IReadOnlyList<WarehouseMasterItem>> GetWarehouseMastersAsync(InventoryUserIdentity user, CancellationToken token = default)
    {
        RequireWarehouseManagement(user);
        return store.GetWarehouseMastersAsync(user, token);
    }

    public async Task<WarehouseMasterItem> SaveWarehouseAsync(InventoryUserIdentity user, Guid? warehouseId, SaveWarehouseRequest request, CancellationToken token = default)
    {
        RequireWarehouseManagement(user);
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 160)
            throw new InventoryValidationException("Warehouse name is required and cannot exceed 160 characters.");
        if (request.PriceFormationCostBasis is not ("LatestReceiptCost" or "WeightedAverageCost"))
            throw new InventoryValidationException("Warehouse cost basis is invalid.");
        var saved = await store.SaveWarehouseAsync(
            user, warehouseId, request with { Name = request.Name.Trim() }, token);
        await synchronization.DispatchPendingAsync(
            user.TenantId, user.BusinessId, CancellationToken.None);
        return saved;
    }

    public Task<IReadOnlyList<InventoryReasonItem>> GetReasonsAsync(InventoryUserIdentity user, string? operationType, bool includeInactive, string? search, CancellationToken token = default)
    {
        Validate(user, user.BusinessId, 1, 1);
        var type = Normalize(operationType);
        return store.GetReasonsAsync(user, type, includeInactive, Normalize(search), token);
    }

    public Task<IReadOnlyList<InventoryReasonItem>> GetSelectableReasonsAsync(
        InventoryUserIdentity user, string reasonType, CancellationToken token = default)
    {
        if (user.BusinessId == Guid.Empty || string.IsNullOrWhiteSpace(reasonType) || reasonType.Trim().Length > 64)
            throw new InventoryValidationException("Se requiere un tipo de motivo válido.");
        return store.GetReasonsAsync(user, reasonType.Trim(), false, null, token);
    }

    public Task<InventoryReasonItem> SaveReasonAsync(InventoryUserIdentity user, Guid? inventoryReasonId, SaveInventoryReasonRequest request, CancellationToken token = default)
    {
        if (!user.Permissions.Contains(InventoryPermissionCodes.ManageReasons))
            throw new InventoryForbiddenException("No tienes permiso para administrar los motivos de inventario.");
        if (string.IsNullOrWhiteSpace(request.OperationType) || request.OperationType.Trim().Length > 64)
            throw new InventoryValidationException("Se requiere un tipo de motivo válido.");
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 120)
            throw new InventoryValidationException("El nombre del motivo es obligatorio y no puede superar 120 caracteres.");
        if (request.DisplayOrder is < 0 or > 9999)
            throw new InventoryValidationException("El orden de visualización debe estar entre 0 y 9999.");
        var category = Normalize(request.CounterpartAccountingCategory);
        if (category?.Length > 64)
            throw new InventoryValidationException("La categoría contable no puede superar 64 caracteres.");
        if ((request.OperationType.Trim() is InventoryDocumentTypes.StockCount or
                InventoryDocumentTypes.Adjustment or InventoryDocumentTypes.Damage) &&
            string.IsNullOrWhiteSpace(category))
            throw new InventoryValidationException(
                "Los conteos, ajustes de inventario y averías requieren una categoría contable de contrapartida.");
        if (StringComparer.Ordinal.Equals(category, "Inventory"))
            throw new InventoryValidationException(
                "La categoría contable de contrapartida debe ser diferente de Inventario.");
        return store.SaveReasonAsync(user, inventoryReasonId, request with
        {
            OperationType = request.OperationType.Trim(),
            Name = request.Name.Trim(),
            CounterpartAccountingCategory = category
        }, token);
    }
    public Task<InventoryBalancePage> GetBalancesAsync(InventoryUserIdentity user, InventoryBalanceQuery query, CancellationToken token = default)
    {
        Validate(user, query.BusinessId, query.Page, query.PageSize);
        return store.GetBalancesAsync(user, query with { Search = Normalize(query.Search) }, user.Permissions.Contains(InventoryPermissionCodes.ReadCosts), token);
    }

    public Task<InventoryMovementPage> GetMovementsAsync(InventoryUserIdentity user, InventoryMovementQuery query, CancellationToken token = default)
    {
        Validate(user, query.BusinessId, query.Page, query.PageSize);
        if (query.From is not null && query.To is not null && query.From > query.To) throw new InventoryValidationException("The starting date cannot be after the ending date.");
        return store.GetMovementsAsync(user, query with { Search = Normalize(query.Search), DocumentType = Normalize(query.DocumentType), MovementType = Normalize(query.MovementType) }, user.Permissions.Contains(InventoryPermissionCodes.ReadCosts), token);
    }

    public Task<InventoryOperationPage> GetOperationsAsync(InventoryUserIdentity user, InventoryOperationQuery query, CancellationToken token = default)
    {
        Validate(user, query.BusinessId, query.Page, query.PageSize);
        if (query.From is not null && query.To is not null && query.From > query.To) throw new InventoryValidationException("The starting date cannot be after the ending date.");
        return store.GetOperationsAsync(user, query with
        {
            Search = Normalize(query.Search),
            DocumentType = Normalize(query.DocumentType),
            Status = Normalize(query.Status),
            ReasonCode = Normalize(query.ReasonCode),
            PurchaseEvidenceType = Normalize(query.PurchaseEvidenceType)
        }, user.Permissions.Contains(InventoryPermissionCodes.ReadCosts), token);
    }
    public Task<InventoryOperationDetail?> GetOperationDetailAsync(
        InventoryUserIdentity user, Guid documentId, CancellationToken token = default)
    {
        Validate(user, user.BusinessId, 1, 1);
        if (documentId == Guid.Empty)
            throw new InventoryValidationException("DocumentId is required.");
        return store.GetOperationDetailAsync(
            user, documentId, user.Permissions.Contains(InventoryPermissionCodes.ReadCosts), token);
    }

    public Task<WarehouseTransferPendingPage> GetPendingTransfersAsync(InventoryUserIdentity user, WarehouseTransferPendingQuery query, CancellationToken token = default)
    {
        Validate(user, query.BusinessId, query.Page, query.PageSize);
        return store.GetPendingTransfersAsync(user, query with { Search = Normalize(query.Search) }, token);
    }

    public Task<WarehouseTransferDetail?> GetTransferAsync(InventoryUserIdentity user, Guid transferId, CancellationToken token = default)
    {
        Validate(user, user.BusinessId, 1, 1);
        if (transferId == Guid.Empty) throw new InventoryValidationException("TransferId is required.");
        return store.GetTransferAsync(user, transferId, token);
    }


    private static void RequireWarehouseManagement(InventoryUserIdentity user)
    {
        if (!user.Permissions.Contains(InventoryPermissionCodes.ManageWarehouses))
            throw new InventoryForbiddenException($"Permission '{InventoryPermissionCodes.ManageWarehouses}' is required.");
    }
    private static void Validate(InventoryUserIdentity user, Guid businessId, int page, int pageSize)
    {
        if (user.BusinessId != businessId) throw new InventoryForbiddenException("The query belongs to another business.");
        if (!user.Permissions.Contains(InventoryPermissionCodes.Read)) throw new InventoryForbiddenException($"Permission '{InventoryPermissionCodes.Read}' is required.");
        if (page < 1 || pageSize is < 1 or > 200) throw new InventoryValidationException("Page must be positive and PageSize must be between 1 and 200.");
    }

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

}
