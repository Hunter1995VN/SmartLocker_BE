# Hướng dẫn MQTT cho thành viên nhóm (Windows)

## 1. Lấy code và chuẩn bị

```powershell
git fetch origin
git switch --track origin/hunglqde180096/mqtt-integration
```

Nếu đã có nhánh local, dùng `git switch hunglqde180096/mqtt-integration`.
Cài .NET SDK 8, Mosquitto (đã test bản 2.1.2), Node phù hợp FE nếu muốn xem web.
Mỗi máy tự tạo `API/appsettings.Development.json` theo cấu hình dev của nhóm và
thêm section Mqtt bên dưới. Không commit connection string hoặc mật khẩu.
BE chạy các background job booking hiện có: dùng DB test được nhóm thống nhất.
Không chạy migration trên DB database-first dùng chung.

## 2. Test trên máy tính, chưa cần ESP32

Mở ba terminal. Thay đường dẫn Mosquitto theo máy bạn.

Terminal A, giữ broker chạy (chỉ một broker chiếm cổng 1883):

```powershell
& 'C:\Program Files\mosquitto\mosquitto.exe' -v
```

Terminal B, tại thư mục repo BE sau khi cấu hình DB và Mqtt:

```powershell
dotnet restore
dotnet run --project API --launch-profile API
```

Terminal C:

```powershell
& 'C:\Program Files\mosquitto\mosquitto_pub.exe' -h 127.0.0.1 -p 1883 -t 'smartlocker/test' -m 'Hello SmartLocker'
```

BE phải log `MQTT received: smartlocker/test -> Hello SmartLocker`.
Tại thư mục BE, thử cập nhật cửa:

```powershell
.\scripts\Send-DoorEvent.ps1 -State OPEN -MosquittoDirectory 'C:\Program Files\mosquitto'
.\scripts\Send-DoorEvent.ps1 -State CLOSED -MosquittoDirectory 'C:\Program Files\mosquitto'
```

Script tự tìm Mosquitto trong PATH/thư mục cài mặc định; tham số trên dùng khi cần.
Nếu PowerShell chặn script, chạy riêng tiến trình:
`powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Send-DoorEvent.ps1 -State OPEN`.

## 3. Dữ liệu test và FE

DB nhóm hiện có SIM-MQTT-001 / SIM-MQTT-01 tại Bến Thành; thông tin ID ở phần
Door events bên dưới. Clone code không tự tạo dữ liệu này. Nếu dùng DB khác,
tạo thiết bị và ô tủ test theo schema hiện có, đặt tủ DISABLED, cùng StationId,
và gán Lockers.IoTDeviceId đúng thiết bị. Truyền `-DeviceCode` và `-LockerId` cho
Send-DoorEvent.ps1, đồng thời cập nhật mapping firmware. Không tự tạo thêm tủ
trong DB chung. Không để nhiều máy/board cùng ghi vào một ô tủ test.

FE là repo riêng: `npm ci`, cấu hình `VITE_API_URL=http://localhost:5000`,
`npm run dev`. Xin tài khoản admin của nhóm, vào Lockers, chọn Bến Thành và
SIM-MQTT-01, quan sát Cảm biến cửa sau khoảng 5 giây. Giữ tủ DISABLED.
FE đọc HTTP API, không kết nối MQTT trực tiếp; polling đã có sẵn. Bản sửa lỗi
loading FE là thay đổi riêng, không nằm trong nhánh BE này.

## 4. Cho ESP32 kết nối qua LAN

Máy broker và ESP32 phải cùng mạng có thể truy cập nhau. Xem IPv4 máy bằng
`ipconfig`; IP 192.168.1.7 trong ví dụ chỉ là máy phát triển ban đầu.
Tại thư mục BE, tạo cấu hình riêng (thay IP và đường dẫn theo máy):

```powershell
.\scripts\New-MqttLanConfig.ps1 -LanAddress '192.168.1.7' -OutputDirectory 'C:\mqtt-smartlocker'
cd C:\mqtt-smartlocker
& 'C:\Program Files\mosquitto\mosquitto_passwd.exe' -c mqtt-local.passwords esp32
```

Nhập mật khẩu riêng hai lần. `-c` chỉ dùng lần tạo đầu vì sẽ ghi đè file cũ.
Chọn đường dẫn không dấu; Mosquitto Windows có thể lỗi với tên thư mục tiếng Việt.
Nếu không có quyền tạo ở C:\, chọn thư mục không dấu khác mà bạn có quyền ghi.
Dừng broker cũ bằng Ctrl+C rồi chạy tại chính thư mục cấu hình vừa tạo:

```powershell
& 'C:\Program Files\mosquitto\mosquitto.exe' -c .\mosquitto-lan.conf -v
```

Listener localhost cho BE/script không yêu cầu mật khẩu. Listener IP LAN yêu cầu
tài khoản. File mosquitto-lan.conf ở gốc repo chỉ là ví dụ, không dùng nguyên IP
trên máy khác. Không đưa file password vào Git.

Nếu firewall chặn thiết bị, trên mạng Private tin cậy, dùng PowerShell admin
(thay LocalAddress theo máy):

```powershell
New-NetFirewallRule -DisplayName 'SmartLocker MQTT LAN' -Direction Inbound -Action Allow -Protocol TCP -LocalPort 1883 -LocalAddress 192.168.1.7 -RemoteAddress LocalSubnet -Profile Private
```

Chỉ tạo rule một lần. Mạng guest/AP isolation có thể chặn board dù rule đã đúng.
Kiểm tra TCP bằng `Test-NetConnection 192.168.1.7 -Port 1883`; test trên cùng máy
không chứng minh firewall đã cho phép ESP32 từ bên ngoài.

## 5. Nạp firmware bằng Arduino IDE

