namespace API.Controllers;

using Application.DTOs.Common;
using Application.DTOs.Station;
using Application.Interfaces;
using Domain.Entities;
using Domain.Enums;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

[ApiController]
[Route("api/[controller]")]
public class StationsController : ControllerBase
{
    private readonly ISmartLockerDbContext _db;
    private static bool _daNangSeedChecked = false;

    public StationsController(ISmartLockerDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// UC-T05: Tìm kiếm danh sách trạm trên bản đồ số GPS (TP. Đà Nẵng & TP. Hồ Chí Minh)
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetStations(
        [FromQuery] string? search = null,
        [FromQuery] double? userLat = null,
        [FromQuery] double? userLng = null,
        [FromQuery] double? radiusKm = null)
    {
        if (!_daNangSeedChecked)
        {
            await EnsureDaNangStationsSeededAsync();
            _daNangSeedChecked = true;
        }

        var query = _db.Stations.AsNoTracking()
            .Where(s => s.Status == StationStatus.ACTIVE || s.Status == StationStatus.MAINTENANCE);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(s => s.Name.Contains(term) || s.Address.Contains(term));
        }

        var nowUtc = DateTime.UtcNow;
        var windowEndUtc = nowUtc.AddHours(1);

        var rawStations = await query
            .Select(s => new
            {
                s.Id,
                s.Name,
                s.Address,
                s.Latitude,
                s.Longitude,
                Status = s.Status.ToString(),
                s.OpensAt,
                s.ClosesAt,
                s.TotalS,
                s.TotalM,
                s.TotalL,
                AvailableS = s.Lockers.Count(l => l.Size == LockerSize.S && l.BusinessStatus == LockerBusinessStatus.AVAILABLE && l.HealthStatus == LockerHealthStatus.HEALTHY)
                    - s.Bookings.Count(b => b.Size == LockerSize.S && b.StartAt < windowEndUtc && b.EndAt > nowUtc
                        && (b.Status == BookingStatus.CONFIRMED || (b.Status == BookingStatus.PENDING_PAYMENT && (b.PaymentExpiresAt == null || b.PaymentExpiresAt > nowUtc)))),
                AvailableM = s.Lockers.Count(l => l.Size == LockerSize.M && l.BusinessStatus == LockerBusinessStatus.AVAILABLE && l.HealthStatus == LockerHealthStatus.HEALTHY)
                    - s.Bookings.Count(b => b.Size == LockerSize.M && b.StartAt < windowEndUtc && b.EndAt > nowUtc
                        && (b.Status == BookingStatus.CONFIRMED || (b.Status == BookingStatus.PENDING_PAYMENT && (b.PaymentExpiresAt == null || b.PaymentExpiresAt > nowUtc)))),
                AvailableL = s.Lockers.Count(l => l.Size == LockerSize.L && l.BusinessStatus == LockerBusinessStatus.AVAILABLE && l.HealthStatus == LockerHealthStatus.HEALTHY)
                    - s.Bookings.Count(b => b.Size == LockerSize.L && b.StartAt < windowEndUtc && b.EndAt > nowUtc
                        && (b.Status == BookingStatus.CONFIRMED || (b.Status == BookingStatus.PENDING_PAYMENT && (b.PaymentExpiresAt == null || b.PaymentExpiresAt > nowUtc)))),
                PriceS = s.PricingPolicies.Where(p => p.Size == LockerSize.S && p.EffectiveTo == null).Select(p => (decimal?)p.PricePerBlock).FirstOrDefault(),
                PriceM = s.PricingPolicies.Where(p => p.Size == LockerSize.M && p.EffectiveTo == null).Select(p => (decimal?)p.PricePerBlock).FirstOrDefault(),
                PriceL = s.PricingPolicies.Where(p => p.Size == LockerSize.L && p.EffectiveTo == null).Select(p => (decimal?)p.PricePerBlock).FirstOrDefault(),
                s.ContactPhone,
                s.CreatedAt
            })
            .ToListAsync();

        // Loại bỏ các bản ghi trạm bị seed trùng lặp (cùng Tên và Địa chỉ), giữ nguyên toàn bộ trạm TP.HCM & Đà Nẵng
        var uniqueStations = rawStations
            .GroupBy(s => new { Name = s.Name.Trim().ToLowerInvariant(), Address = s.Address.Trim().ToLowerInvariant() })
            .Select(g => g
                .OrderByDescending(x => x.AvailableS + x.AvailableM + x.AvailableL)
                .ThenByDescending(x => x.TotalS + x.TotalM + x.TotalL)
                .ThenBy(x => x.CreatedAt)
                .First())
            .ToList();

        var stations = uniqueStations.Select(s =>
        {
            double? dist = null;
            if (userLat.HasValue && userLng.HasValue)
            {
                dist = Math.Round(CalculateDistanceKm(userLat.Value, userLng.Value, (double)s.Latitude, (double)s.Longitude), 2);
            }

            return new StationListItemDto
            {
                Id = s.Id,
                Name = s.Name,
                Address = s.Address,
                Latitude = s.Latitude,
                Longitude = s.Longitude,
                Status = s.Status,
                OpensAt = s.OpensAt.ToString("HH:mm"),
                ClosesAt = s.ClosesAt.ToString("HH:mm"),
                TotalS = s.TotalS,
                TotalM = s.TotalM,
                TotalL = s.TotalL,
                AvailableS = Math.Max(0, Math.Min((int)s.TotalS, s.AvailableS)),
                AvailableM = Math.Max(0, Math.Min((int)s.TotalM, s.AvailableM)),
                AvailableL = Math.Max(0, Math.Min((int)s.TotalL, s.AvailableL)),
                PriceS = s.PriceS,
                PriceM = s.PriceM,
                PriceL = s.PriceL,
                ContactPhone = s.ContactPhone,
                DistanceKm = dist
            };
        }).ToList();

        if (radiusKm.HasValue && radiusKm.Value > 0 && userLat.HasValue && userLng.HasValue)
        {
            stations = stations.Where(s => s.DistanceKm.HasValue && s.DistanceKm.Value <= radiusKm.Value).ToList();
        }

        if (userLat.HasValue && userLng.HasValue)
        {
            stations = stations.OrderBy(s => s.DistanceKm ?? double.MaxValue).ToList();
        }

        return Ok(ApiResponse<List<StationListItemDto>>.SuccessResponse(stations));
    }

