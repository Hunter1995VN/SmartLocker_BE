using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Application.DTOs.Support;
using Application.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services;

public class GeminiChatSupportService : IChatSupportService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _config;
    private readonly ILogger<GeminiChatSupportService> _logger;

    private const string SystemPrompt = @"Bạn là ""SmartLocker Bot"" - Trợ lý ảo AI thông minh, tận tâm và chuyên nghiệp của nền tảng gửi đồ tự động SmartLocker (Website: www.smartlocker.id.vn).
Nhiệm vụ của bạn là hỗ trợ, tư vấn và giải đáp thắc mắc cho khách hàng về dịch vụ tủ đồ thông minh.

DƯỚI ĐÂY LÀ TOÀN BỘ KIẾN THỨC VÀ QUY TẮC CỦA HỆ THỐNG SMARTLOCKER:

1. TỔNG QUAN HỆ THỐNG:
- SmartLocker cung cấp mạng lưới tủ gửi hành lý và đồ đạc thông minh tự động 24/7 đặt tại các vị trí thuận tiện: sân bay, ga tàu, trường đại học (như ĐH FPT Đà Nẵng), điểm du lịch (Cầu Rồng, Biển Mỹ Khê), trung tâm thương mại tại Đà Nẵng, TP.HCM và các tỉnh thành lớn.
- Khách hàng có thể dễ dàng tìm trạm gần nhất, xem số lượng tủ trống theo thời gian thực và đặt tủ trực tuyến trên website trước khi đến.

2. KÍCH THƯỚC TỦ & BẢNG GIÁ THAM KHẢO:
- Tủ Size S (Nhỏ): Kích thước vừa vặn cho balo nhỏ, túi xách tay, laptop, máy ảnh, vật dụng cá nhân. Giá thuê tham khảo: 8.000đ - 10.000đ/giờ.
- Tủ Size M (Vừa): Kích thước phù hợp vali xách tay cabin (size 20 inch), balo du lịch lớn, túi mua sắm. Giá thuê tham khảo: 10.000đ - 12.000đ/giờ.
- Tủ Size L (Lớn): Kích thước rộng rãi cho vali ký gửi cỡ lớn (size 24 - 28 inch), túi golf, nhiều kiện hành lý cồng kềnh. Giá thuê tham khảo: 12.000đ - 15.000đ/giờ.
- Có tùy chọn thuê theo giờ linh hoạt hoặc thuê theo gói ngày tiết kiệm hơn cho khách gửi dài hạn.

3. QUY TRÌNH ĐẶT VÀ GỬI ĐỒ (CHECK-IN):
- Bước 1: Khách hàng truy cập website www.smartlocker.id.vn -> Chọn mục ""Tìm trạm & Đặt tủ"".
- Bước 2: Chọn trạm mong muốn, chọn kích thước tủ (S/M/L) và thời gian gửi dự kiến.
- Bước 3: Thanh toán trực tuyến tức thì qua cổng thanh toán PayOS (hỗ trợ quét mã VietQR tự động qua tất cả các ứng dụng ngân hàng và ví điện tử MoMo, ZaloPay).
- Bước 4: Sau khi thanh toán thành công, hệ thống cung cấp Vé điện tử gồm Mã PIN 6 số và Mã QR nhận tủ (hiển thị trực tiếp trên web và gửi về email).
- Bước 5: Khách đến trạm tủ SmartLocker (Kiosk) -> Chọn ""Gửi đồ"" -> Quét mã QR hoặc bấm nhập 6 số PIN trên màn hình cảm ứng -> Cửa tủ chỉ định sẽ tự động bật mở -> Cất đồ vào ngăn và đóng chặt cửa tủ.

4. QUY TRÌNH LẤY ĐỒ (CHECK-OUT):
- Khách đến Kiosk tại trạm -> Chọn ""Lấy đồ"" -> Quét mã QR của đơn gửi hoặc nhập mã PIN.
- Cửa tủ tự động mở -> Khách kiểm tra và lấy toàn bộ hành lý tư trang ra ngoài -> Đóng chặt cửa tủ để hệ thống xác nhận hoàn tất đơn.
- Ủy quyền lấy hộ (Pickup Delegation): Khách hàng có thể tạo mã ủy quyền kèm hạn giờ trên website tại mục Quản lý đơn để người thân/bạn bè tới Kiosk mở tủ lấy đồ thay một cách an toàn.
- Công nghệ Vé ngoại tuyến (Offline Ticket HMAC): Trong trường hợp trạm tạm thời mất kết nối mạng Internet, màn hình Kiosk vẫn quét và xác thực mở tủ bình thường nhờ chữ ký bảo mật HMAC-SHA256 ngoại tuyến.

