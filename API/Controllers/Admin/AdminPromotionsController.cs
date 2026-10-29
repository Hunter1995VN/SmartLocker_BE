namespace API.Controllers.Admin;

using System.Security.Claims;
using Application.DTOs.Promotion;
using Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

/// <summary>
/// UC-A09: Thiết lập Chiến dịch Khuyến mại, Mã Voucher & Kiểm soát Ngân sách Chiết khấu.
/// </summary>
[ApiController]
[Route("api/admin/promotions")]
[Authorize(Roles = "ADMIN,STAFF")]
public class AdminPromotionsController : ControllerBase
{
    private readonly IPromotionVoucherService _promotionService;

    public AdminPromotionsController(IPromotionVoucherService promotionService)
    {
        _promotionService = promotionService;
    }

    private Guid GetActorId()
    {
        var uid = User.FindFirstValue("uid")
            ?? User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? User.FindFirstValue("sub");
        return Guid.TryParse(uid, out var id) ? id : Guid.Empty;
    }

    /// <summary>
    /// GET /api/admin/promotions/overview — Tổng quan ngân sách chiết khấu tháng, chiến dịch, kho mã Voucher & sổ cái giải ngân
    /// </summary>
    [HttpGet("overview")]
    public async Task<IActionResult> GetOverview()
    {
        var result = await _promotionService.GetAdminOverviewAsync();
        return Ok(result);
    }

    /// <summary>
    /// GET /api/admin/promotions/vouchers — Danh sách kho mã Voucher có bộ lọc tìm kiếm
    /// </summary>
    [HttpGet("vouchers")]
    public async Task<IActionResult> GetVouchers(
        [FromQuery] string? search = null,
        [FromQuery] string? campaignId = null,
        [FromQuery] string? status = null)
    {
        var result = await _promotionService.GetAdminVouchersAsync(search, campaignId, status);
        return Ok(result);
    }

    /// <summary>
    /// PUT /api/admin/promotions/budget-policy — Cập nhật trần ngân sách khuyến mại tháng & quy tắc an toàn tài chính
    /// </summary>
    [HttpPut("budget-policy")]
    [Authorize(Roles = "ADMIN")]
    public async Task<IActionResult> UpdateGlobalBudget([FromBody] UpdateGlobalPromotionBudgetRequest request)
    {
        var result = await _promotionService.UpdateGlobalBudgetPolicyAsync(request, GetActorId());
        if (!result.Success) return BadRequest(result);
        return Ok(result);
    }

    /// <summary>
    /// POST /api/admin/promotions/campaigns — Tạo mới chiến dịch khuyến mại & phân bổ ngân sách
    /// </summary>
    [HttpPost("campaigns")]
    [Authorize(Roles = "ADMIN")]
    public async Task<IActionResult> CreateCampaign([FromBody] UpsertPromotionCampaignRequest request)
    {
        var result = await _promotionService.CreateCampaignAsync(request, GetActorId());
        if (!result.Success) return BadRequest(result);
        return Ok(result);
    }

    /// <summary>
    /// PUT /api/admin/promotions/campaigns/{campaignId} — Chỉnh sửa chiến dịch khuyến mại & ngân sách phân bổ
    /// </summary>
    [HttpPut("campaigns/{campaignId}")]
    [Authorize(Roles = "ADMIN")]
    public async Task<IActionResult> UpdateCampaign(string campaignId, [FromBody] UpsertPromotionCampaignRequest request)
    {
        var result = await _promotionService.UpdateCampaignAsync(campaignId, request, GetActorId());
        if (!result.Success) return BadRequest(result);
        return Ok(result);
    }

    /// <summary>
    /// PATCH /api/admin/promotions/campaigns/{campaignId}/status — Tạm dừng / Kích hoạt lại hoặc Cấp thêm ngân sách (Top-up) cho chiến dịch
    /// </summary>
    [HttpPatch("campaigns/{campaignId}/status")]
    [Authorize(Roles = "ADMIN")]
    public async Task<IActionResult> UpdateCampaignStatus(string campaignId, [FromBody] UpdateCampaignStatusRequest request)
    {
        var result = await _promotionService.UpdateCampaignStatusOrTopUpAsync(campaignId, request, GetActorId());
        if (!result.Success) return BadRequest(result);
        return Ok(result);
    }

    /// <summary>
    /// POST /api/admin/promotions/vouchers — Phát hành mã Voucher mới gắn với Chiến dịch & Ngân sách
    /// </summary>
    [HttpPost("vouchers")]
    [Authorize(Roles = "ADMIN")]
    public async Task<IActionResult> CreateVoucher([FromBody] UpsertVoucherRequest request)
    {
        var result = await _promotionService.CreateVoucherAsync(request, GetActorId());
        if (!result.Success) return BadRequest(result);
        return Ok(result);
    }

    /// <summary>
    /// PUT /api/admin/promotions/vouchers/{voucherId} — Cập nhật thông số mã Voucher & hạn mức ngân sách riêng
    /// </summary>
    [HttpPut("vouchers/{voucherId:guid}")]
    [Authorize(Roles = "ADMIN")]
    public async Task<IActionResult> UpdateVoucher(Guid voucherId, [FromBody] UpsertVoucherRequest request)
    {
        var result = await _promotionService.UpdateVoucherAsync(voucherId, request, GetActorId());
        if (!result.Success) return BadRequest(result);
        return Ok(result);
    }

    /// <summary>
    /// PATCH /api/admin/promotions/vouchers/{voucherId}/toggle — Bật / Tắt nhanh mã Voucher
    /// </summary>
    [HttpPatch("vouchers/{voucherId:guid}/toggle")]
    [Authorize(Roles = "ADMIN")]
    public async Task<IActionResult> ToggleVoucher(Guid voucherId, [FromBody] ToggleVoucherStatusRequest request)
    {
        var result = await _promotionService.ToggleVoucherStatusAsync(voucherId, request, GetActorId());
        if (!result.Success) return BadRequest(result);
        return Ok(result);
    }

    /// <summary>
    /// DELETE /api/admin/promotions/vouchers/{voucherId} — Xóa hoặc thu hồi mã Voucher (bảo toàn đối soát nếu đã có lượt dùng)
    /// </summary>
    [HttpDelete("vouchers/{voucherId:guid}")]
    [Authorize(Roles = "ADMIN")]
    public async Task<IActionResult> DeleteVoucher(Guid voucherId, [FromQuery] string? reason = null)
    {
        var result = await _promotionService.DeleteOrDeactivateVoucherAsync(voucherId, reason, GetActorId());
        if (!result.Success) return BadRequest(result);
        return Ok(result);
    }

    /// <summary>
    /// POST /api/admin/promotions/simulate — Mô phỏng tính toán chiết khấu & tác động ngân sách trước khi phát hành
    /// </summary>
    [HttpPost("simulate")]
    public async Task<IActionResult> SimulateDiscount([FromBody] ValidateVoucherRequest request)
    {
        var result = await _promotionService.SimulateVoucherAsync(request);
        return Ok(result);
    }
}
