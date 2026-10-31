namespace Infrastructure.Services;

using Application.DTOs.Admin;
using Application.DTOs.Common;
using Application.Interfaces;
using Domain.Entities;
using Domain.Enums;
using Domain.Interfaces;
using Microsoft.EntityFrameworkCore;

public class AdminUserService : IAdminUserService
{
    private readonly ISmartLockerDbContext _db;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IAuditLogService _auditLog;

    public AdminUserService(ISmartLockerDbContext db, IPasswordHasher passwordHasher, IAuditLogService auditLog)
    {
        _db = db;
        _passwordHasher = passwordHasher;
        _auditLog = auditLog;
    }

    private async Task<(string? Email, string Role)> GetActorInfoAsync(Guid adminUserId)
    {
        var actor = await _db.Users.AsNoTracking()
            .Where(u => u.Id == adminUserId)
            .Select(u => new { u.Email, u.Role })
            .FirstOrDefaultAsync();

        return (actor?.Email, actor?.Role.ToString() ?? "ADMIN");
    }

    private async Task EnrichInternalActivityStatsAsync(List<UserAdminListItemDto> items)
    {
        var internalIds = items
            .Where(x => x.Role == "ADMIN" || x.Role == "STAFF")
            .Select(x => x.Id)
            .Distinct()
            .ToList();

        if (internalIds.Count == 0)
            return;

        var activityStats = await _db.AuditLogs.AsNoTracking()
            .Where(a => a.UserId.HasValue && internalIds.Contains(a.UserId.Value))
            .GroupBy(a => a.UserId!.Value)
            .Select(g => new
            {
                UserId = g.Key,
                ActionsCount = g.Count(),
                LastLoginAt = g
                    .Where(x => x.Action == "ADMIN_LOGIN" || x.Action == "LOGIN")
                    .Max(x => (DateTime?)x.CreatedAt) ?? g.Max(x => (DateTime?)x.CreatedAt)
            })
            .ToListAsync();

        var statsDict = activityStats.ToDictionary(x => x.UserId);
        foreach (var item in items)
        {
            if (statsDict.TryGetValue(item.Id, out var stat))
            {
                item.ActionsCount = stat.ActionsCount;
                item.LastLoginAt = stat.LastLoginAt;
            }
        }
    }

    public async Task<ApiResponse<List<UserAdminListItemDto>>> GetUsersAsync(
        string? search,
        string? role,
        string? status,
        int page,
        int pageSize,
        bool? hasDebt = null)
    {
        var query = _db.Users.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var q = search.Trim();
            query = query.Where(u => u.FullName.Contains(q) || u.Email.Contains(q) || u.Phone.Contains(q));
        }

        if (!string.IsNullOrWhiteSpace(role))
        {
            if (role.Equals("INTERNAL", StringComparison.OrdinalIgnoreCase))
            {
                query = query.Where(u => u.Role == UserRole.ADMIN || u.Role == UserRole.STAFF);
            }
            else if (Enum.TryParse<UserRole>(role, true, out var roleEnum))
            {
                query = query.Where(u => u.Role == roleEnum);
            }
        }

