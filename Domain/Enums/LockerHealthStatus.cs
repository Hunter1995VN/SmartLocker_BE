namespace Domain.Enums;

public enum LockerHealthStatus
{
    HEALTHY,
    WARNING,
    CRITICAL,
    SENSOR_ERROR,
    LOCK_ERROR,
    DOOR_STUCK,
    MAINTENANCE
}
