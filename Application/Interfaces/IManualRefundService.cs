namespace Application.Interfaces;

using Application.DTOs.Common;
using Application.DTOs.Refund;

public interface IManualRefundService
{
    Task<ApiResponse<ManualRefundOverviewDto>> GetOverviewAsync(
        string? status = null,
        string? category = null,
        string? search = null);

    Task<ApiResponse<List<RefundablePaymentCandidateDto>>> GetRefundableCandidatesAsync(
        string? search = null);

    Task<ApiResponse<ManualRefundItemDto>> CreateRequestAsync(
        CreateManualRefundRequest request,
        Guid actorUserId);

    Task<ApiResponse<ManualRefundItemDto>> ApproveRequestAsync(
        string refundId,
        ApproveManualRefundRequest request,
        Guid adminUserId);

    Task<ApiResponse<ManualRefundItemDto>> RejectRequestAsync(
        string refundId,
        RejectManualRefundRequest request,
        Guid adminUserId);

    Task<ApiResponse<ManualRefundItemDto>> ConfirmDisbursementAsync(
        string refundId,
        ConfirmManualRefundDisbursementRequest request,
        Guid adminUserId);
}
