#include "MqttTelemetry.h"

// =====================================================
// SMARTLOCKER - TEST 3 RELAY + 3 LIMIT SWITCH
//
// Serial:
//   1 = Mo khoa ngan S
//   2 = Mo khoa ngan M
//   3 = Mo khoa ngan L
//
// Relay tu dong tat sau 500 ms.
//
// Limit switch:
//   COM → GND ESP32
//   NO  → GPIO tương ứng
//
// Cua dong nhan switch:
//   LOW  = CLOSED
//   HIGH = OPEN
// =====================================================

// -----------------------------------------------------
// Cấu hình relay
// -----------------------------------------------------

const int LOCK_COUNT = 3;

const uint8_t RELAY_PINS[LOCK_COUNT] = {
  26, // Relay IN1 - ngan S
  25, // Relay IN2 - ngan M
  33  // Relay IN3 - ngan L
};

const char* LOCK_NAMES[LOCK_COUNT] = {
  "S",
  "M",
  "L"
};

// Module đang đặt jumper Low-Com.
const bool RELAY_ACTIVE_LOW = true;

// Thời gian cấp điện cho khóa.
// Chỉ tăng sau khi kiểm tra thông số khóa.
const unsigned long UNLOCK_TIME_MS = 500;

// Relay đang hoạt động.
// -1 nghĩa là không có relay nào bật.
int activeLock = -1;

unsigned long relayStartedAt = 0;

// -----------------------------------------------------
// Cấu hình limit switch
// -----------------------------------------------------

const uint8_t DOOR_PINS[LOCK_COUNT] = {
  27, // Cua S
  32, // Cua M
  23  // Cua L
};

// Cửa đóng sẽ nhấn switch COM-NO xuống GND.
const int DOOR_CLOSED_LEVEL = LOW;

// Thời gian chống dội công tắc.
const unsigned long DEBOUNCE_MS = 40;

int doorStableStates[LOCK_COUNT];
int doorLastReadings[LOCK_COUNT];
unsigned long doorChangedAt[LOCK_COUNT];

// -----------------------------------------------------
// Hàm xác định mức ON/OFF của relay
// -----------------------------------------------------

int relayOnLevel() {
  return RELAY_ACTIVE_LOW ? LOW : HIGH;
}

int relayOffLevel() {
  return RELAY_ACTIVE_LOW ? HIGH : LOW;
}

// -----------------------------------------------------
// Điều khiển relay
// -----------------------------------------------------

void writeRelay(int index, bool turnOn) {
  if (index < 0 || index >= LOCK_COUNT) {
    return;
  }

  digitalWrite(
    RELAY_PINS[index],
    turnOn ? relayOnLevel() : relayOffLevel()
  );
}

void turnOffActiveRelay() {
  if (activeLock < 0) {
    return;
  }

  writeRelay(activeLock, false);

  Serial.print("LOCK ");
  Serial.print(LOCK_NAMES[activeLock]);
  Serial.println(": RELAY OFF");

  activeLock = -1;
}

void startUnlock(int index) {
  if (index < 0 || index >= LOCK_COUNT) {
    return;
  }

  // Không cho bật relay thứ hai khi một relay đang chạy.
  if (activeLock >= 0) {
    Serial.print("BUSY: LOCK ");
    Serial.print(LOCK_NAMES[activeLock]);
    Serial.println(" IS ACTIVE");
    return;
  }

  activeLock = index;
  relayStartedAt = millis();

  writeRelay(index, true);

  Serial.print("LOCK ");
  Serial.print(LOCK_NAMES[index]);
  Serial.println(": RELAY ON");

  Serial.print("DOOR ");
  Serial.print(LOCK_NAMES[index]);
  Serial.print(": ");

  if (doorStableStates[index] == DOOR_CLOSED_LEVEL) {
    Serial.println("CLOSED");
  } else {
    Serial.println("OPEN");
  }
}

// -----------------------------------------------------
// Hiển thị trạng thái cửa
// -----------------------------------------------------

void printDoorState(int index) {
  Serial.print("DOOR ");
  Serial.print(LOCK_NAMES[index]);
  Serial.print(": ");

  if (doorStableStates[index] == DOOR_CLOSED_LEVEL) {
    Serial.println("CLOSED");
  } else {
    Serial.println("OPEN");
  }
}

void printAllDoorStates() {
  Serial.println("----- DOOR STATUS -----");

  for (int i = 0; i < LOCK_COUNT; i++) {
    printDoorState(i);
  }

  Serial.println("-----------------------");
}

// -----------------------------------------------------
// Đọc và chống dội limit switch
// -----------------------------------------------------

void updateDoorSensors() {
  unsigned long now = millis();

  for (int i = 0; i < LOCK_COUNT; i++) {
    int reading = digitalRead(DOOR_PINS[i]);

    // Phát hiện tín hiệu vừa thay đổi.
    if (reading != doorLastReadings[i]) {
      doorLastReadings[i] = reading;
      doorChangedAt[i] = now;
    }

    // Chỉ công nhận trạng thái sau khi ổn định đủ lâu.
    if (
      reading != doorStableStates[i] &&
      now - doorChangedAt[i] >= DEBOUNCE_MS
    ) {
      doorStableStates[i] = reading;
      if (i == 0) updateMqttDoorS(reading == DOOR_CLOSED_LEVEL);
      printDoorState(i);
    }
  }
}

// -----------------------------------------------------
// Xử lý lệnh Serial
// -----------------------------------------------------

void processSerialCommand(char command) {
  // Bỏ qua Enter, xuống dòng, tab và khoảng trắng.
  if (
    command == '\r' ||
    command == '\n' ||
    command == '\t' ||
    command == ' '
  ) {
    return;
  }

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

  Serial.print("INVALID COMMAND: ");
  Serial.println(command);
}

// -----------------------------------------------------
// Khởi động
// -----------------------------------------------------

void setup() {
  Serial.begin(115200);
  delay(500);

  // Đặt relay về OFF trước khi chuyển chân thành OUTPUT.
  for (int i = 0; i < LOCK_COUNT; i++) {
    digitalWrite(RELAY_PINS[i], relayOffLevel());
    pinMode(RELAY_PINS[i], OUTPUT);
    writeRelay(i, false);
  }

  activeLock = -1;

  // Khởi tạo limit switch với pull-up nội.
  for (int i = 0; i < LOCK_COUNT; i++) {
    pinMode(DOOR_PINS[i], INPUT_PULLUP);

    int initialState = digitalRead(DOOR_PINS[i]);

    doorStableStates[i] = initialState;
    doorLastReadings[i] = initialState;
    doorChangedAt[i] = millis();
  }

  Serial.println();
  Serial.println("SMARTLOCKER HARDWARE TEST READY");
  Serial.println("1 = UNLOCK S");
  Serial.println("2 = UNLOCK M");
  Serial.println("3 = UNLOCK L");

  printAllDoorStates();
  startMqttTelemetry(doorStableStates[0] == DOOR_CLOSED_LEVEL);
}

// -----------------------------------------------------
// Vòng lặp chính
// -----------------------------------------------------

void loop() {
  // Đọc lệnh từ bàn phím.
  if (Serial.available() > 0) {
    char command = Serial.read();
    processSerialCommand(command);
  }

  // Relay phải tự tắt độc lập với trạng thái cửa.
  if (
    activeLock >= 0 &&
    millis() - relayStartedAt >= UNLOCK_TIME_MS
  ) {
    turnOffActiveRelay();
  }

  // Luôn theo dõi cảm biến cửa.
  updateDoorSensors();
  delay(1); // Yield to other tasks without blocking relay timing on network calls.
}
