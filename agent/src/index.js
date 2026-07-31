import crypto from 'node:crypto';
import fs from 'node:fs';
import net from 'node:net';
import path from 'node:path';

const configPath = path.resolve(process.env.APEX_TELEMETRY_CONFIG || './config.json');
const config = JSON.parse(fs.readFileSync(configPath, 'utf8'));

function required(value, name) {
  const text = String(value ?? '').trim();
  if (!text) throw new Error(`${name} is required.`);
  return text;
}

const serverId = required(config.serverId, 'serverId');
const apiEndpoint = required(config.apiEndpoint, 'apiEndpoint');
const apiKey = required(config.apiKey, 'apiKey');
const pollIntervalMs = Math.max(10, Number(config.pollIntervalSeconds || 30)) * 1000;
const telnet = {
  host: required(config.telnet?.host, 'telnet.host'),
  port: Math.max(1, Number(config.telnet?.port || 8081)),
  password: required(config.telnet?.password, 'telnet.password'),
  timeoutMs: Math.max(2, Number(config.telnet?.timeoutSeconds || 8)) * 1000,
};

let polling = false;

function cleanTelnet(text) {
  return text
    .replace(/\x1b\[[0-9;?]*[ -/]*[@-~]/g, '')
    .replace(/[\x00-\x08\x0b\x0c\x0e-\x1f\x7f]/g, '')
    .replace(/\r/g, '');
}

function queryListPlayers() {
  return new Promise((resolve, reject) => {
    const socket = net.createConnection({ host: telnet.host, port: telnet.port });
    let output = '';
    let authenticated = false;
    let commandSent = false;
    let settled = false;

    const finish = (error) => {
      if (settled) return;
      settled = true;
      socket.destroy();
      if (error) reject(error); else resolve(cleanTelnet(output));
    };

    socket.setTimeout(telnet.timeoutMs);
    socket.on('timeout', () => finish(new Error('Telnet query timed out.')));
    socket.on('error', finish);
    socket.on('close', () => {
      if (!settled && output) finish();
    });

    socket.on('data', (chunk) => {
      const text = chunk.toString('utf8');
      output += text;
      const lower = cleanTelnet(output).toLowerCase();

      if (!authenticated && (lower.includes('password') || lower.includes('please enter password'))) {
        authenticated = true;
        output = '';
        socket.write(`${telnet.password}\n`);
        return;
      }

      if (authenticated && !commandSent && (lower.includes('type "help"') || lower.includes('logged in') || lower.includes('command'))) {
        commandSent = true;
        output = '';
        socket.write('lp\n');
        return;
      }

      if (commandSent && (/total of \d+ in the game/i.test(lower) || lower.includes('command executed successfully'))) {
        setTimeout(() => finish(), 100);
      }
    });

    socket.on('connect', () => {
      setTimeout(() => {
        if (!authenticated) {
          authenticated = true;
          output = '';
          socket.write(`${telnet.password}\n`);
        }
        setTimeout(() => {
          if (!commandSent) {
            commandSent = true;
            output = '';
            socket.write('lp\n');
          }
        }, 300);
      }, 300);
    });
  });
}

function parseKeyValues(line) {
  const values = {};
  const pattern = /(?:^|,\s*)([A-Za-z][A-Za-z0-9_]*)=([^,]*?)(?=,\s*[A-Za-z][A-Za-z0-9_]*=|$)/g;
  let match;
  while ((match = pattern.exec(line))) values[match[1].toLowerCase()] = match[2].trim();
  return values;
}

function number(value) {
  const parsed = Number(String(value ?? '').trim());
  return Number.isFinite(parsed) ? parsed : null;
}

function first(values, ...names) {
  for (const name of names) {
    const value = values[name.toLowerCase()];
    if (value !== undefined && value !== '') return value;
  }
  return null;
}

function parsePlayers(output) {
  const players = [];
  for (const rawLine of output.split('\n')) {
    const line = rawLine.trim();
    if (!line || !line.includes('=')) continue;
    const values = parseKeyValues(line);
    const platformId = first(values, 'steamid', 'steamid64', 'platformid', 'ownerid', 'crossplatformid', 'userid');
    const name = first(values, 'name', 'playername');
    if (!platformId || !name) continue;

    players.push({
      platformId: String(platformId),
      name: String(name),
      entityId: number(first(values, 'id', 'entityid')),
      zombieKills: number(first(values, 'zombies', 'zombiekills', 'zombie_kills')),
      pvpKills: number(first(values, 'players', 'playerkills', 'pvp', 'pvpkills')),
      deaths: number(first(values, 'deaths')),
      score: number(first(values, 'score')),
      level: number(first(values, 'level')),
      gameStage: number(first(values, 'gamestage', 'game_stage')),
      health: number(first(values, 'health')),
      ping: number(first(values, 'ping')),
    });
  }
  return players;
}

async function sendSnapshots(players) {
  if (players.length === 0) return;
  const occurredAt = new Date().toISOString();
  const events = players.map((player) => ({
    id: crypto.createHash('sha256').update(`${serverId}:${player.platformId}:${occurredAt}`).digest('hex'),
    type: 'player.snapshot',
    occurredAt,
    player: {
      playerId: player.platformId,
      steamId: /^7656119\d{10}$/.test(player.platformId) ? player.platformId : null,
      provider: /^7656119\d{10}$/.test(player.platformId) ? 'steam' : '7dtd',
      name: player.name,
      entityId: player.entityId,
    },
    zombieKills: player.zombieKills,
    pvpKills: player.pvpKills,
    deaths: player.deaths,
    score: player.score,
    level: player.level,
    gameStage: player.gameStage,
    online: true,
    ping: player.ping,
    health: player.health,
  }));

  const body = JSON.stringify({ serverId, events });
  const timestamp = Math.floor(Date.now() / 1000).toString();
  const signature = crypto.createHmac('sha256', apiKey).update(`${timestamp}.${body}`).digest('hex');
  const response = await fetch(apiEndpoint, {
    method: 'POST',
    headers: {
      'content-type': 'application/json',
      'x-apex-server': serverId,
      'x-apex-timestamp': timestamp,
      'x-apex-signature': signature,
    },
    body,
    signal: AbortSignal.timeout(telnet.timeoutMs),
  });
  if (!response.ok) throw new Error(`ApexOrder rejected telemetry: ${response.status} ${await response.text()}`);
  const result = await response.json();
  console.log(`[ApexTelemetry] Sent ${players.length} player snapshot(s); accepted ${result.accepted ?? 0}.`);
}

async function poll() {
  if (polling) return;
  polling = true;
  try {
    const output = await queryListPlayers();
    const players = parsePlayers(output);
    console.log(`[ApexTelemetry] Telnet returned ${players.length} player(s).`);
    await sendSnapshots(players);
  } catch (error) {
    console.error(`[ApexTelemetry] Poll failed: ${error.message}`);
  } finally {
    polling = false;
  }
}

console.log(`[ApexTelemetry] External collector started for ${serverId}; polling ${telnet.host}:${telnet.port}.`);
await poll();
setInterval(poll, pollIntervalMs).unref();
