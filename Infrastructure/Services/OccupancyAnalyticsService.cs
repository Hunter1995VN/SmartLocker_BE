namespace Infrastructure.Services;

using System.Globalization;
using System.Security;
using System.Text;
using System.Text.Json;
using Application.DTOs.Common;
using Application.DTOs.Report;
using Application.Interfaces;
using Domain.Entities;
using Domain.Enums;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// UC-A13: Phân tích Tỷ lệ Lấp đầy (Occupancy Rate - Live &amp; Time-Weighted Locker-Hours)
/// và Tần suất Xoay vòng Ngăn tủ (Locker Turnover Rate, Dwell Time, Idle Turnaround &amp; RevPAL).
/// Truy vấn động 100% từ SQL Server, không thay đổi schema CSDL.
/// </summary>
public class OccupancyAnalyticsService : IOccupancyAnalyticsService
{
    private static readonly TimeSpan VnOffset = TimeSpan.FromHours(7);

    private static readonly HashSet<BookingStatus> UtilizedBookingStatuses = new()
    {
        BookingStatus.CONFIRMED,
        BookingStatus.STORED,
        BookingStatus.COMPLETED,
        BookingStatus.COMPLETED_MANUAL,
        BookingStatus.ABANDONED
    };

    private readonly ISmartLockerDbContext _db;
    private readonly IAuditLogService _auditLog;

    public OccupancyAnalyticsService(ISmartLockerDbContext db, IAuditLogService auditLog)
    {
        _db = db;
        _auditLog = auditLog;
    }

    private static DateTime ToVnTime(DateTime utc) => utc.Add(VnOffset);

    private static bool IsServiceableLocker(Locker l) =>
        l.BusinessStatus != LockerBusinessStatus.MAINTENANCE &&
        l.BusinessStatus != LockerBusinessStatus.DISABLED &&
        l.BusinessStatus != LockerBusinessStatus.BLOCKED;

    private static bool IsLiveOccupiedOrReserved(Locker l) =>
        l.BusinessStatus == LockerBusinessStatus.OCCUPIED ||
        l.BusinessStatus == LockerBusinessStatus.RESERVED;

    private static (DateTime StartUtc, DateTime EndUtc) GetEffectiveIntervalUtc(Booking b, DateTime nowUtc)
    {
        var start = b.CheckedInAt.HasValue && b.CheckedInAt.Value < b.StartAt
            ? b.CheckedInAt.Value
            : b.StartAt;

        DateTime end;
        if (b.CheckedOutAt.HasValue)
        {
            end = b.CheckedOutAt.Value;
        }
        else if (b.Status == BookingStatus.STORED || b.Status == BookingStatus.ABANDONED)
        {
            end = b.EndAt > nowUtc ? b.EndAt : nowUtc;
        }
        else
        {
            end = b.EndAt;
        }

        if (end <= start)
        {
            end = start.AddMinutes(15);
        }

        return (start, end);
    }

    private static decimal ComputeOverlapHours(
        DateTime bStartUtc,
        DateTime bEndUtc,
        DateTime winStartUtc,
        DateTime winEndUtc)
    {
        if (winEndUtc <= winStartUtc || bEndUtc <= winStartUtc || bStartUtc >= winEndUtc)
            return 0m;

        var s = bStartUtc > winStartUtc ? bStartUtc : winStartUtc;
        var e = bEndUtc < winEndUtc ? bEndUtc : winEndUtc;
        return e > s ? (decimal)(e - s).TotalHours : 0m;
    }

    private static bool OverlapsWindow(
        DateTime bStartUtc,
        DateTime bEndUtc,
        DateTime winStartUtc,
        DateTime winEndUtc) =>
        bStartUtc < winEndUtc && bEndUtc > winStartUtc;

