# ApexTelemetry

Mod-free telemetry collector for ApexOrder 7 Days to Die servers.

## Why the architecture changed

The original proof of concept used a compiled `IModApi` DLL inside the server's `Mods` folder. Current 7DTD clients treat compiled code mods as client-required, which made connecting players install ApexTelemetry too. That is not acceptable for invisible server monitoring.

The supported production design is now an **external Node.js agent**. It connects to the server's built-in Telnet interface, runs `lp`, converts the returned player counters into signed telemetry snapshots, and sends them to ApexOrder.

Players install nothing.

## Data collected

The exact fields depend on the `lp` output exposed by the running 7DTD build, but the parser supports:

- Steam/platform ID
- player name
- entity ID
- zombie kills
- player/PvP kills
- deaths
- score
- level
- game stage
- health and ping

## Requirements

- Node.js 20 or newer
- 7DTD Telnet enabled
- Telnet bound to localhost or a private network only
- ApexOrder telemetry receiver and matching per-server API secret

## Install

```bash
cd /opt/ApexTelemetry
git pull origin main
cd agent
cp config.example.json config.json
nano config.json
npm install
```

Example configuration:

```json
{
  "serverId": "apex-7dtd-main",
  "apiEndpoint": "https://apexorder.uk/api/telemetry/v1/events",
  "apiKey": "same-secret-as-apexorder",
  "pollIntervalSeconds": 30,
  "telnet": {
    "host": "127.0.0.1",
    "port": 8081,
    "password": "your-7dtd-telnet-password",
    "timeoutSeconds": 8
  }
}
```

Run with PM2:

```bash
cd /opt/ApexTelemetry/agent
pm2 start npm --name apex-7dtd-telemetry -- start
pm2 save
```

## Remove the old DLL mod

Delete the old server mod before restarting 7DTD:

```bash
rm -rf /path/to/7DaysToDieServer/Mods/ApexTelemetry
```

This prevents the server from advertising ApexTelemetry as a required client mod.

## Security

Do not expose the 7DTD Telnet port publicly. Bind or firewall it to localhost/private management addresses. The agent sends data to ApexOrder using timestamped HMAC-SHA256 signatures and never stores a Discord bot token.

## Legacy prototype

The old `.NET Framework` ModAPI source remains in the repository for reference only. It is not the recommended deployment path.
