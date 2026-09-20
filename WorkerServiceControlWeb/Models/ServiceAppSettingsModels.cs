namespace WorkerServiceControlWeb.Models
{
    public sealed class ServiceAppSettingsSnapshot
    {
        public bool Success { get; init; }
        public bool NotFound { get; init; }
        public bool Exists { get; init; }
        public string Content { get; init; } = "";
        public string Version { get; init; } = "";
        public DateTimeOffset? LastModifiedUtc { get; init; }
        public bool IsValid { get; init; }
        public string ValidationError { get; init; } = "";
        public string Error { get; init; } = "";
        public string FileName { get; init; } = "appsettings.json";
    }

    public sealed class ServiceAppSettingsSaveResult
    {
        public bool Success { get; init; }
        public bool NotFound { get; init; }
        public bool Conflict { get; init; }
        public string Version { get; init; } = "";
        public DateTimeOffset? LastModifiedUtc { get; init; }
        public string Error { get; init; } = "";
    }
}