    public async Task<ApiResponse<OccupancyTurnoverOverviewDto>> GetOccupancyTurnoverOverviewAsync(
        OccupancyTurnoverFilterRequest request)
    {
        var nowUtc = DateTime.UtcNow;
        var nowVn = ToVnTime(nowUtc);

        var toDateVn = (request.ToDate?.Date) ?? nowVn.Date;
        var fromDateVn = (request.FromDate?.Date) ?? toDateVn.AddDays(-29);
        if (fromDateVn > toDateVn)
        {
            (fromDateVn, toDateVn) = (toDateVn, fromDateVn);
        }

        var granularity = (request.Granularity ?? "DAY").Trim().ToUpperInvariant();
        if (granularity is not ("DAY" or "WEEK" or "MONTH"))
        {
            granularity = "DAY";
        }

        var sizeFilter = string.IsNullOrWhiteSpace(request.LockerSize)
            ? "ALL"
            : request.LockerSize.Trim().ToUpperInvariant();
        LockerSize? parsedSize = Enum.TryParse<LockerSize>(sizeFilter, true, out var szEnum)
            ? szEnum
            : null;

        // Mốc UTC của kỳ báo cáo theo giờ Việt Nam (00:00 -> 24:00 ICT)
        var periodStartUtc = DateTime.SpecifyKind(fromDateVn, DateTimeKind.Unspecified) - VnOffset;
        var rawPeriodEndUtc = DateTime.SpecifyKind(toDateVn.AddDays(1), DateTimeKind.Unspecified) - VnOffset;
        // Giới hạn thời điểm kết thúc thực tế ở hiện tại để không làm loãng mẫu số Locker-Hours bởi các giờ tương lai chưa diễn ra
        var effectivePeriodEndUtc = rawPeriodEndUtc > nowUtc ? nowUtc : rawPeriodEndUtc;
        if (effectivePeriodEndUtc <= periodStartUtc)
        {
            effectivePeriodEndUtc = periodStartUtc.AddHours(1);
        }

        var periodDays = Math.Max(1, (int)(toDateVn - fromDateVn).TotalDays + 1);
        var elapsedPeriodHours = Math.Max(1m, (decimal)(effectivePeriodEndUtc - periodStartUtc).TotalHours);
        var elapsedPeriodDays = Math.Max(0.25m, elapsedPeriodHours / 24m);

        // Kỳ liền trước có cùng độ dài để so sánh chênh lệch lấp đầy & vòng quay
        var prevStartVn = fromDateVn.AddDays(-periodDays);
        var prevStartUtc = DateTime.SpecifyKind(prevStartVn, DateTimeKind.Unspecified) - VnOffset;
        var prevEndUtc = periodStartUtc;
        var prevElapsedHours = Math.Max(1m, (decimal)(prevEndUtc - prevStartUtc).TotalHours);

        // 1. Tải danh sách Trạm & Ngăn tủ vật lý
        var allStations = await _db.Stations.AsNoTracking()
            .OrderBy(s => s.Name)
            .ToListAsync();

        var stationOptions = allStations
            .Select(s => new ReportStationOptionDto { Id = s.Id, Name = s.Name })
            .ToList();

        var stationDict = allStations.ToDictionary(s => s.Id);
        var stationLabel = "Toàn mạng lưới trạm SmartLocker";
        if (request.StationId.HasValue && stationDict.TryGetValue(request.StationId.Value, out var selectedSt))
        {
            stationLabel = selectedSt.Name;
        }

        var lockersQuery = _db.Lockers.AsNoTracking().AsQueryable();
        if (request.StationId.HasValue)
        {
            lockersQuery = lockersQuery.Where(l => l.StationId == request.StationId.Value);
        }
        if (parsedSize.HasValue)
        {
            lockersQuery = lockersQuery.Where(l => l.Size == parsedSize.Value);
        }

        var lockers = await lockersQuery
            .OrderBy(l => l.LockerCode)
            .ToListAsync();

        int totalPhysicalLockers = lockers.Count;
        int serviceableLockers = lockers.Count(IsServiceableLocker);
        int availableNow = lockers.Count(l => l.BusinessStatus == LockerBusinessStatus.AVAILABLE);
        int occupiedNow = lockers.Count(l => l.BusinessStatus == LockerBusinessStatus.OCCUPIED);
        int reservedNow = lockers.Count(l => l.BusinessStatus == LockerBusinessStatus.RESERVED);
        int outOfServiceNow = lockers.Count(l => !IsServiceableLocker(l));

        decimal livePhysicalOccPct = totalPhysicalLockers > 0
            ? Math.Round((decimal)(occupiedNow + reservedNow) / totalPhysicalLockers * 100m, 2)
            : 0m;
        decimal liveServiceableOccPct = serviceableLockers > 0
            ? Math.Min(100m, Math.Round((decimal)(occupiedNow + reservedNow) / serviceableLockers * 100m, 2))
            : 0m;

        // 2. Tải danh sách Bookings có giao thoa với [prevStartUtc, rawPeriodEndUtc]
        var bookingsQuery = _db.Bookings.AsNoTracking()
            .Where(b =>
                UtilizedBookingStatuses.Contains(b.Status) &&
                b.StartAt < rawPeriodEndUtc &&
                (
                    (b.CheckedOutAt.HasValue && b.CheckedOutAt.Value > prevStartUtc) ||
                    (!b.CheckedOutAt.HasValue && b.EndAt > prevStartUtc) ||
                    b.Status == BookingStatus.STORED ||
                    b.Status == BookingStatus.ABANDONED
                ));

        if (request.StationId.HasValue)
        {
            bookingsQuery = bookingsQuery.Where(b => b.StationId == request.StationId.Value);
        }
        if (parsedSize.HasValue)
        {
            bookingsQuery = bookingsQuery.Where(b => b.Size == parsedSize.Value);
        }

        var rawBookings = await bookingsQuery.ToListAsync();

        // Tải doanh thu ròng theo BookingId để tính RevPAL, RevPOLH & Doanh thu từng ô tủ
        var relevantBookingIds = rawBookings.Select(b => b.Id).ToList();
        var bookingNetRevenueDict = new Dictionary<Guid, decimal>();
        if (relevantBookingIds.Count > 0)
        {
            var payments = await _db.Payments.AsNoTracking()
                .Where(p => relevantBookingIds.Contains(p.BookingId) &&
                            (p.Status == PaymentStatus.PAID || p.Status == PaymentStatus.REFUNDED))
                .Select(p => new { p.BookingId, p.Kind, p.Status, p.Amount })
                .ToListAsync();

            foreach (var grp in payments.GroupBy(p => p.BookingId))
            {
                decimal gross = grp
                    .Where(x => x.Status == PaymentStatus.PAID && x.Kind != PaymentKind.REFUND)
                    .Sum(x => x.Amount);
                decimal refund = grp
                    .Where(x => x.Kind == PaymentKind.REFUND &&
                                (x.Status == PaymentStatus.REFUNDED || x.Status == PaymentStatus.PAID))
                    .Sum(x => x.Amount);
                bookingNetRevenueDict[grp.Key] = Math.Max(0m, gross - refund);
            }
        }

        decimal GetBookingRevenue(Booking b) =>
            bookingNetRevenueDict.TryGetValue(b.Id, out var rev) ? rev : b.BaseAmount;

        // Chuẩn bị khoảng hiệu lực cho từng booking
        var enrichedBookings = rawBookings
            .Select(b =>
            {
                var (sUtc, eUtc) = GetEffectiveIntervalUtc(b, nowUtc);
                return new
                {
                    Booking = b,
                    StartUtc = sUtc,
                    EndUtc = eUtc,
                    TotalDurationHours = Math.Max(0.25m, (decimal)(eUtc - sUtc).TotalHours),
                    NetRevenue = GetBookingRevenue(b)
                };
            })
            .ToList();

        // Lọc các booking giao thoa với kỳ báo cáo chính & kỳ liền trước
        var currentBookings = enrichedBookings
            .Where(x => OverlapsWindow(x.StartUtc, x.EndUtc, periodStartUtc, rawPeriodEndUtc))
            .ToList();

        var previousBookings = enrichedBookings
            .Where(x => OverlapsWindow(x.StartUtc, x.EndUtc, prevStartUtc, prevEndUtc))
            .ToList();

        // 3. Tính toán Tỷ lệ Lấp đầy Theo Thời gian (Time-Weighted Occupancy - Locker-Hours)
        int effectiveCapacityUnits = serviceableLockers > 0 ? serviceableLockers : totalPhysicalLockers;
        decimal availableCapacityHours = Math.Round(effectiveCapacityUnits * elapsedPeriodHours, 2);
        decimal totalOccupiedHours = Math.Round(
            currentBookings.Sum(x => ComputeOverlapHours(x.StartUtc, x.EndUtc, periodStartUtc, effectivePeriodEndUtc)),
            2);

        decimal timeWeightedOccPct = availableCapacityHours > 0
            ? Math.Min(100m, Math.Round(totalOccupiedHours / availableCapacityHours * 100m, 2))
            : 0m;

        decimal prevAvailableHours = effectiveCapacityUnits * prevElapsedHours;
        decimal prevOccupiedHours = previousBookings
            .Sum(x => ComputeOverlapHours(x.StartUtc, x.EndUtc, prevStartUtc, prevEndUtc));
        decimal prevOccPct = prevAvailableHours > 0
            ? Math.Min(100m, Math.Round(prevOccupiedHours / prevAvailableHours * 100m, 2))
            : 0m;
        decimal occDeltaPct = Math.Round(timeWeightedOccPct - prevOccPct, 2);

        // 4. Tính toán Tần suất Xoay vòng Ngăn tủ (Turnover Rate, Dwell Time, Idle Turnaround)
        int currentCycles = currentBookings.Count;
        int prevCycles = previousBookings.Count;
        decimal cyclesGrowthPct = prevCycles > 0
            ? Math.Round((decimal)(currentCycles - prevCycles) / prevCycles * 100m, 2)
            : (currentCycles > 0 ? 100m : 0m);

        decimal periodTurnoverPerLocker = effectiveCapacityUnits > 0
            ? Math.Round((decimal)currentCycles / effectiveCapacityUnits, 2)
            : 0m;

        decimal dailyTurnoverRate = effectiveCapacityUnits > 0 && elapsedPeriodDays > 0
            ? Math.Round((decimal)currentCycles / (effectiveCapacityUnits * elapsedPeriodDays), 3)
            : 0m;

        decimal avgStorageDurationHours = currentCycles > 0
            ? Math.Round(currentBookings.Average(x => x.TotalDurationHours), 2)
            : 0m;

        int overdueCycles = currentBookings.Count(x => x.Booking.IsOverdue || x.Booking.Status == BookingStatus.ABANDONED);
        decimal overdueRatePct = currentCycles > 0
            ? Math.Round((decimal)overdueCycles / currentCycles * 100m, 2)
            : 0m;

        // Tính thời gian trống trung bình giữa 2 lượt thuê liên tiếp trên cùng ngăn tủ (Idle Turnaround Hours)
        var idleGapsAll = new List<decimal>();
        var perLockerIdleGapDict = new Dictionary<Guid, decimal>();
        foreach (var lockerGrp in currentBookings
                     .Where(x => x.Booking.LockerId.HasValue)
                     .GroupBy(x => x.Booking.LockerId!.Value))
        {
            var ordered = lockerGrp.OrderBy(x => x.StartUtc).ToList();
            var lockerGaps = new List<decimal>();
            for (int i = 1; i < ordered.Count; i++)
            {
                var gapHours = (decimal)(ordered[i].StartUtc - ordered[i - 1].EndUtc).TotalHours;
                if (gapHours >= 0m)
                {
                    lockerGaps.Add(gapHours);
                    idleGapsAll.Add(gapHours);
                }
            }
            if (lockerGaps.Count > 0)
            {
                perLockerIdleGapDict[lockerGrp.Key] = Math.Round(lockerGaps.Average(), 2);
            }
        }

        decimal avgTurnaroundIdleHours = idleGapsAll.Count > 0
            ? Math.Round(idleGapsAll.Average(), 2)
            : 0m;

        // Hiệu suất kinh tế trên công suất ngăn tủ (RevPAL & RevPOLH)
        decimal netRevenueInPeriod = currentBookings.Sum(x => x.NetRevenue);
        decimal revPal = effectiveCapacityUnits > 0
            ? Math.Round(netRevenueInPeriod / effectiveCapacityUnits, 0)
            : 0m;
        decimal revPolh = totalOccupiedHours > 0
            ? Math.Round(netRevenueInPeriod / totalOccupiedHours, 0)
            : 0m;

        // 5. Chuỗi thời gian (DAY / WEEK / MONTH)
        var buckets = BuildTimeBuckets(fromDateVn, toDateVn, granularity);
        var timeSeries = new List<OccupancyTimeSeriesPointDto>(buckets.Count);
        foreach (var b in buckets)
        {
            var winStartUtc = DateTime.SpecifyKind(b.StartDate, DateTimeKind.Unspecified) - VnOffset;
            var rawWinEndUtc = DateTime.SpecifyKind(b.EndDate.AddDays(1), DateTimeKind.Unspecified) - VnOffset;
            var winEndUtc = rawWinEndUtc > nowUtc ? nowUtc : rawWinEndUtc;

            if (winStartUtc >= nowUtc)
            {
                timeSeries.Add(new OccupancyTimeSeriesPointDto
                {
                    PeriodKey = b.Key,
                    Label = b.Label
                });
                continue;
            }

            decimal bucketHours = Math.Max(1m, (decimal)(winEndUtc - winStartUtc).TotalHours);
            decimal bucketDays = Math.Max(0.25m, bucketHours / 24m);
            decimal bucketAvailHours = Math.Round(effectiveCapacityUnits * bucketHours, 2);

            var overlapping = currentBookings
                .Where(x => OverlapsWindow(x.StartUtc, x.EndUtc, winStartUtc, rawWinEndUtc))
                .ToList();

            decimal bucketOccHours = Math.Round(
                overlapping.Sum(x => ComputeOverlapHours(x.StartUtc, x.EndUtc, winStartUtc, winEndUtc)),
                2);
            decimal bucketOccPct = bucketAvailHours > 0
                ? Math.Min(100m, Math.Round(bucketOccHours / bucketAvailHours * 100m, 2))
                : 0m;

            int bucketCycles = overlapping.Count;
            decimal bucketDailyTurn = effectiveCapacityUnits > 0
                ? Math.Round((decimal)bucketCycles / (effectiveCapacityUnits * bucketDays), 3)
                : 0m;
            decimal bucketAvgDur = bucketCycles > 0
                ? Math.Round(overlapping.Average(x => x.TotalDurationHours), 2)
                : 0m;
            decimal bucketRev = overlapping
                .Where(x => ToVnTime(x.StartUtc).Date >= b.StartDate && ToVnTime(x.StartUtc).Date <= b.EndDate)
                .Sum(x => x.NetRevenue);

            timeSeries.Add(new OccupancyTimeSeriesPointDto
            {
                PeriodKey = b.Key,
                Label = b.Label,
                OccupancyRatePercent = bucketOccPct,
                TotalCycles = bucketCycles,
                DailyTurnoverRate = bucketDailyTurn,
                OccupiedLockerHours = bucketOccHours,
                AvailableLockerHours = bucketAvailHours,
                AverageDurationHours = bucketAvgDur,
                NetRevenue = bucketRev
            });
        }

        // 6. Phân bố Tỷ lệ Lấp đầy theo 24 Khung giờ (00:00 - 23:00 ICT)
        var hourlyOccupiedHoursSum = new decimal[24];
        var hourlyObservedDaysCount = new int[24];
        var hourlyCheckIns = new int[24];
        var hourlyCheckOuts = new int[24];

        for (var d = fromDateVn; d <= toDateVn; d = d.AddDays(1))
        {
            for (int h = 0; h < 24; h++)
            {
                var slotStartUtc = DateTime.SpecifyKind(d.AddHours(h), DateTimeKind.Unspecified) - VnOffset;
                var slotEndUtc = slotStartUtc.AddHours(1);
                if (slotStartUtc >= nowUtc) continue;

                var clippedSlotEnd = slotEndUtc > nowUtc ? nowUtc : slotEndUtc;
                hourlyObservedDaysCount[h]++;

                foreach (var bk in currentBookings)
                {
                    if (bk.StartUtc < clippedSlotEnd && bk.EndUtc > slotStartUtc)
                    {
                        hourlyOccupiedHoursSum[h] += ComputeOverlapHours(bk.StartUtc, bk.EndUtc, slotStartUtc, clippedSlotEnd);
                    }
                }
            }
        }

        foreach (var bk in currentBookings)
        {
            var checkInVn = ToVnTime(bk.StartUtc);
            if (checkInVn.Date >= fromDateVn && checkInVn.Date <= toDateVn)
            {
                hourlyCheckIns[checkInVn.Hour]++;
            }
            if (bk.Booking.CheckedOutAt.HasValue)
            {
                var checkOutVn = ToVnTime(bk.Booking.CheckedOutAt.Value);
                if (checkOutVn.Date >= fromDateVn && checkOutVn.Date <= toDateVn)
                {
                    hourlyCheckOuts[checkOutVn.Hour]++;
                }
            }
        }

        var hourlyProfile = Enumerable.Range(0, 24)
            .Select(h =>
            {
                int obsDays = Math.Max(1, hourlyObservedDaysCount[h]);
                decimal avgActive = Math.Round(hourlyOccupiedHoursSum[h] / obsDays, 2);
                decimal estOccPct = effectiveCapacityUnits > 0
                    ? Math.Min(100m, Math.Round(avgActive / effectiveCapacityUnits * 100m, 2))
                    : 0m;
                return new HourlyOccupancyProfileDto
                {
                    Hour = h,
                    HourLabel = $"{h:00}:00 - {h:00}:59",
                    AvgActiveLockers = avgActive,
                    EstimatedOccupancyPercent = estOccPct,
                    CheckInCount = hourlyCheckIns[h],
                    CheckOutCount = hourlyCheckOuts[h]
                };
            })
            .ToList();

        var peakHour = hourlyProfile
            .OrderByDescending(h => h.EstimatedOccupancyPercent)
            .ThenByDescending(h => h.CheckInCount)
            .FirstOrDefault();
        var peakHourLabel = peakHour != null && (peakHour.AvgActiveLockers > 0 || peakHour.CheckInCount > 0)
            ? $"{peakHour.HourLabel} ({peakHour.EstimatedOccupancyPercent:0.0}% lấp đầy)"
            : "Chưa phát sinh lưu trữ";

        // 7. Phân bố theo Thứ trong tuần (Thứ 2 -> Chủ nhật)
        var vnDayLabels = new Dictionary<int, string>
        {
            { 1, "Thứ 2" },
            { 2, "Thứ 3" },
            { 3, "Thứ 4" },
            { 4, "Thứ 5" },
            { 5, "Thứ 6" },
            { 6, "Thứ 7" },
            { 7, "Chủ nhật" }
        };
        var dowOccHours = new decimal[8];
        var dowAvailHours = new decimal[8];
        var dowCycles = new int[8];

        for (var d = fromDateVn; d <= toDateVn; d = d.AddDays(1))
        {
            int idx = d.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)d.DayOfWeek;
            var dayStartUtc = DateTime.SpecifyKind(d, DateTimeKind.Unspecified) - VnOffset;
            var rawDayEndUtc = dayStartUtc.AddDays(1);
            if (dayStartUtc >= nowUtc) continue;
            var dayEndUtc = rawDayEndUtc > nowUtc ? nowUtc : rawDayEndUtc;

            decimal dayHours = Math.Max(1m, (decimal)(dayEndUtc - dayStartUtc).TotalHours);
            dowAvailHours[idx] += effectiveCapacityUnits * dayHours;

            foreach (var bk in currentBookings)
            {
                if (OverlapsWindow(bk.StartUtc, bk.EndUtc, dayStartUtc, rawDayEndUtc))
                {
                    dowOccHours[idx] += ComputeOverlapHours(bk.StartUtc, bk.EndUtc, dayStartUtc, dayEndUtc);
                }
                if (ToVnTime(bk.StartUtc).Date == d)
                {
                    dowCycles[idx]++;
                }
            }
        }

