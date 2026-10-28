// ==============================================================================
//   SMARTLOCKER ESP32 FIRMWARE - SINGLE FILE ALL-IN-ONE (KHÔNG CẦN CÁC FILE .H/.CPP)
//   Dành cho SmartLocker Bến Thành - Trạm SIM-MQTT-001 (3 Tủ: S, M, L)
// ==============================================================================
#include <Arduino.h>
#include <WiFi.h>
#include <PubSubClient.h>
#include <esp_system.h>
#include <sys/time.h>
#include <time.h>

// ------------------------------------------------------------------------------
// 1. CẤU HÌNH WI-FI & MQTT BROKER
// ------------------------------------------------------------------------------
static const char WIFI_SSID[]       = "T u n a s M i n h";
static const char WIFI_PASSWORD[]   = "khongcopass";
static const char MQTT_HOST[]       = "172.20.10.2";
static const unsigned short MQTT_PORT = 1883;
static const char MQTT_USERNAME[]   = "esp32";
static const char MQTT_PASSWORD[]   = "khongcopass";

static const char DEVICE_CODE[]     = "SIM-MQTT-001";
static const char LOCKER_S_ID[]     = "7590fdc1-6057-4764-96dc-bfe3fc7142d3";
static const char LOCKER_M_ID[]     = "86a1edc2-7168-5875-a7ed-cfe4fc8253e4";
static const char LOCKER_L_ID[]     = "97b2fec3-8279-6986-b8fe-dfe5fc9364f5";

// ------------------------------------------------------------------------------
// 2. CẤU HÌNH PHẦN CỨNG (3 RƠ-LE & 3 CẢM BIẾN HÀNH TRÌNH)
// ------------------------------------------------------------------------------
const int LOCK_COUNT = 3;

// Chân kích Relay (Kích LOW: cuộn hút đóng, mở khóa solenoid)
const uint8_t RELAY_PINS[LOCK_COUNT] = {
  26, // Ngăn S (IN1)
  25, // Ngăn M (IN2)
  33  // Ngăn L (IN3)
};

const char* LOCK_NAMES[LOCK_COUNT] = { "S", "M", "L" };
const char* LOCKER_IDS[LOCK_COUNT] = { LOCKER_S_ID, LOCKER_M_ID, LOCKER_L_ID };
const char* LOCKER_CODES[LOCK_COUNT] = { "S", "M", "L" };

// Chân công tắc hành trình cửa (Limit Switch, chân COM nối GND, NO nối GPIO)
const uint8_t DOOR_PINS[LOCK_COUNT] = {
  27, // Cửa S
  32, // Cửa M
  23  // Cửa L
};

const bool RELAY_ACTIVE_LOW = true;
const unsigned long UNLOCK_TIME_MS = 500; // Xung kích mở khóa 500ms
const int DOOR_CLOSED_LEVEL = LOW;         // Chạm GND = Cửa Đóng, Nhả = Cửa Mở
const unsigned long DEBOUNCE_MS = 40;

int activeLock = -1;
unsigned long relayStartedAt = 0;
int doorStableStates[LOCK_COUNT];
int doorLastReadings[LOCK_COUNT];
unsigned long doorChangedAt[LOCK_COUNT];

// ------------------------------------------------------------------------------
// 3. BIẾN ĐỒNG BỘ TRẠNG THÁI CỬA MQTT (FreeRTOS Multi-Core)
// ------------------------------------------------------------------------------
portMUX_TYPE stateMux = portMUX_INITIALIZER_UNLOCKED;
bool doorStates[3] = { false, false, false };
uint32_t stateRevisions[3] = { 0, 0, 0 };

typedef void (*UnlockCallback)(int lockIndex);
UnlockCallback unlockCb = nullptr;

// Khai báo hàm nguyên mẫu
void startUnlock(int index);
void updateMqttDoor(int index, bool closed);

// ------------------------------------------------------------------------------
// 4. TIỆN ÍCH PHÂN TÍCH JSON ĐƠN GIẢN
// ------------------------------------------------------------------------------
char seenRequestIds[8][40];
int seenReqIndex = 0;

