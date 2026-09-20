using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using WorkerServiceControlWeb.Models;

namespace WorkerServiceControlWeb.Services
{
    public class WorkerProcessManager
    {
        // A service key is passed verbatim as a single CLI argument to the
        // worker exe (see StartService), and is used as the dictionary key /
        // URL parameter for that service. It must be one safe token — no
        // spaces, accents or punctuation — otherwise a worker exe's normal
        // log/banner output (full of spaces and "=") could get mistaken for
        // a list of service names during detection.
        private static readonly Regex ValidKeyPattern = new(@"^[A-Za-z0-9_.-]{1,60}$", RegexOptions.Compiled);

        private class RunningService
        {
            // "Active" means: the user clicked Exécuter and hasn't clicked
            // Arrêter since. It's deliberately NOT tied to whether the OS
            // process has actually exited — a one-shot worker (like
            // WorkerService1.exe) finishes its job and exits on its own,
            // but the dot should stay green until the user explicitly
            // stops it. Pid is only kept around so Stop can try to kill the
            // process if it happens to still be alive.
            public bool Active;
            public int? Pid;
            public Guid? RunId;
            public bool CompletionRecorded;
            public readonly object Lock = new();
        }

        private readonly string _contentRootPath;
        private readonly string _registryFilePath;
        private readonly string _runningPidsFilePath;
        private readonly string _logsDirectory;
        private readonly string _tasksDirectory;
        private readonly string _servicesDirectory;
        private readonly object _persistLock = new();
        private readonly object _pidLock = new();
        private readonly ServiceHistoryStore _historyStore;

        private readonly ConcurrentDictionary<string, RegisteredService> _registry = new();
        private readonly ConcurrentDictionary<string, RunningService> _runtime = new();

        public WorkerProcessManager(IConfiguration configuration, IHostEnvironment env, ServiceHistoryStore historyStore)
        {
            _historyStore = historyStore;
            _contentRootPath = env.ContentRootPath;
            _registryFilePath = Path.Combine(env.ContentRootPath, "App_Data", "registered-services.json");
            _runningPidsFilePath = Path.Combine(env.ContentRootPath, "App_Data", "running-pids.json");
            _logsDirectory = Path.Combine(env.ContentRootPath, "App_Data", "logs");
            _tasksDirectory = Path.Combine(env.ContentRootPath, "App_Data", "tasks");
            _servicesDirectory = Path.Combine(env.ContentRootPath, "App_Data", "services");

            LoadRegistry(configuration);
            LoadRunningPids();
            ReconcileRecoveredServices();
        }

        // Resolves a stored ExePath to an absolute path.
        // If it's already absolute (old-style), returns as-is — backwards-compatible.
        // If it's relative (new-style, starts with App_Data\...), joins with ContentRootPath
        // so services work correctly even after the app folder is moved to another PC.
        private string ResolveExePath(string exePath) =>
            Path.IsPathRooted(exePath) ? exePath : Path.Combine(_contentRootPath, exePath);

        private string GetLogFilePath(string key) => Path.Combine(_logsDirectory, key + ".log");

        // Services are launched via the Windows Task Scheduler (see
        // ScheduledProcessLauncher) so they survive this app being stopped
        // by any means. That means a fresh instance of this app has no
        // in-memory idea a service is still running — this persists
        // "service key -> PID" to disk so a new instance can recognize
        // "this service's process is still alive out there" and reflect
        // that as running again.
        private Dictionary<string, int> LoadRunningPidsFile()
        {
            lock (_pidLock)
            {
                if (!File.Exists(_runningPidsFilePath))
                {
                    return new Dictionary<string, int>();
                }

                try
                {
                    var json = File.ReadAllText(_runningPidsFilePath);
                    return JsonSerializer.Deserialize<Dictionary<string, int>>(json) ?? new Dictionary<string, int>();
                }
                catch
                {
                    return new Dictionary<string, int>();
                }
            }
        }

