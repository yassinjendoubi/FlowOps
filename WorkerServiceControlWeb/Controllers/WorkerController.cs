using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WorkerServiceControlWeb.Models;
using WorkerServiceControlWeb.Services;

namespace WorkerServiceControlWeb.Controllers
{
    public class WorkerController : Controller
    {
        private readonly WorkerProcessManager _workerProcessManager;
        private readonly ServiceAppSettingsStore _appSettingsStore;
        private readonly ServiceDropUploadStore _dropUploadStore;

        public WorkerController(
            WorkerProcessManager workerProcessManager,
            ServiceAppSettingsStore appSettingsStore,
            ServiceDropUploadStore dropUploadStore)
        {
            _workerProcessManager = workerProcessManager;
            _appSettingsStore = appSettingsStore;
            _dropUploadStore = dropUploadStore;
        }

        private string CurrentUploadOwner => User.Identity?.Name ?? "anonymous";

        public IActionResult Index()
        {
            return View(_workerProcessManager.GetAllStatuses());
        }

        [HttpGet]
        public IActionResult Service(string key)
        {
            var status = _workerProcessManager.GetStatus(key);

            if (status == null)
            {
                return NotFound();
            }

            return View(status);
        }

        [HttpGet]
        public IActionResult StatusAll()
        {
            return Json(_workerProcessManager.GetAllStatuses());
        }

        [HttpGet]
        public IActionResult Status(string key)
        {
            var status = _workerProcessManager.GetStatus(key);

            if (status == null)
            {
                return NotFound();
            }

            return Json(status);
        }

        [HttpPost]
        public IActionResult Start(string service)
        {
            var success = _workerProcessManager.StartService(service, out var error, User.Identity?.Name);
            return Json(new { success, error });
        }

        [HttpPost]
        public IActionResult Stop(string service)
        {
            var success = _workerProcessManager.StopService(service, out var error, User.Identity?.Name);
            return Json(new { success, error });
        }

        [HttpGet]
        [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
        public IActionResult History(string? key, int take = 50)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return NotFound(new { success = false, error = "Service introuvable." });
            }

            var registered = _workerProcessManager.FindRegisteredService(key);

            if (registered == null)
            {
                return NotFound(new { success = false, error = "Service introuvable." });
            }

            take = Math.Clamp(take, 1, 200);
            // GetStatus reconciles a process that completed between polls.
            // Do it before reading the timeline so the response cannot expose
            // a new history version while omitting that newly appended event.
            var status = _workerProcessManager.GetStatus(key);
            var events = _workerProcessManager.GetHistory(key, take);

            return Json(new
            {
                success = true,
                serviceKey = registered.Key,
                displayName = registered.DisplayName,
                version = status?.HistoryVersion ?? events.Count,
                events
            });
        }

        [Authorize(Roles = "Admin")]
        [HttpGet]
        [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
        public async Task<IActionResult> AppSettings(string? key, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return NotFound(new { success = false, error = "Service introuvable." });
            }

            var registered = _workerProcessManager.FindRegisteredService(key);

            if (registered == null)
            {
                return NotFound(new { success = false, error = "Service introuvable." });
            }

            var snapshot = await _appSettingsStore.ReadAsync(key, cancellationToken);

            if (snapshot.NotFound)
            {
                return NotFound(new { success = false, error = snapshot.Error });
            }

            if (!snapshot.Success)
            {
                return Conflict(new { success = false, error = snapshot.Error });
            }

            return Json(new
            {
                success = true,
                serviceKey = registered.Key,
                displayName = registered.DisplayName,
                snapshot.Exists,
                snapshot.Content,
                snapshot.Version,
                snapshot.LastModifiedUtc,
                snapshot.IsValid,
                snapshot.ValidationError,
                snapshot.FileName
            });
        }

        public sealed class SaveAppSettingsRequest
        {
            public string Key { get; set; } = "";
            public string Content { get; set; } = "";
            public string ExpectedVersion { get; set; } = "";
        }

        [Authorize(Roles = "Admin")]
        [HttpPost]
        [ValidateAntiForgeryToken]
        [RequestSizeLimit(600 * 1024)]
        public async Task<IActionResult> SaveAppSettings(
            [FromBody] SaveAppSettingsRequest? request,
            CancellationToken cancellationToken)
        {
            if (request == null)
            {
                return BadRequest(new { success = false, error = "Requête de configuration invalide." });
            }

            var result = await _appSettingsStore.SaveAsync(
                request.Key,
                request.Content,
                request.ExpectedVersion,
                cancellationToken);

            if (result.NotFound)
            {
                return NotFound(new { success = false, error = result.Error });
            }

            if (result.Conflict)
            {
                return Conflict(new
                {
                    success = false,
                    conflict = true,
                    error = result.Error,
                    result.Version,
                    result.LastModifiedUtc
                });
            }

            if (!result.Success)
            {
                return BadRequest(new { success = false, error = result.Error });
            }

            return Json(new
            {
                success = true,
                result.Version,
                result.LastModifiedUtc,
                message = "Configuration enregistrée. Elle sera appliquée au prochain démarrage du service."
            });
        }

        [HttpGet]
        public IActionResult Browse(string? path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    var drives = DriveInfo.GetDrives()
                        .Where(d => d.IsReady)
                        .Select(d => new { name = d.Name, path = d.RootDirectory.FullName });

                    return Json(new
                    {
                        currentPath = "",
                        parentPath = (string?)null,
                        directories = drives,
                        executables = Array.Empty<object>()
                    });
                }

                if (!Directory.Exists(path))
                {
                    return BadRequest("Dossier introuvable.");
                }

                var dirInfo = new DirectoryInfo(path);

