namespace Application.Interfaces;

using Application.DTOs.Common;
using Application.DTOs.Kiosk;

public interface IKioskService
{
    Task<ApiResponse<KioskCheckInResponse>> CheckInAsync(KioskCheckInRequest request);
    Task<ApiResponse<KioskCheckOutResponse>> CheckOutAsync(KioskCheckOutRequest request);
    Task<ApiResponse<bool>> ManualConfirmAsync(KioskManualConfirmRequest request);
    Task<ApiResponse<KioskStationStatusResponse>> GetStationStatusAsync(Guid stationId);
}
