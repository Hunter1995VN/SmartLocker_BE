# ============================================
# SMART LOCKER - HƯỚNG DẪN CÀI ĐẶT CHO MEMBER
# ============================================

## 📋 YÊU CẦU HỆ THỐNG

### Backend (.NET 8)
- .NET SDK 8.0 trở lên
- SQL Server (LocalDB, Express, hoặc SQL Server đầy đủ)
- Visual Studio 2022 hoặc VS Code

### Frontend (React + Vite)
- Node.js 18+ 
- npm hoặc yarn

---

## 🚀 CÁC BƯỚC CÀI ĐẶT

### 1. Clone Repository
```bash
git clone https://github.com/Hunter1995VN/SmartLocker_FE.git
cd SmartLocker_FE
```

### 2. Cấu hình Backend

#### 2.1 Copy file cấu hình
```bash
cd ../Backend/API
copy appsettings.Example.json appsettings.json
```

#### 2.2 Chỉnh sửa `appsettings.json`
Mở file và điền thông tin của bạn:

| Key | Mô tả | Ví dụ |
|-----|-------|-------|
| `ConnectionStrings:Default` | Chuỗi kết nối SQL Server | `Server=localhost;Database=SmartLocker;...` |
| `JwtSettings:SecretKey` | Khóa bảo mật JWT (32+ ký tự) | `MySuperSecretKey12345678901234567890` |
| `SmtpSettings:Username` | Email gửi thư | `your-email@gmail.com` |
| `SmtpSettings:Password` | App Password Gmail | (xem hướng dẫn bên dưới) |
| `GoogleAuth:ClientId` | Google OAuth Client ID | `123456789-xxx.apps.googleusercontent.com` |

#### 2.3 Tạo Database
```bash
# Chạy migration để tạo bảng
dotnet ef database update
# Hoặc nếu chưa có EF tools
dotnet tool install --global dotnet-ef
dotnet ef database update
```

### 3. Cấu hình Frontend

#### 3.1 Copy file môi trường
```bash
cd ../../SmartLocker_FE
copy .env.example .env.local
```

#### 3.2 Chỉnh sửa `.env.local`
```env
VITE_GOOGLE_MAPS_API_KEY=your-google-maps-api-key
VITE_API_URL=http://localhost:5000
```

### 4. Lấy Google Maps API Key (Miễn phí)
1. Vào https://console.cloud.google.com/
2. Tạo project mới
3. Enable "Maps JavaScript API"
4. Tạo Credentials → API Key
5. Copy key vào `.env.local`

### 5. Lấy Gmail App Password (để gửi email)
1. Bật 2-Step Verification trong tài khoản Google
2. Vào https://myaccount.google.com/apppasswords
3. Tạo App Password mới (chọn app: Mail, device: Windows)
4. Copy password 16 ký tự vào `appsettings.json`

### 6. Chạy ứng dụng

#### Backend:
```bash
cd Backend/API
dotnet run
# Server chạy tại http://localhost:5000
```

#### Frontend:
```bash
cd Frontend
npm install
npm run dev
# App chạy tại http://localhost:5173
```

---

## 🔧 XỬ LÝ LỖI THƯỜNG GẶP

### Lỗi "Connection string is empty"
→ Chưa điền `ConnectionStrings:Default` trong `appsettings.json`

### Lỗi "Invalid JWT Secret Key"
→ `SecretKey` phải có ít nhất 32 ký tự

### Lỗi "SMTP authentication failed"
→ Kiểm tra lại email/password Gmail
→ Đảm bảo đã bật 2-Step Verification và dùng App Password

### Lỗi "Google Maps not loading"
→ Kiểm tra API Key đã được bật Maps JavaScript API
→ Kiểm tra billing đã được enable (Google yêu cầu dù là miễn phí)

---

## 📞 LIÊN HỆ NHÓM
- Leader: Hunter1995VN
- GitHub Repo: https://github.com/Hunter1995VN/SmartLocker_FE
