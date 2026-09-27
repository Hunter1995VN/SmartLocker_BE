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

    Task<ApiResponse<List<LockerGridItemDto>>> GetStationLockersGridAsync(Guid stationId);
    Task<ApiResponse<bool>> UpdateLockerStatusAsync(Guid lockerId, UpdateLockerStatusRequest request, Guid adminUserId);
    Task<ApiResponse<bool>> CreateMaintenanceTicketAsync(CreateMaintenanceTicketRequest request, Guid reportedByUserId);
}
