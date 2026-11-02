namespace Application.DTOs.Voucher;

public class VoucherDto
{
    public Guid Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string DiscountType { get; set; } = "PERCENTAGE";
    public decimal DiscountValue { get; set; }
    public decimal? MaxDiscountAmount { get; set; }
    public decimal MinOrderAmount { get; set; }
    public int UsageLimit { get; set; }
    public int UsedCount { get; set; }
    public int RemainingUses => Math.Max(0, UsageLimit - UsedCount);
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public bool IsEligible { get; set; }
    public decimal EstimatedDiscount { get; set; }
    public string? IneligibleReason { get; set; }
}

public class ValidateVoucherRequest
{
    public string Code { get; set; } = string.Empty;
    public decimal OrderAmount { get; set; }
    public Guid? StationId { get; set; }
    public string? Size { get; set; }
}

public class ValidateVoucherResponse
{
    public bool IsValid { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string DiscountType { get; set; } = "PERCENTAGE";
    public decimal DiscountValue { get; set; }
    public decimal? MaxDiscountAmount { get; set; }
    public decimal MinOrderAmount { get; set; }
    public decimal OriginalAmount { get; set; }
    public decimal DiscountAmount { get; set; }
    public decimal FinalAmount { get; set; }
    public string Message { get; set; } = string.Empty;
}

