namespace Application.Interfaces;

using Application.DTOs.Admin;
using Application.DTOs.Common;

public interface IAdminIncidentService
{
    Task<ApiResponse<List<IncidentDto>>> GetIncidentsAsync(string? status, string? severity);
    Task<ApiResponse<IncidentDto>> CreateIncidentAsync(CreateIncidentRequest request, Guid reportedByUserId);
    Task<ApiResponse<bool>> RemoteUnlockEmergencyAsync(Guid incidentId, RemoteUnlockRequest request, Guid adminOrStaffUserId, string? userEmail, string? userRole);
    Task<ApiResponse<bool>> DirectRemoteUnlockLockerAsync(Guid lockerId, RemoteUnlockRequest request, Guid adminOrStaffUserId, string? userEmail, string? userRole);
}