        private void PersistRunningPidsFile(Dictionary<string, int> pids)
        {
            lock (_pidLock)
            {
                var dir = Path.GetDirectoryName(_runningPidsFilePath)!;
                Directory.CreateDirectory(dir);

                var json = JsonSerializer.Serialize(pids, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_runningPidsFilePath, json);
            }
        }

        private void SaveRunningPid(string key, int pid)
        {
            // Keep the whole read-modify-write operation under one lock. The
            // per-method locks in Load/Persist alone allow two services to read
            // the same old dictionary and overwrite each other's update.
            lock (_pidLock)
            {
                var pids = LoadRunningPidsFile();
                pids[key] = pid;
                PersistRunningPidsFile(pids);
            }
        }

        private void RemoveRunningPid(string key)
        {
            lock (_pidLock)
            {
                var pids = LoadRunningPidsFile();

                if (pids.Remove(key))
                {
                    PersistRunningPidsFile(pids);
                }
            }
        }

        private void LoadRunningPids()
        {
            var pids = LoadRunningPidsFile();
            var stalePidEntries = new List<string>();

            foreach (var (key, state) in _runtime)
            {
                var lastEvent = _historyStore.GetHistory(key, 1).FirstOrDefault();

                if (lastEvent != null)
                {
                    state.RunId = lastEvent.RunId;
                    state.CompletionRecorded = string.Equals(lastEvent.Type, "completed", StringComparison.OrdinalIgnoreCase);
                }

                if (lastEvent != null &&
                    (string.Equals(lastEvent.Type, "completed", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(lastEvent.Type, "stopped", StringComparison.OrdinalIgnoreCase)))
                {
                    state.Active = string.Equals(lastEvent.Type, "completed", StringComparison.OrdinalIgnoreCase);
                    state.Pid = null;

                    if (pids.ContainsKey(key))
                    {
                        stalePidEntries.Add(key);
                    }

                    continue;
                }

                if (pids.TryGetValue(key, out var pid))
                {
                    state.Active = true;
                    state.Pid = pid;
                    state.RunId ??= Guid.NewGuid();
                    state.CompletionRecorded = false;
                }
                else if (lastEvent != null && string.Equals(lastEvent.Type, "started", StringComparison.OrdinalIgnoreCase))
                {
                    // The history is the durable source of truth. A missing PID
                    // will be reconciled as a completion below instead of making
                    // a completed service appear stopped after an app restart.
                    state.Active = true;
                    state.Pid = null;
                    state.CompletionRecorded = false;
                }
            }

            if (stalePidEntries.Count > 0)
            {
                lock (_pidLock)
                {
                    var currentPids = LoadRunningPidsFile();

                    foreach (var key in stalePidEntries)
                    {
                        currentPids.Remove(key);
                    }

                    PersistRunningPidsFile(currentPids);
                }
            }
        }

        private void ReconcileRecoveredServices()
        {
            foreach (var (key, state) in _runtime)
            {
                if (!_registry.TryGetValue(key, out var registered))
                {
                    continue;
                }

                int? pidToMonitor = null;
                Guid? runIdToMonitor = null;

                lock (state.Lock)
                {
                    if (!state.Active)
                    {
                        continue;
                    }

                    var exeName = Path.GetFileNameWithoutExtension(ResolveExePath(registered.ExePath));
                    var stillAlive = state.Pid.HasValue && ProcessUtils.IsProcessAlive(state.Pid.Value, exeName);

                    if (stillAlive)
                    {
                        state.RunId ??= Guid.NewGuid();
                        pidToMonitor = state.Pid;
                        runIdToMonitor = state.RunId;
                    }
                    else
                    {
                        TryRecordCompletionLocked(key, registered, state);
                    }
                }

                if (pidToMonitor.HasValue && runIdToMonitor.HasValue)
                {
                    StartProcessExitMonitor(key, pidToMonitor.Value, runIdToMonitor.Value);
                }
            }
        }

        private void StartProcessExitMonitor(string serviceName, int pid, Guid runId)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        await Task.Delay(500).ConfigureAwait(false);

                        if (!_registry.TryGetValue(serviceName, out var registered) ||
                            !_runtime.TryGetValue(serviceName, out var state))
                        {
                            return;
                        }

                        lock (state.Lock)
                        {
                            if (!state.Active || state.Pid != pid || state.RunId != runId)
                            {
                                return;
                            }

                            var exeName = Path.GetFileNameWithoutExtension(ResolveExePath(registered.ExePath));

                            if (ProcessUtils.IsProcessAlive(pid, exeName))
                            {
                                continue;
                            }

                            if (TryRecordCompletionLocked(serviceName, registered, state))
                            {
                                return;
                            }
                        }
                    }
                }
                catch
                {
                    // GetStatus performs the same reconciliation as a fallback.
                }
            });
        }

        // Caller must hold state.Lock. History is appended before the in-memory
        // completion flag is changed, and the store de-duplicates by RunId/type;
        // this makes retries after an app restart idempotent.
        private bool TryRecordCompletionLocked(string serviceName, RegisteredService registered, RunningService state)
        {
            if (!state.Active || state.CompletionRecorded)
            {
                return true;
            }

            var runId = state.RunId ?? Guid.NewGuid();
            var completedEvent = TryAppendHistory(new ServiceHistoryEvent
            {
                EventId = Guid.NewGuid(),
                RunId = runId,
                ServiceKey = serviceName,
                DisplayName = registered.DisplayName,
                Type = "completed",
                OccurredAtUtc = DateTimeOffset.UtcNow,
                Actor = "system",
                Message = "Traitement terminé ; le service est en attente.",
                Pid = state.Pid
            });

            if (completedEvent == null)
            {
                return false;
            }

            state.RunId = completedEvent.RunId;
            state.CompletionRecorded = true;
            state.Pid = null;

            try
            {
                RemoveRunningPid(serviceName);
            }
            catch
            {
                // The completed event remains authoritative. Startup
                // reconciliation removes any stale PID left on disk.
            }

            return true;
        }

        private ServiceHistoryEvent? TryAppendHistory(ServiceHistoryEvent historyEvent)
        {
            try
            {
                return _historyStore.Append(historyEvent);
            }
            catch
            {
                // Lifecycle control must remain available if the audit disk is
                // temporarily unavailable. Completion is retried by the monitor
                // and status fallback; start/stop retain their normal behavior.
                return null;
            }
        }

        private void LoadRegistry(IConfiguration configuration)
        {
            if (File.Exists(_registryFilePath))
            {
                try
                {
                    var json = File.ReadAllText(_registryFilePath);
                    var items = JsonSerializer.Deserialize<List<RegisteredService>>(json) ?? new List<RegisteredService>();

                    foreach (var item in items)
                    {
                        Register(item);
                    }

                    if (_registry.Count > 0)
                    {
                        return;
                    }
                }
                catch
                {
                    // Corrupt or unreadable file — fall through and reseed defaults.
                }
            }

            // First run (or empty/corrupt file): seed with the two services this app shipped with.
            var defaultPath = configuration["WorkerServicePath"] ?? "";

            Register(new RegisteredService { Key = "EspritVersSesam", DisplayName = "Service Esprit vers Sesam", ExePath = defaultPath });
            Register(new RegisteredService { Key = "SesamVersEsprit", DisplayName = "Service Sesam vers Esprit", ExePath = defaultPath });

            PersistRegistry();
        }

        private void Register(RegisteredService service)
        {
            _registry[service.Key] = service;
            _runtime.TryAdd(service.Key, new RunningService());
        }

        private void PersistRegistry()
        {
            lock (_persistLock)
            {
                var dir = Path.GetDirectoryName(_registryFilePath)!;
                Directory.CreateDirectory(dir);

                var json = JsonSerializer.Serialize(_registry.Values.ToList(), new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_registryFilePath, json);
            }
        }

        public List<RegisteredService> GetRegisteredServices()
        {
            return _registry.Values.OrderBy(s => s.DisplayName).ToList();
        }

        public RegisteredService? FindRegisteredService(string key)
        {
            if (!_registry.TryGetValue(key, out var service))
            {
                return null;
            }

            // Do not expose the mutable object held by the registry.
            return new RegisteredService
            {
                Key = service.Key,
                DisplayName = service.DisplayName,
                ExePath = service.ExePath
            };
        }

        public List<ServiceHistoryEvent> GetHistory(string key, int take = 50)
        {
            return _registry.ContainsKey(key)
                ? _historyStore.GetHistory(key, take)
                : new List<ServiceHistoryEvent>();
        }

        // Logs only ever get wiped when the user explicitly asks for it
        // here — never automatically on start/stop. Works even while the
        // service is running (FileShare.ReadWrite lets us truncate while
        // the worker still has the file open for append).
        public bool ClearLogs(string key, out string error)
        {
            error = "";

            if (!_registry.TryGetValue(key, out _) || !_runtime.TryGetValue(key, out var state))
            {
                error = "Service inconnu.";
                return false;
            }

            lock (state.Lock)
            {
                try
                {
                    var logFilePath = GetLogFilePath(key);
                    Directory.CreateDirectory(Path.GetDirectoryName(logFilePath)!);

                    using var stream = new FileStream(logFilePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    return false;
                }
            }

            return true;
        }

        // History only ever gets wiped when the user explicitly asks for it
        // here — never automatically on start/stop.
        public bool ClearHistory(string key, out string error)
        {
            error = "";

            if (!_registry.ContainsKey(key))
            {
                error = "Service inconnu.";
                return false;
            }

            _historyStore.RemoveHistory(key);
            return true;
        }

        public bool RemoveService(string key, out string error)
        {
            error = "";

            if (!_registry.TryGetValue(key, out var registered))
            {
                error = "Service inconnu.";
                return false;
            }

            if (_runtime.TryGetValue(key, out var state))
            {
                lock (state.Lock)
                {
                    if (state.Pid.HasValue)
                    {
                        var exeName = Path.GetFileNameWithoutExtension(ResolveExePath(registered.ExePath));

                        if (ProcessUtils.IsProcessAlive(state.Pid.Value, exeName))
                        {
                            ProcessUtils.KillPid(state.Pid.Value);
                        }

                        state.Pid = null;
                    }

                    state.Active = false;
                }
            }

            _registry.TryRemove(key, out _);
            _runtime.TryRemove(key, out _);
            RemoveRunningPid(key);
            _historyStore.RemoveHistory(key);

            try { File.Delete(GetLogFilePath(key)); } catch { /* best effort */ }
            try { Directory.Delete(Path.Combine(_servicesDirectory, key), recursive: true); } catch { /* best effort */ }

            PersistRegistry();

            return true;
        }

        public bool AddService(string exePath, string key, string displayName, out string error)
        {
            error = "";
            key = key.Trim();

            if (string.IsNullOrWhiteSpace(key))
            {
                error = "Clé de service manquante.";
                return false;
            }

            if (!ValidKeyPattern.IsMatch(key))
            {
                error = "Clé invalide : seules les lettres, chiffres, '_', '-', '.' sont autorisés (pas d'espaces).";
                return false;
            }

            if (string.IsNullOrWhiteSpace(exePath))
            {
                error = "Chemin de l'exécutable manquant.";
                return false;
            }

            if (_registry.ContainsKey(key))
            {
                error = $"Un service avec la clé '{key}' existe déjà.";
                return false;
            }

            // Copy the entire exe directory into App_Data/services/<key>/ so the
            // app folder is self-contained and can be moved to another PC as-is.
            var sourceDir = Path.GetDirectoryName(Path.GetFullPath(exePath))!;
            var destDir = Path.Combine(_servicesDirectory, key);

            try
            {
                CopyDirectory(sourceDir, destDir);
            }
            catch (Exception ex)
            {
                error = "Impossible de copier le répertoire du service : " + ex.Message;
                return false;
            }

            // Store a relative path (no drive letter) so it resolves on any machine.
            var relativePath = Path.Combine("App_Data", "services", key, Path.GetFileName(exePath));

            Register(new RegisteredService
            {
                Key = key,
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? key : displayName.Trim(),
                ExePath = relativePath
            });

            PersistRegistry();

            return true;
        }

        private static void CopyDirectory(string source, string dest)
        {
            Directory.CreateDirectory(dest);

            foreach (var file in Directory.GetFiles(source))
            {
                File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: true);
            }

            foreach (var dir in Directory.GetDirectories(source))
            {
                CopyDirectory(dir, Path.Combine(dest, Path.GetFileName(dir)));
            }
        }

        public List<string> DetectServices(string exePath, out string error, out string method)
        {
            error = "";
            method = "";

            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            {
                return new List<string>();
            }

            var projectRoot = FindProjectRoot(exePath);

            if (projectRoot != null)
            {
                var fromSource = DetectServicesFromSource(projectRoot);

                if (fromSource.Count > 0)
                {
                    method = "source";
                    return fromSource;
                }
            }

            method = "runtime";
            return DetectServicesAtRuntime(exePath, out error);
        }

        // Walks up from the exe's folder looking for the project root —
        // the nearest ancestor directory that directly contains a .csproj.
        // Handles the standard bin/Debug/<tfm>/x.exe layout used when the
        // exe and its source live on the same dev machine.
        private static string? FindProjectRoot(string exePath)
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(exePath));
            var depth = 0;

            while (!string.IsNullOrEmpty(dir) && depth < 6)
            {
                try
                {
                    if (Directory.GetFiles(dir, "*.csproj").Length > 0)
                    {
                        return dir;
                    }
                }
                catch
                {
                    return null;
                }

                dir = Path.GetDirectoryName(dir);
                depth++;
            }

            return null;
        }

        // Reads the dispatch literals straight out of the project's source
        // code instead of running the exe — safe (no side effects) and
        // reflects the actual logic, not guessed log output. Matches the
        // common pattern used to compare an args-derived service name
        // against known values: if/else-if "== \"X\"" and switch "case \"X\":".
        private static readonly Regex SourceLiteralPattern = new(
            "(?:==\\s*\"([^\"]+)\"|case\\s*\"([^\"]+)\"\\s*:)",
            RegexOptions.Compiled);

        private static List<string> DetectServicesFromSource(string projectRoot)
        {
            var found = new List<string>();

            IEnumerable<string> csFiles;

            try
            {
                csFiles = Directory.EnumerateFiles(projectRoot, "*.cs", SearchOption.AllDirectories)
                    .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                             && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                             && !f.Contains($"{Path.DirectorySeparatorChar}.vs{Path.DirectorySeparatorChar}")
                             && !f.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}"));
            }
            catch
            {
                return found;
            }

            foreach (var file in csFiles)
            {
                string content;

                try
                {
                    content = File.ReadAllText(file);
                }
                catch
                {
                    continue;
                }

                foreach (Match match in SourceLiteralPattern.Matches(content))
                {
                    var token = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;

                    if (ValidKeyPattern.IsMatch(token) && !found.Contains(token))
                    {
                        found.Add(token);
                    }
                }
            }

            return found;
        }

        private List<string> DetectServicesAtRuntime(string exePath, out string error)
        {
            error = "";
            var detected = new List<string>();

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = "--list-services",
                    WorkingDirectory = Path.GetDirectoryName(exePath) ?? string.Empty,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi);

                if (process == null)
                {
                    error = "Impossible de démarrer le processus.";
                    return detected;
                }

                var exited = process.WaitForExit(5000);

                if (!exited)
                {
                    try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                    error = "Le programme n'a pas répondu (délai dépassé).";
                    return detected;
                }

                var output = process.StandardOutput.ReadToEnd();

                foreach (var line in output.Split(Environment.NewLine))
                {
                    var trimmed = line.Trim();

                    // Real worker output (banners, log lines, French sentences)
                    // is full of spaces/accents/punctuation and would never
                    // pass this — only a genuine single-token service name
                    // (the convention --list-services is meant to produce)
                    // gets through. This is what stops an exe that doesn't
                    // implement the convention from flooding the registry
                    // with its normal startup logs as fake "services".
                    if (ValidKeyPattern.IsMatch(trimmed))
                    {
                        detected.Add(trimmed);
                    }
                }

                if (detected.Count == 0)
                {
                    error = "Aucun service détecté automatiquement. Vous pouvez les ajouter manuellement.";
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            return detected;
        }

        public bool StartService(string serviceName, out string error, string? actor = null)
        {
            error = "";

            if (!_registry.TryGetValue(serviceName, out var registered) || !_runtime.TryGetValue(serviceName, out var state))
            {
                error = "Service inconnu.";
                return false;
            }

            var logFilePath = GetLogFilePath(serviceName);
            int? pidToMonitor = null;
            Guid? runIdToMonitor = null;

            lock (state.Lock)
            {
                if (state.Active)
                {
                    error = "Le service est déjà en cours d'exécution.";
                    return false;
                }

                if (string.IsNullOrWhiteSpace(registered.ExePath))
                {
                    error = "Configuration manquante.";
                    AppendLog(logFilePath, "Erreur : chemin de l'exécutable introuvable pour ce service.");
                    return false;
                }

                var resolvedExePath = ResolveExePath(registered.ExePath);

                if (!File.Exists(resolvedExePath))
                {
                    error = "Exécutable introuvable.";
                    AppendLog(logFilePath, "Erreur : exécutable introuvable.");
                    AppendLog(logFilePath, $"Chemin utilisé : {resolvedExePath}");
                    return false;
                }

                var workingDirectory = Path.GetDirectoryName(resolvedExePath) ?? string.Empty;
                var pid = ScheduledProcessLauncher.Start(resolvedExePath, serviceName, workingDirectory, logFilePath, _tasksDirectory, out var startError);

                if (pid == null)
                {
                    error = "Démarrage impossible.";
                    AppendLog(logFilePath, "Erreur : impossible de démarrer le processus.");
                    AppendLog(logFilePath, startError);
                    return false;
                }

                var runId = Guid.NewGuid();
                state.Active = true;
                state.Pid = pid;
                state.RunId = runId;
                state.CompletionRecorded = false;
                SaveRunningPid(serviceName, pid.Value);

                // Announce and persist "started" only after the scheduler has
                // returned a concrete process identity.
                AppendLog(logFilePath, $"[{DateTime.Now:HH:mm:ss}] Service démarré.");
                TryAppendHistory(new ServiceHistoryEvent
                {
                    EventId = Guid.NewGuid(),
                    RunId = runId,
                    ServiceKey = serviceName,
                    DisplayName = registered.DisplayName,
                    Type = "started",
                    OccurredAtUtc = DateTimeOffset.UtcNow,
                    Actor = string.IsNullOrWhiteSpace(actor) ? "system" : actor.Trim(),
                    Message = "Service démarré.",
                    Pid = pid
                });

                pidToMonitor = pid;
                runIdToMonitor = runId;
            }

            if (pidToMonitor.HasValue && runIdToMonitor.HasValue)
            {
                StartProcessExitMonitor(serviceName, pidToMonitor.Value, runIdToMonitor.Value);
            }

            return true;
        }

        public bool StopService(string serviceName, out string error, string? actor = null)
        {
            error = "";

            if (!_registry.TryGetValue(serviceName, out var registered) || !_runtime.TryGetValue(serviceName, out var state))
            {
                error = "Service inconnu.";
                return false;
            }

            lock (state.Lock)
            {
                if (!state.Active)
                {
                    error = "Le service n'est pas en cours d'exécution.";
                    return false;
                }

                // Best effort: kill it if it happens to still be running.
                // A one-shot worker will typically have already finished on
                // its own by the time the user clicks Arrêter — that's fine,
                // taskkill just no-ops on a PID that's no longer there.
                var runId = state.RunId ?? Guid.NewGuid();
                var stoppedPid = state.Pid;

                if (state.Pid.HasValue)
                {
                    // A PID may have been recycled after a one-shot worker
                    // completed. Never taskkill it unless it still identifies
                    // the expected executable.
                    var exeName = Path.GetFileNameWithoutExtension(ResolveExePath(registered.ExePath));

                    if (ProcessUtils.IsProcessAlive(state.Pid.Value, exeName))
                    {
                        ProcessUtils.KillPid(state.Pid.Value);
                    }
                    else
                    {
                        TryRecordCompletionLocked(serviceName, registered, state);
                    }
                }
                else
                {
                    TryRecordCompletionLocked(serviceName, registered, state);
                }

                runId = state.RunId ?? runId;

                AppendLog(GetLogFilePath(serviceName), $"[{DateTime.Now:HH:mm:ss}] Service arrêté.");

                TryAppendHistory(new ServiceHistoryEvent
                {
                    EventId = Guid.NewGuid(),
                    RunId = runId,
                    ServiceKey = serviceName,
                    DisplayName = registered.DisplayName,
                    Type = "stopped",
                    OccurredAtUtc = DateTimeOffset.UtcNow,
                    Actor = string.IsNullOrWhiteSpace(actor) ? "system" : actor.Trim(),
                    Message = "Service arrêté manuellement.",
                    Pid = stoppedPid
                });

                state.Active = false;
                state.Pid = null;
                state.RunId = runId;
                state.CompletionRecorded = true;
                RemoveRunningPid(serviceName);
            }

            return true;
        }

        // The worker process keeps its log file open (and locked for writes)
        // for as long as it runs, so a plain File.ReadAllLines can throw a
        // sharing violation while it's still running. Opening explicitly
        // with FileShare.ReadWrite lets us read it concurrently.
        private static List<string> ReadLogFile(string logFilePath)
        {
            try
            {
                if (!File.Exists(logFilePath))
                {
                    return new List<string>();
                }

                using var stream = new FileStream(logFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);

                var lines = new List<string>();
                string? line;

                while ((line = reader.ReadLine()) != null)
                {
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        lines.Add(line);
                    }
                }

                return lines;
            }
            catch
            {
                return new List<string>();
            }
        }

        private static void AppendLog(string logFilePath, string line)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(logFilePath)!);

                using var stream = new FileStream(logFilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                using var writer = new StreamWriter(stream);
                writer.WriteLine(line);
            }
            catch
            {
                // best effort — surfaced logs are a convenience, not critical
            }
        }

        public ServiceStatus? GetStatus(string serviceName)
        {
            if (!_registry.TryGetValue(serviceName, out var registered) || !_runtime.TryGetValue(serviceName, out var state))
            {
                return null;
            }

            lock (state.Lock)
            {
                // Logs accumulate across start/stop cycles — only the
                // explicit "Vider les logs" button clears them.
                var logs = ReadLogFile(GetLogFilePath(serviceName));

                string runtimeState;

                if (!state.Active)
                {
                    runtimeState = "stopped";
                }
                else
                {
                    var exeName = Path.GetFileNameWithoutExtension(ResolveExePath(registered.ExePath));
                    var stillAlive = state.Pid.HasValue && ProcessUtils.IsProcessAlive(state.Pid.Value, exeName);

                    if (!stillAlive)
                    {
                        // Fallback for an exit that happened before the monitor
                        // attached, while the app was down, or after its task
                        // encountered a transient error.
                        TryRecordCompletionLocked(serviceName, registered, state);
                    }

                    runtimeState = stillAlive ? "running" : "waiting";
                }

                var historySummary = _historyStore.GetSummary(serviceName);

                return new ServiceStatus
                {
                    Key = serviceName,
                    DisplayName = registered.DisplayName,
                    State = runtimeState,
                    Logs = logs,
                    HistoryVersion = historySummary.Version,
                    LastEvent = historySummary.LastEvent
                };
            }
        }

        public List<ServiceStatus> GetAllStatuses()
        {
            return _registry.Keys
                .Select(GetStatus)
                .Where(s => s != null)
                .Select(s => s!)
                .OrderBy(s => s.DisplayName)
                .ToList();
        }

        // Returns the keys of service folders that exist inside App_Data/services/.
        // Only services added via the UI (which were copied) appear here.
        public List<string> GetServiceFolderKeys()
        {
            return _registry.Keys
                .Where(key => Directory.Exists(Path.Combine(_servicesDirectory, key)))
                .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        // Lists files and subdirectories at App_Data/services/<key>/<subpath>.
        // Returns an empty list on path-traversal attempts or missing directories.
        public List<ServiceFileEntry> GetServiceFiles(string key, string? subpath)
        {
            if (!TryResolveServicePath(key, subpath, out var root, out var target) ||
                !Directory.Exists(target))
            {
                return new List<ServiceFileEntry>();
            }

            var entries = new List<ServiceFileEntry>();

            foreach (var d in Directory.GetDirectories(target).OrderBy(x => Path.GetFileName(x), StringComparer.OrdinalIgnoreCase))
            {
                if (IsSensitiveServicePath(root, d) || IsReparsePoint(d))
                {
                    continue;
                }

                entries.Add(new ServiceFileEntry
                {
                    Name = Path.GetFileName(d),
                    RelativePath = Path.GetRelativePath(root, d),
                    IsDirectory = true
                });
            }

            foreach (var f in Directory.GetFiles(target).OrderBy(x => Path.GetFileName(x), StringComparer.OrdinalIgnoreCase))
            {
                if (IsSensitiveServicePath(root, f) || IsReparsePoint(f))
                {
                    continue;
                }

                entries.Add(new ServiceFileEntry
                {
                    Name = Path.GetFileName(f),
                    RelativePath = Path.GetRelativePath(root, f),
                    IsDirectory = false,
                    Size = new FileInfo(f).Length
                });
            }

            return entries;
        }

        // Returns the text content of a file inside App_Data/services/<key>/.
        // Returns null for binary files, files over 512 KB, or path-traversal attempts.
        public string? GetServiceFileContent(string key, string relativePath)
        {
            if (!TryResolveServicePath(key, relativePath, out var root, out var full) ||
                !File.Exists(full) ||
                IsSensitiveServicePath(root, full) ||
                IsReparsePoint(full))
            {
                return null;
            }

            if (new FileInfo(full).Length > 512 * 1024)
            {
                return null;
            }

            try
            {
                return File.ReadAllText(full);
            }
            catch
            {
                return null;
            }
        }

        private bool TryResolveServicePath(
            string key,
            string? relativePath,
            out string root,
            out string target)
        {
            root = "";
            target = "";

            if (!_registry.ContainsKey(key) || !ValidKeyPattern.IsMatch(key))
            {
                return false;
            }

            try
            {
                root = Path.TrimEndingDirectorySeparator(
                    Path.GetFullPath(Path.Combine(_servicesDirectory, key)));
                target = string.IsNullOrWhiteSpace(relativePath)
                    ? root
                    : Path.GetFullPath(Path.Combine(root, relativePath));

                var relative = Path.GetRelativePath(root, target);

                return relative == "." ||
                    (!Path.IsPathRooted(relative) &&
                     !string.Equals(relative, "..", StringComparison.OrdinalIgnoreCase) &&
                     !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                     !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                root = "";
                target = "";
                return false;
            }
        }

        private static bool IsSensitiveServicePath(string root, string path)
        {
            var relative = Path.GetRelativePath(root, path)
                .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            var segments = relative.Split(
                Path.DirectorySeparatorChar,
                StringSplitOptions.RemoveEmptyEntries);

            if (segments.Any(segment =>
                    string.Equals(segment, ".settings-backups", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            var fileName = Path.GetFileName(path);

            return fileName.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase) ||
                   fileName.EndsWith(".bak", StringComparison.OrdinalIgnoreCase) ||
                   fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsReparsePoint(string path)
        {
            try
            {
                return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
            }
            catch
            {
                return true;
            }
        }
    }
}
