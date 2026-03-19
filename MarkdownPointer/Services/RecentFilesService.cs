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
        private const int MaxFolders = 10;
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
            var dir = System.IO.Path.GetDirectoryName(fullPath);
            if (dir != null) _hiddenFolders.Remove(dir);
            var existing = _data.Files.FirstOrDefault(
                e => string.Equals(e.Path, fullPath, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                // Move to top (most recent)
                _data.Files.Remove(existing);
                _data.Files.Insert(0, existing);
            }
            else
            {
                _data.Files.Insert(0, new RecentFileEntry { Path = fullPath });
            }

            // Trim oldest unpinned items beyond max
            for (var i = _data.Files.Count - 1; i >= 0 && _data.Files.Count > MaxFiles; i--)
            {
                if (!_data.Files[i].Pinned)
                    _data.Files.RemoveAt(i);
            }
            Save();
        }

        public IReadOnlyList<RecentFileEntry> GetRecentFiles()
        {
            // Pinned first, then unpinned, preserving relative order within each group
            return _data.Files.Where(e => e.Pinned)
                .Concat(_data.Files.Where(e => !e.Pinned))
                .ToList();
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
                .Where(d => d != null && !existing.Contains(d) && !_hiddenFolders.Contains(d!))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(MaxFolders - result.Count);

            foreach (var folder in derived)
            {
                result.Add((folder!, false));
            }

            return result;
        }

        public void RemoveFile(string path)
        {
            var entry = _data.Files.FirstOrDefault(
                e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase));
            if (entry != null)
            {
                _data.Files.Remove(entry);
                Save();
            }
        }

        public void RemoveFolder(string folder)
        {
            // Remove pinned folder
            var index = _data.PinnedFolders.FindIndex(
                f => string.Equals(f, folder, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                _data.PinnedFolders.RemoveAt(index);
                Save();
                return;
            }

            // For derived folders, hide from folder list without removing files
            _hiddenFolders.Add(folder);
        }

        private readonly HashSet<string> _hiddenFolders = new(StringComparer.OrdinalIgnoreCase);

        public void ToggleFilePin(string path)
        {
            var entry = _data.Files.FirstOrDefault(
                e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase));
            if (entry == null) return;

            entry.Pinned = !entry.Pinned;
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

        private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

        private void Save()
        {
            try
            {
                var json = JsonSerializer.Serialize(_data, _jsonOptions);
                File.WriteAllText(_filePath, json);
            }
            catch
            {
                // Ignore save errors
            }
        }
    }
}