bool isDuplicateRequest(const char* reqId) {
  if (!reqId || strlen(reqId) == 0) return false;
  for (int i = 0; i < 8; i++) {
    if (strcmp(seenRequestIds[i], reqId) == 0) return true;
  }
  strncpy(seenRequestIds[seenReqIndex], reqId, sizeof(seenRequestIds[0]) - 1);
  seenRequestIds[seenReqIndex][sizeof(seenRequestIds[0]) - 1] = '\0';
  seenReqIndex = (seenReqIndex + 1) % 8;
  return false;
}

bool extractJsonField(const char* json, const char* field, char* outVal, size_t maxLen) {
  char pattern[64];
  snprintf(pattern, sizeof(pattern), "\"%s\"", field);
  const char* pos = strstr(json, pattern);
  if (!pos) return false;

  pos = strchr(pos + strlen(pattern), ':');
  if (!pos) return false;
  pos++;

  while (*pos == ' ' || *pos == '\t' || *pos == '\r' || *pos == '\n') pos++;

  bool isQuoted = (*pos == '\"');
  if (isQuoted) pos++;

  size_t idx = 0;
  while (*pos && idx < maxLen - 1) {
    if (isQuoted && *pos == '\"') break;
    if (!isQuoted && (*pos == ',' || *pos == '}' || *pos == ' ' || *pos == '\n' || *pos == '\r')) break;
    outVal[idx++] = *pos++;
  }
  outVal[idx] = '\0';
  return idx > 0;
}

// ------------------------------------------------------------------------------
// 5. XỬ LÝ LỆNH TỪ BACKEND QUA MQTT (UNLOCK TỦ)
// ------------------------------------------------------------------------------
void mqttMessageCallback(char* topic, byte* payload, unsigned int length) {
  if (length > 512) length = 512;
  char buf[513];
  memcpy(buf, payload, length);
  buf[length] = '\0';

  Serial.printf("\n[MQTT RX] Topic: %s\nPayload: %s\n", topic, buf);

  char command[32] = {0};
  extractJsonField(buf, "command", command, sizeof(command));

  if (strcasecmp(command, "UNLOCK") == 0) {
    char reqId[40] = {0};
    extractJsonField(buf, "requestId", reqId, sizeof(reqId));

    if (isDuplicateRequest(reqId)) {
      Serial.printf("MQTT UNLOCK: duplicate requestId '%s' ignored.\n", reqId);
      return;
    }

    char code[32] = {0};
    extractJsonField(buf, "lockerCode", code, sizeof(code));

    char lockerId[40] = {0};
    extractJsonField(buf, "lockerId", lockerId, sizeof(lockerId));

    int targetIndex = -1;
    // Khớp mã tủ: "S", "BT-S-01", "SIM-MQTT-01" -> Ngăn 0
    if (strcasecmp(code, "S") == 0 || strstr(code, "-S") != nullptr || strstr(code, "SIM-MQTT-01") != nullptr) {
      targetIndex = 0;
    } else if (strcasecmp(code, "M") == 0 || strstr(code, "-M") != nullptr) {
      targetIndex = 1;
    } else if (strcasecmp(code, "L") == 0 || strstr(code, "-L") != nullptr) {
      targetIndex = 2;
    } else {
      // Khớp theo GUID
      for (int i = 0; i < 3; i++) {
        if (strcasecmp(lockerId, LOCKER_IDS[i]) == 0) {
          targetIndex = i;
          break;
        }
      }
    }

    if (targetIndex >= 0 && targetIndex < 3) {
      Serial.printf(">>> EXECUTING REMOTE UNLOCK FOR LOCKER %s (index %d) <<<\n", LOCKER_CODES[targetIndex], targetIndex);
      if (unlockCb) {
        unlockCb(targetIndex);
      }
    } else {
      Serial.printf("MQTT UNLOCK: unknown lockerCode='%s' / lockerId='%s'\n", code, lockerId);
    }
  }
}

