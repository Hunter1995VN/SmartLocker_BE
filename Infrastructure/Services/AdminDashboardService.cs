namespace Infrastructure.Services;

using Application.DTOs.Admin;
using Application.DTOs.Common;
using Application.Interfaces;
using Domain.Enums;
using Microsoft.EntityFrameworkCore;

public class AdminDashboardService : IAdminDashboardService
{
    private readonly ISmartLockerDbContext _db;

    public AdminDashboardService(ISmartLockerDbContext db)
    {
        _db = db;
    }

    public async Task<ApiResponse<AdminDashboardStatsDto>> GetStatsAsync()
    {
        var now = DateTime.UtcNow;
        var startOfToday = new DateTime(now.Year, now.Month, now.Day, 0, 0, 0, DateTimeKind.Utc);
        var startOfMonth = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);

        // Revenue today and month
        var todayRevenue = await _db.Payments.AsNoTracking()
            .Where(p => p.Status == PaymentStatus.PAID && p.PaidAt >= startOfToday)
            .SumAsync(p => p.Amount);

        var monthRevenue = await _db.Payments.AsNoTracking()
            .Where(p => p.Status == PaymentStatus.PAID && p.PaidAt >= startOfMonth)
            .SumAsync(p => p.Amount);

        var totalStations = await _db.Stations.AsNoTracking().CountAsync();
        var activeStations = await _db.Stations.AsNoTracking().CountAsync(s => s.Status == StationStatus.ACTIVE);

        var totalLockers = await _db.Lockers.AsNoTracking().CountAsync();
        var availableLockers = await _db.Lockers.AsNoTracking().CountAsync(l => l.BusinessStatus == LockerBusinessStatus.AVAILABLE);
        var occupiedLockers = await _db.Lockers.AsNoTracking().CountAsync(l => l.BusinessStatus == LockerBusinessStatus.OCCUPIED || l.BusinessStatus == LockerBusinessStatus.RESERVED);
        var maintenanceLockers = await _db.Lockers.AsNoTracking().CountAsync(l => l.BusinessStatus == LockerBusinessStatus.MAINTENANCE || l.BusinessStatus == LockerBusinessStatus.DISABLED);

        double occupancyRate = totalLockers > 0 ? (double)occupiedLockers / totalLockers * 100 : 0;

        var activeStatuses = new[] { BookingStatus.CONFIRMED, BookingStatus.STORED };
        var totalActiveBookings = await _db.Bookings.AsNoTracking()
            .CountAsync(b => activeStatuses.Contains(b.Status));

        var offlineIoT = await _db.IoTDevices.AsNoTracking()
            .CountAsync(d => d.Status == "OFFLINE" || d.Status == "ERROR");

        var openIncidents = await _db.SecurityIncidents.AsNoTracking()
            .CountAsync(i => i.Status == IncidentStatus.OPEN || i.Status == IncidentStatus.IN_PROGRESS);

        var pendingAbandoned = await _db.AbandonedPropertyRecords.AsNoTracking()
            .CountAsync(a => a.Status == AbandonedPropertyStatus.REPORTED || a.Status == AbandonedPropertyStatus.PENDING_APPROVAL);

        var stats = new AdminDashboardStatsDto
        {
            TodayRevenue = todayRevenue,
            MonthRevenue = monthRevenue,
            TotalStations = totalStations,
            ActiveStations = activeStations,
            TotalLockers = totalLockers,
            AvailableLockers = availableLockers,
            OccupiedLockers = occupiedLockers,
            MaintenanceLockers = maintenanceLockers,
            OverallOccupancyRate = Math.Round(occupancyRate, 2),
            TotalActiveBookings = totalActiveBookings,
            OfflineIoTDevices = offlineIoT,
            OpenSecurityIncidents = openIncidents,
            PendingAbandonedProperties = pendingAbandoned
        };

        return ApiResponse<AdminDashboardStatsDto>.SuccessResponse(stats);
    }

    public async Task<ApiResponse<List<RevenueChartItemDto>>> GetRevenueChartAsync(int days = 30)
    {
        var startDate = DateTime.UtcNow.Date.AddDays(-days + 1);

        var paidPayments = await _db.Payments.AsNoTracking()
            .Where(p => p.Status == PaymentStatus.PAID && p.PaidAt.HasValue && p.PaidAt.Value >= startDate)
            .ToListAsync();

        var result = new List<RevenueChartItemDto>();
        for (int i = 0; i < days; i++)
        {
            var date = startDate.AddDays(i);
            var dayPayments = paidPayments.Where(p => p.PaidAt!.Value.Date == date.Date).ToList();

            result.Add(new RevenueChartItemDto
            {
                Date = date.ToString("yyyy-MM-dd"),
                Revenue = dayPayments.Sum(p => p.Amount),
                BookingCount = dayPayments.Count
            });
        }

        return ApiResponse<List<RevenueChartItemDto>>.SuccessResponse(result);
    }

    public async Task<ApiResponse<List<StationOccupancyItemDto>>> GetStationOccupanciesAsync()
    {
        var stations = await _db.Stations.AsNoTracking()
            .Include(s => s.Lockers)
            .ToListAsync();

        var result = stations.Select(s =>
        {
            int total = s.Lockers.Count;
            int occupied = s.Lockers.Count(l => l.BusinessStatus == LockerBusinessStatus.OCCUPIED || l.BusinessStatus == LockerBusinessStatus.RESERVED);
            double rate = total > 0 ? (double)occupied / total * 100 : 0;

            return new StationOccupancyItemDto
            {
                StationId = s.Id,
                StationName = s.Name,
                TotalLockers = total,
                OccupiedLockers = occupied,
                OccupancyRate = Math.Round(rate, 2)
            };
        }).ToList();

        return ApiResponse<List<StationOccupancyItemDto>>.SuccessResponse(result);
    }
}