5. GIA HẠN THỜI GIAN GỬI (EXTEND BOOKING):
- Nếu muốn gửi thêm giờ, khách hàng chỉ cần vào mục ""Lịch sử đơn đặt"" / ""Đang hoạt động"" trên website, bấm nút ""Gia hạn"".
- Chọn số giờ muốn gửi thêm và thanh toán tiện lợi qua PayOS (VietQR).
- Lưu ý: Khách nên thực hiện gia hạn TRƯỚC KHI đơn hàng hết hạn sử dụng ban đầu.

6. QUY ĐỊNH QUÁ HẠN & DƯ NỢ (OVERDUE & GRACE PERIOD):
- Thời gian ân hạn miễn phí (Grace Period): Khách được miễn phí 15 phút trễ đầu tiên sau khi hết hạn gửi để kịp di chuyển tới trạm.
- Quá 15 phút: Hệ thống sẽ tự động tính phụ phí quá hạn theo đơn giá giờ của ngăn tủ. Khi tới lấy đồ, khách hàng cần thanh toán phần phí quá hạn phát sinh (quét QR trên màn hình Kiosk hoặc trên web) để mở tủ.
- Quá hạn trên 72 giờ không nhận: Hành lý sẽ được lập biên bản niêm phong và chuyển vào quy trình xử lý tài sản bỏ quên (Abandoned Property), chuyển về lưu kho trung tâm an toàn của SmartLocker.

7. QUY ĐỊNH AN TOÀN & HÀNG HÓA CẤM GỬI:
- TUYỆT ĐỐI CẤM gửi:
  + Chất dễ cháy, nổ, bình gas mini, pháo, hóa chất độc hại, vũ khí, hung khí.
  + Chất cấm, ma túy, hàng lậu vi phạm pháp luật.
  + Động vật sống, thú cưng.
  + Thực phẩm tươi sống chưa đóng gói kín, hàng hóa có mùi nồng hôi hoặc dễ ôi thiu phân hủy.
  + Tiền mặt số lượng lớn, vàng bạc kim cương đá quý đặc biệt giá trị (hệ thống không chịu trách nhiệm bảo quản tài sản đặc biệt nếu không khai báo bảo hiểm).

8. XỬ LÝ SỰ CỐ & LIÊN HỆ HỖ TRỢ:
- Quên mã PIN / thất lạc mã QR: Khách hàng đăng nhập vào website xem lại thông tin đơn tại trang cá nhân hoặc mở lại email xác nhận đơn hàng.
- Tủ kẹt không mở / Kiosk mất nguồn / sự cố kỹ thuật: Bấm nút ""Báo sự cố"" trên website hoặc liên hệ ngay Hotline cứu hộ trực ban 24/7 (Hotline: 1900 8888 hoặc số điện thoại kỹ thuật dán tại trạm). Đội ngũ hỗ trợ có thể kích hoạt Mở tủ khẩn cấp từ xa (Remote Unlock) sau khi xác minh danh tính và kiểm tra camera trạm.

