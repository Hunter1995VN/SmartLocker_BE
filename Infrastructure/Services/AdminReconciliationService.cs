namespace Infrastructure.Services;

using Application.DTOs.Admin;
using Application.DTOs.Common;
using Application.Interfaces;
using Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

public class AdminReconciliationService : IAdminReconciliationService
{
    private readonly ISmartLockerDbContext _db;
    private readonly IPaymentService _paymentService;
    private readonly IPaymentGatewayService _paymentGateway;
    private readonly IAuditLogService _auditLog;
    private readonly ILogger<AdminReconciliationService> _logger;

    public AdminReconciliationService(
        ISmartLockerDbContext db,
        IPaymentService paymentService,
        IPaymentGatewayService paymentGateway,
        IAuditLogService auditLog,
        ILogger<AdminReconciliationService> logger)
    {
        _db = db;
        _paymentService = paymentService;
        _paymentGateway = paymentGateway;
        _auditLog = auditLog;
        _logger = logger;
    }

    public async Task<ApiResponse<ReconciliationDashboardDto>> GetReconciliationDashboardAsync(
        string? search = null,
        string? status = null,
        string? kind = null,
        DateTime? fromDate = null,
        DateTime? toDate = null)
    {
        var query = _db.Payments.AsNoTracking()
            .Include(p => p.Booking)
                .ThenInclude(b => b.User)
            .Include(p => p.Booking)
                .ThenInclude(b => b.Station)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var kw = search.Trim();
            query = query.Where(p =>
                p.OrderCode.Contains(kw) ||
                p.Booking.BookingCode.Contains(kw) ||
                (p.Booking.User != null && (p.Booking.User.FullName.Contains(kw) || p.Booking.User.Email.Contains(kw))));
        }

        if (!string.IsNullOrWhiteSpace(status) && status != "ALL" && Enum.TryParse<PaymentStatus>(status, true, out var statusEnum))
        {
            query = query.Where(p => p.Status == statusEnum);
        }

        if (!string.IsNullOrWhiteSpace(kind) && kind != "ALL" && Enum.TryParse<PaymentKind>(kind, true, out var kindEnum))
        {
            query = query.Where(p => p.Kind == kindEnum);
        }

        if (fromDate.HasValue)
        {
            query = query.Where(p => p.CreatedAt >= fromDate.Value);
        }

        if (toDate.HasValue)
        {
            var endOfDay = toDate.Value.Date.AddDays(1);
            query = query.Where(p => p.CreatedAt < endOfDay);
        }

        var payments = await query
            .OrderByDescending(p => p.CreatedAt)
            .Take(150)
            .ToListAsync();

        var lastReconcileLog = await _db.AuditLogs.AsNoTracking()
            .Where(a => a.Action == "FINANCIAL_RECONCILIATION_PAYOS")
            .OrderByDescending(a => a.CreatedAt)
            .Select(a => (DateTime?)a.CreatedAt)
            .FirstOrDefaultAsync();

        var items = payments.Select(MapToReconciliationItem).ToList();

        var grossRevenue = items
            .Where(i => i.DbStatus == "PAID" && i.Kind != "REFUND")
            .Sum(i => i.Amount);

        var refundedAmount = items
            .Where(i => i.Kind == "REFUND" || i.DbStatus == "REFUNDED")
            .Sum(i => i.Amount);

        var pendingAmount = items
            .Where(i => i.DbStatus == "PENDING")
            .Sum(i => i.Amount);

        var dashboard = new ReconciliationDashboardDto
        {
            TotalTransactions = items.Count,
            TotalGrossRevenue = grossRevenue,
            TotalRefundedAmount = refundedAmount,
            TotalNetRevenue = Math.Max(0, grossRevenue - refundedAmount),
            TotalPendingAmount = pendingAmount,
            MatchedCount = items.Count(i => i.ReconciliationState == "MATCHED_PAID"),
            PendingSyncCount = items.Count(i => i.ReconciliationState == "PENDING_GATEWAY"),
            DiscrepancyCount = items.Count(i => i.ReconciliationState == "DISCREPANCY"),
            RefundedCount = items.Count(i => i.ReconciliationState == "REFUNDED"),
            LastReconciledAt = lastReconcileLog,
            Items = items
        };

