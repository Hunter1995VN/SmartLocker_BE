namespace Application.DTOs.Admin;

public class AdminDashboardStatsDto
{
    public decimal TodayRevenue { get; set; }
    public decimal MonthRevenue { get; set; }
    public int TotalStations { get; set; }
    public int ActiveStations { get; set; }
    public int TotalLockers { get; set; }
    public int AvailableLockers { get; set; }
    public int OccupiedLockers { get; set; }
    public int MaintenanceLockers { get; set; }
    public double OverallOccupancyRate { get; set; }
    public int TotalActiveBookings { get; set; }
    public int OfflineIoTDevices { get; set; }
    public int OpenSecurityIncidents { get; set; }
    public int PendingAbandonedProperties { get; set; }
}

public class RevenueChartItemDto
{
    public string Date { get; set; } = string.Empty;
    public decimal Revenue { get; set; }
    public int BookingCount { get; set; }
}

public class StationOccupancyItemDto
{
    public Guid StationId { get; set; }
    public string StationName { get; set; } = string.Empty;
    public int TotalLockers { get; set; }
    public int OccupiedLockers { get; set; }
    public double OccupancyRate { get; set; }
}