// ------------------------------------------------------------------------------
// 6. TÁC VỤ NỀN MQTT TRÊN CORE 0 (FREERTOS)
// ------------------------------------------------------------------------------
void telemetryTask(void*) {
  WiFiClient network;
  PubSubClient mqtt(network);
  mqtt.setServer(MQTT_HOST, MQTT_PORT);
  mqtt.setSocketTimeout(3);
  mqtt.setCallback(mqttMessageCallback);

  if (!mqtt.setBufferSize(768)) {
    Serial.println("MQTT: Buffer allocation failed.");
    vTaskDelete(nullptr);
    return;
  }

  WiFi.mode(WIFI_STA);
  WiFi.setAutoReconnect(false);
  Serial.println("WiFi: Connecting to LAN...");
  WiFi.begin(WIFI_SSID, WIFI_PASSWORD);
  configTime(0, 0, "pool.ntp.org", "time.google.com");

  char clientId[64];
  const uint64_t chipId = ESP.getEfuseMac();
  snprintf(clientId, sizeof(clientId), "smartlocker-esp32-%08lx%08lx",
           (unsigned long)(chipId >> 32), (unsigned long)chipId);

  char cmdTopic[128];
  snprintf(cmdTopic, sizeof(cmdTopic), "smartlocker/devices/%s/cmd", DEVICE_CODE);

  char doorTopic[128];
  snprintf(doorTopic, sizeof(doorTopic), "smartlocker/devices/%s/door", DEVICE_CODE);

  char heartbeatTopic[128];
  snprintf(heartbeatTopic, sizeof(heartbeatTopic), "smartlocker/devices/%s/heartbeat", DEVICE_CODE);

  uint32_t lastWifiAttempt = millis();
  uint32_t lastMqttAttempt = millis() - 5000;
  uint32_t lastHeartbeat = 0;
  uint32_t lastNotice = millis() - 10000;
  uint32_t sentRevisions[3] = { 0, 0, 0 };
  bool initialPublishDone[3] = { false, false, false };

  for (;;) {
    const uint32_t now = millis();

    // 1. Duy trì Wi-Fi
    if (WiFi.status() != WL_CONNECTED) {
      if (mqtt.connected()) mqtt.disconnect();
      if (now - lastWifiAttempt >= 20000) {
        Serial.printf("WiFi: Disconnected, reconnecting to %s...\n", WIFI_SSID);
        WiFi.disconnect(false, false);
        vTaskDelay(pdMS_TO_TICKS(250));
        WiFi.begin(WIFI_SSID, WIFI_PASSWORD);
        lastWifiAttempt = millis();
      }
      vTaskDelay(pdMS_TO_TICKS(100));
      continue;
    }

    lastWifiAttempt = now;

    // 2. Duy trì kết nối MQTT
    if (!mqtt.connected()) {
      if (now - lastMqttAttempt >= 5000) {
        lastMqttAttempt = now;
        Serial.printf("MQTT: Connecting to %s:%d...\n", MQTT_HOST, MQTT_PORT);
        bool ok = false;
        if (strlen(MQTT_USERNAME) > 0 && strlen(MQTT_PASSWORD) > 0 && strcmp(MQTT_USERNAME, "none") != 0) {
          ok = mqtt.connect(clientId, MQTT_USERNAME, MQTT_PASSWORD);
        }
        if (!ok) {
          ok = mqtt.connect(clientId);
        }
        if (ok) {
          Serial.println("MQTT: Connected! Subscribing to command topic...");
          mqtt.subscribe(cmdTopic, 1);
          Serial.printf("MQTT: Subscribed to '%s'\n", cmdTopic);
          for (int i = 0; i < 3; i++) initialPublishDone[i] = false;
        } else {
          Serial.printf("MQTT: Connect failed, rc=%d. Retry in 5s.\n", mqtt.state());
        }
      }
      vTaskDelay(pdMS_TO_TICKS(100));
      continue;
    }

    mqtt.loop();

    // 3. Kiểm tra đồng bộ NTP Time
    struct timeval tv;
    gettimeofday(&tv, nullptr);
    if (tv.tv_sec < 1700000000) {
      if (now - lastNotice >= 10000) {
        Serial.println("MQTT: Waiting for NTP time sync...");
        lastNotice = now;
      }
      vTaskDelay(pdMS_TO_TICKS(100));
      continue;
    }

    // 4. Bắn sự kiện cửa thay đổi (CLOSED / OPEN) cho từng ngăn tủ
    for (int i = 0; i < 3; i++) {
      bool closed;
      uint32_t rev;
      portENTER_CRITICAL(&stateMux);
      closed = doorStates[i];
      rev = stateRevisions[i];
      portEXIT_CRITICAL(&stateMux);

      if (!initialPublishDone[i] || rev != sentRevisions[i]) {
        struct tm utc;
        const time_t seconds = tv.tv_sec;
        gmtime_r(&seconds, &utc);
        char date[24], timestamp[32], eventId[37], payload[380];
        strftime(date, sizeof(date), "%Y-%m-%dT%H:%M:%S", &utc);
        snprintf(timestamp, sizeof(timestamp), "%s.%03ldZ", date, (long)(tv.tv_usec / 1000));

        uint8_t uuid[16];
        esp_fill_random(uuid, sizeof(uuid));
        uuid[6] = (uuid[6] & 0x0f) | 0x40;
        uuid[8] = (uuid[8] & 0x3f) | 0x80;
        snprintf(eventId, sizeof(eventId),
          "%02x%02x%02x%02x-%02x%02x-%02x%02x-%02x%02x-%02x%02x%02x%02x%02x%02x",
          uuid[0],uuid[1],uuid[2],uuid[3],uuid[4],uuid[5],uuid[6],uuid[7],
          uuid[8],uuid[9],uuid[10],uuid[11],uuid[12],uuid[13],uuid[14],uuid[15]);

        snprintf(payload, sizeof(payload),
          "{\"schemaVersion\":1,\"eventId\":\"%s\",\"deviceCode\":\"%s\","
          "\"lockerId\":\"%s\",\"doorState\":\"%s\",\"occurredAt\":\"%s\"}",
          eventId, DEVICE_CODE, LOCKER_IDS[i], closed ? "CLOSED" : "OPEN", timestamp);

        if (mqtt.publish(doorTopic, payload, false)) {
          Serial.printf("[MQTT TX] Door %s (%s): %s\n", LOCKER_CODES[i], LOCKER_IDS[i], closed ? "CLOSED" : "OPEN");
          sentRevisions[i] = rev;
          initialPublishDone[i] = true;
        } else {
          Serial.printf("[MQTT TX FAIL] Door %s publish failed.\n", LOCKER_CODES[i]);
        }
      }
    }

    // 5. Heartbeat định kỳ mỗi 30 giây
    if (now - lastHeartbeat >= 30000) {
      char hbPayload[256];
      snprintf(hbPayload, sizeof(hbPayload),
        "{\"deviceCode\":\"%s\",\"status\":\"ONLINE\",\"uptime\":%lu,\"rssi\":%d,"
        "\"doorS\":\"%s\",\"doorM\":\"%s\",\"doorL\":\"%s\"}",
        DEVICE_CODE, (unsigned long)(now / 1000), WiFi.RSSI(),
        doorStates[0] ? "CLOSED" : "OPEN",
        doorStates[1] ? "CLOSED" : "OPEN",
        doorStates[2] ? "CLOSED" : "OPEN");

      mqtt.publish(heartbeatTopic, hbPayload, false);
      Serial.printf("[HEARTBEAT] Sent status ONLINE, uptime=%lus\n", (unsigned long)(now / 1000));
      lastHeartbeat = now;
    }

    vTaskDelay(pdMS_TO_TICKS(100));
  }
}

