namespace Application.Interfaces;

using Application.DTOs.Admin;
using Application.DTOs.Common;

public interface IAdminStationLockerService
{
    Task<ApiResponse<List<StationAdminDetailDto>>> GetStationsAsync(string? search);
    Task<ApiResponse<StationAdminDetailDto>> GetStationByIdAsync(Guid id);
    Task<ApiResponse<StationAdminDetailDto>> CreateStationAsync(CreateStationRequest request, Guid adminUserId);
    Task<ApiResponse<StationAdminDetailDto>> UpdateStationAsync(Guid id, UpdateStationRequest request, Guid adminUserId);
    Task<ApiResponse<bool>> DeleteStationAsync(Guid id, Guid adminUserId);

    Task<ApiResponse<bool>> UpdatePricingPolicyAsync(Guid stationId, UpdatePricingPolicyRequest request, Guid adminUserId);

    Task<List<Application.DTOs.Kiosk.KioskSizeReservationDto>> GetStationReservationsAsync(Guid stationId);
    Task<ApiResponse<List<LockerGridItemDto>>> GetStationLockersGridAsync(Guid stationId);
    Task<ApiResponse<bool>> UpdateLockerStatusAsync(Guid lockerId, UpdateLockerStatusRequest request, Guid adminUserId);
    Task<ApiResponse<bool>> CreateMaintenanceTicketAsync(CreateMaintenanceTicketRequest request, Guid reportedByUserId);

    /// <summary>
    /// UC-S01: Lấy danh sách thiết bị IoT của toàn bộ các trạm kèm thông số Telemetry.
    /// </summary>
    Task<ApiResponse<List<IoTDeviceDto>>> GetIoTDevicesAsync();

    /// <summary>
    /// UC-S09: Khởi động lại vi điều khiển ESP32 từ xa qua lệnh MQTT Reboot.
    /// </summary>
    Task<ApiResponse<DeviceControlResultDto>> RebootDeviceAsync(Guid stationId, Guid adminUserId);

    /// <summary>
    /// UC-S07: Kích hoạt chu trình chẩn đoán tự động phần cứng (Self-Test Relay/Sensor).
    /// </summary>
    Task<ApiResponse<DeviceControlResultDto>> TriggerSelfTestAsync(Guid stationId, Guid adminUserId);

    /// <summary>
    /// UC-S12: Đổi hộc tủ dự phòng khi hộc tủ được gán bị kẹt cơ học.
    /// </summary>
    Task<ApiResponse<SwapSpareLockerResultDto>> SwapSpareLockerAsync(Guid lockerId, SwapSpareLockerRequest request, Guid adminUserId);

    /// <summary>
    /// UC-A11: Lấy cấu hình các ngưỡng an ninh và timeout cửa mở (Door Ajar) của trạm.
    /// </summary>
    Task<ApiResponse<StationSecurityThresholdsDto>> GetSecurityThresholdsAsync(Guid stationId);

    /// <summary>
    /// UC-A11: Cập nhật cấu hình các ngưỡng an ninh và timeout cửa mở (Door Ajar) của trạm.
    /// </summary>
    Task<ApiResponse<StationSecurityThresholdsDto>> UpdateSecurityThresholdsAsync(Guid stationId, UpdateSecurityThresholdsRequest request, Guid adminUserId);

    /// <summary>
    /// UC-A02: Cấu hình ánh xạ chân bo mạch điều khiển (GPIO Pin) cho ô tủ.
    /// </summary>
    Task<ApiResponse<bool>> UpdateLockerHardwareMappingAsync(Guid lockerId, UpdateLockerHardwareMappingRequest request, Guid adminUserId);

    /// <summary>
    /// UC-S03: Lấy danh sách trạng thái cảm biến cửa và Telemetry gần nhất của trạm.
    /// </summary>
    Task<ApiResponse<List<DoorTelemetryItemDto>>> GetDoorTelemetryAsync(Guid stationId);
}
