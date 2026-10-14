using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
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

[ComImport]
[Guid("00021401-0000-0000-C000-000000000046")]
internal class ShellLinkClass { }

[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("000214F9-0000-0000-C000-000000000046")]
internal interface IShellLinkW
{
    void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cchMaxPath, IntPtr pfd, uint fFlags);
    void GetIDList(out IntPtr ppidl);
    void SetIDList(IntPtr pidl);
    void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cchMaxName);
    void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
    void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cchMaxPath);
    void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
    void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cchMaxPath);
    void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
    void GetHotkey(out short pwHotkey);
    void SetHotkey(short wHotkey);
    void GetShowCmd(out int piShowCmd);
    void SetShowCmd(int iShowCmd);
    void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
    void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
    void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
    void Resolve(IntPtr hwnd, uint fFlags);
    void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
}

[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("0000010b-0000-0000-C000-000000000046")]
internal interface IPersistFile
{
    void GetClassID(out Guid pClassID);
    [PreserveSig] int IsDirty();
    void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
    void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
    void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
    void GetCurFile([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder ppszFileName);
}

public class AppDiscoveryService
{
    private static readonly string HistoryFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LUMA",
        "search_history.json");

    private static readonly string StarredFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LUMA",
        "starred_apps.json");

    private static readonly string RecentAppsFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LUMA",
        "recent_apps.json");

    private List<IslandAppItem> _cachedApps = new();
    private List<IslandAppItem> _cachedRecent = new();
    private List<string> _recentSearches = new();
    private readonly HashSet<string> _starredPaths = new(StringComparer.OrdinalIgnoreCase);
    private List<string> _recentAppPaths = new();

    public IReadOnlyList<IslandAppItem> CachedApps => _cachedApps;
    public IReadOnlyList<IslandAppItem> CachedRecent => _cachedRecent;
    public IReadOnlyList<string> RecentSearches => _recentSearches;
    public IReadOnlyCollection<string> StarredPaths => _starredPaths;

    public AppDiscoveryService()
    {
        LoadSearchHistory();
        LoadStarredApps();
        LoadRecentApps();
        RefreshDataSync();
    }

    public bool IsStarred(string path) => _starredPaths.Contains(path);

    public void ToggleStarred(string path, string name)
    {
        if (_starredPaths.Contains(path))
        {
            _starredPaths.Remove(path);
        }
        else
        {
            _starredPaths.Add(path);
        }
        SaveStarredApps();

        // Update in-memory models
        _cachedApps = _cachedApps.Select(a => a.Path.Equals(path, StringComparison.OrdinalIgnoreCase) ? a with { IsStarred = IsStarred(path) } : a).ToList();
        _cachedRecent = _cachedRecent.Select(a => a.Path.Equals(path, StringComparison.OrdinalIgnoreCase) ? a with { IsStarred = IsStarred(path) } : a).ToList();
    }

    public void RecordRecentApp(IslandAppItem item)
    {
        if (string.IsNullOrEmpty(item.Path)) return;
        _recentAppPaths.RemoveAll(p => p.Equals(item.Path, StringComparison.OrdinalIgnoreCase));
        _recentAppPaths.Insert(0, item.Path);
        if (_recentAppPaths.Count > 10)
        {
            _recentAppPaths = _recentAppPaths.Take(10).ToList();
        }
        SaveRecentApps();
        _cachedRecent = DiscoverRecentApps();
    }

    public void RefreshDataSync()
    {
        try
        {
            _cachedApps = DiscoverInstalledApps();
            _cachedRecent = DiscoverRecentApps();
        }
        catch { }
    }

    public async Task RefreshDataAsync()
    {
        await Task.Run(RefreshDataSync);
    }

    public static string ResolveShortcutTarget(string lnkPath)
    {
        try
        {
            var link = (IShellLinkW)new ShellLinkClass();
            ((IPersistFile)link).Load(lnkPath, 0);
            var sb = new StringBuilder(260);
            link.GetPath(sb, sb.Capacity, IntPtr.Zero, 0);
            return sb.ToString();
        }
        catch
        {
            return string.Empty;
        }
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

                    // Exclude uninstallers, help, license, readme, and Internet Explorer
                    var lower = name.ToLowerInvariant();
                    if (lower.Contains("uninstall") || lower.Contains("удалить") ||
                        lower.Contains("help") || lower.Contains("readme") ||
                        lower.Contains("documentation") || lower.Contains("manual") ||
                        lower.Contains("license") || lower.Contains("website") ||
                        lower.Contains("iexplore") || lower.Contains("internet explorer") ||
                        name.Equals("Интернет", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var target = ResolveShortcutTarget(lnk);
                    if (!string.IsNullOrEmpty(target))
                    {
                        var targetLower = target.ToLowerInvariant();
                        if (targetLower.Contains("iexplore.exe") || targetLower.Contains("uninstall") || targetLower.Contains("setup.exe"))
                        {
                            continue;
                        }

                        if (!File.Exists(target) && !Directory.Exists(target))
                        {
                            continue;
                        }
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
                            IsStarred: IsStarred(lnk)
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

    private void AddSystemAppIfFound(string name, string exePath, List<IslandAppItem> list, HashSet<string> seen, HashSet<string> running)
    {
        if (File.Exists(exePath) && seen.Add(name))
        {
            var icon = ExtractFileIcon(exePath);
            var procName = Path.GetFileNameWithoutExtension(exePath);
            var isRunning = running.Contains(procName);
            list.Add(new IslandAppItem(name, exePath, "APPLICATIONS", "System Application", icon, isRunning, IsStarred(exePath)));
        }
    }

    private List<IslandAppItem> DiscoverRecentApps()
    {
        var result = new List<IslandAppItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Apps recently launched via LUMA
        foreach (var p in _recentAppPaths)
        {
            var match = _cachedApps.FirstOrDefault(a => a.Path.Equals(p, StringComparison.OrdinalIgnoreCase));
            if (match != null && seen.Add(match.Path))
            {
                result.Add(match with { Category = "RECENT", IsStarred = IsStarred(match.Path) });
            }
        }

        // 2. Currently running applications with active processes
        foreach (var app in _cachedApps.Where(a => a.IsOpen))
        {
            if (seen.Add(app.Path))
            {
                result.Add(app with { Category = "RECENT", IsStarred = IsStarred(app.Path) });
            }
            if (result.Count >= 6) break;
        }

        // 3. Real recent application shortcuts from Windows Recent directory
        var recentDir = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
        if (Directory.Exists(recentDir) && result.Count < 6)
        {
            try
            {
                var files = new DirectoryInfo(recentDir)
                    .GetFiles("*.lnk")
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .Take(25);

                foreach (var f in files)
                {
                    var target = ResolveShortcutTarget(f.FullName);
                    if (!string.IsNullOrEmpty(target) && target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(target))
                    {
                        var targetLower = target.ToLowerInvariant();
                        if (targetLower.Contains("iexplore") || targetLower.Contains("uninstall") || targetLower.Contains("setup"))
                            continue;

                        var name = Path.GetFileNameWithoutExtension(target);
                        if (seen.Add(target))
                        {
                            var icon = ExtractFileIcon(target);
                            result.Add(new IslandAppItem(
                                Name: name,
                                Path: target,
                                Category: "RECENT",
                                Subtitle: "Recent Application",
                                Icon: icon,
                                IsOpen: false,
                                IsStarred: IsStarred(target)
                            ));
                            if (result.Count >= 6) break;
                        }
                    }
                }
            }
            catch { }
        }

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
                        list.Add(app with { Category = "SEARCH", IsStarred = IsStarred(app.Path) });
                    }
                }
            }

            // 2. Search in recent apps
            foreach (var rec in _cachedRecent)
            {
                if (rec.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
                {
                    if (seen.Add(rec.Path))
                    {
                        list.Add(rec with { Category = "SEARCH", IsStarred = IsStarred(rec.Path) });
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
                                    IsStarred: IsStarred(filePath)
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

    public void RemoveSearchQuery(string query)
    {
        _recentSearches.RemoveAll(s => s.Equals(query, StringComparison.OrdinalIgnoreCase));
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

        // By default empty: no hardcoded mock queries
        _recentSearches = new List<string>();
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

    private void LoadStarredApps()
    {
        try
        {
            if (File.Exists(StarredFilePath))
            {
                var json = File.ReadAllText(StarredFilePath);
                var items = JsonSerializer.Deserialize<List<string>>(json);
                if (items != null)
                {
                    _starredPaths.Clear();
                    foreach (var it in items) _starredPaths.Add(it);
                }
            }
        }
        catch { }
    }

    private void SaveStarredApps()
    {
        try
        {
            var dir = Path.GetDirectoryName(StarredFilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var json = JsonSerializer.Serialize(_starredPaths.ToList());
            File.WriteAllText(StarredFilePath, json);
        }
        catch { }
    }

    private void LoadRecentApps()
    {
        try
        {
            if (File.Exists(RecentAppsFilePath))
            {
                var json = File.ReadAllText(RecentAppsFilePath);
                var items = JsonSerializer.Deserialize<List<string>>(json);
                if (items != null)
                {
                    _recentAppPaths = items.Take(10).ToList();
                }
            }
        }
        catch { }
    }

    private void SaveRecentApps()
    {
        try
        {
            var dir = Path.GetDirectoryName(RecentAppsFilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var json = JsonSerializer.Serialize(_recentAppPaths);
            File.WriteAllText(RecentAppsFilePath, json);
        }
        catch { }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_LARGEICON = 0x000000000;

    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public static ImageSource? ExtractFileIcon(string path)
    {
        try
        {
            // If it's a shortcut (.lnk), resolve the target executable to get clean icon without shortcut arrow overlay
            if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                var target = ResolveShortcutTarget(path);
                if (!string.IsNullOrEmpty(target) && (File.Exists(target) || Directory.Exists(target)))
                {
                    path = target;
                }
            }

            if (File.Exists(path) || Directory.Exists(path))
            {
                var shinfo = new SHFILEINFO();
                var res = SHGetFileInfo(path, 0, ref shinfo, (uint)Marshal.SizeOf(shinfo), SHGFI_ICON | SHGFI_LARGEICON);
                if (res != IntPtr.Zero && shinfo.hIcon != IntPtr.Zero)
                {
                    try
                    {
                        var bmp = Imaging.CreateBitmapSourceFromHIcon(
                            shinfo.hIcon,
                            Int32Rect.Empty,
                            BitmapSizeOptions.FromEmptyOptions());
                        bmp.Freeze();
                        return bmp;
                    }
                    finally
                    {
                        DestroyIcon(shinfo.hIcon);
                    }
                }

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
