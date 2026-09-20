using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WorkerServiceControlWeb.Models;

namespace WorkerServiceControlWeb.Services
{
    /// <summary>
    /// Durable, append-only lifecycle history. Each service has its own JSONL
    /// file; a damaged final line after a power loss does not hide older events.
    /// </summary>
    public sealed class ServiceHistoryStore
    {
        private const int MaximumReadCount = 200;

        private sealed record HistorySummary(long Version, ServiceHistoryEvent? LastEvent);

        private static readonly JsonSerializerOptions JsonOptions = new();

        private readonly string _historyDirectory;
        private readonly ConcurrentDictionary<string, object> _serviceLocks =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, HistorySummary> _summaries =
            new(StringComparer.OrdinalIgnoreCase);

        public ServiceHistoryStore(IHostEnvironment env)
        {
            _historyDirectory = Path.Combine(env.ContentRootPath, "App_Data", "history");
        }

        public ServiceHistoryEvent Append(ServiceHistoryEvent historyEvent)
        {
            ArgumentNullException.ThrowIfNull(historyEvent);

            if (string.IsNullOrWhiteSpace(historyEvent.ServiceKey))
            {
                throw new ArgumentException("La clé du service est obligatoire.", nameof(historyEvent));
            }

            if (string.IsNullOrWhiteSpace(historyEvent.Type))
            {
                throw new ArgumentException("Le type d'événement est obligatoire.", nameof(historyEvent));
            }

            var normalized = new ServiceHistoryEvent
            {
                EventId = historyEvent.EventId == Guid.Empty ? Guid.NewGuid() : historyEvent.EventId,
                RunId = historyEvent.RunId == Guid.Empty ? Guid.NewGuid() : historyEvent.RunId,
                ServiceKey = historyEvent.ServiceKey,
                DisplayName = historyEvent.DisplayName,
                Type = historyEvent.Type,
                OccurredAtUtc = historyEvent.OccurredAtUtc == default
                    ? DateTimeOffset.UtcNow
                    : historyEvent.OccurredAtUtc.ToUniversalTime(),
                Actor = string.IsNullOrWhiteSpace(historyEvent.Actor) ? "system" : historyEvent.Actor.Trim(),
                Message = historyEvent.Message,
                Pid = historyEvent.Pid
            };

            var serviceLock = GetServiceLock(normalized.ServiceKey);

            lock (serviceLock)
            {
                var path = GetHistoryFilePath(normalized.ServiceKey);
                var currentEvents = ReadEventsCore(path, normalized.ServiceKey);

                // Lifecycle transitions are idempotent by run and type. This
                // prevents a process-exit monitor and a status poll (or an app
                // restart after a successful append) from recording completion
                // twice.
                var existing = currentEvents.LastOrDefault(item =>
                    item.RunId == normalized.RunId &&
                    string.Equals(item.Type, normalized.Type, StringComparison.OrdinalIgnoreCase));

                if (existing != null)
                {
                    UpdateSummary(normalized.ServiceKey, currentEvents);
                    return existing;
                }

                Directory.CreateDirectory(_historyDirectory);

                // If a previous crash left a truncated last JSON object without
                // its newline, start a fresh line. Otherwise the new valid event
                // would be concatenated to the damaged fragment and lost too.
                var separator = NeedsLineSeparator(path) ? "\n" : string.Empty;
                var jsonLine = separator + JsonSerializer.Serialize(normalized, JsonOptions) + "\n";
                var bytes = Encoding.UTF8.GetBytes(jsonLine);

                using (var stream = new FileStream(
                    path,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.Read,
                    bufferSize: 4096,
                    options: FileOptions.WriteThrough))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(flushToDisk: true);
                }

                _summaries[normalized.ServiceKey] = new HistorySummary(currentEvents.Count + 1, normalized);
                return normalized;
            }
        }

        public List<ServiceHistoryEvent> GetHistory(string serviceKey, int take = 50)
        {
            if (string.IsNullOrWhiteSpace(serviceKey) || take <= 0)
            {
                return new List<ServiceHistoryEvent>();
            }

            take = Math.Min(take, MaximumReadCount);
            var serviceLock = GetServiceLock(serviceKey);

            lock (serviceLock)
            {
                var events = ReadEventsCore(GetHistoryFilePath(serviceKey), serviceKey);
                UpdateSummary(serviceKey, events);

                return events
                    .AsEnumerable()
                    .Reverse()
                    .Take(take)
                    .ToList();
            }
        }

        public (long Version, ServiceHistoryEvent? LastEvent) GetSummary(string serviceKey)
        {
            if (string.IsNullOrWhiteSpace(serviceKey))
            {
                return (0, null);
            }

            if (_summaries.TryGetValue(serviceKey, out var cached))
            {
                return (cached.Version, cached.LastEvent);
            }

            var serviceLock = GetServiceLock(serviceKey);

            lock (serviceLock)
            {
                if (_summaries.TryGetValue(serviceKey, out cached))
                {
                    return (cached.Version, cached.LastEvent);
                }

                var events = ReadEventsCore(GetHistoryFilePath(serviceKey), serviceKey);
                UpdateSummary(serviceKey, events);
                cached = _summaries[serviceKey];
                return (cached.Version, cached.LastEvent);
            }
        }

        public void RemoveHistory(string serviceKey)
        {
            if (string.IsNullOrWhiteSpace(serviceKey))
            {
                return;
            }

            var serviceLock = GetServiceLock(serviceKey);

            lock (serviceLock)
            {
                var path = GetHistoryFilePath(serviceKey);

                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                _summaries.TryRemove(serviceKey, out _);
            }
        }

        private object GetServiceLock(string serviceKey) =>
            _serviceLocks.GetOrAdd(serviceKey, static _ => new object());

        private string GetHistoryFilePath(string serviceKey)
        {
            // A hash avoids traversal, reserved Windows file names, and invalid
            // filename characters even if registered-services.json is edited by
            // hand and contains a key that bypassed normal validation.
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(serviceKey));
            var safeName = Convert.ToHexString(hash).ToLowerInvariant() + ".jsonl";
            return Path.Combine(_historyDirectory, safeName);
        }

        private void UpdateSummary(string serviceKey, List<ServiceHistoryEvent> events)
        {
            _summaries[serviceKey] = new HistorySummary(events.Count, events.LastOrDefault());
        }

        private static bool NeedsLineSeparator(string path)
        {
            if (!File.Exists(path))
            {
                return false;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            if (stream.Length == 0)
            {
                return false;
            }

            stream.Seek(-1, SeekOrigin.End);
            return stream.ReadByte() != '\n';
        }

        private static List<ServiceHistoryEvent> ReadEventsCore(string path, string expectedServiceKey)
        {
            var events = new List<ServiceHistoryEvent>();

            if (!File.Exists(path))
            {
                return events;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

            string? line;

            while ((line = reader.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    var historyEvent = JsonSerializer.Deserialize<ServiceHistoryEvent>(line, JsonOptions);

                    if (historyEvent != null &&
                        historyEvent.EventId != Guid.Empty &&
                        historyEvent.RunId != Guid.Empty &&
                        string.Equals(historyEvent.ServiceKey, expectedServiceKey, StringComparison.OrdinalIgnoreCase))
                    {
                        events.Add(historyEvent);
                    }
                }
                catch (JsonException)
                {
                    // Most commonly this is an incomplete final line left by a
                    // sudden shutdown. Keep every previously committed event.
                }
            }

            return events;
        }
    }
}
