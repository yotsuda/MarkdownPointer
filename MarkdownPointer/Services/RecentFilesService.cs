using System.IO;
using System.Text.Json;

namespace MarkdownPointer.Services
{
    public class RecentFilesService
    {
        private const int MaxFiles = 10;
        private const int MaxFolders = 5;
        private readonly string _filePath;
        private List<string> _recentFiles = new();

        public RecentFilesService()
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MarkdownPointer");
            Directory.CreateDirectory(dir);
            _filePath = Path.Combine(dir, "recent.json");
            Load();
        }

        public void AddFile(string path)
        {
            var fullPath = Path.GetFullPath(path);
            _recentFiles.Remove(fullPath);
            _recentFiles.Insert(0, fullPath);
            if (_recentFiles.Count > MaxFiles)
                _recentFiles.RemoveRange(MaxFiles, _recentFiles.Count - MaxFiles);
            Save();
        }

        public IReadOnlyList<string> GetRecentFiles()
        {
            return _recentFiles;
        }

        public IReadOnlyList<string> GetRecentFolders()
        {
            return _recentFiles
                .Select(Path.GetDirectoryName)
                .Where(d => d != null)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(MaxFolders)
                .ToList()!;
        }

        private void Load()
        {
            try
            {
                if (File.Exists(_filePath))
                {
                    var json = File.ReadAllText(_filePath);
                    _recentFiles = JsonSerializer.Deserialize<List<string>>(json) ?? new();
                }
            }
            catch
            {
                _recentFiles = new();
            }
        }

        private void Save()
        {
            try
            {
                var json = JsonSerializer.Serialize(_recentFiles, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_filePath, json);
            }
            catch
            {
                // Ignore save errors
            }
        }
    }
}
