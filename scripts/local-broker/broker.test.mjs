import assert from 'node:assert/strict';
import test from 'node:test';
import { createLocalBroker } from './broker.mjs';

test('LAN credentials and topic ACL isolate the device from API command publishing', async () => {
  const broker = createLocalBroker({ username: 'unit-device', password: 'unit-secret', deviceCode: 'UNIT-ESP32' });
  try {
    const client = { conn: { remoteAddress: '192.0.2.10' } };
    const authenticate = (user, pass) => new Promise((resolve, reject) => broker.authenticate(client, user, Buffer.from(pass), (error, allowed) => error ? reject(error) : resolve(allowed)));
    assert.equal(await authenticate('unit-device', 'wrong'), false);
    assert.equal(await authenticate('unit-device', 'unit-secret'), true);
    const publish = (topic, retain = false) => new Promise(resolve => broker.authorizePublish(client, { topic, retain }, error => resolve(!error)));
    const subscribe = topic => new Promise(resolve => broker.authorizeSubscribe(client, { topic, qos: 0 }, (error, subscription) => resolve(!error && !!subscription)));
    assert.equal(await publish('smartlocker/devices/UNIT-ESP32/door'), true);
    assert.equal(await publish('smartlocker/devices/UNIT-ESP32/heartbeat'), true);
    assert.equal(await publish('smartlocker/devices/UNIT-ESP32/cmd'), false);
    assert.equal(await publish('smartlocker/devices/OTHER/door'), false);
    assert.equal(await publish('smartlocker/devices/UNIT-ESP32/door', true), false);
    assert.equal(await subscribe('smartlocker/devices/UNIT-ESP32/cmd'), true);
    assert.equal(await subscribe('smartlocker/devices/+/cmd'), false);
    assert.equal(await subscribe('#'), false);
  } finally { await new Promise(resolve => broker.close(resolve)); }
});

test('loopback API can issue commands without exposing anonymous LAN access', async () => {
  const broker = createLocalBroker({ username: 'unit-device', password: 'unit-secret', deviceCode: 'UNIT-ESP32' });
  try {
    const client = { conn: { remoteAddress: '127.0.0.1' } };
    const allowed = await new Promise(resolve => broker.authenticate(client, null, null, (_, value) => resolve(value)));
    assert.equal(allowed, true);
    const error = await new Promise(resolve => broker.authorizePublish(client, { topic: 'smartlocker/devices/UNIT-ESP32/cmd' }, resolve));
    assert.equal(error, null);
  } finally { await new Promise(resolve => broker.close(resolve)); }
});
