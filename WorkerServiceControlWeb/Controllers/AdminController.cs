using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WorkerServiceControlWeb.Services;

namespace WorkerServiceControlWeb.Controllers
{
    [Authorize(Roles = "Admin")]
    public class AdminController : Controller
    {
        private readonly UserStore _userStore;

        public AdminController(UserStore userStore)
        {
            _userStore = userStore;
        }

        public IActionResult Dashboard()
        {
            ViewBag.Message = TempData["Message"] as string;
            ViewBag.Error = TempData["Error"] as string;
            return View(_userStore.GetAllUsers());
        }

        [HttpGet]
        public IActionResult EditUser(string username)
        {
            var user = _userStore.FindByUsername(username);

            if (user == null)
            {
                return NotFound();
            }

            return View(user);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult EditUser(string username, string displayName, string jobTitle, string role, string? newPassword)
        {
            if (!_userStore.TryUpdateUser(username, displayName, jobTitle, role, newPassword, out var error))
            {
                var user = _userStore.FindByUsername(username);
                ViewBag.Error = error;
                return View(user);
            }

            TempData["Message"] = $"Utilisateur '{username}' mis à jour.";
            return RedirectToAction("Dashboard");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult DeleteUser(string username)
        {
            if (!_userStore.TryDeleteUser(username, out var error))
            {
                TempData["Error"] = error;
            }
            else
            {
                TempData["Message"] = $"Utilisateur '{username}' supprimé.";
            }

            return RedirectToAction("Dashboard");
        }
    }
}