        int totalDowCycles = Math.Max(1, dowCycles.Sum());
        var dayOfWeekProfile = Enumerable.Range(1, 7)
            .Select(idx => new DayOfWeekUtilizationDto
            {
                DayIndex = idx,
                DayLabel = vnDayLabels[idx],
                TotalCycles = dowCycles[idx],
                OccupiedHours = Math.Round(dowOccHours[idx], 1),
                AvgOccupancyPercent = dowAvailHours[idx] > 0
                    ? Math.Min(100m, Math.Round(dowOccHours[idx] / dowAvailHours[idx] * 100m, 2))
                    : 0m,
                ShareOfCyclesPercent = currentCycles > 0
                    ? Math.Round((decimal)dowCycles[idx] / totalDowCycles * 100m, 1)
                    : 0m
            })
            .ToList();

        var peakDow = dayOfWeekProfile
            .OrderByDescending(d => d.AvgOccupancyPercent)
            .ThenByDescending(d => d.TotalCycles)
            .FirstOrDefault();
        var peakDowLabel = peakDow != null && (peakDow.TotalCycles > 0 || peakDow.OccupiedHours > 0)
            ? $"{peakDow.DayLabel} ({peakDow.AvgOccupancyPercent:0.0}%)"
            : "—";

        // 8. Bóc tách Hiệu suất Lấp đầy & Vòng quay theo Trạm SmartLocker
        var targetStations = request.StationId.HasValue
            ? allStations.Where(s => s.Id == request.StationId.Value).ToList()
            : allStations;

