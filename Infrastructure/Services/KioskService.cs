namespace Infrastructure.Services;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Application.DTOs.Common;
using Application.DTOs.Kiosk;
using Application.DTOs.Booking;
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
    private readonly IBookingService? _bookingService;
    private readonly ILogger<KioskService> _logger;

    public KioskService(
        ISmartLockerDbContext context,
        IMqttPublishService mqttPublishService,
        ILockerNotificationService notificationService,
        IPricingService pricingService,
        ILogger<KioskService> logger, IBookingService? bookingService = null)
    {
        _context = context;
        _mqttPublishService = mqttPublishService;
        _notificationService = notificationService;
        _pricingService = pricingService;
        _logger = logger;
        _bookingService = bookingService;
    }

    private static string FeeSessionHash(string? type, string payload, string token) =>
        OnlineTicket.HashPin($"CHECKOUT_FEE:{type?.ToUpperInvariant()}:{OnlineTicket.HashPin(payload.Trim())}:{token}");

    public async Task<ApiResponse<CreateBookingResponse>> PayOverdueAsync(KioskFeePaymentRequest request)
    {
        if (_bookingService == null || request.SessionToken?.Length != 64 || string.IsNullOrWhiteSpace(request.Payload))
            return ApiResponse<CreateBookingResponse>.ErrorResponse("Phiên thanh toán Kiosk không hợp lệ.");
        var hash = FeeSessionHash(request.CredentialType, request.Payload, request.SessionToken);
        var booking = await _context.Bookings.Include(b => b.Locker).FirstOrDefaultAsync(b => b.Id == request.BookingId
            && b.StationId == request.StationId && b.Status == BookingStatus.STORED && b.KioskSessionHash == hash
            && b.KioskSessionExpiresAt > DateTime.UtcNow && b.AccessCredential != null
            && b.AccessCredential.IsActive && b.AccessCredential.RevokedAt == null);
        if (booking?.Locker == null) return ApiResponse<CreateBookingResponse>.ErrorResponse("Phiên thanh toán đã hết hạn. Vui lòng xác thực lại.");
        var online = await _context.IoTDevices.AnyAsync(d => d.Id == booking.Locker.IoTDeviceId && d.StationId == request.StationId
            && d.ConnectivityStatus == "ONLINE" && d.LastSeenAt >= DateTime.UtcNow.AddSeconds(-60));
        if (!online) return ApiResponse<CreateBookingResponse>.ErrorResponse("Thiết bị tại trạm chưa kết nối. Vui lòng liên hệ nhân viên trước khi thanh toán.");
        return await _bookingService.PayOverdueFeeAsync(booking.UserId, booking.Id);
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
                    && l.DoorState == "CLOSED"
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
            || assignedLocker.DoorState != "CLOSED"
            || assignedLocker.BusinessStatus is not (LockerBusinessStatus.RESERVED or LockerBusinessStatus.AVAILABLE))
            return ApiResponse<KioskCheckInResponse>.ErrorResponse("Ô tủ được gán không khả dụng.");
        if (await _context.Bookings.AnyAsync(b => b.Id != booking.Id && b.LockerId == assignedLocker.Id
            && (b.Status == BookingStatus.PENDING_PAYMENT || b.Status == BookingStatus.CONFIRMED || b.Status == BookingStatus.STORED)))
            return ApiResponse<KioskCheckInResponse>.ErrorResponse("Ô tủ đang được sử dụng bởi đơn khác. Vui lòng liên hệ nhân viên.");
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
        var commandId = Guid.NewGuid().ToString("N");
        return await _context.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            if (_context.Database.IsRelational() && _context is DbContext tracked) tracked.ChangeTracker.Clear();
            await using var transaction = _context.Database.IsRelational()
                ? await _context.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable) : null;
            var result = await CheckOutCoreAsync(request, commandId);
            if (result.Success && transaction != null) await transaction.CommitAsync();
            return result;
        });
    }

    private async Task<ApiResponse<KioskCheckOutResponse>> CheckOutCoreAsync(KioskCheckOutRequest request, string commandId)
    {
        if (request.StationId == Guid.Empty || string.IsNullOrWhiteSpace(request.Payload))
            return ApiResponse<KioskCheckOutResponse>.ErrorResponse("Dữ liệu xác thực không hợp lệ.");

        var booking = await FindAuthorizedBookingAsync(request.StationId, request.CredentialType, request.Payload.Trim());
        if (booking == null && string.Equals(request.CredentialType, "QR", StringComparison.OrdinalIgnoreCase) && request.SessionToken?.Length == 64)
        {
            var sessionHash = FeeSessionHash(request.CredentialType, request.Payload, request.SessionToken);
            booking = await _context.Bookings.Include(b => b.AccessCredential).Include(b => b.Locker).Include(b => b.Payments)
                .FirstOrDefaultAsync(b => b.StationId == request.StationId && b.Status == BookingStatus.STORED
                    && b.KioskSessionHash == sessionHash && b.KioskSessionExpiresAt > DateTime.UtcNow
                    && b.AccessCredential != null && b.AccessCredential.IsActive && b.AccessCredential.RevokedAt == null);
        }

        if (booking == null)
            return ApiResponse<KioskCheckOutResponse>.ErrorResponse("Không tìm thấy thông tin đơn đặt chỗ tương ứng.");

        if (booking.Status != BookingStatus.STORED)
            return ApiResponse<KioskCheckOutResponse>.ErrorResponse("Chỉ có thể lấy đồ với đơn đang lưu trữ.");

        if (booking.Locker == null)
            return ApiResponse<KioskCheckOutResponse>.ErrorResponse("Không tìm thấy thông tin ô tủ của đơn đặt.");

        if (booking.Locker.StationId != request.StationId || booking.Locker.BusinessStatus != LockerBusinessStatus.OCCUPIED)
            return ApiResponse<KioskCheckOutResponse>.ErrorResponse("Ô tủ không khớp trạng thái lưu trữ.");
        if (booking.CheckedOutAt > DateTime.UtcNow.AddSeconds(-30))
            return ApiResponse<KioskCheckOutResponse>.ErrorResponse("Đang chờ thiết bị phản hồi. Không gửi lại lệnh mở tủ.");

        // Check overdue
        var now = DateTime.UtcNow;
        var policy = booking.PricingPolicyId.HasValue
            ? await _context.PricingPolicies.FindAsync(booking.PricingPolicyId.Value)
            : await _pricingService.GetActivePolicyAsync(booking.StationId, booking.Size);
        if (policy == null)
            return ApiResponse<KioskCheckOutResponse>.ErrorResponse("Chưa có chính sách tính phí cho đơn.");
        var paidOverdue = booking.Payments.Where(p => p.Kind == PaymentKind.OVERDUE && p.Status == PaymentStatus.PAID).Sum(p => p.Amount);
        var outstanding = Math.Max(0, _pricingService.CalculateOverdueFee(booking, policy) - paidOverdue);
        if (outstanding > 0)
        {
            var feeToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            booking.KioskSessionHash = FeeSessionHash(request.CredentialType, request.Payload, feeToken);
            booking.KioskSessionExpiresAt = DateTime.UtcNow.AddMinutes(15);
            await _context.SaveChangesAsync();
            return ApiResponse<KioskCheckOutResponse>.SuccessResponse(new KioskCheckOutResponse
            {
                SessionToken = feeToken,
                BookingId = booking.Id,
                BookingCode = booking.BookingCode,
                LockerId = booking.Locker.Id,
                LockerCode = booking.Locker.LockerCode,
                LockerSize = booking.Locker.Size.ToString(),
                IsOverdue = true,
                OverdueFee = outstanding,
                Message = "Đơn của bạn đã quá hạn thuê. Vui lòng thanh toán phụ phí trước khi lấy đồ."
            }, "Đơn đặt bị quá hạn.");
        }

        booking.CheckedOutAt = DateTime.UtcNow;
        var locker = booking.Locker;

        // Find device
        var iotDevice = await _context.IoTDevices
            .FirstOrDefaultAsync(d => d.Id == locker.IoTDeviceId && d.StationId == request.StationId);
        if (iotDevice == null)
            return ApiResponse<KioskCheckOutResponse>.ErrorResponse("Ô tủ chưa được ánh xạ thiết bị tại trạm.");
        if (iotDevice.ConnectivityStatus != "ONLINE" || iotDevice.LastSeenAt == null || iotDevice.LastSeenAt < DateTime.UtcNow.AddSeconds(-60))
            return ApiResponse<KioskCheckOutResponse>.ErrorResponse("Thiết bị tại ô tủ chưa kết nối. Vui lòng liên hệ nhân viên.");
        var deviceCode = iotDevice.DeviceCode;
        var sessionToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        booking.KioskSessionHash = OnlineTicket.HashPin(sessionToken);
        booking.KioskSessionExpiresAt = DateTime.UtcNow.AddMinutes(15);

        await _context.SaveChangesAsync();

        // Dispatch MQTT Unlock command
        if (!await _mqttPublishService.PublishUnlockCommandAsync(deviceCode, locker.Id, locker.LockerCode, commandId))
        {
            booking.CheckedOutAt = null;
            await _context.SaveChangesAsync();
            return ApiResponse<KioskCheckOutResponse>.ErrorResponse("Không gửi được lệnh mở tủ. Vui lòng thử lại hoặc liên hệ nhân viên.");
        }

        // Dispatch SignalR
        try { await _notificationService.NotifyLockerUnlockedAsync(request.StationId, locker.Id, locker.LockerCode); }
        catch (Exception ex) { _logger.LogWarning("Unlock notification failed ({ErrorType}); operation remains pending sensor confirmation.", ex.GetType().Name); }

        return ApiResponse<KioskCheckOutResponse>.SuccessResponse(new KioskCheckOutResponse
        {
            SessionToken = sessionToken,
            BookingId = booking.Id,
            BookingCode = booking.BookingCode,
            LockerId = locker.Id,
            LockerCode = locker.LockerCode,
            LockerSize = locker.Size.ToString(),
            DoorState = locker.DoorState,
            IsOverdue = false,
            Message = $"Mở khóa thành công! Đã gửi lệnh mở ô tủ {locker.LockerCode}. Vui lòng lấy toàn bộ hành lý và đóng chặt cửa."
        }, "Check-out thành công");
    }

    public Task<ApiResponse<bool>> ManualConfirmAsync(KioskManualConfirmRequest request) =>
        Task.FromResult(ApiResponse<bool>.ErrorResponse("Chưa có xác nhận từ cảm biến. Vui lòng liên hệ nhân viên để kiểm tra tủ."));

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
