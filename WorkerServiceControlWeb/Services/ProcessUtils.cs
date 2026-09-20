namespace WorkerServiceControlWeb.Services
{
    // Small process utilities shared by WorkerProcessManager / ScheduledProcessLauncher.
    public static class ProcessUtils
    {
        // Kills a process (and its descendants) by PID alone. Works whether
        // or not this app instance is the one that originally started it
        // (e.g. after recovering a PID from a previous run via the
        // persisted pid file).
        public static void KillPid(int pid)
        {
            try
            {
                using var killer = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "taskkill",
                    Arguments = $"/PID {pid} /T /F",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                });
                killer?.WaitForExit(3000);
            }
            catch
            {
                // best effort
            }
        }

        // Is there still a live process with this id that looks like the
        // expected exe? PIDs get recycled by Windows, so a bare "does this
        // PID exist" check isn't enough on its own.
        public static bool IsProcessAlive(int pid, string expectedExeName)
        {
            try
            {
                using var process = System.Diagnostics.Process.GetProcessById(pid);
                return string.Equals(process.ProcessName, expectedExeName, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
    }
}
