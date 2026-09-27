namespace Application.Interfaces;

using Domain.Entities;
using Domain.Enums;

public interface IPricingService
{
    Task<decimal> CalculateBookingPriceAsync(Guid stationId, LockerSize size, DateTime startAt, DateTime endAt);
    Task<decimal> CalculateExtensionPriceAsync(Booking booking, DateTime newEndAt);
    decimal CalculateOverdueFee(Booking booking, PricingPolicy policy);
    decimal CalculateRefundAmount(Booking booking);
    Task<PricingPolicy?> GetActivePolicyAsync(Guid stationId, LockerSize size);
}
