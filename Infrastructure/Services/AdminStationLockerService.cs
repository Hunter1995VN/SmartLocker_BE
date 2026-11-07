namespace Infrastructure.Services;

using System.Collections.Concurrent;
using Application.DTOs.Admin;
using Application.DTOs.Common;
using Application.Interfaces;
using Domain.Entities;
using Domain.Enums;
using Microsoft.EntityFrameworkCore;

public class AdminStationLockerService : IAdminStationLockerService
{
    private readonly ISmartLockerDbContext _db;
    private readonly IAuditLogService _auditLog;
    private readonly ILockerNotificationService? _notifications;
    private readonly IMqttPublishService? _mqtt;

    public AdminStationLockerService(
        ISmartLockerDbContext db,
        IAuditLogService auditLog,
        ILockerNotificationService? notifications = null,
        IMqttPublishService? mqtt = null)
    {
        _db = db;
        _auditLog = auditLog;
        _notifications = notifications;
        _mqtt = mqtt;
    }

    public async Task<ApiResponse<List<StationAdminDetailDto>>> GetStationsAsync(string? search)
    {
        var query = _db.Stations.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            query = query.Where(s => s.Name.Contains(search) || s.Address.Contains(search));
        }

        var stations = await query.ToListAsync();
        var stationIds = stations.Select(s => s.Id).ToList();
        var policies = await _db.PricingPolicies.AsNoTracking()
            .Where(p => stationIds.Contains(p.StationId) && p.EffectiveTo == null)
            .ToListAsync();
        var lockers = await _db.Lockers.AsNoTracking()
            .Where(l => stationIds.Contains(l.StationId))
            .ToListAsync();
        var result = new List<StationAdminDetailDto>();

        foreach (var s in stations)
        {
            var dto = MapStationToAdminDto(s,
                policies.Where(p => p.StationId == s.Id).ToList(),
                lockers.Where(l => l.StationId == s.Id).ToList());
            result.Add(dto);
        }

