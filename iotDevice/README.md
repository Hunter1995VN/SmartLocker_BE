# ESP32 door telemetry — Arduino IDE

Open `iotDevice.ino` with the other files in this folder. This sketch keeps the
existing S/M/L relay and switch pins and Serial commands 1/2/3. Only the S sensor
is mapped to MQTT for this first hardware test. It does not accept MQTT commands
or open a relay remotely.

## Configuration

Copy `iot_config.example.h` to `iot_config.h` if missing (the latter is ignored by
Git). Fill WIFI_SSID, WIFI_PASSWORD and MQTT_PASSWORD locally. Keep broker
192.168.1.7:1883 and username esp32 if those are still your LAN settings. Use
2.4 GHz Wi-Fi reachable from the broker computer, not an isolated guest network.

In Arduino IDE Library Manager, install **PubSubClient by Nick O'Leary** (2.8).
WiFi, time and FreeRTOS come with the Espressif ESP32 board package; do not install
the Arduino Uno WiFi library for this sketch. Select the same board and COM port
that successfully ran the original sketch, then Verify and Upload.

## Test

1. Keep the LAN Mosquitto broker and backend running. The Windows firewall must
   allow incoming TCP 1883 from the private LAN.
2. Open Serial Monitor at 115200 baud. Expected sequence:
   `WiFi connected`, `MQTT connected`, then `MQTT TX S: OPEN/CLOSED`.
3. If waiting for NTP, ensure Wi-Fi has internet access. UTC timestamps are
   required by the backend; the firmware waits instead of sending a 1970 date.
4. Press the S switch (GPIO 27 to GND): CLOSED. Release: OPEN. Existing debounce
   is 40 ms. Observe `MQTT door updated` in BE and the door sensor on admin
   Ben Thanh / SIM-MQTT-01 after the next UI poll (about five seconds).
5. Stop using Send-DoorEvent.ps1 for this locker while the board is publishing;
   otherwise both sources overwrite the same state.

Relay pins: S=26, M=25, L=33. Door pins: S=27, M=32, L=23. Relay active LOW,
500 ms pulse, one relay at a time. Network operations run in a separate task;
the original loop handles relay timeout and sensors. Validate pulse timing with
your hardware, including Wi-Fi loss, before relying on it operationally.

The firmware uses PubSubClient QoS 0 publishing, no retain. It republishes the
current state after reconnect and every 30 seconds to recover missed snapshots.
It is not a durable event history: intermediate offline transitions may be lost.
`MQTT TX` means publish returned success, not that SQL committed. Check BE/UI.
MQTT connect state -2 generally indicates transport connection failure; 4/5
indicate rejected credentials/authorization. No password is logged.

The shared SIM-MQTT-001 account/device mapping is for local testing. Per-device
broker ACLs, durable delivery, heartbeat and remote commands are future work.
