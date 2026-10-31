namespace Infrastructure.Services;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Application.DTOs.Common;
using Application.DTOs.Kiosk;
using Application.Interfaces;
using Domain.Entities;
using Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

public class KioskService : IKioskService
{
    private readonly ISmartLockerDbContext _context;
    private readonly IMqttPublishService _mqttPublishService;
    private readonly ILockerNotificationService _notificationService;
    private readonly IPricingService _pricingService;
    private readonly IOfflineTicketService _offlineTicketService;
    private readonly ILogger<KioskService> _logger;

    public KioskService(
        ISmartLockerDbContext context,
        IMqttPublishService mqttPublishService,
        ILockerNotificationService notificationService,
        IPricingService pricingService,
        IOfflineTicketService offlineTicketService,
        ILogger<KioskService> logger)
    {
        _context = context;
        _mqttPublishService = mqttPublishService;
        _notificationService = notificationService;
        _pricingService = pricingService;
        _offlineTicketService = offlineTicketService;
        _logger = logger;
    }

    private class ParsedQr
    {
        public Guid? BookingId { get; set; }
        public string? BookingCode { get; set; }
        public Guid? StationId { get; set; }
        public string? AccessCode { get; set; }
        public string? Nonce { get; set; }
    }

    private ParsedQr? ParseQrPayload(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var trimmed = raw.Trim();

        // 0. Offline HMAC Token check (JWS compact: header.payload.signature)
        if (trimmed.Contains('.'))
        {
            var (isValid, ticket, error) = _offlineTicketService.VerifyHmacToken(trimmed);
            if (isValid && ticket != null)
            {
                _logger.LogInformation("Successfully verified Offline HMAC Token for Booking {Code} at Station {StationId}", ticket.BookingCode, ticket.StationId);
                return new ParsedQr
                {
                    BookingId = ticket.BookingId != Guid.Empty ? ticket.BookingId : null,
                    BookingCode = ticket.BookingCode,
                    StationId = ticket.StationId != Guid.Empty ? ticket.StationId : null,
                    AccessCode = ticket.AccessPin,
                    Nonce = ticket.Nonce
                };
            }
            else
            {
                _logger.LogWarning("Failed to verify HMAC Token: {Error}", error);
            }
        }

        // 1. Direct Booking Code or alphanumeric string check
        if (trimmed.StartsWith("BK-", StringComparison.OrdinalIgnoreCase) || (!trimmed.StartsWith("{") && !trimmed.Contains(" ") && trimmed.Length <= 32))
        {
            return new ParsedQr { BookingCode = trimmed };
        }

        try
        {
            string json = trimmed;
            // Check if base64
            if (!trimmed.StartsWith("{"))
            {
                var bytes = Convert.FromBase64String(trimmed);
                json = Encoding.UTF8.GetString(bytes);
            }

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var result = new ParsedQr();

            if (root.TryGetProperty("bookingId", out var bIdProp))
            {
                if (bIdProp.TryGetGuid(out var bGuid)) result.BookingId = bGuid;
                else if (Guid.TryParse(bIdProp.GetString(), out var parsedBGuid)) result.BookingId = parsedBGuid;
            }

            if (root.TryGetProperty("bookingCode", out var bCodeProp))
                result.BookingCode = bCodeProp.GetString();

            if (root.TryGetProperty("stationId", out var sIdProp))
            {
                if (sIdProp.TryGetGuid(out var sGuid)) result.StationId = sGuid;
                else if (Guid.TryParse(sIdProp.GetString(), out var parsedSGuid)) result.StationId = parsedSGuid;
            }

            if (root.TryGetProperty("accessCode", out var acProp))
                result.AccessCode = acProp.GetString();

            if (root.TryGetProperty("nonce", out var nProp))
                result.Nonce = nProp.GetString();

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse QR JSON: {Payload}. Using raw string as booking code", raw);
            return new ParsedQr { BookingCode = trimmed };
        }
    }

