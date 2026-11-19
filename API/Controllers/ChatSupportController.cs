using Application.DTOs.Support;
using Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace API.Controllers;

[ApiController]
[Route("api/support/chat")]
[AllowAnonymous]
public class ChatSupportController : ControllerBase
{
    private readonly IChatSupportService _chatSupportService;
    private readonly ILogger<ChatSupportController> _logger;

    public ChatSupportController(IChatSupportService chatSupportService, ILogger<ChatSupportController> logger)
    {
        _chatSupportService = chatSupportService;
        _logger = logger;
    }

    /// <summary>
    /// Gửi câu hỏi đến Trợ lý AI SmartLocker để được tư vấn và giải đáp tức thì.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Chat([FromBody] ChatSupportRequest request, CancellationToken cancellationToken)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.Message))
        {
            return BadRequest(new { message = "Vui lòng nhập nội dung câu hỏi." });
        }

        if (request.Message.Length > 1000)
        {
            return BadRequest(new { message = "Nội dung câu hỏi quá dài (tối đa 1000 ký tự)." });
        }

        try
        {
            var response = await _chatSupportService.SendMessageAsync(request, cancellationToken);
            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lỗi xử lý Chat Support");
            return StatusCode(500, new { message = "Đã xảy ra lỗi khi kết nối với trợ lý AI. Vui lòng thử lại sau." });
        }
    }
}
