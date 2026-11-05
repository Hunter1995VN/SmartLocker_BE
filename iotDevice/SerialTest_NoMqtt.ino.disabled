// =========================================================================
// SMARTLOCKER - FIRMWARE TEST OFFLINE QUA SERIAL (KHÔNG CẦN WIFI / MQTT)
// =========================================================================
// Hướng dẫn sử dụng:
// 1. Nạp code này vào ESP32 (hoặc board Arduino).
// 2. Mở Serial Monitor (Baud rate: 115200, chọn "Both NL & CR" hoặc "No line ending").
// 3. Nhập phím vào ô gõ Serial và nhấn Enter:
//    - Phím '1': Mở khóa ngăn S (GPIO 26) - Kích xung 500ms rồi tự ngắt
//    - Phím '2': Mở khóa ngăn M (GPIO 25) - Kích xung 500ms rồi tự ngắt
//    - Phím '3': Mở khóa ngăn L (GPIO 33) - Kích xung 500ms rồi tự ngắt
//    - Phím 's' hoặc '?': Kiểm tra trạng thái đóng/mở của cả 3 cảm biến cửa
//
// Sơ đồ chân (ESP32):
// - Relay Solenoid:
//     S = GPIO 26 (Relay IN1)
//     M = GPIO 25 (Relay IN2)
//     L = GPIO 33 (Relay IN3)
// - Cảm biến cửa (Công tắc hành trình / Cảm biến từ):
//     S = GPIO 27 (Chân COM nối GND, chân NO nối GPIO 27)
//     M = GPIO 32 (Chân COM nối GND, chân NO nối GPIO 32)
//     L = GPIO 23 (Chân COM nối GND, chân NO nối GPIO 23)
// =========================================================================

const int LOCK_COUNT = 3;

// Chân điều khiển Relay kích mở khóa Solenoid
const uint8_t RELAY_PINS[LOCK_COUNT] = {
  26, // Ngăn S (IN1)
  25, // Ngăn M (IN2)
  33  // Ngăn L (IN3)
};

// Chân đọc cảm biến hành trình / cảm biến từ báo trạng thái cửa
const uint8_t DOOR_PINS[LOCK_COUNT] = {
  27, // Cảm biến cửa S
  32, // Cảm biến cửa M
  23  // Cảm biến cửa L
};

const char* LOCK_NAMES[LOCK_COUNT] = { "S", "M", "L" };

// Hầu hết module relay 5V/12V trên thị trường là kích mức THẤP (Active LOW)
// Nếu dùng relay kích mức CAO (Active HIGH), hãy đổi thành false
const bool RELAY_ACTIVE_LOW = true;

// Thời gian cấp điện cho khóa Solenoid (500ms).
// LƯU Ý: Khóa solenoid điện từ nếu ngâm điện quá lâu sẽ bị nóng và cháy cuộn dây,
// nên code tự động ngắt điện sau 500ms.
const unsigned long UNLOCK_PULSE_MS = 500;

// Trạng thái cửa:
// - Đóng: Công tắc nối chân GPIO xuống GND (mức LOW)
// - Mở:   Hở mạch, chân GPIO được kéo lên 3.3V (mức HIGH)
const int DOOR_CLOSED_LEVEL = LOW;
const unsigned long DEBOUNCE_MS = 50;

// Biến quản lý đóng/ngắt xung relay
int activeLock = -1;
unsigned long relayTimer = 0;

// Biến chống rung (debounce) cảm biến cửa
int doorStableStates[LOCK_COUNT];
int doorLastReadings[LOCK_COUNT];
unsigned long doorDebounceTimer[LOCK_COUNT];

int relayOnLevel() {
  return RELAY_ACTIVE_LOW ? LOW : HIGH;
}

int relayOffLevel() {
  return RELAY_ACTIVE_LOW ? HIGH : LOW;
}

void setRelay(int index, bool turnOn) {
  if (index < 0 || index >= LOCK_COUNT) return;
  digitalWrite(RELAY_PINS[index], turnOn ? relayOnLevel() : relayOffLevel());
}

void triggerUnlock(int index) {
  if (index < 0 || index >= LOCK_COUNT) return;

  if (activeLock >= 0) {
    Serial.print("[CANH BAO] Dang mo ngan ");
    Serial.print(LOCK_NAMES[activeLock]);
    Serial.println(", vui long doi xong nhip truoc!");
    return;
  }

  activeLock = index;
  relayTimer = millis();
  setRelay(index, true);

  Serial.println();
  Serial.print(">>> [MO KHOA NGAN ");
  Serial.print(LOCK_NAMES[index]);
  Serial.print(" (GPIO ");
  Serial.print(RELAY_PINS[index]);
  Serial.println(")] -> DANG CAP DIEN SOLENOID (500ms)... <<<");
}

void cutOffRelay() {
  if (activeLock < 0) return;
  setRelay(activeLock, false);
  Serial.print("<<< [DA NGAT DIEN NGAN ");
  Serial.print(LOCK_NAMES[activeLock]);
  Serial.println("] -> Chot dien da nha an toan. <<<");
  Serial.println();
  activeLock = -1;
}

