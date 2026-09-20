using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using WorkerServiceControlWeb.Models;

namespace WorkerServiceControlWeb.Services
{
    public sealed class ServiceAppSettingsStore
    {
        private const string SettingsFileName = "appsettings.json";
        private const string MissingVersion = "missing";
        private const int MaxSettingsBytes = 512 * 1024;
        private const int MaxJsonDepth = 64;
        private const int BackupsToKeep = 5;

        private static readonly Regex ValidKeyPattern = new(
            @"^[A-Za-z0-9_.-]{1,60}$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly UTF8Encoding StrictUtf8 = new(
            encoderShouldEmitUTF8Identifier: false,
            throwOnInvalidBytes: true);

        private static readonly StringComparer FileSystemPathComparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

        private static readonly StringComparison FileSystemPathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        private readonly WorkerProcessManager _workerProcessManager;
        private readonly string _contentRootPath;
        private readonly string _servicesDirectory;
        private readonly IReadOnlyList<string> _allowedExternalRoots;
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _pathLocks = new(FileSystemPathComparer);

        public ServiceAppSettingsStore(
            WorkerProcessManager workerProcessManager,
            IHostEnvironment environment,
            IConfiguration configuration)
        {
            _workerProcessManager = workerProcessManager;
            _contentRootPath = Path.GetFullPath(environment.ContentRootPath);
            _servicesDirectory = Path.GetFullPath(Path.Combine(_contentRootPath, "App_Data", "services"));
            _allowedExternalRoots = LoadAllowedExternalRoots(configuration);
        }

