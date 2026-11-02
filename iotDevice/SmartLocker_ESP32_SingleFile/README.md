# Three-compartment ESP32 sketch

Open SmartLocker_ESP32_SingleFile.ino in this folder. Board: ESP32 Dev Module (esp32:esp32:esp32); tested with Espressif core 3.2.0 and PubSubClient. Use only one sketch entry point.

Copy iot_config.example.h to iot_config.h here and supply the actual local Wi-Fi/broker credentials. The real header and compiled firmware contain secrets and are Git-ignored. MQTT_HOST must match the Wi-Fi IPv4 of the broker computer on the same LAN. Do not paste passwords into the .ino or documentation.

| Size | Relay GPIO | Door GPIO | Database mapping in this prototype |
|---|---:|---:|---|
| S | 26 | 27 | SIM-MQTT-01 |
| M | 25 | 32 | BT-M-01 |
| L | 33 | 23 | BT-L-01 |

Each LOCKER_*_ID must equal the real database GUID of the corresponding compartment, mapped to DEVICE_CODE at the same station. Routing uses exact GUIDs and rejects unknown/duplicate mappings. Locker code/size labels alone do not authorize an unlock. Doors use INPUT_PULLUP: switch to GND is CLOSED, released is OPEN. Relay active LOW, 500ms pulse, one relay at a time.

Start the local broker from the repository's start-mqtt-broker.ps1. It reads only the ignored local header, binds loopback plus the configured LAN address, authenticates LAN device clients, and restricts device topics. Start the API with MQTT enabled and the same broker; never simulate heartbeats or door events to pass acceptance.

Verify and upload only after retaining a valid pre-update backup. If Windows/Arduino tools cannot handle Unicode workspace paths, build in a temporary ASCII directory containing this sketch and its local header. Do not commit flash dumps or copied headers.

After upload, verify fresh ONLINE heartbeat and actual door events for all three GUIDs through API/shared SQL. For each paid order, check that only the correct relay activates and only real OPEN→CLOSED events advance the booking. A successful upload/MQTT connection is not proof that the complete business workflow passed. See docs/UC-T11-T12-MULTISIZE-VALIDATION.md for the acceptance checklist and current limits.