void updateMqttDoor(int index, bool closed) {
  if (index < 0 || index >= 3) return;
  portENTER_CRITICAL(&stateMux);
  if (doorStates[index] != closed) {
    doorStates[index] = closed;
    ++stateRevisions[index];
  }
  portEXIT_CRITICAL(&stateMux);
}

// ------------------------------------------------------------------------------
// 7. CÁC HÀM ĐIỀU KHIỂN RƠ-LE & CẢM BIẾN
// ------------------------------------------------------------------------------
int relayOnLevel() {
  return RELAY_ACTIVE_LOW ? LOW : HIGH;
}

int relayOffLevel() {
  return RELAY_ACTIVE_LOW ? HIGH : LOW;
}

void writeRelay(int index, bool turnOn) {
  if (index < 0 || index >= LOCK_COUNT) return;
  digitalWrite(RELAY_PINS[index], turnOn ? relayOnLevel() : relayOffLevel());
}

void turnOffActiveRelay() {
  if (activeLock < 0) return;
  writeRelay(activeLock, false);
  Serial.printf("LOCK %s: RELAY OFF (solenoid cut)\n", LOCK_NAMES[activeLock]);
  activeLock = -1;
}

void startUnlock(int index) {
  if (index < 0 || index >= LOCK_COUNT) return;

  if (activeLock >= 0) {
    Serial.printf("BUSY: Lock %s is already activating.\n", LOCK_NAMES[activeLock]);
    return;
  }

  activeLock = index;
  relayStartedAt = millis();
  writeRelay(index, true);

  Serial.printf(">>> LOCK %s: RELAY ON (500ms pulse) <<<\n", LOCK_NAMES[index]);
}

