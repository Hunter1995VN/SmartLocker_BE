namespace Application.DTOs.Report;

/// <summary>
/// UC-A13: DTOs cho Phân tích Tỷ lệ Lấp đầy (Occupancy Rate) và Tần suất Xoay vòng Ngăn tủ (Locker Turnover Rate).
/// </summary>
public class OccupancyTurnoverFilterRequest
{
    public DateTime? FromDate { get; set; }
    public DateTime? ToDate { get; set; }
    public Guid? StationId { get; set; }
    /// <summary>
    /// ALL | S | M | L
    /// </summary>
    public string? LockerSize { get; set; }
    /// <summary>
    /// DAY | WEEK | MONTH
    /// </summary>
    public string Granularity { get; set; } = "DAY";
}

public class OccupancyTurnoverOverviewDto
{
    public DateTime FromDate { get; set; }
    public DateTime ToDate { get; set; }
    public string Granularity { get; set; } = "DAY";
    public Guid? StationId { get; set; }
    public string StationFilterLabel { get; set; } = "Toàn mạng lưới trạm SmartLocker";
    public string LockerSizeFilter { get; set; } = "ALL";
    public int PeriodDays { get; set; }
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;

    // 1. Trạng thái Lấp đầy Tức thời (Real-time Snapshot)
    public int TotalPhysicalLockers { get; set; }
    public int ServiceableLockers { get; set; }
    public int AvailableNowLockers { get; set; }
    public int OccupiedNowLockers { get; set; }
    public int ReservedNowLockers { get; set; }
    public int OutOfServiceLockers { get; set; }
    public decimal LivePhysicalOccupancyPercent { get; set; }
    public decimal LiveServiceableOccupancyPercent { get; set; }

    // 2. Tỷ lệ Lấp đầy Theo Thời gian Thực tế (Time-Weighted Occupancy - Locker-Hours)
    public decimal AvailableCapacityLockerHours { get; set; }
    public decimal TotalOccupiedLockerHours { get; set; }
    public decimal TimeWeightedOccupancyPercent { get; set; }
    public decimal PreviousPeriodOccupancyPercent { get; set; }
    public decimal OccupancyDeltaPercent { get; set; }

    // 3. Tần suất Xoay vòng Ngăn tủ & Hiệu suất Khai thác (Locker Turnover & Velocity)
    public int TotalCompletedAndActiveCycles { get; set; }
    public int PreviousPeriodCycles { get; set; }
    public decimal CyclesGrowthPercent { get; set; }
    public decimal PeriodTurnoverPerLocker { get; set; }
    public decimal DailyTurnoverRate { get; set; }
    public decimal AverageStorageDurationHours { get; set; }
    public decimal AverageTurnaroundIdleHours { get; set; }
    public int OverdueCycleCount { get; set; }
    public decimal OverdueCycleRatePercent { get; set; }

    // 4. Hiệu suất Kinh tế trên Công suất Ngăn tủ (RevPAL & RevPOLH)
    public decimal NetRevenueInPeriod { get; set; }
    public decimal RevenuePerAvailableLocker { get; set; }
    public decimal RevenuePerOccupiedHour { get; set; }
    public string PeakOccupancyHourLabel { get; set; } = "—";
    public string PeakDayOfWeekLabel { get; set; } = "—";

    // 5. Danh mục & Dữ liệu Phân tích Đa chiều
    public List<ReportStationOptionDto> Stations { get; set; } = new();
    public List<OccupancyTimeSeriesPointDto> TimeSeries { get; set; } = new();
    public List<HourlyOccupancyProfileDto> HourlyProfile { get; set; } = new();
    public List<DayOfWeekUtilizationDto> DayOfWeekProfile { get; set; } = new();
    public List<StationOccupancyTurnoverDto> StationBreakdown { get; set; } = new();
    public List<SizeOccupancyTurnoverDto> SizeBreakdown { get; set; } = new();
    public List<LockerUnitTurnoverItemDto> LockerTurnoverItems { get; set; } = new();
}

