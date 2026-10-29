namespace Application.DTOs.Promotion;

/// <summary>
/// Cấu hình & trạng thái Ngân sách Chiết khấu Tổng toàn hệ thống (Global Discount Budget Guardrail).
/// </summary>
public class GlobalPromotionBudgetDto
{
    public decimal MonthlyBudgetCap { get; set; } = 25000000m;
    public int WarningThresholdPercent { get; set; } = 80;
    public int MaxDiscountPercentPerOrder { get; set; } = 70;
    public decimal MinPayableAmountAfterDiscount { get; set; } = 2000m;
    public bool AutoPauseOnBudgetExhausted { get; set; } = true;

    public decimal MonthlyBudgetSpent { get; set; }
    public decimal MonthlyBudgetRemaining { get; set; }
    public decimal UtilizationPercent { get; set; }
    public string BudgetAlertStatus { get; set; } = "HEALTHY"; // HEALTHY | WARNING | CRITICAL | EXHAUSTED
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class UpdateGlobalPromotionBudgetRequest
{
    public decimal MonthlyBudgetCap { get; set; }
    public int WarningThresholdPercent { get; set; } = 80;
    public int MaxDiscountPercentPerOrder { get; set; } = 70;
    public decimal MinPayableAmountAfterDiscount { get; set; } = 2000m;
    public bool AutoPauseOnBudgetExhausted { get; set; } = true;
    public string? Reason { get; set; }
}

/// <summary>
/// Chiến dịch khuyến mại gom nhóm các mã Voucher và phân bổ hạn mức ngân sách chiết khấu riêng.
/// </summary>
public class PromotionCampaignDto
{
    public string Id { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = "SEASONAL"; // SEASONAL | NEW_USER | PARTNER | LONG_STAY | FLASH_SALE
    public decimal AllocatedBudget { get; set; }
    public decimal SpentBudget { get; set; }
    public decimal RemainingBudget { get; set; }
    public decimal UtilizationPercent { get; set; }
    public int VoucherCount { get; set; }
    public int ActiveVoucherCount { get; set; }
    public int TotalRedemptions { get; set; }
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public bool IsPaused { get; set; }
    public bool AutoPauseWhenDepleted { get; set; } = true;
    public string Status { get; set; } = "ACTIVE"; // ACTIVE | PAUSED | SCHEDULED | ENDED | BUDGET_EXHAUSTED
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class UpsertPromotionCampaignRequest
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = "SEASONAL";
    public decimal AllocatedBudget { get; set; }
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public bool IsPaused { get; set; }
    public bool AutoPauseWhenDepleted { get; set; } = true;
    public string? Reason { get; set; }
}

public class UpdateCampaignStatusRequest
{
    public bool IsPaused { get; set; }
    public decimal? TopUpBudgetAmount { get; set; }
    public string? Reason { get; set; }
}

/// <summary>
/// DTO quản trị Mã Voucher đầy đủ kèm thông tin Chiến dịch & Ngân sách Chiết khấu.
/// </summary>
public class VoucherAdminDto
{
    public Guid Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string CampaignId { get; set; } = string.Empty;
    public string CampaignName { get; set; } = string.Empty;
    public string DiscountType { get; set; } = "PERCENTAGE"; // PERCENTAGE | FIXED
    public decimal DiscountValue { get; set; }
    public decimal? MaxDiscountAmount { get; set; }
    public decimal MinOrderAmount { get; set; }
    public int UsageLimit { get; set; }
    public int UsedCount { get; set; }
    public int RemainingUses { get; set; }
    public int PerUserLimit { get; set; } = 3;
    public decimal BudgetCap { get; set; }
    public decimal BudgetSpent { get; set; }
    public decimal BudgetRemaining { get; set; }
    public decimal BudgetUtilizationPercent { get; set; }
    public List<string> ApplicableSizes { get; set; } = new() { "S", "M", "L" };
    public List<Guid> ApplicableStationIds { get; set; } = new();
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public bool IsActive { get; set; }
    public string EffectiveStatus { get; set; } = "ACTIVE"; // ACTIVE | PAUSED | SCHEDULED | EXPIRED | LIMIT_REACHED | BUDGET_EXHAUSTED | CAMPAIGN_PAUSED
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class UpsertVoucherRequest
{
    public string Code { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? CampaignId { get; set; }
    public string DiscountType { get; set; } = "PERCENTAGE";
    public decimal DiscountValue { get; set; }
    public decimal? MaxDiscountAmount { get; set; }
    public decimal MinOrderAmount { get; set; }
    public int UsageLimit { get; set; } = 200;
    public int PerUserLimit { get; set; } = 3;
    public decimal? BudgetCap { get; set; }
    public List<string>? ApplicableSizes { get; set; }
    public List<Guid>? ApplicableStationIds { get; set; }
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public bool IsActive { get; set; } = true;
    public string? Reason { get; set; }
}

public class ToggleVoucherStatusRequest
{
    public bool IsActive { get; set; }
    public string? Reason { get; set; }
}

/// <summary>
/// Yêu cầu kiểm tra / mô phỏng áp mã Voucher cho đơn đặt tủ.
/// </summary>
public class ValidateVoucherRequest
{
    public string Code { get; set; } = string.Empty;
    public decimal OrderAmount { get; set; }
    public Guid? StationId { get; set; }
    public string? Size { get; set; }
}

public class VoucherValidationResultDto
{
    public bool IsValid { get; set; }
    public Guid? VoucherId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string CampaignId { get; set; } = string.Empty;
    public string CampaignName { get; set; } = string.Empty;
    public string DiscountType { get; set; } = string.Empty;
    public decimal DiscountValue { get; set; }
    public decimal? MaxDiscountAmount { get; set; }
    public decimal OriginalAmount { get; set; }
    public decimal DiscountAmount { get; set; }
    public decimal FinalPayableAmount { get; set; }
    public decimal VoucherBudgetRemainingAfter { get; set; }
    public decimal CampaignBudgetRemainingAfter { get; set; }
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// DTO hiển thị danh sách mã ưu đãi cho khách hàng trên trang đặt tủ (StationBookingPage).
/// </summary>
public class AvailableVoucherItemDto
{
    public Guid Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string CampaignName { get; set; } = string.Empty;
    public string DiscountType { get; set; } = string.Empty;
    public decimal DiscountValue { get; set; }
    public decimal? MaxDiscountAmount { get; set; }
    public decimal MinOrderAmount { get; set; }
    public int RemainingUses { get; set; }
    public DateTime EndDate { get; set; }
    public bool IsEligibleNow { get; set; }
    public decimal EstimatedDiscount { get; set; }
    public decimal FinalPayableAmount { get; set; }
    public string? IneligibleReason { get; set; }
}

public class PromotionLedgerItemDto
{
    public long Id { get; set; }
    public string EventType { get; set; } = string.Empty; // VOUCHER_REDEEMED | VOUCHER_RELEASED | CAMPAIGN_CONFIG | BUDGET_CONFIG | VOUCHER_CONFIG
    public string VoucherCode { get; set; } = string.Empty;
    public string CampaignName { get; set; } = string.Empty;
    public string? BookingCode { get; set; }
    public string ActorEmail { get; set; } = string.Empty;
    public string ActorRole { get; set; } = string.Empty;
    public decimal OriginalAmount { get; set; }
    public decimal DiscountAmount { get; set; }
    public decimal FinalAmount { get; set; }
    public string Summary { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}

public class PromotionDashboardOverviewDto
{
    public GlobalPromotionBudgetDto GlobalBudget { get; set; } = new();
    public int TotalCampaigns { get; set; }
    public int ActiveCampaigns { get; set; }
    public int TotalVouchers { get; set; }
    public int ActiveVouchers { get; set; }
    public int TotalRedemptionsCount { get; set; }
    public decimal TotalDiscountGranted { get; set; }
    public decimal TotalGmvDriven { get; set; }
    public decimal AverageDiscountPerOrder { get; set; }
    public decimal RoiMultiple { get; set; }
    public List<PromotionCampaignDto> Campaigns { get; set; } = new();
    public List<VoucherAdminDto> Vouchers { get; set; } = new();
    public List<PromotionLedgerItemDto> RecentLedger { get; set; } = new();
}
