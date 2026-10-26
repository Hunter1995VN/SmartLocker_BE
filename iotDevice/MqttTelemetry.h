#pragma once

typedef void (*UnlockCallback)(int lockIndex);

void setUnlockCallback(UnlockCallback cb);
void startMqtt(bool doorSClosed, bool doorMClosed, bool doorLClosed);
void updateMqttDoor(int index, bool closed);
