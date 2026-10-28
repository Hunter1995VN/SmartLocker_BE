param(
    [Parameter(Mandatory = $true)][string]$LanAddress,
    [Parameter(Mandatory = $true)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$ip = $null
if (-not [Net.IPAddress]::TryParse($LanAddress, [ref]$ip) -or
    $ip.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork -or
    $LanAddress -eq '0.0.0.0' -or $LanAddress.StartsWith('127.')) {
    throw 'Supply the IPv4 address of your LAN adapter.'
}
$target = [IO.Path]::GetFullPath($OutputDirectory)
if ($target -match '[^\x00-\x7F]') { throw 'Use an ASCII-only path; Mosquitto on Windows may reject accented paths.' }
New-Item -ItemType Directory -Path $target -Force | Out-Null
$configFile = Join-Path $target 'mosquitto-lan.conf'
if (Test-Path -LiteralPath $configFile) { throw 'Config already exists. Edit its LAN address or choose a new directory.' }
$config = @"
per_listener_settings true
listener 1883 127.0.0.1
allow_anonymous true
listener 1883 $LanAddress
allow_anonymous false
password_file mqtt-local.passwords
"@
[IO.File]::WriteAllText($configFile, $config, [Text.Encoding]::ASCII)
Write-Output "Created $configFile. Create the password file and launch Mosquitto FROM this directory."
