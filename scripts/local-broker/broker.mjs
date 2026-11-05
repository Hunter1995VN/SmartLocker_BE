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
    const allowed = client?.[trusted] || (!packet.retain && [prefix + 'door', prefix + 'heartbeat', prefix + 'diagnostics'].includes(packet.topic));
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
  let configPath = process.env.SMARTLOCKER_IOT_CONFIG;
  if (!configPath) {
    const candidate1 = fileURLToPath(new URL('../../iotDevice/SmartLocker_ESP32_SingleFile/iot_config.h', import.meta.url));
    const candidate2 = fileURLToPath(new URL('../../iotDevice/iot_config.h', import.meta.url));
    try {
      await readFile(candidate1);
      configPath = candidate1;
    } catch {
      configPath = candidate2;
    }
  }

  let username = 'esp32';
  let password = 'khongcopass';
  let deviceCode = 'SIM-MQTT-001';
  let host = '127.0.0.1';

  try {
    const config = await readFile(configPath, 'utf8');
    const field = name => {
      const match = config.match(new RegExp(`(?:char\\s+${name}\\[\\]|char\\s*\\*\\s*${name})\\s*=\\s*("(?:[^"\\\\]|\\\\.)*")`));
      return match ? JSON.parse(match[1]) : null;
    };
    username = field('MQTT_USERNAME') || username;
    password = field('MQTT_PASSWORD') || password;
    deviceCode = field('DEVICE_CODE') || deviceCode;
    host = field('MQTT_HOST') || host;
  } catch (e) {
    console.warn('Could not read iot_config.h, using defaults.', e.message);
  }

  const localAddresses = Object.values(networkInterfaces()).flat().filter(Boolean).filter(n => !n.internal && n.family === 'IPv4').map(n => n.address);
  const listenAddresses = ['127.0.0.1'];
  if (localAddresses.includes(host)) {
    listenAddresses.push(host);
  } else if (localAddresses.length > 0) {
    listenAddresses.push(localAddresses[0]);
  }

  const broker = createLocalBroker({ username, password, deviceCode });
  const uniqueAddresses = Array.from(new Set(listenAddresses));
  const servers = uniqueAddresses.map(address => {
    const server = createServer(broker.handle);
    server.on('error', (err) => { console.error(`Unable to listen on ${address}:1883: ${err.message}`); });
    server.listen(1883, address, () => console.log(`MQTT listening on ${address}:1883 (${address === '127.0.0.1' ? 'local API' : 'authenticated device; topic ACL enabled'})`));
    return server;
  });
  process.once('SIGINT', () => { for (const server of servers) server.close(); broker.close(() => process.exit(0)); });
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  start().catch((err) => { console.error('Local broker startup failed:', err); process.exitCode = 1; });
}
