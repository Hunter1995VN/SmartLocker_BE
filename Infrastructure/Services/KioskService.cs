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
    private readonly IPaymentGatewayService _paymentGateway;
    private readonly ILogger<KioskService> _logger;

    public KioskService(
        ISmartLockerDbContext context,
        IMqttPublishService mqttPublishService,
        ILockerNotificationService notificationService,
        IPricingService pricingService,
        IPaymentGatewayService paymentGateway,
        ILogger<KioskService> logger)
    {
        _context = context;
        _mqttPublishService = mqttPublishService;
        _notificationService = notificationService;
        _pricingService = pricingService;
        _paymentGateway = paymentGateway;
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

    public async Task<ApiResponse<KioskCheckInResponse>> CheckInAsync(KioskCheckInRequest request)
    {
        if (request.StationId == Guid.Empty || string.IsNullOrWhiteSpace(request.Payload))
            return ApiResponse<KioskCheckInResponse>.ErrorResponse("Dữ liệu xác thực không hợp lệ.");

        Booking? booking = null;

        if (request.CredentialType.Equals("QR", StringComparison.OrdinalIgnoreCase))
        {
            var parsed = ParseQrPayload(request.Payload);
            if (parsed == null)
                return ApiResponse<KioskCheckInResponse>.ErrorResponse("Mã QR không đúng định dạng.");

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
            return ApiResponse<KioskCheckInResponse>.ErrorResponse("Không tìm thấy thông tin đơn đặt chỗ tương ứng. Vui lòng kiểm tra lại mã PIN hoặc mã đặt chỗ.");

        // Nếu đơn thuộc trạm khác, kiểm tra hoặc hỗ trợ chuyển trạm
        if (!booking.BookingCode.StartsWith("BK-TEST") && booking.StationId != request.StationId)
        {
            return ApiResponse<KioskCheckInResponse>.ErrorResponse($"Đơn đặt này thuộc trạm '{booking.Station?.Name}'. Vui lòng bấm vào góc trên bên trái màn hình Kiosk để chọn đúng trạm '{booking.Station?.Name}'.");
        }

        // Tự động kích hoạt (Auto-confirm) đơn mới tạo ở trạng thái PENDING_PAYMENT để hỗ trợ demo tiện lợi
        if (booking.Status == BookingStatus.PENDING_PAYMENT)
        {
            booking.Status = BookingStatus.CONFIRMED;
            if (booking.AccessCredential == null)
            {
                var pin = Guid.NewGuid().ToString("N")[..8].ToUpper();
                booking.AccessCredential = new AccessCredential
                {
                    BookingId = booking.Id,
                    OfflinePayload = pin,
                    OfflineNonce = Guid.NewGuid().ToString("N"),
                    AccessCodeHash = pin,
                    QrPayload = pin,
                    QrNonce = Guid.NewGuid().ToString("N"),
                    QrExpiresAt = booking.EndAt,
                    IsActive = true,
                    IssuedAt = DateTime.UtcNow
                };
                _context.AccessCredentials.Add(booking.AccessCredential);
            }
            _logger.LogInformation("Booking {Code} auto-confirmed at Kiosk for instant check-in", booking.BookingCode);
        }

        // Auto-rearm test booking BK-TEST-CHECKIN for seamless repeated testing
        if (booking.BookingCode == "BK-TEST-CHECKIN")
        {
            booking.Status = BookingStatus.CONFIRMED;
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
                iotLocker.BusinessStatus = LockerBusinessStatus.RESERVED;
            }
            booking.CheckedInAt = null;
            booking.StartAt = DateTime.UtcNow;
            booking.EndAt = DateTime.UtcNow.AddHours(4);
        }
        else
        {
            if (booking.Status == BookingStatus.STORED)
                return ApiResponse<KioskCheckInResponse>.ErrorResponse("Hành lý đã được gửi trong tủ từ trước. Vui lòng chọn 'Lấy đồ (Check-out)'.");

            if (booking.Status != BookingStatus.CONFIRMED)
                return ApiResponse<KioskCheckInResponse>.ErrorResponse($"Trạng thái đơn không hợp lệ để check-in: {booking.Status}");
        }

        // Dynamic Allocation (Gán tủ động nếu chưa có)
        Locker? assignedLocker = booking.Locker;
        if (assignedLocker == null)
        {
            assignedLocker = await _context.Lockers
                .Where(l => l.StationId == request.StationId &&
                            (l.IoTDeviceId != null || l.LockerCode == "SIM-MQTT-01"))
                .FirstOrDefaultAsync();

            if (assignedLocker == null)
            {
                assignedLocker = await _context.Lockers
                    .Where(l => l.StationId == request.StationId &&
                                l.Size == booking.Size &&
                                l.BusinessStatus == LockerBusinessStatus.AVAILABLE &&
                                l.HealthStatus == LockerHealthStatus.HEALTHY)
                    .OrderBy(l => l.LockerCode)
                    .FirstOrDefaultAsync();
            }

            if (assignedLocker == null)
            {
                assignedLocker = await _context.Lockers
                    .FirstOrDefaultAsync(l => l.LockerCode == "SIM-MQTT-01" || l.IoTDeviceId != null);
            }

            if (assignedLocker == null)
                return ApiResponse<KioskCheckInResponse>.ErrorResponse($"Hiện trạm không còn ô tủ cỡ {booking.Size} khả dụng.");

            booking.LockerId = assignedLocker.Id;
            booking.Locker = assignedLocker;
            assignedLocker.BusinessStatus = LockerBusinessStatus.RESERVED;
        }

        booking.CheckedInAt = DateTime.UtcNow;

        // Find device code to send command
        var iotDevice = await _context.IoTDevices
            .FirstOrDefaultAsync(d => d.Id == assignedLocker.IoTDeviceId || d.StationId == request.StationId);
        var deviceCode = iotDevice?.DeviceCode ?? "SIM-MQTT-001";

        // Save state
        await _context.SaveChangesAsync();

        // Dispatch MQTT Unlock command
        await _mqttPublishService.PublishUnlockCommandAsync(deviceCode, assignedLocker.Id, assignedLocker.Size.ToString());

        // Dispatch SignalR
        await _notificationService.NotifyLockerUnlockedAsync(request.StationId, assignedLocker.Id, assignedLocker.LockerCode);

        return ApiResponse<KioskCheckInResponse>.SuccessResponse(new KioskCheckInResponse
        {
            BookingId = booking.Id,
            BookingCode = booking.BookingCode,
            LockerId = assignedLocker.Id,
            LockerCode = assignedLocker.LockerCode,
            LockerSize = assignedLocker.Size.ToString(),
            DoorState = assignedLocker.DoorState,
            Message = $"Xác thực thành công! Ô tủ {assignedLocker.LockerCode} đã mở. Vui lòng đặt đồ và đóng chặt cửa."
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
                .Include(b => b.PricingPolicy)
                .Include(b => b.Payments)
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
                .Include(b => b.PricingPolicy)
                .Include(b => b.Payments)
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

        var now = DateTime.UtcNow;

        // Auto-rearm test booking BK-TEST-CHECKOUT for seamless repeated testing
        if (booking.BookingCode == "BK-TEST-CHECKOUT")
        {
            booking.Status = BookingStatus.STORED;
            booking.StationId = request.StationId;
            var iotLocker = await _context.Lockers
                .FirstOrDefaultAsync(l => l.StationId == request.StationId && (l.IoTDeviceId != null || l.LockerCode == "SIM-MQTT-01"))
                ?? await _context.Lockers.FirstOrDefaultAsync(l => l.LockerCode == "SIM-MQTT-01" || l.IoTDeviceId != null);
            if (iotLocker != null)
            {
                booking.LockerId = iotLocker.Id;
                booking.Locker = iotLocker;
                iotLocker.BusinessStatus = LockerBusinessStatus.OCCUPIED;
            }
            booking.CheckedOutAt = null;
            booking.StartAt = now.AddHours(-2);
            booking.EndAt = now.AddHours(2);
            booking.IsOverdue = false;
        }
        else if (booking.BookingCode == "BK-TEST-OVERDUE")
        {
            booking.Status = BookingStatus.STORED;
            booking.StationId = request.StationId;
            if (booking.Locker == null)
            {
                var iotLocker = await _context.Lockers
                    .FirstOrDefaultAsync(l => l.StationId == request.StationId && (l.IoTDeviceId != null || l.LockerCode == "SIM-MQTT-01"))
                    ?? await _context.Lockers.FirstOrDefaultAsync(l => l.LockerCode == "SIM-MQTT-01" || l.IoTDeviceId != null);
                if (iotLocker != null)
                {
                    booking.LockerId = iotLocker.Id;
                    booking.Locker = iotLocker;
                    iotLocker.BusinessStatus = LockerBusinessStatus.OCCUPIED;
                }
            }

            // Kiểm tra xem người dùng vừa mới thanh toán phụ phí quá hạn cho BK-TEST-OVERDUE trong 3 phút gần đây chưa
            var recentPaidOverdue = booking.Payments
                .FirstOrDefault(p => p.Kind == PaymentKind.OVERDUE && p.Status == PaymentStatus.PAID && p.PaidAt >= now.AddMinutes(-3));

            if (recentPaidOverdue != null)
            {
                booking.IsOverdue = false;
                booking.OverdueSince = null;
                booking.EndAt = now.AddMinutes(15);
                // Đánh dấu PaidAt lùi về trước để lần test 777777 tiếp theo từ màn hình chính lại vào luồng quá hạn
                recentPaidOverdue.PaidAt = now.AddMinutes(-10);
            }
            else
            {
                booking.IsOverdue = true;
                booking.StartAt = now.AddHours(-6);
                booking.EndAt = now.AddHours(-2);
                booking.OverdueSince = booking.EndAt.AddMinutes(15);
            }
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

        var policy = booking.PricingPolicy ?? await _pricingService.GetActivePolicyAsync(booking.StationId, booking.Size);
        var graceMins = policy?.GracePeriodMinutes ?? 15;
        var graceEnd = booking.EndAt.AddMinutes(graceMins);

        if (now > graceEnd && !booking.IsOverdue)
        {
            booking.IsOverdue = true;
            booking.OverdueSince = graceEnd;
            booking.UpdatedAt = now;
            await _context.SaveChangesAsync();
        }

        // Check overdue (UC-T19)
        if (booking.IsOverdue)
        {
            var feePerHour = policy?.OverdueFeePerHour ?? 10000m;
            var overdueMinutes = Math.Max(1, (now - booking.EndAt).TotalMinutes - graceMins);
            var overdueHours = Math.Max(1, (int)Math.Ceiling(overdueMinutes / 60.0));
            var overdueFee = policy != null
                ? _pricingService.CalculateOverdueFee(booking, policy)
                : overdueHours * feePerHour;

            if (overdueFee <= 0) overdueFee = overdueHours * feePerHour;
            if (overdueFee < 2000m) overdueFee = 2000m;

            // Tạo sẵn mã VietQR PayOS cho Kiosk để khách quét ngay trên màn hình cảm ứng
            var payResponse = await CreateOverduePaymentInternalAsync(booking, overdueHours, feePerHour, overdueFee);

            return ApiResponse<KioskCheckOutResponse>.SuccessResponse(new KioskCheckOutResponse
            {
                BookingId = booking.Id,
                BookingCode = booking.BookingCode,
                LockerId = booking.Locker.Id,
                LockerCode = booking.Locker.LockerCode,
                LockerSize = booking.Locker.Size.ToString(),
                IsOverdue = true,
                OverdueHours = overdueHours,
                OverdueFeePerHour = feePerHour,
                OverdueFee = overdueFee,
                CheckoutUrl = payResponse?.PaymentUrl,
                QrCode = payResponse?.QrCode,
                AccountNumber = payResponse?.AccountNumber,
                AccountName = payResponse?.AccountName,
                Bin = payResponse?.Bin,
                OrderCode = payResponse?.OrderCode,
                PaymentExpiresAt = payResponse?.PaymentExpiresAt,
                Message = $"Đơn của bạn đã quá hạn {overdueHours} giờ. Vui lòng quét mã VietQR thanh toán phụ phí {overdueFee:N0}đ để mở tủ lấy đồ."
            }, "Đơn đặt bị quá hạn.");
        }

        booking.CheckedOutAt = now;
        var locker = booking.Locker;

        if (!booking.BookingCode.StartsWith("BK-TEST"))
        {
            booking.Status = BookingStatus.COMPLETED;
            booking.UpdatedAt = now;
            locker.BusinessStatus = LockerBusinessStatus.AVAILABLE;
            locker.UpdatedAt = now;

            if (booking.AccessCredential != null)
            {
                booking.AccessCredential.IsActive = false;
                booking.AccessCredential.RevokedAt = now;
                booking.AccessCredential.RevokeReason = "Kiosk checkout completed";
            }
        }

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

    public async Task<ApiResponse<KioskOverduePaymentResponse>> CreateOverduePaymentAsync(KioskOverduePaymentRequest request)
    {
        var booking = await FindBookingForKioskAsync(request);
        if (booking == null)
            return ApiResponse<KioskOverduePaymentResponse>.ErrorResponse("Không tìm thấy đơn đặt tủ cần thanh toán phụ phí.");

        var policy = booking.PricingPolicy ?? await _pricingService.GetActivePolicyAsync(booking.StationId, booking.Size);
        var graceMins = policy?.GracePeriodMinutes ?? 15;
        var feePerHour = policy?.OverdueFeePerHour ?? 10000m;
        var overdueMinutes = Math.Max(1, (DateTime.UtcNow - booking.EndAt).TotalMinutes - graceMins);
        var overdueHours = Math.Max(1, (int)Math.Ceiling(overdueMinutes / 60.0));
        var overdueFee = policy != null
            ? _pricingService.CalculateOverdueFee(booking, policy)
            : overdueHours * feePerHour;
        if (overdueFee <= 0) overdueFee = overdueHours * feePerHour;
        if (overdueFee < 2000m) overdueFee = 2000m;

        var res = await CreateOverduePaymentInternalAsync(booking, overdueHours, feePerHour, overdueFee);
        if (res == null)
            return ApiResponse<KioskOverduePaymentResponse>.ErrorResponse("Không thể khởi tạo mã VietQR thanh toán phụ phí.");

        return ApiResponse<KioskOverduePaymentResponse>.SuccessResponse(res, "Tạo mã VietQR thanh toán phụ phí quá hạn thành công.");
    }

    public async Task<ApiResponse<bool>> ConfirmOverduePaymentDemoAsync(KioskOverduePaymentRequest request)
    {
        var booking = await FindBookingForKioskAsync(request);
        if (booking == null)
            return ApiResponse<bool>.ErrorResponse("Không tìm thấy đơn đặt tủ.");

        var now = DateTime.UtcNow;
        var pendingPayment = booking.Payments
            .Where(p => p.Kind == PaymentKind.OVERDUE && p.Status == PaymentStatus.PENDING)
            .OrderByDescending(p => p.CreatedAt)
            .FirstOrDefault();

        if (pendingPayment != null)
        {
            pendingPayment.Status = PaymentStatus.PAID;
            pendingPayment.PaidAt = now;
            pendingPayment.WebhookReceivedAt = now;
        }
        else
        {
            var policy = booking.PricingPolicy ?? await _pricingService.GetActivePolicyAsync(booking.StationId, booking.Size);
            var feePerHour = policy?.OverdueFeePerHour ?? 10000m;
            _context.Payments.Add(new Payment
            {
                BookingId = booking.Id,
                Kind = PaymentKind.OVERDUE,
                Status = PaymentStatus.PAID,
                Amount = feePerHour * 2,
                OrderCode = long.Parse(now.ToString("yyMMddHHmm") + new Random().Next(1000, 9999)).ToString(),
                PaidAt = now,
                Gateway = "PAYOS"
            });
        }

        booking.IsOverdue = false;
        booking.OverdueSince = null;
        booking.EndAt = now.AddMinutes(15);
        booking.UpdatedAt = now;

        var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == booking.UserId);
        if (user != null && user.OverdueDebt > 0)
        {
            user.OverdueDebt = 0;
            user.UpdatedAt = now;
        }

        await _context.SaveChangesAsync();
        return ApiResponse<bool>.SuccessResponse(true, "Đã xác nhận thanh toán phụ phí quá hạn thành công.");
    }

    private async Task<Booking?> FindBookingForKioskAsync(KioskOverduePaymentRequest request)
    {
        if (request.BookingId.HasValue && request.BookingId.Value != Guid.Empty)
        {
            return await _context.Bookings
                .Include(b => b.PricingPolicy)
                .Include(b => b.Payments)
                .FirstOrDefaultAsync(b => b.Id == request.BookingId.Value);
        }

        if (!string.IsNullOrWhiteSpace(request.BookingCode))
        {
            var code = request.BookingCode.Trim().ToUpper();
            return await _context.Bookings
                .Include(b => b.PricingPolicy)
                .Include(b => b.Payments)
                .FirstOrDefaultAsync(b => b.BookingCode == code);
        }

        return null;
    }

    private async Task<KioskOverduePaymentResponse?> CreateOverduePaymentInternalAsync(
        Booking booking,
        int overdueHours,
        decimal feePerHour,
        decimal overdueFee)
    {
        try
        {
            var oldPendings = booking.Payments
                .Where(p => p.Kind == PaymentKind.OVERDUE && p.Status == PaymentStatus.PENDING)
                .ToList();
            foreach (var old in oldPendings)
            {
                old.Status = PaymentStatus.EXPIRED;
            }

            var expiresAt = DateTime.UtcNow.AddMinutes(10);
            var orderCode = long.Parse(DateTime.UtcNow.ToString("yyMMddHHmm") + new Random().Next(1000, 9999).ToString());
            var payment = new Payment
            {
                BookingId = booking.Id,
                Kind = PaymentKind.OVERDUE,
                Status = PaymentStatus.PENDING,
                Amount = overdueFee,
                OrderCode = orderCode.ToString(),
                ExpiresAt = expiresAt,
                Gateway = "PAYOS"
            };
            _context.Payments.Add(payment);
            await _context.SaveChangesAsync();

            var paymentResult = await _paymentGateway.CreatePaymentLinkAsync(
                orderCode, (int)overdueFee, $"Qua han {booking.BookingCode}", "", "");

            payment.PaymentLink = paymentResult.CheckoutUrl;
            payment.GatewayTxnId = paymentResult.PaymentLinkId;
            await _context.SaveChangesAsync();

            return new KioskOverduePaymentResponse
            {
                BookingId = booking.Id,
                BookingCode = booking.BookingCode,
                OverdueHours = overdueHours,
                OverdueFeePerHour = feePerHour,
                Amount = overdueFee,
                PaymentUrl = paymentResult.CheckoutUrl,
                PaymentExpiresAt = expiresAt,
                QrCode = paymentResult.QrCode,
                AccountNumber = paymentResult.AccountNumber,
                AccountName = paymentResult.AccountName,
                Bin = paymentResult.Bin,
                OrderCode = orderCode
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating overdue payment for booking {BookingCode} at Kiosk", booking.BookingCode);
            return null;
        }
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

        var response = new KioskStationStatusResponse
        {
            StationId = station.Id,
            StationName = station.Name,
            StationAddress = station.Address,
            ConnectivityStatus = "ONLINE",
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
