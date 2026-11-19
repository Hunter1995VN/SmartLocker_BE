using Application.DTOs.Support;

namespace Application.Interfaces;

public interface IChatSupportService
{
    Task<ChatSupportResponse> SendMessageAsync(ChatSupportRequest request, CancellationToken cancellationToken = default);
}