        var stationBreakdown = targetStations
            .Select(st =>
            {
                var stLockers = lockers.Where(l => l.StationId == st.Id).ToList();
                int stTotal = stLockers.Count;
                int stServiceable = stLockers.Count(IsServiceableLocker);
                int stOut = stTotal - stServiceable;
                int stLiveOcc = stLockers.Count(IsLiveOccupiedOrReserved);
                int stCap = stServiceable > 0 ? stServiceable : stTotal;

                var stBookings = currentBookings.Where(x => x.Booking.StationId == st.Id).ToList();
                decimal stAvailHours = stCap * elapsedPeriodHours;
                decimal stOccHours = stBookings.Sum(x =>
                    ComputeOverlapHours(x.StartUtc, x.EndUtc, periodStartUtc, effectivePeriodEndUtc));

                decimal stLivePct = stCap > 0
                    ? Math.Min(100m, Math.Round((decimal)stLiveOcc / stCap * 100m, 2))
                    : 0m;
                decimal stTimeOccPct = stAvailHours > 0
                    ? Math.Min(100m, Math.Round(stOccHours / stAvailHours * 100m, 2))
                    : 0m;

                int stCycles = stBookings.Count;
                decimal stTurnPerLocker = stCap > 0
                    ? Math.Round((decimal)stCycles / stCap, 2)
                    : 0m;
                decimal stDailyTurn = stCap > 0 && elapsedPeriodDays > 0
                    ? Math.Round((decimal)stCycles / (stCap * elapsedPeriodDays), 3)
                    : 0m;
                decimal stAvgDur = stCycles > 0
                    ? Math.Round(stBookings.Average(x => x.TotalDurationHours), 2)
                    : 0m;
                decimal stRev = stBookings.Sum(x => x.NetRevenue);
                decimal stRevPal = stCap > 0 ? Math.Round(stRev / stCap, 0) : 0m;

                // Chẩn đoán công suất tự động
                decimal diagScore = Math.Max(stLivePct, stTimeOccPct);
                var (capStatus, capRec) = DiagnoseStationCapacity(diagScore, stDailyTurn, stOut);

                return new StationOccupancyTurnoverDto
                {
                    StationId = st.Id,
                    StationName = st.Name,
                    Address = st.Address,
                    TotalLockers = stTotal,
                    ServiceableLockers = stServiceable,
                    OutOfServiceLockers = stOut,
                    LiveOccupiedCount = stLiveOcc,
                    LiveOccupancyPercent = stLivePct,
                    TimeWeightedOccupancyPercent = stTimeOccPct,
                    TotalCycles = stCycles,
                    TurnoverPerLocker = stTurnPerLocker,
                    DailyTurnoverRate = stDailyTurn,
                    AvgDurationHours = stAvgDur,
                    RevenuePerAvailableLocker = stRevPal,
                    CapacityStatus = capStatus,
                    CapacityRecommendation = capRec
                };
            })
            .OrderByDescending(x => x.TimeWeightedOccupancyPercent)
            .ThenByDescending(x => x.TurnoverPerLocker)
            .ToList();

        // 9. Bóc tách theo Kích cỡ Ngăn tủ (S / M / L)
        var sizeMeta = new (LockerSize EnumVal, string Code, string Label)[]
        {
            (LockerSize.S, "S", "Ngăn cỡ Nhỏ (Size S - Balo / Túi xách)"),
            (LockerSize.M, "M", "Ngăn cỡ Trung (Size M - Vali Cabin 20-24 inch)"),
            (LockerSize.L, "L", "Ngăn cỡ Lớn (Size L - Vali Ký gửi 28 inch)")
        };

