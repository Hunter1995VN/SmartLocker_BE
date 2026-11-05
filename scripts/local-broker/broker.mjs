import Aedes from 'aedes';
import { createServer } from 'node:net';
import { readFile } from 'node:fs/promises';
import { networkInterfaces } from 'node:os';
import { createHash, timingSafeEqual } from 'node:crypto';
import { fileURLToPath } from 'node:url';
import { resolve } from 'node:path';

const digest = value => createHash('sha256').update(value).digest();
export function createLocalBroker({ username, password, deviceCode }) {
  if (!username || !password || !deviceCode || /[+#/]/.test(deviceCode)) throw new Error('Invalid local broker configuration');
  const broker = Aedes();
  const trusted = Symbol('loopback');
  broker.authenticate = (client, user, pass, callback) => {
    const address = client.conn.remoteAddress;
    client[trusted] = address === '127.0.0.1' || address === '::1' || address === '::ffff:127.0.0.1';
    const allowed = client[trusted] || (user === username && timingSafeEqual(digest(pass || ''), digest(password)));
    callback(null, allowed);
  };
  const prefix = `smartlocker/devices/${deviceCode}/`;
  broker.authorizePublish = (client, packet, callback) => {
    const allowed = client?.[trusted] || (!packet.retain && [prefix + 'door', prefix + 'heartbeat'].includes(packet.topic));
    callback(allowed ? null : new Error('Device publish denied'));
  };
  broker.authorizeSubscribe = (client, subscription, callback) => {
    const allowed = client[trusted] || subscription.topic === prefix + 'cmd';
    callback(null, allowed ? subscription : null);
  };
  broker.on('clientError', () => console.warn('MQTT client disconnected after protocol/auth error.'));
  return broker;
}

async function start() {
  const configPath = process.env.SMARTLOCKER_IOT_CONFIG || fileURLToPath(new URL('../../iotDevice/SmartLocker_ESP32_SingleFile/iot_config.h', import.meta.url));
  const config = await readFile(configPath, 'utf8');
  const field = name => {
    const match = config.match(new RegExp(`(?:char\\s+${name}\\[\\]|char\\s*\\*\\s*${name})\\s*=\\s*("(?:[^"\\\\]|\\\\.)*")`));
    if (!match) throw new Error(`Missing configuration field ${name}`);
    return JSON.parse(match[1]);
  };
  const host = field('MQTT_HOST');
  const localAddresses = Object.values(networkInterfaces()).flat().filter(Boolean).filter(n => !n.internal && n.family === 'IPv4').map(n => n.address);
  if (!localAddresses.includes(host)) throw new Error('MQTT_HOST must match an IPv4 address of this computer. Update the local header first.');
  const broker = createLocalBroker({ username: field('MQTT_USERNAME'), password: field('MQTT_PASSWORD'), deviceCode: field('DEVICE_CODE') });
  const servers = ['127.0.0.1', host].map(address => {
    const server = createServer(broker.handle);
    server.on('error', () => { console.error(`Unable to listen on ${address}:1883`); process.exitCode = 1; });
    server.listen(1883, address, () => console.log(`MQTT listening on ${address}:1883 (${address === '127.0.0.1' ? 'local API' : 'authenticated device; topic ACL enabled'})`));
    return server;
  });
  process.once('SIGINT', () => { for (const server of servers) server.close(); broker.close(() => process.exit(0)); });
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  start().catch(() => { console.error('Local broker startup failed. Check the ignored IoT header and network address.'); process.exitCode = 1; });
}
