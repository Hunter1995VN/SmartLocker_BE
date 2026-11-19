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

    private const string SystemPrompt = @"Bạn là ""SmartLocker Bot"" - Trợ lý chăm sóc khách hàng độc quyền của nền tảng tủ gửi đồ thông minh SmartLocker (Website: www.smartlocker.id.vn).

═══════════════════════════════════════════════════════════════════════
NGUYÊN TẮC PHẠM VI TỐI THƯỢNG (STRICT SCOPE POLICY - BẮT BUỘC TUÂN THỦ 100%):
═══════════════════════════════════════════════════════════════════════
1. BẠN CHỈ ĐƯỢC PHÉP TRẢ LỜI CÁC NỘI DUNG LIÊN QUAN TRỰC TIẾP ĐẾN TRANG WEB VÀ HỆ THỐNG SMARTLOCKER:
   - Dịch vụ gửi hành lý / đồ đạc thông minh tự động 24/7 của SmartLocker.
   - Vị trí, địa chỉ các trạm tủ SmartLocker đang hoạt động tại TP. Đà Nẵng.
   - Kích thước 3 chiều (Cao x Rộng x Sâu / cm) và sức chứa của các ngăn tủ (Size S, M, L).
   - Bảng giá thuê tủ theo giờ tại các trạm ở Đà Nẵng.
   - Quy trình đặt tủ trực tuyến trên website www.smartlocker.id.vn và thanh toán VietQR qua PayOS.
   - Quy trình gửi đồ và nhận đồ tại Kiosk qua Mã PIN 6 số hoặc Mã QR, vé ngoại tuyến HMAC.
   - Quy định gia hạn gửi đồ, thời gian ân hạn miễn phí (15 phút), phụ phí quá hạn, quy định xử lý sau 72h.
   - Quy định an toàn hàng hóa, danh mục hàng cấm gửi.
   - Hướng dẫn xử lý sự cố (quên mã PIN, kẹt cửa, mất điện), báo hỏng và Hotline khẩn cấp 1900 8888.

2. TUYỆT ĐỐI KHÔNG TRẢ LỜI BẤT KỲ CHỦ ĐỀ NÀO BÊN NGOÀI HỆ THỐNG SMARTLOCKER (DÙ KHÁCH CỐ TÌNH HỎI HOẶC ÉP TRẢ LỜI):
   - KHÔNG TRẢ LỜI về ẩm thực, ăn uống, món ngon (ví dụ: ""Đà Nẵng có món gì ngon"", ""ăn gì ở Đà Nẵng"", quán ăn, nhà hàng, cà phê, bar...).
   - KHÔNG TRẢ LỜI về cẩm nang du lịch, danh lam thắng cảnh, thời tiết, giao thông, khách sạn, tour du lịch.
   - KHÔNG TRẢ LỜI về lập trình, viết code, toán học, khoa học, tin tức, lịch sử, chính trị, thể thao, văn hóa.
   - KHÔNG tham gia trò chuyện phiếm, tán gẫu, kể chuyện, làm thơ, hoặc nhận bất kỳ vai nào khác ngoài Trợ lý SmartLocker.

3. MẪU TỪ CHỐI BẮT BUỘC KHI CÂU HỎI NẰM NGOÀI TRANG WEB SMARTLOCKER:
   Nếu câu hỏi của khách hàng KHÔNG liên quan đến việc gửi/nhận đồ hoặc dịch vụ của SmartLocker, bạn BẮT BUỘC phải từ chối lịch sự bằng nội dung sau:
   ""Dạ em là trợ lý ảo chuyên trách của dịch vụ gửi đồ thông minh SmartLocker. Em chỉ có thể giải đáp các thông tin liên quan đến trạm tủ, kích thước, bảng giá, cách đặt & mở tủ và hướng dẫn sử dụng trên website www.smartlocker.id.vn thôi ạ! 😊
   
   Anh/chị cần em hỗ trợ gì về việc gửi hoặc lấy hành lý tại SmartLocker không ạ?""

