using System.Diagnostics;

namespace WorkerServiceControlWeb.Services
{
    // Launches a worker exe via the Windows Task Scheduler instead of as a
    // direct child process. This is what actually survives this app being
    // stopped — by any means (Ctrl+C, closing a terminal, Visual Studio's
    // "Stop" button, a rebuild). A direct child (even with CreateProcess
    // flags like CREATE_BREAKAWAY_FROM_JOB) can still get killed if the
    // process that launched *this* app (dotnet run, or VS's debugger) put
    // it in a cleanup Job Object that doesn't allow breakaway — Windows
    // enforces that at the OS level and a child can't override it.
    //
    // A process started by the Task Scheduler service has no parent/child
    // relationship with this app at all, so it can't be reached by any
    // Job Object this app happens to be in.
    //
    // Trade-off: we lose the live, event-driven stdout/stderr pipe. Output
    // is redirected to a log file instead, which WorkerProcessManager reads
    // back (see GetStatus) — that file also makes the worker's logs survive
    // this app's restarts, which an in-memory list never would.
    public static class ScheduledProcessLauncher
    {
        public static int? Start(string exePath, string arguments, string workingDirectory, string logFilePath, string tasksDirectory, out string error)
        {
            error = "";

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(logFilePath)!);

                // Logs accumulate across runs — only the "Vider les logs"
                // button clears them — so just make sure the file exists,
                // never truncate it here.
                if (!File.Exists(logFilePath))
                {
                    File.WriteAllText(logFilePath, string.Empty);
                }
            }
            catch (Exception ex)
            {
                error = "Impossible de préparer le fichier de logs : " + ex.Message;
                return null;
            }

            var taskName = "WSCW_" + Guid.NewGuid().ToString("N");

            // schtasks' /TR value is capped at 261 characters — full exe +
            // log paths blow past that easily. Writing the actual command
            // into a short-pathed .bat file and pointing /TR at *that*
            // sidesteps the limit entirely.
            string batPath;
            string vbsPath;

            try
            {
                Directory.CreateDirectory(tasksDirectory);
                batPath = Path.Combine(tasksDirectory, taskName + ".bat");
                vbsPath = Path.Combine(tasksDirectory, taskName + ".vbs");

                // "chcp 65001" switches the console to UTF-8 first so the
                // redirected log file is consistently UTF-8 (avoids garbled
                // accented characters when reading it back).
                // ">>" (append), not ">" (overwrite) — logs accumulate
                // across runs until the user explicitly clears them.
                var script = "@echo off\r\n" +
                             "chcp 65001 >nul\r\n" +
                             $"cd /d \"{workingDirectory}\"\r\n" +
                             $"\"{exePath}\" {arguments} >> \"{logFilePath}\" 2>&1\r\n";

                File.WriteAllText(batPath, script);

                // VBScript wrapper: runs cmd with window style 0 (SW_HIDE)
                // so no black CMD window appears on screen when the Task
                // Scheduler fires the batch file.
                var vbs = $"CreateObject(\"WScript.Shell\").Run \"cmd /c \" & Chr(34) & \"{batPath}\" & Chr(34), 0, False\r\n";
                File.WriteAllText(vbsPath, vbs);
            }
            catch (Exception ex)
            {
                error = "Impossible de préparer le script de lancement : " + ex.Message;
                return null;
            }

            if (!RunSchtasks(new[] { "/Create", "/TN", taskName, "/TR", $"wscript.exe /nologo \"{vbsPath}\"", "/SC", "ONCE", "/ST", "23:59", "/F" }, out error))
            {
                TryDelete(batPath);
                TryDelete(vbsPath);
                return null;
            }

