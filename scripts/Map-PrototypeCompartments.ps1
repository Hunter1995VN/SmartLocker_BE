param(
    [Parameter(Mandatory)][string]$SettingsPath,
    [Parameter(Mandatory)][Guid]$StationId,
    [Parameter(Mandatory)][string]$DeviceCode,
    [Parameter(Mandatory)][Guid]$MediumLockerId,
    [Parameter(Mandatory)][Guid]$LargeLockerId,
    [Guid]$SmallLockerId = [Guid]::Empty,
    [switch]$Apply
)
$ErrorActionPreference = 'Stop'
$cfg = Get-Content -LiteralPath $SettingsPath -Raw | ConvertFrom-Json
$cs = $cfg.ConnectionStrings.DefaultConnection
if (!$cs) { $cs = $cfg.ConnectionStrings.Default }
$db = [System.Data.SqlClient.SqlConnection]::new($cs)
try {
    $db.Open()
    $cmd = $db.CreateCommand()
    $cmd.CommandTimeout = 20
    $cmd.CommandText = @'
SET XACT_ABORT ON;
SET TRANSACTION ISOLATION LEVEL SERIALIZABLE;
BEGIN TRANSACTION;
DECLARE @device uniqueidentifier = (SELECT Id FROM IoTDevices WITH(UPDLOCK,HOLDLOCK) WHERE DeviceCode=@code AND StationId=@station);
IF @device IS NULL THROW 51000,'Device is not mapped to this station.',1;
DECLARE @targets TABLE(Id uniqueidentifier,Size nvarchar(1),Pin smallint);
INSERT @targets VALUES(@medium,'M',25),(@large,'L',33);
IF @small IS NOT NULL INSERT @targets VALUES(@small,'S',26);
IF (SELECT COUNT(*) FROM Lockers l WITH(UPDLOCK,HOLDLOCK) JOIN @targets t ON t.Id=l.Id AND t.Size=l.Size
    WHERE l.StationId=@station AND (l.IoTDeviceId IS NULL OR l.IoTDeviceId=@device)
    AND l.BusinessStatus='AVAILABLE' AND l.HealthStatus='HEALTHY') <> (SELECT COUNT(*) FROM @targets)
    THROW 51001,'Expected available, healthy, correctly sized lockers without another device mapping.',1;
IF EXISTS(SELECT 1 FROM Bookings b WITH(UPDLOCK,HOLDLOCK) JOIN @targets t ON t.Id=b.LockerId
    WHERE b.Status IN ('PENDING_PAYMENT','CONFIRMED','STORED'))
    THROW 51002,'An active booking owns a target locker; no changes applied.',1;
IF EXISTS(SELECT 1 FROM Lockers l JOIN @targets t ON t.Pin=l.GpioPin
    WHERE l.IoTDeviceId=@device AND l.Id<>t.Id)
    THROW 51003,'GPIO is already mapped to a different locker.',1;
SELECT 'BEFORE' AS Phase,l.Id,l.LockerCode,l.Size,l.IoTDeviceId,l.GpioPin,l.DoorState,l.LastDoorEventAt
FROM Lockers l JOIN @targets t ON t.Id=l.Id;
IF @apply=1
    UPDATE l SET IoTDeviceId=@device,GpioPin=t.Pin,
        DoorState=CASE WHEN l.IoTDeviceId IS NULL THEN 'UNKNOWN' ELSE l.DoorState END,
        LastDoorEventAt=CASE WHEN l.IoTDeviceId IS NULL THEN NULL ELSE l.LastDoorEventAt END,
        UpdatedAt=SYSUTCDATETIME()
    FROM Lockers l JOIN @targets t ON t.Id=l.Id;
SELECT CASE WHEN @apply=1 THEN 'APPLIED' ELSE 'READ_ONLY' END AS Phase,l.Id,l.LockerCode,l.Size,l.IoTDeviceId,l.GpioPin,l.DoorState,l.LastDoorEventAt
FROM Lockers l JOIN @targets t ON t.Id=l.Id;
COMMIT;
'@
    [void]$cmd.Parameters.AddWithValue('@station', $StationId)
    [void]$cmd.Parameters.AddWithValue('@code', $DeviceCode)
    [void]$cmd.Parameters.AddWithValue('@medium', $MediumLockerId)
    [void]$cmd.Parameters.AddWithValue('@large', $LargeLockerId)
    [void]$cmd.Parameters.AddWithValue('@small', $(if ($SmallLockerId -eq [Guid]::Empty) { [DBNull]::Value } else { $SmallLockerId }))
    [void]$cmd.Parameters.AddWithValue('@apply', [bool]$Apply)
    $reader = $cmd.ExecuteReader()
    do {
        while ($reader.Read()) {
            $row = [ordered]@{}
            for ($i=0; $i -lt $reader.FieldCount; $i++) {
                $row[$reader.GetName($i)] = if ($reader.IsDBNull($i)) { $null } else { $reader.GetValue($i) }
            }
            [pscustomobject]$row
        }
    } while ($reader.NextResult())
    $reader.Close()
} finally { $db.Dispose() }