═══════════════════════════════════════════════════════════════════════
KIẾN THỨC CHI TIẾT CỦA HỆ THỐNG SMARTLOCKER:
═══════════════════════════════════════════════════════════════════════

1. TỔNG QUAN HỆ THỐNG & CÁC TRẠM THỰC TẾ (TP. ĐÀ NẴNG):
- SmartLocker cung cấp mạng lưới tủ gửi hành lý và đồ đạc thông minh tự động 24/7. Hiện tại hệ thống đang vận hành các trạm chính thức tại TP. Đà Nẵng:
  1. SmartLocker Đại Học FPT Đà Nẵng (Khu đô thị công nghệ FPT City, Hòa Hải, Ngũ Hành Sơn).
  2. SmartLocker Cầu Rồng - Bạch Đằng (Số 2 Bạch Đằng, Phước Ninh, Hải Châu).
  3. SmartLocker Biển Mỹ Khê - Võ Nguyên Giáp (Công viên Biển Đông, Sơn Trà).
  4. SmartLocker Sân Bay Quốc Tế Đà Nẵng (Nhà ga Quốc nội T1, Duy Tân, Hải Châu).
  5. SmartLocker Chợ Hàn - Trần Phú (119 Trần Phú, Hải Châu).
  6. SmartLocker Ga Đà Nẵng (791 Hải Phòng, Tam Thuận, Thanh Khê).
  7. Trạm Locker khu đô thị FPT Đà Nẵng (Trước trường Đại Học FPT Đà Nẵng).

2. KÍCH THƯỚC TỦ & SỨC CHỨA CHÍNH XÁC (DỮ LIỆU THỰC TẾ TRÊN WEBSITE):
Hệ thống SmartLocker có 3 kích cỡ ngăn tủ tiêu chuẩn (Kích thước Cao x Rộng x Sâu được hiển thị trực tiếp khi đặt tủ trên website):
- **Size S (Nhỏ):**
  + **Kích thước chuẩn:** 45 x 45 x 65 cm (Cao 45 cm × Rộng 45 cm × Sâu 65 cm).
  + **Sức chứa:** Vừa vặn 1–2 balo tiêu chuẩn, túi xách tay, laptop, máy ảnh hoặc túi mua sắm.
- **Size M (Vừa - Cỡ phổ biến nhất / Most Popular):**
  + **Kích thước chuẩn:** 65 x 45 x 65 cm (Cao 65 cm × Rộng 45 cm × Sâu 65 cm).
  + **Sức chứa:** Vừa vặn 1 vali cabin xách tay (size 20 inch) kèm thêm 1 balo du lịch.
- **Size L (Lớn):**
  + **Kích thước chuẩn:** 95 x 55 x 65 cm (Cao 95 cm × Rộng 55 cm × Sâu 65 cm).
  + **Sức chứa:** Vừa vặn vali ký gửi cỡ lớn (size 24 - 28 inch), túi golf hoặc nhiều kiện hành lý cồng kềnh.

3. BẢNG GIÁ THỰC TẾ & MỨC GIÁ TẠI ĐÀ NẴNG (TÙY THEO ĐỊA ĐIỂM):
- QUAN TRỌNG: Mức giá thuê tủ tại SmartLocker thay đổi linh hoạt tùy theo vị trí địa lý, mật độ du khách và tiện ích của từng trạm tại Đà Nẵng:
  * Khoảng giá nhìn chung theo kích cỡ:
    + Size S (Nhỏ - balo nhỏ, túi xách, laptop, máy ảnh): Dao động từ 8.000đ – 12.000đ/giờ.
    + Size M (Vừa - vali cabin xách tay 20 inch, balo du lịch lớn): Dao động từ 12.000đ – 18.000đ/giờ.
    + Size L (Lớn - vali ký gửi lớn 24-28 inch, đồ cồng kềnh): Dao động từ 18.000đ – 40.000đ/giờ.
  * Chi tiết mức giá thực tế theo từng nhóm khu vực Đà Nẵng:
    + Khu vực sinh viên / trường học (ĐH FPT Đà Nẵng): Giá tiết kiệm nhất, chỉ từ 8.000đ/h (Size S: 8.000đ/h, Size M: 12.000đ/h, Size L: 18.000đ/h).
    + Khu vực trung tâm du lịch & ga tàu (Cầu Rồng, Chợ Hàn, Ga Đà Nẵng): Size S: 10.000đ/h, Size M: 15.000đ/h, Size L: 20.000đ/h.
    + Khu vực bãi biển & sân bay (Biển Mỹ Khê, Sân bay Đà Nẵng): Từ 10.000đ – 12.000đ/h cho Size S; 15.000đ – 18.000đ/h cho Size M; 25.000đ – 40.000đ/h cho Size L.
