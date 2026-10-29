# UC-T21 — nghiệm thu nhánh tách từ dev

Phạm vi: khách đánh giá sau checkout COMPLETED hoặc COMPLETED_MANUAL, 1–5 sao, CLEAN/FAST/EASY_TO_FIND, góp ý tối đa 1000 ký tự, một đánh giá/booking. Chỉ chủ booking được đọc/gửi; ADMIN/STAFF được xem tổng hợp. Public station trả ratingAverage/ratingCount từ SQL, không có điểm mẫu.

Nhánh `hunglqde180096/uc-t21-verified` được tách từ `origin/dev` 70b5cbd; không merge chuỗi nhánh UC cũ vì chứa UC chưa nghiệm thu. Không thay đổi voucher, hoàn tiền, RBAC, thanh toán hoặc firmware. Thêm tùy chọn `BackgroundJobs:Enabled=false` cho môi trường kiểm thử DB chung; mặc định vẫn true.

Validation ngày 29/10/2026:
- 12/12 tests trong ReviewTests PASS: phạm vi sao/trạng thái, checkout timestamp, chủ sở hữu, validation, lưu/trim/deduplicate, chặn trùng và tính tổng hợp.
- FE build PASS; bản tích hợp trên dev qua 7 nhóm kiểm tra API/SQL/production UI thật, không mock HTTP. Kết quả tương ứng trong FE `docs/validation/uc-t21-actual.json`.
- Đã tạo review TEST 3 sao qua API thật ở đợt kiểm thử được người dùng cho phép, lưu đúng SQL. Bản tích hợp đọc lại review đó, kiểm tra duplicate 409, sai sao/tiêu chí/trạng thái 400, sai chủ 404, Traveler đọc staff summary 403. Không xóa/sửa review để làm ca test pass.
- History và chi tiết hiển thị COMPLETED_MANUAL; form biến mất khi đã gửi, trang trạm và staff summary cùng count=1/average=3 tại thời điểm thử.
- Unique BookingId và Stars CHECK đã có trên DB thật. Script `20261029_UcT21StationReviews.sql` chỉ triển khai bảng UC này khi thiếu, không seed/backfill/xóa dữ liệu. Chưa chạy hai INSERT đồng thời lên booking mới; chống trùng dựa trên kiểm thử API và unique constraint SQL.

Review TEST được giữ nguyên với comment ghi rõ TEST và hiện có trong tổng điểm trạm. Đây là dữ liệu kiểm thử được cho phép, không phải phản hồi trải nghiệm thực tế. Không có thanh toán hay lệnh mở tủ trong ca nghiệm thu UC-T21.

Không commit cấu hình DB, thông tin đăng nhập, token, PIN, khóa mã hóa hoặc dữ liệu khách hàng vào nhánh này. Lệnh test: `dotnet test tests/Hung.UseCases.Tests/Hung.UseCases.Tests.csproj`.