            // Tasks created by Windows refuse to start on battery power by
            // default. FlowOps is a supervision tool, so its services must
            // behave the same way whether the laptop is plugged in or not.
            // Update both power settings before requesting the manual run.
            if (!ConfigureTaskForBatteryPower(taskName, out error))
            {
                RunSchtasks(new[] { "/Delete", "/TN", taskName, "/F" }, out _);
                TryDelete(batPath);
                TryDelete(vbsPath);
                return null;
            }

            var beforeStart = DateTime.Now;

            if (!RunSchtasks(new[] { "/Run", "/TN", taskName }, out error))
            {
                RunSchtasks(new[] { "/Delete", "/TN", taskName, "/F" }, out _);
                TryDelete(batPath);
                TryDelete(vbsPath);
                return null;
            }

            var pid = WaitForProcess(exePath, beforeStart);

            RunSchtasks(new[] { "/Delete", "/TN", taskName, "/F" }, out _);
            TryDelete(batPath);
            TryDelete(vbsPath);

            if (pid == null)
            {
                error = "Le service a été planifié mais le processus n'a pas pu être localisé.";
            }

            return pid;
        }

        private static bool ConfigureTaskForBatteryPower(string taskName, out string error)
        {
            error = "";

            // taskName is generated locally from a GUID and therefore contains
            // only a fixed prefix plus hexadecimal characters.
            var command =
                $"$task = Get-ScheduledTask -TaskName '{taskName}' -ErrorAction Stop; " +
                "$task.Settings.DisallowStartIfOnBatteries = $false; " +
                "$task.Settings.StopIfGoingOnBatteries = $false; " +
                "Set-ScheduledTask -InputObject $task -ErrorAction Stop | Out-Null";

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            psi.ArgumentList.Add("-NoLogo");
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add(command);

            try
            {
                using var process = Process.Start(psi);

                if (process == null)
                {
                    error = "Impossible de configurer la tâche pour le fonctionnement sur batterie.";
                    return false;
                }

                if (!process.WaitForExit(15000))
                {
                    try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                    error = "La configuration de la tâche a dépassé le délai autorisé.";
                    return false;
                }

                if (process.ExitCode == 0)
                {
                    return true;
                }

                var details = process.StandardError.ReadToEnd().Trim();
                error = string.IsNullOrWhiteSpace(details)
                    ? "Windows a refusé la configuration de la tâche pour le fonctionnement sur batterie."
                    : "Configuration de l’alimentation impossible : " + details;
                return false;
            }
            catch (Exception ex)
            {
                error = "Configuration de l’alimentation impossible : " + ex.Message;
                return false;
            }
        }

        private static void TryDelete(string path)
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }

        private static int? WaitForProcess(string exePath, DateTime after)
        {
            var name = Path.GetFileNameWithoutExtension(exePath);

            for (var attempt = 0; attempt < 20; attempt++)
            {
                Thread.Sleep(250);

                foreach (var candidate in Process.GetProcessesByName(name))
                {
                    try
                    {
                        if (candidate.StartTime >= after.AddSeconds(-2) &&
                            string.Equals(candidate.MainModule?.FileName, exePath, StringComparison.OrdinalIgnoreCase))
                        {
                            return candidate.Id;
                        }
                    }
                    catch
                    {
                        // access denied / exited mid-check — ignore and keep looking
                    }
                    finally
                    {
                        candidate.Dispose();
                    }
                }
            }

            return null;
        }

        private static bool RunSchtasks(string[] arguments, out string error)
        {
            error = "";

            var psi = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            foreach (var arg in arguments)
            {
                psi.ArgumentList.Add(arg);
            }

            using var process = Process.Start(psi);

            if (process == null)
            {
                error = "Impossible de démarrer schtasks.exe.";
                return false;
            }

            process.WaitForExit(10000);

            if (process.ExitCode != 0)
            {
                error = process.StandardError.ReadToEnd().Trim();
                if (string.IsNullOrEmpty(error))
                {
                    error = "schtasks a échoué (code " + process.ExitCode + ").";
                }
                return false;
            }

            return true;
        }
    }
}
