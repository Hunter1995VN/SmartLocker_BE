namespace Application.DTOs.Report;

/// <summary>
/// UC-A10: DTOs cho Phân tích & Xuất Báo cáo Doanh thu Tài chính và Lưu lượng Giao dịch Thanh toán.
/// </summary>
public class FinancialReportFilterRequest
{
    public DateTime? FromDate { get; set; }
    public DateTime? ToDate { get; set; }
    public Guid? StationId { get; set; }
    public string? PaymentKind { get; set; }
    /// <summary>
    /// DAY | WEEK | MONTH
    /// </summary>
    public string Granularity { get; set; } = "DAY";
}

public class FinancialReportOverviewDto
{
    public DateTime FromDate { get; set; }
    public DateTime ToDate { get; set; }
    public string Granularity { get; set; } = "DAY";
    public Guid? StationId { get; set; }
    public string StationFilterLabel { get; set; } = "Toàn hệ thống trạm SmartLocker";
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;

    // 1. Chỉ số Doanh thu Tài chính (VND)
    public decimal GrossRevenue { get; set; }
    public decimal BaseBookingRevenue { get; set; }
    public decimal ExtensionRevenue { get; set; }
    public decimal OverduePenaltyRevenue { get; set; }
    public decimal TotalRefundedAmount { get; set; }
    public decimal TotalVoucherDiscountSubsidy { get; set; }
    public decimal NetRevenue { get; set; }
    public decimal PreviousPeriodNetRevenue { get; set; }
    public decimal NetRevenueGrowthPercent { get; set; }
    public decimal AverageOrderValue { get; set; }
    public decimal OutstandingOverdueReceivable { get; set; }

    // 2. Chỉ số Lưu lượng Giao dịch Thanh toán (Payment Volume & Funnel)
    public int TotalTransactions { get; set; }
    public int PaidTransactionCount { get; set; }
    public int PendingTransactionCount { get; set; }
    public int ExpiredOrFailedTransactionCount { get; set; }
    public int RefundTransactionCount { get; set; }
    public decimal PaymentSuccessRatePercent { get; set; }
    public decimal RefundRatePercent { get; set; }
    public int AveragePaymentLatencySeconds { get; set; }
    public string PeakTrafficHourLabel { get; set; } = "—";

    // 3. Danh sách trạm phục vụ bộ lọc
    public List<ReportStationOptionDto> Stations { get; set; } = new();

    // 4. Chuỗi thời gian Doanh thu & Lưu lượng giao dịch
    public List<FinancialTimeSeriesPointDto> TimeSeries { get; set; } = new();

    // 5. Phân bố lưu lượng giao dịch theo khung giờ (00h - 23h)
    public List<HourlyPaymentTrafficDto> HourlyTraffic { get; set; } = new();

    // 6. Bóc tách theo Trạm tủ
    public List<StationFinancialBreakdownDto> StationBreakdown { get; set; } = new();

    // 7. Bóc tách theo Kích cỡ ngăn tủ (S / M / L)
    public List<LockerSizeRevenueBreakdownDto> SizeBreakdown { get; set; } = new();

    // 8. Bóc tách theo Loại giao dịch & Phương thức thanh toán
    public List<PaymentKindBreakdownDto> KindBreakdown { get; set; } = new();

    // 9. Sổ chi tiết giao dịch trong kỳ
    public List<FinancialLedgerRowDto> LedgerItems { get; set; } = new();
}

public class ReportStationOptionDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public class FinancialTimeSeriesPointDto
{
    public string PeriodKey { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public decimal GrossRevenue { get; set; }
    public decimal BaseRevenue { get; set; }
    public decimal ExtensionRevenue { get; set; }
    public decimal OverdueRevenue { get; set; }
    public decimal RefundAmount { get; set; }
    public decimal NetRevenue { get; set; }
    public int TotalTransactions { get; set; }
    public int PaidTransactions { get; set; }
    public int FailedOrExpiredTransactions { get; set; }
    public int RefundTransactions { get; set; }
}

public class HourlyPaymentTrafficDto
{
    public int Hour { get; set; }
    public string HourLabel { get; set; } = string.Empty;
    public int TotalAttemptCount { get; set; }
    public int PaidCount { get; set; }
    public decimal PaidVolumeAmount { get; set; }
}

public class StationFinancialBreakdownDto
{
    public Guid StationId { get; set; }
    public string StationName { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public int TotalBookings { get; set; }
    public int TotalTransactions { get; set; }
    public int PaidTransactions { get; set; }
    public decimal GrossRevenue { get; set; }
    public decimal RefundAmount { get; set; }
    public decimal NetRevenue { get; set; }
    public decimal RevenueSharePercent { get; set; }
    public decimal PaymentSuccessRatePercent { get; set; }
}

public class LockerSizeRevenueBreakdownDto
{
    public string Size { get; set; } = string.Empty;
    public string SizeLabel { get; set; } = string.Empty;
    public int PaidTransactions { get; set; }
    public decimal GrossRevenue { get; set; }
    public decimal RevenueSharePercent { get; set; }
    public decimal AverageTicket { get; set; }
}

public class PaymentKindBreakdownDto
{
    public string Kind { get; set; } = string.Empty;
    public string KindLabel { get; set; } = string.Empty;
    public int TransactionCount { get; set; }
    public decimal TotalAmount { get; set; }
    public decimal SharePercent { get; set; }
}

public class FinancialLedgerRowDto
{
    public Guid PaymentId { get; set; }
    public string OrderCode { get; set; } = string.Empty;
    public string BookingCode { get; set; } = string.Empty;
    public string StationName { get; set; } = string.Empty;
    public string LockerSize { get; set; } = string.Empty;
    public string CustomerName { get; set; } = string.Empty;
    public string CustomerEmail { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Gateway { get; set; } = "PAYOS";
    public string? GatewayTxnId { get; set; }
    public decimal Amount { get; set; }
    public decimal SignedNetImpact { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? SettledAt { get; set; }
}

public class ExportedReportFileDto
{
    public byte[] Content { get; set; } = Array.Empty<byte>();
    public string ContentType { get; set; } = "application/octet-stream";
    public string FileName { get; set; } = "SmartLocker_Financial_Report.xls";
}