void printSingleDoor(int index) {
  bool isClosed = (doorStableStates[index] == DOOR_CLOSED_LEVEL);
  Serial.print("  - Cua [");
  Serial.print(LOCK_NAMES[index]);
  Serial.print("] (GPIO ");
  Serial.print(DOOR_PINS[index]);
  Serial.print("): ");
  if (isClosed) {
    Serial.println("[ DANG DONG ] (CLOSED - Chot da sập)");
  } else {
    Serial.println("[ DANG MO ]   (OPEN   - Canh cua dang mo)");
  }
}

void printAllDoors() {
  Serial.println("==========================================");
  Serial.println("       TRANG THAI CAC NGAN TU HIEN TAI    ");
  Serial.println("==========================================");
  for (int i = 0; i < LOCK_COUNT; i++) {
    printSingleDoor(i);
  }
  Serial.println("==========================================");
}

void checkDoorSensors() {
  unsigned long now = millis();

  for (int i = 0; i < LOCK_COUNT; i++) {
    int currentReading = digitalRead(DOOR_PINS[i]);

    if (currentReading != doorLastReadings[i]) {
      doorLastReadings[i] = currentReading;
      doorDebounceTimer[i] = now;
    }

    if ((now - doorDebounceTimer[i]) >= DEBOUNCE_MS) {
      if (currentReading != doorStableStates[i]) {
        doorStableStates[i] = currentReading;
        Serial.println();
        Serial.print("[SU KIEN CAM BIEN] ");
        printSingleDoor(i);
      }
    }
  }
}

void processCommand(char c) {
  if (c == '\r' || c == '\n' || c == ' ' || c == '\t') return;

  switch (c) {
    case '1':
    case 's':
    case 'S':
      triggerUnlock(0);
      break;

    case '2':
    case 'm':
    case 'M':
      triggerUnlock(1);
      break;

    case '3':
    case 'l':
    case 'L':
      triggerUnlock(2);
      break;

    case '?':
      printAllDoors();
      break;

    default:
      Serial.println();
      Serial.print("[LENH KHONG HOP LE]: '");
      Serial.print(c);
      Serial.println("'. Vui long chi nhap: 1 (S), 2 (M), 3 (L), hoac ? de xem trang thai.");
      break;
  }
}

void setup() {
  Serial.begin(115200);
  delay(1000); // Cho mach on dinh nguon

  // 1. Khoi tao Relay (Tranh bat nham relay luc khoi dong)
  for (int i = 0; i < LOCK_COUNT; i++) {
    digitalWrite(RELAY_PINS[i], relayOffLevel());
    pinMode(RELAY_PINS[i], OUTPUT);
    digitalWrite(RELAY_PINS[i], relayOffLevel());
  }

  // 2. Khoi tao Cam bien cua voi dien tro keo len INPUT_PULLUP
  for (int i = 0; i < LOCK_COUNT; i++) {
    pinMode(DOOR_PINS[i], INPUT_PULLUP);
    int initialVal = digitalRead(DOOR_PINS[i]);
    doorStableStates[i] = initialVal;
    doorLastReadings[i] = initialVal;
    doorDebounceTimer[i] = millis();
  }

  // In Menu huong dan
  Serial.println();
  Serial.println("==========================================================");
  Serial.println("  SMARTLOCKER - CHE DO TEST OFFLINE (KHONG DUNG MQTT/WIFI)");
  Serial.println("==========================================================");
  Serial.println("  Ket noi chan:");
  Serial.println("    - Relay IN1 (Ngan S) : GPIO 26");
  Serial.println("    - Relay IN2 (Ngan M) : GPIO 25");
  Serial.println("    - Relay IN3 (Ngan L) : GPIO 33");
  Serial.println("    - Cam bien cua S     : GPIO 27 (INPUT_PULLUP)");
  Serial.println("    - Cam bien cua M     : GPIO 32 (INPUT_PULLUP)");
  Serial.println("    - Cam bien cua L     : GPIO 23 (INPUT_PULLUP)");
  Serial.println("----------------------------------------------------------");
  Serial.println("  HUONG DAN LENH SERIAL:");
  Serial.println("    Gõ '1' va Enter -> Mo khoa ngan S (xung 500ms)");
  Serial.println("    Gõ '2' va Enter -> Mo khoa ngan M (xung 500ms)");
  Serial.println("    Gõ '3' va Enter -> Mo khoa ngan L (xung 500ms)");
  Serial.println("    Gõ '?' va Enter -> Xem trang thai 3 cua dong/mo");
  Serial.println("==========================================================");
  Serial.println();

  printAllDoors();
}

void loop() {
  // 1. Doc lenh tu ban phim Serial
  if (Serial.available() > 0) {
    char cmd = Serial.read();
    processCommand(cmd);
  }

  // 2. Tu dong ngat dien Relay sau UNLOCK_PULSE_MS (500ms) de bao ve cuon hut
  if (activeLock >= 0 && (millis() - relayTimer >= UNLOCK_PULSE_MS)) {
    cutOffRelay();
  }

  // 3. Quet cam bien hanh trinh cua
  checkDoorSensors();

  delay(2);
}
