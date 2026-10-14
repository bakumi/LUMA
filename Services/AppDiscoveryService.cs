using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LUMA.Services;

public record IslandAppItem(
    string Name,
    string Path,
    string Category, // "STARRED", "RECENT", "APPLICATIONS", "SEARCH"
    string Subtitle,
    ImageSource? Icon,
    bool IsOpen,
    bool IsStarred
);

public class AppDiscoveryService
{
    private static readonly string HistoryFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LUMA",
        "search_history.json");

    private List<IslandAppItem> _cachedApps = new();
    private List<IslandAppItem> _cachedRecent = new();
    private List<string> _recentSearches = new();

    public IReadOnlyList<IslandAppItem> CachedApps => _cachedApps;
    public IReadOnlyList<IslandAppItem> CachedRecent => _cachedRecent;
    public IReadOnlyList<string> RecentSearches => _recentSearches;

    public AppDiscoveryService()
    {
        LoadSearchHistory();
        RefreshDataSync();
    }

    public void RefreshDataSync()
    {
        try
        {
            _cachedApps = DiscoverInstalledApps();
            _cachedRecent = DiscoverRecentFiles();
        }
        catch { }
    }

    public async Task RefreshDataAsync()
    {
        await Task.Run(RefreshDataSync);
    }

    private List<IslandAppItem> DiscoverInstalledApps()
    {
        var result = new List<IslandAppItem>();
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var startDirs = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms)
        };

        var runningProcNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var p in Process.GetProcesses())
            {
                try { runningProcNames.Add(p.ProcessName); } catch { }
            }
        }
        catch { }

        foreach (var dir in startDirs)
        {
            if (!Directory.Exists(dir)) continue;

            try
            {
                var lnks = Directory.EnumerateFiles(dir, "*.lnk", SearchOption.AllDirectories);
                foreach (var lnk in lnks)
                {
                    var name = Path.GetFileNameWithoutExtension(lnk);
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    // Exclude uninstallers, help, license, readme
                    var lower = name.ToLowerInvariant();
                    if (lower.Contains("uninstall") || lower.Contains("удалить") ||
                        lower.Contains("help") || lower.Contains("readme") ||
                        lower.Contains("documentation") || lower.Contains("manual") ||
                        lower.Contains("license") || lower.Contains("website"))
                    {
                        continue;
                    }

                    if (seenNames.Add(name))
                    {
                        var icon = ExtractFileIcon(lnk);
                        var isRunning = runningProcNames.Contains(name) ||
                                       runningProcNames.Contains(Path.GetFileNameWithoutExtension(lnk));

                        result.Add(new IslandAppItem(
                            Name: name,
                            Path: lnk,
                            Category: "APPLICATIONS",
                            Subtitle: "Application",
                            Icon: icon,
                            IsOpen: isRunning,
                            IsStarred: false
                        ));
                    }
                }
            }
            catch { }
        }

        // Add standard Windows apps if not already present
        AddSystemAppIfFound("File Explorer", Environment.GetFolderPath(Environment.SpecialFolder.Windows) + @"\explorer.exe", result, seenNames, runningProcNames);
        AddSystemAppIfFound("Task Manager", Environment.GetFolderPath(Environment.SpecialFolder.System) + @"\Taskmgr.exe", result, seenNames, runningProcNames);
        AddSystemAppIfFound("Notepad", Environment.GetFolderPath(Environment.SpecialFolder.System) + @"\notepad.exe", result, seenNames, runningProcNames);
        AddSystemAppIfFound("Calculator", Environment.GetFolderPath(Environment.SpecialFolder.System) + @"\calc.exe", result, seenNames, runningProcNames);
        AddSystemAppIfFound("Paint", Environment.GetFolderPath(Environment.SpecialFolder.System) + @"\mspaint.exe", result, seenNames, runningProcNames);

        return result;
    }

    private static void AddSystemAppIfFound(string name, string exePath, List<IslandAppItem> list, HashSet<string> seen, HashSet<string> running)
    {
        if (File.Exists(exePath) && seen.Add(name))
        {
            var icon = ExtractFileIcon(exePath);
            var procName = Path.GetFileNameWithoutExtension(exePath);
            var isRunning = running.Contains(procName);
            list.Add(new IslandAppItem(name, exePath, "APPLICATIONS", "System Application", icon, isRunning, false));
        }
    }

    private List<IslandAppItem> DiscoverRecentFiles()
    {
        var result = new List<IslandAppItem>();
        var recentDir = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
        if (!Directory.Exists(recentDir)) return result;

        try
        {
            var files = new DirectoryInfo(recentDir)
                .GetFiles("*.lnk")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Take(12);

            foreach (var f in files)
            {
                var name = Path.GetFileNameWithoutExtension(f.Name);
                if (string.IsNullOrWhiteSpace(name) || name.StartsWith("!")) continue;

                var icon = ExtractFileIcon(f.FullName);
                result.Add(new IslandAppItem(
                    Name: name,
                    Path: f.FullName,
                    Category: "RECENT",
                    Subtitle: "Recent file",
                    Icon: icon,
                    IsOpen: false,
                    IsStarred: false
                ));
            }
        }
        catch { }

        return result;
    }

    public async Task<List<IslandAppItem>> SearchAsync(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return new List<IslandAppItem>();
        }

        return await Task.Run(() =>
        {
            var list = new List<IslandAppItem>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var q = query.Trim();

            // 1. Search in cached apps
            foreach (var app in _cachedApps)
            {
                if (app.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
                {
                    if (seen.Add(app.Path))
                    {
                        list.Add(app with { Category = "SEARCH" });
                    }
                }
            }

            // 2. Search in recent files
            foreach (var rec in _cachedRecent)
            {
                if (rec.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
                {
                    if (seen.Add(rec.Path))
                    {
                        list.Add(rec with { Category = "SEARCH" });
                    }
                }
            }

            // 3. Search common user directories for matching files
            if (q.Length >= 2 && list.Count < 20)
            {
                var userSearchDirs = new[]
                {
                    Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
                    Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)
                };

                foreach (var dir in userSearchDirs)
                {
                    if (!Directory.Exists(dir)) continue;

                    try
                    {
                        var matches = Directory.EnumerateFiles(dir, $"*{q}*", SearchOption.TopDirectoryOnly)
                            .Take(6);

                        foreach (var filePath in matches)
                        {
                            if (seen.Add(filePath))
                            {
                                var fName = Path.GetFileName(filePath);
                                var ext = Path.GetExtension(filePath).TrimStart('.').ToUpperInvariant();
                                var icon = ExtractFileIcon(filePath);

                                list.Add(new IslandAppItem(
                                    Name: fName,
                                    Path: filePath,
                                    Category: "SEARCH",
                                    Subtitle: string.IsNullOrEmpty(ext) ? "File" : $"{ext} File",
                                    Icon: icon,
                                    IsOpen: false,
                                    IsStarred: false
                                ));

                                if (list.Count >= 25) break;
                            }
                        }
                    }
                    catch { }

                    if (list.Count >= 25) break;
                }
            }

            return list;
        });
    }

    public void AddSearchQuery(string query)
    {
        var clean = query.Trim();
        if (string.IsNullOrWhiteSpace(clean) || clean.Length < 2) return;

        _recentSearches.RemoveAll(s => s.Equals(clean, StringComparison.OrdinalIgnoreCase));
        _recentSearches.Insert(0, clean);
        if (_recentSearches.Count > 5)
        {
            _recentSearches = _recentSearches.Take(5).ToList();
        }

        SaveSearchHistory();
    }

    private void LoadSearchHistory()
    {
        try
        {
            if (File.Exists(HistoryFilePath))
            {
                var json = File.ReadAllText(HistoryFilePath);
                var items = JsonSerializer.Deserialize<List<string>>(json);
                if (items != null)
                {
                    _recentSearches = items.Take(5).ToList();
                    return;
                }
            }
        }
        catch { }

        // Default useful initial queries if none exist
        _recentSearches = new List<string> { "code", "steam", "calc", "cmd" };
    }

    private void SaveSearchHistory()
    {
        try
        {
            var dir = Path.GetDirectoryName(HistoryFilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var json = JsonSerializer.Serialize(_recentSearches);
            File.WriteAllText(HistoryFilePath, json);
        }
        catch { }
    }

    public static ImageSource? ExtractFileIcon(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                using var sysIcon = System.Drawing.Icon.ExtractAssociatedIcon(path);
                if (sysIcon != null)
                {
                    var bmp = Imaging.CreateBitmapSourceFromHIcon(
                        sysIcon.Handle,
                        Int32Rect.Empty,
                        BitmapSizeOptions.FromEmptyOptions());
                    bmp.Freeze();
                    return bmp;
                }
            }
        }
        catch { }

        return null;
    }
}
