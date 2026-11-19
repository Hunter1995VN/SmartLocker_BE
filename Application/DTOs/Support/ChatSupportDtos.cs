namespace Application.DTOs.Support;

public class ChatMessageDto
{
    public string Role { get; set; } = "user"; // "user" or "model"
    public string Content { get; set; } = string.Empty;
}

public class ChatSupportRequest
{
    public string Message { get; set; } = string.Empty;
    public List<ChatMessageDto>? History { get; set; }
}

public class ChatSupportResponse
{
    public string Reply { get; set; } = string.Empty;
    public List<string>? SuggestedQuestions { get; set; }
}
