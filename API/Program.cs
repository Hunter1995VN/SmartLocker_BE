using System.Text;
using Microsoft.AspNetCore.DataProtection;
using API.Middleware;
using Application.Interfaces;
using Infrastructure.Data;
using Infrastructure.Services;
using Infrastructure.BackgroundJobs;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using System.Text.Json.Serialization;

using SmartInfrastructure;
using Application.Services;

var builder = WebApplication.CreateBuilder(args);

var keyFolder = builder.Configuration["TicketProtection:KeyDirectory"]
    ?? Path.Combine(builder.Environment.ContentRootPath, "App_Data", "keys");
var protection = builder.Services.AddDataProtection().SetApplicationName("SmartLocker.AccessTickets")
    .PersistKeysToFileSystem(new DirectoryInfo(keyFolder));
var certificateThumbprint = builder.Configuration["TicketProtection:CertificateThumbprint"];
if (!string.IsNullOrWhiteSpace(certificateThumbprint)) protection.ProtectKeysWithCertificate(certificateThumbprint);
else if (OperatingSystem.IsWindows()) protection.ProtectKeysWithDpapi();
else throw new InvalidOperationException("Configure TicketProtection:CertificateThumbprint to encrypt the persisted ticket key ring.");
builder.Services.AddSingleton<IAccessPinProtector, API.Services.AccessPinProtector>();

// === Controllers + JSON enum serialization ===
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
        options.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    });

// === CORS (cho phép Frontend React + AllowCredentials cho Google OAuth) ===
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFrontend", policy =>
    {
        policy.SetIsOriginAllowed(_ => true)
              .AllowAnyMethod()
              .AllowAnyHeader()
              .AllowCredentials();
    });
});

// === Swagger với Bearer Token support ===
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "SmartLocker API",
        Version = "v1",
        Description = "SmartLocker Management System - .NET 8 Clean Architecture"
    });
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "Bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Nhập JWT token: Bearer {token}"
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

// === Database (hỗ trợ cả DefaultConnection và Default) ===
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? builder.Configuration.GetConnectionString("Default");

builder.Services.AddDbContext<SmartLockerDbContext>(options =>
    options.UseSqlServer(
        connectionString,
        sqlOptions => sqlOptions.EnableRetryOnFailure(3)
    ));
builder.Services.AddScoped<ISmartLockerDbContext>(sp =>
    sp.GetRequiredService<SmartLockerDbContext>());

// === Auth module của Quân (JWT, OTP, Google OAuth, Repositories) ===
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddScoped<AuthService>();

// === JWT Authentication (từ module Auth của Quân) ===
var jwtSettings = builder.Configuration.GetSection("JwtSettings");
var secretKey = string.IsNullOrWhiteSpace(jwtSettings["SecretKey"])
    ? "SmartLocker.Super.Secret.Key.2025.MinLength32Chars"
    : jwtSettings["SecretKey"]!;

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

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminOnly", policy => policy.RequireRole("ADMIN"));
    options.AddPolicy("StaffOrAdmin", policy => policy.RequireRole("ADMIN", "STAFF"));
});

// === Nhiệm's Services (Booking/Payment) ===
builder.Services.AddScoped<IPricingService, PricingService>();
builder.Services.AddScoped<IPaymentGatewayService, PayOSService>();
builder.Services.AddScoped<IBookingService, BookingService>();
builder.Services.AddScoped<IPaymentService, PaymentService>();

// === Background Jobs (Nhiệm) ===
if (builder.Configuration.GetValue("BackgroundJobs:Enabled", true))
{
    builder.Services.AddHostedService<BookingExpirationJob>();
    builder.Services.AddHostedService<NoShowJob>();
    builder.Services.AddHostedService<OverdueCheckJob>();
}

// === SignalR & Realtime Locker Notifications ===
builder.Services.AddSignalR();
builder.Services.AddScoped<ILockerNotificationService, API.Services.LockerNotificationService>();

// ============================================================
var app = builder.Build();

// === Middleware Pipeline ===

// Global exception handler (Nhiệm)
app.UseMiddleware<ExceptionHandlingMiddleware>();

// Swagger (luôn bật để dễ demo đồ án)
app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "SmartLocker API v1");
    c.RoutePrefix = "swagger";
});

// Fix Cross-Origin-Opener-Policy để Google OAuth popup hoạt động (Quân)
app.Use(async (context, next) =>
{
    context.Response.Headers["Cross-Origin-Opener-Policy"] = "same-origin-allow-popups";
    await next();
});

app.UseCors("AllowFrontend");
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapHub<API.Hubs.LockerHub>("/hubs/locker");

// Health check endpoint
app.MapGet("/api/health", () => Results.Ok(new { status = "healthy", timestamp = DateTime.UtcNow }));

// Redirect root to Swagger
app.MapGet("/", () => Results.Redirect("/swagger/index.html"));

app.Run();
