using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using SmartLocker.Application.Services;
using SmartLocker.Infrastructure.Persistence;
using SmartLocker.Infrastructure.Services;
using SmartInfrastructure;

var builder = WebApplication.CreateBuilder(args);

// ===================================================================================
// 1. ĐĂNG KÝ SERVICES
// ===================================================================================
//
// ⚠️ BẢO MẬT: Các giá trị nhạy cảm (DB password, JWT Secret, SMTP password, Google ClientId)
//    nên được cấu hình qua Environment Variables hoặc User Secrets, KHÔNG commit lên Git.
//    Xem file appsettings.Example.json để biết danh sách các biến cần thiết.
//
//    Cách set biến môi trường trên Windows (PowerShell):
//    $env:ConnectionStrings__Default="Server=...;Password=...;"
//    $env:JwtSettings__SecretKey="your-secret-key"
//    $env:SmtpSettings__Password="your-app-password"
//    $env:GoogleAuth__ClientId="your-client-id.apps.googleusercontent.com"

builder.Services.AddControllers();

// Connection String từ appsettings.json
var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? "Server=localhost;Database=db65218;Trusted_Connection=True;TrustServerCertificate=True;";

// Đăng ký Application + Infrastructure services (sẽ tự inject DbContext, JWT, Email, OTP, Repos)
builder.Services.AddInfrastructure(builder.Configuration);

// Đăng ký AuthService cho Controller
builder.Services.AddScoped<AuthService>();

// CORS - cho phép Frontend React (port 5173/5174/5175) gọi API
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFrontend", policy =>
    {
        policy.SetIsOriginAllowed(_ => true)  // Cho phép tất cả origin (chỉ dùng cho dev)
              .AllowAnyMethod()
              .AllowAnyHeader()
              .AllowCredentials();
    });
});

// JWT Authentication Middleware
var jwtSettings = builder.Configuration.GetSection("JwtSettings");
var secretKey = jwtSettings["SecretKey"] ?? "SmartLocker.Super.Secret.Key.2025.MinLength32Chars";

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtSettings["Issuer"] ?? "SmartLocker",
            ValidAudience = jwtSettings["Audience"] ?? "SmartLocker.Clients",
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secretKey)),
            ClockSkew = TimeSpan.FromSeconds(30)
        };
    });

builder.Services.AddAuthorization();

// Swagger UI - có sẵn Bearer Token Authorization
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "SmartLocker Auth API",
        Version = "v1",
        Description = "API xác thực người dùng SmartLocker - .NET 8 Clean Architecture"
    });

    // Thêm nhập Bearer Token
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "Bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Nhập JWT token của bạn vào đây"
    });
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" }
            },
            Array.Empty<string>()
        }
    });
});

// ===================================================================================
// 2. BUILD APP
// ===================================================================================
var app = builder.Build();

// Tự động chạy migrations / tạo schema khi dev (sẽ tạo bảng nếu chưa có)
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "SmartLocker Auth v1");
        c.RoutePrefix = "swagger";
    });

    // Tạo database nếu chưa tồn tại (chỉ dành cho dev/demo)
    using var scope = app.Services.CreateScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<SmartLockerDbContext>();
    try
    {
        dbContext.Database.EnsureCreated();
        Console.WriteLine("✅ Database đã sẵn sàng");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"⚠️ Lỗi kết nối DB: {ex.Message}");
        Console.WriteLine("ℹ️ Chạy file do_an.sql trên SQL Server trước khi khởi động.");
    }
}

// ===================================================================================
// 3. MIDDLEWARE PIPELINE
// ===================================================================================

app.UseCors("AllowFrontend");

// Fix Cross-Origin-Opener-Policy để Google OAuth popup hoạt động
app.Use(async (context, next) =>
{
    context.Response.Headers["Cross-Origin-Opener-Policy"] = "same-origin-allow-popups";
    await next();
});

app.UseAuthentication();   // Xác thực JWT
app.UseAuthorization();    // Phân quyền

app.MapControllers();

// Không redirect HTTPS trong dev (để tránh lỗi certificate)
// app.UseHttpsRedirection();

// Endpoint health check đơn giản
app.MapGet("/", () => Results.Ok(new
{
    app = "SmartLocker Auth API",
    version = "1.0.0",
    status = "running",
    swagger = "/swagger"
}));

app.MapGet("/api/health", () => Results.Ok(new { status = "healthy", timestamp = DateTime.UtcNow }));

app.Run();
