namespace Infrastructure.Services;

using Application.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PayOS;
using PayOS.Models.V2.PaymentRequests;
using PayOS.Models.Webhooks;

public class PayOSService : IPaymentGatewayService
{
    private readonly PayOSClient _client;
    private readonly ILogger<PayOSService> _logger;
    private readonly string _returnUrl;
    private readonly string _cancelUrl;

    public PayOSService(IConfiguration configuration, ILogger<PayOSService> logger)
    {
        _logger = logger;

        var section = configuration.GetSection("PayOS");
        var clientId = section["ClientId"] ?? "";
        var apiKey = section["ApiKey"] ?? "";
        var checksumKey = section["ChecksumKey"] ?? "";

        _returnUrl = section["ReturnUrl"] ?? "http://localhost:5173/payment/callback";
        _cancelUrl = section["CancelUrl"] ?? "http://localhost:5173/payment/cancel";

        _client = new PayOSClient(clientId, apiKey, checksumKey);
        _logger.LogInformation("PayOS client initialized with ClientId: {ClientId}", clientId[..8] + "...");
    }

    public async Task<(string CheckoutUrl, string PaymentLinkId)> CreatePaymentLinkAsync(
        long orderCode, int amount, string description, string returnUrl, string cancelUrl)
    {
        try
        {
            var request = new CreatePaymentLinkRequest
            {
                OrderCode = orderCode,
                Amount = amount,
                Description = description.Length > 25 ? description[..25] : description, // PayOS giới hạn 25 ký tự
                ReturnUrl = string.IsNullOrEmpty(returnUrl) ? _returnUrl : returnUrl,
                CancelUrl = string.IsNullOrEmpty(cancelUrl) ? _cancelUrl : cancelUrl
            };

            var result = await _client.PaymentRequests.CreateAsync(request);

            _logger.LogInformation("PayOS payment link created: OrderCode={OrderCode}, CheckoutUrl={Url}",
                orderCode, result.CheckoutUrl);

            return (result.CheckoutUrl, result.PaymentLinkId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create PayOS payment link for OrderCode={OrderCode}", orderCode);
            throw;
        }
    }

    public async Task<(long OrderCode, bool IsSuccess)> VerifyWebhookAsync(object webhookBody)
    {
        try
        {
            if (webhookBody is not Webhook webhook)
            {
                _logger.LogWarning("Invalid webhook body type: {Type}", webhookBody.GetType().Name);
                return (0, false);
            }

            var data = await _client.Webhooks.VerifyAsync(webhook);

            var isSuccess = data.Code == "00";
            _logger.LogInformation("PayOS webhook verified: OrderCode={OrderCode}, Code={Code}, Success={Success}",
                data.OrderCode, data.Code, isSuccess);

            return (data.OrderCode, isSuccess);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to verify PayOS webhook");
            return (0, false);
        }
    }

    public async Task<object?> GetPaymentLinkInfoAsync(long orderCode)
    {
        try
        {
            return await _client.PaymentRequests.GetAsync(orderCode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get PayOS payment info for OrderCode={OrderCode}", orderCode);
            return null;
        }
    }

    public async Task<bool> CancelPaymentLinkAsync(long orderCode, string? reason = null)
    {
        try
        {
            await _client.PaymentRequests.CancelAsync(orderCode, reason);
            _logger.LogInformation("PayOS payment link cancelled: OrderCode={OrderCode}", orderCode);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to cancel PayOS payment link: OrderCode={OrderCode}", orderCode);
            return false;
        }
    }
}
