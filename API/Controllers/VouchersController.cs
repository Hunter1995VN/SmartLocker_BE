namespace API.Controllers;

using Application.DTOs.Common;
using Application.DTOs.Voucher;
using Application.Interfaces;
using Domain.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

[ApiController]
[Route("api/[controller]")]
public class VouchersController : ControllerBase
{
    private readonly ISmartLockerDbContext _db;
    private static bool _voucherTableChecked = false;

    public VouchersController(ISmartLockerDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// UC-T18: Lấy danh sách các mã giảm giá / Voucher đang có hiệu lực từ Database
    /// </summary>
    [HttpGet("available")]
    public async Task<IActionResult> GetAvailableVouchers([FromQuery] decimal orderAmount = 0)
    {
        if (!_voucherTableChecked)
        {
            await EnsureVouchersTableAndSeedAsync(_db);
            _voucherTableChecked = true;
        }

        var now = DateTime.UtcNow;
        var vouchers = await _db.Vouchers
            .AsNoTracking()
            .Where(v => v.IsActive && v.StartDate <= now && v.EndDate >= now && v.UsedCount < v.UsageLimit)
            .OrderBy(v => v.MinOrderAmount)
            .ThenByDescending(v => v.DiscountValue)
            .ToListAsync();

        var result = vouchers.Select(v =>
        {
            var isEligible = orderAmount <= 0 || orderAmount >= v.MinOrderAmount;
            var estimatedDiscount = isEligible && orderAmount > 0
                ? CalculateDiscountAmount(v, orderAmount)
                : 0m;

            string? reason = null;
            if (!isEligible)
            {
                reason = $"Đơn tối thiểu {v.MinOrderAmount:N0} VND";
            }

            return new VoucherDto
            {
                Id = v.Id,
                Code = v.Code,
                Title = v.Title,
                Description = v.Description,
                DiscountType = v.DiscountType,
                DiscountValue = v.DiscountValue,
                MaxDiscountAmount = v.MaxDiscountAmount,
                MinOrderAmount = v.MinOrderAmount,
                UsageLimit = v.UsageLimit,
                UsedCount = v.UsedCount,
                StartDate = v.StartDate,
                EndDate = v.EndDate,
                IsEligible = isEligible,
                EstimatedDiscount = estimatedDiscount,
                IneligibleReason = reason
            };
        }).ToList();

        return Ok(ApiResponse<List<VoucherDto>>.SuccessResponse(result));
    }

    /// <summary>
    /// UC-T18: Kiểm tra và tính toán số tiền chiết khấu của mã Voucher theo đơn hàng thực tế
    /// </summary>
    [HttpPost("validate")]
    public async Task<IActionResult> ValidateVoucher([FromBody] ValidateVoucherRequest request)
    {
        if (!_voucherTableChecked)
        {
            await EnsureVouchersTableAndSeedAsync(_db);
            _voucherTableChecked = true;
        }

        var code = (request.Code ?? string.Empty).Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(code))
        {
            return BadRequest(ApiResponse<ValidateVoucherResponse>.ErrorResponse("Vui lòng nhập mã giảm giá."));
        }

        var now = DateTime.UtcNow;
        var voucher = await _db.Vouchers
            .AsNoTracking()
            .FirstOrDefaultAsync(v => v.Code.ToUpper() == code);

        if (voucher == null || !voucher.IsActive)
        {
            return BadRequest(ApiResponse<ValidateVoucherResponse>.ErrorResponse($"Mã giảm giá '{code}' không tồn tại hoặc đã ngừng áp dụng."));
        }

        if (now < voucher.StartDate || now > voucher.EndDate)
        {
            return BadRequest(ApiResponse<ValidateVoucherResponse>.ErrorResponse($"Mã giảm giá '{code}' đã hết hạn sử dụng."));
        }

        if (voucher.UsedCount >= voucher.UsageLimit)
        {
            return BadRequest(ApiResponse<ValidateVoucherResponse>.ErrorResponse($"Mã giảm giá '{code}' đã hết lượt sử dụng."));
        }

        if (request.OrderAmount < voucher.MinOrderAmount)
        {
            return BadRequest(ApiResponse<ValidateVoucherResponse>.ErrorResponse(
                $"Mã '{code}' yêu cầu giá trị đơn đặt tủ tối thiểu {voucher.MinOrderAmount:N0} VND (hiện tại: {request.OrderAmount:N0} VND)."));
        }

        var discountAmount = CalculateDiscountAmount(voucher, request.OrderAmount);
        var finalAmount = Math.Max(2000m, request.OrderAmount - discountAmount);
        var actualDiscount = Math.Max(0m, request.OrderAmount - finalAmount);

        var response = new ValidateVoucherResponse
        {
            IsValid = true,
            Code = voucher.Code,
            Title = voucher.Title,
            Description = voucher.Description,
            DiscountType = voucher.DiscountType,
            DiscountValue = voucher.DiscountValue,
            MaxDiscountAmount = voucher.MaxDiscountAmount,
            MinOrderAmount = voucher.MinOrderAmount,
            OriginalAmount = request.OrderAmount,
            DiscountAmount = actualDiscount,
            FinalAmount = finalAmount,
            Message = $"Đã áp dụng mã {voucher.Code}: Giảm {actualDiscount:N0} VND!"
        };

        return Ok(ApiResponse<ValidateVoucherResponse>.SuccessResponse(response, response.Message));
    }