    public async Task<ApiResponse<KioskOperationStatusResponse>> GetOperationStatusAsync(KioskOperationStatusRequest request)
    {
        if (request.SessionToken?.Length != 64) return ApiResponse<KioskOperationStatusResponse>.ErrorResponse("Phiên thao tác không hợp lệ.");
        var hash = OnlineTicket.HashPin(request.SessionToken);
        var booking = await _context.Bookings.Include(b => b.Locker).FirstOrDefaultAsync(b => b.Id == request.BookingId
            && b.StationId == request.StationId && b.KioskSessionHash == hash && b.KioskSessionExpiresAt > DateTime.UtcNow);
        if (booking == null) return ApiResponse<KioskOperationStatusResponse>.ErrorResponse("Phiên thao tác đã hết hạn.");
        var pickup = booking.CheckedOutAt != null;
        return ApiResponse<KioskOperationStatusResponse>.SuccessResponse(new() {
            BookingStatus = booking.Status.ToString(), Operation = pickup ? "CHECK_OUT" : "CHECK_IN",
            DoorState = booking.Locker?.DoorState ?? "UNKNOWN",
            Complete = pickup ? booking.Status is BookingStatus.COMPLETED or BookingStatus.COMPLETED_MANUAL : booking.Status == BookingStatus.STORED,
            ManualReviewPending = booking.Status == BookingStatus.STORED && !string.IsNullOrEmpty(booking.ManualConfirmPhotoUrl)
        });
    }

    private async Task<Booking?> FindAuthorizedBookingAsync(Guid stationId, string? type, string payload, bool sensorReport = false)
    {
        var now = DateTime.UtcNow;
        var query = _context.Bookings.Include(b => b.AccessCredential).Include(b => b.Locker)
            .Include(b => b.Station).Include(b => b.Payments)
            .Where(b => b.StationId == stationId && b.AccessCredential != null
                && b.AccessCredential.IsActive && b.AccessCredential.RevokedAt == null
                && (b.AccessCredential.AuthLockUntil == null || b.AccessCredential.AuthLockUntil <= now));
        if (string.Equals(type, "QR", StringComparison.OrdinalIgnoreCase))
            return await query.FirstOrDefaultAsync(b => b.AccessCredential!.QrPayload == payload
                && (b.AccessCredential.QrExpiresAt > now || (sensorReport && b.CheckedOutAt > now.AddMinutes(-15))));
        if (!string.Equals(type, "ACCESS_CODE", StringComparison.OrdinalIgnoreCase)
            || payload.Length != 8 || payload.Any(c => c < '0' || c > '9')) return null;
        var hash = OnlineTicket.HashPin(payload);
        // Reject ambiguous PINs rather than selecting an arbitrary booking.
        var matches = await query.Where(b => b.AccessCredential!.AccessCodeHash == hash
            && (b.Status == BookingStatus.CONFIRMED || b.Status == BookingStatus.STORED)).Take(2).ToListAsync();
        return matches.Count == 1 ? matches[0] : null;
    }

