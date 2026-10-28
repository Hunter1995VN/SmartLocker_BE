#include "MqttTelemetry.h"

// =====================================================
// SMARTLOCKER ESP32 - 3 RELAYS + 3 LIMIT SWITCHES + MQTT
//
// Relays:
//   S = GPIO 26 (IN1)
//   M = GPIO 25 (IN2)
//   L = GPIO 33 (IN3)
//
// Limit Switches:
//   S = GPIO 27 (COM->GND, NO->GPIO 27)
//   M = GPIO 32 (COM->GND, NO->GPIO 32)
//   L = GPIO 23 (COM->GND, NO->GPIO 23)
//
// Serial debug:
//   1 = Mở khóa S
//   2 = Mở khóa M
//   3 = Mở khóa L
//
// Cửa đóng: LOW (GND)
// Cửa mở:   HIGH (Pull-up)
// =====================================================

const int LOCK_COUNT = 3;

const uint8_t RELAY_PINS[LOCK_COUNT] = {
  26, // Relay IN1 - Ngăn S
  25, // Relay IN2 - Ngăn M
  33  // Relay IN3 - Ngăn L
};

const char* LOCK_NAMES[LOCK_COUNT] = {
  "S",
  "M",
  "L"
};

// Module đặt jumper Low-Level Trigger
const bool RELAY_ACTIVE_LOW = true;

// Thời gian kích xung solenoid (500 ms rồi tự ngắt)
const unsigned long UNLOCK_TIME_MS = 500;

int activeLock = -1;
unsigned long relayStartedAt = 0;

// Cấu hình Limit Switch
const uint8_t DOOR_PINS[LOCK_COUNT] = {
  27, // Cửa S
  32, // Cửa M
  23  // Cửa L
};

const int DOOR_CLOSED_LEVEL = LOW;
const unsigned long DEBOUNCE_MS = 40;

int doorStableStates[LOCK_COUNT];
int doorLastReadings[LOCK_COUNT];
unsigned long doorChangedAt[LOCK_COUNT];

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
      // Gửi telemetry cho ngăn tương ứng
      updateMqttDoor(i, reading == DOOR_CLOSED_LEVEL);
    }
  }
}

void processSerialCommand(char command) {
  if (command == '\r' || command == '\n' || command == '\t' || command == ' ') return;

  if (command == '1') {
    startUnlock(0);
    return;
  }
  if (command == '2') {
    startUnlock(1);
    return;
  }
  if (command == '3') {
    startUnlock(2);
    return;
  }

  Serial.printf("INVALID SERIAL CMD: '%c' (Use 1=S, 2=M, 3=L)\n", command);
}

void setup() {
  Serial.begin(115200);
  delay(500);

  // Khởi tạo relay trạng thái tắt trước khi chuyển OUTPUT
  for (int i = 0; i < LOCK_COUNT; i++) {
    digitalWrite(RELAY_PINS[i], relayOffLevel());
    pinMode(RELAY_PINS[i], OUTPUT);
    writeRelay(i, false);
  }

  activeLock = -1;

  // Khởi tạo limit switches với PULLUP
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
  Serial.println("  Controls: S (GPIO 26), M (GPIO 25), L (GPIO 33)");
  Serial.println("  Sensors:  S (GPIO 27), M (GPIO 32), L (GPIO 23)");
  Serial.println("  Serial test: 1=S, 2=M, 3=L");
  Serial.println("=========================================");

  printAllDoorStates();

  // Đăng ký callback mở khóa cho MQTT
  setUnlockCallback(startUnlock);

  // Bắt đầu tác vụ MQTT nền
  startMqtt(
    doorStableStates[0] == DOOR_CLOSED_LEVEL,
    doorStableStates[1] == DOOR_CLOSED_LEVEL,
    doorStableStates[2] == DOOR_CLOSED_LEVEL
  );
}

void loop() {
  // Đọc lệnh test từ bàn phím Serial
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

  delay(1); // Nhường CPU cho các tác vụ RTOS khác
}