        if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<UserStatus>(status, true, out var statusEnum))
        {
            query = query.Where(u => u.Status == statusEnum);
        }

        if (hasDebt == true)
        {
            query = query.Where(u => u.OverdueDebt > 0);
        }
        else if (hasDebt == false)
        {
            query = query.Where(u => u.OverdueDebt <= 0);
        }

        var safePage = Math.Max(1, page);
        var safePageSize = Math.Clamp(pageSize, 1, 200);

        var users = await query
            .OrderByDescending(u => u.OverdueDebt)
            .ThenByDescending(u => u.CreatedAt)
            .Skip((safePage - 1) * safePageSize)
            .Take(safePageSize)
            .Select(u => new UserAdminListItemDto
            {
                Id = u.Id,
                FullName = u.FullName,
                Email = u.Email,
                Phone = u.Phone,
                Role = u.Role.ToString(),
                Status = u.Status.ToString(),
                OverdueDebt = u.OverdueDebt,
                IsBookingBlocked = u.Role == UserRole.TRAVELER && u.OverdueDebt > 0,
                AvatarUrl = u.AvatarUrl,
                CreatedAt = u.CreatedAt,
                UpdatedAt = u.UpdatedAt,
                DeletedAt = u.DeletedAt
            })
            .ToListAsync();

        await EnrichInternalActivityStatsAsync(users);

        return ApiResponse<List<UserAdminListItemDto>>.SuccessResponse(users);
    }

    /// <summary>
    /// UC-A05: Danh sách chuyên biệt cho Tài khoản Nội bộ (ADMIN & STAFF) kèm thống kê phiên đăng nhập và thao tác
    /// </summary>
    public async Task<ApiResponse<List<UserAdminListItemDto>>> GetInternalUsersAsync(
        string? search,
        string? role,
        string? status)
    {
        var query = _db.Users.AsNoTracking()
            .Where(u => u.Role == UserRole.ADMIN || u.Role == UserRole.STAFF);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var q = search.Trim();
            query = query.Where(u => u.FullName.Contains(q) || u.Email.Contains(q) || u.Phone.Contains(q));
        }

        if (!string.IsNullOrWhiteSpace(role) &&
            !role.Equals("ALL", StringComparison.OrdinalIgnoreCase) &&
            Enum.TryParse<UserRole>(role, true, out var roleEnum) &&
            (roleEnum == UserRole.ADMIN || roleEnum == UserRole.STAFF))
        {
            query = query.Where(u => u.Role == roleEnum);
        }

        if (!string.IsNullOrWhiteSpace(status) &&
            !status.Equals("ALL", StringComparison.OrdinalIgnoreCase) &&
            Enum.TryParse<UserStatus>(status, true, out var statusEnum))
        {
            query = query.Where(u => u.Status == statusEnum);
        }

        var internalUsers = await query
            .OrderBy(u => u.Role == UserRole.ADMIN ? 0 : 1)
            .ThenBy(u => u.Status == UserStatus.ACTIVE ? 0 : u.Status == UserStatus.SUSPENDED ? 1 : 2)
            .ThenByDescending(u => u.CreatedAt)
            .Select(u => new UserAdminListItemDto
            {
                Id = u.Id,
                FullName = u.FullName,
                Email = u.Email,
                Phone = u.Phone,
                Role = u.Role.ToString(),
                Status = u.Status.ToString(),
                OverdueDebt = u.OverdueDebt,
                IsBookingBlocked = false,
                AvatarUrl = u.AvatarUrl,
                CreatedAt = u.CreatedAt,
                UpdatedAt = u.UpdatedAt,
                DeletedAt = u.DeletedAt
            })
            .ToListAsync();

        await EnrichInternalActivityStatsAsync(internalUsers);

        return ApiResponse<List<UserAdminListItemDto>>.SuccessResponse(internalUsers);
    }

    /// <summary>
    /// UC-A05: Tổng hợp thống kê phân quyền RBAC, Ma trận quyền hạn Admin / Staff / Traveler & Nhật ký phân quyền gần đây
    /// </summary>
    public async Task<ApiResponse<RbacSummaryResponseDto>> GetRbacSummaryAsync()
    {
        var allUsers = await _db.Users.AsNoTracking()
            .Select(u => new { u.Id, u.FullName, u.Email, u.Role, u.Status })
            .ToListAsync();

        var admins = allUsers.Where(u => u.Role == UserRole.ADMIN).ToList();
        var staff = allUsers.Where(u => u.Role == UserRole.STAFF).ToList();
        var travelers = allUsers.Where(u => u.Role == UserRole.TRAVELER).ToList();
        var internals = allUsers.Where(u => u.Role == UserRole.ADMIN || u.Role == UserRole.STAFF).ToList();

        var rbacActions = new[]
        {
            "ADMIN_LOGIN",
            "CREATE_STAFF",
            "UPDATE_INTERNAL_USER",
            "UPDATE_USER_ROLE",
            "RESET_INTERNAL_PASSWORD",
            "UPDATE_USER_STATUS",
            "DEACTIVATE_INTERNAL_USER",
            "BATCH_SUSPEND_DEBTORS"
        };

        var recentLogsRaw = await _db.AuditLogs.AsNoTracking()
            .Where(a => rbacActions.Contains(a.Action) || a.TargetEntity == "User")
            .OrderByDescending(a => a.CreatedAt)
            .Take(25)
            .ToListAsync();

        var userDict = allUsers.ToDictionary(u => u.Id);
        var recentLogs = recentLogsRaw.Select(l =>
        {
            var actor = l.UserId.HasValue && userDict.TryGetValue(l.UserId.Value, out var u) ? u : null;
            return new AuditLogItemDto
            {
                Id = l.Id,
                UserId = l.UserId,
                UserName = actor?.FullName ?? l.UserEmail ?? "Hệ thống",
                UserEmail = !string.IsNullOrWhiteSpace(l.UserEmail) ? l.UserEmail : (actor?.Email ?? "system@smartlocker.vn"),
                UserRole = !string.IsNullOrWhiteSpace(l.UserRole) ? l.UserRole : (actor?.Role.ToString() ?? "ADMIN"),
                Action = l.Action,
                TargetEntity = l.TargetEntity,
                TargetId = l.TargetId,
                Details = l.Details,
                IpAddress = l.IpAddress,
                CreatedAt = l.CreatedAt
            };
        }).ToList();

        var summary = new RbacSummaryResponseDto
        {
            TotalInternalAccounts = internals.Count,
            TotalAdmins = admins.Count,
            ActiveAdmins = admins.Count(a => a.Status == UserStatus.ACTIVE),
            TotalStaff = staff.Count,
            ActiveStaff = staff.Count(s => s.Status == UserStatus.ACTIVE),
            SuspendedInternalAccounts = internals.Count(i => i.Status == UserStatus.SUSPENDED),
            DeactivatedInternalAccounts = internals.Count(i => i.Status == UserStatus.DELETED),
            TotalTravelers = travelers.Count,
            Roles = new List<RbacRoleOverviewDto>
            {
                new()
                {
                    Role = "ADMIN",
                    DisplayName = "Quản trị viên cấp cao (Super Admin)",
                    Subtitle = "Toàn quyền quản trị hệ thống, tài chính & phân quyền nhân sự",
                    Description = "Kiểm soát toàn bộ cấu hình trạm, chính sách giá & hoàn tiền, phê duyệt 4 mắt đối với tài sản bỏ quên >72h, đối soát tài chính PayOS và quản lý vòng đời tài khoản nội bộ.",
                    TotalUsers = admins.Count,
                    ActiveUsers = admins.Count(a => a.Status == UserStatus.ACTIVE),
                    SuspendedUsers = admins.Count(a => a.Status == UserStatus.SUSPENDED),
                    KeyPermissions = new List<string>
                    {
                        "Tạo mới, sửa hồ sơ, cấp lại mật khẩu, khóa/vô hiệu hóa & phân quyền tài khoản nội bộ (UC-A05)",
                        "Thêm/sửa/xóa trạm tủ & cấu hình bảng giá, phí phạt quá hạn, chính sách hoàn tiền (UC-A03, UC-A04)",
                        "Phê duyệt quy trình 4 mắt (4-Eyes Approval) thanh lý / xử lý tài sản quá hạn 72h (UC-A07)",
                        "Chặn/mở chặn tài khoản khách hàng nợ xấu & điều chỉnh công nợ (UC-A06)",
                        "Mở khóa khẩn cấp từ xa, đối soát dòng tiền PayOS & kiểm tra Audit Logs toàn cục (UC-S05, UC-A12, UC-A08)"
                    },
                    Restrictions = new List<string>
                    {
                        "Không được tự khóa, tự xóa hoặc tự hạ quyền tài khoản đang đăng nhập của chính mình",
                        "Không được vô hiệu hóa hoặc hạ quyền Quản trị viên đang hoạt động cuối cùng của hệ thống"
                    }
                },
                new()
                {
                    Role = "STAFF",
                    DisplayName = "Nhân viên vận hành trạm (Field Staff)",
                    Subtitle = "Giám sát vận hành hiện trường, cứu hộ sự cố & lập biên bản",
                    Description = "Thực hiện nghiệp vụ vận hành hàng ngày tại trạm tủ: theo dõi trạng thái IoT, chuyển ô tủ sang bảo trì, tiếp nhận sự cố, niêm phong & bàn giao hành lý thất lạc dưới sự giám sát của Admin.",
                    TotalUsers = staff.Count,
                    ActiveUsers = staff.Count(s => s.Status == UserStatus.ACTIVE),
                    SuspendedUsers = staff.Count(s => s.Status == UserStatus.SUSPENDED),
                    KeyPermissions = new List<string>
                    {
                        "Xem Dashboard vận hành, sơ đồ lưới ô tủ & tín hiệu cảm biến IoT thời gian thực (UC-A02, UC-S01)",
                        "Đổi trạng thái ô tủ sang Bảo trì (MAINTENANCE) & tạo phiếu bảo trì kỹ thuật (UC-S02)",
                        "Tiếp nhận xử lý sự cố hiện trường & hỗ trợ mở khóa cứu hộ có ghi vết Audit (UC-S04, UC-S05)",
                        "Lập biên bản niêm phong đồ quá hạn 72h & bàn giao tài sản thất lạc cho khách (UC-S10, UC-S11)",
                        "Tra cứu danh sách trạm, người dùng (chế độ chỉ đọc) & nhật ký vận hành"
                    },
                    Restrictions = new List<string>
                    {
                        "CẤM tạo/sửa/khóa tài khoản nội bộ hoặc thay đổi vai trò phân quyền RBAC (UC-A05)",
                        "CẤM thêm/xóa trạm tủ hoặc thay đổi cấu hình giá thuê & chính sách phạt/hoàn tiền (UC-A03, UC-A04)",
                        "CẤM tự phê duyệt thanh lý tài sản bỏ quên 72h (bắt buộc nguyên tắc 4 mắt bởi ADMIN - UC-A07)",
                        "CẤM xóa nợ quá hạn hoặc chặn hàng loạt tài khoản khách hàng (UC-A06)"
                    }
                },
                new()
                {
                    Role = "TRAVELER",
                    DisplayName = "Khách hàng thuê tủ (Traveler)",
                    Subtitle = "Sử dụng dịch vụ đặt chỗ, thanh toán & mở tủ thông minh",
                    Description = "Người dùng cuối trên Web/Mobile App và Kiosk: tìm kiếm trạm, đặt thuê ngăn tủ, thanh toán VietQR PayOS, lưu vé mở khóa ngoại tuyến HMAC và tự quản lý hồ sơ cá nhân.",
                    TotalUsers = travelers.Count,
                    ActiveUsers = travelers.Count(t => t.Status == UserStatus.ACTIVE),
                    SuspendedUsers = travelers.Count(t => t.Status == UserStatus.SUSPENDED),
                    KeyPermissions = new List<string>
                    {
                        "Tìm trạm, đặt tủ, gia hạn, hủy đơn & thanh toán trực tuyến PayOS",
                        "Nhận mã PIN & vé mở khóa ngoại tuyến HMAC-SHA256 (JWS) để mở tủ tại Kiosk",
                        "Quản lý hồ sơ cá nhân, cấu hình thông báo & tra cứu dư nợ quá hạn"
                    },
                    Restrictions = new List<string>
                    {
                        "Bị chặn hoàn toàn quyền đăng nhập vào cổng quản trị Admin Portal (/api/admin/*)",
                        "Tự động bị tạm ngưng đặt đơn mới nếu phát sinh dư nợ quá hạn chưa thanh toán (BR-P08)"
                    }
                }
            },
            PermissionMatrix = BuildPermissionMatrix(),
            RecentRbacLogs = recentLogs
        };

        return ApiResponse<RbacSummaryResponseDto>.SuccessResponse(summary);
    }

    private static List<RbacPermissionItemDto> BuildPermissionMatrix() => new()
    {
        new()
        {
            Code = "PORTAL_DASHBOARD_VIEW",
            Module = "Dashboard & Telemetry",
            Name = "Xem Dashboard vận hành & cảnh báo IoT",
            Description = "Truy cập trang tổng quan KPIs, biểu đồ doanh thu, tỷ lệ lấp đầy trạm và trạng thái kết nối thiết bị IoT.",
            AdminAllowed = true,
            StaffAllowed = true,
            TravelerAllowed = false,
            StaffScopeNote = "Cho phép xem toàn bộ chỉ số vận hành",
            RiskLevel = "LOW"
        },
        new()
        {
            Code = "STATION_CRUD",
            Module = "Trạm & Hạ tầng (UC-A03)",
            Name = "Thêm mới, cập nhật & xóa trạm tủ thông minh",
            Description = "Khởi tạo trạm mới, chỉnh sửa tọa độ/địa chỉ trạm, cấu hình danh mục ngăn tủ vật lý hoặc ngừng hoạt động trạm.",
            AdminAllowed = true,
            StaffAllowed = false,
            TravelerAllowed = false,
            StaffScopeNote = "Chỉ xem thông tin trạm (Read-only)",
            RiskLevel = "HIGH"
        },
        new()
        {
            Code = "PRICING_REFUND_POLICY",
            Module = "Giá cước & Chính sách (UC-A04)",
            Name = "Cấu hình giá thuê, phí phạt quá hạn & hoàn tiền",
            Description = "Thay đổi đơn giá theo kích cỡ ngăn tủ (S/M/L), mức phạt quá hạn theo giờ và tỷ lệ hoàn tiền khi hủy đơn.",
            AdminAllowed = true,
            StaffAllowed = false,
            TravelerAllowed = false,
            StaffScopeNote = "Chỉ xem bảng giá hiện hành",
            RiskLevel = "HIGH"
        },
        new()
        {
            Code = "LOCKER_STATUS_MAINTENANCE",
            Module = "Lưới Ô tủ & Bảo trì (UC-S01/S02)",
            Name = "Điều khiển trạng thái bảo trì ô tủ & lập phiếu kỹ thuật",
            Description = "Theo dõi sơ đồ ô tủ trực quan, chuyển ngăn tủ sang chế độ MAINTENANCE/AVAILABLE và tạo Maintenance Ticket.",
            AdminAllowed = true,
            StaffAllowed = true,
            TravelerAllowed = false,
            StaffScopeNote = "Toàn quyền thao tác bảo trì hiện trường",
            RiskLevel = "MEDIUM"
        },
        new()
        {
            Code = "INCIDENT_TRIAGE_HANDLE",
            Module = "Sự cố & Cứu hộ (UC-S04)",
            Name = "Tiếp nhận, xử lý & cập nhật biên bản sự cố an ninh",
            Description = "Tạo phiếu sự cố kẹt khóa/cảm biến bất thường, cập nhật tiến độ xử lý (OPEN -> INVESTIGATING -> RESOLVED).",
            AdminAllowed = true,
            StaffAllowed = true,
            TravelerAllowed = false,
            StaffScopeNote = "Thực thi trực tiếp tại trạm",
            RiskLevel = "MEDIUM"
        },
        new()
        {
            Code = "EMERGENCY_REMOTE_UNLOCK",
            Module = "Sự cố & Cứu hộ (UC-S05)",
            Name = "Kích hoạt lệnh Mở khóa khẩn cấp từ xa qua MQTT",
            Description = "Phát lệnh mở chốt điện từ khẩn cấp từ xa kèm lý do bắt buộc và ghi nhận tức thì vào nhật ký an ninh.",
            AdminAllowed = true,
            StaffAllowed = true,
            TravelerAllowed = false,
            StaffScopeNote = "Bắt buộc nhập lý do & lưu vết Audit Log",
            RiskLevel = "CRITICAL"
        },
        new()
        {
            Code = "ABANDONED_SEAL_HANDOVER",
            Module = "Tài sản bỏ quên 72h (UC-S10/S11)",
            Name = "Lập biên bản niêm phong 72h & bàn giao đồ thất lạc",
            Description = "Mở tủ quá hạn 72h có nhân chứng, chụp ảnh niêm phong tài sản lưu kho và lập biên bản trả đồ cho chủ sở hữu.",
            AdminAllowed = true,
            StaffAllowed = true,
            TravelerAllowed = false,
            StaffScopeNote = "Đóng vai trò Nhân viên chứng kiến (Witness)",
            RiskLevel = "HIGH"
        },
        new()
        {
            Code = "ABANDONED_4EYES_APPROVAL",
            Module = "Tài sản bỏ quên 72h (UC-A07)",
            Name = "Phê duyệt 4 mắt (4-Eyes Principle) xử lý/thanh lý tài sản",
            Description = "Xét duyệt độc lập hoặc từ chối hồ sơ thanh lý tài sản vô thừa nhận (người duyệt bắt buộc khác nhân viên lập biên bản).",
            AdminAllowed = true,
            StaffAllowed = false,
            TravelerAllowed = false,
            StaffScopeNote = "Bị khóa — Yêu cầu quyền ADMIN độc lập",
            RiskLevel = "CRITICAL"
        },
        new()
        {
            Code = "CUSTOMER_DEBT_BLOCKING",
            Module = "Khách hàng & Công nợ (UC-A06)",
            Name = "Điều chỉnh dư nợ quá hạn & chặn tài khoản nợ xấu",
            Description = "Ghi nhận tất toán công nợ, khóa cá nhân hoặc khóa hàng loạt các tài khoản khách hàng có dư nợ quá hạn.",
            AdminAllowed = true,
            StaffAllowed = false,
            TravelerAllowed = false,
            StaffScopeNote = "Chỉ được tra cứu danh sách & trạng thái nợ",
            RiskLevel = "HIGH"
        },
        new()
        {
            Code = "INTERNAL_RBAC_MANAGEMENT",
            Module = "Nhân sự & Phân quyền (UC-A05)",
            Name = "Quản lý vòng đời tài khoản nội bộ & cấp quyền RBAC",
            Description = "Tạo tài khoản Admin/Staff mới, sửa thông tin nhân sự, đặt lại mật khẩu, khóa/vô hiệu hóa và luân chuyển vai trò.",
            AdminAllowed = true,
            StaffAllowed = false,
            TravelerAllowed = false,
            StaffScopeNote = "Chỉ xem danh bạ nội bộ (Read-only)",
            RiskLevel = "CRITICAL"
        },
        new()
        {
            Code = "PAYOS_RECONCILIATION",
            Module = "Tài chính & Đối soát (UC-A12)",
            Name = "Đối soát giao dịch tài chính PayOS & đồng bộ thanh toán",
            Description = "Kiểm tra bảng đối soát doanh thu, phát hiện lệch trạng thái cổng thanh toán PayOS và kích hoạt đồng bộ giao dịch.",
            AdminAllowed = true,
            StaffAllowed = true,
            TravelerAllowed = false,
            StaffScopeNote = "Hỗ trợ tra cứu & đồng bộ đơn lỗi cho khách",
            RiskLevel = "HIGH"
        },
        new()
        {
            Code = "MANUAL_EXCEPTION_REFUND",
            Module = "Tài chính & Hoàn tiền Ngoại lệ (UC-A11)",
            Name = "Phê duyệt lệnh hoàn tiền thủ công cho các trường hợp ngoại lệ",
            Description = "Thẩm định độc lập, phê duyệt hoặc từ chối lệnh hoàn tiền ngoại lệ (kẹt khóa IoT, mất điện trạm, thu trùng PayOS) và xác nhận giải ngân Napas247.",
            AdminAllowed = true,
            StaffAllowed = false,
            TravelerAllowed = false,
            StaffScopeNote = "Chỉ được lập đề xuất kèm biên bản hiện trường — CẤM tự duyệt chi",
            RiskLevel = "CRITICAL"
        },
        new()
        {
            Code = "PROMOTION_VOUCHER_BUDGET",
            Module = "Khuyến mại & Ngân sách (UC-A09)",
            Name = "Thiết lập chiến dịch khuyến mại, mã Voucher & trần ngân sách chiết khấu",
            Description = "Khởi tạo chiến dịch, phát hành mã giảm giá (%/VND), phân bổ ngân sách chiết khấu 3 tầng và cấu hình tự động ngắt khi chạm hạn mức.",
            AdminAllowed = true,
            StaffAllowed = false,
            TravelerAllowed = false,
            StaffScopeNote = "Chỉ xem thông tin chiến dịch & giả lập mã (Read-only)",
            RiskLevel = "HIGH"
        },
        new()
        {
            Code = "SECURITY_AUDIT_LOGS",
            Module = "An ninh & Truy vết (UC-A08)",
            Name = "Tra cứu nhật ký truy vết an ninh toàn hệ thống (Audit Logs)",
            Description = "Truy vấn lịch sử bất biến mọi thao tác nhạy cảm theo tác nhân, vai trò, loại hành động và mốc thời gian.",
            AdminAllowed = true,
            StaffAllowed = true,
            TravelerAllowed = false,
            StaffScopeNote = "Chỉ đọc — Không ai có quyền sửa/xóa log",
            RiskLevel = "MEDIUM"
        }
    };

    public async Task<ApiResponse<UserAdminListItemDto>> CreateStaffAsync(CreateStaffRequest request, Guid adminUserId)
    {
        if (string.IsNullOrWhiteSpace(request.FullName))
            return ApiResponse<UserAdminListItemDto>.ErrorResponse("Họ và tên nhân sự không được để trống");

        if (string.IsNullOrWhiteSpace(request.Email) || !request.Email.Contains('@'))
            return ApiResponse<UserAdminListItemDto>.ErrorResponse("Địa chỉ Email không hợp lệ");

        if (string.IsNullOrWhiteSpace(request.Phone))
            return ApiResponse<UserAdminListItemDto>.ErrorResponse("Số điện thoại không được để trống");

        if (string.IsNullOrWhiteSpace(request.Password) || request.Password.Length < 6)
            return ApiResponse<UserAdminListItemDto>.ErrorResponse("Mật khẩu khởi tạo phải có tối thiểu 6 ký tự");

        var cleanEmail = request.Email.Trim().ToLowerInvariant();
        var cleanPhone = request.Phone.Trim();
        var cleanName = request.FullName.Trim();

        var existingUser = await _db.Users.FirstOrDefaultAsync(
            u => u.Email.ToLower() == cleanEmail || u.Phone == cleanPhone);
        if (existingUser != null)
            return ApiResponse<UserAdminListItemDto>.ErrorResponse("Email hoặc Số điện thoại đã được đăng ký trong hệ thống");

        var targetRole = (request.Role == UserRole.ADMIN || request.Role == UserRole.STAFF)
            ? request.Role
            : UserRole.STAFF;

        var hashedPassword = _passwordHasher.Hash(request.Password);
        var now = DateTime.UtcNow;

        var staff = new User
        {
            Id = Guid.NewGuid(),
            FullName = cleanName,
            Email = cleanEmail,
            Phone = cleanPhone,
            PasswordHash = hashedPassword,
            Role = targetRole,
            Status = UserStatus.ACTIVE,
            CreatedAt = now,
            UpdatedAt = now
        };

        _db.Users.Add(staff);
        await _db.SaveChangesAsync();

        var actor = await GetActorInfoAsync(adminUserId);
        await _auditLog.LogAsync(
            adminUserId,
            actor.Email,
            actor.Role,
            "CREATE_STAFF",
            "User",
            staff.Id.ToString(),
            $"Tạo tài khoản nội bộ mới: {staff.FullName} ({staff.Email}) — Vai trò: {staff.Role}");

        var dto = new UserAdminListItemDto
        {
            Id = staff.Id,
            FullName = staff.FullName,
            Email = staff.Email,
            Phone = staff.Phone,
            Role = staff.Role.ToString(),
            Status = staff.Status.ToString(),
            OverdueDebt = 0,
            IsBookingBlocked = false,
            CreatedAt = staff.CreatedAt,
            UpdatedAt = staff.UpdatedAt,
            ActionsCount = 0
        };

        return ApiResponse<UserAdminListItemDto>.SuccessResponse(dto, $"Đã tạo tài khoản nội bộ {staff.FullName} ({staff.Role}) thành công");
    }

    /// <summary>
    /// UC-A05: Cập nhật thông tin hồ sơ tài khoản nội bộ (Họ tên, Email, SĐT, và tùy chọn Vai trò)
    /// </summary>
    public async Task<ApiResponse<UserAdminListItemDto>> UpdateInternalUserAsync(
        Guid userId,
        UpdateInternalUserRequest request,
        Guid adminUserId)
    {
        if (string.IsNullOrWhiteSpace(request.FullName))
            return ApiResponse<UserAdminListItemDto>.ErrorResponse("Họ và tên không được để trống");

        if (string.IsNullOrWhiteSpace(request.Email) || !request.Email.Contains('@'))
            return ApiResponse<UserAdminListItemDto>.ErrorResponse("Địa chỉ Email không hợp lệ");

        if (string.IsNullOrWhiteSpace(request.Phone))
            return ApiResponse<UserAdminListItemDto>.ErrorResponse("Số điện thoại không được để trống");

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user == null)
            return ApiResponse<UserAdminListItemDto>.ErrorResponse("Không tìm thấy tài khoản người dùng");

        var cleanEmail = request.Email.Trim().ToLowerInvariant();
        var cleanPhone = request.Phone.Trim();
        var cleanName = request.FullName.Trim();

        var duplicate = await _db.Users.AnyAsync(u =>
            u.Id != userId && (u.Email.ToLower() == cleanEmail || u.Phone == cleanPhone));
        if (duplicate)
            return ApiResponse<UserAdminListItemDto>.ErrorResponse("Email hoặc Số điện thoại này đã thuộc về một tài khoản khác");

        var changes = new List<string>();
        if (user.FullName != cleanName)
        {
            changes.Add($"Họ tên: '{user.FullName}' -> '{cleanName}'");
            user.FullName = cleanName;
        }
        if (!string.Equals(user.Email, cleanEmail, StringComparison.OrdinalIgnoreCase))
        {
            changes.Add($"Email: '{user.Email}' -> '{cleanEmail}'");
            user.Email = cleanEmail;
        }
        if (user.Phone != cleanPhone)
        {
            changes.Add($"SĐT: '{user.Phone}' -> '{cleanPhone}'");
            user.Phone = cleanPhone;
        }

        if (request.Role.HasValue && request.Role.Value != user.Role)
        {
            if (userId == adminUserId && request.Role.Value != UserRole.ADMIN)
            {
                return ApiResponse<UserAdminListItemDto>.ErrorResponse(
                    "Bảo vệ an toàn RBAC: Không thể tự hạ quyền tài khoản Quản trị viên đang đăng nhập của chính bạn.");
            }

            if (user.Role == UserRole.ADMIN && user.Status == UserStatus.ACTIVE && request.Role.Value != UserRole.ADMIN)
            {
                var activeAdminsCount = await _db.Users.CountAsync(
                    u => u.Role == UserRole.ADMIN && u.Status == UserStatus.ACTIVE);
                if (activeAdminsCount <= 1)
                {
                    return ApiResponse<UserAdminListItemDto>.ErrorResponse(
                        "Bảo vệ an toàn RBAC: Không thể hạ quyền Quản trị viên đang hoạt động cuối cùng của hệ thống.");
                }
            }

            changes.Add($"Vai trò: {user.Role} -> {request.Role.Value}");
            user.Role = request.Role.Value;
        }

        user.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        var actor = await GetActorInfoAsync(adminUserId);
        var changeSummary = changes.Count > 0 ? string.Join("; ", changes) : "Cập nhật hồ sơ nội bộ";
        if (!string.IsNullOrWhiteSpace(request.Reason))
        {
            changeSummary += $". Ghi chú: {request.Reason.Trim()}";
        }

        await _auditLog.LogAsync(
            adminUserId,
            actor.Email,
            actor.Role,
            "UPDATE_INTERNAL_USER",
            "User",
            user.Id.ToString(),
            $"Cập nhật tài khoản {user.Email}: {changeSummary}");

        var dto = new UserAdminListItemDto
        {
            Id = user.Id,
            FullName = user.FullName,
            Email = user.Email,
            Phone = user.Phone,
            Role = user.Role.ToString(),
            Status = user.Status.ToString(),
            OverdueDebt = user.OverdueDebt,
            IsBookingBlocked = user.Role == UserRole.TRAVELER && user.OverdueDebt > 0,
            AvatarUrl = user.AvatarUrl,
            CreatedAt = user.CreatedAt,
            UpdatedAt = user.UpdatedAt,
            DeletedAt = user.DeletedAt
        };
        await EnrichInternalActivityStatsAsync(new List<UserAdminListItemDto> { dto });

        return ApiResponse<UserAdminListItemDto>.SuccessResponse(dto, "Đã cập nhật thông tin tài khoản nội bộ thành công");
    }

    /// <summary>
    /// UC-A05: Cấp lại / Đặt lại mật khẩu cho tài khoản nội bộ (Admin / Staff)
    /// </summary>
    public async Task<ApiResponse<bool>> ResetInternalPasswordAsync(
        Guid userId,
        ResetInternalPasswordRequest request,
        Guid adminUserId)
    {
        if (string.IsNullOrWhiteSpace(request.NewPassword) || request.NewPassword.Length < 6)
            return ApiResponse<bool>.ErrorResponse("Mật khẩu mới phải có tối thiểu 6 ký tự");

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user == null)
            return ApiResponse<bool>.ErrorResponse("Không tìm thấy tài khoản người dùng");

        user.PasswordHash = _passwordHasher.Hash(request.NewPassword);
        user.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        var actor = await GetActorInfoAsync(adminUserId);
        var reasonText = string.IsNullOrWhiteSpace(request.Reason)
            ? "Quản trị viên cấp lại mật khẩu truy cập"
            : request.Reason.Trim();

        await _auditLog.LogAsync(
            adminUserId,
            actor.Email,
            actor.Role,
            "RESET_INTERNAL_PASSWORD",
            "User",
            user.Id.ToString(),
            $"Đặt lại mật khẩu cho tài khoản {user.Email} ({user.Role}). Lý do: {reasonText}");

        return ApiResponse<bool>.SuccessResponse(true, $"Đã đặt lại mật khẩu mới cho {user.FullName} ({user.Email}) thành công");
    }

    /// <summary>
    /// UC-A05: Vô hiệu hóa / Thu hồi vĩnh viễn tài khoản nội bộ (Soft Delete -> DELETED)
    /// </summary>
    public async Task<ApiResponse<bool>> DeactivateInternalUserAsync(Guid userId, string? reason, Guid adminUserId)
    {
        if (userId == adminUserId)
        {
            return ApiResponse<bool>.ErrorResponse(
                "Bảo vệ an toàn RBAC: Không thể tự vô hiệu hóa tài khoản đang đăng nhập của chính bạn.");
        }

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user == null)
            return ApiResponse<bool>.ErrorResponse("Không tìm thấy tài khoản người dùng");

        if (user.Role == UserRole.ADMIN && user.Status == UserStatus.ACTIVE)
        {
            var activeAdminsCount = await _db.Users.CountAsync(
                u => u.Role == UserRole.ADMIN && u.Status == UserStatus.ACTIVE);
            if (activeAdminsCount <= 1)
            {
                return ApiResponse<bool>.ErrorResponse(
                    "Bảo vệ an toàn RBAC: Không thể vô hiệu hóa Quản trị viên đang hoạt động cuối cùng của hệ thống.");
            }
        }

        var oldStatus = user.Status;
        var now = DateTime.UtcNow;
        user.Status = UserStatus.DELETED;
        user.DeletedAt = now;
        user.UpdatedAt = now;

        await _db.SaveChangesAsync();

        var actor = await GetActorInfoAsync(adminUserId);
        var cleanReason = string.IsNullOrWhiteSpace(reason)
            ? "Thu hồi quyền truy cập nhân sự nghỉ việc / ngừng công tác"
            : reason.Trim();

        await _auditLog.LogAsync(
            adminUserId,
            actor.Email,
            actor.Role,
            "DEACTIVATE_INTERNAL_USER",
            "User",
            user.Id.ToString(),
            $"Vô hiệu hóa tài khoản nội bộ {user.Email} ({user.Role}) từ {oldStatus} -> DELETED. Lý do: {cleanReason}");

        return ApiResponse<bool>.SuccessResponse(true, $"Đã vô hiệu hóa tài khoản {user.FullName} ({user.Email})");
    }

    public async Task<ApiResponse<bool>> UpdateUserStatusAsync(Guid userId, UpdateUserStatusRequest request, Guid adminUserId)
    {
        if (userId == adminUserId && request.Status != UserStatus.ACTIVE)
        {
            return ApiResponse<bool>.ErrorResponse(
                "Bảo vệ an toàn RBAC: Không thể tự khóa hoặc vô hiệu hóa tài khoản đang đăng nhập của chính bạn.");
        }

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user == null)
            return ApiResponse<bool>.ErrorResponse("Không tìm thấy người dùng");

        if (user.Role == UserRole.ADMIN && user.Status == UserStatus.ACTIVE && request.Status != UserStatus.ACTIVE)
        {
            var activeAdminsCount = await _db.Users.CountAsync(
                u => u.Role == UserRole.ADMIN && u.Status == UserStatus.ACTIVE);
            if (activeAdminsCount <= 1)
            {
                return ApiResponse<bool>.ErrorResponse(
                    "Bảo vệ an toàn RBAC: Không thể khóa Quản trị viên đang hoạt động cuối cùng của hệ thống.");
            }
        }

        var oldStatus = user.Status;
        user.Status = request.Status;
        user.UpdatedAt = DateTime.UtcNow;

        if (request.Status == UserStatus.DELETED)
        {
            user.DeletedAt = DateTime.UtcNow;
        }
        else if (request.Status == UserStatus.ACTIVE)
        {
            user.DeletedAt = null;
        }

        await _db.SaveChangesAsync();

        var actor = await GetActorInfoAsync(adminUserId);
        await _auditLog.LogAsync(
            adminUserId,
            actor.Email,
            actor.Role,
            "UPDATE_USER_STATUS",
            "User",
            user.Id.ToString(),
            $"Cập nhật trạng thái tài khoản {user.Email} ({user.Role}) từ {oldStatus} sang {request.Status}. Lý do: {request.Reason}");

        return ApiResponse<bool>.SuccessResponse(true, $"Đã chuyển trạng thái tài khoản thành {request.Status}");
    }

    public async Task<ApiResponse<bool>> UpdateUserRoleAsync(Guid userId, UpdateUserRoleRequest request, Guid adminUserId)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user == null)
            return ApiResponse<bool>.ErrorResponse("Không tìm thấy người dùng");

        if (userId == adminUserId && request.Role != UserRole.ADMIN)
        {
            return ApiResponse<bool>.ErrorResponse(
                "Bảo vệ an toàn RBAC: Không thể tự hạ quyền tài khoản Quản trị viên đang đăng nhập của chính bạn.");
        }

        if (user.Role == UserRole.ADMIN && user.Status == UserStatus.ACTIVE && request.Role != UserRole.ADMIN)
        {
            var activeAdminsCount = await _db.Users.CountAsync(
                u => u.Role == UserRole.ADMIN && u.Status == UserStatus.ACTIVE);
            if (activeAdminsCount <= 1)
            {
                return ApiResponse<bool>.ErrorResponse(
                    "Bảo vệ an toàn RBAC: Không thể hạ quyền Quản trị viên đang hoạt động cuối cùng của hệ thống.");
            }
        }

        var oldRole = user.Role;
        user.Role = request.Role;
        user.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();

        var actor = await GetActorInfoAsync(adminUserId);
        var reasonSuffix = string.IsNullOrWhiteSpace(request.Reason) ? "" : $". Lý do: {request.Reason.Trim()}";
        await _auditLog.LogAsync(
            adminUserId,
            actor.Email,
            actor.Role,
            "UPDATE_USER_ROLE",
            "User",
            user.Id.ToString(),
            $"Thay đổi phân quyền RBAC của {user.Email} từ {oldRole} sang {request.Role}{reasonSuffix}");

        return ApiResponse<bool>.SuccessResponse(true, $"Đã cập nhật phân quyền thành {request.Role}");
    }

    /// <summary>
    /// UC-A06: Quản lý công nợ quá hạn & Tự động chặn/mở chặn tài khoản có nợ xấu
    /// </summary>
    public async Task<ApiResponse<UserAdminListItemDto>> UpdateUserDebtAsync(Guid userId, UpdateUserDebtRequest request, Guid adminUserId)
    {
        if (request.OverdueDebt < 0)
            return ApiResponse<UserAdminListItemDto>.ErrorResponse("Số tiền nợ quá hạn không được âm");

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user == null)
            return ApiResponse<UserAdminListItemDto>.ErrorResponse("Không tìm thấy người dùng");

        var oldDebt = user.OverdueDebt;
        var oldStatus = user.Status;
        user.OverdueDebt = request.OverdueDebt;

        if (request.OverdueDebt > 0 && request.AutoSuspendIfDebt && user.Role == UserRole.TRAVELER)
        {
            user.Status = UserStatus.SUSPENDED;
        }
        else if (request.OverdueDebt == 0 && user.Status == UserStatus.SUSPENDED && user.Role == UserRole.TRAVELER)
        {
            user.Status = UserStatus.ACTIVE;
        }

        user.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        var actor = await GetActorInfoAsync(adminUserId);
        await _auditLog.LogAsync(
            adminUserId,
            actor.Email,
            actor.Role,
            "UPDATE_USER_DEBT",
            "User",
            user.Id.ToString(),
            $"Cập nhật công nợ của {user.Email} từ {oldDebt:N0}đ sang {user.OverdueDebt:N0}đ (Trạng thái: {oldStatus} -> {user.Status}). Lý do: {request.Reason}"
        );

        var dto = new UserAdminListItemDto
        {
            Id = user.Id,
            FullName = user.FullName,
            Email = user.Email,
            Phone = user.Phone,
            Role = user.Role.ToString(),
            Status = user.Status.ToString(),
            OverdueDebt = user.OverdueDebt,
            IsBookingBlocked = user.Role == UserRole.TRAVELER && user.OverdueDebt > 0,
            AvatarUrl = user.AvatarUrl,
            CreatedAt = user.CreatedAt,
            UpdatedAt = user.UpdatedAt
        };

        return ApiResponse<UserAdminListItemDto>.SuccessResponse(dto, $"Đã cập nhật dư nợ quá hạn ({user.OverdueDebt:N0}đ) và trạng thái ({user.Status})");
    }

    /// <summary>
    /// UC-A06: Chặn hàng loạt tất cả tài khoản khách hàng đang có nợ xấu (OverdueDebt > 0)
    /// </summary>
    public async Task<ApiResponse<int>> BlockAllDebtorsAsync(string? reason, Guid adminUserId)
    {
        var debtors = await _db.Users
            .Where(u => u.Role == UserRole.TRAVELER && u.OverdueDebt > 0)
            .ToListAsync();

        if (debtors.Count == 0)
            return ApiResponse<int>.SuccessResponse(0, "Không có tài khoản khách hàng nào đang có nợ cước cần xử lý");

        var actor = await GetActorInfoAsync(adminUserId);
        var cleanReason = string.IsNullOrWhiteSpace(reason) ? "Tạm khóa quyền đặt tủ do có dư nợ cước quá hạn chưa thanh toán (BR-P08)" : reason.Trim();
        await _auditLog.LogAsync(
            adminUserId,
            actor.Email,
            actor.Role,
            "BATCH_BLOCK_BOOKING_DEBTORS",
            "User",
            $"Count:{debtors.Count}",
            $"Đã kích hoạt tạm khóa quyền đặt tủ cho {debtors.Count} khách hàng có nợ cước. Lý do: {cleanReason}"
        );

        return ApiResponse<int>.SuccessResponse(debtors.Count, $"Đã xác nhận tạm khóa quyền đặt tủ cho {debtors.Count} khách hàng nợ cước (tài khoản vẫn duy trì hoạt động để đăng nhập thanh toán).");
    }
}