        var sizeBreakdown = sizeMeta
            .Where(sm => !parsedSize.HasValue || parsedSize.Value == sm.EnumVal)
            .Select(sm =>
            {
                var szLockers = lockers.Where(l => l.Size == sm.EnumVal).ToList();
                int szTotal = szLockers.Count;
                int szServ = szLockers.Count(IsServiceableLocker);
                int szLiveOcc = szLockers.Count(IsLiveOccupiedOrReserved);
                int szCap = szServ > 0 ? szServ : szTotal;

                var szBookings = currentBookings.Where(x => x.Booking.Size == sm.EnumVal).ToList();
                decimal szAvailHours = szCap * elapsedPeriodHours;
                decimal szOccHours = szBookings.Sum(x =>
                    ComputeOverlapHours(x.StartUtc, x.EndUtc, periodStartUtc, effectivePeriodEndUtc));

                decimal szOccPct = szAvailHours > 0
                    ? Math.Min(100m, Math.Round(szOccHours / szAvailHours * 100m, 2))
                    : 0m;
                int szCycles = szBookings.Count;
                int szOverdue = szBookings.Count(x => x.Booking.IsOverdue || x.Booking.Status == BookingStatus.ABANDONED);

                return new SizeOccupancyTurnoverDto
                {
                    Size = sm.Code,
                    SizeLabel = sm.Label,
                    TotalLockers = szTotal,
                    ServiceableLockers = szServ,
                    LiveOccupiedCount = szLiveOcc,
                    TimeWeightedOccupancyPercent = szOccPct,
                    TotalCycles = szCycles,
                    TurnoverPerLocker = szCap > 0 ? Math.Round((decimal)szCycles / szCap, 2) : 0m,
                    DailyTurnoverRate = szCap > 0 && elapsedPeriodDays > 0
                        ? Math.Round((decimal)szCycles / (szCap * elapsedPeriodDays), 3)
                        : 0m,
                    AvgDurationHours = szCycles > 0
                        ? Math.Round(szBookings.Average(x => x.TotalDurationHours), 2)
                        : 0m,
                    OverdueRatePercent = szCycles > 0
                        ? Math.Round((decimal)szOverdue / szCycles * 100m, 1)
                        : 0m,
                    RevenuePerAvailableLocker = szCap > 0
                        ? Math.Round(szBookings.Sum(x => x.NetRevenue) / szCap, 0)
                        : 0m
                };
            })
            .ToList();

        // 10. Bảng Chi tiết Tần suất Xoay vòng Từng Ngăn tủ Vật lý
        var bookingsByLockerId = currentBookings
            .Where(x => x.Booking.LockerId.HasValue)
            .GroupBy(x => x.Booking.LockerId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());

        var lockerTurnoverItems = lockers
            .Select(l =>
            {
                var lBookings = bookingsByLockerId.TryGetValue(l.Id, out var list) ? list : new();
                int cycles = lBookings.Count;
                decimal occHours = Math.Round(
                    lBookings.Sum(x => ComputeOverlapHours(x.StartUtc, x.EndUtc, periodStartUtc, effectivePeriodEndUtc)),
                    2);
                decimal occPct = elapsedPeriodHours > 0
                    ? Math.Min(100m, Math.Round(occHours / elapsedPeriodHours * 100m, 2))
                    : 0m;
                decimal dailyTurn = elapsedPeriodDays > 0
                    ? Math.Round((decimal)cycles / elapsedPeriodDays, 3)
                    : 0m;
                decimal avgDur = cycles > 0
                    ? Math.Round(lBookings.Average(x => x.TotalDurationHours), 2)
                    : 0m;
                decimal idleGap = perLockerIdleGapDict.TryGetValue(l.Id, out var gapVal) ? gapVal : 0m;
                decimal rev = lBookings.Sum(x => x.NetRevenue);
                DateTime? lastUsed = lBookings.Count > 0
                    ? lBookings.Max(x => x.EndUtc > nowUtc ? x.StartUtc : x.EndUtc)
                    : l.LastDoorEventAt;

                string tier;
                if (!IsServiceableLocker(l))
                    tier = "MAINTENANCE";
                else if (occPct >= 65m || dailyTurn >= 0.8m)
                    tier = "HOT";
                else if (occPct >= 25m || cycles >= 2)
                    tier = "OPTIMAL";
                else if (cycles == 1)
                    tier = "COLD";
                else
                    tier = "IDLE";

                var stName = stationDict.TryGetValue(l.StationId, out var stObj) ? stObj.Name : "Trạm không xác định";

                return new LockerUnitTurnoverItemDto
                {
                    LockerId = l.Id,
                    LockerCode = l.LockerCode,
                    StationId = l.StationId,
                    StationName = stName,
                    Size = l.Size.ToString(),
                    BusinessStatus = l.BusinessStatus.ToString(),
                    HealthStatus = l.HealthStatus.ToString(),
                    DoorState = l.DoorState,
                    CycleCount = cycles,
                    DailyTurnoverRate = dailyTurn,
                    OccupiedHours = occHours,
                    OccupancyRatePercent = occPct,
                    AvgDurationHours = avgDur,
                    AvgIdleGapHours = idleGap,
                    GeneratedRevenue = rev,
                    LastUsedAt = lastUsed,
                    UtilizationTier = tier
                };
            })
            .OrderByDescending(x => x.CycleCount)
            .ThenByDescending(x => x.OccupancyRatePercent)
            .ThenBy(x => x.StationName)
            .ThenBy(x => x.LockerCode)
            .ToList();

        var dto = new OccupancyTurnoverOverviewDto
        {
            FromDate = fromDateVn,
            ToDate = toDateVn,
            Granularity = granularity,
            StationId = request.StationId,
            StationFilterLabel = stationLabel,
            LockerSizeFilter = sizeFilter,
            PeriodDays = periodDays,
            GeneratedAt = nowUtc,

            TotalPhysicalLockers = totalPhysicalLockers,
            ServiceableLockers = serviceableLockers,
            AvailableNowLockers = availableNow,
            OccupiedNowLockers = occupiedNow,
            ReservedNowLockers = reservedNow,
            OutOfServiceLockers = outOfServiceNow,
            LivePhysicalOccupancyPercent = livePhysicalOccPct,
            LiveServiceableOccupancyPercent = liveServiceableOccPct,

            AvailableCapacityLockerHours = availableCapacityHours,
            TotalOccupiedLockerHours = totalOccupiedHours,
            TimeWeightedOccupancyPercent = timeWeightedOccPct,
            PreviousPeriodOccupancyPercent = prevOccPct,
            OccupancyDeltaPercent = occDeltaPct,

            TotalCompletedAndActiveCycles = currentCycles,
            PreviousPeriodCycles = prevCycles,
            CyclesGrowthPercent = cyclesGrowthPct,
            PeriodTurnoverPerLocker = periodTurnoverPerLocker,
            DailyTurnoverRate = dailyTurnoverRate,
            AverageStorageDurationHours = avgStorageDurationHours,
            AverageTurnaroundIdleHours = avgTurnaroundIdleHours,
            OverdueCycleCount = overdueCycles,
            OverdueCycleRatePercent = overdueRatePct,

            NetRevenueInPeriod = netRevenueInPeriod,
            RevenuePerAvailableLocker = revPal,
            RevenuePerOccupiedHour = revPolh,
            PeakOccupancyHourLabel = peakHourLabel,
            PeakDayOfWeekLabel = peakDowLabel,

            Stations = stationOptions,
            TimeSeries = timeSeries,
            HourlyProfile = hourlyProfile,
            DayOfWeekProfile = dayOfWeekProfile,
            StationBreakdown = stationBreakdown,
            SizeBreakdown = sizeBreakdown,
            LockerTurnoverItems = lockerTurnoverItems
        };

