namespace Application.DTOs.Admin;

public record AdminLoginRequest(
    string Email,
    string Password
);

public record AdminLoginResponse(
    string Token,
    Guid UserId,
    string FullName,
    string Email,
    string Role,
    string? AvatarUrl
);