void printDoorState(int index) {
  Serial.printf("DOOR %s: %s\n", LOCK_NAMES[index], doorStableStates[index] == DOOR_CLOSED_LEVEL ? "CLOSED" : "OPEN");
}

void printAllDoorStates() {
  Serial.println("----- SMARTLOCKER STATUS -----");
  for (int i = 0; i < LOCK_COUNT; i++) {
    printDoorState(i);
  }
  Serial.println("------------------------------");
}

void updateDoorSensors() {
  unsigned long now = millis();

  for (int i = 0; i < LOCK_COUNT; i++) {
    int reading = digitalRead(DOOR_PINS[i]);

    if (reading != doorLastReadings[i]) {
      doorLastReadings[i] = reading;
      doorChangedAt[i] = now;
    }

    if (reading != doorStableStates[i] && (now - doorChangedAt[i] >= DEBOUNCE_MS)) {
      doorStableStates[i] = reading;
      printDoorState(i);
      updateMqttDoor(i, reading == DOOR_CLOSED_LEVEL);
    }
  }
}

void processSerialCommand(char command) {
  if (command == '\r' || command == '\n' || command == '\t' || command == ' ') return;
  if (command == '1') { startUnlock(0); return; }
  if (command == '2') { startUnlock(1); return; }
  if (command == '3') { startUnlock(2); return; }
  Serial.printf("INVALID SERIAL CMD: '%c' (Use 1=S, 2=M, 3=L)\n", command);
}

// ------------------------------------------------------------------------------
// 8. SETUP & LOOP
// ------------------------------------------------------------------------------
void setup() {
  Serial.begin(115200);
  delay(500);

  // Khởi tạo relay
  for (int i = 0; i < LOCK_COUNT; i++) {
    digitalWrite(RELAY_PINS[i], relayOffLevel());
    pinMode(RELAY_PINS[i], OUTPUT);
    writeRelay(i, false);
  }
  activeLock = -1;

  // Khởi tạo cảm biến cửa
  for (int i = 0; i < LOCK_COUNT; i++) {
    pinMode(DOOR_PINS[i], INPUT_PULLUP);
    int initial = digitalRead(DOOR_PINS[i]);
    doorStableStates[i] = initial;
    doorLastReadings[i] = initial;
    doorChangedAt[i] = millis();
  }

  Serial.println();
  Serial.println("=========================================");
  Serial.println("  SMARTLOCKER ESP32 FIRMWARE READY");
  Serial.println("  Station: SIM-MQTT-001 (Ben Thanh)");
  Serial.println("  Relays:  S (GPIO 26), M (GPIO 25), L (GPIO 33)");
  Serial.println("  Sensors: S (GPIO 27), M (GPIO 32), L (GPIO 23)");
  Serial.println("=========================================");

  printAllDoorStates();

  unlockCb = startUnlock;

  doorStates[0] = (doorStableStates[0] == DOOR_CLOSED_LEVEL);
  doorStates[1] = (doorStableStates[1] == DOOR_CLOSED_LEVEL);
  doorStates[2] = (doorStableStates[2] == DOOR_CLOSED_LEVEL);

  // Khởi chạy tác vụ MQTT trên Core 0
  xTaskCreatePinnedToCore(telemetryTask, "MqttTask", 6144, nullptr, 1, nullptr, 0);
}

void loop() {
  // Test bằng bàn phím Serial Monitor: gõ 1, 2, hoặc 3 rồi Enter
  if (Serial.available() > 0) {
    char cmd = Serial.read();
    processSerialCommand(cmd);
  }

  // Tự động ngắt relay sau 500ms
  if (activeLock >= 0 && (millis() - relayStartedAt >= UNLOCK_TIME_MS)) {
    turnOffActiveRelay();
  }

  // Quét cảm biến cửa
  updateDoorSensors();

  delay(1);
}
