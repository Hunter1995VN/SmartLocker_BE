namespace Domain.Entities;

/// <summary>
/// Thực thể Mã giảm giá / Voucher ánh xạ trực tiếp tới bảng Vouchers trong SQL Server.
/// </summary>
public class Voucher
{
    public Guid Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string DiscountType { get; set; } = "PERCENTAGE"; // PERCENTAGE | FIXED
    public decimal DiscountValue { get; set; }
    public decimal? MaxDiscountAmount { get; set; }
    public decimal MinOrderAmount { get; set; }
    public int UsageLimit { get; set; } = 1000;
    public int UsedCount { get; set; }
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
