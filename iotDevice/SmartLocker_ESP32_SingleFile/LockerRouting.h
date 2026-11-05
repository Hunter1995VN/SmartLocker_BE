#pragma once
#include <string.h>
#include <strings.h>

// A database GUID is authoritative. Never infer a relay from substrings such as
// '-M' in 'SIM-MQTT-01', or fall back to S when identity is missing/unknown.
inline int resolveLockerIndex(const char* lockerId, const char* const lockerIds[3]) {
  if (!lockerId || strlen(lockerId) != 36) return -1;
  int match = -1;
  for (int i = 0; i < 3; ++i) {
    if (lockerIds[i] && strcasecmp(lockerId, lockerIds[i]) == 0) {
      if (match >= 0) return -1; // Invalid duplicate mapping: fail closed.
      match = i;
    }
  }
  return match;
}