        return ApiResponse<OccupancyTurnoverOverviewDto>.SuccessResponse(dto);
    }

    public async Task<ExportedReportFileDto> ExportOccupancyReportAsync(
        OccupancyTurnoverFilterRequest request,
        string format,
        Guid actorUserId,
        string? actorEmail,
        string? actorRole)
    {
        var reportRes = await GetOccupancyTurnoverOverviewAsync(request);
        var data = reportRes.Data ?? new OccupancyTurnoverOverviewDto();
        var normFormat = (format ?? "excel").Trim().ToLowerInvariant();
        var dateRangeSlug = $"{data.FromDate:yyyyMMdd}_{data.ToDate:yyyyMMdd}";

        ExportedReportFileDto exported;
        if (normFormat == "csv")
        {
            var csvBytes = BuildCsvBytes(data);
            exported = new ExportedReportFileDto
            {
                Content = csvBytes,
                ContentType = "text/csv; charset=utf-8",
                FileName = $"SmartLocker_LapDay_XoayVongTu_{dateRangeSlug}.csv"
            };
        }
        else if (normFormat == "json")
        {
            var jsonBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(data, new JsonSerializerOptions
            {
                WriteIndented = true
            }));
            exported = new ExportedReportFileDto
            {
                Content = jsonBytes,
                ContentType = "application/json; charset=utf-8",
                FileName = $"SmartLocker_LapDay_XoayVongTu_{dateRangeSlug}.json"
            };
        }
        else
        {
            var xlsBytes = BuildExcelWorkbookBytes(data);
            exported = new ExportedReportFileDto
            {
                Content = xlsBytes,
                ContentType = "application/vnd.ms-excel",
                FileName = $"SmartLocker_BaoCao_LapDay_XoayVongTu_{dateRangeSlug}.xls"
            };
        }

        await _auditLog.LogAsync(
            actorUserId == Guid.Empty ? null : actorUserId,
            actorEmail ?? "admin@smartlocker.vn",
            actorRole ?? "ADMIN",
            "EXPORT_OCCUPANCY_REPORT",
            "OccupancyReport",
            dateRangeSlug,
            $"Xuất báo cáo Lấp đầy & Xoay vòng ngăn tủ ({normFormat.ToUpperInvariant()}) kỳ {data.FromDate:dd/MM/yyyy} - {data.ToDate:dd/MM/yyyy} | Phạm vi: {data.StationFilterLabel} | Lấp đầy TB: {data.TimeWeightedOccupancyPercent:0.00}% | Tổng vòng quay: {data.TotalCompletedAndActiveCycles} lượt ({data.PeriodTurnoverPerLocker:0.00} vòng/tủ).");

        return exported;
    }

    private static (string Status, string Recommendation) DiagnoseStationCapacity(
        decimal occPercent,
        decimal dailyTurnover,
        int outOfServiceCount)
    {
        if (occPercent >= 80m)
        {
            return (
                "OVERLOADED",
                outOfServiceCount > 0
                    ? $"Cảnh báo quá tải công suất ({occPercent:0.0}%). Ưu tiên khôi phục ngay {outOfServiceCount} ô đang bảo trì và mở rộng thêm module tủ."
                    : $"Cảnh báo quá tải công suất ({occPercent:0.0}%). Đề xuất bổ sung thêm module ngăn tủ hoặc áp dụng phụ phí khung giờ cao điểm."
            );
        }

        if (occPercent >= 45m || dailyTurnover >= 0.5m)
        {
            return (
                "OPTIMAL",
                "Hiệu suất khai thác tối ưu. Duy trì lịch bảo trì định kỳ để đảm bảo độ sẵn sàng khóa từ & cảm biến IoT."
            );
        }

        if (occPercent >= 18m || dailyTurnover >= 0.15m)
        {
            return (
                "MODERATE",
                "Công suất trung bình. Khuyến nghị tung mã Voucher giảm giá khung giờ thấp điểm để tăng hệ số xoay vòng ngăn tủ."
            );
        }

        return (
            "UNDERUTILIZED",
            "Dư thừa công suất lớn. Cần đẩy mạnh biển chỉ dẫn tại điểm đặt trạm hoặc cân nhắc tái phân bổ bớt module tủ sang trạm cao điểm."
        );
    }

    private sealed record TimeBucket(string Key, string Label, DateTime StartDate, DateTime EndDate);

    private static List<TimeBucket> BuildTimeBuckets(DateTime fromDate, DateTime toDate, string granularity)
    {
        var buckets = new List<TimeBucket>();
        if (granularity == "MONTH")
        {
            var cursor = new DateTime(fromDate.Year, fromDate.Month, 1);
            while (cursor <= toDate)
            {
                var start = cursor < fromDate ? fromDate : cursor;
                var lastDayOfMonth = cursor.AddMonths(1).AddDays(-1);
                var end = lastDayOfMonth > toDate ? toDate : lastDayOfMonth;
                buckets.Add(new TimeBucket(
                    $"{cursor:yyyy-MM}",
                    $"Tháng {cursor:MM/yyyy}",
                    start,
                    end));
                cursor = cursor.AddMonths(1);
            }
        }
        else if (granularity == "WEEK")
        {
            var cursor = fromDate;
            int weekIdx = 1;
            while (cursor <= toDate)
            {
                var end = cursor.AddDays(6);
                if (end > toDate) end = toDate;
                buckets.Add(new TimeBucket(
                    $"W{weekIdx}_{cursor:yyyyMMdd}",
                    $"{cursor:dd/MM} - {end:dd/MM}",
                    cursor,
                    end));
                cursor = end.AddDays(1);
                weekIdx++;
            }
        }
        else
        {
            for (var d = fromDate; d <= toDate; d = d.AddDays(1))
            {
                buckets.Add(new TimeBucket(
                    $"{d:yyyy-MM-dd}",
                    $"{d:dd/MM}",
                    d,
                    d));
            }
        }

        return buckets;
    }

    private static byte[] BuildCsvBytes(OccupancyTurnoverOverviewDto data)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"\"BÁO CÁO PHÂN TÍCH TỶ LỆ LẤP ĐẦY & TẦN SUẤT XOAY VÒNG NGĂN TỦ SMARTLOCKER\"");
        sb.AppendLine($"\"Kỳ báo cáo:\",\"{data.FromDate:dd/MM/yyyy} - {data.ToDate:dd/MM/yyyy}\",\"Phạm vi:\",\"{EscapeCsv(data.StationFilterLabel)}\",\"Cỡ tủ:\",\"{data.LockerSizeFilter}\"");
        sb.AppendLine($"\"Tổng tủ vật lý:\",\"{data.TotalPhysicalLockers}\",\"Tủ khả dụng:\",\"{data.ServiceableLockers}\",\"Lấp đầy tức thời (%):\",\"{data.LiveServiceableOccupancyPercent:0.00}\",\"Lấp đầy theo giờ (%):\",\"{data.TimeWeightedOccupancyPercent:0.00}\"");
        sb.AppendLine($"\"Tổng lượt xoay vòng:\",\"{data.TotalCompletedAndActiveCycles}\",\"Vòng quay / Tủ (kỳ):\",\"{data.PeriodTurnoverPerLocker:0.00}\",\"Vòng quay / Tủ / Ngày:\",\"{data.DailyTurnoverRate:0.000}\",\"Thời gian lưu trữ TB (giờ):\",\"{data.AverageStorageDurationHours:0.00}\"");
        sb.AppendLine();

        sb.AppendLine("\"Mã Ngăn Tủ\",\"Trạm SmartLocker\",\"Kích Cỡ\",\"Trạng Thái Kinh Doanh\",\"Sức Khỏe IoT\",\"Số Lượt Thuê (Vòng Quay)\",\"Tần Suất Xoay Vòng / Ngày\",\"Tổng Giờ Lấp Đầy\",\"Tỷ Lệ Lấp Đầy (%)\",\"Thời Gian Lưu Trữ TB (Giờ)\",\"Thời Gian Trống TB Giữa 2 Lượt (Giờ)\",\"Doanh Thu Tạo Ra (VND)\",\"Phân Hạng Khai Thác\"");

        foreach (var item in data.LockerTurnoverItems)
        {
            sb.AppendLine(string.Join(",",
                $"\"{EscapeCsv(item.LockerCode)}\"",
                $"\"{EscapeCsv(item.StationName)}\"",
                $"\"{EscapeCsv(item.Size)}\"",
                $"\"{EscapeCsv(item.BusinessStatus)}\"",
                $"\"{EscapeCsv(item.HealthStatus)}\"",
                item.CycleCount.ToString(CultureInfo.InvariantCulture),
                item.DailyTurnoverRate.ToString("0.000", CultureInfo.InvariantCulture),
                item.OccupiedHours.ToString("0.00", CultureInfo.InvariantCulture),
                item.OccupancyRatePercent.ToString("0.00", CultureInfo.InvariantCulture),
                item.AvgDurationHours.ToString("0.00", CultureInfo.InvariantCulture),
                item.AvgIdleGapHours.ToString("0.00", CultureInfo.InvariantCulture),
                item.GeneratedRevenue.ToString("0", CultureInfo.InvariantCulture),
                $"\"{EscapeCsv(item.UtilizationTier)}\""));
        }

        var preamble = Encoding.UTF8.GetPreamble();
        var body = Encoding.UTF8.GetBytes(sb.ToString());
        var combined = new byte[preamble.Length + body.Length];
        Buffer.BlockCopy(preamble, 0, combined, 0, preamble.Length);
        Buffer.BlockCopy(body, 0, combined, preamble.Length, body.Length);
        return combined;
    }

    private static string EscapeCsv(string? val) =>
        string.IsNullOrEmpty(val) ? string.Empty : val.Replace("\"", "\"\"");

    private static string XmlEsc(string? val) =>
        SecurityElement.Escape(val ?? string.Empty) ?? string.Empty;

    private static byte[] BuildExcelWorkbookBytes(OccupancyTurnoverOverviewDto d)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.AppendLine("<?mso-application progid=\"Excel.Sheet\"?>");
        sb.AppendLine("<Workbook xmlns=\"urn:schemas-microsoft-com:office:spreadsheet\"");
        sb.AppendLine(" xmlns:o=\"urn:schemas-microsoft-com:office:office\"");
        sb.AppendLine(" xmlns:x=\"urn:schemas-microsoft-com:office:excel\"");
        sb.AppendLine(" xmlns:ss=\"urn:schemas-microsoft-com:office:spreadsheet\">");

        sb.AppendLine(" <Styles>");
        sb.AppendLine("  <Style ss:ID=\"Default\" ss:Name=\"Normal\"><Font ss:FontName=\"Calibri\" ss:Size=\"11\"/></Style>");
        sb.AppendLine("  <Style ss:ID=\"Title\"><Font ss:FontName=\"Calibri\" ss:Size=\"14\" ss:Bold=\"1\" ss:Color=\"#0F172A\"/></Style>");
        sb.AppendLine("  <Style ss:ID=\"Header\"><Font ss:FontName=\"Calibri\" ss:Size=\"11\" ss:Bold=\"1\" ss:Color=\"#FFFFFF\"/><Interior ss:Color=\"#0F766E\" ss:Pattern=\"Solid\"/></Style>");
        sb.AppendLine("  <Style ss:ID=\"Bold\"><Font ss:FontName=\"Calibri\" ss:Size=\"11\" ss:Bold=\"1\"/></Style>");
        sb.AppendLine("  <Style ss:ID=\"Int\"><NumberFormat ss:Format=\"#,##0\"/></Style>");
        sb.AppendLine("  <Style ss:ID=\"Dec\"><NumberFormat ss:Format=\"0.00\"/></Style>");
        sb.AppendLine(" </Styles>");

        // SHEET 1: Tổng hợp Công suất & Xoay vòng
        sb.AppendLine(" <Worksheet ss:Name=\"1_TongHop_CongSuat\">");
        sb.AppendLine("  <Table>");
        sb.AppendLine("   <Column ss:Width=\"310\"/><Column ss:Width=\"180\"/>");
        AppendRow(sb, ("BÁO CÁO PHÂN TÍCH TỶ LỆ LẤP ĐẦY & TẦN SUẤT XOAY VÒNG NGĂN TỦ", "String", "Title"));
        AppendRow(sb, ("Kỳ phân tích", "String", "Bold"), ($"{d.FromDate:dd/MM/yyyy} - {d.ToDate:dd/MM/yyyy} ({d.PeriodDays} ngày)", "String", null));
        AppendRow(sb, ("Phạm vi trạm", "String", "Bold"), (d.StationFilterLabel, "String", null));
        AppendRow(sb, ("Kích cỡ lọc", "String", "Bold"), (d.LockerSizeFilter, "String", null));
        AppendRow(sb, ("CHỈ SỐ VẬN HÀNH & CÔNG SUẤT NGĂN TỦ", "String", "Header"), ("GIÁ TRỊ ĐO LƯỜNG", "String", "Header"));
        AppendNumericSummaryRow(sb, "1. Tổng số ngăn tủ vật lý", d.TotalPhysicalLockers, "Int");
        AppendNumericSummaryRow(sb, "2. Số ngăn tủ khả dụng khai thác (Serviceable)", d.ServiceableLockers, "Int");
        AppendNumericSummaryRow(sb, "3. Số ngăn tủ đang bận tức thời (Live Occupied + Reserved)", d.OccupiedNowLockers + d.ReservedNowLockers, "Int");
        AppendNumericSummaryRow(sb, "4. Tỷ lệ lấp đầy tức thời trên tủ khả dụng (%)", d.LiveServiceableOccupancyPercent, "Dec");
        AppendNumericSummaryRow(sb, "5. Tổng quỹ giờ ngăn tủ khả dụng (Available Locker-Hours)", d.AvailableCapacityLockerHours, "Dec");
        AppendNumericSummaryRow(sb, "6. Tổng giờ ngăn tủ được lấp đầy thực tế (Occupied Locker-Hours)", d.TotalOccupiedLockerHours, "Dec");
        AppendNumericSummaryRow(sb, "7. Tỷ lệ lấp đầy bình quân theo thời gian thực tế (%)", d.TimeWeightedOccupancyPercent, "Dec");
        AppendNumericSummaryRow(sb, "8. Chênh lệch lấp đầy so với kỳ liền trước (điểm %)", d.OccupancyDeltaPercent, "Dec");
        AppendNumericSummaryRow(sb, "9. Tổng số lượt khách thuê ngăn tủ (Tổng vòng quay)", d.TotalCompletedAndActiveCycles, "Int");
        AppendNumericSummaryRow(sb, "10. Hệ số xoay vòng trung bình / ngăn tủ trong kỳ (Lượt/Tủ)", d.PeriodTurnoverPerLocker, "Dec");
        AppendNumericSummaryRow(sb, "11. Tần suất xoay vòng ngày (Vòng/Tủ/Ngày)", d.DailyTurnoverRate, "Dec");
        AppendNumericSummaryRow(sb, "12. Thời gian lưu trữ trung bình mỗi lượt (Giờ)", d.AverageStorageDurationHours, "Dec");
        AppendNumericSummaryRow(sb, "13. Thời gian trống trung bình giữa 2 lượt thuê (Giờ)", d.AverageTurnaroundIdleHours, "Dec");
        AppendNumericSummaryRow(sb, "14. Doanh thu thuần bình quân trên mỗi ngăn tủ khả dụng - RevPAL (VND)", d.RevenuePerAvailableLocker, "Int");
        AppendNumericSummaryRow(sb, "15. Doanh thu thuần trên mỗi giờ lấp đầy - RevPOLH (VND/Giờ)", d.RevenuePerOccupiedHour, "Int");
        AppendRow(sb, ("16. Khung giờ cao điểm lấp đầy", "String", null), (d.PeakOccupancyHourLabel, "String", "Bold"));
        AppendRow(sb, ("17. Ngày cao điểm trong tuần", "String", null), (d.PeakDayOfWeekLabel, "String", "Bold"));
        sb.AppendLine("  </Table>");
        sb.AppendLine(" </Worksheet>");

        // SHEET 2: Chuỗi thời gian
        sb.AppendLine(" <Worksheet ss:Name=\"2_XuHuong_ChuoiThoiGian\">");
        sb.AppendLine("  <Table>");
        AppendRow(sb,
            ("Mốc Thời Gian", "String", "Header"),
            ("Tỷ Lệ Lấp Đầy (%)", "String", "Header"),
            ("Số Lượt Thuê (Vòng Quay)", "String", "Header"),
            ("Vòng Quay / Tủ / Ngày", "String", "Header"),
            ("Giờ Lấp Đầy Thực Tế", "String", "Header"),
            ("Quỹ Giờ Khả Dụng", "String", "Header"),
            ("Thời Gian Lưu Trữ TB (Giờ)", "String", "Header"),
            ("Doanh Thu Thuần (VND)", "String", "Header"));
        foreach (var pt in d.TimeSeries)
        {
            AppendRow(sb,
                (pt.Label, "String", null),
                (pt.OccupancyRatePercent.ToString("0.00", CultureInfo.InvariantCulture), "Number", "Dec"),
                (pt.TotalCycles.ToString(CultureInfo.InvariantCulture), "Number", "Int"),
                (pt.DailyTurnoverRate.ToString("0.000", CultureInfo.InvariantCulture), "Number", "Dec"),
                (pt.OccupiedLockerHours.ToString("0.00", CultureInfo.InvariantCulture), "Number", "Dec"),
                (pt.AvailableLockerHours.ToString("0.00", CultureInfo.InvariantCulture), "Number", "Dec"),
                (pt.AverageDurationHours.ToString("0.00", CultureInfo.InvariantCulture), "Number", "Dec"),
                (pt.NetRevenue.ToString("0", CultureInfo.InvariantCulture), "Number", "Int"));
        }
        sb.AppendLine("  </Table>");
        sb.AppendLine(" </Worksheet>");

        // SHEET 3: Hiệu suất theo Trạm & Kích cỡ
        sb.AppendLine(" <Worksheet ss:Name=\"3_HieuSuat_Tram_KichCo\">");
        sb.AppendLine("  <Table>");
        AppendRow(sb,
            ("Trạm SmartLocker", "String", "Header"),
            ("Tổng Tủ", "String", "Header"),
            ("Tủ Khả Dụng", "String", "Header"),
            ("Lấp Đầy Tức Thời (%)", "String", "Header"),
            ("Lấp Đầy Theo Giờ (%)", "String", "Header"),
            ("Tổng Lượt Thuê", "String", "Header"),
            ("Vòng Quay / Tủ", "String", "Header"),
            ("Vòng Quay / Ngày", "String", "Header"),
            ("Lưu Trữ TB (Giờ)", "String", "Header"),
            ("RevPAL (VND/Tủ)", "String", "Header"),
            ("Chẩn Đoán Công Suất", "String", "Header"),
            ("Khuyến Nghị Vận Hành", "String", "Header"));
        foreach (var st in d.StationBreakdown)
        {
            AppendRow(sb,
                (st.StationName, "String", "Bold"),
                (st.TotalLockers.ToString(CultureInfo.InvariantCulture), "Number", "Int"),
                (st.ServiceableLockers.ToString(CultureInfo.InvariantCulture), "Number", "Int"),
                (st.LiveOccupancyPercent.ToString("0.00", CultureInfo.InvariantCulture), "Number", "Dec"),
                (st.TimeWeightedOccupancyPercent.ToString("0.00", CultureInfo.InvariantCulture), "Number", "Dec"),
                (st.TotalCycles.ToString(CultureInfo.InvariantCulture), "Number", "Int"),
                (st.TurnoverPerLocker.ToString("0.00", CultureInfo.InvariantCulture), "Number", "Dec"),
                (st.DailyTurnoverRate.ToString("0.000", CultureInfo.InvariantCulture), "Number", "Dec"),
                (st.AvgDurationHours.ToString("0.00", CultureInfo.InvariantCulture), "Number", "Dec"),
                (st.RevenuePerAvailableLocker.ToString("0", CultureInfo.InvariantCulture), "Number", "Int"),
                (st.CapacityStatus, "String", "Bold"),
                (st.CapacityRecommendation, "String", null));
        }
        sb.AppendLine("  </Table>");
        sb.AppendLine(" </Worksheet>");

        // SHEET 4: Chi tiết xoay vòng từng ngăn tủ vật lý
        sb.AppendLine(" <Worksheet ss:Name=\"4_XoayVong_TungNganTu\">");
        sb.AppendLine("  <Table>");
        AppendRow(sb,
            ("Mã Ngăn Tủ", "String", "Header"),
            ("Trạm SmartLocker", "String", "Header"),
            ("Kích Cỡ", "String", "Header"),
            ("Trạng Thái Hiện Tại", "String", "Header"),
            ("Sức Khỏe IoT", "String", "Header"),
            ("Số Lượt Xoay Vòng", "String", "Header"),
            ("Vòng Quay / Ngày", "String", "Header"),
            ("Tổng Giờ Lấp Đầy", "String", "Header"),
            ("Tỷ Lệ Lấp Đầy (%)", "String", "Header"),
            ("Lưu Trữ TB (Giờ)", "String", "Header"),
            ("Trống Chờ Khách TB (Giờ)", "String", "Header"),
            ("Doanh Thu Tạo Ra (VND)", "String", "Header"),
            ("Phân Hạng Khai Thác", "String", "Header"));
        foreach (var l in d.LockerTurnoverItems)
        {
            AppendRow(sb,
                (l.LockerCode, "String", "Bold"),
                (l.StationName, "String", null),
                (l.Size, "String", null),
                (l.BusinessStatus, "String", null),
                (l.HealthStatus, "String", null),
                (l.CycleCount.ToString(CultureInfo.InvariantCulture), "Number", "Int"),
                (l.DailyTurnoverRate.ToString("0.000", CultureInfo.InvariantCulture), "Number", "Dec"),
                (l.OccupiedHours.ToString("0.00", CultureInfo.InvariantCulture), "Number", "Dec"),
                (l.OccupancyRatePercent.ToString("0.00", CultureInfo.InvariantCulture), "Number", "Dec"),
                (l.AvgDurationHours.ToString("0.00", CultureInfo.InvariantCulture), "Number", "Dec"),
                (l.AvgIdleGapHours.ToString("0.00", CultureInfo.InvariantCulture), "Number", "Dec"),
                (l.GeneratedRevenue.ToString("0", CultureInfo.InvariantCulture), "Number", "Int"),
                (l.UtilizationTier, "String", "Bold"));
        }
        sb.AppendLine("  </Table>");
        sb.AppendLine(" </Worksheet>");

        sb.AppendLine("</Workbook>");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static void AppendNumericSummaryRow(StringBuilder sb, string label, decimal val, string styleId)
    {
        var fmt = styleId == "Dec" ? "0.00" : "0";
        AppendRow(sb, (label, "String", null), (val.ToString(fmt, CultureInfo.InvariantCulture), "Number", styleId));
    }

    private static void AppendRow(
        StringBuilder sb,
        params (string Value, string Type, string? StyleId)[] cells)
    {
        sb.Append("   <Row>");
        foreach (var c in cells)
        {
            var styleAttr = string.IsNullOrEmpty(c.StyleId) ? "" : $" ss:StyleID=\"{c.StyleId}\"";
            sb.Append($"<Cell{styleAttr}><Data ss:Type=\"{c.Type}\">{XmlEsc(c.Value)}</Data></Cell>");
        }
        sb.AppendLine("</Row>");
    }
}
