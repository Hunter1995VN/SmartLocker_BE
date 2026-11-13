namespace API.Controllers;

using Application.DTOs.Common;
using Application.DTOs.Station;
using Application.Interfaces;
using Domain.Enums;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Infrastructure.Services;

[ApiController]
[Route("api/[controller]")]
public class StationsController : ControllerBase
{
    private readonly ISmartLockerDbContext _db;

    public StationsController(ISmartLockerDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// Danh s\u00e1ch tr\u1ea1m (UC-G04)
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetStations([FromQuery] string? search = null,
        [FromQuery] double? userLat = null, [FromQuery] double? userLng = null,
        [FromQuery] double? radiusKm = null)
    {
        var query = _db.Stations.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(s => s.Name.Contains(term) || s.Address.Contains(term));
        }

        var now = DateTime.UtcNow;
        var rawStations = await query
            .Where(s => s.Status == StationStatus.ACTIVE || s.Status == StationStatus.MAINTENANCE)
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
                TotalS = s.Lockers.Count(l => l.Size == LockerSize.S && _db.IoTDevices.Any(d => d.Id == l.IoTDeviceId && d.StationId == s.Id)),
                TotalM = s.Lockers.Count(l => l.Size == LockerSize.M && _db.IoTDevices.Any(d => d.Id == l.IoTDeviceId && d.StationId == s.Id)),
                TotalL = s.Lockers.Count(l => l.Size == LockerSize.L && _db.IoTDevices.Any(d => d.Id == l.IoTDeviceId && d.StationId == s.Id)),
                PriceS = s.PricingPolicies.Where(p => p.Size == LockerSize.S && p.EffectiveFrom <= now && (p.EffectiveTo == null || p.EffectiveTo > now)).OrderByDescending(p => p.EffectiveFrom).Select(p => (decimal?)p.PricePerBlock).FirstOrDefault(),
                PriceM = s.PricingPolicies.Where(p => p.Size == LockerSize.M && p.EffectiveFrom <= now && (p.EffectiveTo == null || p.EffectiveTo > now)).OrderByDescending(p => p.EffectiveFrom).Select(p => (decimal?)p.PricePerBlock).FirstOrDefault(),
                PriceL = s.PricingPolicies.Where(p => p.Size == LockerSize.L && p.EffectiveFrom <= now && (p.EffectiveTo == null || p.EffectiveTo > now)).OrderByDescending(p => p.EffectiveFrom).Select(p => (decimal?)p.PricePerBlock).FirstOrDefault(),
                s.ContactPhone,
                s.CreatedAt
            })
            .ToListAsync();

        var ratings = await _db.StationReviews.Where(r => !r.IsHidden).GroupBy(r => r.Booking.StationId)
            .Select(g => new { StationId = g.Key, Average = g.Average(r => r.Stars), Count = g.Count() })
            .ToDictionaryAsync(r => r.StationId);
        var stations = new List<StationListItemDto>();
        var until = now.AddHours(1);
        var ready = (await LockerAvailability.ReadyLockers(_db, null, now)
            .GroupBy(l => new { l.StationId, l.Size })
            .Select(g => new { g.Key.StationId, g.Key.Size, Count = g.Count() }).ToListAsync())
            .ToDictionary(g => (g.StationId, g.Size), g => g.Count);
        var reservations = (await _db.Bookings.Where(b => b.LockerId == null && b.StartAt < until && b.EndAt > now
            && (b.Status == BookingStatus.CONFIRMED || (b.Status == BookingStatus.PENDING_PAYMENT && b.PaymentExpiresAt > now)))
            .GroupBy(b => new { b.StationId, b.Size })
            .Select(g => new { g.Key.StationId, g.Key.Size, Count = g.Count() }).ToListAsync())
            .ToDictionary(g => (g.StationId, g.Size), g => g.Count);
        int Available(Guid stationId, LockerSize size) => Math.Max(0,
            ready.GetValueOrDefault((stationId, size)) - reservations.GetValueOrDefault((stationId, size)));
        var uniqueStations = rawStations.GroupBy(s => new {
            Name = s.Name.Trim().ToLowerInvariant(), Address = s.Address.Trim().ToLowerInvariant()
        }).Select(g => g.OrderByDescending(s => Available(s.Id, LockerSize.S) + Available(s.Id, LockerSize.M) + Available(s.Id, LockerSize.L))
            .ThenByDescending(s => s.TotalS + s.TotalM + s.TotalL).ThenBy(s => s.CreatedAt).First()).ToList();
        foreach (var s in uniqueStations) stations.Add(new StationListItemDto
        {
            Id = s.Id,
            RatingAverage = ratings.TryGetValue(s.Id, out var rating) ? rating.Average : null,
            RatingCount = ratings.TryGetValue(s.Id, out var ratingCount) ? ratingCount.Count : 0,
            Name = s.Name,
            Address = s.Address,
            Latitude = s.Latitude,
            Longitude = s.Longitude,
            Status = s.Status,
            OpensAt = s.OpensAt.ToString("HH:mm"),
            ClosesAt = s.ClosesAt.ToString("HH:mm"),
            TotalS = (short)s.TotalS,
            TotalM = (short)s.TotalM,
            TotalL = (short)s.TotalL,
            AvailableS = s.Status == "ACTIVE" ? Available(s.Id, LockerSize.S) : 0,
            AvailableM = s.Status == "ACTIVE" ? Available(s.Id, LockerSize.M) : 0,
            AvailableL = s.Status == "ACTIVE" ? Available(s.Id, LockerSize.L) : 0,
            PriceS = s.PriceS, PriceM = s.PriceM, PriceL = s.PriceL,
            ContactPhone = s.ContactPhone,
            DistanceKm = userLat.HasValue && userLng.HasValue
                ? Math.Round(CalculateDistanceKm(userLat.Value, userLng.Value, (double)s.Latitude, (double)s.Longitude), 2) : null
        });

        if (radiusKm > 0 && userLat.HasValue && userLng.HasValue)
            stations = stations.Where(s => s.DistanceKm.HasValue && s.DistanceKm <= radiusKm.Value).ToList();
        if (userLat.HasValue && userLng.HasValue)
            stations = stations.OrderBy(s => s.DistanceKm ?? double.MaxValue).ToList();

        return Ok(ApiResponse<List<StationListItemDto>>.SuccessResponse(stations));
    }

    /// <summary>
    /// Chi ti\u1ebft tr\u1ea1m + b\u1ea3ng gi\u00e1 + availability (UC-G05)
    /// </summary>
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetStation(Guid id,
        [FromQuery] DateTime? startAt = null,
        [FromQuery] DateTime? endAt = null)
    {
        var station = await _db.Stations.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == id);

        if (station == null)
            return NotFound(ApiResponse<object>.ErrorResponse("Kh\u00f4ng t\u00ecm th\u1ea5y tr\u1ea1m"));

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

        // Get pricing
        var rating = await _db.StationReviews.Where(r => !r.IsHidden && r.Booking.StationId == id).GroupBy(r => r.Booking.StationId)
            .Select(g => new { Average = g.Average(r => r.Stars), Count = g.Count() }).FirstOrDefaultAsync();
        dto.RatingAverage = rating?.Average;
        dto.RatingCount = rating?.Count ?? 0;

        var now = DateTime.UtcNow;
        var policies = (await _db.PricingPolicies.AsNoTracking()
            .Where(p => p.StationId == id && p.EffectiveFrom <= now && (p.EffectiveTo == null || p.EffectiveTo > now))
            .OrderByDescending(p => p.EffectiveFrom).ToListAsync()).GroupBy(p => p.Size).Select(g => g.First()).ToList();

        dto.PriceS = policies.FirstOrDefault(p => p.Size == LockerSize.S)?.PricePerBlock;
        dto.PriceM = policies.FirstOrDefault(p => p.Size == LockerSize.M)?.PricePerBlock;
        dto.PriceL = policies.FirstOrDefault(p => p.Size == LockerSize.L)?.PricePerBlock;
        dto.OverdueFeeS = policies.FirstOrDefault(p => p.Size == LockerSize.S)?.OverdueFeePerHour;
        dto.OverdueFeeM = policies.FirstOrDefault(p => p.Size == LockerSize.M)?.OverdueFeePerHour;
        dto.OverdueFeeL = policies.FirstOrDefault(p => p.Size == LockerSize.L)?.OverdueFeePerHour;
        var referencePolicy = policies.FirstOrDefault(p => p.Size == LockerSize.M) ?? policies.FirstOrDefault();
        if (referencePolicy != null)
        {
            dto.MinHours = referencePolicy.MinHours; dto.MaxHours = referencePolicy.MaxHours;
            dto.GracePeriodMinutes = referencePolicy.GracePeriodMinutes;
            dto.RefundCutoffHours = referencePolicy.RefundCutoffHours;
            dto.RefundRateEarly = referencePolicy.RefundRateEarly; dto.RefundRateLate = referencePolicy.RefundRateLate;
        }
        dto.SizePricing = policies.Select(p => new StationSizePricingDto { Size = p.Size.ToString(),
            PricePerBlock = p.PricePerBlock, BlockHours = p.BlockHours, MinHours = p.MinHours, MaxHours = p.MaxHours }).ToList();
        var devices = await _db.IoTDevices.AsNoTracking().Where(d => d.StationId == id).ToListAsync();
        var deviceIds = devices.Select(d => d.Id).ToList();
        var lockers = await _db.Lockers.AsNoTracking().Where(l => l.StationId == id && l.IoTDeviceId != null && deviceIds.Contains(l.IoTDeviceId.Value)).ToListAsync();
        dto.TotalS = (short)lockers.Count(l => l.Size == LockerSize.S);
        dto.TotalM = (short)lockers.Count(l => l.Size == LockerSize.M);
        dto.TotalL = (short)lockers.Count(l => l.Size == LockerSize.L);
        dto.Lockers = lockers.OrderBy(l => l.Size).ThenBy(l => l.LockerCode).Select(l => new StationLockerDto {
            LockerId = l.Id, LockerCode = l.LockerCode, Size = l.Size.ToString(), BusinessStatus = l.BusinessStatus.ToString(),
            HealthStatus = l.HealthStatus.ToString(), DoorState = l.DoorState, LastDoorEventAt = l.LastDoorEventAt,
            ConnectivityStatus = devices.Any(d => d.Id == l.IoTDeviceId && d.ConnectivityStatus == "ONLINE" && d.LastSeenAt >= now.AddSeconds(-60)) ? "ONLINE" : "OFFLINE"
        }).ToList();

        // Calculate availability (if time range not provided, default to current time window)
        var fromTime = startAt ?? DateTime.UtcNow;
        var toTime = endAt ?? fromTime.AddHours(3);
        if (fromTime >= toTime) return BadRequest(ApiResponse<object>.ErrorResponse("Khoảng thời gian không hợp lệ."));

        foreach (var size in new[] { LockerSize.S, LockerSize.M, LockerSize.L })
        {
            var available = station.Status == StationStatus.ACTIVE
                ? await LockerAvailability.CountAsync(_db, id, size, fromTime, toTime, now) : 0;

            switch (size)
            {
                case LockerSize.S: dto.AvailableS = available; break;
                case LockerSize.M: dto.AvailableM = available; break;
                case LockerSize.L: dto.AvailableL = available; break;
            }
        }

        return Ok(ApiResponse<StationDto>.SuccessResponse(dto));
    }

    private static double CalculateDistanceKm(double lat1, double lon1, double lat2, double lon2)
    {
        const double radius = 6371.0;
        var dLat = (lat2 - lat1) * Math.PI / 180.0;
        var dLon = (lon2 - lon1) * Math.PI / 180.0;
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
            + Math.Cos(lat1 * Math.PI / 180.0) * Math.Cos(lat2 * Math.PI / 180.0)
            * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return radius * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }
}
