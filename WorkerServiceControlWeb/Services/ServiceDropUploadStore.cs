using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace WorkerServiceControlWeb.Services;

/// <summary>
/// Stores a dropped project for a short time while the user chooses an executable
/// and confirms which services should be registered.
/// </summary>
public sealed class ServiceDropUploadStore
{
    public const int MaximumFileCount = 5_000;
    public const long MaximumFileSize = 256L * 1024 * 1024;
    public const long MaximumTotalSize = 750L * 1024 * 1024;

    private static readonly TimeSpan UploadLifetime = TimeSpan.FromHours(2);
    private static readonly Regex TokenPattern = new("^[a-f0-9]{32}$", RegexOptions.Compiled);
    private static readonly HashSet<string> IgnoredFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", "node_modules", "packages", "obj", "TestResults"
    };
    private static readonly HashSet<string> IgnoredExecutables = new(StringComparer.OrdinalIgnoreCase)
    {
        "apphost.exe", "createdump.exe", "dotnet.exe", "iisexpress.exe",
        "testhost.exe", "vstest.console.exe"
    };

    private readonly string _uploadRoot;
    private readonly ConcurrentDictionary<string, UploadSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _cleanupLock = new(1, 1);

    private sealed record UploadSession(string Owner, string RootPath, DateTimeOffset CreatedAtUtc);

    public sealed record ExecutableCandidate(
        string RelativePath,
        string FileName,
        string Folder,
        bool Recommended);

    public sealed record StageResult(
        string Token,
        int FileCount,
        long TotalBytes,
        IReadOnlyList<ExecutableCandidate> Executables);

    public ServiceDropUploadStore(IHostEnvironment environment)
    {
        _uploadRoot = Path.GetFullPath(Path.Combine(
            environment.ContentRootPath,
            "App_Data",
            "uploads",
            "service-drop"));
        Directory.CreateDirectory(_uploadRoot);
        CleanupExpiredUploads();
    }

    public async Task<StageResult> StageAsync(
        IReadOnlyList<IFormFile> files,
        IReadOnlyList<string> relativePaths,
        string owner,
        CancellationToken cancellationToken)
    {
        await CleanupExpiredUploadsAsync();

        if (files.Count == 0 || files.Count != relativePaths.Count)
        {
            throw new InvalidOperationException("Le dossier déposé ne contient aucun fichier exploitable.");
        }

        if (files.Count > MaximumFileCount)
        {
            throw new InvalidOperationException($"Le projet dépasse la limite de {MaximumFileCount:N0} fichiers.");
        }

        long totalBytes = 0;
        var normalizedPaths = new List<string>(files.Count);
        var uniquePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < files.Count; index++)
        {
            var file = files[index];

            if (file.Length > MaximumFileSize)
            {
                throw new InvalidOperationException($"Le fichier « {file.FileName} » dépasse la taille autorisée.");
            }

            totalBytes = checked(totalBytes + file.Length);

            if (totalBytes > MaximumTotalSize)
            {
                throw new InvalidOperationException("Le projet dépasse la taille maximale autorisée de 750 Mo.");
            }

            var normalized = NormalizeRelativePath(relativePaths[index]);

            if (!uniquePaths.Add(normalized))
            {
                throw new InvalidOperationException($"Le chemin « {normalized} » apparaît plusieurs fois.");
            }

            normalizedPaths.Add(normalized);
        }

        var token = Guid.NewGuid().ToString("N");
        var root = GetSessionRoot(token);
        Directory.CreateDirectory(root);

        try
        {
            for (var index = 0; index < files.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var destination = ResolveContainedPath(root, normalizedPaths[index]);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

                await using var output = new FileStream(
                    destination,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    64 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                await files[index].CopyToAsync(output, cancellationToken);
            }

            var ranked = FindExecutables(root);

            if (ranked.Count == 0)
            {
                throw new InvalidOperationException(
                    "Aucun fichier .exe n’a été trouvé. Compilez ou publiez le projet, puis déposez le dossier qui contient sa sortie.");
            }

            _sessions[token] = new UploadSession(owner, root, DateTimeOffset.UtcNow);

            return new StageResult(token, files.Count, totalBytes, ranked);
        }
        catch
        {
            TryDeleteDirectory(root);
            throw;
        }
    }

    public bool TryResolveExecutable(
        string token,
        string relativePath,
        string owner,
        out string executablePath,
        out string error)
    {
        executablePath = "";
        error = "Dépôt introuvable ou expiré. Déposez à nouveau le dossier du projet.";

        if (!TryGetSession(token, owner, out var session))
        {
            return false;
        }

        try
        {
            var normalized = NormalizeRelativePath(relativePath);

            if (!normalized.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                error = "Le fichier sélectionné n’est pas un exécutable .exe.";
                return false;
            }

            var resolved = ResolveContainedPath(session.RootPath, normalized);

            if (!File.Exists(resolved))
            {
                error = "L’exécutable sélectionné est introuvable dans le dossier déposé.";
                return false;
            }

            executablePath = resolved;
            error = "";
            return true;
        }
        catch
        {
            error = "Chemin d’exécutable invalide.";
            return false;
        }
    }

    public void Discard(string token, string owner)
    {
        if (!TryGetSession(token, owner, out var session))
        {
            return;
        }

        _sessions.TryRemove(token, out _);
        TryDeleteDirectory(session.RootPath);
    }

    private bool TryGetSession(string token, string owner, out UploadSession session)
    {
        session = null!;

        if (string.IsNullOrWhiteSpace(token) || !TokenPattern.IsMatch(token))
        {
            return false;
        }

        if (!_sessions.TryGetValue(token, out var found) ||
            !string.Equals(found.Owner, owner, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (DateTimeOffset.UtcNow - found.CreatedAtUtc > UploadLifetime || !Directory.Exists(found.RootPath))
        {
            _sessions.TryRemove(token, out _);
            TryDeleteDirectory(found.RootPath);
            return false;
        }

        session = found;
        return true;
    }

    private List<ExecutableCandidate> FindExecutables(string root)
    {
        var candidates = Directory.EnumerateFiles(root, "*.exe", SearchOption.AllDirectories)
            .Where(path => !HasIgnoredSegment(root, path))
            .Where(path => !IgnoredExecutables.Contains(Path.GetFileName(path)))
            .Where(path => !Path.GetFileName(path).StartsWith("testhost", StringComparison.OrdinalIgnoreCase))
            .Select(path => new
            {
                FullPath = path,
                RelativePath = Path.GetRelativePath(root, path),
                Score = ScoreExecutable(path)
            })
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.RelativePath.Length)
            .ThenBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return candidates.Select((item, index) => new ExecutableCandidate(
                item.RelativePath.Replace(Path.DirectorySeparatorChar, '/'),
                Path.GetFileName(item.FullPath),
                FormatFolder(Path.GetDirectoryName(item.RelativePath)),
                index == 0))
            .ToList();
    }

    private static int ScoreExecutable(string path)
    {
        var score = 0;
        var directory = Path.GetDirectoryName(path)!;
        var baseName = Path.GetFileNameWithoutExtension(path);

        if (File.Exists(Path.Combine(directory, baseName + ".runtimeconfig.json"))) score += 120;
        if (File.Exists(Path.Combine(directory, baseName + ".deps.json"))) score += 80;

        var segments = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (segments.Any(s => s.Equals("publish", StringComparison.OrdinalIgnoreCase))) score += 45;
        if (segments.Any(s => s.Equals("Release", StringComparison.OrdinalIgnoreCase))) score += 25;
        if (segments.Any(s => s.Equals("bin", StringComparison.OrdinalIgnoreCase))) score += 20;
        if (segments.Any(s => s.Equals("Debug", StringComparison.OrdinalIgnoreCase))) score += 10;

        return score;
    }

    private static bool HasIgnoredSegment(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(IgnoredFolders.Contains);
    }

    private static string FormatFolder(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || folder == ".")
        {
            return "Racine du dossier";
        }

        return folder.Replace(Path.DirectorySeparatorChar, '/');
    }

    private static string NormalizeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.IndexOf('\0') >= 0)
        {
            throw new InvalidOperationException("Un fichier possède un chemin invalide.");
        }

        path = path.Replace('\\', '/').TrimStart('/');

        if (Path.IsPathRooted(path) || path.Contains(':'))
        {
            throw new InvalidOperationException("Un fichier possède un chemin absolu non autorisé.");
        }

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length == 0 || segments.Any(segment =>
                segment is "." or ".." ||
                segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
        {
            throw new InvalidOperationException("Un fichier possède un chemin invalide.");
        }

        return Path.Combine(segments);
    }

    private string GetSessionRoot(string token)
    {
        if (!TokenPattern.IsMatch(token))
        {
            throw new InvalidOperationException("Jeton de dépôt invalide.");
        }

        return ResolveContainedPath(_uploadRoot, token);
    }

    private static string ResolveContainedPath(string root, string relativePath)
    {
        var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var resolved = Path.GetFullPath(Path.Combine(canonicalRoot, relativePath));
        var relation = Path.GetRelativePath(canonicalRoot, resolved);

        if (Path.IsPathRooted(relation) || relation == ".." ||
            relation.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            relation.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Le chemin sort du dossier temporaire autorisé.");
        }

        return resolved;
    }

    private async Task CleanupExpiredUploadsAsync()
    {
        if (!await _cleanupLock.WaitAsync(0))
        {
            return;
        }

        try
        {
            CleanupExpiredUploads();
        }
        finally
        {
            _cleanupLock.Release();
        }
    }

    private void CleanupExpiredUploads()
    {
        if (!Directory.Exists(_uploadRoot))
        {
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(_uploadRoot))
        {
            var token = Path.GetFileName(directory);

            if (!TokenPattern.IsMatch(token))
            {
                continue;
            }

            DateTimeOffset createdAt;

            if (_sessions.TryGetValue(token, out var session))
            {
                createdAt = session.CreatedAtUtc;
            }
            else
            {
                createdAt = new DirectoryInfo(directory).CreationTimeUtc;
            }

            if (DateTimeOffset.UtcNow - createdAt <= UploadLifetime)
            {
                continue;
            }

            _sessions.TryRemove(token, out _);
            TryDeleteDirectory(directory);
        }
    }

    private void TryDeleteDirectory(string path)
    {
        try
        {
            var canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            var parent = Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(canonical) ?? "");

            if (!string.Equals(parent, Path.TrimEndingDirectorySeparator(_uploadRoot), StringComparison.OrdinalIgnoreCase) ||
                !TokenPattern.IsMatch(Path.GetFileName(canonical)))
            {
                return;
            }

            if (Directory.Exists(canonical))
            {
                Directory.Delete(canonical, recursive: true);
            }
        }
        catch
        {
            // Temporary uploads are also removed by the next cleanup pass.
        }
    }
}
