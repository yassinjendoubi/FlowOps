namespace WorkerServiceControlWeb.Models
{
    public sealed class ServiceHistoryEvent
    {
        public Guid EventId { get; init; }
        public Guid RunId { get; init; }
        public string ServiceKey { get; init; } = "";
        public string DisplayName { get; init; } = "";
        public string Type { get; init; } = "";
        public DateTimeOffset OccurredAtUtc { get; init; }
        public string Actor { get; init; } = "system";
        public string Message { get; init; } = "";
        public int? Pid { get; init; }
    }
}
