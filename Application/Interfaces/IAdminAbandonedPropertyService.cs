namespace Application.Interfaces;

using Application.DTOs.Admin;
using Application.DTOs.Common;

public interface IAdminAbandonedPropertyService
{
    Task<ApiResponse<List<AbandonedPropertyRecordDto>>> GetAbandonedPropertiesAsync(string? status);
    Task<ApiResponse<List<OverdueBookingCandidateDto>>> GetOverdueCandidatesAsync();
    Task<ApiResponse<AbandonedPropertyRecordDto>> CreateRecordAsync(CreateAbandonedPropertyRequest request, Guid staffWitnessId);
    Task<ApiResponse<bool>> ApproveRecordAsync(Guid recordId, ApproveAbandonedPropertyRequest request, Guid adminApprovalId);
    Task<ApiResponse<bool>> RejectRecordAsync(Guid recordId, RejectAbandonedPropertyRequest request, Guid adminApprovalId);
    Task<ApiResponse<bool>> ResolveRecordAsync(Guid recordId, string disposalAction, string? notes, Guid staffOrAdminId);
    Task<ApiResponse<bool>> ResolveRecordDetailAsync(Guid recordId, ResolveAbandonedPropertyDetailRequest request, Guid staffOrAdminId);
}
