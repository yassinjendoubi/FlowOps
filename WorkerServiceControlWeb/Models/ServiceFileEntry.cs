namespace WorkerServiceControlWeb.Models
{
    public class ServiceFileEntry
    {
        public string Name { get; set; } = "";
        public string RelativePath { get; set; } = "";
        public bool IsDirectory { get; set; }
        public long Size { get; set; }
    }
}
