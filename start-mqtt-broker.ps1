Write-Host "========================================================" -ForegroundColor Cyan
Write-Host "  SMARTLOCKER MQTT BROKER RUNNING ON PORT 1883" -ForegroundColor Green
Write-Host "  Host IP: 172.20.10.2 (Listening on 0.0.0.0:1883)" -ForegroundColor Yellow
Write-Host "========================================================" -ForegroundColor Cyan
npx aedes-cli -p 1883 --host 0.0.0.0 -v
