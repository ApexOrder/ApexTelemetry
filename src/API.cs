using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Web.Script.Serialization;

namespace ApexTelemetry
{
    public sealed class API : IModApi
    {
        private static TelemetryClient client;
        private static TelemetryConfig config;

        public void InitMod(Mod modInstance)
        {
            try
            {
                var root = modInstance.Path;
                var configPath = Path.Combine(root, "Config", "telemetry.json");
                if (!File.Exists(configPath))
                {
                    Log.Warning("[ApexTelemetry] Config/telemetry.json is missing; telemetry is disabled.");
                    return;
                }

                config = new JavaScriptSerializer().Deserialize<TelemetryConfig>(File.ReadAllText(configPath));
                if (config == null || !config.enabled) return;
                if (string.IsNullOrWhiteSpace(config.serverId) || string.IsNullOrWhiteSpace(config.endpoint) || string.IsNullOrWhiteSpace(config.apiKey))
                    throw new InvalidOperationException("serverId, endpoint and apiKey are required.");

                client = new TelemetryClient(config, Path.Combine(root, "Data", "telemetry-queue.jsonl"));
                ModEvents.GameStartDone.RegisterHandler(OnGameStartDone);
                ModEvents.PlayerSpawnedInWorld.RegisterHandler(OnPlayerSpawnedInWorld);
                Log.Out("[ApexTelemetry] Initialised for server " + config.serverId + ".");
            }
            catch (Exception error)
            {
                Log.Error("[ApexTelemetry] Failed to initialise: " + error);
            }
        }

        private static void OnGameStartDone()
        {
            client?.Enqueue(new TelemetryEvent
            {
                type = "server.started",
                data = new Dictionary<string, object> { ["game"] = "7dtd" }
            });
        }

        private static void OnPlayerSpawnedInWorld(ClientInfo clientInfo, RespawnType respawnReason, Vector3i position)
        {
            if (client == null || clientInfo == null) return;
            var eventType = respawnReason == RespawnType.EnterMultiplayer || respawnReason == RespawnType.JoinMultiplayer
                ? "player.joined"
                : "player.spawned";

            client.Enqueue(new TelemetryEvent
            {
                type = eventType,
                player = ReadIdentity(clientInfo),
                data = new Dictionary<string, object>
                {
                    ["respawnType"] = respawnReason.ToString(),
                    ["position"] = position.ToString()
                }
            });
        }

        private static PlayerIdentity ReadIdentity(ClientInfo info)
        {
            var identity = new PlayerIdentity
            {
                name = ReadString(info, "playerName", "PlayerName", "name"),
                steamId = ReadString(info, "steamId", "SteamId", "steamID", "SteamID"),
                eosId = ReadString(info, "entityId", "EntityId", "ownerId", "OwnerId"),
                entityId = ReadInt(info, "entityId", "EntityId")
            };
            identity.platformId = !string.IsNullOrWhiteSpace(identity.steamId) ? identity.steamId : identity.eosId;
            return identity;
        }

        private static string ReadString(object source, params string[] names)
        {
            foreach (var name in names)
            {
                var value = ReadMember(source, name);
                if (value != null && !string.IsNullOrWhiteSpace(value.ToString())) return value.ToString();
            }
            return null;
        }

        private static int ReadInt(object source, params string[] names)
        {
            foreach (var name in names)
            {
                var value = ReadMember(source, name);
                if (value != null && int.TryParse(value.ToString(), out var result)) return result;
            }
            return 0;
        }

        private static object ReadMember(object source, string name)
        {
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase;
            var type = source.GetType();
            var property = type.GetProperty(name, flags);
            if (property != null) return property.GetValue(source, null);
            var field = type.GetField(name, flags);
            return field?.GetValue(source);
        }
    }
}
