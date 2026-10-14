using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LUMA.Services;

public record WallpaperItem(
    string Id,
    string Title,
    string FilePath,
    ImageSource Image,
    bool IsCurrent = false
);

public class WallpaperService
{
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int SystemParametersInfo(int uAction, int uParam, string lpvParam, int fuWinIni);

    private const int SpiSetDeskWallpaper = 20;
    private const int SpifUpdateIniFile = 0x01;
    private const int SpifSendChange = 0x02;

    public static string WallpapersDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LUMA",
        "Wallpapers");

    public List<WallpaperItem> Wallpapers { get; private set; } = new();
    public int SelectedIndex { get; set; } = 0; // Default to current wallpaper if available

    public WallpaperService()
    {
        InitializeWallpapers();
    }

    public static string? GetCurrentWindowsWallpaperPath()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
            var wall = key?.GetValue("Wallpaper") as string;
            if (!string.IsNullOrEmpty(wall) && File.Exists(wall))
            {
                return wall;
            }

            var transcoded = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                @"Microsoft\Windows\Themes\TranscodedWallpaper");
            if (File.Exists(transcoded))
            {
                return transcoded;
            }
        }
        catch { }

        return null;
    }

    public void InitializeWallpapers()
    {
        try
        {
            if (!Directory.Exists(WallpapersDirectory))
            {
                Directory.CreateDirectory(WallpapersDirectory);
            }

            var items = new List<WallpaperItem>();

            // 1. Detect current Windows wallpaper
            var currentPath = GetCurrentWindowsWallpaperPath();
            if (!string.IsNullOrEmpty(currentPath))
            {
                var curImg = LoadBitmapNoLock(currentPath);
                if (curImg != null)
                {
                    items.Add(new WallpaperItem("current_wall", "Текущие обои", currentPath, curImg, true));
                }
            }

            var defFiles = new[]
            {
                ("wall_1.jpg", "Midnight Peaks", Color.FromRgb(0x12, 0x18, 0x2E), Color.FromRgb(0x28, 0x34, 0x56), Color.FromRgb(0x1B, 0x1E, 0x33)),
                ("wall_2.jpg", "Sunset Ridge", Color.FromRgb(0x40, 0x18, 0x32), Color.FromRgb(0x82, 0x34, 0x48), Color.FromRgb(0x28, 0x14, 0x22)),
                ("wall_3.jpg", "Autumn Dolomites", Color.FromRgb(0x52, 0x82, 0xAD), Color.FromRgb(0xD8, 0x94, 0x4A), Color.FromRgb(0x8E, 0x48, 0x18)),
                ("wall_4.jpg", "Misty Terraces", Color.FromRgb(0x60, 0x6E, 0x78), Color.FromRgb(0x42, 0x4E, 0x58), Color.FromRgb(0x24, 0x2A, 0x30)),
                ("wall_5.jpg", "Alpine Lake", Color.FromRgb(0x28, 0x58, 0x82), Color.FromRgb(0x68, 0xA0, 0xBE), Color.FromRgb(0x14, 0x2C, 0x40))
            };

            for (int i = 0; i < defFiles.Length; i++)
            {
                var (fileName, title, top, mid, bot) = defFiles[i];
                var fullPath = Path.Combine(WallpapersDirectory, fileName);

                if (!File.Exists(fullPath))
                {
                    GenerateWallpaperImage(fullPath, top, mid, bot, i);
                }

                var imgSource = LoadBitmapNoLock(fullPath);
                if (imgSource != null)
                {
                    items.Add(new WallpaperItem($"wall_{i + 1}", title, fullPath, imgSource, false));
                }
            }

            Wallpapers = items;
            SelectedIndex = 0; // Focus on current wallpaper
        }
        catch
        {
        }
    }

    public void LoadCustomFolder(string folderPath)
    {
        if (!Directory.Exists(folderPath)) return;

        try
        {
            var validExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".bmp" };
            var files = Directory.EnumerateFiles(folderPath, "*.*", SearchOption.TopDirectoryOnly);
            var customItems = new List<WallpaperItem>();

            int idx = 1;
            foreach (var f in files)
            {
                var ext = Path.GetExtension(f);
                if (validExts.Contains(ext))
                {
                    var img = LoadBitmapNoLock(f);
                    customItems.Add(new WallpaperItem($"custom_{idx}", Path.GetFileNameWithoutExtension(f), f, img));
                    idx++;
                    if (customItems.Count >= 10) break;
                }
            }

            if (customItems.Count > 0)
            {
                // Pad to at least 5 if possible by repeating or keeping defaults
                while (customItems.Count < 5 && Wallpapers.Count > 0)
                {
                    customItems.Add(Wallpapers[customItems.Count % Wallpapers.Count]);
                }
                Wallpapers = customItems;
                SelectedIndex = Math.Min(2, Wallpapers.Count - 1);
            }
        }
        catch { }
    }

    public bool ApplyWallpaper(string filePath)
    {
        try
        {
            if (!File.Exists(filePath)) return false;

            var res = SystemParametersInfo(SpiSetDeskWallpaper, 0, filePath, SpifUpdateIniFile | SpifSendChange);
            return res != 0;
        }
        catch
        {
            return false;
        }
    }

    private static ImageSource LoadBitmapNoLock(string path)
    {
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.UriSource = new Uri(path);
        bi.EndInit();
        bi.Freeze();
        return bi;
    }

    private static void GenerateWallpaperImage(string outputPath, Color top, Color mid, Color bot, int variant)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            // Background Sky Gradient
            var grad = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, 1)
            };
            grad.GradientStops.Add(new GradientStop(top, 0.0));
            grad.GradientStops.Add(new GradientStop(mid, 0.48));
            grad.GradientStops.Add(new GradientStop(bot, 1.0));
            dc.DrawRectangle(grad, null, new Rect(0, 0, 1920, 1080));

            // Mountains / Landscape Geometry
            var stream = new StreamGeometry();
            using (var ctx = stream.Open())
            {
                if (variant == 2) // Autumn hill & trail
                {
                    ctx.BeginFigure(new Point(0, 520), true, true);
                    ctx.BezierTo(new Point(450, 420), new Point(850, 560), new Point(1350, 410), true, true);
                    ctx.LineTo(new Point(1920, 480), true, true);
                    ctx.LineTo(new Point(1920, 1080), true, true);
                    ctx.LineTo(new Point(0, 1080), true, true);
                }
                else
                {
                    ctx.BeginFigure(new Point(0, 680), true, true);
                    ctx.LineTo(new Point(420, 440), true, true);
                    ctx.LineTo(new Point(850, 580), true, true);
                    ctx.LineTo(new Point(1250, 390), true, true);
                    ctx.LineTo(new Point(1680, 540), true, true);
                    ctx.LineTo(new Point(1920, 460), true, true);
                    ctx.LineTo(new Point(1920, 1080), true, true);
                    ctx.LineTo(new Point(0, 1080), true, true);
                }
            }
            stream.Freeze();

            var hillBrush = new SolidColorBrush(Color.FromArgb(220, bot.R, bot.G, bot.B));
            dc.DrawGeometry(hillBrush, null, stream);

            // Foreground hills
            var fgStream = new StreamGeometry();
            using (var ctx = fgStream.Open())
            {
                ctx.BeginFigure(new Point(0, 780), true, true);
                ctx.BezierTo(new Point(600, 680), new Point(1200, 720), new Point(1920, 640), true, true);
                ctx.LineTo(new Point(1920, 1080), true, true);
                ctx.LineTo(new Point(0, 1080), true, true);
            }
            fgStream.Freeze();

            var fgBrush = new SolidColorBrush(Color.FromArgb(245, (byte)Math.Max(0, bot.R - 20), (byte)Math.Max(0, bot.G - 20), (byte)Math.Max(0, bot.B - 20)));
            dc.DrawGeometry(fgBrush, null, fgStream);
        }

        var rtb = new RenderTargetBitmap(1920, 1080, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);

        var encoder = new JpegBitmapEncoder { QualityLevel = 92 };
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(outputPath);
        encoder.Save(fs);
    }
}