        public async Task<ServiceAppSettingsSnapshot> ReadAsync(string key, CancellationToken cancellationToken)
        {
            var resolution = ResolveTarget(key);

            if (!resolution.Success)
            {
                return SnapshotFailure(resolution.NotFound, resolution.Error);
            }

            var target = resolution.Target!;
            var pathLock = _pathLocks.GetOrAdd(target.SettingsPath, static _ => new SemaphoreSlim(1, 1));

            await pathLock.WaitAsync(cancellationToken);

            try
            {
                if (!IsTargetStillSafe(target))
                {
                    return SnapshotFailure(false, "La configuration de ce service n'est pas accessible.");
                }

                var current = await ReadCurrentFileAsync(target.SettingsPath, cancellationToken);

                if (!current.Exists)
                {
                    return new ServiceAppSettingsSnapshot
                    {
                        Success = true,
                        Exists = false,
                        Content = "{\r\n}\r\n",
                        Version = MissingVersion,
                        IsValid = true,
                        FileName = SettingsFileName
                    };
                }

                string content;

                try
                {
                    content = DecodeUtf8(current.Bytes);
                }
                catch (DecoderFallbackException)
                {
                    return new ServiceAppSettingsSnapshot
                    {
                        Success = true,
                        Exists = true,
                        Content = "",
                        Version = current.Version,
                        LastModifiedUtc = current.LastModifiedUtc,
                        IsValid = false,
                        ValidationError = "Le fichier doit être encodé en UTF-8.",
                        FileName = SettingsFileName
                    };
                }

                var validation = ValidateJson(content);

                return new ServiceAppSettingsSnapshot
                {
                    Success = true,
                    Exists = true,
                    Content = content,
                    Version = current.Version,
                    LastModifiedUtc = current.LastModifiedUtc,
                    IsValid = validation.IsValid,
                    ValidationError = validation.Error,
                    FileName = SettingsFileName
                };
            }
            catch (SettingsFileTooLargeException)
            {
                return SnapshotFailure(false, "Le fichier appsettings.json dépasse la taille maximale autorisée de 512 Kio.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return SnapshotFailure(false, "Impossible de lire la configuration du service.");
            }
            finally
            {
                pathLock.Release();
            }
        }

        public async Task<ServiceAppSettingsSaveResult> SaveAsync(
            string key,
            string? content,
            string? expectedVersion,
            CancellationToken cancellationToken)
        {
            var resolution = ResolveTarget(key);

            if (!resolution.Success)
            {
                return SaveFailure(resolution.NotFound, false, resolution.Error);
            }

            if (string.IsNullOrWhiteSpace(expectedVersion))
            {
                return SaveFailure(false, false, "La version attendue est obligatoire.");
            }

            if (content is null)
            {
                return SaveFailure(false, false, "Le contenu de la configuration est obligatoire.");
            }

            byte[] newBytes;

            try
            {
                newBytes = StrictUtf8.GetBytes(content);
            }
            catch (EncoderFallbackException)
            {
                return SaveFailure(false, false, "Le contenu doit être encodé en UTF-8 valide.");
            }

            if (newBytes.Length > MaxSettingsBytes)
            {
                return SaveFailure(false, false, "Le contenu dépasse la taille maximale autorisée de 512 Kio.");
            }

            var validation = ValidateJson(content);

            if (!validation.IsValid)
            {
                return SaveFailure(false, false, validation.Error);
            }

            var target = resolution.Target!;
            var pathLock = _pathLocks.GetOrAdd(target.SettingsPath, static _ => new SemaphoreSlim(1, 1));

            await pathLock.WaitAsync(cancellationToken);

            try
            {
                if (!IsTargetStillSafe(target))
                {
                    return SaveFailure(false, false, "La configuration de ce service n'est pas accessible.");
                }

                var current = await ReadCurrentFileAsync(target.SettingsPath, cancellationToken);

                if (!string.Equals(expectedVersion, current.Version, StringComparison.Ordinal))
                {
                    return new ServiceAppSettingsSaveResult
                    {
                        Conflict = true,
                        Version = current.Version,
                        LastModifiedUtc = current.LastModifiedUtc,
                        Error = "Le fichier a été modifié depuis son ouverture. Rechargez-le avant d'enregistrer."
                    };
                }

                var temporaryPath = Path.Combine(
                    target.ExecutableDirectory,
                    $".appsettings.{Guid.NewGuid():N}.tmp");

                try
                {
                    await WriteTemporaryFileAsync(temporaryPath, newBytes, cancellationToken);

                    // Recheck immediately before committing. The semaphore serializes
                    // writes made through this store; this second check also catches
                    // a cooperative external editor that changed the file while the
                    // temporary file was being flushed.
                    var beforeCommit = await ReadCurrentFileAsync(target.SettingsPath, cancellationToken);

                    if (!string.Equals(beforeCommit.Version, current.Version, StringComparison.Ordinal))
                    {
                        return new ServiceAppSettingsSaveResult
                        {
                            Conflict = true,
                            Version = beforeCommit.Version,
                            LastModifiedUtc = beforeCommit.LastModifiedUtc,
                            Error = "Le fichier a été modifié depuis son ouverture. Rechargez-le avant d'enregistrer."
                        };
                    }

                    if (!IsTargetStillSafe(target))
                    {
                        return SaveFailure(false, false, "La configuration de ce service n'est pas accessible.");
                    }

                    string? backupDirectory = null;

                    if (beforeCommit.Exists)
                    {
                        backupDirectory = EnsureBackupDirectory(target);
                        var backupPath = Path.Combine(
                            backupDirectory,
                            $"appsettings-{DateTime.UtcNow:yyyyMMdd'T'HHmmssfffffff'Z'}-{Guid.NewGuid():N}.json");

                        File.Replace(
                            temporaryPath,
                            target.SettingsPath,
                            backupPath,
                            ignoreMetadataErrors: true);
                    }
                    else
                    {
                        File.Move(temporaryPath, target.SettingsPath);
                    }

                    if (backupDirectory is not null)
                    {
                        PruneBackups(backupDirectory);
                    }

                    return new ServiceAppSettingsSaveResult
                    {
                        Success = true,
                        Version = ComputeVersion(newBytes),
                        LastModifiedUtc = GetLastModifiedUtc(target.SettingsPath)
                    };
                }
                finally
                {
                    try
                    {
                        if (File.Exists(temporaryPath))
                        {
                            File.Delete(temporaryPath);
                        }
                    }
                    catch
                    {
                        // A stale temporary file is safer than risking the live file.
                    }
                }
            }
            catch (SettingsFileTooLargeException)
            {
                return SaveFailure(false, false, "Le fichier appsettings.json dépasse la taille maximale autorisée de 512 Kio.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return SaveFailure(false, false, "Impossible d'enregistrer la configuration du service.");
            }
            finally
            {
                pathLock.Release();
            }
        }

        private TargetResolution ResolveTarget(string key)
        {
            if (string.IsNullOrWhiteSpace(key) || !ValidKeyPattern.IsMatch(key))
            {
                return TargetResolution.NotFoundResult();
            }

            var registered = _workerProcessManager.FindRegisteredService(key);

            if (registered is null ||
                !string.Equals(registered.Key, key, StringComparison.Ordinal) ||
                !ValidKeyPattern.IsMatch(registered.Key))
            {
                return TargetResolution.NotFoundResult();
            }

            if (string.IsNullOrWhiteSpace(registered.ExePath))
            {
                return TargetResolution.UnsafeResult();
            }

            try
            {
                var storedExePath = registered.ExePath.Trim();
                var isFullyQualified = Path.IsPathFullyQualified(storedExePath);

                // Reject drive-relative and current-drive-rooted forms. Only a
                // normal relative path or a fully-qualified absolute path has
                // deterministic resolution.
                if (Path.IsPathRooted(storedExePath) && !isFullyQualified)
                {
                    return TargetResolution.UnsafeResult();
                }

                var executablePath = isFullyQualified
                    ? Path.GetFullPath(storedExePath)
                    : Path.GetFullPath(Path.Combine(_contentRootPath, storedExePath));

                var executableDirectory = Path.GetDirectoryName(executablePath);

                if (string.IsNullOrEmpty(executableDirectory) ||
                    !Directory.Exists(executableDirectory) ||
                    !File.Exists(executablePath))
                {
                    return TargetResolution.UnsafeResult();
                }

                var managedRoot = Path.GetFullPath(Path.Combine(_servicesDirectory, registered.Key));
                string? allowedRoot = null;

                if (IsWithinOrEqual(executablePath, managedRoot))
                {
                    allowedRoot = managedRoot;
                }
                else if (isFullyQualified)
                {
                    allowedRoot = _allowedExternalRoots.FirstOrDefault(root => IsWithinOrEqual(executablePath, root));
                }

                if (allowedRoot is null || !Directory.Exists(allowedRoot))
                {
                    return TargetResolution.UnsafeResult();
                }

                var settingsPath = Path.GetFullPath(Path.Combine(executableDirectory, SettingsFileName));

                if (!IsWithinOrEqual(settingsPath, executableDirectory) ||
                    !IsWithinOrEqual(settingsPath, allowedRoot))
                {
                    return TargetResolution.UnsafeResult();
                }

                var target = new SettingsTarget(
                    executablePath,
                    executableDirectory,
                    settingsPath,
                    allowedRoot);

                return IsTargetStillSafe(target)
                    ? TargetResolution.SuccessResult(target)
                    : TargetResolution.UnsafeResult();
            }
            catch
            {
                return TargetResolution.UnsafeResult();
            }
        }

        private IReadOnlyList<string> LoadAllowedExternalRoots(IConfiguration configuration)
        {
            var roots = new HashSet<string>(FileSystemPathComparer);
            var entries = configuration.GetSection("ServiceSettings:AllowedExternalRoots").GetChildren();

            foreach (var entry in entries)
            {
                var configuredRoot = entry.Value?.Trim();

                if (string.IsNullOrEmpty(configuredRoot))
                {
                    continue;
                }

                if (!Path.IsPathFullyQualified(configuredRoot))
                {
                    throw new InvalidOperationException(
                        "Every ServiceSettings:AllowedExternalRoots entry must be a fully-qualified path.");
                }

                roots.Add(Path.TrimEndingDirectorySeparator(Path.GetFullPath(configuredRoot)));
            }

            return roots.ToArray();
        }

        private static JsonValidationResult ValidateJson(string content)
        {
            try
            {
                using var document = JsonDocument.Parse(
                    content,
                    new JsonDocumentOptions
                    {
                        AllowTrailingCommas = true,
                        CommentHandling = JsonCommentHandling.Skip,
                        MaxDepth = MaxJsonDepth
                    });

                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    return JsonValidationResult.Invalid("La racine du document doit être un objet JSON.");
                }

                if (ContainsCaseInsensitiveDuplicateProperties(document.RootElement))
                {
                    return JsonValidationResult.Invalid(
                        "Le JSON contient des propriétés en double (la casse est ignorée)."
                    );
                }

                return JsonValidationResult.Valid();
            }
            catch (JsonException ex)
            {
                if (ex.LineNumber.HasValue && ex.BytePositionInLine.HasValue)
                {
                    return JsonValidationResult.Invalid(
                        $"JSON invalide (ligne {ex.LineNumber.Value + 1}, position {ex.BytePositionInLine.Value + 1}).");
                }

                return JsonValidationResult.Invalid("JSON invalide.");
            }
            catch
            {
                return JsonValidationResult.Invalid("JSON invalide.");
            }
        }

        private static bool ContainsCaseInsensitiveDuplicateProperties(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                {
                    var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    foreach (var property in element.EnumerateObject())
                    {
                        if (!names.Add(property.Name) ||
                            ContainsCaseInsensitiveDuplicateProperties(property.Value))
                        {
                            return true;
                        }
                    }

                    break;
                }

                case JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray())
                    {
                        if (ContainsCaseInsensitiveDuplicateProperties(item))
                        {
                            return true;
                        }
                    }

                    break;
            }

