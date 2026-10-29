namespace Infrastructure.Services;

using System.Text.Json;
using Application.DTOs.Common;
using Application.DTOs.Refund;
using Application.Interfaces;
using Domain.Entities;
using Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

/// <summary>
/// Quản lý quy trình Lập đề xuất, Thẩm định 4 mắt & Phê duyệt Lệnh Hoàn tiền Thủ công
/// cho các trường hợp ngoại lệ vận hành (lỗi kẹt khóa IoT, mất kết nối trạm, thu trùng PayOS, khiếu nại đặc biệt).
/// Tích hợp trực tiếp bút toán chi hoàn vào bảng Payments để khớp 100% với Đối soát tài chính PayOS (UC-A12).
/// </summary>
public class ManualRefundService : IManualRefundService
{
    private static readonly SemaphoreSlim _refundLock = new(1, 1);
    private const string MetaAction = "MANUAL_REFUND_META";
    private const string MetaTargetEntity = "ManualRefundOrder";

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private static readonly Dictionary<string, string> CategoryLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["HARDWARE_LOCK_JAM"] = "Sự cố phần cứng / Kẹt chốt khóa IoT",
        ["STATION_OUTAGE"] = "Mất điện / Mất kết nối trạm đột xuất",
        ["DUPLICATE_OR_OVERCHARGE"] = "Thu trùng giao dịch / Lệch phụ phí PayOS",
        ["FORCE_MAJEURE_COMPLAINT"] = "Bất khả kháng / Hỗ trợ thiện chí CSKH"
    };

    private readonly ISmartLockerDbContext _db;
    private readonly IAuditLogService _auditLog;
    private readonly IPromotionVoucherService _promotionService;
    private readonly ILogger<ManualRefundService> _logger;

    public ManualRefundService(
        ISmartLockerDbContext db,
        IAuditLogService auditLog,
        IPromotionVoucherService promotionService,
        ILogger<ManualRefundService> logger)
    {
        _db = db;
        _auditLog = auditLog;
        _promotionService = promotionService;
        _logger = logger;
    }

    private sealed class StoredManualRefundRecord
    {
        public string Id { get; set; } = string.Empty;
        public string RefundCode { get; set; } = string.Empty;
        public Guid BookingId { get; set; }
        public string BookingCode { get; set; } = string.Empty;
        public Guid PaymentId { get; set; }
        public string OriginalOrderCode { get; set; } = string.Empty;
        public string OriginalPaymentKind { get; set; } = "BASE";
        public string StationName { get; set; } = string.Empty;
        public string? LockerCode { get; set; }
        public Guid CustomerId { get; set; }
        public string CustomerName { get; set; } = string.Empty;
        public string CustomerEmail { get; set; } = string.Empty;
        public string CustomerPhone { get; set; } = string.Empty;

        public string ExceptionCategory { get; set; } = "HARDWARE_LOCK_JAM";
        public string ReasonSummary { get; set; } = string.Empty;
        public string? EvidenceNote { get; set; }
        public string? RelatedIncidentCode { get; set; }

        public decimal OriginalPaidAmount { get; set; }
        public decimal PreviouslyRefundedAmount { get; set; }
        public decimal RequestedAmount { get; set; }
        public decimal? ApprovedAmount { get; set; }

        public string RefundMethod { get; set; } = "BANK_NAPAS247";
        public string? BankName { get; set; }
        public string? BankAccountNumber { get; set; }
        public string? BankAccountHolder { get; set; }
        public string? GatewayTransferRef { get; set; }
        public Guid? RefundPaymentId { get; set; }

        public bool CancelBookingAndReleaseLocker { get; set; }
        public bool WaiveRelatedOverdueDebt { get; set; }

        public string Status { get; set; } = "PENDING_APPROVAL";

        public Guid RequestedByUserId { get; set; }
        public string RequestedByName { get; set; } = string.Empty;
        public string RequestedByEmail { get; set; } = string.Empty;
        public string RequestedByRole { get; set; } = string.Empty;
        public DateTime RequestedAt { get; set; } = DateTime.UtcNow;

        public Guid? ReviewedByUserId { get; set; }
        public string? ReviewedByName { get; set; }
        public string? ReviewedByEmail { get; set; }
        public DateTime? ReviewedAt { get; set; }
        public string? AdminDecisionNote { get; set; }

        public DateTime? DisbursedAt { get; set; }
        public string? DisbursedByEmail { get; set; }
    }

    private async Task<Dictionary<string, StoredManualRefundRecord>> LoadStoredRecordsAsync()
    {
        var logs = await _db.AuditLogs.AsNoTracking()
            .Where(a => a.Action == MetaAction && a.TargetEntity == MetaTargetEntity)
            .OrderBy(a => a.Id)
            .ToListAsync();

        var dict = new Dictionary<string, StoredManualRefundRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var log in logs)
        {
            if (string.IsNullOrWhiteSpace(log.Details)) continue;
            try
            {
                var rec = JsonSerializer.Deserialize<StoredManualRefundRecord>(log.Details, _jsonOpts);
                if (rec != null && !string.IsNullOrWhiteSpace(rec.Id))
                {
                    dict[rec.Id] = rec;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Bỏ qua bản ghi MANUAL_REFUND_META lỗi định dạng Id={LogId}", log.Id);
            }
        }

        return dict;
    }

    private async Task SaveSnapshotAsync(Guid actorId, StoredManualRefundRecord record)
    {
        var json = JsonSerializer.Serialize(record, _jsonOpts);
        _db.AuditLogs.Add(new AuditLog
        {
            UserId = actorId,
            UserEmail = "system-refund-meta@smartlocker.vn",
            UserRole = "ADMIN",
            Action = MetaAction,
            TargetEntity = MetaTargetEntity,
            TargetId = record.Id,
            Details = json,
            CreatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();
    }

    private async Task<(string Name, string Email, string Role)> GetActorProfileAsync(Guid userId)
    {
        if (userId == Guid.Empty)
            return ("Quản trị viên Hệ thống", "admin@smartlocker.vn", "ADMIN");

        var user = await _db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.FullName, u.Email, Role = u.Role.ToString() })
            .FirstOrDefaultAsync();

        return user != null
            ? (user.FullName, user.Email, user.Role)
            : ("Quản trị viên Hệ thống", "admin@smartlocker.vn", "ADMIN");
    }

    private static ManualRefundItemDto MapToDto(StoredManualRefundRecord r, string? liveBookingStatus = null)
    {
        var baseCompare = r.OriginalPaidAmount > 0 ? r.OriginalPaidAmount : 1m;
        var effectiveAmt = r.ApprovedAmount ?? r.RequestedAmount;

        return new ManualRefundItemDto
        {
            Id = r.Id,
            RefundCode = r.RefundCode,
            BookingId = r.BookingId,
            BookingCode = r.BookingCode,
            BookingStatus = liveBookingStatus ?? "CONFIRMED",
            PaymentId = r.PaymentId,
            OriginalOrderCode = r.OriginalOrderCode,
            OriginalPaymentKind = r.OriginalPaymentKind,
            StationName = r.StationName,
            LockerCode = r.LockerCode,
            CustomerId = r.CustomerId,
            CustomerName = r.CustomerName,
            CustomerEmail = r.CustomerEmail,
            CustomerPhone = r.CustomerPhone,
            ExceptionCategory = r.ExceptionCategory,
            ReasonSummary = r.ReasonSummary,
            EvidenceNote = r.EvidenceNote,
            RelatedIncidentCode = r.RelatedIncidentCode,
            OriginalPaidAmount = r.OriginalPaidAmount,
            PreviouslyRefundedAmount = r.PreviouslyRefundedAmount,
            RequestedAmount = r.RequestedAmount,
            ApprovedAmount = r.ApprovedAmount,
            RefundPercentOfPaid = Math.Round(effectiveAmt * 100m / baseCompare, 1),
            RefundMethod = r.RefundMethod,
            BankName = r.BankName,
            BankAccountNumber = r.BankAccountNumber,
            BankAccountHolder = r.BankAccountHolder,
            GatewayTransferRef = r.GatewayTransferRef,
            RefundPaymentId = r.RefundPaymentId,
            CancelBookingAndReleaseLocker = r.CancelBookingAndReleaseLocker,
            WaiveRelatedOverdueDebt = r.WaiveRelatedOverdueDebt,
            Status = r.Status,
            RequestedByUserId = r.RequestedByUserId,
            RequestedByName = r.RequestedByName,
            RequestedByEmail = r.RequestedByEmail,
            RequestedByRole = r.RequestedByRole,
            RequestedAt = r.RequestedAt,
            ReviewedByUserId = r.ReviewedByUserId,
            ReviewedByName = r.ReviewedByName,
            ReviewedByEmail = r.ReviewedByEmail,
            ReviewedAt = r.ReviewedAt,
            AdminDecisionNote = r.AdminDecisionNote,
            DisbursedAt = r.DisbursedAt,
            DisbursedByEmail = r.DisbursedByEmail
        };
    }

    public async Task<ApiResponse<ManualRefundOverviewDto>> GetOverviewAsync(
        string? status = null,
        string? category = null,
        string? search = null)
    {
        var storedDict = await LoadStoredRecordsAsync();
        var allRecords = storedDict.Values
            .OrderByDescending(r => r.RequestedAt)
            .ToList();

        var bookingIds = allRecords.Select(r => r.BookingId).Distinct().ToList();
        var liveBookingStatuses = await _db.Bookings.AsNoTracking()
            .Where(b => bookingIds.Contains(b.Id))
            .ToDictionaryAsync(b => b.Id, b => b.Status.ToString());

        var allDtos = allRecords
            .Select(r => MapToDto(r, liveBookingStatuses.TryGetValue(r.BookingId, out var st) ? st : null))
            .ToList();

        var filtered = allDtos.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(status) && !status.Equals("ALL", StringComparison.OrdinalIgnoreCase))
        {
            filtered = filtered.Where(x => string.Equals(x.Status, status.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(category) && !category.Equals("ALL", StringComparison.OrdinalIgnoreCase))
        {
            filtered = filtered.Where(x => string.Equals(x.ExceptionCategory, category.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var kw = search.Trim().ToLowerInvariant();
            filtered = filtered.Where(x =>
                x.RefundCode.ToLowerInvariant().Contains(kw) ||
                x.BookingCode.ToLowerInvariant().Contains(kw) ||
                x.OriginalOrderCode.ToLowerInvariant().Contains(kw) ||
                x.CustomerName.ToLowerInvariant().Contains(kw) ||
                x.CustomerEmail.ToLowerInvariant().Contains(kw) ||
                x.StationName.ToLowerInvariant().Contains(kw) ||
                (x.RelatedIncidentCode != null && x.RelatedIncidentCode.ToLowerInvariant().Contains(kw)));
        }

        var disbursedItems = allDtos.Where(x => x.Status == "DISBURSED").ToList();
        var pendingItems = allDtos.Where(x => x.Status == "PENDING_APPROVAL").ToList();
        var approvedPendingItems = allDtos.Where(x => x.Status == "APPROVED_PENDING_PAYOUT").ToList();

        var categoryStats = CategoryLabels.Select(kv =>
        {
            var matches = allDtos.Where(x =>
                string.Equals(x.ExceptionCategory, kv.Key, StringComparison.OrdinalIgnoreCase) &&
                x.Status != "REJECTED").ToList();

            return new RefundCategoryStatDto
            {
                Category = kv.Key,
                CategoryLabel = kv.Value,
                Count = matches.Count,
                TotalAmount = matches.Sum(m => m.ApprovedAmount ?? m.RequestedAmount)
            };
        }).ToList();

        var overview = new ManualRefundOverviewDto
        {
            TotalRequests = allDtos.Count,
            PendingApprovalCount = pendingItems.Count,
            PendingApprovalAmount = pendingItems.Sum(x => x.RequestedAmount),
            ApprovedPendingPayoutCount = approvedPendingItems.Count,
            ApprovedPendingPayoutAmount = approvedPendingItems.Sum(x => x.ApprovedAmount ?? x.RequestedAmount),
            DisbursedCount = disbursedItems.Count,
            TotalDisbursedAmount = disbursedItems.Sum(x => x.ApprovedAmount ?? x.RequestedAmount),
            RejectedCount = allDtos.Count(x => x.Status == "REJECTED"),
            AverageRefundAmount = disbursedItems.Count > 0
                ? Math.Round(disbursedItems.Sum(x => x.ApprovedAmount ?? x.RequestedAmount) / disbursedItems.Count, 0)
                : 0m,
            CategoryBreakdown = categoryStats,
            Items = filtered.ToList()
        };

        return ApiResponse<ManualRefundOverviewDto>.SuccessResponse(overview);
    }

    public async Task<ApiResponse<List<RefundablePaymentCandidateDto>>> GetRefundableCandidatesAsync(string? search = null)
    {
        var storedDict = await LoadStoredRecordsAsync();
        var activeManualRefunds = storedDict.Values
            .Where(r => r.Status == "PENDING_APPROVAL" || r.Status == "APPROVED_PENDING_PAYOUT")
            .ToList();

        var query = _db.Payments.AsNoTracking()
            .Include(p => p.Booking)
                .ThenInclude(b => b.User)
            .Include(p => p.Booking)
                .ThenInclude(b => b.Station)
            .Include(p => p.Booking)
                .ThenInclude(b => b.Locker)
            .Include(p => p.Booking)
                .ThenInclude(b => b.Payments)
            .Where(p => p.Status == PaymentStatus.PAID && p.Kind != PaymentKind.REFUND);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var kw = search.Trim();
            query = query.Where(p =>
                p.OrderCode.Contains(kw) ||
                p.Booking.BookingCode.Contains(kw) ||
                (p.Booking.User != null && (p.Booking.User.FullName.Contains(kw) || p.Booking.User.Email.Contains(kw) || p.Booking.User.Phone.Contains(kw))) ||
                (p.Booking.Station != null && p.Booking.Station.Name.Contains(kw)));
        }

        var paidPayments = await query
            .OrderByDescending(p => p.PaidAt ?? p.CreatedAt)
            .Take(80)
            .ToListAsync();

        var stationIds = paidPayments
            .Where(p => p.Booking != null)
            .Select(p => p.Booking.StationId)
            .Distinct()
            .ToList();

        var openIncidents = await _db.SecurityIncidents.AsNoTracking()
            .Where(i => stationIds.Contains(i.StationId) && i.Status != IncidentStatus.RESOLVED)
            .OrderByDescending(i => i.CreatedAt)
            .ToListAsync();

        var candidates = new List<RefundablePaymentCandidateDto>();

        foreach (var p in paidPayments)
        {
            var b = p.Booking;
            if (b == null) continue;

            var totalBookingPaid = b.Payments
                .Where(x => x.Status == PaymentStatus.PAID && x.Kind != PaymentKind.REFUND)
                .Sum(x => x.Amount);

            var alreadyRefunded = b.Payments
                .Where(x => x.Kind == PaymentKind.REFUND &&
                            (x.Status == PaymentStatus.REFUNDED || x.Status == PaymentStatus.PAID))
                .Sum(x => x.Amount);

            var pendingRequested = activeManualRefunds
                .Where(r => r.BookingId == b.Id)
                .Sum(r => r.ApprovedAmount ?? r.RequestedAmount);

            var maxRemaining = Math.Max(0m, totalBookingPaid - alreadyRefunded - pendingRequested);

            var matchingIncident = openIncidents.FirstOrDefault(inc =>
                (b.LockerId.HasValue && inc.LockerId == b.LockerId) || inc.StationId == b.StationId);

            candidates.Add(new RefundablePaymentCandidateDto
            {
                PaymentId = p.Id,
                OrderCode = p.OrderCode,
                PaymentKind = p.Kind.ToString(),
                PaidAmount = p.Amount,
                PaidAt = p.PaidAt ?? p.CreatedAt,
                BookingId = b.Id,
                BookingCode = b.BookingCode,
                BookingStatus = b.Status.ToString(),
                StationName = b.Station?.Name ?? "Trạm SmartLocker",
                LockerCode = b.Locker?.LockerCode,
                LockerSize = b.Size.ToString(),
                CustomerId = b.UserId,
                CustomerName = b.User?.FullName ?? "Khách hàng",
                CustomerEmail = b.User?.Email ?? "",
                CustomerPhone = b.User?.Phone ?? "",
                CustomerOverdueDebt = b.User?.OverdueDebt ?? 0m,
                TotalBookingPaidAmount = totalBookingPaid,
                AlreadyRefundedAmount = alreadyRefunded,
                PendingRefundRequestedAmount = pendingRequested,
                MaxRefundableRemaining = Math.Min(p.Amount, maxRemaining),
                SuggestedIncidentCode = matchingIncident?.IncidentCode
            });
        }

        return ApiResponse<List<RefundablePaymentCandidateDto>>.SuccessResponse(candidates);
    }

    public async Task<ApiResponse<ManualRefundItemDto>> CreateRequestAsync(
        CreateManualRefundRequest request,
        Guid actorUserId)
    {
        if (request.PaymentId == Guid.Empty)
            return ApiResponse<ManualRefundItemDto>.ErrorResponse("Vui lòng chọn giao dịch đã thanh toán cần lập lệnh hoàn tiền.");

        if (request.RequestedAmount < 1000m)
            return ApiResponse<ManualRefundItemDto>.ErrorResponse("Số tiền đề xuất hoàn tối thiểu phải từ 1.000 VNĐ.");

        if (string.IsNullOrWhiteSpace(request.ReasonSummary) || request.ReasonSummary.Trim().Length < 8)
            return ApiResponse<ManualRefundItemDto>.ErrorResponse("Vui lòng mô tả rõ lý do ngoại lệ cần hoàn tiền (tối thiểu 8 ký tự).");

        var categoryKey = CategoryLabels.ContainsKey(request.ExceptionCategory ?? "")
            ? request.ExceptionCategory!.Trim().ToUpperInvariant()
            : "FORCE_MAJEURE_COMPLAINT";

        await _refundLock.WaitAsync();
        try
        {
            var payment = await _db.Payments
                .Include(p => p.Booking)
                    .ThenInclude(b => b.User)
                .Include(p => p.Booking)
                    .ThenInclude(b => b.Station)
                .Include(p => p.Booking)
                    .ThenInclude(b => b.Locker)
                .Include(p => p.Booking)
                    .ThenInclude(b => b.Payments)
                .FirstOrDefaultAsync(p => p.Id == request.PaymentId);

            if (payment == null || payment.Booking == null)
                return ApiResponse<ManualRefundItemDto>.ErrorResponse("Không tìm thấy giao dịch gốc trong cơ sở dữ liệu.");

            if (payment.Status != PaymentStatus.PAID || payment.Kind == PaymentKind.REFUND)
                return ApiResponse<ManualRefundItemDto>.ErrorResponse("Chỉ được phép lập lệnh hoàn tiền trên các giao dịch đã thu tiền thành công (trạng thái PAID).");

            var storedDict = await LoadStoredRecordsAsync();
            var booking = payment.Booking;

            var totalBookingPaid = booking.Payments
                .Where(x => x.Status == PaymentStatus.PAID && x.Kind != PaymentKind.REFUND)
                .Sum(x => x.Amount);

            var alreadyRefunded = booking.Payments
                .Where(x => x.Kind == PaymentKind.REFUND &&
                            (x.Status == PaymentStatus.REFUNDED || x.Status == PaymentStatus.PAID))
                .Sum(x => x.Amount);

            var pendingRequested = storedDict.Values
                .Where(r => r.BookingId == booking.Id &&
                            (r.Status == "PENDING_APPROVAL" || r.Status == "APPROVED_PENDING_PAYOUT"))
                .Sum(r => r.ApprovedAmount ?? r.RequestedAmount);

            var maxAllowed = Math.Min(
                payment.Amount - (payment.RefundAmount ?? 0m),
                Math.Max(0m, totalBookingPaid - alreadyRefunded - pendingRequested));

            if (maxAllowed <= 0m)
            {
                return ApiResponse<ManualRefundItemDto>.ErrorResponse(
                    $"Giao dịch {payment.OrderCode} (Đơn {booking.BookingCode}) đã được hoàn tối đa hoặc đang có lệnh chờ duyệt hết hạn mức ({totalBookingPaid:N0}đ).");
            }

            var requestedAmt = Math.Round(request.RequestedAmount, 0);
            if (requestedAmt > maxAllowed)
            {
                return ApiResponse<ManualRefundItemDto>.ErrorResponse(
                    $"Số tiền đề xuất hoàn ({requestedAmt:N0}đ) vượt quá hạn mức còn được phép hoàn của giao dịch ({maxAllowed:N0}đ). Hệ thống chặn hoàn lố tự động.");
            }

            var (actorName, actorEmail, actorRole) = await GetActorProfileAsync(actorUserId);
            var isAdminActor = string.Equals(actorRole, "ADMIN", StringComparison.OrdinalIgnoreCase);

            var now = DateTime.UtcNow;
            var refundCode = $"MRF-{now:yyMMdd}-{Random.Shared.Next(1000, 9999)}";
            var id = Guid.NewGuid().ToString("N");

            var record = new StoredManualRefundRecord
            {
                Id = id,
                RefundCode = refundCode,
                BookingId = booking.Id,
                BookingCode = booking.BookingCode,
                PaymentId = payment.Id,
                OriginalOrderCode = payment.OrderCode,
                OriginalPaymentKind = payment.Kind.ToString(),
                StationName = booking.Station?.Name ?? "Trạm SmartLocker",
                LockerCode = booking.Locker?.LockerCode,
                CustomerId = booking.UserId,
                CustomerName = booking.User?.FullName ?? "Khách hàng",
                CustomerEmail = booking.User?.Email ?? "",
                CustomerPhone = booking.User?.Phone ?? "",
                ExceptionCategory = categoryKey,
                ReasonSummary = request.ReasonSummary.Trim(),
                EvidenceNote = string.IsNullOrWhiteSpace(request.EvidenceNote) ? null : request.EvidenceNote.Trim(),
                RelatedIncidentCode = string.IsNullOrWhiteSpace(request.RelatedIncidentCode) ? null : request.RelatedIncidentCode.Trim().ToUpperInvariant(),
                OriginalPaidAmount = payment.Amount,
                PreviouslyRefundedAmount = alreadyRefunded,
                RequestedAmount = requestedAmt,
                RefundMethod = string.IsNullOrWhiteSpace(request.RefundMethod) ? "BANK_NAPAS247" : request.RefundMethod.Trim().ToUpperInvariant(),
                BankName = string.IsNullOrWhiteSpace(request.BankName) ? null : request.BankName.Trim(),
                BankAccountNumber = string.IsNullOrWhiteSpace(request.BankAccountNumber) ? null : request.BankAccountNumber.Trim(),
                BankAccountHolder = string.IsNullOrWhiteSpace(request.BankAccountHolder)
                    ? booking.User?.FullName
                    : request.BankAccountHolder.Trim().ToUpperInvariant(),
                CancelBookingAndReleaseLocker = request.CancelBookingAndReleaseLocker,
                WaiveRelatedOverdueDebt = request.WaiveRelatedOverdueDebt,
                Status = "PENDING_APPROVAL",
                RequestedByUserId = actorUserId,
                RequestedByName = actorName,
                RequestedByEmail = actorEmail,
                RequestedByRole = actorRole,
                RequestedAt = now
            };

            // Nếu chính ADMIN lập lệnh khẩn cấp và chọn Phê duyệt + Giải ngân ngay
            if (isAdminActor && request.AutoApproveAndDisburseNow)
            {
                var transferRef = string.IsNullOrWhiteSpace(request.GatewayTransferRef)
                    ? $"FT{now:yyMMddHHmmss}{Random.Shared.Next(10, 99)}"
                    : request.GatewayTransferRef.Trim().ToUpperInvariant();

                record.ApprovedAmount = requestedAmt;
                record.ReviewedByUserId = actorUserId;
                record.ReviewedByName = actorName;
                record.ReviewedByEmail = actorEmail;
                record.ReviewedAt = now;
                record.AdminDecisionNote = "Admin khởi tạo & phê duyệt giải ngân khẩn cấp trực tiếp.";

                await ExecuteFinancialDisbursementAsync(record, payment, booking, transferRef, actorUserId, actorEmail, actorRole);
                await SaveSnapshotAsync(actorUserId, record);

                await _auditLog.LogAsync(
                    actorUserId,
                    actorEmail,
                    actorRole,
                    "DIRECT_APPROVE_MANUAL_REFUND",
                    "Payment",
                    record.RefundCode,
                    $"Admin phê duyệt & giải ngân khẩn cấp lệnh hoàn tiền ngoại lệ {record.RefundCode} cho đơn {booking.BookingCode} (OrderCode {payment.OrderCode}): Số tiền={requestedAmt:N0}đ, Mã bút toán={transferRef}, Nhóm={categoryKey}. Lý do: {record.ReasonSummary}");

                return ApiResponse<ManualRefundItemDto>.SuccessResponse(
                    MapToDto(record, booking.Status.ToString()),
                    $"Đã phê duyệt & hạch toán hoàn tiền ngay {requestedAmt:N0}đ (Mã lệnh {record.RefundCode}).");
            }

            await SaveSnapshotAsync(actorUserId, record);

            await _auditLog.LogAsync(
                actorUserId,
                actorEmail,
                actorRole,
                "CREATE_MANUAL_REFUND_REQUEST",
                "Payment",
                record.RefundCode,
                $"Khởi tạo đề xuất hoàn tiền ngoại lệ {record.RefundCode} cho đơn {booking.BookingCode} (OrderCode {payment.OrderCode}): Đề xuất={requestedAmt:N0}đ / Thu gốc={payment.Amount:N0}đ, Nhóm={categoryKey}. Lý do: {record.ReasonSummary}");

            return ApiResponse<ManualRefundItemDto>.SuccessResponse(
                MapToDto(record, booking.Status.ToString()),
                $"Đã lập lệnh đề xuất hoàn tiền ngoại lệ {record.RefundCode} ({requestedAmt:N0}đ) — đang chờ Admin thẩm định phê duyệt.");
        }
        finally
        {
            _refundLock.Release();
        }
    }

    public async Task<ApiResponse<ManualRefundItemDto>> ApproveRequestAsync(
        string refundId,
        ApproveManualRefundRequest request,
        Guid adminUserId)
    {
        if (request.ApprovedAmount < 1000m)
            return ApiResponse<ManualRefundItemDto>.ErrorResponse("Số tiền phê duyệt hoàn tối thiểu phải từ 1.000 VNĐ.");

        if (string.IsNullOrWhiteSpace(request.AdminDecisionNote) || request.AdminDecisionNote.Trim().Length < 5)
            return ApiResponse<ManualRefundItemDto>.ErrorResponse("Vui lòng nhập ghi chú quyết định thẩm định của Admin (tối thiểu 5 ký tự).");

        await _refundLock.WaitAsync();
        try
        {
            var storedDict = await LoadStoredRecordsAsync();
            if (!storedDict.TryGetValue(refundId, out var record))
                return ApiResponse<ManualRefundItemDto>.ErrorResponse("Không tìm thấy phiếu yêu cầu hoàn tiền ngoại lệ.");

            if (record.Status != "PENDING_APPROVAL")
                return ApiResponse<ManualRefundItemDto>.ErrorResponse($"Chỉ có thể phê duyệt phiếu đang ở trạng thái CHỜ DUYỆT (hiện tại: {record.Status}).");

            var (adminName, adminEmail, adminRole) = await GetActorProfileAsync(adminUserId);
            if (!string.Equals(adminRole, "ADMIN", StringComparison.OrdinalIgnoreCase))
                return ApiResponse<ManualRefundItemDto>.ErrorResponse("Chỉ Quản trị viên (ADMIN) mới có thẩm quyền phê duyệt lệnh hoàn tiền ngoại lệ.");

            var payment = await _db.Payments
                .Include(p => p.Booking)
                    .ThenInclude(b => b.User)
                .Include(p => p.Booking)
                    .ThenInclude(b => b.Locker)
                .Include(p => p.Booking)
                    .ThenInclude(b => b.Payments)
                .FirstOrDefaultAsync(p => p.Id == record.PaymentId);

            if (payment == null || payment.Booking == null)
                return ApiResponse<ManualRefundItemDto>.ErrorResponse("Không tìm thấy giao dịch gốc liên kết với phiếu hoàn tiền này.");

            var booking = payment.Booking;
            var totalBookingPaid = booking.Payments
                .Where(x => x.Status == PaymentStatus.PAID && x.Kind != PaymentKind.REFUND)
                .Sum(x => x.Amount);

            var alreadyRefunded = booking.Payments
                .Where(x => x.Kind == PaymentKind.REFUND &&
                            (x.Status == PaymentStatus.REFUNDED || x.Status == PaymentStatus.PAID))
                .Sum(x => x.Amount);

            var otherPending = storedDict.Values
                .Where(r => r.BookingId == booking.Id &&
                            !string.Equals(r.Id, record.Id, StringComparison.OrdinalIgnoreCase) &&
                            r.Status == "APPROVED_PENDING_PAYOUT")
                .Sum(r => r.ApprovedAmount ?? r.RequestedAmount);

            var maxAllowed = Math.Min(
                payment.Amount - (payment.RefundAmount ?? 0m),
                Math.Max(0m, totalBookingPaid - alreadyRefunded - otherPending));

            var approvedAmt = Math.Round(request.ApprovedAmount, 0);
            if (approvedAmt > maxAllowed)
            {
                return ApiResponse<ManualRefundItemDto>.ErrorResponse(
                    $"Số tiền phê duyệt ({approvedAmt:N0}đ) vượt quá số dư tối đa có thể hoàn của giao dịch ({maxAllowed:N0}đ).");
            }

            var now = DateTime.UtcNow;
            record.ApprovedAmount = approvedAmt;
            record.ReviewedByUserId = adminUserId;
            record.ReviewedByName = adminName;
            record.ReviewedByEmail = adminEmail;
            record.ReviewedAt = now;
            record.AdminDecisionNote = request.AdminDecisionNote.Trim();
            record.CancelBookingAndReleaseLocker = request.CancelBookingAndReleaseLocker;
            record.WaiveRelatedOverdueDebt = request.WaiveRelatedOverdueDebt;

            if (request.DisburseImmediately)
            {
                var transferRef = string.IsNullOrWhiteSpace(request.GatewayTransferRef)
                    ? $"FT{now:yyMMddHHmmss}{Random.Shared.Next(10, 99)}"
                    : request.GatewayTransferRef.Trim().ToUpperInvariant();

                await ExecuteFinancialDisbursementAsync(record, payment, booking, transferRef, adminUserId, adminEmail, adminRole);
                await SaveSnapshotAsync(adminUserId, record);

                await _auditLog.LogAsync(
                    adminUserId,
                    adminEmail,
                    adminRole,
                    "APPROVE_AND_DISBURSE_MANUAL_REFUND",
                    "Payment",
                    record.RefundCode,
                    $"Phê duyệt & xác nhận giải ngân lệnh hoàn tiền {record.RefundCode} cho đơn {booking.BookingCode}: Duyệt hoàn={approvedAmt:N0}đ (Đề xuất={record.RequestedAmount:N0}đ), Mã FT={transferRef}. Ghi chú Admin: {record.AdminDecisionNote}");

                return ApiResponse<ManualRefundItemDto>.SuccessResponse(
                    MapToDto(record, booking.Status.ToString()),
                    $"Đã phê duyệt & hạch toán bút toán hoàn tiền {approvedAmt:N0}đ thành công (Mã {record.RefundCode}).");
            }

            record.Status = "APPROVED_PENDING_PAYOUT";
            await SaveSnapshotAsync(adminUserId, record);

            await _auditLog.LogAsync(
                adminUserId,
                adminEmail,
                adminRole,
                "APPROVE_MANUAL_REFUND",
                "Payment",
                record.RefundCode,
                $"Phê duyệt lệnh hoàn tiền ngoại lệ {record.RefundCode} (chờ kế toán chuyển khoản Napas247): Số tiền duyệt={approvedAmt:N0}đ. Ghi chú: {record.AdminDecisionNote}");

            return ApiResponse<ManualRefundItemDto>.SuccessResponse(
                MapToDto(record, booking.Status.ToString()),
                $"Đã phê duyệt lệnh {record.RefundCode} ({approvedAmt:N0}đ) — chuyển sang hàng đợi Xác nhận Giải ngân.");
        }
        finally
        {
            _refundLock.Release();
        }
    }

    public async Task<ApiResponse<ManualRefundItemDto>> RejectRequestAsync(
        string refundId,
        RejectManualRefundRequest request,
        Guid adminUserId)
    {
        if (string.IsNullOrWhiteSpace(request.RejectionReason) || request.RejectionReason.Trim().Length < 5)
            return ApiResponse<ManualRefundItemDto>.ErrorResponse("Bắt buộc nhập lý do từ chối lệnh hoàn tiền (tối thiểu 5 ký tự).");

        await _refundLock.WaitAsync();
        try
        {
            var storedDict = await LoadStoredRecordsAsync();
            if (!storedDict.TryGetValue(refundId, out var record))
                return ApiResponse<ManualRefundItemDto>.ErrorResponse("Không tìm thấy phiếu yêu cầu hoàn tiền.");

            if (record.Status != "PENDING_APPROVAL" && record.Status != "APPROVED_PENDING_PAYOUT")
                return ApiResponse<ManualRefundItemDto>.ErrorResponse($"Không thể từ chối phiếu ở trạng thái {record.Status}.");

            var (adminName, adminEmail, adminRole) = await GetActorProfileAsync(adminUserId);
            if (!string.Equals(adminRole, "ADMIN", StringComparison.OrdinalIgnoreCase))
                return ApiResponse<ManualRefundItemDto>.ErrorResponse("Chỉ Quản trị viên (ADMIN) mới có quyền từ chối lệnh hoàn tiền.");

            record.Status = "REJECTED";
            record.ReviewedByUserId = adminUserId;
            record.ReviewedByName = adminName;
            record.ReviewedByEmail = adminEmail;
            record.ReviewedAt = DateTime.UtcNow;
            record.AdminDecisionNote = request.RejectionReason.Trim();

            await SaveSnapshotAsync(adminUserId, record);

            await _auditLog.LogAsync(
                adminUserId,
                adminEmail,
                adminRole,
                "REJECT_MANUAL_REFUND",
                "Payment",
                record.RefundCode,
                $"Từ chối lệnh hoàn tiền ngoại lệ {record.RefundCode} (Đơn {record.BookingCode}, Đề xuất {record.RequestedAmount:N0}đ). Lý do từ chối: {record.AdminDecisionNote}");

            return ApiResponse<ManualRefundItemDto>.SuccessResponse(
                MapToDto(record),
                $"Đã từ chối lệnh hoàn tiền {record.RefundCode}.");
        }
        finally
        {
            _refundLock.Release();
        }
    }

    public async Task<ApiResponse<ManualRefundItemDto>> ConfirmDisbursementAsync(
        string refundId,
        ConfirmManualRefundDisbursementRequest request,
        Guid adminUserId)
    {
        if (string.IsNullOrWhiteSpace(request.GatewayTransferRef) || request.GatewayTransferRef.Trim().Length < 4)
            return ApiResponse<ManualRefundItemDto>.ErrorResponse("Vui lòng nhập Mã tham chiếu chuyển khoản ngân hàng / Napas247 (VD: FT2630299182).");

        await _refundLock.WaitAsync();
        try
        {
            var storedDict = await LoadStoredRecordsAsync();
            if (!storedDict.TryGetValue(refundId, out var record))
                return ApiResponse<ManualRefundItemDto>.ErrorResponse("Không tìm thấy lệnh hoàn tiền.");

            if (record.Status != "APPROVED_PENDING_PAYOUT")
                return ApiResponse<ManualRefundItemDto>.ErrorResponse("Chỉ xác nhận giải ngân cho các lệnh đã được Admin phê duyệt (APPROVED_PENDING_PAYOUT).");

            var (_, adminEmail, adminRole) = await GetActorProfileAsync(adminUserId);
            if (!string.Equals(adminRole, "ADMIN", StringComparison.OrdinalIgnoreCase))
                return ApiResponse<ManualRefundItemDto>.ErrorResponse("Chỉ Quản trị viên (ADMIN) mới có quyền xác nhận tất toán giải ngân.");

            var payment = await _db.Payments
                .Include(p => p.Booking)
                    .ThenInclude(b => b.User)
                .Include(p => p.Booking)
                    .ThenInclude(b => b.Locker)
                .Include(p => p.Booking)
                    .ThenInclude(b => b.Payments)
                .FirstOrDefaultAsync(p => p.Id == record.PaymentId);

            if (payment == null || payment.Booking == null)
                return ApiResponse<ManualRefundItemDto>.ErrorResponse("Không tìm thấy giao dịch gốc liên kết.");

            var transferRef = request.GatewayTransferRef.Trim().ToUpperInvariant();
            if (!string.IsNullOrWhiteSpace(request.Note))
            {
                record.AdminDecisionNote = $"{record.AdminDecisionNote} | Giải ngân: {request.Note.Trim()}";
            }

            await ExecuteFinancialDisbursementAsync(record, payment, payment.Booking, transferRef, adminUserId, adminEmail, adminRole);
            await SaveSnapshotAsync(adminUserId, record);

            await _auditLog.LogAsync(
                adminUserId,
                adminEmail,
                adminRole,
                "DISBURSE_MANUAL_REFUND",
                "Payment",
                record.RefundCode,
                $"Xác nhận đã chuyển khoản hoàn tiền {record.RefundCode} cho đơn {record.BookingCode}: Số tiền={record.ApprovedAmount ?? record.RequestedAmount:N0}đ, Mã giao dịch NH={transferRef}.");

            return ApiResponse<ManualRefundItemDto>.SuccessResponse(
                MapToDto(record, payment.Booking.Status.ToString()),
                $"Đã xác nhận giải ngân & ghi sổ kế toán hoàn tiền thành công (Mã FT: {transferRef}).");
        }
        finally
        {
            _refundLock.Release();
        }
    }

    private async Task ExecuteFinancialDisbursementAsync(
        StoredManualRefundRecord record,
        Payment originalPayment,
        Booking booking,
        string transferRef,
        Guid actorId,
        string actorEmail,
        string actorRole)
    {
        var now = DateTime.UtcNow;
        var finalAmount = record.ApprovedAmount ?? record.RequestedAmount;

        // 1. Tạo bút toán chi hoàn tiền độc lập vào bảng Payments (khớp chuẩn với AdminReconciliationService UC-A12)
        var refundPayment = new Payment
        {
            Id = Guid.NewGuid(),
            BookingId = booking.Id,
            Kind = PaymentKind.REFUND,
            Status = PaymentStatus.REFUNDED,
            Amount = finalAmount,
            Currency = "VND",
            OrderCode = record.RefundCode,
            Gateway = record.RefundMethod.Length > 20 ? record.RefundMethod[..20] : record.RefundMethod,
            GatewayTxnId = transferRef,
            PaidAt = now,
            RefundAmount = finalAmount,
            RefundedAt = now,
            WebhookReceivedAt = now,
            WebhookPayload = JsonSerializer.Serialize(new
            {
                manualExceptionRefund = true,
                refundCode = record.RefundCode,
                originalOrderCode = originalPayment.OrderCode,
                category = record.ExceptionCategory,
                bankName = record.BankName,
                bankAccountNumber = record.BankAccountNumber,
                approvedBy = actorEmail
            }, _jsonOpts),
            CreatedAt = now,
            UpdatedAt = now
        };

        _db.Payments.Add(refundPayment);

        // 2. Cập nhật lũy kế RefundAmount trên giao dịch thu gốc (giữ Status = PAID để không bị cộng trùng trên đối soát Gross/Refund)
        originalPayment.RefundAmount = (originalPayment.RefundAmount ?? 0m) + finalAmount;
        originalPayment.RefundedAt = now;
        originalPayment.UpdatedAt = now;

        // 3. Nếu tích chọn Hủy đơn & giải phóng ngăn tủ (khi đơn đang kẹt CONFIRMED / STORED do sự cố)
        if (record.CancelBookingAndReleaseLocker &&
            (booking.Status == BookingStatus.CONFIRMED || booking.Status == BookingStatus.STORED))
        {
            booking.Status = BookingStatus.CANCELLED;
            booking.CancellationReason = $"[HOÀN TIỀN NGOẠI LỆ {record.RefundCode}] {record.ReasonSummary}";
            booking.CancelPolicyRate = Math.Round(finalAmount / Math.Max(1m, originalPayment.Amount), 2);
            booking.UpdatedAt = now;

            if (booking.Locker != null &&
                (booking.Locker.BusinessStatus == LockerBusinessStatus.OCCUPIED ||
                 booking.Locker.BusinessStatus == LockerBusinessStatus.RESERVED))
            {
                // Nếu hoàn tiền do lỗi phần cứng kẹt khóa thì chuyển ngăn sang MAINTENANCE để kỹ thuật kiểm tra, ngược lại trả về AVAILABLE
                booking.Locker.BusinessStatus = record.ExceptionCategory == "HARDWARE_LOCK_JAM"
                    ? LockerBusinessStatus.MAINTENANCE
                    : LockerBusinessStatus.AVAILABLE;
                booking.Locker.UpdatedAt = now;
            }

            // Nếu hoàn 100% tiền gốc của đơn thì hoàn trả lại hạn mức Voucher đã dùng (nếu có)
            if (finalAmount >= originalPayment.Amount)
            {
                await _promotionService.ReleaseVoucherForBookingAsync(
                    booking.Id,
                    $"Hoàn tiền ngoại lệ 100% theo lệnh {record.RefundCode}");
            }
        }

        // 4. Nếu tích chọn Miễn trừ dư nợ quá hạn phát sinh do sự cố kỹ thuật liên quan
        if (record.WaiveRelatedOverdueDebt && booking.User != null && booking.User.OverdueDebt > 0m)
        {
            var waivedDebt = booking.User.OverdueDebt;
            booking.User.OverdueDebt = 0m;
            booking.User.UpdatedAt = now;
            booking.IsOverdue = false;

            await _auditLog.LogAsync(
                actorId,
                actorEmail,
                actorRole,
                "WAIVE_DEBT_ON_EXCEPTION_REFUND",
                "User",
                booking.UserId.ToString(),
                $"Miễn trừ {waivedDebt:N0}đ dư nợ quá hạn phát sinh do sự cố ngoại lệ kèm lệnh hoàn tiền {record.RefundCode}.");
        }

        await _db.SaveChangesAsync();

        record.Status = "DISBURSED";
        record.GatewayTransferRef = transferRef;
        record.RefundPaymentId = refundPayment.Id;
        record.DisbursedAt = now;
        record.DisbursedByEmail = actorEmail;
    }
}