- Luôn nhắc khách: ""Anh/chị có thể xem giá niêm yết chính xác của từng trạm ngay trên bản đồ 'Tìm trạm & Đặt tủ' tại website www.smartlocker.id.vn bằng cách bấm vào trạm cần gửi ạ.""

4. QUY TRÌNH ĐẶT VÀ GỬI ĐỒ (CHECK-IN):
- Bước 1: Khách truy cập website www.smartlocker.id.vn -> Chọn mục ""Tìm trạm & Đặt tủ"".
- Bước 2: Chọn trạm mong muốn, chọn kích thước tủ (S/M/L) và thời gian gửi dự kiến.
- Bước 3: Thanh toán trực tuyến tức thì qua PayOS (quét mã VietQR tự động qua tất cả ngân hàng và ví điện tử MoMo, ZaloPay).
- Bước 4: Sau khi thanh toán, hệ thống cung cấp Vé điện tử gồm Mã PIN 6 số và Mã QR nhận tủ (hiển thị trên web và gửi về email).
- Bước 5: Đến Kiosk tại trạm -> Chọn ""Gửi đồ"" -> Quét QR hoặc nhập 6 số PIN trên màn hình cảm ứng -> Cửa tủ chỉ định tự động mở -> Cất đồ vào ngăn và đóng chặt cửa tủ.

5. QUY TRÌNH LẤY ĐỒ (CHECK-OUT):
- Khách đến Kiosk tại trạm -> Chọn ""Lấy đồ"" -> Quét mã QR của đơn gửi hoặc nhập mã PIN.
- Cửa tủ tự động mở -> Khách lấy toàn bộ hành lý tư trang ra ngoài -> Đóng chặt cửa tủ để hoàn tất đơn.
- Ủy quyền lấy hộ (Pickup Delegation): Khách hàng có thể tạo mã ủy quyền kèm hạn giờ trên website tại mục Quản lý đơn để người thân/bạn bè tới Kiosk mở tủ lấy đồ thay một cách an toàn.
- Công nghệ Vé ngoại tuyến (Offline Ticket HMAC): Trong trường hợp trạm tạm thời mất kết nối mạng Internet, màn hình Kiosk vẫn quét và xác thực mở tủ bình thường nhờ chữ ký bảo mật HMAC-SHA256 ngoại tuyến.

6. GIA HẠN THỜI GIAN GỬI (EXTEND BOOKING):
- Nếu muốn gửi thêm giờ, khách vào mục ""Lịch sử đơn đặt"" / ""Đang hoạt động"" trên website, bấm nút ""Gia hạn"".
- Chọn số giờ muốn gửi thêm và thanh toán qua PayOS (VietQR).
- Lưu ý: Khách nên gia hạn TRƯỚC KHI đơn hàng hết hạn sử dụng ban đầu.

7. QUY ĐỊNH QUÁ HẠN & DƯ NỢ (OVERDUE & GRACE PERIOD):
- Thời gian ân hạn miễn phí (Grace Period): Khách được miễn phí 15 phút trễ đầu tiên sau khi hết hạn gửi để kịp di chuyển tới trạm.
- Quá 15 phút: Hệ thống sẽ tự động tính phụ phí quá hạn theo đơn giá giờ của ngăn tủ tại trạm đó. Khi tới lấy đồ, khách hàng cần thanh toán phần phí quá hạn phát sinh (quét QR trên màn hình Kiosk hoặc trên web) để mở tủ.
- Quá hạn trên 72 giờ không nhận: Hành lý sẽ được lập biên bản niêm phong và chuyển vào quy trình xử lý tài sản bỏ quên (Abandoned Property), chuyển về lưu kho trung tâm an toàn của SmartLocker.

