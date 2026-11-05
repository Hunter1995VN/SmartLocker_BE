namespace Application.DTOs.Admin;

/// <summary>
/// UC-A11: Cấu hình các ngưỡng cảnh báo an ninh & thời gian timeout cửa mở
/// </summary>
public class StationSecurityThresholdsDto
{
    public Guid StationId { get; set; }
    public string StationName { get; set; } = string.Empty;

    /// <summary>
    /// Thời gian cho phép cửa mở trước khi cảnh báo an ninh (giây). Mặc định 60s.
    /// </summary>
    public int DoorAjarTimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// Số lần nhập sai mã PIN/OTP liên tiếp tối đa trước khi khóa tạm thời (mặc định 5).
    /// </summary>
    public int MaxConsecutiveFailures { get; set; } = 5;

    /// <summary>
    /// Độ nhạy phát hiện cạy phá/rung chấn bất thường: 1 (Thấp), 2 (Vừa), 3 (Cao).
    /// </summary>
    public int VibrationSensitivityLevel { get; set; } = 2;

    /// <summary>
    /// Tự động bật còi hú (Buzzer) tại trạm khi phát hiện cửa mở quá lâu hoặc cạy phá.
    /// </summary>
    public bool EnableBuzzerAlarm { get; set; } = true;

    /// <summary>
    /// Tự động tạo bản ghi Sự cố An ninh (Security Incident) lên Admin Portal.
    /// </summary>
    public bool AutoCreateSecurityIncident { get; set; } = true;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class UpdateSecurityThresholdsRequest
{
    public int DoorAjarTimeoutSeconds { get; set; } = 60;
    public int MaxConsecutiveFailures { get; set; } = 5;
    public int VibrationSensitivityLevel { get; set; } = 2;
    public bool EnableBuzzerAlarm { get; set; } = true;
    public bool AutoCreateSecurityIncident { get; set; } = true;
}
