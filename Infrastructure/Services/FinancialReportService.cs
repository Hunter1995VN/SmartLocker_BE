namespace Infrastructure.Services;

using System.Globalization;
using System.Security;
using System.Text;
using System.Text.Json;
using Application.DTOs.Common;
using Application.DTOs.Report;
using Application.Interfaces;
using Domain.Entities;
using Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

/// <summary>
/// UC-A10: Phân tích chuyên sâu & Xuất Báo cáo Doanh thu Tài chính và Lưu lượng Giao dịch Thanh toán
/// Hỗ trợ xuất Excel đa sheet (.xls SpreadsheetML), CSV UTF-8 BOM và JSON đối chiếu kế toán.
/// </summary>
public class FinancialReportService : IFinancialReportService
{
    private static readonly TimeSpan VietnamOffset = TimeSpan.FromHours(7);

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly ISmartLockerDbContext _db;
    private readonly IAuditLogService _auditLog;
    private readonly ILogger<FinancialReportService> _logger;

    public FinancialReportService(
        ISmartLockerDbContext db,
        IAuditLogService auditLog,
        ILogger<FinancialReportService> logger)
    {
        _db = db;
        _auditLog = auditLog;
        _logger = logger;
    }

    private sealed class PromoRedemptionSnap
    {
        public Guid BookingId { get; set; }
        public decimal DiscountAmount { get; set; }
        public bool IsReleased { get; set; }
        public DateTime RedeemedAt { get; set; }
    }

    private static DateTime ToVnTime(DateTime utc) => utc.Add(VietnamOffset);

    public async Task<ApiResponse<FinancialReportOverviewDto>> GetFinancialReportAsync(
        FinancialReportFilterRequest filter)
    {
        var overview = await BuildOverviewInternalAsync(filter);
        return ApiResponse<FinancialReportOverviewDto>.SuccessResponse(overview);
    }

    public async Task<ExportedReportFileDto> ExportFinancialReportAsync(
        FinancialReportFilterRequest filter,
        string format,
        Guid actorId,
        string? actorEmail,
        string? actorRole)
    {
        var overview = await BuildOverviewInternalAsync(filter);
        var fmt = (format ?? "excel").Trim().ToLowerInvariant();
        var dateTag = $"{overview.FromDate:yyyyMMdd}_{overview.ToDate:yyyyMMdd}";

        byte[] bytes;
        string contentType;
        string fileName;

        if (fmt == "csv")
        {
            bytes = BuildCsvBytes(overview);
            contentType = "text/csv; charset=utf-8";
            fileName = $"SmartLocker_BaoCaoTaiChinh_{dateTag}.csv";
        }
        else if (fmt == "json")
        {
            bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(overview, _jsonOpts));
            contentType = "application/json; charset=utf-8";
            fileName = $"SmartLocker_BaoCaoTaiChinh_{dateTag}.json";
        }
        else
        {
            bytes = BuildExcelWorkbookBytes(overview, actorEmail, actorRole);
            contentType = "application/vnd.ms-excel; charset=utf-8";
            fileName = $"SmartLocker_BaoCaoTaiChinh_LuuLuong_{dateTag}.xls";
        }

        await _auditLog.LogAsync(
            actorId == Guid.Empty ? null : actorId,
            string.IsNullOrWhiteSpace(actorEmail) ? "admin@smartlocker.vn" : actorEmail,
            string.IsNullOrWhiteSpace(actorRole) ? "ADMIN" : actorRole,
            "EXPORT_FINANCIAL_REPORT",
            "FinancialReport",
            $"{fmt.ToUpperInvariant()}|{dateTag}",
            $"Xuất báo cáo doanh thu tài chính & lưu lượng thanh toán định dạng {fmt.ToUpperInvariant()} kỳ {overview.FromDate:dd/MM/yyyy} – {overview.ToDate:dd/MM/yyyy} ({overview.StationFilterLabel}): Doanh thu thuần={overview.NetRevenue:N0}đ, Tổng giao dịch={overview.TotalTransactions}.");

