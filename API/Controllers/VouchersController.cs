namespace API.Controllers;

using System.Security.Claims;
using Application.DTOs.Promotion;
using Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

/// <summary>
/// API tra cứu & kiểm tra áp dụng mã ưu đãi Voucher cho khách hàng thuê tủ (Traveler Checkout).
/// </summary>
[ApiController]
[Route("api/vouchers")]
public class VouchersController : ControllerBase
{
    private readonly IPromotionVoucherService _promotionService;

    public VouchersController(IPromotionVoucherService promotionService)
    {
        _promotionService = promotionService;
    }

    private Guid? TryGetUserId()
    {
        var uid = User.FindFirstValue("uid")
            ?? User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? User.FindFirstValue("sub");
        return Guid.TryParse(uid, out var id) ? id : null;
    }

    /// <summary>
    /// GET /api/vouchers/available — Lấy danh sách mã ưu đãi khả dụng kèm tính toán mức giảm ước tính theo đơn hiện tại
    /// </summary>
    [HttpGet("available")]
    [AllowAnonymous]
    public async Task<IActionResult> GetAvailableVouchers(
        [FromQuery] Guid? stationId = null,
        [FromQuery] string? size = null,
        [FromQuery] decimal orderAmount = 0m)
    {
        var result = await _promotionService.GetAvailableVouchersForBookingAsync(
            TryGetUserId(),
            stationId,
            size,
            orderAmount);
        return Ok(result);
    }

    /// <summary>
    /// POST /api/vouchers/validate — Kiểm tra điều kiện & tính tiền chiết khấu chính xác cho mã Voucher khách nhập
    /// </summary>
    [HttpPost("validate")]
    [AllowAnonymous]
    public async Task<IActionResult> ValidateVoucher([FromBody] ValidateVoucherRequest request)
    {
        var result = await _promotionService.ValidateVoucherForUserAsync(TryGetUserId(), request);
        if (!result.Success) return BadRequest(result);
        return Ok(result);
    }
}
