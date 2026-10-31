namespace Infrastructure.Services;

using Application.Interfaces;
using Domain.Entities;
using Domain.Enums;
using Microsoft.EntityFrameworkCore;
using System;

public class PricingService : IPricingService
{
    private readonly ISmartLockerDbContext _context;

    public PricingService(ISmartLockerDbContext context)
    {
        _context = context;
    }

    public async Task<decimal> CalculateBookingPriceAsync(Guid stationId, LockerSize size, DateTime startAt, DateTime endAt)
    {
        var policy = await GetActivePolicyAsync(stationId, size);
        if (policy == null) throw new Exception("No active pricing policy found.");

        double totalHours = (endAt - startAt).TotalHours;
        if (totalHours <= 0) return 0;
        
        int blocks = (int)Math.Ceiling(totalHours / policy.BlockHours);
        return blocks * policy.PricePerBlock;
    }

    public async Task<decimal> CalculateExtensionPriceAsync(Booking booking, DateTime newEndAt)
    {
        var policy = booking.PricingPolicy ?? await GetActivePolicyAsync(booking.StationId, booking.Size);
        if (policy == null) throw new Exception("No active pricing policy found.");

        double extensionHours = (newEndAt - booking.EndAt).TotalHours;
        if (extensionHours <= 0) return 0;

        int blocks = (int)Math.Ceiling(extensionHours / policy.BlockHours);
        return blocks * policy.PricePerBlock;
    }

    public decimal CalculateOverdueFee(Booking booking, PricingPolicy policy)
    {
        var endTime = booking.CheckedOutAt ?? DateTime.UtcNow;
        var overdueMinutes = (endTime - booking.EndAt).TotalMinutes - policy.GracePeriodMinutes;
        if (overdueMinutes <= 0) return 0;

        int overdueHours = (int)Math.Ceiling(overdueMinutes / 60.0);
        return overdueHours * policy.OverdueFeePerHour;
    }

    public decimal CalculateRefundAmount(Booking booking)
    {
        if (booking.PricingPolicy == null) return 0;

        var policy = booking.PricingPolicy;
        var now = DateTime.UtcNow;
        var holdDurationMinutes = policy.NoShowWindowMinutes > 0 ? (int)policy.NoShowWindowMinutes : 30;
        var confirmedAt = booking.Payments?.FirstOrDefault(p => p.Kind == Domain.Enums.PaymentKind.BASE && p.Status == Domain.Enums.PaymentStatus.PAID)?.PaidAt ?? booking.CreatedAt;
        var elapsedMinutes = (now - confirmedAt).TotalMinutes;

        decimal rate = 0;

        // 1. Trường hợp hủy trong thời gian giữ chỗ chờ nhận tủ:
        if (elapsedMinutes >= 0 && elapsedMinutes <= holdDurationMinutes)
        {
            var earlyCutoffMinutes = policy.RefundCutoffHours > 0 && policy.RefundCutoffHours < holdDurationMinutes
                ? policy.RefundCutoffHours
                : (short)(holdDurationMinutes / 2);

            if (elapsedMinutes <= earlyCutoffMinutes)
            {
                rate = policy.RefundRateEarly;
            }
            else
            {
                rate = policy.RefundRateLate;
            }
        }
        else
        {
            // 2. Dự phòng cho đơn đặt trước xa trong tương lai:
            var timeUntilStart = (booking.StartAt - now).TotalHours;
            if (timeUntilStart > policy.RefundCutoffHours)
            {
                rate = policy.RefundRateEarly;
            }
            else if (timeUntilStart > 0)
            {
                rate = policy.RefundRateLate;
            }
        }

        return booking.BaseAmount * rate;
    }

    public async Task<PricingPolicy?> GetActivePolicyAsync(Guid stationId, LockerSize size)
    {
        return await _context.PricingPolicies
            .Where(p => p.StationId == stationId && p.Size == size && p.EffectiveTo == null)
            .OrderByDescending(p => p.EffectiveFrom)
            .FirstOrDefaultAsync();
    }
}
