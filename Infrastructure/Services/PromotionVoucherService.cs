namespace Infrastructure.Services;

using System.Text.Json;
using System.Text.RegularExpressions;
using Application.DTOs.Common;
using Application.DTOs.Promotion;
using Application.Interfaces;
using Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

/// <summary>
/// Quản lý Chiến dịch Khuyến mại, Kho Mã Voucher & Kiểm soát Ngân sách Chiết khấu 3 tầng:
/// (1) Ngân sách tháng toàn hệ thống -> (2) Ngân sách từng Chiến dịch -> (3) Ngân sách & Lượt dùng từng Mã Voucher.
/// Đảm bảo an toàn tài chính, tương thích hạn mức tối thiểu PayOS (>= 2.000đ) và tự động hoàn trả ngân sách khi đơn hủy/hết hạn.
/// </summary>
public class PromotionVoucherService : IPromotionVoucherService
{
    private static readonly SemaphoreSlim _redeemSemaphore = new(1, 1);
    private const string MetaAction = "PROMO_META_SNAPSHOT";
    private const string GlobalBudgetEntity = "PromoGlobalBudget";
    private const string CampaignMetaEntity = "PromoCampaignMeta";
    private const string VoucherMetaEntity = "PromoVoucherMeta";
    private const string RedemptionDataEntity = "PromoRedemptionData";

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly ISmartLockerDbContext _db;
    private readonly IAuditLogService _auditLog;
    private readonly ILogger<PromotionVoucherService> _logger;

    public PromotionVoucherService(
        ISmartLockerDbContext db,
        IAuditLogService auditLog,
        ILogger<PromotionVoucherService> logger)
    {
        _db = db;
        _auditLog = auditLog;
        _logger = logger;
    }

    // ─── Internal Metadata Models ─────────────────────────────────────────────

    private sealed class StoredGlobalBudget
    {
        public decimal MonthlyBudgetCap { get; set; } = 25000000m;
        public int WarningThresholdPercent { get; set; } = 80;
        public int MaxDiscountPercentPerOrder { get; set; } = 70;
        public decimal MinPayableAmountAfterDiscount { get; set; } = 2000m;
        public bool AutoPauseOnBudgetExhausted { get; set; } = true;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }

    private sealed class StoredCampaignMeta
    {
        public string Id { get; set; } = string.Empty;
        public string Code { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Category { get; set; } = "SEASONAL";
        public decimal AllocatedBudget { get; set; }
        public DateTime StartDate { get; set; }
        public DateTime EndDate { get; set; }
        public bool IsPaused { get; set; }
        public bool AutoPauseWhenDepleted { get; set; } = true;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }

    private sealed class StoredVoucherMeta
    {
        public Guid VoucherId { get; set; }
        public string CampaignId { get; set; } = string.Empty;
        public decimal BudgetCap { get; set; }
        public int PerUserLimit { get; set; } = 3;
        public List<string> ApplicableSizes { get; set; } = new() { "S", "M", "L" };
        public List<Guid> ApplicableStationIds { get; set; } = new();
    }

    private sealed class StoredRedemptionRecord
    {
        public Guid BookingId { get; set; }
        public string BookingCode { get; set; } = string.Empty;
        public Guid UserId { get; set; }
        public Guid VoucherId { get; set; }
        public string VoucherCode { get; set; } = string.Empty;
        public string CampaignId { get; set; } = string.Empty;
        public decimal OriginalAmount { get; set; }
        public decimal DiscountAmount { get; set; }
        public decimal FinalAmount { get; set; }
        public bool IsReleased { get; set; }
        public DateTime RedeemedAt { get; set; } = DateTime.UtcNow;
    }

    private sealed class LoadedPromotionContext
    {
        public StoredGlobalBudget GlobalConfig { get; set; } = new();
        public Dictionary<string, StoredCampaignMeta> Campaigns { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<Guid, StoredVoucherMeta> VoucherMetas { get; set; } = new();
        public List<Voucher> Vouchers { get; set; } = new();
        public List<StoredRedemptionRecord> ActiveRedemptions { get; set; } = new();
    }

    // ─── Helper: Default Built-in Campaigns for Seed Vouchers ─────────────────

    private static Dictionary<string, StoredCampaignMeta> GetDefaultCampaigns()
    {
        var baseStart = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        var baseEnd = new DateTime(2027, 12, 31, 23, 59, 59, DateTimeKind.Utc);

        var list = new[]
        {
            new StoredCampaignMeta
            {
                Id = "CMP-NEW-TRAVELER",
                Code = "CMP-NEW-TRAVELER",
                Name = "Chiến dịch Chào bạn mới SmartLocker",
                Description = "Trợ giá trải nghiệm lần đầu cho hành khách mới đăng ký sử dụng tủ gửi đồ thông minh trên toàn hệ thống.",
                Category = "NEW_USER",
                AllocatedBudget = 8000000m,
                StartDate = baseStart,
                EndDate = baseEnd,
                IsPaused = false,
                AutoPauseWhenDepleted = true,
                CreatedAt = baseStart,
                UpdatedAt = baseStart
            },
            new StoredCampaignMeta
            {
                Id = "CMP-DANANG-LAUNCH",
                Code = "CMP-DANANG-LAUNCH",
                Name = "Khai trương Cụm Trạm Du lịch Đà Nẵng",
                Description = "Ưu đãi kích cầu du lịch tại sân bay, ga tàu và bãi biển TP. Đà Nẵng.",
                Category = "SEASONAL",
                AllocatedBudget = 7500000m,
                StartDate = baseStart,
                EndDate = baseEnd,
                IsPaused = false,
                AutoPauseWhenDepleted = true,
                CreatedAt = baseStart,
                UpdatedAt = baseStart
            },
            new StoredCampaignMeta
            {
                Id = "CMP-FPT-CAMPUS",
                Code = "CMP-FPT-CAMPUS",
                Name = "Đồng hành Sinh viên & Đô thị FPT City",
                Description = "Chính sách chiết khấu đối tác dành cho sinh viên, giảng viên và cư dân khu công nghệ cao FPT.",
                Category = "PARTNER",
                AllocatedBudget = 4500000m,
                StartDate = baseStart,
                EndDate = baseEnd,
                IsPaused = false,
                AutoPauseWhenDepleted = true,
                CreatedAt = baseStart,
                UpdatedAt = baseStart
            },
            new StoredCampaignMeta
            {
                Id = "CMP-SMART-SAVINGS",
                Code = "CMP-SMART-SAVINGS",
                Name = "Tiết kiệm Thuê tủ Dài hạn & Giờ Thấp điểm",
                Description = "Khuyến khích hành khách gửi hành lý từ 6h–24h tối ưu công suất lấp đầy ô tủ.",
                Category = "LONG_STAY",
                AllocatedBudget = 5000000m,
                StartDate = baseStart,
                EndDate = baseEnd,
                IsPaused = false,
                AutoPauseWhenDepleted = true,
                CreatedAt = baseStart,
                UpdatedAt = baseStart
            }
        };

        return list.ToDictionary(c => c.Id, c => c, StringComparer.OrdinalIgnoreCase);
    }

    private static string InferDefaultCampaignId(string voucherCode)
    {
        var upper = (voucherCode ?? string.Empty).ToUpperInvariant();
        if (upper.Contains("WELCOME") || upper.Contains("NEW")) return "CMP-NEW-TRAVELER";
        if (upper.Contains("DANANG") || upper.Contains("DN")) return "CMP-DANANG-LAUNCH";
        if (upper.Contains("FPT") || upper.Contains("STUDENT")) return "CMP-FPT-CAMPUS";
        return "CMP-SMART-SAVINGS";
    }

    private static decimal EstimateHistoricalUnitDiscount(Voucher v)
    {
        if (string.Equals(v.DiscountType, "FIXED", StringComparison.OrdinalIgnoreCase))
        {
            return Math.Max(1000m, v.DiscountValue);
        }

        var referenceOrder = Math.Max(v.MinOrderAmount > 0 ? v.MinOrderAmount : 45000m, 45000m);
        var raw = Math.Round(referenceOrder * v.DiscountValue / 100m, 0);
        if (v.MaxDiscountAmount.HasValue && v.MaxDiscountAmount.Value > 0)
        {
            raw = Math.Min(raw, v.MaxDiscountAmount.Value);
        }
        return Math.Max(1000m, raw);
    }

    private static decimal ComputeDefaultVoucherBudgetCap(Voucher v)
    {
        var maxUnit = string.Equals(v.DiscountType, "FIXED", StringComparison.OrdinalIgnoreCase)
            ? v.DiscountValue
            : (v.MaxDiscountAmount.HasValue && v.MaxDiscountAmount.Value > 0
                ? v.MaxDiscountAmount.Value
                : Math.Max(20000m, v.DiscountValue * 500m));

        var sensibleUnits = Math.Min(Math.Max(v.UsageLimit, Math.Max(v.UsedCount, 10)), 250);
        return Math.Max(maxUnit * sensibleUnits, EstimateHistoricalUnitDiscount(v) * Math.Max(v.UsedCount, 1));
    }