public class OccupancyTimeSeriesPointDto
{
    public string PeriodKey { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public decimal OccupancyRatePercent { get; set; }
    public int TotalCycles { get; set; }
    public decimal DailyTurnoverRate { get; set; }
    public decimal OccupiedLockerHours { get; set; }
    public decimal AvailableLockerHours { get; set; }
    public decimal AverageDurationHours { get; set; }
    public decimal NetRevenue { get; set; }
}

public class HourlyOccupancyProfileDto
{
    public int Hour { get; set; }
    public string HourLabel { get; set; } = string.Empty;
    public decimal AvgActiveLockers { get; set; }
    public decimal EstimatedOccupancyPercent { get; set; }
    public int CheckInCount { get; set; }
    public int CheckOutCount { get; set; }
}

public class DayOfWeekUtilizationDto
{
    public int DayIndex { get; set; }
    public string DayLabel { get; set; } = string.Empty;
    public int TotalCycles { get; set; }
    public decimal OccupiedHours { get; set; }
    public decimal AvgOccupancyPercent { get; set; }
    public decimal ShareOfCyclesPercent { get; set; }
}

public class StationOccupancyTurnoverDto
{
    public Guid StationId { get; set; }
    public string StationName { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public int TotalLockers { get; set; }
    public int ServiceableLockers { get; set; }
    public int OutOfServiceLockers { get; set; }
    public int LiveOccupiedCount { get; set; }
    public decimal LiveOccupancyPercent { get; set; }
    public decimal TimeWeightedOccupancyPercent { get; set; }
    public int TotalCycles { get; set; }
    public decimal TurnoverPerLocker { get; set; }
    public decimal DailyTurnoverRate { get; set; }
    public decimal AvgDurationHours { get; set; }
    public decimal RevenuePerAvailableLocker { get; set; }
    /// <summary>
    /// OVERLOADED | OPTIMAL | MODERATE | UNDERUTILIZED
    /// </summary>
    public string CapacityStatus { get; set; } = "MODERATE";
    public string CapacityRecommendation { get; set; } = string.Empty;
}

public class SizeOccupancyTurnoverDto
{
    public string Size { get; set; } = string.Empty;
    public string SizeLabel { get; set; } = string.Empty;
    public int TotalLockers { get; set; }
    public int ServiceableLockers { get; set; }
    public int LiveOccupiedCount { get; set; }
    public decimal TimeWeightedOccupancyPercent { get; set; }
    public int TotalCycles { get; set; }
    public decimal TurnoverPerLocker { get; set; }
    public decimal DailyTurnoverRate { get; set; }
    public decimal AvgDurationHours { get; set; }
    public decimal OverdueRatePercent { get; set; }
    public decimal RevenuePerAvailableLocker { get; set; }
}

public class LockerUnitTurnoverItemDto
{
    public Guid LockerId { get; set; }
    public string LockerCode { get; set; } = string.Empty;
    public Guid StationId { get; set; }
    public string StationName { get; set; } = string.Empty;
    public string Size { get; set; } = string.Empty;
    public string BusinessStatus { get; set; } = string.Empty;
    public string HealthStatus { get; set; } = string.Empty;
    public string DoorState { get; set; } = string.Empty;
    public int CycleCount { get; set; }
    public decimal DailyTurnoverRate { get; set; }
    public decimal OccupiedHours { get; set; }
    public decimal OccupancyRatePercent { get; set; }
    public decimal AvgDurationHours { get; set; }
    public decimal AvgIdleGapHours { get; set; }
    public decimal GeneratedRevenue { get; set; }
    public DateTime? LastUsedAt { get; set; }
    /// <summary>
    /// HOT | OPTIMAL | COLD | IDLE | MAINTENANCE
    /// </summary>
    public string UtilizationTier { get; set; } = "IDLE";
}
