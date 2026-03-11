using System.IO;
using System.Text.Json;

namespace MarkdownPointer.Services
{
    public class RecentFileEntry
    {
        public string Path { get; set; } = "";
        public bool Pinned { get; set; }
    }

    public class RecentData
    {
        public List<RecentFileEntry> Files { get; set; } = new();
        public List<string> PinnedFolders { get; set; } = new();
    }

    public class RecentFilesService
    {
        private const int MaxFiles = 10;
        private const int MaxFolders = 5;
        private readonly string _filePath;
        private RecentData _data = new();

        public RecentFilesService()
        {
            var dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MarkdownPointer");
            Directory.CreateDirectory(dir);
            _filePath = System.IO.Path.Combine(dir, "recent.json");
            Load();
        }

        public void AddFile(string path)
        {
            var fullPath = System.IO.Path.GetFullPath(path);
            var existing = _data.Files.FirstOrDefault(
                e => string.Equals(e.Path, fullPath, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                // Move to top of its group (pinned stay pinned)
                _data.Files.Remove(existing);
                var insertIndex = existing.Pinned ? 0 : _data.Files.Count(e => e.Pinned);
                _data.Files.Insert(insertIndex, existing);
            }
            else
            {
                // Insert after pinned items
                var insertIndex = _data.Files.Count(e => e.Pinned);
                _data.Files.Insert(insertIndex, new RecentFileEntry { Path = fullPath });
            }

            // Trim only unpinned items beyond max
            while (_data.Files.Count > MaxFiles && _data.Files.Any(e => !e.Pinned))
            {
                var lastUnpinned = _data.Files.LastOrDefault(e => !e.Pinned);
                if (lastUnpinned != null) _data.Files.Remove(lastUnpinned);
                else break;
            }
            Save();
        }

        public IReadOnlyList<RecentFileEntry> GetRecentFiles()
        {
            return _data.Files.ToList();
        }

        /// <summary>
        /// Returns recent folders: pinned folders first, then derived from recent files.
        /// </summary>
        public IReadOnlyList<(string Path, bool Pinned)> GetRecentFolders()
        {
            var result = new List<(string Path, bool Pinned)>();

            // Pinned folders first
            foreach (var folder in _data.PinnedFolders)
            {
                result.Add((folder, true));
            }

            // Then derived folders from recent files (excluding already-listed)
            var existing = new HashSet<string>(
                _data.PinnedFolders, StringComparer.OrdinalIgnoreCase);

            var derived = _data.Files
                .Select(e => System.IO.Path.GetDirectoryName(e.Path))
                .Where(d => d != null && !existing.Contains(d))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(MaxFolders - result.Count);

            foreach (var folder in derived)
            {
                result.Add((folder!, false));
            }

            return result;
        }

        public void ToggleFilePin(string path)
        {
            var entry = _data.Files.FirstOrDefault(
                e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase));
            if (entry == null) return;

            entry.Pinned = !entry.Pinned;

            // Reorder: pinned first, then unpinned
            _data.Files.Remove(entry);
            if (entry.Pinned)
                _data.Files.Insert(0, entry);
            else
                _data.Files.Insert(_data.Files.Count(e => e.Pinned), entry);

            Save();
        }

        public void ToggleFolderPin(string folder)
        {
            var index = _data.PinnedFolders.FindIndex(
                f => string.Equals(f, folder, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                _data.PinnedFolders.RemoveAt(index);
            }
            else
            {
                _data.PinnedFolders.Add(folder);
            }
            Save();
        }

        private void Load()
        {
            try
            {
                if (!File.Exists(_filePath))
                {
                    _data = new RecentData();
                    return;
                }

                var json = File.ReadAllText(_filePath);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.ValueKind == JsonValueKind.Array)
                {
                    // Legacy format: plain string array
                    var paths = JsonSerializer.Deserialize<List<string>>(json) ?? new();
                    _data = new RecentData
                    {
                        Files = paths.Select(p => new RecentFileEntry { Path = p }).ToList()
                    };
                }
                else
                {
                    // New format
                    _data = JsonSerializer.Deserialize<RecentData>(json) ?? new RecentData();
                }
            }
            catch
            {
                _data = new RecentData();
            }
        }

        private void Save()
        {
            try
            {
                var json = JsonSerializer.Serialize(_data, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_filePath, json);
            }
            catch
            {
                // Ignore save errors
            }
        }
    }
}
