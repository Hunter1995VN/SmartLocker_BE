namespace API.Controllers;

using Application.DTOs.Common;
using Application.DTOs.Station;
using Application.Interfaces;
using Domain.Enums;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

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
    public async Task<IActionResult> GetStations([FromQuery] string? search = null)
    {
        var query = _db.Stations.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            query = query.Where(s => s.Name.Contains(search) || s.Address.Contains(search));
        }

        var stations = await query
            .Where(s => s.Status == StationStatus.ACTIVE)
            .Select(s => new StationListItemDto
            {
                Id = s.Id,
                Name = s.Name,
                Address = s.Address,
                Latitude = s.Latitude,
                Longitude = s.Longitude,
                Status = s.Status.ToString(),
                TotalS = s.TotalS,
                TotalM = s.TotalM,
                TotalL = s.TotalL
            })
            .ToListAsync();

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
        var policies = await _db.PricingPolicies.AsNoTracking()
            .Where(p => p.StationId == id && p.EffectiveTo == null)
            .ToListAsync();

        dto.PriceS = policies.FirstOrDefault(p => p.Size == LockerSize.S)?.PricePerBlock;
        dto.PriceM = policies.FirstOrDefault(p => p.Size == LockerSize.M)?.PricePerBlock;
        dto.PriceL = policies.FirstOrDefault(p => p.Size == LockerSize.L)?.PricePerBlock;

        // Calculate availability if time range provided
        if (startAt.HasValue && endAt.HasValue)
        {
            var activeStatuses = new[] { BookingStatus.PENDING_PAYMENT, BookingStatus.CONFIRMED, BookingStatus.STORED };

            foreach (var size in new[] { LockerSize.S, LockerSize.M, LockerSize.L })
            {
                var totalBookable = await _db.Lockers.AsNoTracking()
                    .CountAsync(l => l.StationId == id && l.Size == size
                        && l.BusinessStatus == LockerBusinessStatus.AVAILABLE
                        && l.HealthStatus == LockerHealthStatus.HEALTHY);

                var overlapping = await _db.Bookings.AsNoTracking()
                    .CountAsync(b => b.StationId == id && b.Size == size
                        && activeStatuses.Contains(b.Status)
                        && b.StartAt < endAt.Value && b.EndAt > startAt.Value);

                var available = Math.Max(0, totalBookable - overlapping);

                switch (size)
                {
                    case LockerSize.S: dto.AvailableS = available; break;
                    case LockerSize.M: dto.AvailableM = available; break;
                    case LockerSize.L: dto.AvailableL = available; break;
                }
            }
        }

        return Ok(ApiResponse<StationDto>.SuccessResponse(dto));
    }
}
