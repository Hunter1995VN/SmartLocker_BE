using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Domain.Interfaces;
using Application.Interfaces;
using Infrastructure.Persistence;
using Infrastructure.Persistence.Repositories;
using Infrastructure.Services;

namespace SmartInfrastructure;

/// <summary>
/// Extension method đăng ký các service Infrastructure vào DI container.
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        // 1. Kết nối SQL Server — hỗ trợ cả DefaultConnection (Nhiệm) và Default (Quân)
        var connStr = config.GetConnectionString("DefaultConnection") 
            ?? config.GetConnectionString("Default")
            ?? "Server=localhost;Database=db65218;Trusted_Connection=True;TrustServerCertificate=True;";

        services.AddDbContext<SmartLockerDbContext>(opts =>
            opts.UseSqlServer(connStr));

        // 2. Bind options
        services.Configure<SmtpSettings>(config.GetSection("SmtpSettings"));
        services.Configure<JwtSettings>(config.GetSection("JwtSettings"));

        // 3. Đăng ký các service Auth (Quân)
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IOtpRepository, OtpRepository>();
        services.AddSingleton<IOtpGenerator, OtpGenerator>();
        services.AddSingleton<IPasswordHasher, BcryptPasswordHasher>();
        services.AddScoped<IOtpSender, EmailOtpSender>();
        services.AddSingleton<IJwtTokenService, JwtTokenService>();
        services.AddHttpClient<IGoogleAuthService, GoogleAuthService>();

        // 4. Đăng ký các service Admin (Phân hệ Quản trị Vận hành)
        services.AddScoped<IAuditLogService, AuditLogService>();
        services.AddScoped<IAdminDashboardService, AdminDashboardService>();
        services.AddScoped<IAdminStationLockerService, AdminStationLockerService>();
        services.AddScoped<IAdminUserService, AdminUserService>();
        services.AddScoped<IAdminIncidentService, AdminIncidentService>();
        services.AddScoped<IAdminAbandonedPropertyService, AdminAbandonedPropertyService>();

        return services;
    }
}
