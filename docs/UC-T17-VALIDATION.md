# UC-T17 — nghiệp vụ tìm đồ đã kiểm thử

Nhánh dựa trên dev BE b849643 sau UC-T21; chỉ thêm LostItemReport, mapping bảng Notifications đã có, API chủ booking và staff xử lý, validation JPEG, schema riêng UC-T17 và tests. Không gộp UC-T16/T15/T11/T12 hay xử lý cảm biến lỗi UC-S08.

Tiêu chí:
- Chỉ chủ booking COMPLETED/COMPLETED_MANUAL, có checkout trong 7 ngày, mô tả ≤2000; JPEG minh họa tùy chọn ≤500 KB, kiểm tra cấu trúc đầu/cuối, segment và kích thước ảnh.
- Mỗi booking chỉ có một phiếu đang OPEN hoặc FOUND trong luồng API (serializable transaction/retry); SQL có filtered unique OPEN BookingId.
- Tạo incident HIGH đúng booking/trạm/tủ. Chỉ BLOCKED tủ AVAILABLE và không có booking pending/confirmed/stored khác; không nhả reservation khách sau, không nhả tủ hỏng hoặc note của incident khác.
- ADMIN/STAFF phản hồi FOUND/NOT_FOUND, chỉ RETURNED sau FOUND; RETURNED là terminal. Save lặp cùng kết quả/phản hồi idempotent. Mỗi phản hồi mới tạo thông báo in-app đúng chủ và audit; owner-only mark READ.

Validation:
- 33/33 unit tests PASS: 21 T17 và 12 hồi quy T21. Ca biên 7 ngày/trạng thái, ảnh sai, phân quyền chủ, xung đột booking khác, bàn giao và thông báo/audit.
- Form/ảnh/staff queue và API thật trên cùng SQL dùng chung, với record TEST do người dùng cho phép. Lỗi nhãn select được phát hiện, sửa và tiếp tục trên cùng phiếu TEST, không dựng dữ liệu DB để pass.
- Phiếu kết thúc RETURNED qua FOUND, staff được kiểm tra bằng tài khoản ADMIN đã cấp; Traveler bị cấm gọi API staff. UI nhận/đọc phản hồi; ảnh JPEG đã decode thực. Không tuyên bố có đồ thất lạc/bàn giao vật lý thật.
- SQL `uc-t17-sql.json`: ảnh 56419 ký tự, incident RESOLVED, ba notice TEST READ (cleanup + FOUND + RETURNED); booking gốc COMPLETED_MANUAL, checkout/review không đổi qua API trước/sau.
- 7 nhóm API/UI thật của T21 chạy lại sau mở rộng support GET: PASS. Không có API 5xx hoặc browser exception trong ca T17.

Schema `20261029_UcT17LostItems.sql` chỉ thêm cấu trúc T17 còn thiếu; Notifications đã tồn tại và không được tạo lại. DB shared đã có các cấu trúc từ đợt triển khai được người dùng cho phép. Không seed hay đổi booking/payment status trong SQL để thử tính năng.

Không push định danh khách/booking/report, thông tin đăng nhập hoặc ảnh khách trong báo cáo; bằng chứng chi tiết giữ cục bộ. Không gửi email/SMS bên ngoài. Mặc định job nền ứng dụng vẫn true; test server dùng BackgroundJobs__Enabled=false/Mqtt__Enabled=false để không xử lý đơn hoặc sensor của nhóm.
