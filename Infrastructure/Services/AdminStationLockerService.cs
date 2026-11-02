namespace Infrastructure.Services;

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

    public AdminStationLockerService(ISmartLockerDbContext db, IAuditLogService auditLog)
    {
        _db = db;
        _auditLog = auditLog;
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

        // Auto-generate Lockers (S, M, L)
        int lockerIndex = 1;
        var lockers = new List<Locker>();

        for (int i = 0; i < request.TotalS; i++)
        {
            lockers.Add(new Locker
            {
                Id = Guid.NewGuid(),
                StationId = station.Id,
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

        var grid = lockers.Select(l =>
        {
            var booking = activeBookings.FirstOrDefault(b => b.LockerId == l.Id);
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
                Notes = l.Notes,
                CurrentBooking = bookingBrief
            };
        }).ToList();

        return ApiResponse<List<LockerGridItemDto>>.SuccessResponse(grid);
    }

    public async Task<ApiResponse<bool>> UpdateLockerStatusAsync(Guid lockerId, UpdateLockerStatusRequest request, Guid adminUserId)
    {
        var locker = await _db.Lockers.FirstOrDefaultAsync(l => l.Id == lockerId);
        if (locker == null)
            return ApiResponse<bool>.ErrorResponse("Không tìm thấy ô tủ");

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
}