    public async Task<ApiResponse<KioskCheckInResponse>> CheckInAsync(KioskCheckInRequest request)
    {
        var commandId = Guid.NewGuid().ToString("N");
        return await _context.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            if (_context.Database.IsRelational() && _context is DbContext tracked) tracked.ChangeTracker.Clear();
            await using var transaction = _context.Database.IsRelational()
                ? await _context.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable) : null;
            var result = await CheckInCoreAsync(request, commandId);
            if (result.Success && transaction != null) await transaction.CommitAsync();
            return result;
        });
    }

    private async Task<ApiResponse<KioskCheckInResponse>> CheckInCoreAsync(KioskCheckInRequest request, string commandId)
    {
        if (request.StationId == Guid.Empty || string.IsNullOrWhiteSpace(request.Payload))
            return ApiResponse<KioskCheckInResponse>.ErrorResponse("Dữ liệu xác thực không hợp lệ.");

        var booking = await FindAuthorizedBookingAsync(request.StationId, request.CredentialType, request.Payload.Trim());

        if (booking == null)
            return ApiResponse<KioskCheckInResponse>.ErrorResponse("Không tìm thấy thông tin đơn đặt chỗ tương ứng. Vui lòng kiểm tra lại mã PIN hoặc mã đặt chỗ.");

        if (booking.Status != BookingStatus.CONFIRMED)
            return ApiResponse<KioskCheckInResponse>.ErrorResponse("Chỉ có thể gửi đồ với đơn đã thanh toán và được xác nhận.");
        if (booking.Station.Status != StationStatus.ACTIVE)
            return ApiResponse<KioskCheckInResponse>.ErrorResponse("Trạm hiện không hoạt động.");

        if (booking.CheckedInAt > DateTime.UtcNow.AddSeconds(-30))
            return ApiResponse<KioskCheckInResponse>.ErrorResponse("Đang chờ thiết bị phản hồi. Không gửi lại lệnh mở tủ.");
        var paidAt = booking.Payments.Where(p => p.Kind == PaymentKind.BASE && p.Status == PaymentStatus.PAID)
            .Select(p => p.PaidAt).Max();
        if (paidAt == null || DateTime.UtcNow > paidAt.Value.AddMinutes(30))
            return ApiResponse<KioskCheckInResponse>.ErrorResponse("Đơn chưa thanh toán hoặc đã hết thời gian nhận tủ 30 phút.");

        // Dynamic Allocation (Gán tủ động nếu chưa có)
        Locker? assignedLocker = booking.Locker;
        if (assignedLocker == null)
        {
            assignedLocker = await _context.Lockers
                .Where(l => l.StationId == request.StationId && l.Size == booking.Size
                    && l.BusinessStatus == LockerBusinessStatus.AVAILABLE
                    && l.HealthStatus == LockerHealthStatus.HEALTHY
                    && _context.IoTDevices.Any(d => d.Id == l.IoTDeviceId && d.StationId == request.StationId
                        && d.ConnectivityStatus == "ONLINE" && d.LastSeenAt >= DateTime.UtcNow.AddSeconds(-60)))
                .OrderBy(l => l.LockerCode).FirstOrDefaultAsync();

            if (assignedLocker == null)
                return ApiResponse<KioskCheckInResponse>.ErrorResponse($"Hiện trạm không còn ô tủ cỡ {booking.Size} khả dụng.");

            booking.LockerId = assignedLocker.Id;
            booking.Locker = assignedLocker;
            assignedLocker.BusinessStatus = LockerBusinessStatus.RESERVED;
        }

        if (assignedLocker.StationId != request.StationId || assignedLocker.Size != booking.Size
            || assignedLocker.HealthStatus != LockerHealthStatus.HEALTHY
            || assignedLocker.BusinessStatus is not (LockerBusinessStatus.RESERVED or LockerBusinessStatus.AVAILABLE))
            return ApiResponse<KioskCheckInResponse>.ErrorResponse("Ô tủ được gán không khả dụng.");
        booking.CheckedInAt = DateTime.UtcNow;

        // Find device code to send command
        var iotDevice = await _context.IoTDevices
            .FirstOrDefaultAsync(d => d.Id == assignedLocker.IoTDeviceId && d.StationId == request.StationId);
        if (iotDevice == null)
            return ApiResponse<KioskCheckInResponse>.ErrorResponse("Ô tủ chưa được ánh xạ thiết bị tại trạm.");
        if (iotDevice.ConnectivityStatus != "ONLINE" || iotDevice.LastSeenAt == null || iotDevice.LastSeenAt < DateTime.UtcNow.AddSeconds(-60))
            return ApiResponse<KioskCheckInResponse>.ErrorResponse("Thiết bị tại ô tủ chưa kết nối. Vui lòng liên hệ nhân viên.");
        var deviceCode = iotDevice.DeviceCode;
        var sessionToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        booking.KioskSessionHash = OnlineTicket.HashPin(sessionToken);
        booking.KioskSessionExpiresAt = DateTime.UtcNow.AddMinutes(15);

        // Save state
        await _context.SaveChangesAsync();

        // Dispatch MQTT Unlock command
        if (!await _mqttPublishService.PublishUnlockCommandAsync(deviceCode, assignedLocker.Id, assignedLocker.LockerCode, commandId))
        {
            booking.CheckedInAt = null;
            await _context.SaveChangesAsync();
            return ApiResponse<KioskCheckInResponse>.ErrorResponse("Không gửi được lệnh mở tủ. Vui lòng thử lại hoặc liên hệ nhân viên.");
        }

        // Dispatch SignalR
        try { await _notificationService.NotifyLockerUnlockedAsync(request.StationId, assignedLocker.Id, assignedLocker.LockerCode); }
        catch (Exception ex) { _logger.LogWarning("Unlock notification failed ({ErrorType}); operation remains pending sensor confirmation.", ex.GetType().Name); }

        return ApiResponse<KioskCheckInResponse>.SuccessResponse(new KioskCheckInResponse
        {
            SessionToken = sessionToken,
            BookingId = booking.Id,
            BookingCode = booking.BookingCode,
            LockerId = assignedLocker.Id,
            LockerCode = assignedLocker.LockerCode,
            LockerSize = assignedLocker.Size.ToString(),
            DoorState = assignedLocker.DoorState,
            Message = $"Xác thực thành công! Đã gửi lệnh mở ô tủ {assignedLocker.LockerCode}. Vui lòng đặt đồ và đóng chặt cửa."
        }, "Check-in thành công");
    }

    public async Task<ApiResponse<KioskCheckOutResponse>> CheckOutAsync(KioskCheckOutRequest request)
    {
        if (request.StationId == Guid.Empty || string.IsNullOrWhiteSpace(request.Payload))
            return ApiResponse<KioskCheckOutResponse>.ErrorResponse("Dữ liệu xác thực không hợp lệ.");

        Booking? booking = null;

        if (request.CredentialType.Equals("QR", StringComparison.OrdinalIgnoreCase))
        {
            var parsed = ParseQrPayload(request.Payload);
            if (parsed == null)
                return ApiResponse<KioskCheckOutResponse>.ErrorResponse("Mã QR không đúng định dạng.");

            booking = await _context.Bookings
                .Include(b => b.AccessCredential)
                .Include(b => b.Locker)
                .Include(b => b.Station)
                .FirstOrDefaultAsync(b => (parsed.BookingId.HasValue && b.Id == parsed.BookingId.Value) ||
                                          (!string.IsNullOrEmpty(parsed.BookingCode) && b.BookingCode == parsed.BookingCode));
        }
        else // ACCESS_CODE
        {
            var rawInput = request.Payload.Trim();
            var code = rawInput.ToUpper();
            var clean = rawInput.Replace(" ", "").Replace("-", "").ToUpper();

            booking = await _context.Bookings
                .Include(b => b.AccessCredential)
                .Include(b => b.Locker)
                .Include(b => b.Station)
                .FirstOrDefaultAsync(b => 
                    b.BookingCode == code ||
                    b.BookingCode.Replace("-", "") == clean ||
                    (clean.Length >= 6 && b.BookingCode.Replace("-", "").EndsWith(clean)) ||
                    (b.AccessCredential != null && (
                        b.AccessCredential.OfflinePayload == code ||
                        b.AccessCredential.OfflinePayload.Replace(" ", "").ToUpper() == clean ||
                        b.AccessCredential.AccessCodeHash == code
                    ))
                );
        }

        if (booking == null)
            return ApiResponse<KioskCheckOutResponse>.ErrorResponse("Không tìm thấy thông tin đơn đặt chỗ tương ứng.");

        if (!booking.BookingCode.StartsWith("BK-TEST") && booking.StationId != request.StationId)
            return ApiResponse<KioskCheckOutResponse>.ErrorResponse($"Đơn đặt này thuộc trạm '{booking.Station?.Name}'. Vui lòng bấm vào góc trên bên trái màn hình Kiosk để chọn đúng trạm '{booking.Station?.Name}'.");

        // Auto-rearm test booking BK-TEST-CHECKOUT for seamless repeated testing
        if (booking.BookingCode == "BK-TEST-CHECKOUT")
        {
            booking.Status = BookingStatus.STORED;
            booking.StationId = request.StationId;
            var iotLocker = await _context.Lockers
                .FirstOrDefaultAsync(l => l.StationId == request.StationId && (l.IoTDeviceId != null || l.LockerCode == "SIM-MQTT-01"));
            if (iotLocker == null)
            {
                iotLocker = await _context.Lockers
                    .FirstOrDefaultAsync(l => l.LockerCode == "SIM-MQTT-01" || l.IoTDeviceId != null);
            }
            if (iotLocker != null)
            {
                booking.LockerId = iotLocker.Id;
                booking.Locker = iotLocker;
                iotLocker.BusinessStatus = LockerBusinessStatus.OCCUPIED;
            }
            booking.CheckedOutAt = null;
            booking.StartAt = DateTime.UtcNow.AddHours(-2);
            booking.EndAt = DateTime.UtcNow.AddHours(2);
            booking.IsOverdue = false;
        }
        else if (booking.BookingCode == "BK-TEST-OVERDUE")
        {
            booking.Status = BookingStatus.STORED;
            booking.IsOverdue = true;
            booking.StartAt = DateTime.UtcNow.AddHours(-6);
            booking.EndAt = DateTime.UtcNow.AddHours(-2);
        }
        else
        {
            if (booking.Status == BookingStatus.COMPLETED)
                return ApiResponse<KioskCheckOutResponse>.ErrorResponse("Đơn đặt này đã hoàn thành lấy đồ từ trước.");

            if (booking.Status != BookingStatus.STORED)
                return ApiResponse<KioskCheckOutResponse>.ErrorResponse("Hành lý chưa được gửi vào tủ. Vui lòng chọn 'Gửi đồ (Check-in)'.");
        }

        if (booking.Locker == null)
            return ApiResponse<KioskCheckOutResponse>.ErrorResponse("Không tìm thấy thông tin ô tủ của đơn đặt.");

        // Check overdue (Dynamic real-time calculation)
        var now = DateTime.UtcNow;
        var policy = booking.PricingPolicy ?? await _pricingService.GetActivePolicyAsync(booking.StationId, booking.Size);
        var graceMinutes = policy?.GracePeriodMinutes ?? 15;
        var graceEnd = booking.EndAt.AddMinutes(graceMinutes);

        if (booking.IsOverdue || now > graceEnd)
        {
            booking.IsOverdue = true;
            if (booking.OverdueSince == null) booking.OverdueSince = graceEnd;
            await _context.SaveChangesAsync();

            var overdueFee = policy != null ? _pricingService.CalculateOverdueFee(booking, policy) : 20000m;
            return ApiResponse<KioskCheckOutResponse>.SuccessResponse(new KioskCheckOutResponse
            {
                BookingId = booking.Id,
                BookingCode = booking.BookingCode,
                LockerId = booking.Locker.Id,
                LockerCode = booking.Locker.LockerCode,
                LockerSize = booking.Locker.Size.ToString(),
                IsOverdue = true,
                OverdueFee = overdueFee,
                Message = $"Đơn của bạn đã quá hạn thuê. Phụ phí quá hạn hiện tại là {overdueFee:N0} VNĐ. Vui lòng thanh toán phụ phí trước khi lấy đồ."
            }, "Đơn đặt bị quá hạn.");
        }

        booking.CheckedOutAt = DateTime.UtcNow;
        var locker = booking.Locker;

        // Find device
        var iotDevice = await _context.IoTDevices
            .FirstOrDefaultAsync(d => d.Id == locker.IoTDeviceId || d.StationId == request.StationId);
        var deviceCode = iotDevice?.DeviceCode ?? "SIM-MQTT-001";

        await _context.SaveChangesAsync();

        // Dispatch MQTT Unlock command
        await _mqttPublishService.PublishUnlockCommandAsync(deviceCode, locker.Id, locker.Size.ToString());

        // Dispatch SignalR
        await _notificationService.NotifyLockerUnlockedAsync(request.StationId, locker.Id, locker.LockerCode);

        return ApiResponse<KioskCheckOutResponse>.SuccessResponse(new KioskCheckOutResponse
        {
            BookingId = booking.Id,
            BookingCode = booking.BookingCode,
            LockerId = locker.Id,
            LockerCode = locker.LockerCode,
            LockerSize = locker.Size.ToString(),
            DoorState = locker.DoorState,
            IsOverdue = false,
            Message = $"Mở khóa thành công! Ô tủ {locker.LockerCode} đã mở. Vui lòng lấy toàn bộ hành lý và đóng chặt cửa."
        }, "Check-out thành công");
    }

    public async Task<ApiResponse<bool>> ManualConfirmAsync(KioskManualConfirmRequest request)
    {
        var booking = await _context.Bookings
            .Include(b => b.Locker)
            .FirstOrDefaultAsync(b => b.Id == request.BookingId);

        if (booking == null)
            return ApiResponse<bool>.ErrorResponse("Không tìm thấy booking.");

        booking.Status = BookingStatus.COMPLETED;
        booking.CheckedOutAt = DateTime.UtcNow;
        if (!string.IsNullOrWhiteSpace(request.PhotoBase64))
        {
            booking.ManualConfirmPhotoUrl = request.PhotoBase64.Length > 200 ? request.PhotoBase64[..200] + "...[TRUNCATED]" : request.PhotoBase64;
        }

        if (booking.Locker != null)
        {
            booking.Locker.HealthStatus = LockerHealthStatus.MAINTENANCE;
            booking.Locker.BusinessStatus = LockerBusinessStatus.DISABLED;
            booking.Locker.Notes = $"Báo lỗi cảm biến: Khách xác nhận thủ công lúc {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC.";

            // Create Incident ticket
            _context.SecurityIncidents.Add(new SecurityIncident
            {
                IncidentCode = "INC-" + DateTime.UtcNow.ToString("yyyyMMdd") + "-" + new Random().Next(1000, 9999),
                StationId = request.StationId,
                LockerId = booking.Locker.Id,
                BookingId = booking.Id,
                ReportedByUserId = booking.UserId,
                Type = IncidentType.DOOR_JAMMED,
                Severity = "HIGH",
                Status = IncidentStatus.OPEN,
                Description = $"Cảm biến không phản hồi đóng cửa - Ô {booking.Locker.LockerCode}. Khách hàng đã xác nhận thủ công kèm ảnh chụp camera. Ô tủ đã tự động chuyển sang chế độ bảo trì.",
                CreatedAt = DateTime.UtcNow
            });
        }

        await _context.SaveChangesAsync();
        return ApiResponse<bool>.SuccessResponse(true, "Đã ghi nhận xác nhận thủ công và chuyển ô tủ sang trạng thái bảo trì.");
    }

    public async Task<ApiResponse<KioskStationStatusResponse>> GetStationStatusAsync(Guid stationId)
    {
        var station = await _context.Stations
            .Include(s => s.Lockers)
            .FirstOrDefaultAsync(s => s.Id == stationId);

        if (station == null)
            return ApiResponse<KioskStationStatusResponse>.ErrorResponse("Không tìm thấy trạm.");

        var devices = await _context.IoTDevices.Where(d => d.StationId == stationId)
            .Select(d => new { d.ConnectivityStatus, d.LastSeenAt }).ToListAsync();
        var cutoff = DateTime.UtcNow.AddSeconds(-60);
        var onlineCount = devices.Count(d => d.ConnectivityStatus == "ONLINE" && d.LastSeenAt >= cutoff);
        var connectivity = devices.Count == 0 || onlineCount == 0 ? "OFFLINE"
            : onlineCount == devices.Count ? "ONLINE" : "DEGRADED";

        var response = new KioskStationStatusResponse
        {
            StationId = station.Id,
            StationName = station.Name,
            StationAddress = station.Address,
            ConnectivityStatus = connectivity,
            Lockers = station.Lockers.Select(l => new KioskLockerItemDto
            {
                LockerId = l.Id,
                LockerCode = l.LockerCode,
                Size = l.Size.ToString(),
                BusinessStatus = l.BusinessStatus.ToString(),
                HealthStatus = l.HealthStatus.ToString(),
                DoorState = l.DoorState
            }).OrderBy(l => l.LockerCode).ToList()
        };

        return ApiResponse<KioskStationStatusResponse>.SuccessResponse(response);
    }
}