    private async Task<(string Email, string Role)> GetActorInfoAsync(Guid actorId)
    {
        if (actorId == Guid.Empty) return ("admin@smartlocker.vn", "ADMIN");
        var u = await _db.Users.AsNoTracking()
            .Where(x => x.Id == actorId)
            .Select(x => new { x.Email, Role = x.Role.ToString() })
            .FirstOrDefaultAsync();
        return u != null ? (u.Email, u.Role) : ("admin@smartlocker.vn", "ADMIN");
    }

    private async Task SaveMetaSnapshotAsync(Guid? actorId, string targetEntity, string targetId, object payload)
    {
        var json = JsonSerializer.Serialize(payload, _jsonOpts);
        _db.AuditLogs.Add(new AuditLog
        {
            UserId = actorId,
            UserEmail = "system-meta@smartlocker.vn",
            UserRole = "ADMIN",
            Action = MetaAction,
            TargetEntity = targetEntity,
            TargetId = targetId,
            Details = json,
            CreatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();
    }

    private static void SanitizeLegacyMojibake(Voucher v)
    {
        static bool LooksMojibake(string? s) =>
            !string.IsNullOrEmpty(s) && (s.Contains("Ã") || s.Contains("áº") || s.Contains("á»") || s.Contains("Æ°") || s.Contains("Ä‘"));

        if (!LooksMojibake(v.Title) && !LooksMojibake(v.Description)) return;

        switch ((v.Code ?? string.Empty).Trim().ToUpperInvariant())
        {
            case "FPTCITY50":
                v.Title = "Giảm 50% (Tối đa 20.000đ) — Ưu đãi FPT";
                v.Description = "Dành cho sinh viên & cán bộ khu đô thị FPT City Đà Nẵng và Khu CNC.";
                break;
            case "WELCOME30":
                v.Title = "Giảm 30% (Tối đa 30.000đ) cho khách hàng mới";
                v.Description = "Áp dụng cho đơn đặt tủ từ 30.000đ trở lên trên toàn hệ thống.";
                break;
            case "DANANG20":
                v.Title = "Giảm 20% (Tối đa 25.000đ) — Ưu đãi Đà Nẵng";
                v.Description = "Khuyến mại chào mừng hệ thống trạm tủ thông minh SmartLocker tại TP. Đà Nẵng.";
                break;
            case "SMART10K":
                v.Title = "Giảm 10.000đ cho đơn từ 20.000đ";
                v.Description = "Ưu đãi giảm trực tiếp 10.000đ cho mọi lượt đặt tủ từ 20.000đ trở lên.";
                break;
            case "LONGSTAY25K":
                v.Title = "Giảm 25.000đ cho đơn thuê dài hạn từ 60.000đ";
                v.Description = "Tiết kiệm hơn khi gửi hành lý từ 6 giờ đến 24 giờ.";
                break;
        }
    }

    private async Task<LoadedPromotionContext> LoadContextAsync(bool trackVouchers = false)
    {
        var ctx = new LoadedPromotionContext
        {
            Campaigns = GetDefaultCampaigns()
        };

        ctx.Vouchers = trackVouchers
            ? await _db.Vouchers.OrderByDescending(v => v.CreatedAt).ToListAsync()
            : await _db.Vouchers.AsNoTracking().OrderByDescending(v => v.CreatedAt).ToListAsync();

        var metaLogs = await _db.AuditLogs.AsNoTracking()
            .Where(a => a.Action == MetaAction)
            .OrderBy(a => a.Id)
            .ToListAsync();

        var redemptionByBooking = new Dictionary<Guid, StoredRedemptionRecord>();

        foreach (var log in metaLogs)
        {
            if (string.IsNullOrWhiteSpace(log.Details)) continue;

            try
            {
                if (log.TargetEntity == GlobalBudgetEntity)
                {
                    var cfg = JsonSerializer.Deserialize<StoredGlobalBudget>(log.Details, _jsonOpts);
                    if (cfg != null) ctx.GlobalConfig = cfg;
                }
                else if (log.TargetEntity == CampaignMetaEntity)
                {
                    var cmp = JsonSerializer.Deserialize<StoredCampaignMeta>(log.Details, _jsonOpts);
                    if (cmp != null && !string.IsNullOrWhiteSpace(cmp.Id))
                    {
                        ctx.Campaigns[cmp.Id] = cmp;
                    }
                }
                else if (log.TargetEntity == VoucherMetaEntity)
                {
                    var vm = JsonSerializer.Deserialize<StoredVoucherMeta>(log.Details, _jsonOpts);
                    if (vm != null && vm.VoucherId != Guid.Empty)
                    {
                        ctx.VoucherMetas[vm.VoucherId] = vm;
                    }
                }
                else if (log.TargetEntity == RedemptionDataEntity)
                {
                    var rec = JsonSerializer.Deserialize<StoredRedemptionRecord>(log.Details, _jsonOpts);
                    if (rec != null && rec.BookingId != Guid.Empty)
                    {
                        redemptionByBooking[rec.BookingId] = rec;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Bỏ qua bản ghi PROMO_META_SNAPSHOT lỗi định dạng Id={LogId}", log.Id);
            }
        }

        ctx.ActiveRedemptions = redemptionByBooking.Values.Where(r => !r.IsReleased).ToList();

        // Đảm bảo mọi Voucher đều có metadata mặc định hợp lý & chuẩn hóa hiển thị tiếng Việt
        foreach (var v in ctx.Vouchers)
        {
            if (!trackVouchers)
            {
                SanitizeLegacyMojibake(v);
            }
            if (!ctx.VoucherMetas.TryGetValue(v.Id, out var meta))
            {
                var inferredCmpId = InferDefaultCampaignId(v.Code);
                if (!ctx.Campaigns.ContainsKey(inferredCmpId))
                {
                    inferredCmpId = ctx.Campaigns.Keys.First();
                }

                ctx.VoucherMetas[v.Id] = new StoredVoucherMeta
                {
                    VoucherId = v.Id,
                    CampaignId = inferredCmpId,
                    BudgetCap = ComputeDefaultVoucherBudgetCap(v),
                    PerUserLimit = v.Code.Contains("WELCOME", StringComparison.OrdinalIgnoreCase) ? 1 : 3,
                    ApplicableSizes = new List<string> { "S", "M", "L" },
                    ApplicableStationIds = new List<Guid>()
                };
            }
            else if (meta.BudgetCap <= 0)
            {
                meta.BudgetCap = ComputeDefaultVoucherBudgetCap(v);
            }
        }

        return ctx;
    }

    private decimal CalculateVoucherBudgetSpent(Voucher v, List<StoredRedemptionRecord> activeRedemptions)
    {
        var voucherRedemptions = activeRedemptions.Where(r => r.VoucherId == v.Id).ToList();
        var liveSpent = voucherRedemptions.Sum(r => r.DiscountAmount);
        var historicalUses = Math.Max(0, v.UsedCount - voucherRedemptions.Count);
        var historicalSpent = historicalUses * EstimateHistoricalUnitDiscount(v);
        return historicalSpent + liveSpent;
    }

    private decimal CalculateVoucherGmvDriven(Voucher v, List<StoredRedemptionRecord> activeRedemptions)
    {
        var voucherRedemptions = activeRedemptions.Where(r => r.VoucherId == v.Id).ToList();
        var liveGmv = voucherRedemptions.Sum(r => r.OriginalAmount);
        var historicalUses = Math.Max(0, v.UsedCount - voucherRedemptions.Count);
        var avgOrder = Math.Max(v.MinOrderAmount > 0 ? v.MinOrderAmount * 1.35m : 50000m, 45000m);
        return Math.Round(historicalUses * avgOrder, 0) + liveGmv;
    }

    private VoucherAdminDto BuildVoucherAdminDto(Voucher v, LoadedPromotionContext ctx)
    {
        var now = DateTime.UtcNow;
        var meta = ctx.VoucherMetas[v.Id];
        ctx.Campaigns.TryGetValue(meta.CampaignId, out var cmp);
        var cmpName = cmp?.Name ?? "Chiến dịch Khuyến mại Chung";

        var spent = CalculateVoucherBudgetSpent(v, ctx.ActiveRedemptions);
        var cap = Math.Max(meta.BudgetCap, 0m);
        var remainingBudget = Math.Max(0m, cap - spent);
        var utilPct = cap > 0 ? Math.Min(100m, Math.Round(spent * 100m / cap, 1)) : 0m;
        var minUnitNeeded = string.Equals(v.DiscountType, "FIXED", StringComparison.OrdinalIgnoreCase)
            ? v.DiscountValue
            : 1000m;

        string effectiveStatus;
        if (!v.IsActive)
            effectiveStatus = "PAUSED";
        else if (cmp != null && cmp.IsPaused)
            effectiveStatus = "CAMPAIGN_PAUSED";
        else if (now < v.StartDate || (cmp != null && now < cmp.StartDate))
            effectiveStatus = "SCHEDULED";
        else if (now > v.EndDate || (cmp != null && now > cmp.EndDate))
            effectiveStatus = "EXPIRED";
        else if (v.UsedCount >= v.UsageLimit)
            effectiveStatus = "LIMIT_REACHED";
        else if (cap > 0 && remainingBudget < minUnitNeeded)
            effectiveStatus = "BUDGET_EXHAUSTED";
        else
            effectiveStatus = "ACTIVE";

        return new VoucherAdminDto
        {
            Id = v.Id,
            Code = v.Code,
            Title = v.Title,
            Description = v.Description,
            CampaignId = meta.CampaignId,
            CampaignName = cmpName,
            DiscountType = v.DiscountType.ToUpperInvariant(),
            DiscountValue = v.DiscountValue,
            MaxDiscountAmount = v.MaxDiscountAmount,
            MinOrderAmount = v.MinOrderAmount,
            UsageLimit = v.UsageLimit,
            UsedCount = v.UsedCount,
            RemainingUses = Math.Max(0, v.UsageLimit - v.UsedCount),
            PerUserLimit = Math.Max(1, meta.PerUserLimit),
            BudgetCap = cap,
            BudgetSpent = spent,
            BudgetRemaining = remainingBudget,
            BudgetUtilizationPercent = utilPct,
            ApplicableSizes = meta.ApplicableSizes?.Count > 0 ? meta.ApplicableSizes : new List<string> { "S", "M", "L" },
            ApplicableStationIds = meta.ApplicableStationIds ?? new List<Guid>(),
            StartDate = v.StartDate,
            EndDate = v.EndDate,
            IsActive = v.IsActive,
            EffectiveStatus = effectiveStatus,
            CreatedAt = v.CreatedAt,
            UpdatedAt = v.UpdatedAt
        };
    }

    private List<PromotionCampaignDto> BuildCampaignDtos(LoadedPromotionContext ctx, List<VoucherAdminDto> voucherDtos)
    {
        var now = DateTime.UtcNow;
        var result = new List<PromotionCampaignDto>();

        foreach (var cmp in ctx.Campaigns.Values.OrderByDescending(c => c.CreatedAt))
        {
            var linked = voucherDtos.Where(v => string.Equals(v.CampaignId, cmp.Id, StringComparison.OrdinalIgnoreCase)).ToList();
            var spent = linked.Sum(v => v.BudgetSpent);
            var allocated = Math.Max(cmp.AllocatedBudget, 0m);
            var remaining = Math.Max(0m, allocated - spent);
            var utilPct = allocated > 0 ? Math.Min(100m, Math.Round(spent * 100m / allocated, 1)) : 0m;

            string status;
            if (cmp.IsPaused)
                status = "PAUSED";
            else if (now < cmp.StartDate)
                status = "SCHEDULED";
            else if (now > cmp.EndDate)
                status = "ENDED";
            else if (allocated > 0 && remaining < 1000m)
                status = "BUDGET_EXHAUSTED";
            else
                status = "ACTIVE";

            result.Add(new PromotionCampaignDto
            {
                Id = cmp.Id,
                Code = cmp.Code,
                Name = cmp.Name,
                Description = cmp.Description,
                Category = cmp.Category,
                AllocatedBudget = allocated,
                SpentBudget = spent,
                RemainingBudget = remaining,
                UtilizationPercent = utilPct,
                VoucherCount = linked.Count,
                ActiveVoucherCount = linked.Count(v => v.EffectiveStatus == "ACTIVE"),
                TotalRedemptions = linked.Sum(v => v.UsedCount),
                StartDate = cmp.StartDate,
                EndDate = cmp.EndDate,
                IsPaused = cmp.IsPaused,
                AutoPauseWhenDepleted = cmp.AutoPauseWhenDepleted,
                Status = status,
                CreatedAt = cmp.CreatedAt,
                UpdatedAt = cmp.UpdatedAt
            });
        }

        return result;
    }

    private GlobalPromotionBudgetDto BuildGlobalBudgetDto(LoadedPromotionContext ctx, decimal totalSpent)
    {
        var cap = Math.Max(100000m, ctx.GlobalConfig.MonthlyBudgetCap);
        var remaining = Math.Max(0m, cap - totalSpent);
        var utilPct = Math.Min(100m, Math.Round(totalSpent * 100m / cap, 1));

        string alertStatus;
        if (remaining < 1000m || utilPct >= 100m)
            alertStatus = "EXHAUSTED";
        else if (utilPct >= 95m)
            alertStatus = "CRITICAL";
        else if (utilPct >= ctx.GlobalConfig.WarningThresholdPercent)
            alertStatus = "WARNING";
        else
            alertStatus = "HEALTHY";

        return new GlobalPromotionBudgetDto
        {
            MonthlyBudgetCap = cap,
            WarningThresholdPercent = ctx.GlobalConfig.WarningThresholdPercent,
            MaxDiscountPercentPerOrder = ctx.GlobalConfig.MaxDiscountPercentPerOrder,
            MinPayableAmountAfterDiscount = Math.Max(2000m, ctx.GlobalConfig.MinPayableAmountAfterDiscount),
            AutoPauseOnBudgetExhausted = ctx.GlobalConfig.AutoPauseOnBudgetExhausted,
            MonthlyBudgetSpent = totalSpent,
            MonthlyBudgetRemaining = remaining,
            UtilizationPercent = utilPct,
            BudgetAlertStatus = alertStatus,
            UpdatedAt = ctx.GlobalConfig.UpdatedAt
        };
    }

    // ─── Core Discount Calculation & Validation Engine ────────────────────────

    private VoucherValidationResultDto EvaluateVoucherCore(
        LoadedPromotionContext ctx,
        Voucher voucher,
        Guid? userId,
        Guid? stationId,
        string? size,
        decimal orderAmount,
        bool checkUserQuota)
    {
        var now = DateTime.UtcNow;
        var voucherDto = BuildVoucherAdminDto(voucher, ctx);
        ctx.Campaigns.TryGetValue(voucherDto.CampaignId, out var campaign);

        var totalSpentAll = ctx.Vouchers.Sum(v => CalculateVoucherBudgetSpent(v, ctx.ActiveRedemptions));
        var globalBudget = BuildGlobalBudgetDto(ctx, totalSpentAll);

        decimal campaignSpent = ctx.Vouchers
            .Where(v => ctx.VoucherMetas.TryGetValue(v.Id, out var m) &&
                        string.Equals(m.CampaignId, voucherDto.CampaignId, StringComparison.OrdinalIgnoreCase))
            .Sum(v => CalculateVoucherBudgetSpent(v, ctx.ActiveRedemptions));
        decimal campaignRemaining = campaign != null
            ? Math.Max(0m, campaign.AllocatedBudget - campaignSpent)
            : decimal.MaxValue;

        VoucherValidationResultDto Fail(string reason) => new()
        {
            IsValid = false,
            VoucherId = voucher.Id,
            Code = voucher.Code,
            Title = voucher.Title,
            CampaignId = voucherDto.CampaignId,
            CampaignName = voucherDto.CampaignName,
            DiscountType = voucher.DiscountType,
            DiscountValue = voucher.DiscountValue,
            MaxDiscountAmount = voucher.MaxDiscountAmount,
            OriginalAmount = orderAmount,
            DiscountAmount = 0m,
            FinalPayableAmount = orderAmount,
            VoucherBudgetRemainingAfter = voucherDto.BudgetRemaining,
            CampaignBudgetRemainingAfter = campaign != null ? campaignRemaining : voucherDto.BudgetRemaining,
            Message = reason
        };

        if (!voucher.IsActive)
            return Fail("Mã giảm giá hiện đang tạm dừng kích hoạt.");

        if (campaign != null && campaign.IsPaused)
            return Fail($"Chiến dịch \"{campaign.Name}\" hiện đang tạm dừng.");

        if (now < voucher.StartDate || (campaign != null && now < campaign.StartDate))
            return Fail($"Mã giảm giá chưa đến thời gian áp dụng (bắt đầu từ {voucher.StartDate:dd/MM/yyyy HH:mm}).");

        if (now > voucher.EndDate || (campaign != null && now > campaign.EndDate))
            return Fail("Mã giảm giá đã hết thời hạn sử dụng.");

        if (voucher.UsedCount >= voucher.UsageLimit)
            return Fail("Mã giảm giá đã hết tổng số lượt sử dụng cho phép.");

        if (orderAmount < voucher.MinOrderAmount)
        {
            var missing = voucher.MinOrderAmount - orderAmount;
            return Fail($"Đơn thuê tối thiểu từ {voucher.MinOrderAmount:N0}đ để áp mã này (cần thêm {missing:N0}đ).");
        }

        // Kiểm tra phạm vi kích thước ô tủ
        if (!string.IsNullOrWhiteSpace(size) &&
            voucherDto.ApplicableSizes.Count > 0 &&
            !voucherDto.ApplicableSizes.Any(s => string.Equals(s, size.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            return Fail($"Mã ưu đãi này chỉ áp dụng cho ngăn tủ kích thước: {string.Join(", ", voucherDto.ApplicableSizes)}.");
        }

        // Kiểm tra phạm vi trạm áp dụng
        if (stationId.HasValue &&
            stationId.Value != Guid.Empty &&
            voucherDto.ApplicableStationIds.Count > 0 &&
            !voucherDto.ApplicableStationIds.Contains(stationId.Value))
        {
            return Fail("Mã ưu đãi này không áp dụng tại trạm tủ đang chọn.");
        }

        // Kiểm tra giới hạn lượt dùng trên mỗi khách hàng
        if (checkUserQuota && userId.HasValue && userId.Value != Guid.Empty)
        {
            var usedByUser = ctx.ActiveRedemptions.Count(r => r.VoucherId == voucher.Id && r.UserId == userId.Value);
            if (usedByUser >= voucherDto.PerUserLimit)
            {
                return Fail($"Bạn đã sử dụng tối đa {voucherDto.PerUserLimit} lượt cho mã ưu đãi {voucher.Code}.");
            }
        }

        var minPayable = Math.Max(2000m, globalBudget.MinPayableAmountAfterDiscount);
        if (orderAmount <= minPayable)
        {
            return Fail($"Giá trị đơn thuê ({orderAmount:N0}đ) đã ở mức thanh toán tối thiểu {minPayable:N0}đ.");
        }

        // Tính mức chiết khấu theo loại mã
        decimal rawDiscount;
        if (string.Equals(voucher.DiscountType, "FIXED", StringComparison.OrdinalIgnoreCase))
        {
            rawDiscount = voucher.DiscountValue;
            if (voucher.MaxDiscountAmount.HasValue && voucher.MaxDiscountAmount.Value > 0)
            {
                rawDiscount = Math.Min(rawDiscount, voucher.MaxDiscountAmount.Value);
            }
        }
        else
        {
            rawDiscount = Math.Round(orderAmount * voucher.DiscountValue / 100m, 0);
            if (voucher.MaxDiscountAmount.HasValue && voucher.MaxDiscountAmount.Value > 0)
            {
                rawDiscount = Math.Min(rawDiscount, voucher.MaxDiscountAmount.Value);
            }
        }

        // Trần % giảm tối đa trên 1 đơn theo chính sách an toàn tài chính toàn hệ thống
        var maxByGlobalPercent = Math.Round(orderAmount * globalBudget.MaxDiscountPercentPerOrder / 100m, 0);
        // Đảm bảo số tiền còn lại >= minPayable (2.000đ cho cổng VietQR PayOS)
        var maxForPayOs = Math.Max(0m, orderAmount - minPayable);

        var finalDiscount = Math.Min(Math.Min(rawDiscount, maxByGlobalPercent), maxForPayOs);
        if (finalDiscount <= 0)
        {
            return Fail("Không thể áp dụng chiết khấu do chạm ngưỡng thanh toán tối thiểu cổng PayOS.");
        }

        // Kiểm tra ngân sách chiết khấu 3 tầng (Voucher -> Chiến dịch -> Ngân sách tháng toàn hệ thống)
        if (voucherDto.BudgetCap > 0 && voucherDto.BudgetRemaining < finalDiscount)
        {
            return Fail($"Ngân sách chiết khấu của mã {voucher.Code} không còn đủ hạn mức ({voucherDto.BudgetRemaining:N0}đ còn lại).");
        }

        if (campaign != null && campaign.AllocatedBudget > 0 && campaignRemaining < finalDiscount)
        {
            return Fail($"Chiến dịch \"{campaign.Name}\" đã sử dụng hết ngân sách chiết khấu được phân bổ.");
        }

        if (globalBudget.MonthlyBudgetCap > 0 && globalBudget.MonthlyBudgetRemaining < finalDiscount)
        {
            return Fail("Ngân sách khuyến mại tháng của hệ thống đã chạm trần hạn mức.");
        }

        var finalPayable = orderAmount - finalDiscount;

        return new VoucherValidationResultDto
        {
            IsValid = true,
            VoucherId = voucher.Id,
            Code = voucher.Code,
            Title = voucher.Title,
            CampaignId = voucherDto.CampaignId,
            CampaignName = voucherDto.CampaignName,
            DiscountType = voucher.DiscountType.ToUpperInvariant(),
            DiscountValue = voucher.DiscountValue,
            MaxDiscountAmount = voucher.MaxDiscountAmount,
            OriginalAmount = orderAmount,
            DiscountAmount = finalDiscount,
            FinalPayableAmount = finalPayable,
            VoucherBudgetRemainingAfter = Math.Max(0m, voucherDto.BudgetRemaining - finalDiscount),
            CampaignBudgetRemainingAfter = campaign != null
                ? Math.Max(0m, campaignRemaining - finalDiscount)
                : Math.Max(0m, voucherDto.BudgetRemaining - finalDiscount),
            Message = $"Áp dụng mã {voucher.Code} thành công: Giảm {finalDiscount:N0}đ, còn thanh toán {finalPayable:N0}đ."
        };
    }

    // ─── Admin Implementations ────────────────────────────────────────────────

    public async Task<ApiResponse<PromotionDashboardOverviewDto>> GetAdminOverviewAsync()
    {
        var ctx = await LoadContextAsync(false);
        var voucherDtos = ctx.Vouchers.Select(v => BuildVoucherAdminDto(v, ctx)).ToList();
        var campaignDtos = BuildCampaignDtos(ctx, voucherDtos);

        var totalDiscountGranted = voucherDtos.Sum(v => v.BudgetSpent);
        var totalGmv = ctx.Vouchers.Sum(v => CalculateVoucherGmvDriven(v, ctx.ActiveRedemptions));
        var totalRedemptions = voucherDtos.Sum(v => v.UsedCount);
        var globalBudget = BuildGlobalBudgetDto(ctx, totalDiscountGranted);

        var promoAuditActions = new[]
        {
            "VOUCHER_REDEEMED",
            "VOUCHER_RELEASED",
            "CREATE_VOUCHER",
            "UPDATE_VOUCHER",
            "TOGGLE_VOUCHER",
            "DELETE_VOUCHER",
            "CREATE_CAMPAIGN",
            "UPDATE_CAMPAIGN",
            "UPDATE_PROMO_BUDGET"
        };

        var recentAudit = await _db.AuditLogs.AsNoTracking()
            .Where(a => promoAuditActions.Contains(a.Action))
            .OrderByDescending(a => a.CreatedAt)
            .Take(30)
            .ToListAsync();

        var ledger = recentAudit.Select(a =>
        {
            var matchRedemption = ctx.ActiveRedemptions.FirstOrDefault(r =>
                !string.IsNullOrWhiteSpace(a.TargetId) &&
                string.Equals(r.VoucherId.ToString(), a.TargetId, StringComparison.OrdinalIgnoreCase) &&
                Math.Abs((r.RedeemedAt - a.CreatedAt).TotalSeconds) < 10);

            return new PromotionLedgerItemDto
            {
                Id = a.Id,
                EventType = a.Action,
                VoucherCode = matchRedemption?.VoucherCode ?? (a.TargetEntity == "Voucher" ? (a.TargetId ?? "—") : a.TargetEntity),
                CampaignName = matchRedemption != null && ctx.Campaigns.TryGetValue(matchRedemption.CampaignId, out var c)
                    ? c.Name
                    : "Quản trị Khuyến mại & Ngân sách",
                BookingCode = matchRedemption?.BookingCode,
                ActorEmail = a.UserEmail ?? "system@smartlocker.vn",
                ActorRole = a.UserRole ?? "ADMIN",
                OriginalAmount = matchRedemption?.OriginalAmount ?? 0m,
                DiscountAmount = matchRedemption?.DiscountAmount ?? 0m,
                FinalAmount = matchRedemption?.FinalAmount ?? 0m,
                Summary = a.Details ?? a.Action,
                CreatedAt = a.CreatedAt
            };
        }).ToList();

        var overview = new PromotionDashboardOverviewDto
        {
            GlobalBudget = globalBudget,
            TotalCampaigns = campaignDtos.Count,
            ActiveCampaigns = campaignDtos.Count(c => c.Status == "ACTIVE"),
            TotalVouchers = voucherDtos.Count,
            ActiveVouchers = voucherDtos.Count(v => v.EffectiveStatus == "ACTIVE"),
            TotalRedemptionsCount = totalRedemptions,
            TotalDiscountGranted = totalDiscountGranted,
            TotalGmvDriven = totalGmv,
            AverageDiscountPerOrder = totalRedemptions > 0
                ? Math.Round(totalDiscountGranted / totalRedemptions, 0)
                : 0m,
            RoiMultiple = totalDiscountGranted > 0
                ? Math.Round(totalGmv / totalDiscountGranted, 2)
                : 0m,
            Campaigns = campaignDtos,
            Vouchers = voucherDtos,
            RecentLedger = ledger
        };

        return ApiResponse<PromotionDashboardOverviewDto>.SuccessResponse(overview);
    }

    public async Task<ApiResponse<List<VoucherAdminDto>>> GetAdminVouchersAsync(
        string? search = null,
        string? campaignId = null,
        string? status = null)
    {
        var ctx = await LoadContextAsync(false);
        var items = ctx.Vouchers.Select(v => BuildVoucherAdminDto(v, ctx)).AsEnumerable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var kw = search.Trim().ToLowerInvariant();
            items = items.Where(v =>
                v.Code.ToLowerInvariant().Contains(kw) ||
                v.Title.ToLowerInvariant().Contains(kw) ||
                v.CampaignName.ToLowerInvariant().Contains(kw));
        }

        if (!string.IsNullOrWhiteSpace(campaignId) && !campaignId.Equals("ALL", StringComparison.OrdinalIgnoreCase))
        {
            items = items.Where(v => string.Equals(v.CampaignId, campaignId.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(status) && !status.Equals("ALL", StringComparison.OrdinalIgnoreCase))
        {
            items = items.Where(v => string.Equals(v.EffectiveStatus, status.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        return ApiResponse<List<VoucherAdminDto>>.SuccessResponse(items.ToList());
    }

    public async Task<ApiResponse<GlobalPromotionBudgetDto>> UpdateGlobalBudgetPolicyAsync(
        UpdateGlobalPromotionBudgetRequest request,
        Guid adminId)
    {
        if (request.MonthlyBudgetCap < 500000m)
            return ApiResponse<GlobalPromotionBudgetDto>.ErrorResponse("Hạn mức ngân sách khuyến mại tháng tối thiểu phải từ 500.000 VNĐ.");

        if (request.WarningThresholdPercent < 50 || request.WarningThresholdPercent > 99)
            return ApiResponse<GlobalPromotionBudgetDto>.ErrorResponse("Ngưỡng cảnh báo ngân sách phải nằm trong khoảng 50% – 99%.");

        if (request.MaxDiscountPercentPerOrder < 10 || request.MaxDiscountPercentPerOrder > 95)
            return ApiResponse<GlobalPromotionBudgetDto>.ErrorResponse("Trần tỷ lệ giảm tối đa trên mỗi đơn phải từ 10% đến 95%.");

        if (request.MinPayableAmountAfterDiscount < 2000m)
            return ApiResponse<GlobalPromotionBudgetDto>.ErrorResponse("Số tiền thanh toán tối thiểu sau chiết khấu phải từ 2.000 VNĐ để tương thích cổng VietQR PayOS.");

        var stored = new StoredGlobalBudget
        {
            MonthlyBudgetCap = Math.Round(request.MonthlyBudgetCap, 0),
            WarningThresholdPercent = request.WarningThresholdPercent,
            MaxDiscountPercentPerOrder = request.MaxDiscountPercentPerOrder,
            MinPayableAmountAfterDiscount = Math.Round(request.MinPayableAmountAfterDiscount, 0),
            AutoPauseOnBudgetExhausted = request.AutoPauseOnBudgetExhausted,
            UpdatedAt = DateTime.UtcNow
        };

        await SaveMetaSnapshotAsync(adminId, GlobalBudgetEntity, "GLOBAL", stored);

        var (actorEmail, actorRole) = await GetActorInfoAsync(adminId);
        await _auditLog.LogAsync(
            adminId,
            actorEmail,
            actorRole,
            "UPDATE_PROMO_BUDGET",
            "PromotionBudget",
            "GLOBAL",
            $"Cập nhật chính sách ngân sách khuyến mại tháng: Hạn mức={stored.MonthlyBudgetCap:N0}đ, Cảnh báo={stored.WarningThresholdPercent}%, Trần giảm/đơn={stored.MaxDiscountPercentPerOrder}%, Tối thiểu PayOS={stored.MinPayableAmountAfterDiscount:N0}đ. Lý do: {request.Reason ?? "Điều chỉnh ngân sách định kỳ"}");

        var ctx = await LoadContextAsync(false);
        var totalSpent = ctx.Vouchers.Sum(v => CalculateVoucherBudgetSpent(v, ctx.ActiveRedemptions));
        return ApiResponse<GlobalPromotionBudgetDto>.SuccessResponse(
            BuildGlobalBudgetDto(ctx, totalSpent),
            "Đã cập nhật chính sách ngân sách chiết khấu toàn hệ thống.");
    }

    public async Task<ApiResponse<PromotionCampaignDto>> CreateCampaignAsync(
        UpsertPromotionCampaignRequest request,
        Guid adminId)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length < 3)
            return ApiResponse<PromotionCampaignDto>.ErrorResponse("Tên chiến dịch khuyến mại phải có ít nhất 3 ký tự.");

        if (request.AllocatedBudget < 100000m)
            return ApiResponse<PromotionCampaignDto>.ErrorResponse("Ngân sách phân bổ cho chiến dịch tối thiểu từ 100.000 VNĐ.");

        if (request.EndDate <= request.StartDate)
            return ApiResponse<PromotionCampaignDto>.ErrorResponse("Thời gian kết thúc chiến dịch phải lớn hơn thời gian bắt đầu.");

        var ctx = await LoadContextAsync(false);
        var normalizedCode = string.IsNullOrWhiteSpace(request.Code)
            ? $"CMP-{DateTime.UtcNow:yyMMdd}-{Random.Shared.Next(100, 999)}"
            : Regex.Replace(request.Code.Trim().ToUpperInvariant(), @"[^A-Z0-9\-_]", "-");

        if (ctx.Campaigns.Values.Any(c => string.Equals(c.Code, normalizedCode, StringComparison.OrdinalIgnoreCase)))
            return ApiResponse<PromotionCampaignDto>.ErrorResponse($"Mã chiến dịch \"{normalizedCode}\" đã tồn tại.");

        var cmp = new StoredCampaignMeta
        {
            Id = normalizedCode,
            Code = normalizedCode,
            Name = request.Name.Trim(),
            Description = request.Description?.Trim() ?? string.Empty,
            Category = string.IsNullOrWhiteSpace(request.Category) ? "SEASONAL" : request.Category.Trim().ToUpperInvariant(),
            AllocatedBudget = Math.Round(request.AllocatedBudget, 0),
            StartDate = request.StartDate,
            EndDate = request.EndDate,
            IsPaused = request.IsPaused,
            AutoPauseWhenDepleted = request.AutoPauseWhenDepleted,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        await SaveMetaSnapshotAsync(adminId, CampaignMetaEntity, cmp.Id, cmp);

        var (actorEmail, actorRole) = await GetActorInfoAsync(adminId);
        await _auditLog.LogAsync(
            adminId,
            actorEmail,
            actorRole,
            "CREATE_CAMPAIGN",
            "PromotionCampaign",
            cmp.Id,
            $"Tạo chiến dịch khuyến mại mới \"{cmp.Name}\" ({cmp.Code}) — Ngân sách phân bổ: {cmp.AllocatedBudget:N0}đ ({cmp.StartDate:dd/MM/yyyy} -> {cmp.EndDate:dd/MM/yyyy}).");

        var refreshed = await LoadContextAsync(false);
        var voucherDtos = refreshed.Vouchers.Select(v => BuildVoucherAdminDto(v, refreshed)).ToList();
        var createdDto = BuildCampaignDtos(refreshed, voucherDtos).First(c => c.Id == cmp.Id);
        return ApiResponse<PromotionCampaignDto>.SuccessResponse(createdDto, $"Đã thiết lập chiến dịch \"{cmp.Name}\" thành công.");
    }

    public async Task<ApiResponse<PromotionCampaignDto>> UpdateCampaignAsync(
        string campaignId,
        UpsertPromotionCampaignRequest request,
        Guid adminId)
    {
        var ctx = await LoadContextAsync(false);
        if (!ctx.Campaigns.TryGetValue(campaignId, out var existing))
            return ApiResponse<PromotionCampaignDto>.ErrorResponse("Không tìm thấy chiến dịch khuyến mại cần cập nhật.");

        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length < 3)
            return ApiResponse<PromotionCampaignDto>.ErrorResponse("Tên chiến dịch khuyến mại không hợp lệ.");

        if (request.EndDate <= request.StartDate)
            return ApiResponse<PromotionCampaignDto>.ErrorResponse("Thời gian kết thúc chiến dịch phải sau thời gian bắt đầu.");

        var voucherDtos = ctx.Vouchers.Select(v => BuildVoucherAdminDto(v, ctx)).ToList();
        var currentSpent = voucherDtos
            .Where(v => string.Equals(v.CampaignId, existing.Id, StringComparison.OrdinalIgnoreCase))
            .Sum(v => v.BudgetSpent);

        if (request.AllocatedBudget < currentSpent)
        {
            return ApiResponse<PromotionCampaignDto>.ErrorResponse(
                $"Ngân sách phân bổ ({request.AllocatedBudget:N0}đ) không được thấp hơn số tiền chiết khấu đã giải ngân của chiến dịch ({currentSpent:N0}đ).");
        }

        existing.Name = request.Name.Trim();
        existing.Description = request.Description?.Trim() ?? string.Empty;
        existing.Category = string.IsNullOrWhiteSpace(request.Category) ? existing.Category : request.Category.Trim().ToUpperInvariant();
        existing.AllocatedBudget = Math.Round(request.AllocatedBudget, 0);
        existing.StartDate = request.StartDate;
        existing.EndDate = request.EndDate;
        existing.IsPaused = request.IsPaused;
        existing.AutoPauseWhenDepleted = request.AutoPauseWhenDepleted;
        existing.UpdatedAt = DateTime.UtcNow;

        await SaveMetaSnapshotAsync(adminId, CampaignMetaEntity, existing.Id, existing);

        var (actorEmail, actorRole) = await GetActorInfoAsync(adminId);
        await _auditLog.LogAsync(
            adminId,
            actorEmail,
            actorRole,
            "UPDATE_CAMPAIGN",
            "PromotionCampaign",
            existing.Id,
            $"Cập nhật chiến dịch \"{existing.Name}\" ({existing.Code}) — Ngân sách: {existing.AllocatedBudget:N0}đ, Tạm dừng={existing.IsPaused}. Lý do: {request.Reason ?? "Điều chỉnh kế hoạch khuyến mại"}");

        var refreshed = await LoadContextAsync(false);
        var refreshedVouchers = refreshed.Vouchers.Select(v => BuildVoucherAdminDto(v, refreshed)).ToList();
        var updatedDto = BuildCampaignDtos(refreshed, refreshedVouchers).First(c => c.Id == existing.Id);
        return ApiResponse<PromotionCampaignDto>.SuccessResponse(updatedDto, "Đã cập nhật chiến dịch khuyến mại thành công.");
    }

    public async Task<ApiResponse<PromotionCampaignDto>> UpdateCampaignStatusOrTopUpAsync(
        string campaignId,
        UpdateCampaignStatusRequest request,
        Guid adminId)
    {
        var ctx = await LoadContextAsync(false);
        if (!ctx.Campaigns.TryGetValue(campaignId, out var existing))
            return ApiResponse<PromotionCampaignDto>.ErrorResponse("Không tìm thấy chiến dịch khuyến mại.");

        existing.IsPaused = request.IsPaused;
        if (request.TopUpBudgetAmount.HasValue && request.TopUpBudgetAmount.Value > 0)
        {
            existing.AllocatedBudget += Math.Round(request.TopUpBudgetAmount.Value, 0);
        }
        existing.UpdatedAt = DateTime.UtcNow;

        await SaveMetaSnapshotAsync(adminId, CampaignMetaEntity, existing.Id, existing);

        var (actorEmail, actorRole) = await GetActorInfoAsync(adminId);
        await _auditLog.LogAsync(
            adminId,
            actorEmail,
            actorRole,
            "UPDATE_CAMPAIGN",
            "PromotionCampaign",
            existing.Id,
            $"Điều chỉnh trạng thái/ngân sách chiến dịch \"{existing.Name}\": Trạng thái={(existing.IsPaused ? "PAUSED" : "ACTIVE")}, Ngân sách hiện tại={existing.AllocatedBudget:N0}đ. Lý do: {request.Reason ?? "Quản trị viên thao tác"}");

        var refreshed = await LoadContextAsync(false);
        var refreshedVouchers = refreshed.Vouchers.Select(v => BuildVoucherAdminDto(v, refreshed)).ToList();
        var updatedDto = BuildCampaignDtos(refreshed, refreshedVouchers).First(c => c.Id == existing.Id);
        return ApiResponse<PromotionCampaignDto>.SuccessResponse(updatedDto, "Đã cập nhật trạng thái & ngân sách chiến dịch.");
    }

    private static string? ValidateVoucherInputs(UpsertVoucherRequest request, out string cleanCode, out string cleanType)
    {
        cleanCode = Regex.Replace((request.Code ?? string.Empty).Trim().ToUpperInvariant(), @"[^A-Z0-9\-_]", "");
        cleanType = (request.DiscountType ?? "PERCENTAGE").Trim().ToUpperInvariant();

        if (cleanCode.Length < 3 || cleanCode.Length > 40)
            return "Mã Voucher phải từ 3 đến 40 ký tự chữ/số in hoa (VD: SUMMER30, SMART20K).";

        if (string.IsNullOrWhiteSpace(request.Title) || request.Title.Trim().Length < 3 || request.Title.Trim().Length > 150)
            return "Tiêu đề ưu đãi phải từ 3 đến 150 ký tự.";

        if (!string.IsNullOrWhiteSpace(request.Description) && request.Description.Trim().Length > 350)
            return "Mô tả chi tiết không vượt quá 350 ký tự.";

        if (cleanType != "PERCENTAGE" && cleanType != "FIXED")
            return "Loại chiết khấu chỉ hỗ trợ PERCENTAGE (Theo %) hoặc FIXED (Số tiền cố định VNĐ).";

        if (cleanType == "PERCENTAGE")
        {
            if (request.DiscountValue < 1m || request.DiscountValue > 95m)
                return "Mức giảm theo phần trăm phải nằm trong khoảng 1% – 95%.";
            if (!request.MaxDiscountAmount.HasValue || request.MaxDiscountAmount.Value < 1000m)
                return "Mã giảm theo phần trăm bắt buộc phải thiết lập Mức giảm tối đa (MaxDiscountAmount >= 1.000đ) để bảo vệ ngân sách.";
        }
        else
        {
            if (request.DiscountValue < 1000m)
                return "Mức giảm tiền trực tiếp (FIXED) tối thiểu phải từ 1.000 VNĐ.";
        }

        if (request.MinOrderAmount < 0m)
            return "Giá trị đơn hàng tối thiểu không được âm.";

        if (request.UsageLimit < 1)
            return "Tổng giới hạn lượt sử dụng (UsageLimit) phải lớn hơn hoặc bằng 1.";

        if (request.EndDate <= request.StartDate)
            return "Ngày kết thúc hiệu lực của mã Voucher phải lớn hơn ngày bắt đầu.";

        return null;
    }

    public async Task<ApiResponse<VoucherAdminDto>> CreateVoucherAsync(UpsertVoucherRequest request, Guid adminId)
    {
        var validationError = ValidateVoucherInputs(request, out var cleanCode, out var cleanType);
        if (validationError != null)
            return ApiResponse<VoucherAdminDto>.ErrorResponse(validationError);

        var duplicate = await _db.Vouchers.AnyAsync(v => v.Code == cleanCode);
        if (duplicate)
            return ApiResponse<VoucherAdminDto>.ErrorResponse($"Mã Voucher \"{cleanCode}\" đã tồn tại trong hệ thống.");

        var ctx = await LoadContextAsync(false);
        var campaignId = !string.IsNullOrWhiteSpace(request.CampaignId) && ctx.Campaigns.ContainsKey(request.CampaignId)
            ? request.CampaignId.Trim()
            : InferDefaultCampaignId(cleanCode);

        var maxDiscount = cleanType == "FIXED"
            ? request.DiscountValue
            : request.MaxDiscountAmount!.Value;

        var defaultCap = maxDiscount * Math.Min(request.UsageLimit, 200);
        var budgetCap = request.BudgetCap.HasValue && request.BudgetCap.Value > 0
            ? Math.Round(request.BudgetCap.Value, 0)
            : defaultCap;

        if (budgetCap < maxDiscount)
            return ApiResponse<VoucherAdminDto>.ErrorResponse($"Ngân sách cấp cho mã ({budgetCap:N0}đ) không được nhỏ hơn mức giảm tối đa 1 lượt ({maxDiscount:N0}đ).");

        if (ctx.Campaigns.TryGetValue(campaignId, out var cmp) && budgetCap > cmp.AllocatedBudget)
        {
            return ApiResponse<VoucherAdminDto>.ErrorResponse(
                $"Ngân sách riêng của mã ({budgetCap:N0}đ) không được vượt quá tổng ngân sách của chiến dịch \"{cmp.Name}\" ({cmp.AllocatedBudget:N0}đ).");
        }

        var voucher = new Voucher
        {
            Id = Guid.NewGuid(),
            Code = cleanCode,
            Title = request.Title.Trim(),
            Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(),
            DiscountType = cleanType,
            DiscountValue = Math.Round(request.DiscountValue, 2),
            MaxDiscountAmount = Math.Round(maxDiscount, 2),
            MinOrderAmount = Math.Round(request.MinOrderAmount, 2),
            UsageLimit = request.UsageLimit,
            UsedCount = 0,
            StartDate = request.StartDate,
            EndDate = request.EndDate,
            IsActive = request.IsActive,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _db.Vouchers.Add(voucher);
        await _db.SaveChangesAsync();

        var meta = new StoredVoucherMeta
        {
            VoucherId = voucher.Id,
            CampaignId = campaignId,
            BudgetCap = budgetCap,
            PerUserLimit = Math.Clamp(request.PerUserLimit, 1, 100),
            ApplicableSizes = request.ApplicableSizes?.Count > 0
                ? request.ApplicableSizes.Select(s => s.Trim().ToUpperInvariant()).Distinct().ToList()
                : new List<string> { "S", "M", "L" },
            ApplicableStationIds = request.ApplicableStationIds?.Distinct().ToList() ?? new List<Guid>()
        };

        await SaveMetaSnapshotAsync(adminId, VoucherMetaEntity, voucher.Id.ToString(), meta);

        var (actorEmail, actorRole) = await GetActorInfoAsync(adminId);
        await _auditLog.LogAsync(
            adminId,
            actorEmail,
            actorRole,
            "CREATE_VOUCHER",
            "Voucher",
            voucher.Code,
            $"Phát hành mã Voucher {voucher.Code} ({voucher.Title}) thuộc chiến dịch {campaignId} — {voucher.DiscountType} {voucher.DiscountValue:N0}, Ngân sách mã: {budgetCap:N0}đ, Tối đa {voucher.UsageLimit} lượt.");

        var refreshed = await LoadContextAsync(false);
        var created = refreshed.Vouchers.First(v => v.Id == voucher.Id);
        return ApiResponse<VoucherAdminDto>.SuccessResponse(
            BuildVoucherAdminDto(created, refreshed),
            $"Đã phát hành mã Voucher {voucher.Code} thành công!");
    }

    public async Task<ApiResponse<VoucherAdminDto>> UpdateVoucherAsync(
        Guid voucherId,
        UpsertVoucherRequest request,
        Guid adminId)
    {
        var validationError = ValidateVoucherInputs(request, out var cleanCode, out var cleanType);
        if (validationError != null)
            return ApiResponse<VoucherAdminDto>.ErrorResponse(validationError);

        var voucher = await _db.Vouchers.FirstOrDefaultAsync(v => v.Id == voucherId);
        if (voucher == null)
            return ApiResponse<VoucherAdminDto>.ErrorResponse("Không tìm thấy mã Voucher cần chỉnh sửa.");

        var codeConflict = await _db.Vouchers.AnyAsync(v => v.Id != voucherId && v.Code == cleanCode);
        if (codeConflict)
            return ApiResponse<VoucherAdminDto>.ErrorResponse($"Mã Voucher \"{cleanCode}\" đã được sử dụng cho ưu đãi khác.");

        if (request.UsageLimit < voucher.UsedCount)
            return ApiResponse<VoucherAdminDto>.ErrorResponse(
                $"Giới hạn lượt dùng mới ({request.UsageLimit}) không được nhỏ hơn số lượt đã sử dụng thực tế ({voucher.UsedCount}).");

        var ctx = await LoadContextAsync(false);
        var currentSpent = CalculateVoucherBudgetSpent(voucher, ctx.ActiveRedemptions);

        var campaignId = !string.IsNullOrWhiteSpace(request.CampaignId) && ctx.Campaigns.ContainsKey(request.CampaignId)
            ? request.CampaignId.Trim()
            : (ctx.VoucherMetas.TryGetValue(voucherId, out var oldMeta) ? oldMeta.CampaignId : InferDefaultCampaignId(cleanCode));

        var maxDiscount = cleanType == "FIXED"
            ? request.DiscountValue
            : request.MaxDiscountAmount!.Value;

        var budgetCap = request.BudgetCap.HasValue && request.BudgetCap.Value > 0
            ? Math.Round(request.BudgetCap.Value, 0)
            : ComputeDefaultVoucherBudgetCap(voucher);

        if (budgetCap < currentSpent)
            return ApiResponse<VoucherAdminDto>.ErrorResponse(
                $"Ngân sách mã ({budgetCap:N0}đ) không được thấp hơn số tiền chiết khấu đã giải ngân ({currentSpent:N0}đ).");

        voucher.Code = cleanCode;
        voucher.Title = request.Title.Trim();
        voucher.Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        voucher.DiscountType = cleanType;
        voucher.DiscountValue = Math.Round(request.DiscountValue, 2);
        voucher.MaxDiscountAmount = Math.Round(maxDiscount, 2);
        voucher.MinOrderAmount = Math.Round(request.MinOrderAmount, 2);
        voucher.UsageLimit = request.UsageLimit;
        voucher.StartDate = request.StartDate;
        voucher.EndDate = request.EndDate;
        voucher.IsActive = request.IsActive;
        voucher.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();

        var newMeta = new StoredVoucherMeta
        {
            VoucherId = voucher.Id,
            CampaignId = campaignId,
            BudgetCap = budgetCap,
            PerUserLimit = Math.Clamp(request.PerUserLimit, 1, 100),
            ApplicableSizes = request.ApplicableSizes?.Count > 0
                ? request.ApplicableSizes.Select(s => s.Trim().ToUpperInvariant()).Distinct().ToList()
                : new List<string> { "S", "M", "L" },
            ApplicableStationIds = request.ApplicableStationIds?.Distinct().ToList() ?? new List<Guid>()
        };

        await SaveMetaSnapshotAsync(adminId, VoucherMetaEntity, voucher.Id.ToString(), newMeta);

        var (actorEmail, actorRole) = await GetActorInfoAsync(adminId);
        await _auditLog.LogAsync(
            adminId,
            actorEmail,
            actorRole,
            "UPDATE_VOUCHER",
            "Voucher",
            voucher.Code,
            $"Cập nhật mã Voucher {voucher.Code} — Ngân sách mã: {budgetCap:N0}đ, Lượt tối đa: {voucher.UsageLimit}, Kích hoạt={voucher.IsActive}. Lý do: {request.Reason ?? "Cập nhật điều kiện khuyến mại"}");

        var refreshed = await LoadContextAsync(false);
        var updated = refreshed.Vouchers.First(v => v.Id == voucher.Id);
        return ApiResponse<VoucherAdminDto>.SuccessResponse(
            BuildVoucherAdminDto(updated, refreshed),
            $"Đã lưu cập nhật mã Voucher {voucher.Code}.");
    }

    public async Task<ApiResponse<VoucherAdminDto>> ToggleVoucherStatusAsync(
        Guid voucherId,
        ToggleVoucherStatusRequest request,
        Guid adminId)
    {
        var voucher = await _db.Vouchers.FirstOrDefaultAsync(v => v.Id == voucherId);
        if (voucher == null)
            return ApiResponse<VoucherAdminDto>.ErrorResponse("Không tìm thấy mã Voucher.");

        voucher.IsActive = request.IsActive;
        voucher.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        var (actorEmail, actorRole) = await GetActorInfoAsync(adminId);
        await _auditLog.LogAsync(
            adminId,
            actorEmail,
            actorRole,
            "TOGGLE_VOUCHER",
            "Voucher",
            voucher.Code,
            $"{(request.IsActive ? "Kích hoạt mở lại" : "Tạm ngưng áp dụng")} mã Voucher {voucher.Code}. Lý do: {request.Reason ?? "Điều hành khuyến mại"}");

        var refreshed = await LoadContextAsync(false);
        var updated = refreshed.Vouchers.First(v => v.Id == voucher.Id);
        return ApiResponse<VoucherAdminDto>.SuccessResponse(
            BuildVoucherAdminDto(updated, refreshed),
            request.IsActive
                ? $"Đã kích hoạt mã Voucher {voucher.Code}."
                : $"Đã tạm dừng mã Voucher {voucher.Code}.");
    }

    public async Task<ApiResponse<bool>> DeleteOrDeactivateVoucherAsync(
        Guid voucherId,
        string? reason,
        Guid adminId)
    {
        var voucher = await _db.Vouchers.FirstOrDefaultAsync(v => v.Id == voucherId);
        if (voucher == null)
            return ApiResponse<bool>.ErrorResponse("Không tìm thấy mã Voucher.");

        var (actorEmail, actorRole) = await GetActorInfoAsync(adminId);

        // Nếu mã đã từng phát sinh lượt sử dụng -> Vô hiệu hóa bảo toàn lịch sử đối soát ngân sách
        if (voucher.UsedCount > 0)
        {
            voucher.IsActive = false;
            voucher.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();

            await _auditLog.LogAsync(
                adminId,
                actorEmail,
                actorRole,
                "DELETE_VOUCHER",
                "Voucher",
                voucher.Code,
                $"Ngưng vĩnh viễn mã Voucher {voucher.Code} (giữ lại bản ghi vì đã có {voucher.UsedCount} lượt sử dụng phục vụ đối soát ngân sách). Lý do: {reason ?? "Thu hồi mã"}");

            return ApiResponse<bool>.SuccessResponse(
                true,
                $"Mã {voucher.Code} đã có {voucher.UsedCount} lượt dùng nên được chuyển sang trạng thái Ngưng hoạt động để bảo toàn số liệu ngân sách.");
        }

        var code = voucher.Code;
        _db.Vouchers.Remove(voucher);
        await _db.SaveChangesAsync();

        await _auditLog.LogAsync(
            adminId,
            actorEmail,
            actorRole,
            "DELETE_VOUCHER",
            "Voucher",
            code,
            $"Xóa hoàn toàn mã Voucher chưa qua sử dụng {code}. Lý do: {reason ?? "Dọn dẹp kho mã"}");

        return ApiResponse<bool>.SuccessResponse(true, $"Đã xóa mã Voucher {code} khỏi hệ thống.");
    }

    public async Task<ApiResponse<VoucherValidationResultDto>> SimulateVoucherAsync(ValidateVoucherRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Code))
            return ApiResponse<VoucherValidationResultDto>.ErrorResponse("Vui lòng nhập hoặc chọn mã Voucher cần mô phỏng.");

        var cleanCode = request.Code.Trim().ToUpperInvariant();
        var ctx = await LoadContextAsync(false);
        var voucher = ctx.Vouchers.FirstOrDefault(v => string.Equals(v.Code, cleanCode, StringComparison.OrdinalIgnoreCase));
        if (voucher == null)
            return ApiResponse<VoucherValidationResultDto>.ErrorResponse($"Mã ưu đãi \"{cleanCode}\" không tồn tại.");

        var eval = EvaluateVoucherCore(ctx, voucher, null, request.StationId, request.Size, request.OrderAmount, false);
        return ApiResponse<VoucherValidationResultDto>.SuccessResponse(eval, eval.Message);
    }

    // ─── Traveler & Booking Checkout Implementations ──────────────────────────

    public async Task<ApiResponse<List<AvailableVoucherItemDto>>> GetAvailableVouchersForBookingAsync(
        Guid? userId,
        Guid? stationId,
        string? size,
        decimal orderAmount)
    {
        var now = DateTime.UtcNow;
        var ctx = await LoadContextAsync(false);
        var list = new List<AvailableVoucherItemDto>();

        foreach (var v in ctx.Vouchers.Where(x => x.IsActive && x.EndDate >= now && x.StartDate <= now))
        {
            var eval = EvaluateVoucherCore(
                ctx,
                v,
                userId,
                stationId,
                size,
                orderAmount > 0 ? orderAmount : Math.Max(v.MinOrderAmount, 30000m),
                userId.HasValue);

            var actualCheck = orderAmount > 0
                ? eval
                : EvaluateVoucherCore(ctx, v, userId, stationId, size, 0m, userId.HasValue);

            // Không hiển thị các mã đã cạn kiệt ngân sách/hết lượt hoặc chiến dịch đang tạm dừng
            if (!eval.IsValid && orderAmount <= 0 &&
                (eval.Message.Contains("tạm dừng") || eval.Message.Contains("hết tổng số lượt") || eval.Message.Contains("ngân sách")))
            {
                continue;
            }

            list.Add(new AvailableVoucherItemDto
            {
                Id = v.Id,
                Code = v.Code,
                Title = v.Title,
                Description = v.Description,
                CampaignName = eval.CampaignName,
                DiscountType = v.DiscountType.ToUpperInvariant(),
                DiscountValue = v.DiscountValue,
                MaxDiscountAmount = v.MaxDiscountAmount,
                MinOrderAmount = v.MinOrderAmount,
                RemainingUses = Math.Max(0, v.UsageLimit - v.UsedCount),
                EndDate = v.EndDate,
                IsEligibleNow = orderAmount > 0 && actualCheck.IsValid,
                EstimatedDiscount = orderAmount > 0 && actualCheck.IsValid ? actualCheck.DiscountAmount : 0m,
                FinalPayableAmount = orderAmount > 0 && actualCheck.IsValid ? actualCheck.FinalPayableAmount : orderAmount,
                IneligibleReason = orderAmount > 0 && !actualCheck.IsValid ? actualCheck.Message : null
            });
        }

        var ordered = list
            .OrderByDescending(x => x.IsEligibleNow)
            .ThenByDescending(x => x.EstimatedDiscount)
            .ThenBy(x => x.MinOrderAmount)
            .ToList();

        return ApiResponse<List<AvailableVoucherItemDto>>.SuccessResponse(ordered);
    }

    public async Task<ApiResponse<VoucherValidationResultDto>> ValidateVoucherForUserAsync(
        Guid? userId,
        ValidateVoucherRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Code))
            return ApiResponse<VoucherValidationResultDto>.ErrorResponse("Vui lòng nhập mã Voucher.");

        var cleanCode = request.Code.Trim().ToUpperInvariant();
        var ctx = await LoadContextAsync(false);
        var voucher = ctx.Vouchers.FirstOrDefault(v => string.Equals(v.Code, cleanCode, StringComparison.OrdinalIgnoreCase));
        if (voucher == null)
            return ApiResponse<VoucherValidationResultDto>.ErrorResponse($"Mã giảm giá \"{cleanCode}\" không tồn tại hoặc đã bị thu hồi.");

        var eval = EvaluateVoucherCore(ctx, voucher, userId, request.StationId, request.Size, request.OrderAmount, true);
        if (!eval.IsValid)
            return ApiResponse<VoucherValidationResultDto>.ErrorResponse(eval.Message);

        return ApiResponse<VoucherValidationResultDto>.SuccessResponse(eval, eval.Message);
    }

    public async Task<ApiResponse<VoucherValidationResultDto>> RedeemVoucherForBookingAsync(
        Guid userId,
        Guid bookingId,
        string bookingCode,
        Guid stationId,
        string size,
        decimal originalAmount,
        string voucherCode)
    {
        await _redeemSemaphore.WaitAsync();
        try
        {
            var cleanCode = voucherCode.Trim().ToUpperInvariant();
            var ctx = await LoadContextAsync(trackVouchers: true);
            var voucher = ctx.Vouchers.FirstOrDefault(v => string.Equals(v.Code, cleanCode, StringComparison.OrdinalIgnoreCase));
            if (voucher == null)
                return ApiResponse<VoucherValidationResultDto>.ErrorResponse($"Mã ưu đãi \"{cleanCode}\" không tồn tại.");

            var eval = EvaluateVoucherCore(ctx, voucher, userId, stationId, size, originalAmount, checkUserQuota: true);
            if (!eval.IsValid)
                return ApiResponse<VoucherValidationResultDto>.ErrorResponse(eval.Message);

            // Trừ lượt sử dụng ngay trong giao dịch đặt tủ
            voucher.UsedCount += 1;
            voucher.UpdatedAt = DateTime.UtcNow;

            // Tự động ngắt hiệu lực nếu bật chế độ AutoPauseOnBudgetExhausted và ngân sách/lượt đã chạm đáy
            if (ctx.GlobalConfig.AutoPauseOnBudgetExhausted)
            {
                var minNextDiscount = string.Equals(voucher.DiscountType, "FIXED", StringComparison.OrdinalIgnoreCase)
                    ? voucher.DiscountValue
                    : 1000m;
                if (voucher.UsedCount >= voucher.UsageLimit || eval.VoucherBudgetRemainingAfter < minNextDiscount)
                {
                    voucher.IsActive = false;
                }
            }

            var redemption = new StoredRedemptionRecord
            {
                BookingId = bookingId,
                BookingCode = bookingCode,
                UserId = userId,
                VoucherId = voucher.Id,
                VoucherCode = voucher.Code,
                CampaignId = eval.CampaignId,
                OriginalAmount = originalAmount,
                DiscountAmount = eval.DiscountAmount,
                FinalAmount = eval.FinalPayableAmount,
                IsReleased = false,
                RedeemedAt = DateTime.UtcNow
            };

            var redemptionJson = JsonSerializer.Serialize(redemption, _jsonOpts);
            _db.AuditLogs.Add(new AuditLog
            {
                UserId = userId,
                UserEmail = "system-meta@smartlocker.vn",
                UserRole = "TRAVELER",
                Action = MetaAction,
                TargetEntity = RedemptionDataEntity,
                TargetId = bookingId.ToString(),
                Details = redemptionJson,
                CreatedAt = DateTime.UtcNow
            });

            var (userEmail, userRole) = await GetActorInfoAsync(userId);
            _db.AuditLogs.Add(new AuditLog
            {
                UserId = userId,
                UserEmail = userEmail,
                UserRole = userRole,
                Action = "VOUCHER_REDEEMED",
                TargetEntity = "Voucher",
                TargetId = voucher.Id.ToString(),
                Details = $"Đơn {bookingCode} áp mã {voucher.Code} ({eval.CampaignName}): Giá gốc {originalAmount:N0}đ - Giảm {eval.DiscountAmount:N0}đ = Còn {eval.FinalPayableAmount:N0}đ.",
                CreatedAt = DateTime.UtcNow
            });

            await _db.SaveChangesAsync();
            return ApiResponse<VoucherValidationResultDto>.SuccessResponse(eval, eval.Message);
        }
        finally
        {
            _redeemSemaphore.Release();
        }
    }

    public async Task ReleaseVoucherForBookingAsync(Guid bookingId, string reason)
    {
        await _redeemSemaphore.WaitAsync();
        try
        {
            var metaLogs = await _db.AuditLogs.AsNoTracking()
                .Where(a => a.Action == MetaAction &&
                            a.TargetEntity == RedemptionDataEntity &&
                            a.TargetId == bookingId.ToString())
                .OrderByDescending(a => a.Id)
                .FirstOrDefaultAsync();

            if (metaLogs == null || string.IsNullOrWhiteSpace(metaLogs.Details))
                return;

            var record = JsonSerializer.Deserialize<StoredRedemptionRecord>(metaLogs.Details, _jsonOpts);
            if (record == null || record.IsReleased)
                return;

            var voucher = await _db.Vouchers.FirstOrDefaultAsync(v => v.Id == record.VoucherId);
            if (voucher != null && voucher.UsedCount > 0)
            {
                voucher.UsedCount -= 1;
                if (!voucher.IsActive && DateTime.UtcNow <= voucher.EndDate && voucher.UsedCount < voucher.UsageLimit)
                {
                    voucher.IsActive = true;
                }
                voucher.UpdatedAt = DateTime.UtcNow;
            }

            record.IsReleased = true;
            _db.AuditLogs.Add(new AuditLog
            {
                UserId = record.UserId,
                UserEmail = "system-meta@smartlocker.vn",
                UserRole = "SYSTEM",
                Action = MetaAction,
                TargetEntity = RedemptionDataEntity,
                TargetId = bookingId.ToString(),
                Details = JsonSerializer.Serialize(record, _jsonOpts),
                CreatedAt = DateTime.UtcNow
            });

            _db.AuditLogs.Add(new AuditLog
            {
                UserId = record.UserId,
                UserEmail = "system@smartlocker.vn",
                UserRole = "SYSTEM",
                Action = "VOUCHER_RELEASED",
                TargetEntity = "Voucher",
                TargetId = record.VoucherId.ToString(),
                Details = $"Hoàn trả 01 lượt dùng & ngân sách chiết khấu {record.DiscountAmount:N0}đ của mã {record.VoucherCode} từ đơn {record.BookingCode} ({reason}).",
                CreatedAt = DateTime.UtcNow
            });

            await _db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Không thể hoàn trả ngân sách Voucher cho đơn {BookingId}", bookingId);
        }
        finally
        {
            _redeemSemaphore.Release();
        }
    }
}