            return false;
        }

        private static async Task<CurrentFileState> ReadCurrentFileAsync(
            string settingsPath,
            CancellationToken cancellationToken)
        {
            if (!File.Exists(settingsPath))
            {
                return CurrentFileState.Missing();
            }

            await using var stream = new FileStream(
                settingsPath,
                new FileStreamOptions
                {
                    Mode = FileMode.Open,
                    Access = FileAccess.Read,
                    Share = FileShare.Read,
                    BufferSize = 16 * 1024,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan
                });

            if (stream.Length > MaxSettingsBytes)
            {
                throw new SettingsFileTooLargeException();
            }

            var bytes = new byte[(int)stream.Length];
            var offset = 0;

            while (offset < bytes.Length)
            {
                var count = await stream.ReadAsync(bytes.AsMemory(offset), cancellationToken);

                if (count == 0)
                {
                    throw new IOException("The settings file changed while it was being read.");
                }

                offset += count;
            }

            return new CurrentFileState(
                true,
                bytes,
                ComputeVersion(bytes),
                GetLastModifiedUtc(settingsPath));
        }

        private static async Task WriteTemporaryFileAsync(
            string temporaryPath,
            byte[] content,
            CancellationToken cancellationToken)
        {
            await using var stream = new FileStream(
                temporaryPath,
                new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    BufferSize = 16 * 1024,
                    Options = FileOptions.Asynchronous | FileOptions.WriteThrough
                });

