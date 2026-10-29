namespace Application.DTOs.User;

/// <summary>
/// DTO chứa thông tin hồ sơ người dùng đầy đủ cho use case Quản lý thông tin cá nhân.
/// </summary>
public class UserProfileDto
{
    public Guid Id { get; set; }
    public string FullName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string? AvatarUrl { get; set; }
    public string Role { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public decimal OverdueDebt { get; set; }
    public bool HasOverdueDebt { get; set; }
    public int OverdueBookingsCount { get; set; }
    public bool HasPassword { get; set; }
    public bool GoogleLinked { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public int TotalBookings { get; set; }
    public int ActiveBookings { get; set; }
}

/// <summary>
/// Yêu cầu cập nhật thông tin cá nhân (Họ tên, SĐT, Avatar).
/// </summary>
public class UpdateProfileRequest
{
    public string FullName { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string? AvatarUrl { get; set; }
}

/// <summary>
/// Yêu cầu đổi mật khẩu.
/// </summary>
public class ChangePasswordRequest
{
    public string? CurrentPassword { get; set; }
    public string NewPassword { get; set; } = string.Empty;
    public string ConfirmPassword { get; set; } = string.Empty;
}

/// <summary>
/// Chi tiết đơn đặt tủ đang quá hạn.
/// </summary>
public class OverdueBookingDetailDto
{
    public Guid BookingId { get; set; }
    public string BookingCode { get; set; } = string.Empty;
    public Guid StationId { get; set; }
    public string StationName { get; set; } = string.Empty;
    public string StationAddress { get; set; } = string.Empty;
    public string LockerCode { get; set; } = string.Empty;
    public string LockerSize { get; set; } = string.Empty;
    public DateTime StartAt { get; set; }
    public DateTime EndAt { get; set; }
    public DateTime? OverdueSince { get; set; }
    public double OverdueHours { get; set; }
    public decimal OverdueFeePerHour { get; set; }
    public decimal EstimatedOverdueFee { get; set; }
    public string Status { get; set; } = string.Empty;
}

/// <summary>
/// Lịch sử các khoản phí quá hạn đã thanh toán thành công.
/// </summary>
public class OverduePaymentHistoryDto
{
    public Guid PaymentId { get; set; }
    public string OrderCode { get; set; } = string.Empty;
    public Guid BookingId { get; set; }
    public string BookingCode { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public DateTime PaidAt { get; set; }
    public string Gateway { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
}

/// <summary>
/// Phản hồi tra cứu dư nợ quá hạn của người dùng.
/// </summary>
public class UserOverdueDebtResponse
{
    public Guid UserId { get; set; }
    public string FullName { get; set; } = string.Empty;
    public decimal AccountOverdueDebt { get; set; }
    public decimal ActiveBookingsOverdueFee { get; set; }
    public decimal TotalOverdueDebt { get; set; }
    public bool HasOverdueDebt { get; set; }
    public bool IsBookingBlocked { get; set; }
    public string StatusMessage { get; set; } = string.Empty;
    public List<OverdueBookingDetailDto> OverdueBookings { get; set; } = new();
    public List<OverduePaymentHistoryDto> PaymentHistory { get; set; } = new();
}
