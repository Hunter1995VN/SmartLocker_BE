namespace Application.DTOs.Auth;

public class NotificationPreferenceDto
{
    public bool EnableEmail { get; set; } = true;
    public bool EnableSms { get; set; } = false;
    public bool EnableWebPush { get; set; } = true;
    public bool BookingConfirmAlert { get; set; } = true;
    public bool CheckInReminderAlert { get; set; } = true;
    public bool ExpiryWarningAlert { get; set; } = true;
    public bool OverduePenaltyAlert { get; set; } = true;
    public bool PromotionalAlert { get; set; } = false;
    public string PreferredLanguage { get; set; } = "vi";
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
