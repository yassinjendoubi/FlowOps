using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WorkerServiceControlWeb.Models;
using WorkerServiceControlWeb.Services;

namespace WorkerServiceControlWeb.Controllers
{
    [AllowAnonymous]
    public class AccountController : Controller
    {
        private readonly UserStore _userStore;

        public AccountController(UserStore userStore)
        {
            _userStore = userStore;
        }

        [HttpGet]
        public IActionResult Login(string? returnUrl = null)
        {
            if (User.Identity?.IsAuthenticated == true)
            {
                return RedirectToAction("Index", "Worker");
            }

            ViewBag.ReturnUrl = returnUrl;
            return View();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Login(string username, string password, string? returnUrl = null)
        {
            var user = _userStore.Validate(username ?? "", password ?? "");

            if (user == null)
            {
                ViewBag.ReturnUrl = returnUrl;
                ViewBag.Error = "Nom d'utilisateur ou mot de passe incorrect.";
                return View();
            }

            await SignInUserAsync(user);

            if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
            {
                return Redirect(returnUrl);
            }

            return RedirectToAction("Index", "Worker");
        }

        [HttpGet]
        public IActionResult Register()
        {
            if (User.Identity?.IsAuthenticated == true)
            {
                return RedirectToAction("Index", "Worker");
            }

            return View();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Register(string username, string password, string confirmPassword, string displayName, string jobTitle)
        {
            if (password != confirmPassword)
            {
                ViewBag.Error = "Les mots de passe ne correspondent pas.";
                return View();
            }

            if (!_userStore.TryCreateUser(username ?? "", password ?? "", displayName ?? "", jobTitle ?? "", out var error))
            {
                ViewBag.Error = error;
                return View();
            }

            var user = _userStore.Validate(username!, password!)!;

            await SignInUserAsync(user);

            return RedirectToAction("Index", "Worker");
        }

        private async Task SignInUserAsync(AppUser user)
        {
            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.Name, user.Username),
                new Claim(ClaimTypes.Role, user.Role),
                new Claim("DisplayName", user.DisplayName),
                new Claim("JobTitle", user.JobTitle),
                new Claim("MemberSince", user.MemberSince.ToString("O"))
            };

            var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
        }

        [Authorize]
        [HttpGet]
        public IActionResult Profile()
        {
            return View();
        }

        [Authorize]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Logout()
        {
            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return RedirectToAction("Login");
        }
    }
}
