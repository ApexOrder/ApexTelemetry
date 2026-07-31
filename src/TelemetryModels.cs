using System;
using System.Collections.Generic;

namespace ApexTelemetry
{
    public sealed class TelemetryConfig
    {
        public bool enabled { get; set; } = true;
        public string serverId { get; set; } = string.Empty;
        public string endpoint { get; set; } = string.Empty;
        public string apiKey { get; set; } = string.Empty;
        public int flushIntervalSeconds { get; set; } = 10;
        public int requestTimeoutSeconds { get; set; } = 8;
        public int maxBatchSize { get; set; } = 50;
    }

    public sealed class TelemetryEvent
    {
        public string id { get; set; } = Guid.NewGuid().ToString("D");
        public string serverId { get; set; }
        public string type { get; set; }
        public string occurredAt { get; set; } = DateTime.UtcNow.ToString("O");
        public PlayerIdentity player { get; set; }
        public Dictionary<string, object> data { get; set; } = new Dictionary<string, object>();
    }

    public sealed class PlayerIdentity
    {
        public string platformId { get; set; }
        public string steamId { get; set; }
        public string eosId { get; set; }
        public string name { get; set; }
        public int entityId { get; set; }
    }

    public sealed class TelemetryBatch
    {
        public string version { get; set; } = "1";
        public string serverId { get; set; }
        public List<TelemetryEvent> events { get; set; } = new List<TelemetryEvent>();
    }
}
