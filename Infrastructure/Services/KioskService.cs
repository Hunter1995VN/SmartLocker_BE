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
    private readonly ILogger<KioskService> _logger;

    public KioskService(
        ISmartLockerDbContext context,
        IMqttPublishService mqttPublishService,
        ILockerNotificationService notificationService,
        IPricingService pricingService,
        ILogger<KioskService> logger)
    {
        _context = context;
        _mqttPublishService = mqttPublishService;
        _notificationService = notificationService;
        _pricingService = pricingService;
        _logger = logger;
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
        await using var transaction = _context.Database.IsRelational()
            ? await _context.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable) : null;
        var result = await CheckInCoreAsync(request);
        if (result.Success && transaction != null) await transaction.CommitAsync();
        return result;
    }

    private async Task<ApiResponse<KioskCheckInResponse>> CheckInCoreAsync(KioskCheckInRequest request)
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
                    && l.HealthStatus == LockerHealthStatus.HEALTHY && l.IoTDeviceId != null)
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
        var deviceCode = iotDevice.DeviceCode;
        var sessionToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        booking.KioskSessionHash = OnlineTicket.HashPin(sessionToken);
        booking.KioskSessionExpiresAt = DateTime.UtcNow.AddMinutes(15);

        // Save state
        await _context.SaveChangesAsync();

        // Dispatch MQTT Unlock command
        if (!await _mqttPublishService.PublishUnlockCommandAsync(deviceCode, assignedLocker.Id, assignedLocker.LockerCode))
        {
            booking.CheckedInAt = null;
            await _context.SaveChangesAsync();
            return ApiResponse<KioskCheckInResponse>.ErrorResponse("Không gửi được lệnh mở tủ. Vui lòng thử lại hoặc liên hệ nhân viên.");
        }

        // Dispatch SignalR
        await _notificationService.NotifyLockerUnlockedAsync(request.StationId, assignedLocker.Id, assignedLocker.LockerCode);

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
        await using var transaction = _context.Database.IsRelational()
            ? await _context.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable) : null;
        var result = await CheckOutCoreAsync(request);
        if (result.Success && transaction != null) await transaction.CommitAsync();
        return result;
    }

    private async Task<ApiResponse<KioskCheckOutResponse>> CheckOutCoreAsync(KioskCheckOutRequest request)
    {
        if (request.StationId == Guid.Empty || string.IsNullOrWhiteSpace(request.Payload))
            return ApiResponse<KioskCheckOutResponse>.ErrorResponse("Dữ liệu xác thực không hợp lệ.");

        PickupDelegation? delegation = null;
        var booking = await FindAuthorizedBookingAsync(request.StationId, request.CredentialType, request.Payload.Trim());
        if (booking == null && request.CredentialType == "ACCESS_CODE" && request.Payload.Length == 8
            && request.Payload.All(c => c >= '0' && c <= '9'))
        {
            var hash = OnlineTicket.HashPin(request.Payload);
            var grants = await _context.PickupDelegations.Include(x => x.Booking).ThenInclude(b => b.Locker)
                .Include(x => x.Booking).ThenInclude(b => b.Payments)
                .Where(x => x.Booking.StationId == request.StationId && x.Booking.Status == BookingStatus.STORED
                    && x.PinHash == hash && x.UsedAt == null && x.RevokedAt == null && x.ExpiresAt > DateTime.UtcNow)
                .Take(2).ToListAsync();
            if (grants.Count == 1) { delegation = grants[0]; booking = delegation.Booking; }
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
            return ApiResponse<KioskCheckOutResponse>.SuccessResponse(new KioskCheckOutResponse
            {
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

        if (delegation != null)
        {
            delegation.UsedAt = DateTime.UtcNow;
            _context.AuditLogs.Add(new AuditLog { Action = "DELEGATE_PICKUP", TargetEntity = "Booking",
                TargetId = booking.Id.ToString(), Details = "Delegate " + delegation.Id + ": " + delegation.Contact });
        }
        booking.CheckedOutAt = DateTime.UtcNow;
        var locker = booking.Locker;

        // Find device
        var iotDevice = await _context.IoTDevices
            .FirstOrDefaultAsync(d => d.Id == locker.IoTDeviceId && d.StationId == request.StationId);
        if (iotDevice == null)
            return ApiResponse<KioskCheckOutResponse>.ErrorResponse("Ô tủ chưa được ánh xạ thiết bị tại trạm.");
        var deviceCode = iotDevice.DeviceCode;
        var sessionToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        booking.KioskSessionHash = OnlineTicket.HashPin(sessionToken);
        booking.KioskSessionExpiresAt = DateTime.UtcNow.AddMinutes(15);

        await _context.SaveChangesAsync();

        // Dispatch MQTT Unlock command
        if (!await _mqttPublishService.PublishUnlockCommandAsync(deviceCode, locker.Id, locker.LockerCode))
        {
            if (delegation != null) delegation.UsedAt = null;
            booking.CheckedOutAt = null;
            await _context.SaveChangesAsync();
            return ApiResponse<KioskCheckOutResponse>.ErrorResponse("Không gửi được lệnh mở tủ. Vui lòng thử lại hoặc liên hệ nhân viên.");
        }

        // Dispatch SignalR
        await _notificationService.NotifyLockerUnlockedAsync(request.StationId, locker.Id, locker.LockerCode);

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
