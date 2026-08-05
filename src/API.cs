using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
            var identity = ReadIdentity(clientInfo);

            client.Enqueue(new TelemetryEvent
            {
                type = eventType,
                player = identity,
                data = new Dictionary<string, object>
                {
                    ["respawnType"] = respawnReason.ToString(),
                    ["position"] = position.ToString()
                }
            });

            if (eventType == "player.joined") TryShowClaimMessage(clientInfo, identity);
        }

        private static void TryShowClaimMessage(ClientInfo clientInfo, PlayerIdentity identity)
        {
            if (config == null || !config.profileClaimsEnabled || identity == null || string.IsNullOrWhiteSpace(identity.steamId)) return;
            if (string.IsNullOrWhiteSpace(config.profileClaimBaseUrl)) return;

            try
            {
                var separator = config.profileClaimBaseUrl.Contains("?") ? "&" : "?";
                var url = config.profileClaimBaseUrl + separator
                    + "playerId=" + Uri.EscapeDataString(identity.steamId)
                    + "&name=" + Uri.EscapeDataString(identity.name ?? string.Empty);
                var message = (string.IsNullOrWhiteSpace(config.profileClaimPrefix)
                    ? "[ApexOrder] Claim your profile and stats by signing in with Steam:"
                    : config.profileClaimPrefix.Trim()) + " " + url;

                if (!TrySendTargetedChat(clientInfo, message))
                    Log.Out("[ApexTelemetry] Claim link for " + (identity.name ?? identity.steamId) + ": " + url);
            }
            catch (Exception error)
            {
                Log.Warning("[ApexTelemetry] Could not show profile claim message: " + error.Message);
            }
        }

        private static bool TrySendTargetedChat(ClientInfo clientInfo, string message)
        {
            try
            {
                var gameManagerType = typeof(GameManager);
                var instanceProperty = gameManagerType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
                var instance = instanceProperty?.GetValue(null, null);
                if (instance == null) return false;

                var methods = gameManagerType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .Where(method => method.Name.IndexOf("ChatMessage", StringComparison.OrdinalIgnoreCase) >= 0)
                    .OrderBy(method => method.GetParameters().Length);

                foreach (var method in methods)
                {
                    var parameters = method.GetParameters();
                    var args = new object[parameters.Length];
                    var usedMessage = false;
                    var compatible = true;

                    for (var index = 0; index < parameters.Length; index++)
                    {
                        var parameterType = parameters[index].ParameterType;
                        if (parameterType == typeof(string) && !usedMessage)
                        {
                            args[index] = message;
                            usedMessage = true;
                        }
                        else if (parameterType.IsInstanceOfType(clientInfo)) args[index] = clientInfo;
                        else if (parameterType == typeof(string)) args[index] = string.Empty;
                        else if (parameterType == typeof(int)) args[index] = -1;
                        else if (parameterType == typeof(bool)) args[index] = false;
                        else if (parameterType.IsEnum) args[index] = Enum.GetValues(parameterType).GetValue(0);
                        else if (!parameterType.IsValueType) args[index] = null;
                        else if (parameters[index].HasDefaultValue) args[index] = parameters[index].DefaultValue;
                        else { compatible = false; break; }
                    }

                    if (!compatible || !usedMessage) continue;
                    try
                    {
                        method.Invoke(instance, args);
                        return true;
                    }
                    catch { }
                }
            }
            catch { }
            return false;
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
