import crypto from 'node:crypto';
import fs from 'node:fs';
import net from 'node:net';
import path from 'node:path';

const configPath = path.resolve(process.env.APEX_TELEMETRY_CONFIG || './config.json');
const config = JSON.parse(fs.readFileSync(configPath, 'utf8'));
const statePath = path.resolve(path.dirname(configPath), config.stateFile || 'state.json');

const requireText = (value, name) => {
  const text = String(value ?? '').trim();
  if (!text) throw new Error(`${name} is required.`);
  return text;
};
const serverId = requireText(config.serverId, 'serverId');
const apiEndpoint = requireText(config.apiEndpoint, 'apiEndpoint');
const apiKey = requireText(config.apiKey, 'apiKey');
const pollIntervalMs = Math.max(10, Number(config.pollIntervalSeconds || 30)) * 1000;
const telnet = {
  host: requireText(config.telnet?.host, 'telnet.host'),
  port: Math.max(1, Number(config.telnet?.port || 8081)),
  password: requireText(config.telnet?.password, 'telnet.password'),
  timeoutMs: Math.max(2, Number(config.telnet?.timeoutSeconds || 8)) * 1000,
};

let state = {};
try { state = JSON.parse(fs.readFileSync(statePath, 'utf8')); } catch { state = {}; }
let polling = false;

function saveState(next) {
  const temp = `${statePath}.tmp`;
  fs.writeFileSync(temp, JSON.stringify(next, null, 2));
  fs.renameSync(temp, statePath);
}