                var directories = dirInfo.GetDirectories()
                    .OrderBy(d => d.Name)
                    .Select(d => new { name = d.Name, path = d.FullName });

                var executables = dirInfo.GetFiles("*.exe")
                    .OrderBy(f => f.Name)
                    .Select(f => new { name = f.Name, path = f.FullName });

                return Json(new
                {
                    currentPath = dirInfo.FullName,
                    parentPath = dirInfo.Parent?.FullName,
                    directories,
                    executables
                });
            }
            catch (Exception ex)
            {
                return BadRequest(ex.Message);
            }
        }

        public class DetectServicesRequest
        {
            public string ExePath { get; set; } = "";
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult DetectServices([FromBody] DetectServicesRequest? request)
        {
            if (request == null)
            {
                return BadRequest(new { success = false, error = "Requête de détection invalide." });
            }

            var detected = _workerProcessManager.DetectServices(request.ExePath, out var error, out var method);
            return Json(new { success = detected.Count > 0, services = detected, error, method });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [RequestSizeLimit(800L * 1024 * 1024)]
        [RequestFormLimits(
            MultipartBodyLengthLimit = 800L * 1024 * 1024,
            ValueCountLimit = ServiceDropUploadStore.MaximumFileCount + 100)]
        public async Task<IActionResult> UploadServiceFolder(
            [FromForm] List<IFormFile> files,
            [FromForm] List<string> relativePaths,
            CancellationToken cancellationToken)
        {
            try
            {
                var staged = await _dropUploadStore.StageAsync(
                    files,
                    relativePaths,
                    CurrentUploadOwner,
                    cancellationToken);

                return Json(new
                {
                    success = true,
                    uploadToken = staged.Token,
                    staged.FileCount,
                    staged.TotalBytes,
                    executables = staged.Executables
                });
            }
            catch (OperationCanceledException)
            {
                return BadRequest(new { success = false, error = "Le dépôt du projet a été annulé." });
            }
            catch (Exception ex)
            {
                return BadRequest(new { success = false, error = ex.Message });
            }
        }

        public sealed class UploadedExecutableRequest
        {
            public string UploadToken { get; set; } = "";
            public string ExecutableRelativePath { get; set; } = "";
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult DetectUploadedServices([FromBody] UploadedExecutableRequest? request)
        {
            if (request == null)
            {
                return BadRequest(new { success = false, error = "Requête de détection invalide." });
            }

            if (!_dropUploadStore.TryResolveExecutable(
                    request.UploadToken,
                    request.ExecutableRelativePath,
                    CurrentUploadOwner,
                    out var executablePath,
                    out var resolveError))
            {
                return BadRequest(new { success = false, error = resolveError });
            }

            var detected = _workerProcessManager.DetectServices(executablePath, out var error, out var method);
            return Json(new { success = detected.Count > 0, services = detected, error, method });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult DiscardServiceUpload([FromBody] UploadedExecutableRequest? request)
        {
            if (request != null)
            {
                _dropUploadStore.Discard(request.UploadToken, CurrentUploadOwner);
            }

            return Json(new { success = true });
        }

        public class AddServiceItem
        {
            public string Key { get; set; } = "";
            public string DisplayName { get; set; } = "";
        }

        public class AddServicesRequest
        {
            public string ExePath { get; set; } = "";
            public string UploadToken { get; set; } = "";
            public string UploadedExeRelativePath { get; set; } = "";
            public List<AddServiceItem> Services { get; set; } = new();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult AddServices([FromBody] AddServicesRequest? request)
        {
            if (request == null || request.Services.Count == 0)
            {
                return BadRequest(new { success = false, errors = new[] { "Ajoutez au moins un service." } });
            }

            var executablePath = request.ExePath;

            if (!string.IsNullOrWhiteSpace(request.UploadToken))
            {
                if (!_dropUploadStore.TryResolveExecutable(
                        request.UploadToken,
                        request.UploadedExeRelativePath,
                        CurrentUploadOwner,
                        out executablePath,
                        out var resolveError))
                {
                    return BadRequest(new { success = false, errors = new[] { resolveError } });
                }
            }

            var errors = new List<string>();

            foreach (var item in request.Services)
            {
                if (!_workerProcessManager.AddService(executablePath, item.Key, item.DisplayName, out var error))
                {
                    errors.Add($"{item.Key} : {error}");
                }
            }

            if (errors.Count == 0 && !string.IsNullOrWhiteSpace(request.UploadToken))
            {
                _dropUploadStore.Discard(request.UploadToken, CurrentUploadOwner);
            }

            return Json(new { success = errors.Count == 0, errors });
        }

        [Authorize(Roles = "Admin")]
        [HttpPost]
        public IActionResult DeleteService(string key)
        {
            var success = _workerProcessManager.RemoveService(key, out var error);
            return Json(new { success, error });
        }

        [HttpPost]
        public IActionResult ClearLogs(string key)
        {
            var success = _workerProcessManager.ClearLogs(key, out var error);
            return Json(new { success, error });
        }

        [HttpPost]
        public IActionResult ClearHistory(string key)
        {
            var success = _workerProcessManager.ClearHistory(key, out var error);
            return Json(new { success, error });
        }

        public IActionResult Files()
        {
            var keys = _workerProcessManager.GetServiceFolderKeys();
            return View(keys);
        }

        [HttpGet]
        public IActionResult ServiceFiles(string key, string? subpath)
        {
            var entries = _workerProcessManager.GetServiceFiles(key, subpath);
            return Json(entries);
        }

        [HttpGet]
        public IActionResult ServiceFileContent(string key, string path)
        {
            var content = _workerProcessManager.GetServiceFileContent(key, path);
            if (content == null) return NotFound();
            return Content(content, "text/plain; charset=utf-8");
        }
    }
}
