# ESP32 firmware entry points

Use `SmartLocker_ESP32_SingleFile/SmartLocker_ESP32_SingleFile.ino` and the instructions in that folder for the current three-compartment firmware. It supports S/M/L relay commands, exact database GUID routing, door telemetry and heartbeat.

The modular alternative is `iotDevice.ino` together with `MqttTelemetry.cpp/.h`. It shares `SmartLocker_ESP32_SingleFile/LockerRouting.h`; copy `iot_config.example.h` to the ignored `iot_config.h` and keep IDs/device configuration consistent with SQL. Standalone legacy sketches use `.disabled` extensions so Arduino does not compile duplicate `setup`/`loop` functions.

Never commit real Wi-Fi/MQTT credentials or firmware/flash backups. Do not run synthetic door-event scripts against physical compartments during real acceptance. See `../docs/UC-T11-T12-MULTISIZE-VALIDATION.md` for tested behavior and remaining paid M/L acceptance.