    public static decimal CalculateDiscountAmount(Voucher voucher, decimal orderAmount)
    {
        if (orderAmount <= 0) return 0m;

        decimal rawDiscount;
        if (string.Equals(voucher.DiscountType, "PERCENTAGE", StringComparison.OrdinalIgnoreCase))
        {
            rawDiscount = Math.Round(orderAmount * (voucher.DiscountValue / 100m), 0);
            if (voucher.MaxDiscountAmount.HasValue && voucher.MaxDiscountAmount.Value > 0)
            {
                rawDiscount = Math.Min(rawDiscount, voucher.MaxDiscountAmount.Value);
            }
        }
        else
        {
            rawDiscount = voucher.DiscountValue;
        }

        // Đảm bảo số tiền thanh toán qua PayOS luôn tối thiểu 2,000 VND
        var maxAllowedDiscount = Math.Max(0m, orderAmount - 2000m);
        return Math.Min(rawDiscount, maxAllowedDiscount);
    }

    public static async Task EnsureVouchersTableAndSeedAsync(ISmartLockerDbContext db)
    {
        try
        {
            await db.Database.ExecuteSqlRawAsync(@"
                IF OBJECT_ID(N'dbo.Vouchers', N'U') IS NULL
                BEGIN
                    CREATE TABLE dbo.Vouchers (
                        Id UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
                        Code NVARCHAR(40) NOT NULL UNIQUE,
                        Title NVARCHAR(150) NOT NULL,
                        Description NVARCHAR(350) NULL,
                        DiscountType NVARCHAR(20) NOT NULL DEFAULT 'PERCENTAGE',
                        DiscountValue DECIMAL(12,2) NOT NULL,
                        MaxDiscountAmount DECIMAL(12,2) NULL,
                        MinOrderAmount DECIMAL(12,2) NOT NULL DEFAULT 0,
                        UsageLimit INT NOT NULL DEFAULT 1000,
                        UsedCount INT NOT NULL DEFAULT 0,
                        StartDate DATETIME2 NOT NULL,
                        EndDate DATETIME2 NOT NULL,
                        IsActive BIT NOT NULL DEFAULT 1,
                        CreatedAt DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
                        UpdatedAt DATETIME2 NOT NULL DEFAULT GETUTCDATE()
                    );
                END
            ");

            var existingCount = await db.Vouchers.CountAsync();
            if (existingCount == 0)
            {
                var now = DateTime.UtcNow;
                var seeds = new List<Voucher>
                {
                    new Voucher
                    {
                        Id = Guid.NewGuid(),
                        Code = "SMART10K",
                        Title = "Giảm 10.000đ cho đơn từ 20.000đ",
                        Description = "Ưu đãi giảm trực tiếp 10.000đ cho mọi lượt đặt tủ từ 20.000đ trở lên.",
                        DiscountType = "FIXED",
                        DiscountValue = 10000m,
                        MaxDiscountAmount = 10000m,
                        MinOrderAmount = 20000m,
                        UsageLimit = 1000,
                        UsedCount = 12,
                        StartDate = now.AddMonths(-1),
                        EndDate = now.AddYears(1),
                        IsActive = true,
                        CreatedAt = now,
                        UpdatedAt = now
                    },
                    new Voucher
                    {
                        Id = Guid.NewGuid(),
                        Code = "DANANG20",
                        Title = "Giảm 20% (Tối đa 25.000đ) — Ưu đãi Đà Nẵng",
                        Description = "Khuyến mại chào mừng hệ thống trạm tủ thông minh SmartLocker tại TP. Đà Nẵng.",
                        DiscountType = "PERCENTAGE",
                        DiscountValue = 20m,
                        MaxDiscountAmount = 25000m,
                        MinOrderAmount = 15000m,
                        UsageLimit = 500,
                        UsedCount = 28,
                        StartDate = now.AddMonths(-1),
                        EndDate = now.AddYears(1),
                        IsActive = true,
                        CreatedAt = now,
                        UpdatedAt = now
                    },
                    new Voucher
                    {
                        Id = Guid.NewGuid(),
                        Code = "WELCOME30",
                        Title = "Giảm 30% (Tối đa 30.000đ) cho khách hàng mới",
                        Description = "Áp dụng cho đơn đặt tủ từ 30.000đ trở lên trên toàn hệ thống.",
                        DiscountType = "PERCENTAGE",
                        DiscountValue = 30m,
                        MaxDiscountAmount = 30000m,
                        MinOrderAmount = 30000m,
                        UsageLimit = 500,
                        UsedCount = 45,
                        StartDate = now.AddMonths(-1),
                        EndDate = now.AddYears(1),
                        IsActive = true,
                        CreatedAt = now,
                        UpdatedAt = now
                    },
                    new Voucher
                    {
                        Id = Guid.NewGuid(),
                        Code = "FPTCITY50",
                        Title = "Giảm 50% (Tối đa 20.000đ) — Ưu đãi FPT",
                        Description = "Dành cho sinh viên & cán bộ khu đô thị FPT City Đà Nẵng và Khu CNC.",
                        DiscountType = "PERCENTAGE",
                        DiscountValue = 50m,
                        MaxDiscountAmount = 20000m,
                        MinOrderAmount = 10000m,
                        UsageLimit = 300,
                        UsedCount = 19,
                        StartDate = now.AddMonths(-1),
                        EndDate = now.AddYears(1),
                        IsActive = true,
                        CreatedAt = now,
                        UpdatedAt = now
                    },
                    new Voucher
                    {
                        Id = Guid.NewGuid(),
                        Code = "LONGSTAY25K",
                        Title = "Giảm 25.000đ cho đơn thuê dài hạn từ 60.000đ",
                        Description = "Tiết kiệm hơn khi gửi hành lý từ 6 giờ đến 24 giờ.",
                        DiscountType = "FIXED",
                        DiscountValue = 25000m,
                        MaxDiscountAmount = 25000m,
                        MinOrderAmount = 60000m,
                        UsageLimit = 500,
                        UsedCount = 8,
                        StartDate = now.AddMonths(-1),
                        EndDate = now.AddYears(1),
                        IsActive = true,
                        CreatedAt = now,
                        UpdatedAt = now
                    }
                };

                db.Vouchers.AddRange(seeds);
                await db.SaveChangesAsync();
            }
        }
        catch
        {
            // Ignore if concurrent initialization occurs
        }
    }
}

