using System.Security.Cryptography;
using System.Text.Json;
using WorkerServiceControlWeb.Models;

namespace WorkerServiceControlWeb.Services
{
    public class UserStore
    {
        private const int Iterations = 100_000;
        private const int HashSize = 32;

        private readonly string _filePath;
        private readonly object _lock = new();
        private List<AppUser> _users = new();

        public UserStore(IHostEnvironment env)
        {
            _filePath = Path.Combine(env.ContentRootPath, "App_Data", "users.json");
            Load();
        }

        private void Load()
        {
            if (File.Exists(_filePath))
            {
                try
                {
                    var json = File.ReadAllText(_filePath);
                    _users = JsonSerializer.Deserialize<List<AppUser>>(json) ?? new List<AppUser>();

                    if (_users.Count > 0)
                    {
                        // Migrate files written before the Role field existed: make sure
                        // there is always at least one Admin account.
                        if (!_users.Any(u => u.Role == "Admin"))
                        {
                            var promoted = _users.FirstOrDefault(u => string.Equals(u.Username, "admin", StringComparison.OrdinalIgnoreCase))
                                ?? _users[0];
                            promoted.Role = "Admin";
                            Persist();
                        }

                        return;
                    }
                }
                catch
                {
                    // Corrupt or unreadable file — fall through and reseed the default account.
                }
            }

            _users = new List<AppUser>
            {
                new AppUser
                {
                    Username = "admin",
                    DisplayName = "Administrateur",
                    JobTitle = "Responsable IT",
                    Role = "Admin",
                    MemberSince = DateTime.UtcNow,
                    PasswordHash = HashPassword("Admin123!")
                }
            };

            Persist();
        }

        private void Persist()
        {
            lock (_lock)
            {
                var dir = Path.GetDirectoryName(_filePath)!;
                Directory.CreateDirectory(dir);

                var json = JsonSerializer.Serialize(_users, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_filePath, json);
            }
        }

        public List<AppUser> GetAllUsers()
        {
            lock (_lock)
            {
                return _users.OrderBy(u => u.Username, StringComparer.OrdinalIgnoreCase).ToList();
            }
        }

        public bool TryCreateUser(string username, string password, string displayName, string jobTitle, out string error)
        {
            error = "";
            username = username.Trim();

            if (string.IsNullOrWhiteSpace(username))
            {
                error = "Le nom d'utilisateur est obligatoire.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(password) || password.Length < 6)
            {
                error = "Le mot de passe doit contenir au moins 6 caractères.";
                return false;
            }

            lock (_lock)
            {
                if (_users.Any(u => string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase)))
                {
                    error = "Ce nom d'utilisateur existe déjà.";
                    return false;
                }

                _users.Add(new AppUser
                {
                    Username = username,
                    DisplayName = string.IsNullOrWhiteSpace(displayName) ? username : displayName.Trim(),
                    JobTitle = jobTitle?.Trim() ?? "",
                    MemberSince = DateTime.UtcNow,
                    PasswordHash = HashPassword(password)
                });

                Persist();
            }

            return true;
        }

        public AppUser? FindByUsername(string username)
        {
            lock (_lock)
            {
                return _users.FirstOrDefault(u => string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase));
            }
        }

        public bool TryUpdateUser(string username, string displayName, string jobTitle, string role, string? newPassword, out string error)
        {
            error = "";

            if (role != "Admin" && role != "User")
            {
                error = "Rôle invalide.";
                return false;
            }

            lock (_lock)
            {
                var user = _users.FirstOrDefault(u => string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase));

                if (user == null)
                {
                    error = "Utilisateur introuvable.";
                    return false;
                }

                if (user.Role == "Admin" && role != "Admin" && _users.Count(u => u.Role == "Admin") <= 1)
                {
                    error = "Impossible : il doit rester au moins un administrateur.";
                    return false;
                }

                if (!string.IsNullOrEmpty(newPassword))
                {
                    if (newPassword.Length < 6)
                    {
                        error = "Le nouveau mot de passe doit contenir au moins 6 caractères.";
                        return false;
                    }

                    user.PasswordHash = HashPassword(newPassword);
                }

                user.DisplayName = string.IsNullOrWhiteSpace(displayName) ? user.Username : displayName.Trim();
                user.JobTitle = jobTitle?.Trim() ?? "";
                user.Role = role;

                Persist();
            }

            return true;
        }

        public bool TryDeleteUser(string username, out string error)
        {
            error = "";

            lock (_lock)
            {
                var user = _users.FirstOrDefault(u => string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase));

                if (user == null)
                {
                    error = "Utilisateur introuvable.";
                    return false;
                }

                if (user.Role == "Admin" && _users.Count(u => u.Role == "Admin") <= 1)
                {
                    error = "Impossible de supprimer le dernier administrateur.";
                    return false;
                }

                _users.Remove(user);
                Persist();
            }

            return true;
        }

        public AppUser? Validate(string username, string password)
        {
            var user = _users.FirstOrDefault(u => string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase));

            if (user == null)
            {
                return null;
            }

            return VerifyPassword(password, user.PasswordHash) ? user : null;
        }

        private static string HashPassword(string password)
        {
            var salt = RandomNumberGenerator.GetBytes(16);
            var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashSize);
            return Convert.ToBase64String(salt) + "." + Convert.ToBase64String(hash);
        }

        private static bool VerifyPassword(string password, string stored)
        {
            var parts = stored.Split('.');

            if (parts.Length != 2)
            {
                return false;
            }

            var salt = Convert.FromBase64String(parts[0]);
            var expectedHash = Convert.FromBase64String(parts[1]);
            var actualHash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashSize);

            return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
        }
    }
}
