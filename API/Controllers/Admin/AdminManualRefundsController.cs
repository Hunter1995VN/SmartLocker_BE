namespace API.Controllers.Admin;

using System.Security.Claims;
using Application.DTOs.Refund;
using Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

/// <summary>
/// Quản lý & Phê duyệt Lệnh Hoàn tiền Thủ công cho các trường hợp ngoại lệ:
/// - STAFF & ADMIN: Xem danh sách, tra cứu giao dịch đủ điều kiện và lập phiếu đề xuất hoàn tiền ngoại lệ.
/// - Chỉ ADMIN: Thẩm định phê duyệt, từ chối hoặc xác nhận tất toán giải ngân chuyển khoản.
/// </summary>
[ApiController]
[Route("api/admin/manual-refunds")]
[Authorize(Roles = "ADMIN,STAFF")]
public class AdminManualRefundsController : ControllerBase
{
    private readonly IManualRefundService _manualRefundService;

    public AdminManualRefundsController(IManualRefundService manualRefundService)
    {
        _manualRefundService = manualRefundService;
    }

    private Guid GetUserId()
    {
        var uid = User.FindFirstValue("uid")
            ?? User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? User.FindFirstValue("sub");
        return Guid.TryParse(uid, out var userId) ? userId : Guid.Empty;
    }

    [HttpGet("overview")]
    public async Task<IActionResult> GetOverview(
        [FromQuery] string? status = null,
        [FromQuery] string? category = null,
        [FromQuery] string? search = null)
    {
        var result = await _manualRefundService.GetOverviewAsync(status, category, search);
        return Ok(result);
    }

    [HttpGet("candidates")]
    public async Task<IActionResult> GetRefundableCandidates([FromQuery] string? search = null)
    {
        var result = await _manualRefundService.GetRefundableCandidatesAsync(search);
        return Ok(result);
    }

    [HttpPost]
    [Authorize(Roles = "ADMIN,STAFF")]
    public async Task<IActionResult> CreateRequest([FromBody] CreateManualRefundRequest request)
    {
        var userId = GetUserId();
        if (userId == Guid.Empty)
            return Unauthorized(new { message = "Không xác định được danh tính người thực hiện." });

        var result = await _manualRefundService.CreateRequestAsync(request, userId);
        if (!result.Success)
            return BadRequest(result);
        return Ok(result);
    }

    [HttpPost("{refundId}/approve")]
    [Authorize(Roles = "ADMIN")]
    public async Task<IActionResult> ApproveRequest(
        string refundId,
        [FromBody] ApproveManualRefundRequest request)
    {
        var adminId = GetUserId();
        if (adminId == Guid.Empty)
            return Unauthorized(new { message = "Không xác định được danh tính Quản trị viên." });

        var result = await _manualRefundService.ApproveRequestAsync(refundId, request, adminId);
        if (!result.Success)
            return BadRequest(result);
        return Ok(result);
    }

    [HttpPost("{refundId}/reject")]
    [Authorize(Roles = "ADMIN")]
    public async Task<IActionResult> RejectRequest(
        string refundId,
        [FromBody] RejectManualRefundRequest request)
    {
        var adminId = GetUserId();
        if (adminId == Guid.Empty)
            return Unauthorized(new { message = "Không xác định được danh tính Quản trị viên." });

        var result = await _manualRefundService.RejectRequestAsync(refundId, request, adminId);
        if (!result.Success)
            return BadRequest(result);
        return Ok(result);
    }

    [HttpPost("{refundId}/disburse")]
    [Authorize(Roles = "ADMIN")]
    public async Task<IActionResult> ConfirmDisbursement(
        string refundId,
        [FromBody] ConfirmManualRefundDisbursementRequest request)
    {
        var adminId = GetUserId();
        if (adminId == Guid.Empty)
            return Unauthorized(new { message = "Không xác định được danh tính Quản trị viên." });

        var result = await _manualRefundService.ConfirmDisbursementAsync(refundId, request, adminId);
        if (!result.Success)
            return BadRequest(result);
        return Ok(result);
    }
}