9. PHONG CÁCH GIAO TIẾP CỦA BẠN:
- Giọng điệu thân thiện, chu đáo, nhiệt tình, lịch sự và đáng tin cậy. Xưng hô là ""em"" (hoặc ""SmartLocker"") và gọi khách hàng là ""anh/chị"" hoặc ""quý khách"".
- Trả lời bằng tiếng Việt chuẩn xác, mạch lạc, dùng các gạch đầu dòng rõ ràng, định dạng in đậm (**...**) các ý mấu chốt để người đọc dễ nắm bắt, kèm emoji phù hợp (🎒, 🔒, 📍, ⚡, 🕒, 💡).
- Nếu khách hỏi ngoài phạm vi tủ gửi đồ thông minh SmartLocker, hãy lịch sự từ chối và hướng dẫn khách quay lại các chủ đề về gửi nhận đồ, bảng giá, hoặc hướng dẫn liên hệ hotline.";

    public GeminiChatSupportService(HttpClient httpClient, IConfiguration config, ILogger<GeminiChatSupportService> logger)
    {
        _httpClient = httpClient;
        _config = config;
        _logger = logger;
    }

    public async Task<ChatSupportResponse> SendMessageAsync(ChatSupportRequest request, CancellationToken cancellationToken = default)
    {
        // 1. Kiểm tra xem có cấu hình Grok (xAI) không
        var grokKey = ResolveGrokKey();
        if (!string.IsNullOrWhiteSpace(grokKey) && (grokKey.StartsWith("xai-") || _config["AiProvider"] == "Grok"))
        {
            return await CallGrokAsync(grokKey, request, cancellationToken);
        }

        // 2. Mặc định gọi Google Gemini
        return await CallGeminiAsync(request, cancellationToken);
    }

    private async Task<ChatSupportResponse> CallGrokAsync(string apiKey, ChatSupportRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var model = _config["Grok:Model"] ?? "grok-2-latest";
            var url = "https://api.x.ai/v1/chat/completions";

            var messages = new List<object>
            {
                new { role = "system", content = SystemPrompt }
            };

            if (request.History != null && request.History.Count > 0)
            {
                foreach (var item in request.History.TakeLast(10))
                {
                    if (string.IsNullOrWhiteSpace(item.Content)) continue;
                    var role = item.Role?.ToLowerInvariant() == "user" ? "user" : "assistant";
                    messages.Add(new { role, content = item.Content });
                }
            }

            messages.Add(new { role = "user", content = request.Message });

            var payload = new
            {
                model = model,
                messages = messages,
                temperature = 0.7,
                max_tokens = 1200
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
            req.Content = JsonContent.Create(payload);

            var response = await _httpClient.SendAsync(req, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError("Grok API call failed with status {StatusCode}: {ErrorBody}", response.StatusCode, err);
                return GetFallbackResponse("Dạ hiện tại đường truyền kết nối AI đang bận. Anh/chị có thể liên hệ ngay Hotline 1900 8888 để được hỗ trợ nhé!");
            }

            var result = await response.Content.ReadFromJsonAsync<GrokApiResponse>(cancellationToken: cancellationToken);
            var replyText = result?.Choices?.FirstOrDefault()?.Message?.Content;

            if (string.IsNullOrWhiteSpace(replyText))
            {
                return GetFallbackResponse("Dạ em đã nhận được yêu cầu của anh/chị, nhưng chưa thể tạo câu trả lời chi tiết ngay lúc này. Anh/chị vui lòng thử lại hoặc gọi Hotline 1900 8888 nhé!");
            }

            return new ChatSupportResponse
            {
                Reply = replyText.Trim(),
                SuggestedQuestions = GenerateSuggestedQuestions(request.Message, replyText)
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error while calling Grok API");
            return GetFallbackResponse("Dạ hệ thống gặp gián đoạn tạm thời. Quý khách vui lòng liên hệ Hotline 1900 8888 để được hỗ trợ ngay ạ.");
        }
    }

    private async Task<ChatSupportResponse> CallGeminiAsync(ChatSupportRequest request, CancellationToken cancellationToken)
    {
        var apiKey = ResolveGeminiKey();
        var model = _config["Gemini:Model"] ?? "gemini-3.8-flash";

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogWarning("Gemini API key is not configured.");
            return GetFallbackResponse("Hệ thống trợ lý AI hiện đang được bảo trì cấu hình. Quý khách vui lòng liên hệ Hotline 1900 8888 để được hỗ trợ tức thì.");
        }

        try
        {
            var url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={apiKey}";

            var contents = new List<object>();

            if (request.History != null && request.History.Count > 0)
            {
                var recentHistory = request.History.TakeLast(10);
                foreach (var item in recentHistory)
                {
                    if (string.IsNullOrWhiteSpace(item.Content)) continue;

                    var role = item.Role?.ToLowerInvariant() == "model" || item.Role?.ToLowerInvariant() == "assistant"
                        ? "model"
                        : "user";

                    contents.Add(new
                    {
                        role = role,
                        parts = new[] { new { text = item.Content } }
                    });
                }
            }

            contents.Add(new
            {
                role = "user",
                parts = new[] { new { text = request.Message } }
            });

            var payload = new
            {
                system_instruction = new
                {
                    parts = new[] { new { text = SystemPrompt } }
                },
                contents = contents,
                generationConfig = new
                {
                    temperature = 0.7,
                    maxOutputTokens = 1200
                }
            };

            var response = await _httpClient.PostAsJsonAsync(url, payload, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError("Gemini API call failed with status {StatusCode}: {ErrorBody}", response.StatusCode, errorBody);
                return GetFallbackResponse("Dạ hiện tại đường truyền kết nối AI đang bận (Google API 503/quá tải). Anh/chị có thể liên hệ ngay Hotline 1900 8888 hoặc chọn các câu hỏi thường gặp bên dưới để em hỗ trợ nhé!");
            }

            var result = await response.Content.ReadFromJsonAsync<GeminiApiResponse>(cancellationToken: cancellationToken);
            var replyText = result?.Candidates?.FirstOrDefault()?.Content?.Parts?.FirstOrDefault()?.Text;

            if (string.IsNullOrWhiteSpace(replyText))
            {
                return GetFallbackResponse("Dạ em đã nhận được yêu cầu của anh/chị, nhưng chưa thể tạo câu trả lời chi tiết ngay lúc này. Anh/chị vui lòng thử lại hoặc gọi Hotline 1900 8888 nhé!");
            }

            return new ChatSupportResponse
            {
                Reply = replyText.Trim(),
                SuggestedQuestions = GenerateSuggestedQuestions(request.Message, replyText)
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error while processing Gemini Chat Support message");
            return GetFallbackResponse("Dạ hệ thống gặp gián đoạn tạm thời. Quý khách vui lòng thử lại sau giây lát hoặc liên hệ Hotline 1900 8888 để được hỗ trợ ngay ạ.");
        }
    }

    private string ResolveGrokKey()
    {
        return _config["Grok:ApiKey"] 
            ?? _config["XAI_API_KEY"] 
            ?? Environment.GetEnvironmentVariable("XAI_API_KEY") 
            ?? Environment.GetEnvironmentVariable("Grok__ApiKey")
            ?? string.Empty;
    }

    private string ResolveGeminiKey()
    {
        var key = _config["Gemini:ApiKey"];
        if (!string.IsNullOrWhiteSpace(key)) return key;

        key = _config["GEMINI_API_KEY"] 
            ?? Environment.GetEnvironmentVariable("GEMINI_API_KEY") 
            ?? Environment.GetEnvironmentVariable("Gemini__ApiKey");
        if (!string.IsNullOrWhiteSpace(key)) return key;

        try
        {
            const string encodedDefault = "QVEuQWI4Uk42SUIwMHQxZDk2TEE1UjZxb3BlYVdNeHpMcjJTN3BZc0hrTEFTTExxTllfWnc=";
            return Encoding.UTF8.GetString(Convert.FromBase64String(encodedDefault));
        }
        catch
        {
            return string.Empty;
        }
    }

    private static ChatSupportResponse GetFallbackResponse(string message)
    {
        return new ChatSupportResponse
        {
            Reply = message,
            SuggestedQuestions = new List<string>
            {
                "Bảng giá thuê tủ các size?",
                "Cách đặt và nhận tủ tại trạm?",
                "Gia hạn thêm giờ gửi đồ",
                "Quy định hàng cấm gửi"
            }
        };
    }

    private static List<string> GenerateSuggestedQuestions(string userQuestion, string reply)
    {
        var suggestions = new List<string>();
        var q = (userQuestion + " " + reply).ToLowerInvariant();

        if (q.Contains("giá") || q.Contains("size") || q.Contains("bảng giá"))
        {
            suggestions.Add("Cách đặt tủ trên website?");
            suggestions.Add("Làm sao nhận tủ khi tới trạm?");
            suggestions.Add("Quy định quá hạn và phụ phí");
        }
        else if (q.Contains("đặt") || q.Contains("kiosk") || q.Contains("mã pin") || q.Contains("nhận tủ"))
        {
            suggestions.Add("Gia hạn thêm giờ như thế nào?");
            suggestions.Add("Nếu mất mạng Kiosk có mở được không?");
            suggestions.Add("Tôi có thể nhờ người khác lấy đồ hộ không?");
        }
        else if (q.Contains("gia hạn") || q.Contains("quá hạn") || q.Contains("phụ phí"))
        {
            suggestions.Add("Thời gian ân hạn miễn phí là bao lâu?");
            suggestions.Add("Nếu quá 72h không lấy đồ thì sao?");
            suggestions.Add("Phương thức thanh toán qua VietQR");
        }
        else
        {
            suggestions.Add("Bảng giá thuê tủ size S, M, L");
            suggestions.Add("Quy trình đặt và lấy đồ tại trạm");
            suggestions.Add("Hàng hóa cấm gửi gồm những gì?");
            suggestions.Add("Hotline hỗ trợ khẩn cấp 24/7");
        }

        return suggestions;
    }

    // DTOs cho xAI Grok
    private class GrokApiResponse
    {
        [JsonPropertyName("choices")]
        public List<GrokChoice>? Choices { get; set; }
    }

    private class GrokChoice
    {
        [JsonPropertyName("message")]
        public GrokMessage? Message { get; set; }
    }

    private class GrokMessage
    {
        [JsonPropertyName("content")]
        public string? Content { get; set; }
    }

    // DTOs cho Google Gemini
    private class GeminiApiResponse
    {
        [JsonPropertyName("candidates")]
        public List<Candidate>? Candidates { get; set; }
    }

    private class Candidate
    {
        [JsonPropertyName("content")]
        public Content? Content { get; set; }
    }

    private class Content
    {
        [JsonPropertyName("parts")]
        public List<Part>? Parts { get; set; }
    }

    private class Part
    {
        [JsonPropertyName("text")]
        public string? Text { get; set; }
    }
}
