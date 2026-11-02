using Application.DTOs.Common;
using Application.DTOs.User;
using Application.Interfaces;
using Domain.Entities;
using Domain.Enums;
using Domain.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;

namespace Infrastructure.Services;

/// <summary>
/// Service triển khai nghiệp vụ Quản lý thông tin cá nhân và Tra cứu dư nợ quá hạn.
/// Bảo đảm tính chính xác thời gian thực của mọi khoản dư nợ và đơn quá hạn.
/// </summary>
public class UserProfileService : IUserProfileService
{
    private readonly ISmartLockerDbContext _context;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IPricingService _pricingService;
    private readonly ILogger<UserProfileService> _logger;

    public UserProfileService(
        ISmartLockerDbContext context,
        IPasswordHasher passwordHasher,
        IPricingService pricingService,
        ILogger<UserProfileService> logger)
    {
        _context = context;
        _passwordHasher = passwordHasher;
        _pricingService = pricingService;
        _logger = logger;
    }

    /// <summary>
    /// Helper: Tính toán danh sách và phụ phí các đơn đang quá hạn theo thời gian thực (Real-time).
    /// Tự động cập nhật cờ IsOverdue và OverdueSince vào database nếu phát hiện đơn đã vượt quá thời gian ân hạn.
    /// </summary>
    private async Task<(List<OverdueBookingDetailDto> OverdueList, decimal ActiveOverdueFee)> GetActiveOverdueDetailsAsync(Guid userId, CancellationToken ct)
    {
        var storedBookings = await _context.Bookings
            .Include(b => b.Station)
            .Include(b => b.Locker)
            .Include(b => b.PricingPolicy)
            .Where(b => b.UserId == userId && b.Status == BookingStatus.STORED)
            .ToListAsync(ct);

        var now = DateTime.UtcNow;
        var overdueList = new List<OverdueBookingDetailDto>();
        decimal activeBookingsOverdueFee = 0;
        bool hasChanges = false;

        foreach (var booking in storedBookings)
        {
            var policy = booking.PricingPolicy ?? await _pricingService.GetActivePolicyAsync(booking.StationId, booking.Size);
            bool isOverdue = booking.IsOverdue;
            var graceMinutes = policy?.GracePeriodMinutes ?? 15;
            var overdueLimit = booking.EndAt.AddMinutes(graceMinutes);

            // Kiểm tra theo thời gian thực nếu vượt quá mốc ân hạn
            if (now > overdueLimit)
            {
                isOverdue = true;
                if (!booking.IsOverdue || booking.OverdueSince == null)
                {
                    booking.IsOverdue = true;
                    booking.OverdueSince = overdueLimit;
                    hasChanges = true;
                }
            }

            if (isOverdue)
            {
                decimal fee = 0;
                if (policy != null)
                {
                    fee = _pricingService.CalculateOverdueFee(booking, policy);
                }
                else
                {
                    fee = 20000m; // Phí cơ sở dự phòng nếu chưa cấu hình chính sách giá
                }

                var overdueSince = booking.OverdueSince ?? overdueLimit;
                var overdueHours = Math.Max(0.1, Math.Round((now - overdueSince).TotalHours, 1));

                overdueList.Add(new OverdueBookingDetailDto
                {
                    BookingId = booking.Id,
                    BookingCode = booking.BookingCode,
                    StationId = booking.StationId,
                    StationName = booking.Station?.Name ?? "Trạm SmartLocker",
                    StationAddress = booking.Station?.Address ?? "",
                    LockerCode = booking.Locker?.LockerCode ?? "Chưa rõ",
                    LockerSize = booking.Size.ToString(),
                    StartAt = booking.StartAt,
                    EndAt = booking.EndAt,
                    OverdueSince = overdueSince,
                    OverdueHours = overdueHours,
                    OverdueFeePerHour = policy?.OverdueFeePerHour ?? 0,
                    EstimatedOverdueFee = fee,
                    Status = booking.Status.ToString()
                });

                activeBookingsOverdueFee += fee;
            }
        }

        // Lưu cập nhật trạng thái nếu có đơn vừa mới chuyển sang quá hạn
        if (hasChanges)
        {
            try
            {
                await _context.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Không thể cập nhật cờ IsOverdue cho đơn đặt tủ trong quá trình kiểm tra.");
            }
        }

        return (overdueList, activeBookingsOverdueFee);
    }

