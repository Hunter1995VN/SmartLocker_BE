@echo off
title SmartLocker MQTT Broker (Port 1883)
echo ========================================================
echo   SMARTLOCKER MQTT BROKER RUNNING ON PORT 1883
echo   Host IP: 172.20.10.2 (0.0.0.0:1883)
echo ========================================================
npx aedes-cli -p 1883 --host 0.0.0.0 -v
pause