Mở iotDevice/iotDevice.ino, cài ESP32 board package và PubSubClient 2.8 của
Nick O'Leary. Đã chạy thực tế với ESP32 Dev Module, core ESP32 3.2.0.
Copy iot_config.example.h thành iot_config.h; tự điền Wi-Fi 2.4 GHz, mật khẩu
Wi-Fi, MQTT_HOST là IP LAN máy broker, username/password MQTT. Không commit file này.
Chọn board/COM đúng, Verify rồi Upload; Serial Monitor 115200 baud.

Thứ tự log: WiFi connected -> MQTT connected -> MQTT TX S.
NTP cần internet để có timestamp UTC hợp lệ. Nhấn công tắc S (GPIO 27) -> CLOSED;
thả -> OPEN. BE log `MQTT door updated`, FE cập nhật trong lần polling tiếp theo.
Không chạy script giả lập đồng thời với ESP32 cho cùng ô tủ.
Serial 1/2/3 vẫn kích relay S/M/L 500 ms; gửi từng số cách nhau ít nhất một giây.
MQTT hiện chỉ báo cảm biến S, chưa điều khiển mở khóa từ web, chưa heartbeat.

## 6. Xử lý lỗi nhanh

- Port in use: broker/service khác đang chiếm 1883; dùng một instance.
- MQTT state 4/5: kiểm tra tài khoản/mật khẩu và password file broker.
- MQTT state -2: kiểm tra IP, broker, firewall, cùng LAN.
- WiFi disconnected: kiểm tra SSID/password/băng tần và reason trong Serial.
- Waiting for NTP: kiểm tra internet/DNS trên Wi-Fi.
- Door ignored: kiểm tra mapping DB và timestamp; không đổi mã thiết bị tùy ý.
- Script lỗi đường dẫn: dùng -MosquittoDirectory; file config/password broker ở đường dẫn không dấu.
- IP LAN đổi sau khi chuyển Wi-Fi: cập nhật cấu hình broker, firmware và firewall.

## Chi tiết kỹ thuật và cấu hình BE

The backend uses MQTTnet 4.3.3.952. `smartlocker/test` only logs test messages.
Door event topics update door state in the database; no physical lock is operated.

Merge this section into `API/appsettings.Development.json`, preserving existing settings:

```json
"Mqtt": {
  "Enabled": true,
  "Host": "127.0.0.1",
  "Port": 1883,
  "Topic": "smartlocker/test",
  "ReconnectDelaySeconds": 5
}
```

MQTT is disabled by default when this section is absent. Client IDs are generated
per process to avoid collisions. Optional `Username` and `Password` belong only in
local configuration or environment variables. This configuration is for loopback
development, not a public broker.

1. Keep Mosquitto running (`.\mosquitto.exe -v` from its installation directory).
   If a broker service already owns port 1883, use that instance instead.
2. From the BE repository, run `dotnet run --project API --launch-profile API`.
3. Wait for `MQTT connected to 127.0.0.1:1883; subscribed to smartlocker/test`.
4. From the Mosquitto directory, publish:

```powershell
.\mosquitto_pub.exe -h 127.0.0.1 -p 1883 -t "smartlocker/test" -m "Hello SmartLocker"
```

The BE log should contain `MQTT received: smartlocker/test -> Hello SmartLocker`.
Only send test data on this topic: its payload is logged (up to 1024 characters).

Stop and restart the broker to check reconnection. The service retries every five
seconds and subscribes again after connecting. Messages sent while disconnected
are not replayed: this smoke test uses a clean session and non-retained messages.
Restart the BE after configuration changes. Normal BE startup also runs its
existing booking background jobs.

## Door events: MQTT -> database -> admin UI

The subscriber also listens to `smartlocker/devices/+/door` (option `Mqtt:DoorEventTopic`).
Example payload on `smartlocker/devices/SIM-MQTT-001/door`:

```json
{
  "schemaVersion": 1,
  "eventId": "a90ff45a-e9e2-43f1-bf1e-64672541b466",
  "deviceCode": "SIM-MQTT-001",
  "lockerId": "7590fdc1-6057-4764-96dc-bfe3fc7142d3",
  "doorState": "OPEN",
  "occurredAt": "2026-10-12T08:00:00Z"
}
```

Use a fresh eventId and UTC timestamp for each new event. Retries must preserve
the original timestamp. Only OPEN/CLOSED are accepted. Retained, malformed,
over-4096-byte, older-than-24-hour and more-than-2-minute-future messages are ignored.
The device in the topic must match the payload and the locker's IoTDeviceId and
StationId. An atomic conditional update rejects timestamps less than or equal to
LastDoorEventAt. EventId is logged, not persisted as an event ledger.

The approved simulator is SIM-MQTT-001 / SIM-MQTT-01 at SmartLocker Ben Thanh
(station 22222222-2222-2222-2222-222222222222). The locker is DISABLED so it cannot
be booked. Its business status is independent of the physical door state.

From the BE repository, with broker and updated BE running:

```powershell
.\scripts\Send-DoorEvent.ps1 -State OPEN
.\scripts\Send-DoorEvent.ps1 -State CLOSED
```

Log confirmation: `MQTT door updated: locker ... -> OPEN/CLOSED`.
In the admin realtime locker grid, select Ben Thanh and SIM-MQTT-01 (show all or
disabled lockers). The existing five-second polling refreshes the door sensor
display in the detail panel. The customer booking page is not this monitoring view.

This is trusted local simulation: matching a device code is not authentication.
Before accepting real devices over a network, configure broker credentials and
topic ACLs per device. No heartbeat or online status is derived from door events.
Database processing failures are logged; a durable inbox/retry queue is not yet
implemented. This stage is for local integration testing.
