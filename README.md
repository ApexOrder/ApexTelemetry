# ApexTelemetry

Server-side telemetry collector for ApexOrder 7 Days to Die servers.

## First milestone

The initial scaffold provides:

- a dedicated-server `IModApi` entry point
- stable player identity payloads
- server-start and player-join events
- signed HTTPS batches using HMAC-SHA256
- a durable local JSONL retry queue
- configuration kept outside source control

The collector does not contain a Discord token and does not post directly to Discord. It sends reusable events to the ApexOrder API, which can power the website, leaderboards and Discord bot.

## Requirements

- 7 Days to Die dedicated server
- .NET Framework 4.8 targeting pack
- game assemblies from `7DaysToDieServer_Data/Managed`

The official ModAPI loads compiled C# code from a mod folder containing `ModInfo.xml`. Game assemblies must be referenced at build time but are not committed to this repository.

## Build

Set the managed-assembly directory and build:

```powershell
$env:SEVEN_DAYS_MANAGED = "C:\7DTD\7DaysToDieServer_Data\Managed"
dotnet build -c Release
```

Linux example:

```bash
export SEVEN_DAYS_MANAGED=/path/to/7DaysToDieServer_Data/Managed
dotnet build -c Release
```

## Install

Create this folder on the dedicated server:

```text
Mods/ApexTelemetry/
```

Copy into it:

```text
ModInfo.xml
ApexTelemetry.dll
Config/telemetry.json
```

Create `Config/telemetry.json` from `Config/telemetry.example.json` and provide:

- the ApexOrder server UUID
- the telemetry receiver URL
- a unique long random API key

The API key must match the secret configured for that server in the ApexOrder backend.

## Event authentication

Each request contains:

```text
X-Apex-Server
X-Apex-Timestamp
X-Apex-Signature
```

The signature is lowercase hexadecimal HMAC-SHA256 over:

```text
<unix timestamp>.<raw JSON body>
```

## Current events

```text
server.started
player.joined
player.spawned
```

Planned next events include disconnects, deaths, zombie kills, PvP kills and periodic player-stat snapshots. Those handlers will be added only after verifying their signatures against the exact dedicated-server build in use.
