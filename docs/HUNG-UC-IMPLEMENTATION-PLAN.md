# Ke hoach hoan thien 10 UC cua Hung

Ngay: 2026-10-29. Nhanh goc: hunglqde180096/uc-t09 (BE va FE).

## Cach trien khai
- Giu nguyen thay doi firmware va docs/Report5 co san.
- Tai su dung co chon loc hung/ten_uc; khong merge nguyen nhanh vi se xoa tinh nang cua thanh vien khac.
- Tach commit/nhanh theo UC; commit schema dung chung rieng.
- PR vao dev theo thu tu phu thuoc; khong tu merge PR.

## Thu tu va tieu chi chap nhan
1. T09: PIN 8 so, QR het han 60s, tu cap nhat tren FE; khong lo ve chua thanh toan/da thu hoi.
2. Shared: schema delegation/review/lost/walk-in, session kiosk; script SQL rieng, khong tu chay tren DB that.
3. T11: xac thuc ve/PIN that, don da thanh toan, dung tram/size/tu khoe; MQTT fail khong bao thanh cong; OPEN -> CLOSED moi STORED.
4. T12: chi STORED, tinh no theo pricing, chan unlock khi con no; OPEN -> CLOSED moi COMPLETED/AVAILABLE, thu hoi ve.
5. S02: AVAILABLE/OCCUPIED/RESERVED/MAINTENANCE; SignalR, reconnect va polling.
6. S08: anh JPEG tu camera, session hop le, cho staff duyet; khong dong don ngay khi gui anh.
7. T15: khach vang lai, SDT/size/duration, thanh toan qua gateway hien co, session bi mat, chi cap ve sau thanh toan.
8. T16: PIN uy quyen 8 so, mot lan, het han/thu hoi, dung tram, khong vuot phi.
9. T17: bao quen trong 7 ngay, anh va staff response; khong chiem tu cua khach tiep theo.
10. T21: 1-5 sao, CLEAN/FAST/EASY_TO_FIND, chi COMPLETED, khong trung.
11. T23: VI/EN/JA/KO/ZH tren toan bo luong kiosk, reset session.

## Kiem thu
- dotnet test tests/Hung.UseCases.Tests/Hung.UseCases.Tests.csproj
- pnpm build; lint lien quan, kiem tra component/API khi du dieu kien.
- Test tich hop booking/payment/ticket/check-in/storage/check-out/support.
- ESP32: can thiet bi that de xac nhan solenoid va cam bien; test mock khong duoc ghi la hardware PASS.
- Chi push va tao PR sau khi check dat; ghi ro check chua chay va loi con lai.

## Phat hien ban dau
- Nhanh T09 thieu support API/UI; co tren hung/ten_uc.
- Kiosk T09 con bypass demo va chua doi chieu nonce/han QR.
- Build FE pass; BE can chay ngoai sandbox do MSBuild bi han che.
- Chua hoan thanh dot trien khai; cap nhat ket qua khi co bang chung.