            await stream.WriteAsync(content, cancellationToken);
            await stream.FlushAsync(cancellationToken);
            stream.Flush(flushToDisk: true);
        }

        private static string EnsureBackupDirectory(SettingsTarget target)
        {
            var backupDirectory = Path.GetFullPath(
                Path.Combine(target.ExecutableDirectory, ".settings-backups"));

            if (!IsWithinOrEqual(backupDirectory, target.ExecutableDirectory) ||
                !IsWithinOrEqual(backupDirectory, target.AllowedRoot))
            {
                throw new IOException("Unsafe backup directory.");
            }

            Directory.CreateDirectory(backupDirectory);

            if (!HasNoReparsePointsInPath(backupDirectory))
            {
                throw new IOException("Unsafe backup directory.");
            }

            if (OperatingSystem.IsWindows())
            {
                var attributes = File.GetAttributes(backupDirectory);
                File.SetAttributes(backupDirectory, attributes | FileAttributes.Hidden);
            }

            return backupDirectory;
        }

        private static void PruneBackups(string backupDirectory)
        {
            try
            {
                var oldBackups = Directory
                    .EnumerateFiles(backupDirectory, "appsettings-*.json", SearchOption.TopDirectoryOnly)
                    .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
                    .Skip(BackupsToKeep)
                    .ToList();

                foreach (var backup in oldBackups)
                {
                    try
                    {
                        if (!IsReparsePoint(backup))
                        {
                            File.Delete(backup);
                        }
                    }
                    catch
                    {
                        // Retention is best effort and must not roll back a valid save.
                    }
                }
            }
            catch
            {
                // Retention is best effort and must not roll back a valid save.
            }
        }

        private static bool IsTargetStillSafe(SettingsTarget target)
        {
            try
            {
                if (!File.Exists(target.ExecutablePath) ||
                    !Directory.Exists(target.ExecutableDirectory) ||
                    !Directory.Exists(target.AllowedRoot) ||
                    !IsWithinOrEqual(target.ExecutablePath, target.AllowedRoot) ||
                    !IsWithinOrEqual(target.SettingsPath, target.ExecutableDirectory) ||
                    !IsWithinOrEqual(target.SettingsPath, target.AllowedRoot) ||
                    !HasNoReparsePointsInPath(target.ExecutableDirectory) ||
                    IsReparsePoint(target.ExecutablePath))
                {
                    return false;
                }

                if (Directory.Exists(target.SettingsPath))
                {
                    return false;
                }

                return !File.Exists(target.SettingsPath) || !IsReparsePoint(target.SettingsPath);
            }
            catch
            {
                return false;
            }
        }

        private static bool HasNoReparsePointsInPath(string directoryPath)
        {
            var fullPath = Path.GetFullPath(directoryPath);
            var pathRoot = Path.GetPathRoot(fullPath);

            if (string.IsNullOrEmpty(pathRoot) || !Directory.Exists(pathRoot))
            {
                return false;
            }

            var current = pathRoot;

            if (IsReparsePoint(current))
            {
                return false;
            }

            var relative = Path.GetRelativePath(pathRoot, fullPath);

            if (relative == ".")
            {
                return true;
            }

            var segments = relative.Split(
                new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries);

            foreach (var segment in segments)
            {
                current = Path.Combine(current, segment);

                if (!Directory.Exists(current) || IsReparsePoint(current))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsReparsePoint(string path)
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }

        private static bool IsWithinOrEqual(string path, string root)
        {
            try
            {
                var canonicalPath = Path.GetFullPath(path);
                var canonicalRoot = Path.GetFullPath(root);
                var relative = Path.GetRelativePath(canonicalRoot, canonicalPath);

                if (string.Equals(relative, ".", StringComparison.Ordinal))
                {
                    return true;
                }

                if (Path.IsPathRooted(relative) ||
                    string.Equals(relative, "..", FileSystemPathComparison))
                {
                    return false;
                }

                return !relative.StartsWith(
                           ".." + Path.DirectorySeparatorChar,
                           FileSystemPathComparison) &&
                       !relative.StartsWith(
                           ".." + Path.AltDirectorySeparatorChar,
                           FileSystemPathComparison);
            }
            catch
            {
                return false;
            }
        }

        private static string DecodeUtf8(byte[] bytes)
        {
            var offset = bytes.Length >= 3 &&
                         bytes[0] == 0xEF &&
                         bytes[1] == 0xBB &&
                         bytes[2] == 0xBF
                ? 3
                : 0;

            return StrictUtf8.GetString(bytes, offset, bytes.Length - offset);
        }

        private static string ComputeVersion(byte[] content)
        {
            return Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        }

        private static DateTimeOffset GetLastModifiedUtc(string path)
        {
            return new DateTimeOffset(File.GetLastWriteTimeUtc(path));
        }

        private static ServiceAppSettingsSnapshot SnapshotFailure(bool notFound, string error)
        {
            return new ServiceAppSettingsSnapshot
            {
                NotFound = notFound,
                Error = error,
                FileName = SettingsFileName
            };
        }

        private static ServiceAppSettingsSaveResult SaveFailure(bool notFound, bool conflict, string error)
        {
            return new ServiceAppSettingsSaveResult
            {
                NotFound = notFound,
                Conflict = conflict,
                Error = error
            };
        }

        private sealed record SettingsTarget(
            string ExecutablePath,
            string ExecutableDirectory,
            string SettingsPath,
            string AllowedRoot);

        private sealed record TargetResolution(
            bool Success,
            bool NotFound,
            SettingsTarget? Target,
            string Error)
        {
            public static TargetResolution SuccessResult(SettingsTarget target) =>
                new(true, false, target, "");

            public static TargetResolution NotFoundResult() =>
                new(false, true, null, "Service introuvable.");

            public static TargetResolution UnsafeResult() =>
                new(false, false, null, "La configuration de ce service n'est pas accessible.");
        }

        private sealed record CurrentFileState(
            bool Exists,
            byte[] Bytes,
            string Version,
            DateTimeOffset? LastModifiedUtc)
        {
            public static CurrentFileState Missing() =>
                new(false, Array.Empty<byte>(), MissingVersion, null);
        }

        private sealed record JsonValidationResult(bool IsValid, string Error)
        {
            public static JsonValidationResult Valid() => new(true, "");
            public static JsonValidationResult Invalid(string error) => new(false, error);
        }

        private sealed class SettingsFileTooLargeException : Exception
        {
        }
    }
}
