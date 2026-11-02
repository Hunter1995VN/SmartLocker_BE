namespace Application.Interfaces;

using Application.DTOs.Admin;
using Application.DTOs.Common;

public interface IAdminRefundService
{
    Task<ApiResponse<PagedResult<AdminRefundDto>>> GetRefundRequestsAsync(string? status, int page = 1, int pageSize = 20);
    Task<ApiResponse<AdminRefundDto>> ApproveRefundAsync(Guid paymentId, Guid adminId, ProcessRefundRequest? request);
    Task<ApiResponse<AdminRefundDto>> RejectRefundAsync(Guid paymentId, Guid adminId, ProcessRefundRequest? request);
}

