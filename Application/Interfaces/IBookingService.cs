namespace Application.Interfaces;

using Application.DTOs.Booking;
using Application.DTOs.Common;

public interface IBookingService
{
    Task<ApiResponse<CreateBookingResponse>> CreateBookingAsync(Guid userId, CreateBookingRequest request);
    Task<ApiResponse<BookingDto>> GetBookingByIdAsync(Guid userId, Guid bookingId);
    Task<ApiResponse<PagedResult<BookingListItemDto>>> GetUserBookingsAsync(Guid userId, string? status, int page = 1, int pageSize = 20);
    Task<ApiResponse<CreateBookingResponse>> ExtendBookingAsync(Guid userId, Guid bookingId, ExtendBookingRequest request);
    Task<ApiResponse<BookingDto>> CancelBookingAsync(Guid userId, Guid bookingId, CancelBookingRequest request);
    Task<ApiResponse<CreateBookingResponse>> PayOverdueFeeAsync(Guid userId, Guid bookingId);
}
