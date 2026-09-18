namespace Application.DTOs;

/// <summary>
/// Kết quả trả về thống nhất từ các handler - giống Result pattern.
/// Tránh throw exception giúp dễ handle lỗi phía controller.
/// </summary>
public class Result<T>
{
    public bool IsSuccess { get; init; }
    public T? Data { get; init; }
    public string? ErrorMessage { get; init; }
    public string? ErrorCode { get; init; }
    public Dictionary<string, object>? Metadata { get; init; }

    public static Result<T> Success(T data, Dictionary<string, object>? meta = null) =>
        new() { IsSuccess = true, Data = data, Metadata = meta };

    public static Result<T> Failure(string code, string message) =>
        new() { IsSuccess = false, ErrorCode = code, ErrorMessage = message };
}
