/**
 * SmartLocker ESP32 Hardware Simulator
 * Mô phỏng bo mạch ESP32 thời gian thực cho trạm SmartLocker Sân Bay Quốc Tế Đà Nẵng (SIM-MQTT-001)
 * Hỗ trợ:
 * - Gửi Heartbeat định kỳ 10s: Uptime tăng dần, RSSI dao động thực tế theo Wi-Fi, trạng thái cửa.
 * - Lắng nghe lệnh REBOOT (UC-S09): Phản hồi ack và mô phỏng restart phần cứng.
 * - Lắng nghe lệnh SELF_TEST (UC-S07): Kích xung Relay S/M/L, đọc cảm biến cửa và gửi diagnostics report.
 */
import mqtt from 'mqtt';

const BROKER_URL = process.env.MQTT_URL || 'mqtt://127.0.0.1:1883';
const DEVICE_CODE = process.env.DEVICE_CODE || 'SIM-MQTT-001';

console.log('================================================================');
console.log('🤖 SMARTLOCKER ESP32 HARDWARE SIMULATOR (IoT Lead: Minh)');
console.log(`📡 Broker MQTT: ${BROKER_URL}`);
console.log(`🆔 Device Code: ${DEVICE_CODE}`);
console.log('================================================================\n');

let uptime = 120;
let wifiRssi = -68;
const doorStates = {
  doorS: 'CLOSED',
  doorM: 'CLOSED',
  doorL: 'CLOSED'
};

const client = mqtt.connect(BROKER_URL, {
  clientId: `esp32-sim-${Math.random().toString(16).substring(2, 8)}`,
  username: 'esp32',
  password: 'khongcopass',
  reconnectPeriod: 3000
});

const cmdTopic = `smartlocker/devices/${DEVICE_CODE}/cmd`;
const heartbeatTopic = `smartlocker/devices/${DEVICE_CODE}/heartbeat`;
const diagnosticsTopic = `smartlocker/devices/${DEVICE_CODE}/diagnostics`;

client.on('connect', () => {
  console.log(`✅ [ESP32] Đã kết nối thành công tới MQTT Broker tại ${BROKER_URL}`);
  client.subscribe(cmdTopic, (err) => {
    if (err) console.error(`❌ Subscribe error:`, err);
    else console.log(`📥 [ESP32] Đã đăng ký lắng nghe lệnh điều khiển: ${cmdTopic}`);
  });

  // Gửi heartbeat ngay lập tức khi khởi động
  sendHeartbeat();
});

client.on('error', (err) => {
  console.error(`⚠️ [ESP32] Lỗi kết nối MQTT: ${err.message}`);
});

function sendHeartbeat() {
  uptime += 10;
  // Dao động nhẹ sóng Wi-Fi (-63 đến -71 dBm) giống phần cứng thực tế
  wifiRssi = -67 + Math.floor(Math.random() * 7) - 3;

  const payload = {
    deviceCode: DEVICE_CODE,
    status: 'ONLINE',
    uptime: uptime,
    rssi: wifiRssi,
    ...doorStates
  };

  client.publish(heartbeatTopic, JSON.stringify(payload), { qos: 1 }, (err) => {
    if (err) {
      console.error(`❌ [Heartbeat] Thất bại: ${err.message}`);
    } else {
      console.log(`💓 [Heartbeat Sent] Uptime=${uptime}s (${Math.floor(uptime/60)}p${uptime%60}s) | RSSI=${wifiRssi}dBm | Cửa: [S:${doorStates.doorS}, M:${doorStates.doorM}, L:${doorStates.doorL}]`);
    }
  });
}

// Định kỳ gửi heartbeat mỗi 10 giây
const heartbeatTimer = setInterval(sendHeartbeat, 10000);

client.on('message', (topic, message) => {
  if (topic === cmdTopic) {
    try {
      const data = JSON.parse(message.toString());
      console.log(`\n================================================================`);
      console.log(`🔔 [LỆNH ĐIỀU KHIỂN TỪ BACKEND] => Action: ${data.action}`);
      console.log(`================================================================`);

      if (data.action === 'REBOOT') {
        console.log(`🔄 [UC-S09] Nhận lệnh REBOOT từ xa!`);
        console.log(`⏳ Đang lưu cấu hình NVRAM và gọi esp_restart()...`);
        setTimeout(() => {
          uptime = 0;
          console.log(`⚡ [ESP32] Khởi động lại thành công! Uptime reset về 0s.`);
          sendHeartbeat();
        }, 2000);
      } else if (data.action === 'UNLOCK') {
        console.log(`🔓 [UC-S02 / UC-S12] Nhận lệnh MỞ KHÓA Ô TỦ: LockerCode='${data.lockerCode}', LockerId='${data.lockerId}'`);
        console.log(`⚡ Kích xung Solenoid Relay (500ms)... Cửa ${data.lockerCode} đã bung mở!`);
        const doorEventTopic = `smartlocker/devices/${DEVICE_CODE}/door`;
        const doorEvent = {
          schemaVersion: 1,
          eventId: 'sim-evt-' + Date.now(),
          deviceCode: DEVICE_CODE,
          lockerId: data.lockerId,
          doorState: 'OPEN',
          occurredAt: new Date().toISOString()
        };
        client.publish(doorEventTopic, JSON.stringify(doorEvent), { qos: 1 });
        console.log(`📢 [Telemetry] Cảm biến hành trình báo cửa ô ${data.lockerCode} [OPEN]`);
      } else if (data.action === 'SELF_TEST') {
        console.log(`🧪 [UC-S07] Bắt đầu chu trình tự chẩn đoán phần cứng (Self-Test):`);
        console.log(`   [1/3] Kích xung Relay Ngăn S (GPIO 26)... [OK - 200ms]`);
        console.log(`   [2/3] Kích xung Relay Ngăn M (GPIO 25)... [OK - 200ms]`);
        console.log(`   [3/3] Kích xung Relay Ngăn L (GPIO 33)... [OK - 200ms]`);
        console.log(`   [Cảm biến] Đọc công tắc hành trình cửa (Limit Switch): S=CLOSED, M=CLOSED, L=CLOSED`);

        setTimeout(() => {
          const diagResult = {
            schemaVersion: 1,
            deviceCode: DEVICE_CODE,
            status: 'HEALTHY',
            durationMs: 760,
            relays: { S: 'OK (200ms)', M: 'OK (200ms)', L: 'OK (200ms)' },
            doors: { S: doorStates.doorS, M: doorStates.doorM, L: doorStates.doorL },
            wifiRssi: wifiRssi,
            freeHeapBytes: 192512,
            uptimeSeconds: uptime,
            timestamp: new Date().toISOString()
          };

          client.publish(diagnosticsTopic, JSON.stringify(diagResult), { qos: 1 }, (err) => {
            if (err) {
              console.error(`❌ [Diagnostics] Lỗi publish: ${err.message}`);
            } else {
              console.log(`📊 [UC-S07] Đã gửi báo cáo chẩn đoán lên '${diagnosticsTopic}' thành công!`);
            }
          });
        }, 800);
      }
    } catch (e) {
      console.error(`❌ Lỗi phân tích gói tin lệnh: ${e.message}`);
    }
  }
});

process.on('SIGINT', () => {
  clearInterval(heartbeatTimer);
  client.end(true, () => process.exit(0));
});
