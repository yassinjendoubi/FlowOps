namespace WorkerServiceControlWeb.Models
{
    public class ServiceStatus
    {
        public string Key { get; set; } = "";
        public string DisplayName { get; set; } = "";

        // "running" — the process is actually alive right now.
        // "waiting" — started and not yet stopped by the user, but the
        //             underlying process already finished on its own
        //             (normal for a one-shot worker).
        // "stopped" — never started, or explicitly stopped.
        public string State { get; set; } = "stopped";

        public List<string> Logs { get; set; } = new();
        public long HistoryVersion { get; set; }
        public ServiceHistoryEvent? LastEvent { get; set; }
    }
}
