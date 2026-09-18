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
        var now = DateTime.UtcNow;
        var overdueMinutes = (now - booking.EndAt).TotalMinutes - policy.GracePeriodMinutes;
        if (overdueMinutes <= 0) return 0;

        int overdueHours = (int)Math.Ceiling(overdueMinutes / 60.0);
        return overdueHours * policy.OverdueFeePerHour;
    }

    public decimal CalculateRefundAmount(Booking booking)
    {
        if (booking.PricingPolicy == null) return 0;

        var timeUntilStart = (booking.StartAt - DateTime.UtcNow).TotalHours;
        decimal rate = 0;

        if (timeUntilStart > booking.PricingPolicy.RefundCutoffHours)
        {
            rate = booking.PricingPolicy.RefundRateEarly;
        }
        else if (timeUntilStart > 0)
        {
            rate = booking.PricingPolicy.RefundRateLate;
        }

        return booking.BaseAmount * rate;
    }

    public async Task<PricingPolicy?> GetActivePolicyAsync(Guid stationId, LockerSize size)
    {
        return await _context.PricingPolicies
            .Where(p => p.StationId == stationId && p.Size == size && p.EffectiveTo == null)
            .FirstOrDefaultAsync();
    }
}
