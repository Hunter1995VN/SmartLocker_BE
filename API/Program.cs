using API.Middleware;
using Application.Interfaces;
using Infrastructure.Data;
using Infrastructure.Services;
using Infrastructure.BackgroundJobs;
using Microsoft.EntityFrameworkCore;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

// === Services ===

// Controllers + JSON enum serialization
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
        options.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    });

// CORS
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

// Swagger
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "SmartLocker API", Version = "v1" });
});

// Database
builder.Services.AddDbContext<SmartLockerDbContext>(options =>
    options.UseSqlServer(
        builder.Configuration.GetConnectionString("DefaultConnection"),
        sqlOptions => sqlOptions.EnableRetryOnFailure(3)
    ));
builder.Services.AddScoped<ISmartLockerDbContext>(sp =>
    sp.GetRequiredService<SmartLockerDbContext>());

// Application Services
builder.Services.AddScoped<IPricingService, PricingService>();
builder.Services.AddScoped<IPaymentGatewayService, PayOSService>();
builder.Services.AddScoped<IBookingService, BookingService>();
builder.Services.AddScoped<IPaymentService, PaymentService>();

// Background Jobs
builder.Services.AddHostedService<BookingExpirationJob>();
builder.Services.AddHostedService<NoShowJob>();
builder.Services.AddHostedService<OverdueCheckJob>();

var app = builder.Build();

// === Middleware Pipeline ===

// Global exception handler
app.UseMiddleware<ExceptionHandlingMiddleware>();

// Swagger (always on for capstone project)
app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "SmartLocker API v1");
    c.RoutePrefix = "swagger";
});

app.UseHttpsRedirection();
app.UseCors("AllowAll");
app.UseAuthorization();
app.MapControllers();

app.Run();
