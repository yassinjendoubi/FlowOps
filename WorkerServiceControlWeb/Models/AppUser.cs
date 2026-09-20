namespace WorkerServiceControlWeb.Models
{
    public class AppUser
    {
        public string Username { get; set; } = "";
        public string PasswordHash { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string JobTitle { get; set; } = "";
        public string Role { get; set; } = "User";
        public DateTime MemberSince { get; set; } = DateTime.UtcNow;
    }
}