        return new ExportedReportFileDto
        {
            Content = bytes,
            ContentType = contentType,
            FileName = fileName
        };
    }

    private async Task<FinancialReportOverviewDto> BuildOverviewInternalAsync(
        FinancialReportFilterRequest filter)
    {
        var nowVn = ToVnTime(DateTime.UtcNow);
        var toLocalDate = (filter.ToDate ?? nowVn).Date;
        var fromLocalDate = (filter.FromDate ?? toLocalDate.AddDays(-29)).Date;
        if (fromLocalDate > toLocalDate)
        {
            (fromLocalDate, toLocalDate) = (toLocalDate, fromLocalDate);
        }

        // Quy đổi ranh giới ngày giờ địa phương VN (UTC+7) sang UTC để truy vấn SQL Server chính xác
        var utcStart = fromLocalDate.Subtract(VietnamOffset);
        var utcEndExclusive = toLocalDate.AddDays(1).Subtract(VietnamOffset);

        var spanDays = Math.Max(1, (int)(toLocalDate - fromLocalDate).TotalDays + 1);
        var prevUtcStart = utcStart.AddDays(-spanDays);
        var prevUtcEndExclusive = utcStart;

        var granularity = (filter.Granularity ?? "DAY").Trim().ToUpperInvariant();
        if (granularity != "DAY" && granularity != "WEEK" && granularity != "MONTH")
        {
            granularity = spanDays > 90 ? "MONTH" : "DAY";
        }

        var allStations = await _db.Stations.AsNoTracking()
            .OrderBy(s => s.Name)
            .ToListAsync();

        var paymentsQuery = _db.Payments.AsNoTracking()
            .Include(p => p.Booking)
                .ThenInclude(b => b.User)
            .Include(p => p.Booking)
                .ThenInclude(b => b.Station)
            .Where(p =>
                (p.PaidAt ?? p.RefundedAt ?? p.CreatedAt) >= utcStart &&
                (p.PaidAt ?? p.RefundedAt ?? p.CreatedAt) < utcEndExclusive);

        var prevPaymentsQuery = _db.Payments.AsNoTracking()
            .Include(p => p.Booking)
            .Where(p =>
                (p.PaidAt ?? p.RefundedAt ?? p.CreatedAt) >= prevUtcStart &&
                (p.PaidAt ?? p.RefundedAt ?? p.CreatedAt) < prevUtcEndExclusive);

        if (filter.StationId.HasValue && filter.StationId.Value != Guid.Empty)
        {
            var stId = filter.StationId.Value;
            paymentsQuery = paymentsQuery.Where(p => p.Booking != null && p.Booking.StationId == stId);
            prevPaymentsQuery = prevPaymentsQuery.Where(p => p.Booking != null && p.Booking.StationId == stId);
        }

        if (!string.IsNullOrWhiteSpace(filter.PaymentKind) &&
            !filter.PaymentKind.Equals("ALL", StringComparison.OrdinalIgnoreCase) &&
            Enum.TryParse<PaymentKind>(filter.PaymentKind.Trim(), true, out var parsedKind))
        {
            paymentsQuery = paymentsQuery.Where(p => p.Kind == parsedKind);
            prevPaymentsQuery = prevPaymentsQuery.Where(p => p.Kind == parsedKind);
        }

        var payments = await paymentsQuery
            .OrderByDescending(p => p.PaidAt ?? p.RefundedAt ?? p.CreatedAt)
            .ToListAsync();

        var prevPayments = await prevPaymentsQuery.ToListAsync();

        // Công nợ phải thu quá hạn hiện hành
        var overdueReceivable = await _db.Users.AsNoTracking()
            .Where(u => u.OverdueDebt > 0)
            .SumAsync(u => (decimal?)u.OverdueDebt) ?? 0m;

        // Trợ giá chiết khấu Voucher trong kỳ (từ PROMO_META_SNAPSHOT)
        decimal voucherSubsidy = 0m;
        try
        {
            var promoLogs = await _db.AuditLogs.AsNoTracking()
                .Where(a => a.Action == "PROMO_META_SNAPSHOT" && a.TargetEntity == "PromoRedemptionData")
                .OrderBy(a => a.Id)
                .ToListAsync();

            var latestByBooking = new Dictionary<Guid, PromoRedemptionSnap>();
            foreach (var l in promoLogs)
            {
                if (string.IsNullOrWhiteSpace(l.Details)) continue;
                try
                {
                    var snap = JsonSerializer.Deserialize<PromoRedemptionSnap>(l.Details, _jsonOpts);
                    if (snap != null && snap.BookingId != Guid.Empty)
                    {
                        latestByBooking[snap.BookingId] = snap;
                    }
                }
                catch
                {
                    // Ignore malformed snapshot
                }
            }

            voucherSubsidy = latestByBooking.Values
                .Where(s => !s.IsReleased && s.RedeemedAt >= utcStart && s.RedeemedAt < utcEndExclusive)
                .Sum(s => s.DiscountAmount);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Không thể tổng hợp số liệu chiết khấu Voucher cho báo cáo tài chính");
        }

        // === 1. Tính toán Doanh thu Tài chính ===
        static bool IsPaidRevenue(Payment p) =>
            p.Status == PaymentStatus.PAID && p.Kind != PaymentKind.REFUND;

        static bool IsSettledRefund(Payment p) =>
            p.Kind == PaymentKind.REFUND &&
            (p.Status == PaymentStatus.REFUNDED || p.Status == PaymentStatus.PAID);

        var paidRevenueItems = payments.Where(IsPaidRevenue).ToList();
        var refundItems = payments.Where(IsSettledRefund).ToList();

        var baseRevenue = paidRevenueItems.Where(p => p.Kind == PaymentKind.BASE).Sum(p => p.Amount);
        var extRevenue = paidRevenueItems.Where(p => p.Kind == PaymentKind.EXTENSION).Sum(p => p.Amount);
        var overdueRevenue = paidRevenueItems.Where(p => p.Kind == PaymentKind.OVERDUE).Sum(p => p.Amount);
        var grossRevenue = baseRevenue + extRevenue + overdueRevenue;
        var totalRefunded = refundItems.Sum(p => p.Amount);
        var netRevenue = Math.Max(0m, grossRevenue - totalRefunded);

        var prevGross = prevPayments.Where(IsPaidRevenue).Sum(p => p.Amount);
        var prevRefund = prevPayments.Where(IsSettledRefund).Sum(p => p.Amount);
        var prevNet = Math.Max(0m, prevGross - prevRefund);

        decimal growthPct = 0m;
        if (prevNet > 0m)
        {
            growthPct = Math.Round((netRevenue - prevNet) * 100m / prevNet, 1);
        }
        else if (netRevenue > 0m)
        {
            growthPct = 100m;
        }

        // === 2. Tính toán Lưu lượng Giao dịch Thanh toán ===
        var totalTx = payments.Count;
        var paidTxCount = paidRevenueItems.Count;
        var refundTxCount = refundItems.Count;
        var pendingTxCount = payments.Count(p => p.Status == PaymentStatus.PENDING && p.Kind != PaymentKind.REFUND);
        var failedExpiredTxCount = payments.Count(p =>
            p.Kind != PaymentKind.REFUND &&
            (p.Status == PaymentStatus.EXPIRED || p.Status == PaymentStatus.FAILED));

        var nonRefundAttempts = Math.Max(0, totalTx - refundTxCount);
        var successRatePct = nonRefundAttempts > 0
            ? Math.Round(paidTxCount * 100m / nonRefundAttempts, 1)
            : 0m;

        var refundRatePct = grossRevenue > 0m
            ? Math.Round(totalRefunded * 100m / grossRevenue, 1)
            : 0m;

        var latencySamples = paidRevenueItems
            .Where(p => p.PaidAt.HasValue && p.PaidAt.Value >= p.CreatedAt)
            .Select(p => (p.PaidAt!.Value - p.CreatedAt).TotalSeconds)
            .Where(sec => sec >= 1 && sec <= 3600)
            .ToList();

        var avgLatencySec = latencySamples.Count > 0
            ? (int)Math.Round(latencySamples.Average())
            : 0;

        // === 3. Chuỗi thời gian (TimeSeries: DAY / WEEK / MONTH) ===
        var timeSeries = BuildTimeSeries(payments, fromLocalDate, toLocalDate, granularity);

        // === 4. Phân bố lưu lượng theo 24 khung giờ trong ngày (ICT) ===
        var hourlyTraffic = Enumerable.Range(0, 24).Select(hour =>
        {
            var inHour = payments.Where(p =>
            {
                var ts = ToVnTime(p.PaidAt ?? p.RefundedAt ?? p.CreatedAt);
                return ts.Hour == hour && p.Kind != PaymentKind.REFUND;
            }).ToList();

            var paidInHour = inHour.Where(IsPaidRevenue).ToList();
            return new HourlyPaymentTrafficDto
            {
                Hour = hour,
                HourLabel = $"{hour:D2}:00",
                TotalAttemptCount = inHour.Count,
                PaidCount = paidInHour.Count,
                PaidVolumeAmount = paidInHour.Sum(x => x.Amount)
            };
        }).ToList();

        var peakHour = hourlyTraffic
            .OrderByDescending(h => h.PaidCount)
            .ThenByDescending(h => h.PaidVolumeAmount)
            .FirstOrDefault();

        var peakHourLabel = peakHour != null && peakHour.PaidCount > 0
            ? $"{peakHour.Hour:D2}:00 – {(peakHour.Hour + 1) % 24:D2}:00 ({peakHour.PaidCount} GD thành công)"
            : "Chưa phát sinh đỉnh giao dịch";

        // === 5. Bóc tách theo Trạm ===
        var stationBreakdown = allStations
            .Where(st => !filter.StationId.HasValue || filter.StationId.Value == Guid.Empty || st.Id == filter.StationId.Value)
            .Select(st =>
            {
                var stPayments = payments.Where(p => p.Booking != null && p.Booking.StationId == st.Id).ToList();
                var stPaid = stPayments.Where(IsPaidRevenue).ToList();
                var stRefunds = stPayments.Where(IsSettledRefund).ToList();
                var stGross = stPaid.Sum(x => x.Amount);
                var stRefAmt = stRefunds.Sum(x => x.Amount);
                var stNet = Math.Max(0m, stGross - stRefAmt);
                var stAttempts = stPayments.Count(x => x.Kind != PaymentKind.REFUND);

                return new StationFinancialBreakdownDto
                {
                    StationId = st.Id,
                    StationName = st.Name,
                    Address = st.Address,
                    TotalBookings = stPayments.Select(x => x.BookingId).Distinct().Count(),
                    TotalTransactions = stPayments.Count,
                    PaidTransactions = stPaid.Count,
                    GrossRevenue = stGross,
                    RefundAmount = stRefAmt,
                    NetRevenue = stNet,
                    RevenueSharePercent = netRevenue > 0m ? Math.Round(stNet * 100m / netRevenue, 1) : 0m,
                    PaymentSuccessRatePercent = stAttempts > 0 ? Math.Round(stPaid.Count * 100m / stAttempts, 1) : 0m
                };
            })
            .OrderByDescending(s => s.NetRevenue)
            .ThenByDescending(s => s.TotalTransactions)
            .ToList();

        // === 6. Bóc tách theo Kích cỡ ngăn tủ (S / M / L) ===
        var sizeMeta = new (LockerSize Size, string Label)[]
        {
            (LockerSize.S, "Ngăn Nhỏ (Size S)"),
            (LockerSize.M, "Ngăn Trung (Size M)"),
            (LockerSize.L, "Ngăn Lớn (Size L)")
        };

        var sizeBreakdown = sizeMeta.Select(sm =>
        {
            var szPaid = paidRevenueItems.Where(p => p.Booking != null && p.Booking.Size == sm.Size).ToList();
            var szGross = szPaid.Sum(p => p.Amount);
            return new LockerSizeRevenueBreakdownDto
            {
                Size = sm.Size.ToString(),
                SizeLabel = sm.Label,
                PaidTransactions = szPaid.Count,
                GrossRevenue = szGross,
                RevenueSharePercent = grossRevenue > 0m ? Math.Round(szGross * 100m / grossRevenue, 1) : 0m,
                AverageTicket = szPaid.Count > 0 ? Math.Round(szGross / szPaid.Count, 0) : 0m
            };
        }).ToList();

        // === 7. Bóc tách theo Loại giao dịch ===
        var totalFlowMagnitude = grossRevenue + totalRefunded;
        var kindBreakdown = new List<PaymentKindBreakdownDto>
        {
            new()
            {
                Kind = "BASE",
                KindLabel = "Thuê ngăn cơ bản (BASE)",
                TransactionCount = paidRevenueItems.Count(p => p.Kind == PaymentKind.BASE),
                TotalAmount = baseRevenue,
                SharePercent = totalFlowMagnitude > 0m ? Math.Round(baseRevenue * 100m / totalFlowMagnitude, 1) : 0m
            },
            new()
            {
                Kind = "EXTENSION",
                KindLabel = "Gia hạn thời gian gửi (EXTENSION)",
                TransactionCount = paidRevenueItems.Count(p => p.Kind == PaymentKind.EXTENSION),
                TotalAmount = extRevenue,
                SharePercent = totalFlowMagnitude > 0m ? Math.Round(extRevenue * 100m / totalFlowMagnitude, 1) : 0m
            },
            new()
            {
                Kind = "OVERDUE",
                KindLabel = "Thu phụ phí quá hạn (OVERDUE)",
                TransactionCount = paidRevenueItems.Count(p => p.Kind == PaymentKind.OVERDUE),
                TotalAmount = overdueRevenue,
                SharePercent = totalFlowMagnitude > 0m ? Math.Round(overdueRevenue * 100m / totalFlowMagnitude, 1) : 0m
            },
            new()
            {
                Kind = "REFUND",
                KindLabel = "Chi hoàn tiền hủy / ngoại lệ (REFUND)",
                TransactionCount = refundItems.Count,
                TotalAmount = totalRefunded,
                SharePercent = totalFlowMagnitude > 0m ? Math.Round(totalRefunded * 100m / totalFlowMagnitude, 1) : 0m
            }
        };

        // === 8. Sổ phụ chi tiết (tối đa 300 giao dịch mới nhất trong kỳ) ===
        var ledgerItems = payments.Take(300).Select(p =>
        {
            decimal netImpact = 0m;
            if (IsPaidRevenue(p)) netImpact = p.Amount;
            else if (IsSettledRefund(p)) netImpact = -p.Amount;

            return new FinancialLedgerRowDto
            {
                PaymentId = p.Id,
                OrderCode = p.OrderCode,
                BookingCode = p.Booking?.BookingCode ?? "N/A",
                StationName = p.Booking?.Station?.Name ?? "Trạm SmartLocker",
                LockerSize = p.Booking?.Size.ToString() ?? "M",
                CustomerName = p.Booking?.User?.FullName ?? "Khách hàng",
                CustomerEmail = p.Booking?.User?.Email ?? "",
                Kind = p.Kind.ToString(),
                Status = p.Status.ToString(),
                Gateway = string.IsNullOrWhiteSpace(p.Gateway) ? "PAYOS" : p.Gateway,
                GatewayTxnId = p.GatewayTxnId,
                Amount = p.Amount,
                SignedNetImpact = netImpact,
                CreatedAt = p.CreatedAt,
                SettledAt = p.PaidAt ?? p.RefundedAt
            };
        }).ToList();

        var selectedStationName = filter.StationId.HasValue && filter.StationId.Value != Guid.Empty
            ? allStations.FirstOrDefault(s => s.Id == filter.StationId.Value)?.Name ?? "Trạm đã chọn"
            : "Toàn hệ thống trạm SmartLocker";

        return new FinancialReportOverviewDto
        {
            FromDate = fromLocalDate,
            ToDate = toLocalDate,
            Granularity = granularity,
            StationId = filter.StationId,
            StationFilterLabel = selectedStationName,
            GeneratedAt = DateTime.UtcNow,

            GrossRevenue = grossRevenue,
            BaseBookingRevenue = baseRevenue,
            ExtensionRevenue = extRevenue,
            OverduePenaltyRevenue = overdueRevenue,
            TotalRefundedAmount = totalRefunded,
            TotalVoucherDiscountSubsidy = voucherSubsidy,
            NetRevenue = netRevenue,
            PreviousPeriodNetRevenue = prevNet,
            NetRevenueGrowthPercent = growthPct,
            AverageOrderValue = paidTxCount > 0 ? Math.Round(grossRevenue / paidTxCount, 0) : 0m,
            OutstandingOverdueReceivable = overdueReceivable,

            TotalTransactions = totalTx,
            PaidTransactionCount = paidTxCount,
            PendingTransactionCount = pendingTxCount,
            ExpiredOrFailedTransactionCount = failedExpiredTxCount,
            RefundTransactionCount = refundTxCount,
            PaymentSuccessRatePercent = successRatePct,
            RefundRatePercent = refundRatePct,
            AveragePaymentLatencySeconds = avgLatencySec,
            PeakTrafficHourLabel = peakHourLabel,

            Stations = allStations.Select(s => new ReportStationOptionDto { Id = s.Id, Name = s.Name }).ToList(),
            TimeSeries = timeSeries,
            HourlyTraffic = hourlyTraffic,
            StationBreakdown = stationBreakdown,
            SizeBreakdown = sizeBreakdown,
            KindBreakdown = kindBreakdown,
            LedgerItems = ledgerItems
        };
    }

    private static List<FinancialTimeSeriesPointDto> BuildTimeSeries(
        List<Payment> payments,
        DateTime fromDate,
        DateTime toDate,
        string granularity)
    {
        var buckets = new List<(string Key, string Label, DateTime Start, DateTime EndInclusive)>();

        if (granularity == "MONTH")
        {
            var cur = new DateTime(fromDate.Year, fromDate.Month, 1);
            var endMonth = new DateTime(toDate.Year, toDate.Month, 1);
            while (cur <= endMonth)
            {
                var monthEnd = cur.AddMonths(1).AddDays(-1);
                buckets.Add((cur.ToString("yyyy-MM"), cur.ToString("MM/yyyy"), cur, monthEnd));
                cur = cur.AddMonths(1);
            }
        }
        else if (granularity == "WEEK")
        {
            var cur = fromDate;
            while (cur <= toDate)
            {
                var weekEnd = cur.AddDays(6);
                if (weekEnd > toDate) weekEnd = toDate;
                buckets.Add((
                    cur.ToString("yyyy-MM-dd"),
                    $"{cur:dd/MM}–{weekEnd:dd/MM}",
                    cur,
                    weekEnd));
                cur = weekEnd.AddDays(1);
            }
        }
        else
        {
            var cur = fromDate;
            while (cur <= toDate)
            {
                buckets.Add((cur.ToString("yyyy-MM-dd"), cur.ToString("dd/MM"), cur, cur));
                cur = cur.AddDays(1);
            }
        }

        return buckets.Select(b =>
        {
            var inBucket = payments.Where(p =>
            {
                var vnDate = ToVnTime(p.PaidAt ?? p.RefundedAt ?? p.CreatedAt).Date;
                return vnDate >= b.Start && vnDate <= b.EndInclusive;
            }).ToList();

            var paid = inBucket.Where(p => p.Status == PaymentStatus.PAID && p.Kind != PaymentKind.REFUND).ToList();
            var refunds = inBucket.Where(p =>
                p.Kind == PaymentKind.REFUND &&
                (p.Status == PaymentStatus.REFUNDED || p.Status == PaymentStatus.PAID)).ToList();

            var baseRev = paid.Where(p => p.Kind == PaymentKind.BASE).Sum(p => p.Amount);
            var extRev = paid.Where(p => p.Kind == PaymentKind.EXTENSION).Sum(p => p.Amount);
            var overRev = paid.Where(p => p.Kind == PaymentKind.OVERDUE).Sum(p => p.Amount);
            var gross = baseRev + extRev + overRev;
            var refAmt = refunds.Sum(p => p.Amount);

            return new FinancialTimeSeriesPointDto
            {
                PeriodKey = b.Key,
                Label = b.Label,
                GrossRevenue = gross,
                BaseRevenue = baseRev,
                ExtensionRevenue = extRev,
                OverdueRevenue = overRev,
                RefundAmount = refAmt,
                NetRevenue = Math.Max(0m, gross - refAmt),
                TotalTransactions = inBucket.Count,
                PaidTransactions = paid.Count,
                FailedOrExpiredTransactions = inBucket.Count(p =>
                    p.Kind != PaymentKind.REFUND &&
                    (p.Status == PaymentStatus.EXPIRED || p.Status == PaymentStatus.FAILED)),
                RefundTransactions = refunds.Count
            };
        }).ToList();
    }

    private static byte[] BuildCsvBytes(FinancialReportOverviewDto ov)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"\"BÁO CÁO DOANH THU TÀI CHÍNH & LƯU LƯỢNG GIAO DỊCH THANH TOÁN SMARTLOCKER\"");
        sb.AppendLine($"\"Kỳ báo cáo:\",\"{ov.FromDate:dd/MM/yyyy} - {ov.ToDate:dd/MM/yyyy}\",\"Phạm vi:\",\"{EscapeCsv(ov.StationFilterLabel)}\"");
        sb.AppendLine($"\"Tổng doanh thu gộp (VND):\",{ov.GrossRevenue},\"Tổng hoàn tiền (VND):\",{ov.TotalRefundedAmount},\"Doanh thu thuần thực nhận (VND):\",{ov.NetRevenue}");
        sb.AppendLine($"\"Tổng lưu lượng giao dịch:\",{ov.TotalTransactions},\"Giao dịch PAID:\",{ov.PaidTransactionCount},\"Tỷ lệ thanh toán thành công (%):\",{ov.PaymentSuccessRatePercent}");
        sb.AppendLine();

        sb.AppendLine("OrderCode_PayOS,MaBooking,Tram,KichCoTu,KhachHang,Email,LoaiGiaoDich,TrangThai,CongThanhToan,MaThamChieuNH,SoTien_VND,TacDongDoanhThuRong_VND,ThoiGianTao_VN,ThoiGianTatToan_VN");
        foreach (var item in ov.LedgerItems)
        {
            var createdVn = ToVnTime(item.CreatedAt).ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture);
            var settledVn = item.SettledAt.HasValue
                ? ToVnTime(item.SettledAt.Value).ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture)
                : "";

            sb.AppendLine(string.Join(",",
                EscapeCsv(item.OrderCode),
                EscapeCsv(item.BookingCode),
                $"\"{EscapeCsv(item.StationName)}\"",
                EscapeCsv(item.LockerSize),
                $"\"{EscapeCsv(item.CustomerName)}\"",
                $"\"{EscapeCsv(item.CustomerEmail)}\"",
                item.Kind,
                item.Status,
                EscapeCsv(item.Gateway),
                $"\"{EscapeCsv(item.GatewayTxnId ?? "")}\"",
                item.Amount.ToString("0", CultureInfo.InvariantCulture),
                item.SignedNetImpact.ToString("0", CultureInfo.InvariantCulture),
                $"\"{createdVn}\"",
                $"\"{settledVn}\""));
        }

        var preamble = Encoding.UTF8.GetPreamble();
        var body = Encoding.UTF8.GetBytes(sb.ToString());
        var combined = new byte[preamble.Length + body.Length];
        Buffer.BlockCopy(preamble, 0, combined, 0, preamble.Length);
        Buffer.BlockCopy(body, 0, combined, preamble.Length, body.Length);
        return combined;
    }

    private static string EscapeCsv(string? val) => (val ?? string.Empty).Replace("\"", "\"\"");

    private static string XmlEsc(string? val) => SecurityElement.Escape(val ?? string.Empty) ?? string.Empty;

    /// <summary>
    /// Sinh Workbook Excel chuẩn SpreadsheetML (.xls) gồm 4 Sheet định dạng chuyên nghiệp (mở trực tiếp bằng Microsoft Excel).
    /// </summary>
    private static byte[] BuildExcelWorkbookBytes(
        FinancialReportOverviewDto ov,
        string? actorEmail,
        string? actorRole)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.AppendLine("<?mso-application progid=\"Excel.Sheet\"?>");
        sb.AppendLine("<Workbook xmlns=\"urn:schemas-microsoft-com:office:spreadsheet\"");
        sb.AppendLine(" xmlns:o=\"urn:schemas-microsoft-com:office:office\"");
        sb.AppendLine(" xmlns:x=\"urn:schemas-microsoft-com:office:excel\"");
        sb.AppendLine(" xmlns:ss=\"urn:schemas-microsoft-com:office:spreadsheet\">");

        // Styles
        sb.AppendLine("<Styles>");
        sb.AppendLine("<Style ss:ID=\"Default\" ss:Name=\"Normal\"><Font ss:FontName=\"Calibri\" ss:Size=\"11\"/></Style>");
        sb.AppendLine("<Style ss:ID=\"Title\"><Font ss:FontName=\"Calibri\" ss:Size=\"14\" ss:Bold=\"1\" ss:Color=\"#1E3A8A\"/></Style>");
        sb.AppendLine("<Style ss:ID=\"SubTitle\"><Font ss:FontName=\"Calibri\" ss:Size=\"10\" ss:Italic=\"1\" ss:Color=\"#475569\"/></Style>");
        sb.AppendLine("<Style ss:ID=\"Header\"><Font ss:FontName=\"Calibri\" ss:Size=\"11\" ss:Bold=\"1\" ss:Color=\"#FFFFFF\"/><Interior ss:Color=\"#1D4ED8\" ss:Pattern=\"Solid\"/></Style>");
        sb.AppendLine("<Style ss:ID=\"Money\"><NumberFormat ss:Format=\"#,##0\"/></Style>");
        sb.AppendLine("<Style ss:ID=\"TotalBold\"><Font ss:FontName=\"Calibri\" ss:Size=\"11\" ss:Bold=\"1\"/><Interior ss:Color=\"#EFF6FF\" ss:Pattern=\"Solid\"/><NumberFormat ss:Format=\"#,##0\"/></Style>");
        sb.AppendLine("</Styles>");

        // Sheet 1: Tổng hợp Tài chính & Lưu lượng
        sb.AppendLine("<Worksheet ss:Name=\"1_TongHop_TaiChinh\"><Table>");
        sb.AppendLine($"<Row><Cell ss:StyleID=\"Title\"><Data ss:Type=\"String\">BÁO CÁO DOANH THU TÀI CHÍNH &amp; LƯU LƯỢNG GIAO DỊCH SMARTLOCKER</Data></Cell></Row>");
        sb.AppendLine($"<Row><Cell ss:StyleID=\"SubTitle\"><Data ss:Type=\"String\">Kỳ báo cáo: {ov.FromDate:dd/MM/yyyy} – {ov.ToDate:dd/MM/yyyy} | Phạm vi: {XmlEsc(ov.StationFilterLabel)} | Người xuất: {XmlEsc(actorEmail ?? "admin@smartlocker.vn")} ({XmlEsc(actorRole ?? "ADMIN")})</Data></Cell></Row>");
        sb.AppendLine("<Row/>");
        sb.AppendLine("<Row><Cell ss:StyleID=\"Header\"><Data ss:Type=\"String\">Chỉ tiêu Tài chính &amp; Lưu lượng Thanh toán</Data></Cell><Cell ss:StyleID=\"Header\"><Data ss:Type=\"String\">Giá trị</Data></Cell><Cell ss:StyleID=\"Header\"><Data ss:Type=\"String\">Đơn vị / Ghi chú</Data></Cell></Row>");

        void AddMetricRow(string name, decimal val, string unit, bool highlight = false)
        {
            var st = highlight ? "TotalBold" : "Money";
            sb.AppendLine($"<Row><Cell><Data ss:Type=\"String\">{XmlEsc(name)}</Data></Cell><Cell ss:StyleID=\"{st}\"><Data ss:Type=\"Number\">{val.ToString("0.##", CultureInfo.InvariantCulture)}</Data></Cell><Cell><Data ss:Type=\"String\">{XmlEsc(unit)}</Data></Cell></Row>");
        }

        AddMetricRow("1. Tổng Doanh thu Gộp thực thu (Gross Revenue)", ov.GrossRevenue, "VND (Đã khớp PAID)");
        AddMetricRow("   - Doanh thu đặt tủ cơ bản (BASE)", ov.BaseBookingRevenue, "VND");
        AddMetricRow("   - Doanh thu gia hạn lưu trữ (EXTENSION)", ov.ExtensionRevenue, "VND");
        AddMetricRow("   - Doanh thu thu phụ phí quá hạn (OVERDUE)", ov.OverduePenaltyRevenue, "VND");
        AddMetricRow("2. Tổng Hoàn tiền Hủy đơn & Ngoại lệ (Refunds)", ov.TotalRefundedAmount, "VND");
        AddMetricRow("3. Tổng Trợ giá Chiết khấu Khuyến mại (Voucher Subsidy)", ov.TotalVoucherDiscountSubsidy, "VND (Ngân sách Marketing)");
        AddMetricRow("4. DOANH THU THUẦN THỰC NHẬN (NET REVENUE)", ov.NetRevenue, "VND (Gross - Refund)", true);
        AddMetricRow("5. Tăng trưởng Doanh thu Thuần so với kỳ trước", ov.NetRevenueGrowthPercent, "%");
        AddMetricRow("6. Giá trị Giao dịch Bình quân (AOV)", ov.AverageOrderValue, "VND / giao dịch PAID");
        AddMetricRow("7. Công nợ Quá hạn Phải thu đang treo", ov.OutstandingOverdueReceivable, "VND");
        AddMetricRow("8. Tổng Lưu lượng Giao dịch phát sinh", ov.TotalTransactions, "Giao dịch");
        AddMetricRow("   - Giao dịch Thanh toán Thành công (PAID)", ov.PaidTransactionCount, "Giao dịch");
        AddMetricRow("   - Giao dịch Đang chờ thanh toán (PENDING)", ov.PendingTransactionCount, "Giao dịch");
        AddMetricRow("   - Giao dịch Hết hạn / Thất bại (EXPIRED/FAILED)", ov.ExpiredOrFailedTransactionCount, "Giao dịch");
        AddMetricRow("   - Giao dịch Chi hoàn tiền (REFUND)", ov.RefundTransactionCount, "Giao dịch");
        AddMetricRow("9. Tỷ lệ Chuyển đổi Thanh toán Thành công", ov.PaymentSuccessRatePercent, "%");
        AddMetricRow("10. Thời gian Quét QR Thanh toán Trung bình", ov.AveragePaymentLatencySeconds, "Giây");
        sb.AppendLine("</Table></Worksheet>");

        // Sheet 2: Chuỗi thời gian
        sb.AppendLine("<Worksheet ss:Name=\"2_ChuoiThoiGian\"><Table>");
        sb.AppendLine("<Row>" +
            "<Cell ss:StyleID=\"Header\"><Data ss:Type=\"String\">Kỳ thời gian</Data></Cell>" +
            "<Cell ss:StyleID=\"Header\"><Data ss:Type=\"String\">Doanh thu Gộp (VND)</Data></Cell>" +
            "<Cell ss:StyleID=\"Header\"><Data ss:Type=\"String\">Thuê cơ bản (VND)</Data></Cell>" +
            "<Cell ss:StyleID=\"Header\"><Data ss:Type=\"String\">Gia hạn (VND)</Data></Cell>" +
            "<Cell ss:StyleID=\"Header\"><Data ss:Type=\"String\">Phụ phí quá hạn (VND)</Data></Cell>" +
            "<Cell ss:StyleID=\"Header\"><Data ss:Type=\"String\">Hoàn tiền (VND)</Data></Cell>" +
            "<Cell ss:StyleID=\"Header\"><Data ss:Type=\"String\">Doanh thu Thuần (VND)</Data></Cell>" +
            "<Cell ss:StyleID=\"Header\"><Data ss:Type=\"String\">Tổng lượt GD</Data></Cell>" +
            "<Cell ss:StyleID=\"Header\"><Data ss:Type=\"String\">GD Thành công</Data></Cell>" +
            "</Row>");
        foreach (var pt in ov.TimeSeries)
        {
            sb.AppendLine("<Row>" +
                $"<Cell><Data ss:Type=\"String\">{XmlEsc(pt.Label)}</Data></Cell>" +
                $"<Cell ss:StyleID=\"Money\"><Data ss:Type=\"Number\">{pt.GrossRevenue.ToString("0", CultureInfo.InvariantCulture)}</Data></Cell>" +
                $"<Cell ss:StyleID=\"Money\"><Data ss:Type=\"Number\">{pt.BaseRevenue.ToString("0", CultureInfo.InvariantCulture)}</Data></Cell>" +
                $"<Cell ss:StyleID=\"Money\"><Data ss:Type=\"Number\">{pt.ExtensionRevenue.ToString("0", CultureInfo.InvariantCulture)}</Data></Cell>" +
                $"<Cell ss:StyleID=\"Money\"><Data ss:Type=\"Number\">{pt.OverdueRevenue.ToString("0", CultureInfo.InvariantCulture)}</Data></Cell>" +
                $"<Cell ss:StyleID=\"Money\"><Data ss:Type=\"Number\">{pt.RefundAmount.ToString("0", CultureInfo.InvariantCulture)}</Data></Cell>" +
                $"<Cell ss:StyleID=\"TotalBold\"><Data ss:Type=\"Number\">{pt.NetRevenue.ToString("0", CultureInfo.InvariantCulture)}</Data></Cell>" +
                $"<Cell><Data ss:Type=\"Number\">{pt.TotalTransactions}</Data></Cell>" +
                $"<Cell><Data ss:Type=\"Number\">{pt.PaidTransactions}</Data></Cell>" +
                "</Row>");
        }
        sb.AppendLine("</Table></Worksheet>");

        // Sheet 3: Hiệu quả theo Trạm
        sb.AppendLine("<Worksheet ss:Name=\"3_DoanhThu_TheoTram\"><Table>");
        sb.AppendLine("<Row>" +
            "<Cell ss:StyleID=\"Header\"><Data ss:Type=\"String\">Tên Trạm SmartLocker</Data></Cell>" +
            "<Cell ss:StyleID=\"Header\"><Data ss:Type=\"String\">Địa chỉ</Data></Cell>" +
            "<Cell ss:StyleID=\"Header\"><Data ss:Type=\"String\">Số đơn Booking</Data></Cell>" +
            "<Cell ss:StyleID=\"Header\"><Data ss:Type=\"String\">Tổng GD</Data></Cell>" +
            "<Cell ss:StyleID=\"Header\"><Data ss:Type=\"String\">GD PAID</Data></Cell>" +
            "<Cell ss:StyleID=\"Header\"><Data ss:Type=\"String\">Doanh thu Gộp (VND)</Data></Cell>" +
            "<Cell ss:StyleID=\"Header\"><Data ss:Type=\"String\">Hoàn tiền (VND)</Data></Cell>" +
            "<Cell ss:StyleID=\"Header\"><Data ss:Type=\"String\">Doanh thu Thuần (VND)</Data></Cell>" +
            "<Cell ss:StyleID=\"Header\"><Data ss:Type=\"String\">Tỷ trọng (%)</Data></Cell>" +
            "</Row>");
        foreach (var st in ov.StationBreakdown)
        {
            sb.AppendLine("<Row>" +
                $"<Cell><Data ss:Type=\"String\">{XmlEsc(st.StationName)}</Data></Cell>" +
                $"<Cell><Data ss:Type=\"String\">{XmlEsc(st.Address)}</Data></Cell>" +
                $"<Cell><Data ss:Type=\"Number\">{st.TotalBookings}</Data></Cell>" +
                $"<Cell><Data ss:Type=\"Number\">{st.TotalTransactions}</Data></Cell>" +
                $"<Cell><Data ss:Type=\"Number\">{st.PaidTransactions}</Data></Cell>" +
                $"<Cell ss:StyleID=\"Money\"><Data ss:Type=\"Number\">{st.GrossRevenue.ToString("0", CultureInfo.InvariantCulture)}</Data></Cell>" +
                $"<Cell ss:StyleID=\"Money\"><Data ss:Type=\"Number\">{st.RefundAmount.ToString("0", CultureInfo.InvariantCulture)}</Data></Cell>" +
                $"<Cell ss:StyleID=\"TotalBold\"><Data ss:Type=\"Number\">{st.NetRevenue.ToString("0", CultureInfo.InvariantCulture)}</Data></Cell>" +
                $"<Cell><Data ss:Type=\"Number\">{st.RevenueSharePercent.ToString("0.0", CultureInfo.InvariantCulture)}</Data></Cell>" +
                "</Row>");
        }
        sb.AppendLine("</Table></Worksheet>");

        // Sheet 4: Sổ chi tiết giao dịch
        sb.AppendLine("<Worksheet ss:Name=\"4_SoChiTiet_GiaoDich\"><Table>");
        sb.AppendLine("<Row>" +
            "<Cell ss:StyleID=\"Header\"><Data ss:Type=\"String\">OrderCode PayOS</Data></Cell>" +
            "<Cell ss:StyleID=\"Header\"><Data ss:Type=\"String\">Mã Booking</Data></Cell>" +
            "<Cell ss:StyleID=\"Header\"><Data ss:Type=\"String\">Trạm</Data></Cell>" +
            "<Cell ss:StyleID=\"Header\"><Data ss:Type=\"String\">Size</Data></Cell>" +
            "<Cell ss:StyleID=\"Header\"><Data ss:Type=\"String\">Khách hàng</Data></Cell>" +
            "<Cell ss:StyleID=\"Header\"><Data ss:Type=\"String\">Loại giao dịch</Data></Cell>" +
            "<Cell ss:StyleID=\"Header\"><Data ss:Type=\"String\">Trạng thái</Data></Cell>" +
            "<Cell ss:StyleID=\"Header\"><Data ss:Type=\"String\">Cổng TT</Data></Cell>" +
            "<Cell ss:StyleID=\"Header\"><Data ss:Type=\"String\">Số tiền (VND)</Data></Cell>" +
            "<Cell ss:StyleID=\"Header\"><Data ss:Type=\"String\">Tác động ròng (VND)</Data></Cell>" +
            "<Cell ss:StyleID=\"Header\"><Data ss:Type=\"String\">Thời gian (VN)</Data></Cell>" +
            "</Row>");
        foreach (var row in ov.LedgerItems)
        {
            var tsVn = ToVnTime(row.SettledAt ?? row.CreatedAt).ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);
            sb.AppendLine("<Row>" +
                $"<Cell><Data ss:Type=\"String\">{XmlEsc(row.OrderCode)}</Data></Cell>" +
                $"<Cell><Data ss:Type=\"String\">{XmlEsc(row.BookingCode)}</Data></Cell>" +
                $"<Cell><Data ss:Type=\"String\">{XmlEsc(row.StationName)}</Data></Cell>" +
                $"<Cell><Data ss:Type=\"String\">{XmlEsc(row.LockerSize)}</Data></Cell>" +
                $"<Cell><Data ss:Type=\"String\">{XmlEsc(row.CustomerName)}</Data></Cell>" +
                $"<Cell><Data ss:Type=\"String\">{XmlEsc(row.Kind)}</Data></Cell>" +
                $"<Cell><Data ss:Type=\"String\">{XmlEsc(row.Status)}</Data></Cell>" +
                $"<Cell><Data ss:Type=\"String\">{XmlEsc(row.Gateway)}</Data></Cell>" +
                $"<Cell ss:StyleID=\"Money\"><Data ss:Type=\"Number\">{row.Amount.ToString("0", CultureInfo.InvariantCulture)}</Data></Cell>" +
                $"<Cell ss:StyleID=\"Money\"><Data ss:Type=\"Number\">{row.SignedNetImpact.ToString("0", CultureInfo.InvariantCulture)}</Data></Cell>" +
                $"<Cell><Data ss:Type=\"String\">{tsVn}</Data></Cell>" +
                "</Row>");
        }
        sb.AppendLine("</Table></Worksheet>");

        sb.AppendLine("</Workbook>");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }
}
