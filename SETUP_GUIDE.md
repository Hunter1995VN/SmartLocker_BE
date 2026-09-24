# ============================================
# SMART LOCKER - HƯỚNG DẪN CÀI ĐẶT DỰ ÁN (CHÍNH THỨC)
# ============================================

## 📋 YÊU CẦU HỆ THỐNG

### Backend (.NET 8 Clean Architecture)
- .NET SDK 8.0 trở lên
- Visual Studio 2022 hoặc VS Code / JetBrains Rider

### Frontend (React + Vite + Tailwind CSS)
- Node.js 18+ (khuyến nghị 20 LTS)
- npm hoặc yarn

---

## 🚀 CÁC BƯỚC CÀI ĐẶT

### 1. Clone Repositories
Nhóm sử dụng 2 repository riêng biệt:
```bash
# Clone Frontend
git clone https://github.com/Hunter1995VN/SmartLocker_FE.git

# Clone Backend
git clone https://github.com/Hunter1995VN/SmartLocker_BE.git
```

---

### 2. Cấu hình & Chạy Backend

#### 2.1 Tạo file cấu hình bảo mật `appsettings.Development.json`
> ⚠️ **LƯU Ý BẢO MẬT & GITGUARDIAN:**  
> File `appsettings.json` trên Git chỉ chứa các giá trị placeholder (`YOUR_PASSWORD`, `YOUR_PAYOS_API_KEY`...) để tránh bị bot GitGuardian cảnh báo lộ secret.  
> Để chạy ở máy cá nhân, mỗi thành viên tạo một file tên là **`appsettings.Development.json`** nằm ngang hàng với file `appsettings.json` (trong thư mục `SmartLocker_BE/API`). File này đã được `.gitignore` bảo vệ nên sẽ **KHÔNG BAO GIỜ bị đẩy lên Git**.

Tạo file `SmartLocker_BE/API/appsettings.Development.json` và copy toàn bộ nội dung cấu hình đầy đủ sau:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning",
      "Microsoft.EntityFrameworkCore.Database.Command": "Warning",
      "Infrastructure.BackgroundJobs": "Information"
    }
  },
  "ConnectionStrings": {
    "DefaultConnection": "Server=db65218.public.databaseasp.net; Database=db65218; User Id=db65218; Password=P!m62sA=y+7X; Encrypt=True; TrustServerCertificate=True; MultipleActiveResultSets=True;"
  },
  "JwtSettings": {
    "SecretKey": "SmartLocker.Super.Secret.Key.2025.MinLength32Chars",
    "Issuer": "SmartLocker",
    "Audience": "SmartLocker.Clients",
    "AccessTokenMinutes": 1440
  },
  "SmtpSettings": {
    "Host": "smtp.gmail.com",
    "Port": 587,
    "Username": "",
    "Password": "",
    "FromEmail": "no-reply@smartlocker.vn",
    "FromName": "SmartLocker"
  },
  "GoogleAuth": {
    "ClientId": "973344939660-v3cmhvr8so75j9pemns0rn7fc9glrtf4.apps.googleusercontent.com"
  },
  "PayOS": {
    "ClientId": "80203657-9b8a-43f8-ae7b-4d9c58e89bbf",
    "ApiKey": "697ac984-680a-40e8-a752-a6e5bbbc78d7",
    "ChecksumKey": "04f633fe1d07784d1676c2c83fc147d21766e0314b947d644e96014138f612e9",
    "ReturnUrl": "http://localhost:5173/payment/callback",
    "CancelUrl": "http://localhost:5173/payment/cancel"
  },
  "AllowedHosts": "*"
}
```

> ⚠️ **LƯU Ý VỀ DATABASE:**  
> Dự án sử dụng **Database-First** trên đám mây MonsterSQL (`db65218.public.databaseasp.net`) đã có sẵn toàn bộ **23 bảng chuẩn SRS v3.0**.  
> **KHÔNG CẦN và KHÔNG CHẠY** `dotnet ef database update`.

#### 2.2 Khởi động Backend
```bash
cd SmartLocker_BE/API
dotnet run
```
* API tự động lắng nghe tại: **`http://localhost:5000`**
* Trang tài liệu API **Swagger UI** tự động mở tại: **`http://localhost:5000/swagger`**

---

### 3. Cấu hình & Chạy Frontend

#### 3.1 Cấu hình file môi trường
```bash
cd SmartLocker_FE
copy .env.example .env
```
Nội dung file `.env`:
```env
VITE_API_URL=http://localhost:5000
VITE_GOOGLE_MAPS_API_KEY=
```

#### 3.2 Cài đặt thư viện & Khởi động
```bash
npm install
npm run dev
```
* Ứng dụng Frontend chạy tại: **`http://localhost:5173`**

---

### 4. Hướng dẫn Test Tính Năng

#### 4.1 Đăng ký & Đăng nhập (Auth)
* Khi đăng ký tài khoản mới trên giao diện web, mã xác thực **OTP 6 số sẽ tự động in ra màn hình Console của Backend** (chế độ DEV MODE, không bắt buộc phải cấu hình tài khoản Gmail thật).
* Lấy mã OTP từ Console nhập vào web để hoàn tất kích hoạt tài khoản.

#### 4.2 Đặt tủ & Thanh toán PayOS
* Tạo đơn đặt tủ qua giao diện hoặc Swagger UI: `POST /api/bookings`.
* Hệ thống sinh link thanh toán PayOS thật kèm mã QR thanh toán ngân hàng.
* Khi thanh toán thành công, webhook hoặc Background Service tự động đối soát xác thực và cấp mã mở tủ (Access Code & QR).

---

### 5. Xử Lý Sự Cố Thường Gặp

| Vấn đề | Nguyên nhân & Cách giải quyết |
|:---|:---|
| **Lỗi 500 khi gọi bất kỳ API nào** | Kiểm tra `JwtSettings:SecretKey` trong `appsettings.Development.json` không được để chuỗi rỗng `""`. Phải có ít nhất 32 ký tự. |
| **Không mở được trang Swagger** | Đảm bảo truy cập đúng `http://localhost:5000/swagger` khi backend đang chạy. |
| **Frontend không gọi được Backend** | Kiểm tra file `.env` của FE đã đặt `VITE_API_URL=http://localhost:5000` và Backend đã bật. |

---

### 6. Quy Định Nhánh Git (Git Workflow)
Quy trình phân nhánh bắt buộc của nhóm:
* **`main`**: Nhánh phát hành chính thức, chỉ merge từ `dev` sau khi toàn bộ tính năng đã được test ổn định. **Tuyệt đối không merge thẳng từ nhánh cá nhân vào `main`.**
* **`dev`**: Nhánh tích hợp chung của toàn nhóm. Mọi thành viên tạo Pull Request / merge từ nhánh cá nhân của mình vào `dev` trước.
* **Nhánh cá nhân (`nhiem`, `quan`, ...)**: Các thành viên code và commit trên nhánh của mình, sau khi test xong thì merge vào `dev`.

```
[Nhánh cá nhân: nhiem, quan...]  ──merge──>  [dev]  ──test & verify──>  [main]
```

---

### 📞 THÔNG TIN REPOSITORY
- **Backend:** [https://github.com/Hunter1995VN/SmartLocker_BE](https://github.com/Hunter1995VN/SmartLocker_BE) (Nhánh tích hợp: `dev`, Nhánh chính: `main`)
- **Frontend:** [https://github.com/Hunter1995VN/SmartLocker_FE](https://github.com/Hunter1995VN/SmartLocker_FE) (Nhánh tích hợp: `dev`, Nhánh chính: `main`)
- **Leader:** Hunter1995VN
