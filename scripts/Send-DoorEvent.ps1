param(
    [ValidateSet('OPEN', 'CLOSED')][string]$State = 'CLOSED',
    [string]$MosquittoDirectory = '',
    [string]$DeviceCode = 'SIM-MQTT-001',
    [guid]$LockerId = '7590fdc1-6057-4764-96dc-bfe3fc7142d3'
)

$ErrorActionPreference = 'Stop'
# Keep script source ASCII-compatible for Windows PowerShell 5.1.
if (-not $MosquittoDirectory) {
    $installed = Get-Command mosquitto_pub.exe -ErrorAction SilentlyContinue
    if ($installed) { $MosquittoDirectory = Split-Path $installed.Source }
    else {
        foreach ($candidate in @((Join-Path $env:ProgramFiles 'mosquitto'), ('D:\K' + [char]0x1EF3 + ' 9 -\Mosquitto'))) {
            if (Test-Path -LiteralPath (Join-Path $candidate 'mosquitto_pub.exe')) { $MosquittoDirectory = $candidate; break }
        }
    }
}
if (-not $MosquittoDirectory) { throw 'Pass -MosquittoDirectory with your Mosquitto installation directory.' }
if ($DeviceCode -notmatch '^[A-Za-z0-9_-]+$') { throw 'Invalid DeviceCode.' }
$publisher = Join-Path $MosquittoDirectory 'mosquitto_pub.exe'
if (-not (Test-Path -LiteralPath $publisher -PathType Leaf)) {
    throw "mosquitto_pub.exe not found. Pass -MosquittoDirectory with your installation directory."
}
$message = @{
    schemaVersion = 1
    eventId = [guid]::NewGuid().ToString()
    deviceCode = $DeviceCode
    lockerId = $LockerId.ToString()
    doorState = $State
    occurredAt = [DateTimeOffset]::UtcNow.ToString('o')
} | ConvertTo-Json -Compress

# A file avoids PowerShell/Windows native argument quoting changing the JSON.
$payloadFile = [IO.Path]::GetTempFileName()
try {
    [IO.File]::WriteAllText($payloadFile, $message, (New-Object Text.UTF8Encoding($false)))
    & $publisher -h 127.0.0.1 -p 1883 -q 1 -t "smartlocker/devices/$DeviceCode/door" -f $payloadFile
    if ($LASTEXITCODE -ne 0) { throw "MQTT publish failed: $LASTEXITCODE" }
    Write-Output "Published $State for locker $LockerId. Check BE log and admin locker view for DB confirmation."
} finally {
    Remove-Item -LiteralPath $payloadFile -ErrorAction SilentlyContinue
}
