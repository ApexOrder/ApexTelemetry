using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace ApexTelemetry
{
    public sealed class TelemetryClient : IDisposable
    {
        private readonly TelemetryConfig config;
        private readonly string queuePath;
        private readonly HttpClient http;
        private readonly JavaScriptSerializer json = new JavaScriptSerializer();
        private readonly object sync = new object();
        private readonly Timer timer;
        private bool flushing;

        public TelemetryClient(TelemetryConfig config, string queuePath)
        {
            this.config = config ?? throw new ArgumentNullException(nameof(config));
            this.queuePath = queuePath ?? throw new ArgumentNullException(nameof(queuePath));
            Directory.CreateDirectory(Path.GetDirectoryName(queuePath));
            http = new HttpClient { Timeout = TimeSpan.FromSeconds(Math.Max(2, config.requestTimeoutSeconds)) };
            timer = new Timer(_ => _ = FlushAsync(), null,
                TimeSpan.FromSeconds(Math.Max(2, config.flushIntervalSeconds)),
                TimeSpan.FromSeconds(Math.Max(2, config.flushIntervalSeconds)));
        }

        public void Enqueue(TelemetryEvent telemetryEvent)
        {
            if (!config.enabled || telemetryEvent == null) return;
            telemetryEvent.serverId = config.serverId;
            lock (sync)
            {
                File.AppendAllText(queuePath, json.Serialize(telemetryEvent) + Environment.NewLine, Encoding.UTF8);
            }
        }

        public async Task FlushAsync()
        {
            lock (sync)
            {
                if (flushing) return;
                flushing = true;
            }

            try
            {
                List<string> lines;
                lock (sync)
                {
                    if (!File.Exists(queuePath)) return;
                    lines = File.ReadAllLines(queuePath, Encoding.UTF8).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
                }

                if (lines.Count == 0) return;
                var take = Math.Min(Math.Max(1, config.maxBatchSize), lines.Count);
                var batch = new TelemetryBatch { serverId = config.serverId };
                foreach (var line in lines.Take(take)) batch.events.Add(json.Deserialize<TelemetryEvent>(line));

                var body = json.Serialize(batch);
                var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
                var signature = Sign(timestamp + "." + body, config.apiKey);
                using (var request = new HttpRequestMessage(HttpMethod.Post, config.endpoint))
                {
                    request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                    request.Headers.Add("X-Apex-Server", config.serverId);
                    request.Headers.Add("X-Apex-Timestamp", timestamp);
                    request.Headers.Add("X-Apex-Signature", signature);
                    using (var response = await http.SendAsync(request).ConfigureAwait(false))
                    {
                        if (!response.IsSuccessStatusCode) return;
                    }
                }

                lock (sync)
                {
                    var remaining = lines.Skip(take).ToArray();
                    File.WriteAllLines(queuePath, remaining, Encoding.UTF8);
                }
            }
            catch (Exception error)
            {
                Log.Warning("[ApexTelemetry] Delivery failed; events remain queued: " + error.Message);
            }
            finally
            {
                lock (sync) flushing = false;
            }
        }

        private static string Sign(string value, string secret)
        {
            using (var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret ?? string.Empty)))
            {
                return BitConverter.ToString(hmac.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        public void Dispose()
        {
            timer.Dispose();
            try { FlushAsync().GetAwaiter().GetResult(); } catch { }
            http.Dispose();
        }
    }
}