    /// <summary>
    /// UC-T05 / UC-T06: Xem thông tin chi tiết trạm + bảng giá + số lượng ô trống thực tế
    /// </summary>
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetStation(Guid id,
        [FromQuery] DateTime? startAt = null,
        [FromQuery] DateTime? endAt = null)
    {
        var station = await _db.Stations.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == id);

        if (station == null)
            return NotFound(ApiResponse<object>.ErrorResponse("Không tìm thấy trạm"));

        var dto = new StationDto
        {
            Id = station.Id,
            Name = station.Name,
            Address = station.Address,
            Latitude = station.Latitude,
            Longitude = station.Longitude,
            Status = station.Status.ToString(),
            OpensAt = station.OpensAt.ToString("HH:mm"),
            ClosesAt = station.ClosesAt.ToString("HH:mm"),
            TotalS = station.TotalS,
            TotalM = station.TotalM,
            TotalL = station.TotalL,
            ContactPhone = station.ContactPhone
        };

        // Get pricing policies from DB
        var policies = await _db.PricingPolicies.AsNoTracking()
            .Where(p => p.StationId == id && p.EffectiveTo == null)
            .ToListAsync();

        var policyS = policies.FirstOrDefault(p => p.Size == LockerSize.S);
        var policyM = policies.FirstOrDefault(p => p.Size == LockerSize.M);
        var policyL = policies.FirstOrDefault(p => p.Size == LockerSize.L);

        dto.PriceS = policyS?.PricePerBlock;
        dto.PriceM = policyM?.PricePerBlock;
        dto.PriceL = policyL?.PricePerBlock;

        dto.OverdueFeeS = policyS?.OverdueFeePerHour;
        dto.OverdueFeeM = policyM?.OverdueFeePerHour;
        dto.OverdueFeeL = policyL?.OverdueFeePerHour;

        var refPolicy = policyM ?? policyS ?? policyL;
        if (refPolicy != null)
        {
            dto.MinHours = refPolicy.MinHours;
            dto.MaxHours = refPolicy.MaxHours;
            dto.GracePeriodMinutes = refPolicy.GracePeriodMinutes;
            dto.RefundCutoffHours = refPolicy.RefundCutoffHours;
            dto.RefundRateEarly = refPolicy.RefundRateEarly;
            dto.RefundRateLate = refPolicy.RefundRateLate;
        }

        // Calculate availability (if time range not provided, default to current time window)
        var nowUtc = DateTime.UtcNow;
        var fromTime = startAt ?? nowUtc;
        var toTime = endAt ?? fromTime.AddHours(3);

