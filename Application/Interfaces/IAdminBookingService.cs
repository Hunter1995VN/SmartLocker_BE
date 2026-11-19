namespace Application.Interfaces;

using Application.DTOs.Admin;
using Application.DTOs.Common;

public interface IAdminBookingService
{
    Task<ApiResponse<PagedResult<AdminBookingListItemDto>>> GetBookingsAsync(
        string? search,
        string? status,
        Guid? stationId,
        bool? isOverdue,
        DateTime? fromDate,
        DateTime? toDate,
        int page = 1,
        int pageSize = 20);

    Task<ApiResponse<AdminBookingStatsDto>> GetBookingStatsAsync(Guid? stationId);

    Task<ApiResponse<AdminBookingDetailDto>> GetBookingDetailAsync(Guid bookingId);

    Task<ApiResponse<AdminBookingDetailDto>> CancelBookingByAdminAsync(
        Guid adminUserId,
        Guid bookingId,
        string reason);

    Task<ApiResponse<AdminBookingDetailDto>> ForceCompleteBookingByAdminAsync(
        Guid adminUserId,
        Guid bookingId,
        string? note);
}