        return ApiResponse<List<StationAdminDetailDto>>.SuccessResponse(result);
    }

    public async Task<ApiResponse<StationAdminDetailDto>> GetStationByIdAsync(Guid id)
    {
        var station = await _db.Stations.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id);
        if (station == null)
            return ApiResponse<StationAdminDetailDto>.ErrorResponse("Không tìm thấy trạm tủ");

        var dto = await MapStationToAdminDtoAsync(station);
        return ApiResponse<StationAdminDetailDto>.SuccessResponse(dto);
    }

    public async Task<ApiResponse<StationAdminDetailDto>> CreateStationAsync(CreateStationRequest request, Guid adminUserId)
    {
        TimeOnly.TryParse(request.OpensAt, out var opensAt);
        TimeOnly.TryParse(request.ClosesAt, out var closesAt);

        var station = new Station
        {
            Id = Guid.NewGuid(),
            Name = request.Name,
            Address = request.Address,
            Latitude = request.Latitude,
            Longitude = request.Longitude,
            Status = StationStatus.ACTIVE,
            OpensAt = opensAt,
            ClosesAt = closesAt,
            ContactPhone = request.ContactPhone,
            TotalS = request.TotalS,
            TotalM = request.TotalM,
            TotalL = request.TotalL,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _db.Stations.Add(station);

        // Auto-provision master IoT board (UC-A02)
        var deviceCode = $"DEV-{station.Id.ToString()[..8].ToUpperInvariant()}";
        var iotDevice = new IoTDevice
        {
            Id = Guid.NewGuid(),
            StationId = station.Id,
            DeviceCode = deviceCode,
            DeviceType = "MASTER_BOARD",
            FirmwareVersion = "1.0.0",
            ConnectivityStatus = "ONLINE",
            WifiRssi = -55,
            UptimeSeconds = 0,
            LastSeenAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _db.IoTDevices.Add(iotDevice);

        // Auto-generate Lockers (S, M, L) mapped to IoT Device and GPIO pins (UC-A02)
        int lockerIndex = 1;
        var lockers = new List<Locker>();

        for (int i = 0; i < request.TotalS; i++)
        {
            lockers.Add(new Locker
            {
                Id = Guid.NewGuid(),
                StationId = station.Id,
                IoTDeviceId = iotDevice.Id,
                GpioPin = 26,
                LockerCode = $"S-{lockerIndex:D2}",
                Size = LockerSize.S,
                BusinessStatus = LockerBusinessStatus.AVAILABLE,
                HealthStatus = LockerHealthStatus.HEALTHY,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
            lockerIndex++;
        }

        for (int i = 0; i < request.TotalM; i++)
        {
            lockers.Add(new Locker
            {
                Id = Guid.NewGuid(),
                StationId = station.Id,
                IoTDeviceId = iotDevice.Id,
                GpioPin = 25,
                LockerCode = $"M-{lockerIndex:D2}",
                Size = LockerSize.M,
                BusinessStatus = LockerBusinessStatus.AVAILABLE,
                HealthStatus = LockerHealthStatus.HEALTHY,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
            lockerIndex++;
        }

        for (int i = 0; i < request.TotalL; i++)
        {
            lockers.Add(new Locker
            {
                Id = Guid.NewGuid(),
                StationId = station.Id,
                IoTDeviceId = iotDevice.Id,
                GpioPin = 33,
                LockerCode = $"L-{lockerIndex:D2}",
                Size = LockerSize.L,
                BusinessStatus = LockerBusinessStatus.AVAILABLE,
                HealthStatus = LockerHealthStatus.HEALTHY,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
            lockerIndex++;
        }

        _db.Lockers.AddRange(lockers);

        // Add Pricing Policies
        var now = DateTime.UtcNow;
        var policies = new List<PricingPolicy>
        {
            new PricingPolicy
            {
                Id = Guid.NewGuid(),
                StationId = station.Id,
                Size = LockerSize.S,
                PricePerBlock = request.PriceSPerBlock,
                BlockHours = 1,
                OverdueFeePerHour = request.PriceSPerBlock * 1.5m,
                EffectiveFrom = now,
                CreatedBy = adminUserId,
                CreatedAt = now
            },
            new PricingPolicy
            {
                Id = Guid.NewGuid(),
                StationId = station.Id,
                Size = LockerSize.M,
                PricePerBlock = request.PriceMPerBlock,
                BlockHours = 1,
                OverdueFeePerHour = request.PriceMPerBlock * 1.5m,
                EffectiveFrom = now,
                CreatedBy = adminUserId,
                CreatedAt = now
            },
            new PricingPolicy
            {
                Id = Guid.NewGuid(),
                StationId = station.Id,
                Size = LockerSize.L,
                PricePerBlock = request.PriceLPerBlock,
                BlockHours = 1,
                OverdueFeePerHour = request.PriceLPerBlock * 1.5m,
                EffectiveFrom = now,
                CreatedBy = adminUserId,
                CreatedAt = now
            }
        };

        _db.PricingPolicies.AddRange(policies);

        await _db.SaveChangesAsync();

        await _auditLog.LogAsync(adminUserId, null, "ADMIN", "CREATE_STATION", "Station", station.Id.ToString(), $"Tạo trạm mới {station.Name} kèm {lockers.Count} ô tủ");

        var dto = await MapStationToAdminDtoAsync(station);
        return ApiResponse<StationAdminDetailDto>.SuccessResponse(dto, "Tạo trạm tủ mới thành công");
    }

    public async Task<ApiResponse<StationAdminDetailDto>> UpdateStationAsync(Guid id, UpdateStationRequest request, Guid adminUserId)
    {
        var station = await _db.Stations.FirstOrDefaultAsync(s => s.Id == id);
        if (station == null)
            return ApiResponse<StationAdminDetailDto>.ErrorResponse("Không tìm thấy trạm tủ");

        TimeOnly.TryParse(request.OpensAt, out var opensAt);
        TimeOnly.TryParse(request.ClosesAt, out var closesAt);

        station.Name = request.Name;
        station.Address = request.Address;
        station.Latitude = request.Latitude;
        station.Longitude = request.Longitude;
        station.Status = request.Status;
        station.OpensAt = opensAt;
        station.ClosesAt = closesAt;
        station.ContactPhone = request.ContactPhone;
        station.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();

        await _auditLog.LogAsync(adminUserId, null, "ADMIN", "UPDATE_STATION", "Station", station.Id.ToString(), $"Cập nhật thông tin trạm {station.Name}");

        var dto = await MapStationToAdminDtoAsync(station);
        return ApiResponse<StationAdminDetailDto>.SuccessResponse(dto, "Cập nhật trạm tủ thành công");
    }

    public async Task<ApiResponse<bool>> DeleteStationAsync(Guid id, Guid adminUserId)
    {
        var station = await _db.Stations.FirstOrDefaultAsync(s => s.Id == id);
        if (station == null)
            return ApiResponse<bool>.ErrorResponse("Không tìm thấy trạm tủ");

        station.Status = StationStatus.INACTIVE;
        station.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();

        await _auditLog.LogAsync(adminUserId, null, "ADMIN", "DISABLE_STATION", "Station", station.Id.ToString(), $"Ngưng hoạt động trạm {station.Name}");

        return ApiResponse<bool>.SuccessResponse(true, "Đã ngưng hoạt động trạm tủ");
    }

    /// <summary>
    /// UC-A03 & UC-A04: Cập nhật biểu giá thuê, mức phí phạt quá hạn và tỷ lệ hoàn tiền khi hủy đơn
    /// </summary>
    public async Task<ApiResponse<bool>> UpdatePricingPolicyAsync(Guid stationId, UpdatePricingPolicyRequest request, Guid adminUserId)
    {
        var station = await _db.Stations.FirstOrDefaultAsync(s => s.Id == stationId);
        if (station == null)
            return ApiResponse<bool>.ErrorResponse("Không tìm thấy trạm tủ");

        // Expire existing policy
        var existingPolicy = await _db.PricingPolicies
            .FirstOrDefaultAsync(p => p.StationId == stationId && p.Size == request.Size && p.EffectiveTo == null);

        var now = DateTime.UtcNow;
        if (existingPolicy != null)
        {
            existingPolicy.EffectiveTo = now;
        }

        var earlyRate = Math.Clamp(request.RefundRateEarly, 0m, 1m);
        var lateRate = Math.Clamp(request.RefundRateLate, 0m, 1m);
        var cutoffHours = request.RefundCutoffHours > 0 ? request.RefundCutoffHours : (short)2;
        var noShowMinutes = request.NoShowWindowMinutes > 0 ? request.NoShowWindowMinutes : (short)30;

        var newPolicy = new PricingPolicy
        {
            Id = Guid.NewGuid(),
            StationId = stationId,
            Size = request.Size,
            PricePerBlock = request.PricePerBlock,
            BlockHours = request.BlockHours > 0 ? request.BlockHours : (short)1,
            OverdueFeePerHour = request.OverdueFeePerHour,
            GracePeriodMinutes = request.GracePeriodMinutes >= 0 ? request.GracePeriodMinutes : (short)15,
            RefundCutoffHours = cutoffHours,
            RefundRateEarly = earlyRate,
            RefundRateLate = lateRate,
            NoShowWindowMinutes = noShowMinutes,
            MinHours = request.MinHours > 0 ? request.MinHours : (short)1,
            MaxHours = request.MaxHours >= request.MinHours ? request.MaxHours : (short)72,
            EffectiveFrom = now,
            CreatedBy = adminUserId,
            CreatedAt = now
        };

        _db.PricingPolicies.Add(newPolicy);
        await _db.SaveChangesAsync();

        await _auditLog.LogAsync(
            adminUserId,
            null,
            "ADMIN",
            "UPDATE_PRICING",
            "PricingPolicy",
            newPolicy.Id.ToString(),
            $"Cập nhật chính sách giá & phạt/hoàn hủy cỡ {request.Size} tại trạm {station.Name}: Giá={request.PricePerBlock:N0}đ, Phạt quá hạn={request.OverdueFeePerHour:N0}đ/h (Ân hạn {newPolicy.GracePeriodMinutes}p), Hoàn hủy trước {cutoffHours}h={earlyRate * 100:N0}%, sát giờ={lateRate * 100:N0}%"
        );

        return ApiResponse<bool>.SuccessResponse(true, "Cập nhật biểu giá, phí phạt quá hạn và tỷ lệ hoàn hủy thành công");
    }

    public Task<List<Application.DTOs.Kiosk.KioskSizeReservationDto>> GetStationReservationsAsync(Guid stationId) =>
        StationReservationCounts.ReadAsync(_db, stationId);

    public async Task<ApiResponse<List<LockerGridItemDto>>> GetStationLockersGridAsync(Guid stationId)
    {
        var lockers = await _db.Lockers.AsNoTracking()
            .Where(l => l.StationId == stationId)
            .OrderBy(l => l.LockerCode)
            .ToListAsync();

        var activeStatuses = new[] { BookingStatus.CONFIRMED, BookingStatus.STORED };
        var activeBookings = await _db.Bookings.AsNoTracking()
            .Include(b => b.User)
            .Where(b => b.StationId == stationId && activeStatuses.Contains(b.Status))
            .ToListAsync();

        var now = DateTime.UtcNow;
        var devices = await _db.IoTDevices.AsNoTracking().Where(d => d.StationId == stationId).ToDictionaryAsync(d => d.Id);
        var grid = lockers.Select(l =>
        {
            var booking = activeBookings.FirstOrDefault(b => b.LockerId == l.Id);
            var device = l.IoTDeviceId.HasValue ? devices.GetValueOrDefault(l.IoTDeviceId.Value) : null;
            ActiveBookingBriefDto? bookingBrief = null;
            if (booking != null)
            {
                bookingBrief = new ActiveBookingBriefDto
                {
                    BookingId = booking.Id,
                    BookingCode = booking.BookingCode,
                    CustomerName = booking.User.FullName,
                    CustomerPhone = booking.User.Phone,
                    StartAt = booking.StartAt,
                    EndAt = booking.EndAt,
                    IsOverdue = booking.IsOverdue
                };
            }

            return new LockerGridItemDto
            {
                Id = l.Id,
                StationId = l.StationId,
                LockerCode = l.LockerCode,
                Size = l.Size.ToString(),
                GpioPin = l.GpioPin,
                BusinessStatus = l.BusinessStatus.ToString(),
                HealthStatus = l.HealthStatus.ToString(),
                DoorState = l.DoorState,
                LastDoorEventAt = l.LastDoorEventAt,
                IoTDeviceId = l.IoTDeviceId,
                DeviceCode = device?.DeviceCode,
                FirmwareVersion = device?.FirmwareVersion,
                LastSeenAt = device?.LastSeenAt,
                ConnectivityStatus = device == null ? "UNMAPPED" :
                    device.LastSeenAt == null || device.LastSeenAt < now.AddSeconds(-60) || device.LastSeenAt > now.AddSeconds(10)
                        ? "OFFLINE" : device.ConnectivityStatus,
                Notes = l.Notes,
                CurrentBooking = bookingBrief
            };
        }).ToList();

        return ApiResponse<List<LockerGridItemDto>>.SuccessResponse(grid);
    }

    public Task<ApiResponse<bool>> UpdateLockerStatusAsync(Guid lockerId, UpdateLockerStatusRequest request, Guid adminUserId) =>
        _db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            if (_db.Database.IsRelational() && _db is DbContext tracked) tracked.ChangeTracker.Clear();
            await using var transaction = _db.Database.IsRelational()
                ? await _db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable) : null;
            var result = await UpdateLockerStatusCoreAsync(lockerId, request, adminUserId);
            if (result.Success && transaction != null) await transaction.CommitAsync();
            if (result.Success && _notifications != null)
            {
                // Delivery cannot roll back the committed operation; polling recovers a dropped event.
                try { var stationId = await _db.Lockers.Where(l => l.Id == lockerId).Select(l => l.StationId).SingleAsync();
                    await _notifications.NotifyLockerStatusChangedAsync(stationId, lockerId); } catch { }
            }
            return result;
        });

    private async Task<ApiResponse<bool>> UpdateLockerStatusCoreAsync(Guid lockerId, UpdateLockerStatusRequest request, Guid adminUserId)
    {
        var locker = await _db.Lockers.FirstOrDefaultAsync(l => l.Id == lockerId);
        if (locker == null)
            return ApiResponse<bool>.ErrorResponse("Không tìm thấy ô tủ");

        if (!Enum.IsDefined(request.BusinessStatus) || !Enum.IsDefined(request.HealthStatus))
            return ApiResponse<bool>.ErrorResponse("Trạng thái không hợp lệ");
        if (request.BusinessStatus is LockerBusinessStatus.RESERVED or LockerBusinessStatus.OCCUPIED)
            return ApiResponse<bool>.ErrorResponse("Trạng thái giữ chỗ/lưu đồ chỉ được cập nhật theo luồng đơn hàng");
        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Trim().Length < 5)
            return ApiResponse<bool>.ErrorResponse("Cần lý do tối thiểu 5 ký tự");
        if (await _db.Bookings.AnyAsync(b => b.LockerId == lockerId &&
            (b.Status == BookingStatus.CONFIRMED || b.Status == BookingStatus.STORED || b.Status == BookingStatus.PENDING_PAYMENT)))
            return ApiResponse<bool>.ErrorResponse("Ô tủ đang có đơn hoạt động; xử lý qua luồng sự cố của đơn");
        if (request.BusinessStatus == LockerBusinessStatus.AVAILABLE &&
            (request.HealthStatus != LockerHealthStatus.HEALTHY || locker.DoorState != "CLOSED"))
            return ApiResponse<bool>.ErrorResponse("Chỉ đưa tủ khỏe và đã đóng cửa về khả dụng");
        var oldStatus = locker.BusinessStatus;
        locker.BusinessStatus = request.BusinessStatus;
        locker.HealthStatus = request.HealthStatus;
        if (!string.IsNullOrWhiteSpace(request.Reason))
        {
            locker.Notes = request.Reason;
        }
        locker.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();

        await _auditLog.LogAsync(adminUserId, null, "ADMIN", "UPDATE_LOCKER_STATUS", "Locker", locker.Id.ToString(), $"Đổi trạng thái tủ {locker.LockerCode} từ {oldStatus} sang {request.BusinessStatus}. Lý do: {request.Reason}");

        return ApiResponse<bool>.SuccessResponse(true, "Cập nhật trạng thái tủ thành công");
    }

    public async Task<ApiResponse<bool>> CreateMaintenanceTicketAsync(CreateMaintenanceTicketRequest request, Guid reportedByUserId)
    {
        Guid stationId = Guid.Empty;
        if (request.LockerId.HasValue)
        {
            var locker = await _db.Lockers.FirstOrDefaultAsync(l => l.Id == request.LockerId.Value);
            if (locker != null)
            {
                stationId = locker.StationId;
                locker.BusinessStatus = LockerBusinessStatus.MAINTENANCE;
                locker.HealthStatus = LockerHealthStatus.WARNING;
            }
        }

        var ticket = new MaintenanceTicket
        {
            Id = Guid.NewGuid(),
            TicketCode = $"MT-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..4].ToUpper()}",
            StationId = stationId,
            LockerId = request.LockerId,
            IoTDeviceId = request.IoTDeviceId,
            ReportedByUserId = reportedByUserId,
            AssignedStaffId = request.AssignedStaffId,
            IssueDescription = request.IssueDescription,
            Status = MaintenanceStatus.OPEN,
            Priority = request.Priority,
            CreatedAt = DateTime.UtcNow
        };

        _db.MaintenanceTickets.Add(ticket);
        await _db.SaveChangesAsync();

        await _auditLog.LogAsync(reportedByUserId, null, "STAFF", "CREATE_MAINTENANCE_TICKET", "MaintenanceTicket", ticket.Id.ToString(), $"Tạo phiếu bảo trì {ticket.TicketCode}: {request.IssueDescription}");

        return ApiResponse<bool>.SuccessResponse(true, "Tạo phiếu bảo trì thành công");
    }

    private async Task<StationAdminDetailDto> MapStationToAdminDtoAsync(Station station)
    {
        var policies = await _db.PricingPolicies.AsNoTracking()
            .Where(p => p.StationId == station.Id && p.EffectiveTo == null)
            .ToListAsync();

        var lockers = await _db.Lockers.AsNoTracking()
            .Where(l => l.StationId == station.Id)
            .ToListAsync();

        return MapStationToAdminDto(station, policies, lockers);
    }

    private static StationAdminDetailDto MapStationToAdminDto(
        Station station, List<PricingPolicy> policies, List<Locker> lockers)
    {
        return new StationAdminDetailDto
        {
            Id = station.Id,
            Name = station.Name,
            Address = station.Address,
            Latitude = station.Latitude,
            Longitude = station.Longitude,
            Status = station.Status.ToString(),
            OpensAt = station.OpensAt.ToString("HH:mm"),
            ClosesAt = station.ClosesAt.ToString("HH:mm"),
            ContactPhone = station.ContactPhone,
            TotalS = station.TotalS,
            TotalM = station.TotalM,
            TotalL = station.TotalL,
            AvailableS = lockers.Count(l => l.Size == LockerSize.S && l.BusinessStatus == LockerBusinessStatus.AVAILABLE),
            AvailableM = lockers.Count(l => l.Size == LockerSize.M && l.BusinessStatus == LockerBusinessStatus.AVAILABLE),
            AvailableL = lockers.Count(l => l.Size == LockerSize.L && l.BusinessStatus == LockerBusinessStatus.AVAILABLE),
            PriceS = policies.FirstOrDefault(p => p.Size == LockerSize.S)?.PricePerBlock,
            PriceM = policies.FirstOrDefault(p => p.Size == LockerSize.M)?.PricePerBlock,
            PriceL = policies.FirstOrDefault(p => p.Size == LockerSize.L)?.PricePerBlock,
            PricingPolicies = policies
                .OrderBy(p => p.Size)
                .Select(p => new StationPricingPolicyDto
                {
                    Id = p.Id,
                    Size = p.Size.ToString(),
                    PricePerBlock = p.PricePerBlock,
                    BlockHours = p.BlockHours,
                    OverdueFeePerHour = p.OverdueFeePerHour,
                    GracePeriodMinutes = p.GracePeriodMinutes,
                    RefundCutoffHours = p.RefundCutoffHours,
                    RefundRateEarly = p.RefundRateEarly,
                    RefundRateLate = p.RefundRateLate,
                    MinHours = p.MinHours,
                    MaxHours = p.MaxHours,
                    NoShowWindowMinutes = p.NoShowWindowMinutes,
                    EffectiveFrom = p.EffectiveFrom
                }).ToList(),
            CreatedAt = station.CreatedAt
        };
    }

    public async Task<ApiResponse<DeviceControlResultDto>> RebootDeviceAsync(Guid stationId, Guid adminUserId)
    {
        var station = await _db.Stations.FirstOrDefaultAsync(s => s.Id == stationId);
        if (station == null)
            return ApiResponse<DeviceControlResultDto>.ErrorResponse("Không tìm thấy thông tin trạm.");

        var device = await _db.IoTDevices.FirstOrDefaultAsync(d => d.StationId == stationId);
        if (device == null)
            return ApiResponse<DeviceControlResultDto>.ErrorResponse($"Trạm '{station.Name}' chưa được cấu hình mạch phần cứng IoT (Chưa gắn vi điều khiển ESP32).");

        var deviceCode = device.DeviceCode;

        if (_mqtt != null)
        {
            var published = await _mqtt.PublishRebootCommandAsync(deviceCode);
            if (!published)
            {
                return ApiResponse<DeviceControlResultDto>.ErrorResponse("Không thể phát lệnh Reboot qua MQTT (broker mất kết nối).");
            }
        }

        if (device != null)
        {
            device.ConnectivityStatus = "REBOOTING";
            device.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }

        if (_notifications != null)
        {
            await _notifications.NotifyDeviceRebootingAsync(stationId, deviceCode);
        }

        await _auditLog.LogAsync(
            adminUserId,
            null,
            "STAFF",
            "DEVICE_REBOOT",
            "Station",
            stationId.ToString(),
            $"Khởi động lại ESP32 từ xa cho thiết bị '{deviceCode}' tại trạm '{station.Name}'.");

        return ApiResponse<DeviceControlResultDto>.SuccessResponse(new DeviceControlResultDto
        {
            DeviceCode = deviceCode,
            Command = "REBOOT",
            Message = $"Đã phát lệnh khởi động lại ESP32 ({deviceCode}). Thiết bị sẽ kết nối lại sau 3-5 giây.",
            SentAt = DateTime.UtcNow
        });
    }

    public async Task<ApiResponse<DeviceControlResultDto>> TriggerSelfTestAsync(Guid stationId, Guid adminUserId)
    {
        var station = await _db.Stations.FirstOrDefaultAsync(s => s.Id == stationId);
        if (station == null)
            return ApiResponse<DeviceControlResultDto>.ErrorResponse("Không tìm thấy thông tin trạm.");

        var device = await _db.IoTDevices.FirstOrDefaultAsync(d => d.StationId == stationId);
        if (device == null)
            return ApiResponse<DeviceControlResultDto>.ErrorResponse($"Trạm '{station.Name}' chưa được cấu hình mạch phần cứng IoT (Chưa gắn vi điều khiển ESP32).");

        var deviceCode = device.DeviceCode;

        if (_mqtt != null)
        {
            var published = await _mqtt.PublishSelfTestCommandAsync(deviceCode);
            if (!published)
            {
                return ApiResponse<DeviceControlResultDto>.ErrorResponse("Không thể phát lệnh Self-Test qua MQTT (broker mất kết nối).");
            }
        }

        await _auditLog.LogAsync(
            adminUserId,
            null,
            "STAFF",
            "DEVICE_SELF_TEST",
            "Station",
            stationId.ToString(),
            $"Kích hoạt chu trình chẩn đoán tự động Self-Test phần cứng cho thiết bị '{deviceCode}' tại trạm '{station.Name}'.");

        return ApiResponse<DeviceControlResultDto>.SuccessResponse(new DeviceControlResultDto
        {
            DeviceCode = deviceCode,
            Command = "SELF_TEST",
            Message = $"Đã phát lệnh chạy chu trình chẩn đoán Self-Test tới ESP32 ({deviceCode}). Kết quả sẽ trả về qua kênh thời gian thực.",
            SentAt = DateTime.UtcNow
        });
    }

    public async Task<ApiResponse<List<IoTDeviceDto>>> GetIoTDevicesAsync()
    {
        var devices = await _db.IoTDevices.Include(d => d.Station).AsNoTracking().ToListAsync();
        var stations = await _db.Stations.AsNoTracking().ToListAsync();
        var result = new List<IoTDeviceDto>();
        var now = DateTime.UtcNow;

        foreach (var s in stations)
        {
            var dev = devices.FirstOrDefault(d => d.StationId == s.Id);
            if (dev != null)
            {
                // Trạm ĐÃ CẤU HÌNH vi điều khiển phần cứng
                // Kiểm tra trạng thái thực tế: nếu LastSeenAt quá 2 phút trước thì tính là OFFLINE
                var isLive = dev.ConnectivityStatus == "ONLINE" && dev.LastSeenAt.HasValue && dev.LastSeenAt.Value >= now.AddMinutes(-2);
                var actualStatus = isLive ? "ONLINE" : "OFFLINE";

                result.Add(new IoTDeviceDto
                {
                    Id = dev.Id,
                    StationId = s.Id,
                    StationName = s.Name,
                    StationAddress = s.Address,
                    DeviceCode = dev.DeviceCode,
                    DeviceType = dev.DeviceType,
                    FirmwareVersion = dev.FirmwareVersion,
                    ConnectivityStatus = actualStatus,
                    WifiRssi = dev.WifiRssi,
                    UptimeSeconds = dev.UptimeSeconds,
                    LastSeenAt = dev.LastSeenAt,
                    CreatedAt = dev.CreatedAt,
                    IsProvisioned = true
                });
            }
            else
            {
                // Trạm CHƯA GẮN vi điều khiển phần cứng (thực tế chưa lắp đặt)
                result.Add(new IoTDeviceDto
                {
                    Id = Guid.Empty,
                    StationId = s.Id,
                    StationName = s.Name,
                    StationAddress = s.Address,
                    DeviceCode = "CHƯA_GẮN",
                    DeviceType = "CHƯA CÓ",
                    FirmwareVersion = "--",
                    ConnectivityStatus = "UNPROVISIONED",
                    WifiRssi = null,
                    UptimeSeconds = null,
                    LastSeenAt = null,
                    CreatedAt = s.CreatedAt,
                    IsProvisioned = false
                });
            }
        }

        return ApiResponse<List<IoTDeviceDto>>.SuccessResponse(result);
    }

    private static readonly ConcurrentDictionary<Guid, StationSecurityThresholdsDto> _stationSecuritySettings = new();

    public async Task<ApiResponse<SwapSpareLockerResultDto>> SwapSpareLockerAsync(Guid lockerId, SwapSpareLockerRequest request, Guid adminUserId)
    {
        var stuckLocker = await _db.Lockers
            .Include(l => l.Station)
            .Include(l => l.Bookings)
            .FirstOrDefaultAsync(l => l.Id == lockerId);

        if (stuckLocker == null)
            return ApiResponse<SwapSpareLockerResultDto>.ErrorResponse("Không tìm thấy thông tin ô tủ yêu cầu.");

        // Tìm hộc tủ trống dự phòng cùng kích cỡ tại cùng trạm
        var spareLocker = await _db.Lockers
            .FirstOrDefaultAsync(l =>
                l.StationId == stuckLocker.StationId &&
                l.Id != stuckLocker.Id &&
                l.Size == stuckLocker.Size &&
                l.BusinessStatus == LockerBusinessStatus.AVAILABLE &&
                l.HealthStatus == LockerHealthStatus.HEALTHY);

        if (spareLocker == null)
            return ApiResponse<SwapSpareLockerResultDto>.ErrorResponse(
                $"Trạm '{stuckLocker.Station.Name}' hiện không còn hộc tủ dự phòng trống cùng kích cỡ ({stuckLocker.Size}) để hoán đổi.");

        // Tìm booking đang hoạt động liên kết với ô bị kẹt (nếu có)
        var activeBooking = stuckLocker.Bookings.FirstOrDefault(b =>
            b.Status == BookingStatus.CONFIRMED ||
            b.Status == BookingStatus.STORED ||
            b.Status == BookingStatus.PENDING_PAYMENT);

        var now = DateTime.UtcNow;

        // 1. Chuyển hộc tủ bị kẹt sang trạng thái MAINTENANCE
        stuckLocker.BusinessStatus = LockerBusinessStatus.MAINTENANCE;
        stuckLocker.HealthStatus = LockerHealthStatus.WARNING;
        stuckLocker.Notes = $"[KẸT CƠ HỌC] {request.Reason}. Đã hoán đổi sang ô {spareLocker.LockerCode} lúc {now:HH:mm dd/MM/yyyy}.";
        stuckLocker.UpdatedAt = now;

        // 2. Tạo phiếu bảo trì tự động cho ô kẹt
        var ticket = new MaintenanceTicket
        {
            Id = Guid.NewGuid(),
            TicketCode = $"TICK-{now:yyMMdd}-{Guid.NewGuid().ToString()[..4].ToUpperInvariant()}",
            StationId = stuckLocker.StationId,
            LockerId = stuckLocker.Id,
            ReportedByUserId = adminUserId,
            IssueDescription = $"Ô {stuckLocker.LockerCode} bị kẹt cơ học: {request.Reason}. Hệ thống đã điều phối hoán đổi sang ô dự phòng {spareLocker.LockerCode}.",
            Status = MaintenanceStatus.OPEN,
            Priority = "HIGH",
            CreatedAt = now
        };
        _db.MaintenanceTickets.Add(ticket);

        // 3. Nếu có booking đang chạy, chuyển sang ô dự phòng mới
        if (activeBooking != null)
        {
            activeBooking.LockerId = spareLocker.Id;
            activeBooking.UpdatedAt = now;

            // Đặt trạng thái cho hộc tủ mới tương ứng với trạng thái booking
            spareLocker.BusinessStatus = activeBooking.Status == BookingStatus.STORED
                ? LockerBusinessStatus.OCCUPIED
                : LockerBusinessStatus.RESERVED;
            spareLocker.UpdatedAt = now;
        }

        await _db.SaveChangesAsync();

        // 4. Nếu yêu cầu bật chốt ô mới (openNewLocker) và có kết nối MQTT
        if (request.OpenNewLocker && _mqtt != null)
        {
            var device = await _db.IoTDevices.FirstOrDefaultAsync(d => d.StationId == stuckLocker.StationId);
            var deviceCode = device?.DeviceCode ?? "SIM-MQTT-001";
            await _mqtt.PublishUnlockCommandAsync(deviceCode, spareLocker.Id, spareLocker.LockerCode);
        }

        // 5. Gửi thông báo thời gian thực cập nhật giao diện
        if (_notifications != null)
        {
            await _notifications.NotifyLockerStatusChangedAsync(stuckLocker.StationId, stuckLocker.Id);
            await _notifications.NotifyLockerStatusChangedAsync(spareLocker.StationId, spareLocker.Id);
        }

        // 6. Ghi Audit Log an ninh
        await _auditLog.LogAsync(
            adminUserId,
            null,
            "STAFF",
            "LOCKER_SWAP_SPARE",
            "Locker",
            stuckLocker.Id.ToString(),
            $"UC-S12: Hoán đổi ô tủ kẹt '{stuckLocker.LockerCode}' sang hộc dự phòng '{spareLocker.LockerCode}' (Size {stuckLocker.Size}) tại trạm '{stuckLocker.Station.Name}' cho đơn đặt '{activeBooking?.BookingCode ?? "N/A"}'. Lý do: {request.Reason}");

        return ApiResponse<SwapSpareLockerResultDto>.SuccessResponse(new SwapSpareLockerResultDto
        {
            OldLockerId = stuckLocker.Id,
            OldLockerCode = stuckLocker.LockerCode,
            NewLockerId = spareLocker.Id,
            NewLockerCode = spareLocker.LockerCode,
            Size = stuckLocker.Size.ToString(),
            BookingId = activeBooking?.Id,
            BookingCode = activeBooking?.BookingCode,
            Message = $"Đã hoán đổi thành công từ ô {stuckLocker.LockerCode} sang hộc dự phòng {spareLocker.LockerCode} (Size {stuckLocker.Size}). Hộc cũ đã được chuyển sang chế độ Bảo trì.",
            SwappedAt = now
        });
    }

    public async Task<ApiResponse<StationSecurityThresholdsDto>> GetSecurityThresholdsAsync(Guid stationId)
    {
        var station = await _db.Stations.AsNoTracking().FirstOrDefaultAsync(s => s.Id == stationId);
        if (station == null)
            return ApiResponse<StationSecurityThresholdsDto>.ErrorResponse("Không tìm thấy thông tin trạm.");

        var setting = _stationSecuritySettings.GetOrAdd(stationId, _ => new StationSecurityThresholdsDto
        {
            StationId = station.Id,
            StationName = station.Name,
            DoorAjarTimeoutSeconds = 60,
            MaxConsecutiveFailures = 5,
            VibrationSensitivityLevel = 2,
            EnableBuzzerAlarm = true,
            AutoCreateSecurityIncident = true,
            UpdatedAt = DateTime.UtcNow
        });

        setting.StationName = station.Name;
        return ApiResponse<StationSecurityThresholdsDto>.SuccessResponse(setting);
    }

    public async Task<ApiResponse<StationSecurityThresholdsDto>> UpdateSecurityThresholdsAsync(Guid stationId, UpdateSecurityThresholdsRequest request, Guid adminUserId)
    {
        var station = await _db.Stations.FirstOrDefaultAsync(s => s.Id == stationId);
        if (station == null)
            return ApiResponse<StationSecurityThresholdsDto>.ErrorResponse("Không tìm thấy thông tin trạm.");

        var updated = new StationSecurityThresholdsDto
        {
            StationId = station.Id,
            StationName = station.Name,
            DoorAjarTimeoutSeconds = Math.Clamp(request.DoorAjarTimeoutSeconds, 15, 600),
            MaxConsecutiveFailures = Math.Clamp(request.MaxConsecutiveFailures, 3, 10),
            VibrationSensitivityLevel = Math.Clamp(request.VibrationSensitivityLevel, 1, 3),
            EnableBuzzerAlarm = request.EnableBuzzerAlarm,
            AutoCreateSecurityIncident = request.AutoCreateSecurityIncident,
            UpdatedAt = DateTime.UtcNow
        };

        _stationSecuritySettings[stationId] = updated;

        await _auditLog.LogAsync(
            adminUserId,
            null,
            "STAFF",
            "UPDATE_SECURITY_THRESHOLDS",
            "Station",
            stationId.ToString(),
            $"UC-A11: Cập nhật cấu hình ngưỡng an ninh trạm '{station.Name}': DoorAjarTimeout={updated.DoorAjarTimeoutSeconds}s, MaxFailures={updated.MaxConsecutiveFailures}, Buzzer={updated.EnableBuzzerAlarm}");

        return ApiResponse<StationSecurityThresholdsDto>.SuccessResponse(updated);
    }

    public async Task<ApiResponse<bool>> UpdateLockerHardwareMappingAsync(Guid lockerId, UpdateLockerHardwareMappingRequest request, Guid adminUserId)
    {
        var locker = await _db.Lockers.Include(l => l.Station).FirstOrDefaultAsync(l => l.Id == lockerId);
        if (locker == null)
            return ApiResponse<bool>.ErrorResponse("Không tìm thấy thông tin ô tủ.");

        locker.GpioPin = request.GpioPin;
        if (request.IoTDeviceId.HasValue)
        {
            locker.IoTDeviceId = request.IoTDeviceId.Value == Guid.Empty ? null : request.IoTDeviceId;
        }
        locker.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        await _auditLog.LogAsync(
            adminUserId,
            null,
            "STAFF",
            "UPDATE_LOCKER_MAPPING",
            "Locker",
            lockerId.ToString(),
            $"UC-A02: Cập nhật ánh xạ phần cứng cho ô '{locker.LockerCode}' tại trạm '{locker.Station.Name}': Chân GPIO={locker.GpioPin?.ToString() ?? "Chưa gán"}, IoTDeviceId={locker.IoTDeviceId?.ToString() ?? "Chưa gán"}");

        return ApiResponse<bool>.SuccessResponse(true, "Cập nhật ánh xạ chân vi điều khiển thành công.");
    }

    public async Task<ApiResponse<List<DoorTelemetryItemDto>>> GetDoorTelemetryAsync(Guid stationId)
    {
        var station = await _db.Stations.AsNoTracking().FirstOrDefaultAsync(s => s.Id == stationId);
        if (station == null)
            return ApiResponse<List<DoorTelemetryItemDto>>.ErrorResponse("Không tìm thấy thông tin trạm.");

        var device = await _db.IoTDevices.AsNoTracking().FirstOrDefaultAsync(d => d.StationId == stationId);
        var deviceCode = device?.DeviceCode ?? "SIM-MQTT-001";

        var lockers = await _db.Lockers.AsNoTracking()
            .Where(l => l.StationId == stationId)
            .OrderBy(l => l.LockerCode)
            .Select(l => new DoorTelemetryItemDto
            {
                LockerId = l.Id,
                LockerCode = l.LockerCode,
                Size = l.Size.ToString(),
                DoorState = l.DoorState,
                LastDoorEventAt = l.LastDoorEventAt,
                GpioPin = l.GpioPin,
                HealthStatus = l.HealthStatus.ToString(),
                DeviceCode = deviceCode
            })
            .ToListAsync();

        return ApiResponse<List<DoorTelemetryItemDto>>.SuccessResponse(lockers);
    }
}
