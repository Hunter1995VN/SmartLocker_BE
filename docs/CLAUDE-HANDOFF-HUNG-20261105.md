# Bàn giao tiếp tục UC của Hùng — 05/11/2026

## Quyết định người dùng hiện tại

Ưu tiên UC quan trọng và liên kết trước, nghiệm thu từng UC, không cần hoàn tất cả năm một lượt. Hoàn thiện nghiệp vụ -> kiểm thử đủ -> push nhánh riêng -> rà soát đạt mới merge dev. Main tiếp tục bị giữ, không tự phát hành. Dữ liệu TEST được phép tạo qua API thật; không giả PAID, sensor hoặc xóa nợ cũ.

## Đường dẫn và nhánh

Code kiểm thử sạch nằm ở BE/.worktrees/hung-uc-reviewed và FE/.worktrees/hung-uc-reviewed, cùng workspace gốc Đồ án tốt nghiệp. Hai checkout SmartLocker_BE/SmartLocker_FE gốc có bản nháp của người dùng: giữ nguyên, không reset/stash/discard.

Đang ưu tiên hunglqde180096/uc-s02-complete-20261105 ở cả hai repo. Bốn nhánh cục bộ tiếp theo có checkpoint: uc-s08-complete-20261105, uc-t15-complete-20261105, uc-t16-complete-20261105, uc-t23-complete-20261105 (cùng tiền tố hunglqde180096/). Các checkpoint này xếp chồng trên S02 ban đầu; cần tích hợp sửa S02 cuối cùng trước khi nghiệm thu, không merge nguyên chồng vào dev như các UC đã done.

## Trạng thái nghiệm thu

Trước đợt này đã accepted T09/T11/T12/T17/T21. S/M/L thật đã chạy trước đó; không cộng thêm UC. S02 đã chạy ca TEST S trả phí thật đầy đủ: hai màn hình hiển thị đặt/chờ gửi -> RESERVED -> OCCUPIED -> AVAILABLE; SQL COMPLETED và vé thu hồi. Mất/kết nối lại broker cũng được kiểm chứng. Bằng chứng riêng nằm dưới API/App_Data/private-validation, tuyệt đối không commit nội dung thô.

S08: code camera -> pending -> staff approve -> COMPLETED_MANUAL và ngăn MAINTENANCE; tám unit case thêm, chưa nghiệm thu camera/sự cố thật. T15: guest độc lập, secret hash, pricing/capacity và PayOS thật; chưa nghiệm thu guest trả phí và hardware. T16: ủy quyền PIN8 số một lần, thu hồi/hết hạn/phí/đúng ngăn; chưa nghiệm thu người nhận hộ với hardware. T23: checkpoint build được, chưa có browser/camera kiểm tra đủ năm ngôn ngữ. Xem docs theo từng UC để biết giới hạn.

Các con số test thuộc đúng từng cây mã: S02 129, chồng S08 136, chồng T15 149, chồng T16 160. Không nói 160 test cho nhánh S02 độc lập. Chồng T23 build pass, chưa test chấp nhận chức năng mới. Cảnh báo nullable/bundle có sẵn còn tồn tại.

## Cấu hình và bảo mật

Không chép DB string, Wi-Fi/MQTT password, tài khoản, PIN/QR, key ring, payment link hay flash backup vào Git/tài liệu. Cấu hình cục bộ ở appsettings của checkout gốc và iot_config.h đã ignore; key ring dùng thư mục App_Data/keys gốc để đọc credential hiện có. API local 5002, FE 5188, broker LAN/loopback 1883. Xác minh lại tiến trình trước khi dừng; dừng đúng API local trước build nếu Windows khóa DLL.

ESP32 COM5, SIM-MQTT-001, S SIM-MQTT-01 GPIO26/27; M BT-M-01 GPIO25/32; L BT-L-01 GPIO33/23. MQTT là dữ liệu thiết bị thật. Không gửi script Send-DoorEvent để thay nghiệm thu. HB30 giây và events mở/đóng là hai loại bằng chứng khác nhau; snapshot không được hoàn tất booking. Các booking cũ quá hạn thuộc dữ liệu thật, không tự sửa để bỏ chặn.

## Kiểm tra / phát hành

BE: dotnet test tests/Hung.UseCases.Tests/Hung.UseCases.Tests.csproj. FE: pnpm build, lint các file đổi. tests/hung/uc-s02.html dùng AdminLogin và grid production, không mock/token injection. Headless Edge dùng context riêng cho acceptance; không có điều khiển cửa giả. Browser UI + API + SQL phải khớp sau mỗi bước, replay credential sau completion phải bị từ chối.

GitHub connector từng bị 403 khi tạo PR; Git push đã hoạt động. Không lấy token từ credential store. Không force-push. Fetch origin/dev trước merge, xử lý conflict và kiểm tra cây tích hợp. Chỉ những UC accepted mới được push/merge theo yêu cầu mới nhất. Không merge main.