    /// <summary>
    /// Lấy thông tin cá nhân chi tiết của người dùng kèm số dư nợ chính xác nhất.
    /// </summary>
    public async Task<ApiResponse<UserProfileDto>> GetProfileAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == userId && u.DeletedAt == null, ct);
        if (user == null)
        {
            return ApiResponse<UserProfileDto>.ErrorResponse("Không tìm thấy thông tin người dùng.");
        }

        var totalBookings = await _context.Bookings.CountAsync(b => b.UserId == userId, ct);
        var activeBookings = await _context.Bookings.CountAsync(b =>
            b.UserId == userId &&
            (b.Status == BookingStatus.PENDING_PAYMENT ||
             b.Status == BookingStatus.CONFIRMED ||
             b.Status == BookingStatus.STORED), ct);

        // Tính toán dư nợ và số đơn quá hạn thời gian thực
        var (overdueList, activeOverdueFee) = await GetActiveOverdueDetailsAsync(userId, ct);
        var totalOverdueDebt = user.OverdueDebt + activeOverdueFee;
        var overdueBookingsCount = overdueList.Count;

        var profileDto = new UserProfileDto
        {
            Id = user.Id,
            FullName = user.FullName,
            Email = user.Email,
            Phone = user.Phone,
            AvatarUrl = user.AvatarUrl,
            Role = user.Role.ToString(),
            Status = user.Status.ToString(),
            OverdueDebt = totalOverdueDebt,
            HasOverdueDebt = totalOverdueDebt > 0 || overdueBookingsCount > 0,
            OverdueBookingsCount = overdueBookingsCount,
            HasPassword = !string.IsNullOrEmpty(user.PasswordHash),
            GoogleLinked = !string.IsNullOrEmpty(user.GoogleId),
            CreatedAt = user.CreatedAt,
            UpdatedAt = user.UpdatedAt,
            TotalBookings = totalBookings,
            ActiveBookings = activeBookings
        };

        return ApiResponse<UserProfileDto>.SuccessResponse(profileDto);
    }

    /// <summary>
    /// Cập nhật thông tin cá nhân (Họ tên, SĐT, Avatar).
    /// </summary>
    public async Task<ApiResponse<UserProfileDto>> UpdateProfileAsync(Guid userId, UpdateProfileRequest request, CancellationToken ct = default)
    {
        var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == userId && u.DeletedAt == null, ct);
        if (user == null)
        {
            return ApiResponse<UserProfileDto>.ErrorResponse("Không tìm thấy người dùng.");
        }

        // Validate FullName
        if (string.IsNullOrWhiteSpace(request.FullName) || request.FullName.Trim().Length < 2)
        {
            return ApiResponse<UserProfileDto>.ErrorResponse("Họ và tên không được để trống và phải có ít nhất 2 ký tự.");
        }
        if (request.FullName.Trim().Length > 100)
        {
            return ApiResponse<UserProfileDto>.ErrorResponse("Họ và tên không được vượt quá 100 ký tự.");
        }

        // Validate & Clean Phone
        if (string.IsNullOrWhiteSpace(request.Phone))
        {
            return ApiResponse<UserProfileDto>.ErrorResponse("Số điện thoại không được để trống.");
        }

        var cleanPhone = request.Phone.Trim()
            .Replace(" ", "")
            .Replace("-", "")
            .Replace(".", "");

        if (cleanPhone.StartsWith("+84"))
        {
            cleanPhone = "0" + cleanPhone.Substring(3);
        }

        if (!Regex.IsMatch(cleanPhone, @"^0\d{9,10}$"))
        {
            return ApiResponse<UserProfileDto>.ErrorResponse("Số điện thoại không hợp lệ. Vui lòng nhập số điện thoại Việt Nam 10-11 số bắt đầu bằng số 0.");
        }

        // Kiểm tra trùng SĐT với tài khoản khác
        var phoneConflict = await _context.Users.AnyAsync(u =>
            u.Id != userId &&
            u.Phone == cleanPhone &&
            u.DeletedAt == null &&
            u.Status != UserStatus.PENDING_VERIFICATION, ct);

        if (phoneConflict)
        {
            return ApiResponse<UserProfileDto>.ErrorResponse("Số điện thoại này đã được sử dụng bởi một tài khoản khác.");
        }

        // Cập nhật thông tin
        user.FullName = request.FullName.Trim();
        user.Phone = cleanPhone;
        user.AvatarUrl = string.IsNullOrWhiteSpace(request.AvatarUrl) ? null : request.AvatarUrl.Trim();
        user.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(ct);
        _logger.LogInformation("Người dùng {UserId} đã cập nhật thông tin cá nhân.", userId);

        return await GetProfileAsync(userId, ct);
    }

    /// <summary>
    /// Đổi mật khẩu tài khoản.
    /// </summary>
    public async Task<ApiResponse<bool>> ChangePasswordAsync(Guid userId, ChangePasswordRequest request, CancellationToken ct = default)
    {
        var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == userId && u.DeletedAt == null, ct);
        if (user == null)
        {
            return ApiResponse<bool>.ErrorResponse("Không tìm thấy người dùng.");
        }

        if (string.IsNullOrWhiteSpace(request.NewPassword) || request.NewPassword.Length < 6)
        {
            return ApiResponse<bool>.ErrorResponse("Mật khẩu mới phải có tối thiểu 6 ký tự.");
        }

        if (request.NewPassword != request.ConfirmPassword)
        {
            return ApiResponse<bool>.ErrorResponse("Mật khẩu xác nhận không khớp.");
        }

        // Nếu tài khoản đã có mật khẩu trước đó, yêu cầu kiểm tra mật khẩu cũ
        if (!string.IsNullOrEmpty(user.PasswordHash))
        {
            if (string.IsNullOrEmpty(request.CurrentPassword))
            {
                return ApiResponse<bool>.ErrorResponse("Vui lòng nhập mật khẩu hiện tại.");
            }

            if (!_passwordHasher.Verify(request.CurrentPassword, user.PasswordHash))
            {
                return ApiResponse<bool>.ErrorResponse("Mật khẩu hiện tại không chính xác.");
            }

            if (request.CurrentPassword == request.NewPassword)
            {
                return ApiResponse<bool>.ErrorResponse("Mật khẩu mới không được trùng với mật khẩu hiện tại.");
            }
        }

        user.PasswordHash = _passwordHasher.Hash(request.NewPassword);
        user.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(ct);
        _logger.LogInformation("Người dùng {UserId} đã đổi mật khẩu thành công.", userId);

        return ApiResponse<bool>.SuccessResponse(true, "Đổi mật khẩu thành công. Vui lòng sử dụng mật khẩu mới cho các lần đăng nhập tiếp theo.");
    }

    /// <summary>
    /// Tra cứu chi tiết dư nợ quá hạn, các đơn đang quá hạn và lịch sử thanh toán nợ quá hạn.
    /// Đảm bảo số liệu thời gian thực và đồng bộ 100% với trạng thái tủ.
    /// </summary>
    public async Task<ApiResponse<UserOverdueDebtResponse>> GetOverdueDebtAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == userId && u.DeletedAt == null, ct);
        if (user == null)
        {
            return ApiResponse<UserOverdueDebtResponse>.ErrorResponse("Không tìm thấy thông tin người dùng.");
        }

        // 1. Quét chi tiết các đơn đang lưu đồ (STORED) của user để kiểm tra quá hạn
        var (overdueList, activeBookingsOverdueFee) = await GetActiveOverdueDetailsAsync(userId, ct);

        // 2. Lấy lịch sử các khoản thanh toán phụ phí quá hạn đã trả
        var paymentHistory = await _context.Payments
            .Include(p => p.Booking)
            .Where(p => p.Booking.UserId == userId && p.Kind == PaymentKind.OVERDUE && p.Status == PaymentStatus.PAID)
            .OrderByDescending(p => p.PaidAt ?? p.CreatedAt)
            .Take(20)
            .Select(p => new OverduePaymentHistoryDto
            {
                PaymentId = p.Id,
                OrderCode = p.OrderCode,
                BookingId = p.BookingId,
                BookingCode = p.Booking.BookingCode,
                Amount = p.Amount,
                PaidAt = p.PaidAt ?? p.CreatedAt,
                Gateway = p.Gateway ?? "PAYOS",
                Status = p.Status.ToString()
            })
            .ToListAsync(ct);

        var totalOverdueDebt = user.OverdueDebt + activeBookingsOverdueFee;
        var hasOverdue = totalOverdueDebt > 0 || overdueList.Count > 0;
        var isBlocked = user.OverdueDebt > 0 || hasOverdue;

        string statusMessage = hasOverdue
            ? "Tài khoản của bạn hiện có dư nợ quá hạn. Theo quy định (BR-P08), bạn cần thanh toán toàn bộ dư nợ trước khi thực hiện đặt tủ mới."
            : "Tài khoản của bạn không có dư nợ quá hạn. Bạn có thể sử dụng tất cả dịch vụ bình thường.";

        var response = new UserOverdueDebtResponse
        {
            UserId = user.Id,
            FullName = user.FullName,
            AccountOverdueDebt = user.OverdueDebt,
            ActiveBookingsOverdueFee = activeBookingsOverdueFee,
            TotalOverdueDebt = totalOverdueDebt,
            HasOverdueDebt = hasOverdue,
            IsBookingBlocked = isBlocked,
            StatusMessage = statusMessage,
            OverdueBookings = overdueList,
            PaymentHistory = paymentHistory
        };

        return ApiResponse<UserOverdueDebtResponse>.SuccessResponse(response);
    }
}
