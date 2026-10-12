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
bool doorClosed = false;
uint32_t stateRevision = 0;

void telemetryTask(void*) {
  if (!strcmp(WIFI_SSID, "YOUR_WIFI_NAME") || !strcmp(MQTT_PASSWORD, "YOUR_MQTT_PASSWORD")) {
    Serial.println("MQTT: fill in iot_config.h first; hardware Serial test remains available.");
    vTaskDelete(nullptr);
    return;
  }

  WiFiClient network;
  PubSubClient mqtt(network);
  mqtt.setServer(MQTT_HOST, MQTT_PORT);
  mqtt.setSocketTimeout(2);
  if (!mqtt.setBufferSize(512)) {
    Serial.println("MQTT: buffer allocation failed.");
    vTaskDelete(nullptr);
    return;
  }
  WiFi.mode(WIFI_STA);
  // One retry owner: do not race automatic reconnect with manual begin().
  WiFi.setAutoReconnect(false);
  WiFi.onEvent([](WiFiEvent_t event, WiFiEventInfo_t info) {
    if (event == ARDUINO_EVENT_WIFI_STA_DISCONNECTED) {
      Serial.printf("WiFi disconnected: reason=%u\n", info.wifi_sta_disconnected.reason);
    }
  });
  Serial.println("WiFi: connecting (allow 30 seconds)...");
  WiFi.begin(WIFI_SSID, WIFI_PASSWORD);
  configTime(0, 0, "pool.ntp.org", "time.google.com");

  char clientId[64];
  const uint64_t chipId = ESP.getEfuseMac();
  snprintf(clientId, sizeof(clientId), "smartlocker-esp32-%08lx%08lx",
           (unsigned long)(chipId >> 32), (unsigned long)chipId);
  char topic[160];
  snprintf(topic, sizeof(topic), "smartlocker/devices/%s/door", DEVICE_CODE);
  uint32_t lastWifiAttempt = millis();
  uint32_t lastMqttAttempt = millis() - 5000;
  uint32_t lastPublish = 0;
  uint32_t lastNotice = millis() - 10000;
  uint32_t sentRevision = 0;
  bool sent = false;
  bool wifiReported = false;

  for (;;) {
    // All potentially blocking network calls stay off the relay/sensor loop.
    const uint32_t now = millis();
    if (WiFi.status() != WL_CONNECTED) {
      wifiReported = false;
      sent = false;
      if (mqtt.connected()) mqtt.disconnect();
      if (now - lastWifiAttempt >= 30000) {
        Serial.printf("WiFi: attempt timed out, status=%d; restarting connection.\n", WiFi.status());
        // Cancel the old attempt before starting another. Relay timing stays
        // in the independent hardware loop during this network-task delay.
        WiFi.disconnect(false, false);
        vTaskDelay(pdMS_TO_TICKS(250));
        WiFi.begin(WIFI_SSID, WIFI_PASSWORD);
        lastWifiAttempt = millis();
      }
      vTaskDelay(pdMS_TO_TICKS(100));
      continue;
    }
    if (!wifiReported) {
      Serial.print("WiFi connected: ");
      Serial.println(WiFi.localIP());
      wifiReported = true;
    }
    lastWifiAttempt = now;
    if (!mqtt.connected()) {
      sent = false;
      if (now - lastMqttAttempt >= 5000) {
        lastMqttAttempt = now;
        if (mqtt.connect(clientId, MQTT_USERNAME, MQTT_PASSWORD)) {
          Serial.println("MQTT connected; telemetry S ready (no remote unlock).");
        } else {
          Serial.printf("MQTT connect failed, state=%d; retry in 5s.\n", mqtt.state());
        }
      }
      vTaskDelay(pdMS_TO_TICKS(100));
      continue;
    }
    mqtt.loop();

    struct timeval tv;
    gettimeofday(&tv, nullptr);
    if (tv.tv_sec < 1767225600) { // Wait for NTP instead of sending an invalid 1970 date.
      if (now - lastNotice >= 10000) {
        Serial.println("MQTT: waiting for NTP time; check WiFi internet access.");
        lastNotice = now;
      }
      vTaskDelay(pdMS_TO_TICKS(100));
      continue;
    }
    bool closed;
    uint32_t revision;
    portENTER_CRITICAL(&stateMux);
    closed = doorClosed;
    revision = stateRevision;
    portEXIT_CRITICAL(&stateMux);

    // Republish the current snapshot every 30s so a BE restart recovers state.
    // Offline intermediate transitions are not stored; latest sensor state wins.
    if (!sent || revision != sentRevision || now - lastPublish >= 30000) {
      struct tm utc;
      const time_t seconds = tv.tv_sec;
      gmtime_r(&seconds, &utc);
      char date[24], timestamp[32], eventId[37], payload[400];
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
      const int length = snprintf(payload, sizeof(payload),
        "{\"schemaVersion\":1,\"eventId\":\"%s\",\"deviceCode\":\"%s\","
        "\"lockerId\":\"%s\",\"doorState\":\"%s\",\"occurredAt\":\"%s\"}",
        eventId, DEVICE_CODE, LOCKER_S_ID, closed ? "CLOSED" : "OPEN", timestamp);
      if (length > 0 && length < (int)sizeof(payload) && mqtt.publish(topic, payload, false)) {
        Serial.printf("MQTT TX S: %s (check BE for DB confirmation)\n", closed ? "CLOSED" : "OPEN");
        sentRevision = revision;
        sent = true;
        lastPublish = now;
      } else {
        Serial.println("MQTT publish failed; retrying.");
        vTaskDelay(pdMS_TO_TICKS(1000));
      }
    }
    vTaskDelay(pdMS_TO_TICKS(100));
  }
}
}

void updateMqttDoorS(bool closed) {
  portENTER_CRITICAL(&stateMux);
  if (doorClosed != closed) {
    doorClosed = closed;
    ++stateRevision;
  }
  portEXIT_CRITICAL(&stateMux);
}

void startMqttTelemetry(bool closed) {
  updateMqttDoorS(closed);
  if (xTaskCreate(telemetryTask, "mqtt-telemetry", 8192, nullptr, 1, nullptr) != pdPASS)
    Serial.println("MQTT: task creation failed; hardware Serial test remains available.");
}