function clean(text) {
  return text.replace(/\x1b\[[0-9;?]*[ -/]*[@-~]/g, '').replace(/[\x00-\x08\x0b\x0c\x0e-\x1f\x7f]/g, '').replace(/\r/g, '');
}

function queryPlayers() {
  return new Promise((resolve, reject) => {
    const socket = net.createConnection({ host: telnet.host, port: telnet.port });
    let output = '';
    let passwordSent = false;
    let commandSent = false;
    let done = false;
    const finish = (error) => {
      if (done) return;
      done = true;
      socket.destroy();
      error ? reject(error) : resolve(clean(output));
    };
    socket.setTimeout(telnet.timeoutMs);
    socket.on('timeout', () => finish(new Error('Telnet query timed out.')));
    socket.on('error', finish);
    socket.on('close', () => { if (!done && output) finish(); });
    socket.on('data', (chunk) => {
      output += chunk.toString('utf8');
      const lower = clean(output).toLowerCase();
      if (!passwordSent && lower.includes('password')) {
        passwordSent = true; output = ''; socket.write(`${telnet.password}\n`); return;
      }
      if (passwordSent && !commandSent && (lower.includes('logged in') || lower.includes('type "help"') || lower.includes('command'))) {
        commandSent = true; output = ''; socket.write('lp\n'); return;
      }
      if (commandSent && /total of \d+ in the game/i.test(lower)) setTimeout(() => finish(), 100);
    });
    socket.on('connect', () => setTimeout(() => {
      if (!passwordSent) { passwordSent = true; output = ''; socket.write(`${telnet.password}\n`); }
      setTimeout(() => { if (!commandSent) { commandSent = true; output = ''; socket.write('lp\n'); } }, 350);
    }, 250));
  });
}

function parseKeyValues(line) {
  const result = {};
  const regex = /(?:^|,\s*)([A-Za-z][A-Za-z0-9_]*)=([^,]*?)(?=,\s*[A-Za-z][A-Za-z0-9_]*=|$)/g;
  let match;
  while ((match = regex.exec(line))) result[match[1].toLowerCase()] = match[2].trim();
  return result;
}
const first = (values, ...keys) => keys.map((key) => values[key.toLowerCase()]).find((value) => value !== undefined && value !== '') ?? null;
const numeric = (value) => { const n = Number(value); return Number.isFinite(n) ? n : null; };

function parsePlayers(output) {
  return output.split('\n').flatMap((raw) => {
    const values = parseKeyValues(raw.trim());
    const platformId = first(values, 'steamid', 'steamid64', 'platformid', 'ownerid', 'crossplatformid', 'userid');
    const name = first(values, 'name', 'playername');
    if (!platformId || !name) return [];
    return [{
      platformId: String(platformId), name: String(name),
      entityId: numeric(first(values, 'id', 'entityid')),
      zombieKills: numeric(first(values, 'zombies', 'zombiekills', 'zombie_kills')),
      pvpKills: numeric(first(values, 'players', 'playerkills', 'pvp', 'pvpkills')),
      deaths: numeric(first(values, 'deaths')), score: numeric(first(values, 'score')),
      level: numeric(first(values, 'level')), gameStage: numeric(first(values, 'gamestage', 'game_stage')),
      health: numeric(first(values, 'health')), ping: numeric(first(values, 'ping')),
    }];
  });
}

function delta(current, previous) {
  if (current === null) return 0;
  if (previous === undefined || previous === null) return Math.max(0, current);
  return current >= previous ? current - previous : Math.max(0, current);
}

async function submit(players) {
  if (!players.length) return;
  const occurredAt = new Date().toISOString();
  const nextState = { ...state };
  const events = players.map((player) => {
    const previous = state[player.platformId] || {};
    nextState[player.platformId] = {
      zombieKills: player.zombieKills, pvpKills: player.pvpKills, deaths: player.deaths,
      score: player.score, level: player.level, gameStage: player.gameStage, name: player.name,
      lastSeenAt: occurredAt,
    };
    return {
      id: crypto.randomUUID(), type: 'player.snapshot', occurredAt,
      player: {
        playerId: player.platformId,
        steamId: /^7656119\d{10}$/.test(player.platformId) ? player.platformId : null,
        provider: /^7656119\d{10}$/.test(player.platformId) ? 'steam' : '7dtd',
        name: player.name, entityId: player.entityId,
      },
      zombieKillsDelta: delta(player.zombieKills, previous.zombieKills),
      pvpKillsDelta: delta(player.pvpKills, previous.pvpKills),
      deathsDelta: delta(player.deaths, previous.deaths),
      score: player.score, level: player.level, gameStage: player.gameStage,
      counters: { zombieKills: player.zombieKills, pvpKills: player.pvpKills, deaths: player.deaths },
      online: true, ping: player.ping, health: player.health,
    };
  });
  const body = JSON.stringify({ serverId, events });
  const timestamp = Math.floor(Date.now() / 1000).toString();
  const signature = crypto.createHmac('sha256', apiKey).update(`${timestamp}.${body}`).digest('hex');
  const response = await fetch(apiEndpoint, {
    method: 'POST', headers: {
      'content-type': 'application/json', 'x-apex-server': serverId,
      'x-apex-timestamp': timestamp, 'x-apex-signature': signature,
    }, body, signal: AbortSignal.timeout(telnet.timeoutMs),
  });
  if (!response.ok) throw new Error(`ApexOrder rejected telemetry: ${response.status} ${await response.text()}`);
  const result = await response.json();
  state = nextState; saveState(state);
  console.log(`[ApexTelemetry] Sent ${players.length} snapshot(s); accepted ${result.accepted ?? 0}.`);
}

async function poll() {
  if (polling) return;
  polling = true;
  try {
    const players = parsePlayers(await queryPlayers());
    console.log(`[ApexTelemetry] Telnet returned ${players.length} player(s).`);
    await submit(players);
  } catch (error) { console.error(`[ApexTelemetry] Poll failed: ${error.message}`); }
  finally { polling = false; }
}

console.log(`[ApexTelemetry] External collector started for ${serverId}; polling ${telnet.host}:${telnet.port}.`);
await poll();
setInterval(poll, pollIntervalMs).unref();
