namespace Application.DTOs.Refund;

/// <summary>
/// DTOs cho chức năng Phê duyệt Lệnh Hoàn tiền Thủ công cho các trường hợp ngoại lệ
/// (Lỗi phần cứng/kẹt chốt IoT, mất điện trạm, thu trùng/lệch PayOS, khiếu nại CSKH đặc biệt).
/// </summary>
public class ManualRefundOverviewDto
{
    public int TotalRequests { get; set; }
    public int PendingApprovalCount { get; set; }
    public decimal PendingApprovalAmount { get; set; }
    public int ApprovedPendingPayoutCount { get; set; }
    public decimal ApprovedPendingPayoutAmount { get; set; }
    public int DisbursedCount { get; set; }
    public decimal TotalDisbursedAmount { get; set; }
    public int RejectedCount { get; set; }
    public decimal AverageRefundAmount { get; set; }
    public List<RefundCategoryStatDto> CategoryBreakdown { get; set; } = new();
    public List<ManualRefundItemDto> Items { get; set; } = new();
}

public class RefundCategoryStatDto
{
    public string Category { get; set; } = string.Empty;
    public string CategoryLabel { get; set; } = string.Empty;
    public int Count { get; set; }
    public decimal TotalAmount { get; set; }
}

public class ManualRefundItemDto
{
    public string Id { get; set; } = string.Empty;
    public string RefundCode { get; set; } = string.Empty;
    public Guid BookingId { get; set; }
    public string BookingCode { get; set; } = string.Empty;
    public string BookingStatus { get; set; } = string.Empty;
    public Guid PaymentId { get; set; }
    public string OriginalOrderCode { get; set; } = string.Empty;
    public string OriginalPaymentKind { get; set; } = string.Empty;
    public string StationName { get; set; } = string.Empty;
    public string? LockerCode { get; set; }
    public Guid CustomerId { get; set; }
    public string CustomerName { get; set; } = string.Empty;
    public string CustomerEmail { get; set; } = string.Empty;
    public string CustomerPhone { get; set; } = string.Empty;

    public string ExceptionCategory { get; set; } = string.Empty;
    public string ReasonSummary { get; set; } = string.Empty;
    public string? EvidenceNote { get; set; }
    public string? RelatedIncidentCode { get; set; }

    public decimal OriginalPaidAmount { get; set; }
    public decimal PreviouslyRefundedAmount { get; set; }
    public decimal RequestedAmount { get; set; }
    public decimal? ApprovedAmount { get; set; }
    public decimal RefundPercentOfPaid { get; set; }

    public string RefundMethod { get; set; } = "BANK_NAPAS247";
    public string? BankName { get; set; }
    public string? BankAccountNumber { get; set; }
    public string? BankAccountHolder { get; set; }
    public string? GatewayTransferRef { get; set; }
    public Guid? RefundPaymentId { get; set; }

    public bool CancelBookingAndReleaseLocker { get; set; }
    public bool WaiveRelatedOverdueDebt { get; set; }

    /// <summary>
    /// PENDING_APPROVAL | APPROVED_PENDING_PAYOUT | DISBURSED | REJECTED
    /// </summary>
    public string Status { get; set; } = "PENDING_APPROVAL";

    public Guid RequestedByUserId { get; set; }
    public string RequestedByName { get; set; } = string.Empty;
    public string RequestedByEmail { get; set; } = string.Empty;
    public string RequestedByRole { get; set; } = string.Empty;
    public DateTime RequestedAt { get; set; }

    public Guid? ReviewedByUserId { get; set; }
    public string? ReviewedByName { get; set; }
    public string? ReviewedByEmail { get; set; }
    public DateTime? ReviewedAt { get; set; }
    public string? AdminDecisionNote { get; set; }

    public DateTime? DisbursedAt { get; set; }
    public string? DisbursedByEmail { get; set; }
}

public class RefundablePaymentCandidateDto
{
    public Guid PaymentId { get; set; }
    public string OrderCode { get; set; } = string.Empty;
    public string PaymentKind { get; set; } = string.Empty;
    public decimal PaidAmount { get; set; }
    public DateTime? PaidAt { get; set; }
    public Guid BookingId { get; set; }
    public string BookingCode { get; set; } = string.Empty;
    public string BookingStatus { get; set; } = string.Empty;
    public string StationName { get; set; } = string.Empty;
    public string? LockerCode { get; set; }
    public string LockerSize { get; set; } = string.Empty;
    public Guid CustomerId { get; set; }
    public string CustomerName { get; set; } = string.Empty;
    public string CustomerEmail { get; set; } = string.Empty;
    public string CustomerPhone { get; set; } = string.Empty;
    public decimal CustomerOverdueDebt { get; set; }
    public decimal TotalBookingPaidAmount { get; set; }
    public decimal AlreadyRefundedAmount { get; set; }
    public decimal PendingRefundRequestedAmount { get; set; }
    public decimal MaxRefundableRemaining { get; set; }
    public string? SuggestedIncidentCode { get; set; }
}

public class CreateManualRefundRequest
{
    public Guid PaymentId { get; set; }
    public string ExceptionCategory { get; set; } = "HARDWARE_LOCK_JAM";
    public decimal RequestedAmount { get; set; }
    public string ReasonSummary { get; set; } = string.Empty;
    public string? EvidenceNote { get; set; }
    public string? RelatedIncidentCode { get; set; }
    public string RefundMethod { get; set; } = "BANK_NAPAS247";
    public string? BankName { get; set; }
    public string? BankAccountNumber { get; set; }
    public string? BankAccountHolder { get; set; }
    public bool CancelBookingAndReleaseLocker { get; set; }
    public bool WaiveRelatedOverdueDebt { get; set; }

    /// <summary>
    /// Chỉ áp dụng khi chính ADMIN lập lệnh khẩn cấp và đã thực hiện hoàn tiền ngay
    /// </summary>
    public bool AutoApproveAndDisburseNow { get; set; }
    public string? GatewayTransferRef { get; set; }
}

public class ApproveManualRefundRequest
{
    public decimal ApprovedAmount { get; set; }
    public string AdminDecisionNote { get; set; } = string.Empty;
    public bool DisburseImmediately { get; set; } = true;
    public string? GatewayTransferRef { get; set; }
    public bool CancelBookingAndReleaseLocker { get; set; }
    public bool WaiveRelatedOverdueDebt { get; set; }
}

public class RejectManualRefundRequest
{
    public string RejectionReason { get; set; } = string.Empty;
}

public class ConfirmManualRefundDisbursementRequest
{
    public string GatewayTransferRef { get; set; } = string.Empty;
    public string? Note { get; set; }
}