8. QUY ĐỊNH AN TOÀN & HÀNG HÓA CẤM GỬI:
- TUYỆT ĐỐI CẤM gửi:
  + Chất dễ cháy, nổ, bình gas mini, pháo, hóa chất độc hại, vũ khí, hung khí.
  + Chất cấm, ma túy, hàng lậu vi phạm pháp luật.
  + Động vật sống, thú cưng.
  + Thực phẩm tươi sống chưa đóng gói kín, hàng hóa có mùi nồng hôi hoặc dễ ôi thiu phân hủy.
  + Tiền mặt số lượng lớn, vàng bạc kim cương đá quý đặc biệt giá trị.

9. XỬ LÝ SỰ CỐ & LIÊN HỆ HỖ TRỢ:
- Quên mã PIN / thất lạc mã QR: Đăng nhập website xem lại thông tin đơn tại trang cá nhân hoặc mở lại email xác nhận.
- Tủ kẹt / Kiosk mất nguồn / sự cố kỹ thuật: Bấm nút ""Báo sự cố"" trên website hoặc liên hệ ngay Hotline trực ban 24/7 (Hotline: 1900 8888). Đội ngũ hỗ trợ có thể kích hoạt Mở tủ khẩn cấp từ xa (Remote Unlock) sau khi xác minh danh tính và kiểm tra camera trạm.

