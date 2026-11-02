namespace Application.DTOs.Admin;

public class PaymentReconciliationItemDto
{
    public Guid PaymentId { get; set; }
    public string OrderCode { get; set; } = string.Empty;
    public Guid BookingId { get; set; }
    public string BookingCode { get; set; } = string.Empty;
    public string BookingStatus { get; set; } = string.Empty;
    public string CustomerName { get; set; } = string.Empty;
    public string CustomerEmail { get; set; } = string.Empty;
    public string StationName { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty; // BASE, EXTENSION, OVERDUE, REFUND
    public decimal Amount { get; set; }
    public string DbStatus { get; set; } = string.Empty; // PENDING, PAID, FAILED, EXPIRED, REFUNDED
    public string Gateway { get; set; } = "PAYOS";
    public string? GatewayTxnId { get; set; }
    public DateTime? PaidAt { get; set; }
    public DateTime? WebhookReceivedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public string ReconciliationState { get; set; } = string.Empty; // MATCHED_PAID, PENDING_GATEWAY, DISCREPANCY, REFUNDED, EXPIRED
}

public class ReconciliationDashboardDto
{
    public int TotalTransactions { get; set; }
    public decimal TotalGrossRevenue { get; set; }
    public decimal TotalRefundedAmount { get; set; }
    public decimal TotalNetRevenue { get; set; }
    public decimal TotalPendingAmount { get; set; }
    public int MatchedCount { get; set; }
    public int PendingSyncCount { get; set; }
    public int DiscrepancyCount { get; set; }
    public int RefundedCount { get; set; }
    public DateTime? LastReconciledAt { get; set; }
    public List<PaymentReconciliationItemDto> Items { get; set; } = new();
}

public class BatchReconciliationResultDto
{
    public int CheckedOrderCount { get; set; }
    public int NewlySyncedPaidCount { get; set; }
    public int MarkedExpiredOrCancelledCount { get; set; }
    public int StillPendingCount { get; set; }
    public decimal NewlyReconciledAmount { get; set; }
    public DateTime ReconciledAt { get; set; }
    public List<string> SyncedOrderCodes { get; set; } = new();
    public string SummaryMessage { get; set; } = string.Empty;
}
