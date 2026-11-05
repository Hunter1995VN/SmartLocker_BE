namespace Domain.Entities;

using System.ComponentModel.DataAnnotations.Schema;

/// <summary>
/// Thực thể Mã giảm giá / Voucher ánh xạ trực tiếp tới bảng Vouchers trong SQL Server.
/// </summary>
public class Voucher
{
    public Guid Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    /// <summary>
    /// PERCENTAGE | FIXED
    /// </summary>
    public string DiscountType { get; set; } = "PERCENTAGE";
    public decimal DiscountValue { get; set; }
    public decimal? MaxDiscountAmount { get; set; }
    public decimal MinOrderAmount { get; set; }
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    [NotMapped] public DateTime ValidFrom { get => StartDate; set => StartDate = value; }
    [NotMapped] public DateTime ValidTo { get => EndDate; set => EndDate = value; }
    public int UsageLimit { get; set; }
    public int UsedCount { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
