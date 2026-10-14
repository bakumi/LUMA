using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace LUMA;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Length > 0 && e.Args[0] == "--render-preview")
        {
            var outputDir = e.Args.Length > 1 ? e.Args[1] : Directory.GetCurrentDirectory();
            RenderSnapshots(outputDir);
            Shutdown(0);
        }
    }

    private static void RenderSnapshots(string outputDir)
    {
        Directory.CreateDirectory(outputDir);

        var win = new MainWindow();
        win.Show();
        win.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);

        win.IslandSurface.Visibility = Visibility.Visible;
        win.IslandSurface.Opacity = 1;
        win.IslandScale.ScaleX = 1;
        win.IslandScale.ScaleY = 1;
        win.ExpandedContent.Visibility = Visibility.Visible;
        win.ExpandedContent.Opacity = 1;

        // 1. Controls Tab (Photo 2: 540x320)
        win.SetTabForSnapshot("controls");
        win.UpdateLayout();
        win.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
        RenderVisual(win.IslandSurface, 760, 360, Path.Combine(outputDir, "photo2_controls_preview.png"));

        // 2. Tabbed Player Tab (Replaces Today: 480x168)
        win.SetTabForSnapshot("player");
        win.UpdateLayout();
        win.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
        RenderVisual(win.IslandSurface, 760, 210, Path.Combine(outputDir, "player_tab_preview.png"));

        // 3. Standalone Player (Photo 1 without tabs: 380x126)
        win.SetTabForSnapshot("standalone_player");
        win.UpdateLayout();
        win.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
        RenderVisual(win.IslandSurface, 760, 180, Path.Combine(outputDir, "photo1_player_preview.png"));

        // 4. Power Tab (Photo 5: 540x140)
        win.SetTabForSnapshot("power");
        win.UpdateLayout();
        win.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
        RenderVisual(win.IslandSurface, 760, 180, Path.Combine(outputDir, "photo5_power_preview.png"));

        // 5. Stats Tab (Photo 3: 680x275)
        win.SetTabForSnapshot("stats");
        win.UpdateLayout();
        win.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
        RenderVisual(win.IslandSurface, 760, 310, Path.Combine(outputDir, "photo3_stats_preview.png"));

        // 6. Apps Tab (Photo 4: 520x435)
        win.SetTabForSnapshot("apps");
        win.UpdateLayout();
        win.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
        RenderVisual(win.IslandSurface, 760, 470, Path.Combine(outputDir, "photo4_apps_preview.png"));

        // 7. Wallpaper Tab (Exact Reference Photo: 720x246)
        win.SetTabForSnapshot("wallpaper");
        win.UpdateLayout();
        win.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
        RenderVisual(win.IslandSurface, 760, 290, Path.Combine(outputDir, "photo6_wallpaper_preview.png"));

        // 8. In-Island Audio Output Drawer
        win.OpenDrawerForSnapshot("audio");
        win.UpdateLayout();
        win.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
        RenderVisual(win.IslandSurface, 760, 360, Path.Combine(outputDir, "drawer_audio_preview.png"));

        // 9. In-Island Bluetooth Devices Drawer
        win.OpenDrawerForSnapshot("bluetooth");
        win.UpdateLayout();
        win.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
        RenderVisual(win.IslandSurface, 760, 360, Path.Combine(outputDir, "drawer_bluetooth_preview.png"));

        // 10. In-Island Wi-Fi Drawer
        win.OpenDrawerForSnapshot("wifi");
        win.UpdateLayout();
        win.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
        RenderVisual(win.IslandSurface, 760, 360, Path.Combine(outputDir, "drawer_wifi_preview.png"));

        win.Close();
    }

    private static void RenderVisual(Visual visual, int width, int height, string filePath)
    {
        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(filePath);
        encoder.Save(fs);
    }
}