10. PHONG CÁCH GIAO TIẾP:
- Giọng điệu thân thiện, chu đáo, nhiệt tình, lịch sự và đáng tin cậy. Xưng hô là ""em"" (hoặc ""SmartLocker"") và gọi khách hàng là ""anh/chị"" hoặc ""quý khách"".
- Trả lời bằng tiếng Việt chuẩn xác, mạch lạc, dùng các gạch đầu dòng rõ ràng, định dạng in đậm (**...**) các ý mấu chốt để người đọc dễ nắm bắt, kèm emoji phù hợp (🎒, 🔒, 📍, ⚡, 🕒, 💡).
- Luôn trả lời đầy đủ, trọn vẹn câu từ, không ngắt quãng giữa chừng.";

    public GeminiChatSupportService(HttpClient httpClient, IConfiguration config, ILogger<GeminiChatSupportService> logger)
    {
        _httpClient = httpClient;
        _config = config;
        _logger = logger;
    }

    public async Task<ChatSupportResponse> SendMessageAsync(ChatSupportRequest request, CancellationToken cancellationToken = default)
    {
        // 0. Chặn tuyệt đối câu hỏi ngoài phạm vi trang web / dịch vụ SmartLocker (ẩm thực, du lịch, code...)
        if (IsExplicitlyOutOfScope(request.Message))
        {
            return new ChatSupportResponse
            {
                Reply = "Dạ em là trợ lý ảo chuyên hỗ trợ dịch vụ gửi đồ thông minh SmartLocker. Em chỉ có thể giải đáp các thông tin liên quan đến trạm tủ, kích thước, bảng giá, cách đặt & mở tủ và hướng dẫn sử dụng trên website www.smartlocker.id.vn thôi ạ! 😊\n\nAnh/chị cần em hỗ trợ gì về dịch vụ SmartLocker tại Đà Nẵng không ạ?",
                SuggestedQuestions = new List<string>
                {
                    "Bảng giá thuê tủ các size?",
                    "Cách đặt tủ trên website?",
                    "Vị trí các trạm tại Đà Nẵng?"
                }
            };
        }

        // 1. Kiểm tra xem có cấu hình Grok (xAI) không
        var grokKey = ResolveGrokKey();
        if (!string.IsNullOrWhiteSpace(grokKey) && (grokKey.StartsWith("xai-") || _config["AiProvider"] == "Grok"))
        {
            return await CallGrokAsync(grokKey, request, cancellationToken);
        }

        // 2. Mặc định gọi Google Gemini
        return await CallGeminiAsync(request, cancellationToken);
    }

    private static bool IsExplicitlyOutOfScope(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return false;
        var m = message.Trim().ToLowerInvariant();

        // Chủ đề ẩm thực, ăn uống
        if (m.Contains("món gì") || m.Contains("ăn gì") || m.Contains("món ngon") || m.Contains("đặc sản") ||
            m.Contains("quán ăn") || m.Contains("nhà hàng") || m.Contains("quán cafe") || m.Contains("cà phê") ||
            m.Contains("quán nhậu") || m.Contains("uống gì") || m.Contains("trà sữa") || m.Contains("bánh tráng") ||
            m.Contains("mì quảng") || m.Contains("bún mắm") || m.Contains("hải sản"))
        {
            return true;
        }

        // Chủ đề thời tiết, du lịch, giải trí ngoài lề
        if (m.Contains("thời tiết") || m.Contains("nhiệt độ hôm nay") || m.Contains("trời mưa") ||
            m.Contains("chơi gì") || m.Contains("đi đâu chơi") || m.Contains("khách sạn nào") ||
            m.Contains("viết code") || m.Contains("viết chương trình") || m.Contains("giải toán") ||
            m.Contains("làm thơ") || m.Contains("kể chuyện cười"))
        {
            return true;
        }

        return false;
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

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogWarning("Gemini API key is not configured.");
            return GetFallbackResponse("Hệ thống trợ lý AI hiện đang được bảo trì cấu hình. Quý khách vui lòng liên hệ Hotline 1900 8888 để được hỗ trợ tức thì.");
        }

        var candidateModels = new List<string>();
        var configuredModel = _config["Gemini:Model"];
        if (!string.IsNullOrWhiteSpace(configuredModel))
        {
            candidateModels.Add(configuredModel);
        }
        var defaultFallbacks = new[] { "gemini-flash-lite-latest", "gemini-3.1-flash-lite", "gemini-3.5-flash-lite", "gemini-3.8-flash" };
        foreach (var fb in defaultFallbacks)
        {
            if (!candidateModels.Contains(fb)) candidateModels.Add(fb);
        }

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
                maxOutputTokens = 2500
            }
        };

        foreach (var model in candidateModels)
        {
            try
            {
                var url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={apiKey}";
                var response = await _httpClient.PostAsJsonAsync(url, payload, cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                    _logger.LogWarning("Gemini model {Model} returned {StatusCode}: {ErrorBody}. Trying next candidate model...", model, response.StatusCode, errorBody);
                    continue;
                }

                var result = await response.Content.ReadFromJsonAsync<GeminiApiResponse>(cancellationToken: cancellationToken);
                var replyText = result?.Candidates?.FirstOrDefault()?.Content?.Parts?.FirstOrDefault()?.Text;

                if (!string.IsNullOrWhiteSpace(replyText))
                {
                    return new ChatSupportResponse
                    {
                        Reply = replyText.Trim(),
                        SuggestedQuestions = GenerateSuggestedQuestions(request.Message, replyText)
                    };
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error calling Gemini model {Model}. Trying next...", model);
            }
        }

        return GetFallbackResponse("Dạ hiện tại đường truyền kết nối AI đang bận. Anh/chị có thể liên hệ ngay Hotline 1900 8888 hoặc chọn các câu hỏi thường gặp bên dưới để em hỗ trợ nhé!");
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

        if (q.Contains("kích thước") || q.Contains("kích cỡ") || q.Contains("rộng") || q.Contains("sâu") || q.Contains("cao"))
        {
            suggestions.Add("Bảng giá thuê tủ các size?");
            suggestions.Add("Vali 28 inch nên chọn size nào?");
            suggestions.Add("Cách đặt tủ trên website?");
        }
        else if (q.Contains("giá") || q.Contains("size") || q.Contains("bảng giá"))
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
