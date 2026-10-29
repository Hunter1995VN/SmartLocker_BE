namespace Application.Interfaces;

using Application.DTOs.Common;
using Application.DTOs.Promotion;

public interface IPromotionVoucherService
{
    // === Admin Operations ===
    Task<ApiResponse<PromotionDashboardOverviewDto>> GetAdminOverviewAsync();
    Task<ApiResponse<List<VoucherAdminDto>>> GetAdminVouchersAsync(string? search = null, string? campaignId = null, string? status = null);
    Task<ApiResponse<GlobalPromotionBudgetDto>> UpdateGlobalBudgetPolicyAsync(UpdateGlobalPromotionBudgetRequest request, Guid adminId);
    Task<ApiResponse<PromotionCampaignDto>> CreateCampaignAsync(UpsertPromotionCampaignRequest request, Guid adminId);
    Task<ApiResponse<PromotionCampaignDto>> UpdateCampaignAsync(string campaignId, UpsertPromotionCampaignRequest request, Guid adminId);
    Task<ApiResponse<PromotionCampaignDto>> UpdateCampaignStatusOrTopUpAsync(string campaignId, UpdateCampaignStatusRequest request, Guid adminId);
    Task<ApiResponse<VoucherAdminDto>> CreateVoucherAsync(UpsertVoucherRequest request, Guid adminId);
    Task<ApiResponse<VoucherAdminDto>> UpdateVoucherAsync(Guid voucherId, UpsertVoucherRequest request, Guid adminId);
    Task<ApiResponse<VoucherAdminDto>> ToggleVoucherStatusAsync(Guid voucherId, ToggleVoucherStatusRequest request, Guid adminId);
    Task<ApiResponse<bool>> DeleteOrDeactivateVoucherAsync(Guid voucherId, string? reason, Guid adminId);
    Task<ApiResponse<VoucherValidationResultDto>> SimulateVoucherAsync(ValidateVoucherRequest request);

    // === Traveler & Booking Checkout Operations ===
    Task<ApiResponse<List<AvailableVoucherItemDto>>> GetAvailableVouchersForBookingAsync(
        Guid? userId,
        Guid? stationId,
        string? size,
        decimal orderAmount);

    Task<ApiResponse<VoucherValidationResultDto>> ValidateVoucherForUserAsync(
        Guid? userId,
        ValidateVoucherRequest request);

    /// <summary>
    /// Thực thi áp mã giảm giá, trừ lượt & khấu trừ ngân sách an toàn bên trong transaction tạo đơn đặt tủ.
    /// </summary>
    Task<ApiResponse<VoucherValidationResultDto>> RedeemVoucherForBookingAsync(
        Guid userId,
        Guid bookingId,
        string bookingCode,
        Guid stationId,
        string size,
        decimal originalAmount,
        string voucherCode);

    /// <summary>
    /// Hoàn trả lại lượt dùng & ngân sách chiết khấu khi đơn chưa thanh toán bị hủy hoặc hết hạn thanh toán.
    /// </summary>
    Task ReleaseVoucherForBookingAsync(Guid bookingId, string reason);
}