        foreach (var size in new[] { LockerSize.S, LockerSize.M, LockerSize.L })
        {
            var totalBookable = await _db.Lockers.AsNoTracking()
                .CountAsync(l => l.StationId == id && l.Size == size
                    && l.BusinessStatus == LockerBusinessStatus.AVAILABLE
                    && l.HealthStatus == LockerHealthStatus.HEALTHY);

            var overlapping = await _db.Bookings.AsNoTracking()
                .CountAsync(b => b.StationId == id && b.Size == size
                    && (b.Status == BookingStatus.CONFIRMED
                        || b.Status == BookingStatus.STORED
                        || (b.Status == BookingStatus.PENDING_PAYMENT && (b.PaymentExpiresAt == null || b.PaymentExpiresAt > nowUtc)))
                    && b.StartAt < toTime && b.EndAt > fromTime);

            var available = Math.Max(0, totalBookable - overlapping);

            switch (size)
            {
                case LockerSize.S: dto.AvailableS = Math.Min((int)station.TotalS, available); break;
                case LockerSize.M: dto.AvailableM = Math.Min((int)station.TotalM, available); break;
                case LockerSize.L: dto.AvailableL = Math.Min((int)station.TotalL, available); break;
            }
        }

        return Ok(ApiResponse<StationDto>.SuccessResponse(dto));
    }

    /// <summary>
    /// Đảm bảo các trạm tại TP. Đà Nẵng đã được khởi tạo trong Database (giữ nguyên toàn bộ trạm TP.HCM).
    /// </summary>
    private async Task EnsureDaNangStationsSeededAsync()
    {
        try
        {
            var daNangDefs = new[]
            {
                new { Name = "SmartLocker Sân Bay Quốc Tế Đà Nẵng", Address = "Nhà ga Quốc nội T1, Đường Duy Tân, Hòa Thuận Tây, Hải Châu, TP. Đà Nẵng", Lat = 16.0538m, Lng = 108.2036m, Opens = new TimeOnly(0, 0), Closes = new TimeOnly(23, 59), S = (short)10, M = (short)8, L = (short)6, Phone = "0236 3823 397", PriceS = 10000m, PriceM = 15000m, PriceL = 25000m },
                new { Name = "SmartLocker Cầu Rồng - Bạch Đằng", Address = "Số 2 Đường Bạch Đằng (Đầu Cầu Rồng), Phước Ninh, Hải Châu, TP. Đà Nẵng", Lat = 16.0611m, Lng = 108.2244m, Opens = new TimeOnly(0, 0), Closes = new TimeOnly(23, 59), S = (short)10, M = (short)8, L = (short)4, Phone = "0236 3888 123", PriceS = 10000m, PriceM = 15000m, PriceL = 20000m },
                new { Name = "SmartLocker Ga Đà Nẵng", Address = "791 Hải Phòng, Tam Thuận, Thanh Khê, TP. Đà Nẵng", Lat = 16.0718m, Lng = 108.2097m, Opens = new TimeOnly(4, 30), Closes = new TimeOnly(23, 30), S = (short)8, M = (short)8, L = (short)6, Phone = "0236 3823 810", PriceS = 10000m, PriceM = 15000m, PriceL = 20000m },
                new { Name = "SmartLocker Chợ Hàn - Trần Phú", Address = "119 Trần Phú, Hải Châu 1, Hải Châu, TP. Đà Nẵng", Lat = 16.0681m, Lng = 108.2245m, Opens = new TimeOnly(6, 0), Closes = new TimeOnly(22, 30), S = (short)12, M = (short)8, L = (short)4, Phone = "0236 3821 363", PriceS = 10000m, PriceM = 15000m, PriceL = 20000m },
                new { Name = "SmartLocker Biển Mỹ Khê - Võ Nguyên Giáp", Address = "Công viên Biển Đông, Võ Nguyên Giáp, Phước Mỹ, Sơn Trà, TP. Đà Nẵng", Lat = 16.0702m, Lng = 108.2463m, Opens = new TimeOnly(0, 0), Closes = new TimeOnly(23, 59), S = (short)12, M = (short)10, L = (short)4, Phone = "0236 3939 555", PriceS = 12000m, PriceM = 18000m, PriceL = 25000m },
                new { Name = "SmartLocker Đại Học FPT Đà Nẵng (FPT City)", Address = "Khu đô thị công nghệ FPT Đà Nẵng, Hòa Hải, Ngũ Hành Sơn, TP. Đà Nẵng", Lat = 15.9686m, Lng = 108.2606m, Opens = new TimeOnly(6, 0), Closes = new TimeOnly(23, 0), S = (short)10, M = (short)8, L = (short)4, Phone = "0236 7300 999", PriceS = 8000m, PriceM = 12000m, PriceL = 18000m },
            };

            var existingNames = await _db.Stations
                .Select(s => s.Name)
                .ToListAsync();

            var now = DateTime.UtcNow;
            var addedAny = false;

            foreach (var def in daNangDefs)
            {
                if (existingNames.Any(n => n.Equals(def.Name, StringComparison.OrdinalIgnoreCase)))
                    continue;

                var station = new Station
                {
                    Id = Guid.NewGuid(),
                    Name = def.Name,
                    Address = def.Address,
                    Latitude = def.Lat,
                    Longitude = def.Lng,
                    Status = StationStatus.ACTIVE,
                    OpensAt = def.Opens,
                    ClosesAt = def.Closes,
                    Timezone = "Asia/Ho_Chi_Minh",
                    TotalS = def.S,
                    TotalM = def.M,
                    TotalL = def.L,
                    ContactPhone = def.Phone,
                    CreatedAt = now,
                    UpdatedAt = now
                };

                _db.Stations.Add(station);

                int idx = 1;
                for (int i = 0; i < def.S; i++, idx++)
                {
                    _db.Lockers.Add(new Locker
                    {
                        Id = Guid.NewGuid(),
                        StationId = station.Id,
                        LockerCode = $"S-{idx:D2}",
                        Size = LockerSize.S,
                        BusinessStatus = LockerBusinessStatus.AVAILABLE,
                        HealthStatus = LockerHealthStatus.HEALTHY,
                        DoorState = "CLOSED",
                        CreatedAt = now,
                        UpdatedAt = now
                    });
                }
                for (int i = 0; i < def.M; i++, idx++)
                {
                    _db.Lockers.Add(new Locker
                    {
                        Id = Guid.NewGuid(),
                        StationId = station.Id,
                        LockerCode = $"M-{idx:D2}",
                        Size = LockerSize.M,
                        BusinessStatus = LockerBusinessStatus.AVAILABLE,
                        HealthStatus = LockerHealthStatus.HEALTHY,
                        DoorState = "CLOSED",
                        CreatedAt = now,
                        UpdatedAt = now
                    });
                }
                for (int i = 0; i < def.L; i++, idx++)
                {
                    _db.Lockers.Add(new Locker
                    {
                        Id = Guid.NewGuid(),
                        StationId = station.Id,
                        LockerCode = $"L-{idx:D2}",
                        Size = LockerSize.L,
                        BusinessStatus = LockerBusinessStatus.AVAILABLE,
                        HealthStatus = LockerHealthStatus.HEALTHY,
                        DoorState = "CLOSED",
                        CreatedAt = now,
                        UpdatedAt = now
                    });
                }

                _db.PricingPolicies.AddRange(new[]
                {
                    new PricingPolicy { Id = Guid.NewGuid(), StationId = station.Id, Size = LockerSize.S, PricePerBlock = def.PriceS, BlockHours = 1, OverdueFeePerHour = def.PriceS * 1.5m, GracePeriodMinutes = 15, MinHours = 1, MaxHours = 72, RefundCutoffHours = 2, RefundRateEarly = 1.0m, RefundRateLate = 0.5m, EffectiveFrom = now, CreatedAt = now },
                    new PricingPolicy { Id = Guid.NewGuid(), StationId = station.Id, Size = LockerSize.M, PricePerBlock = def.PriceM, BlockHours = 1, OverdueFeePerHour = def.PriceM * 1.5m, GracePeriodMinutes = 15, MinHours = 1, MaxHours = 72, RefundCutoffHours = 2, RefundRateEarly = 1.0m, RefundRateLate = 0.5m, EffectiveFrom = now, CreatedAt = now },
                    new PricingPolicy { Id = Guid.NewGuid(), StationId = station.Id, Size = LockerSize.L, PricePerBlock = def.PriceL, BlockHours = 1, OverdueFeePerHour = def.PriceL * 1.5m, GracePeriodMinutes = 15, MinHours = 1, MaxHours = 72, RefundCutoffHours = 2, RefundRateEarly = 1.0m, RefundRateLate = 0.5m, EffectiveFrom = now, CreatedAt = now }
                });

                addedAny = true;
            }

            if (addedAny)
            {
                await _db.SaveChangesAsync();
            }
        }
        catch
        {
            // Bỏ qua nếu không thể tự động seed
        }
    }

    private static double CalculateDistanceKm(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6371.0;
        var dLat = (lat2 - lat1) * Math.PI / 180.0;
        var dLon = (lon2 - lon1) * Math.PI / 180.0;
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(lat1 * Math.PI / 180.0) * Math.Cos(lat2 * Math.PI / 180.0) *
                Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }
}