        return ApiResponse<ReconciliationDashboardDto>.SuccessResponse(dashboard);
    }

    /// <summary>
    /// UC-A12: Đối soát tài chính định kỳ giữa cổng PayOS và cơ sở dữ liệu hệ thống
    /// Quét các giao dịch PENDING / nghi vấn, truy vấn trạng thái thực trên cổng PayOS và đồng bộ tự động
    /// </summary>
    public async Task<ApiResponse<BatchReconciliationResultDto>> RunBatchReconciliationAsync(
        Guid actorId,
        string? actorEmail,
        string? actorRole)
    {
        var pendingPayments = await _db.Payments
            .Include(p => p.Booking)
            .Where(p => p.Status == PaymentStatus.PENDING && p.Kind != PaymentKind.REFUND)
            .OrderByDescending(p => p.CreatedAt)
            .Take(25)
            .ToListAsync();

        int checkedCount = 0;
        int newlyPaidCount = 0;
        int expiredOrCancelledCount = 0;
        int stillPendingCount = 0;
        decimal newlyReconciledAmount = 0;
        var syncedOrders = new List<string>();
        var now = DateTime.UtcNow;

        foreach (var payment in pendingPayments)
        {
            checkedCount++;
            if (!long.TryParse(payment.OrderCode, out var numericOrderCode))
            {
                if (payment.ExpiresAt.HasValue && payment.ExpiresAt.Value < now)
                {
                    payment.Status = PaymentStatus.EXPIRED;
                    expiredOrCancelledCount++;
                }
                else
                {
                    stillPendingCount++;
                }
                continue;
            }

            try
            {
                var syncResult = await _paymentService.SyncPaymentStatusAsync(numericOrderCode);
                if (syncResult.Success && syncResult.Data != null && syncResult.Data.Status == "PAID")
                {
                    newlyPaidCount++;
                    newlyReconciledAmount += payment.Amount;
                    syncedOrders.Add(payment.OrderCode);
                    continue;
                }

                // Kiểm tra xem trên PayOS đã CANCELLED / EXPIRED hay đã quá hạn lâu chưa
                var gatewayStatus = await _paymentGateway.GetPaymentStatusAsync(numericOrderCode);
                if (string.Equals(gatewayStatus, "CANCELLED", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(gatewayStatus, "EXPIRED", StringComparison.OrdinalIgnoreCase) ||
                    (payment.ExpiresAt.HasValue && payment.ExpiresAt.Value < now.AddMinutes(-15)))
                {
                    payment.Status = PaymentStatus.EXPIRED;
                    payment.UpdatedAt = now;
                    expiredOrCancelledCount++;
                }
                else
                {
                    stillPendingCount++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "⚠️ Lỗi khi đối soát PayOS cho OrderCode {OrderCode}", payment.OrderCode);
                stillPendingCount++;
            }
        }

        if (expiredOrCancelledCount > 0)
        {
            await _db.SaveChangesAsync();
        }

        var summaryMsg = $"Đã đối soát {checkedCount} giao dịch với cổng PayOS: Đồng bộ thành công {newlyPaidCount} đơn PAID (+{newlyReconciledAmount:N0}đ), đóng {expiredOrCancelledCount} đơn hết hạn, còn {stillPendingCount} đơn đang chờ.";

        await _auditLog.LogAsync(
            actorId,
            actorEmail,
            actorRole ?? "ADMIN",
            "FINANCIAL_RECONCILIATION_PAYOS",
            "Payment",
            $"Checked:{checkedCount}|Paid:{newlyPaidCount}",
            summaryMsg
        );

        var result = new BatchReconciliationResultDto
        {
            CheckedOrderCount = checkedCount,
            NewlySyncedPaidCount = newlyPaidCount,
            MarkedExpiredOrCancelledCount = expiredOrCancelledCount,
            StillPendingCount = stillPendingCount,
            NewlyReconciledAmount = newlyReconciledAmount,
            ReconciledAt = now,
            SyncedOrderCodes = syncedOrders,
            SummaryMessage = summaryMsg
        };

        return ApiResponse<BatchReconciliationResultDto>.SuccessResponse(result, summaryMsg);
    }

    public async Task<ApiResponse<PaymentReconciliationItemDto>> ReconcileSingleOrderAsync(
        long orderCode,
        Guid actorId,
        string? actorEmail,
        string? actorRole)
    {
        var orderCodeStr = orderCode.ToString();
        var syncRes = await _paymentService.SyncPaymentStatusAsync(orderCode);

        var payment = await _db.Payments.AsNoTracking()
            .Include(p => p.Booking)
                .ThenInclude(b => b.User)
            .Include(p => p.Booking)
                .ThenInclude(b => b.Station)
            .FirstOrDefaultAsync(p => p.OrderCode == orderCodeStr);

        if (payment == null)
            return ApiResponse<PaymentReconciliationItemDto>.ErrorResponse($"Không tìm thấy giao dịch với mã OrderCode {orderCode}");

        await _auditLog.LogAsync(
            actorId,
            actorEmail,
            actorRole ?? "ADMIN",
            "SINGLE_ORDER_RECONCILIATION",
            "Payment",
            orderCodeStr,
            $"Đối soát thủ công mã đơn PayOS {orderCodeStr}: Trạng thái DB hiện tại = {payment.Status}"
        );

        var dto = MapToReconciliationItem(payment);
        return ApiResponse<PaymentReconciliationItemDto>.SuccessResponse(
            dto,
            syncRes.Message ?? $"Đã đồng bộ trạng thái đơn {orderCodeStr} với cổng PayOS ({dto.DbStatus})");
    }

    private static PaymentReconciliationItemDto MapToReconciliationItem(Domain.Entities.Payment p)
    {
        string reconState;
        if (p.Kind == PaymentKind.REFUND || p.Status == PaymentStatus.REFUNDED)
        {
            reconState = "REFUNDED";
        }
        else if (p.Status == PaymentStatus.PAID)
        {
            // Nếu đơn PAID nhưng Booking vẫn ở PENDING_PAYMENT thì báo DISCREPANCY
            reconState = p.Booking?.Status == BookingStatus.PENDING_PAYMENT ? "DISCREPANCY" : "MATCHED_PAID";
        }
        else if (p.Status == PaymentStatus.PENDING)
        {
            reconState = "PENDING_GATEWAY";
        }
        else
        {
            reconState = "EXPIRED";
        }

        return new PaymentReconciliationItemDto
        {
            PaymentId = p.Id,
            OrderCode = p.OrderCode,
            BookingId = p.BookingId,
            BookingCode = p.Booking?.BookingCode ?? "N/A",
            BookingStatus = p.Booking?.Status.ToString() ?? "UNKNOWN",
            CustomerName = p.Booking?.User?.FullName ?? "Khách vãng lai",
            CustomerEmail = p.Booking?.User?.Email ?? "",
            StationName = p.Booking?.Station?.Name ?? "Trạm SmartLocker",
            Kind = p.Kind.ToString(),
            Amount = p.Amount,
            DbStatus = p.Status.ToString(),
            Gateway = string.IsNullOrWhiteSpace(p.Gateway) ? "PAYOS" : p.Gateway,
            GatewayTxnId = p.GatewayTxnId,
            PaidAt = p.PaidAt,
            WebhookReceivedAt = p.WebhookReceivedAt,
            ExpiresAt = p.ExpiresAt,
            CreatedAt = p.CreatedAt,
            ReconciliationState = reconState
        };
    }
}
