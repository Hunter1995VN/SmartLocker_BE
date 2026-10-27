#include <Arduino.h>
#include <WiFi.h>
#include <PubSubClient.h>
#include <esp_system.h>
#include <sys/time.h>
#include <time.h>
#include "MqttTelemetry.h"
#include "iot_config.h"

namespace {
portMUX_TYPE stateMux = portMUX_INITIALIZER_UNLOCKED;
bool doorStates[3] = { false, false, false };
uint32_t stateRevisions[3] = { 0, 0, 0 };
UnlockCallback unlockCb = nullptr;

const char* LOCKER_IDS[3] = {
  LOCKER_S_ID,
  LOCKER_M_ID,
  LOCKER_L_ID
};

const char* LOCKER_CODES[3] = { "S", "M", "L" };

// Deduplication buffer for request IDs
char seenRequestIds[8][40];
int seenReqIndex = 0;

bool isDuplicateRequest(const char* reqId) {
  if (!reqId || strlen(reqId) == 0) return false;
  for (int i = 0; i < 8; i++) {
    if (strcmp(seenRequestIds[i], reqId) == 0) {
      return true;
    }
  }
  strncpy(seenRequestIds[seenReqIndex], reqId, sizeof(seenRequestIds[0]) - 1);
  seenRequestIds[seenReqIndex][sizeof(seenRequestIds[0]) - 1] = '\0';
  seenReqIndex = (seenReqIndex + 1) % 8;
  return false;
}

// Simple JSON string finder
bool extractJsonField(const char* json, const char* field, char* outVal, size_t maxLen) {
  char pattern[64];
  snprintf(pattern, sizeof(pattern), "\"%s\"", field);
  const char* pos = strstr(json, pattern);
  if (!pos) return false;

  pos = strchr(pos + strlen(pattern), ':');
  if (!pos) return false;
  pos++; // skip ':'

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
    if (strcasecmp(code, "S") == 0 || strstr(code, "-S") != nullptr || strstr(code, "SIM-MQTT-01") != nullptr) targetIndex = 0;
    else if (strcasecmp(code, "M") == 0 || strstr(code, "-M") != nullptr) targetIndex = 1;
    else if (strcasecmp(code, "L") == 0 || strstr(code, "-L") != nullptr) targetIndex = 2;
    else {
      // Check by GUID
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

void telemetryTask(void*) {
  if (!strcmp(WIFI_SSID, "YOUR_WIFI_NAME") || !strcmp(MQTT_PASSWORD, "YOUR_MQTT_PASSWORD")) {
    Serial.println("MQTT: Fill in iot_config.h with your Wi-Fi & MQTT credentials.");
    vTaskDelete(nullptr);
    return;
  }

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

    // 1. Maintain Wi-Fi
    if (WiFi.status() != WL_CONNECTED) {
      if (mqtt.connected()) mqtt.disconnect();
      if (now - lastWifiAttempt >= 20000) {
        Serial.printf("WiFi: Disconnected (status=%d), reconnecting...\n", WiFi.status());
        WiFi.disconnect(false, false);
        vTaskDelay(pdMS_TO_TICKS(250));
        WiFi.begin(WIFI_SSID, WIFI_PASSWORD);
        lastWifiAttempt = millis();
      }
      vTaskDelay(pdMS_TO_TICKS(100));
      continue;
    }

    lastWifiAttempt = now;

    // 2. Maintain MQTT connection & Subscribe to Command Topic
    if (!mqtt.connected()) {
      if (now - lastMqttAttempt >= 5000) {
        lastMqttAttempt = now;
        Serial.printf("MQTT: Connecting to %s:%d...\n", MQTT_HOST, MQTT_PORT);
        if (mqtt.connect(clientId, MQTT_USERNAME, MQTT_PASSWORD)) {
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

    // 3. NTP Time check (UTC)
    struct timeval tv;
    gettimeofday(&tv, nullptr);
    if (tv.tv_sec < 1700000000) { // Check that epoch has advanced beyond year 2023
      if (now - lastNotice >= 10000) {
        Serial.println("MQTT: Waiting for NTP time sync...");
        lastNotice = now;
      }
      vTaskDelay(pdMS_TO_TICKS(100));
      continue;
    }

    // 4. Check door state updates for all 3 lockers (S, M, L)
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

    // 5. Periodic Heartbeat every 30 seconds
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
} // namespace

void setUnlockCallback(UnlockCallback cb) {
  unlockCb = cb;
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

void startMqtt(bool doorSClosed, bool doorMClosed, bool doorLClosed) {
  doorStates[0] = doorSClosed;
  doorStates[1] = doorMClosed;
  doorStates[2] = doorLClosed;
  xTaskCreatePinnedToCore(telemetryTask, "MqttTask", 6144, nullptr, 1, nullptr, 0);
}
