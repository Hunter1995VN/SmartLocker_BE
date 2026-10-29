namespace Application.Interfaces;

using Application.DTOs.Admin;
using Application.DTOs.Common;

public interface IAdminReconciliationService
{
    Task<ApiResponse<ReconciliationDashboardDto>> GetReconciliationDashboardAsync(
        string? search = null,
        string? status = null,
        string? kind = null,
        DateTime? fromDate = null,
        DateTime? toDate = null);

    Task<ApiResponse<BatchReconciliationResultDto>> RunBatchReconciliationAsync(
        Guid actorId,
        string? actorEmail,
        string? actorRole);

    Task<ApiResponse<PaymentReconciliationItemDto>> ReconcileSingleOrderAsync(
        long orderCode,
        Guid actorId,
        string? actorEmail,
        string? actorRole);
}
