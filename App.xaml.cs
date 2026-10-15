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



        DispatcherUnhandledException += (s, args) =>
        {
            try
            {
                File.AppendAllText("crash.log", $"[{System.DateTime.Now}] DispatcherException: {args.Exception}\n");
            }
            catch { }
            args.Handled = true; // Prevent app crashing
        };

        System.AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            try
            {
                File.AppendAllText("crash.log", $"[{System.DateTime.Now}] AppDomainException: {args.ExceptionObject}\n");
            }
            catch { }
        };


        if (e.Args.Length > 0 && e.Args[0] == "--render-preview")
        {
            var outputDir = e.Args.Length > 1 ? e.Args[1] : Directory.GetCurrentDirectory();
            RenderSnapshots(outputDir);
            Shutdown(0);
            return;
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

        // 11. Voice Notch Snapshot (Win + Alt Mode - Silent ChatGPT short vertical bars)
        win.IslandSurface.Visibility = Visibility.Collapsed;
        win.ExpandedContent.Visibility = Visibility.Collapsed;
        win.SetVoiceAssistantActive(true);
        win.CompactNotch.Visibility = Visibility.Visible;
        win.CompactNotch.Width = 236;
        win.CompactNotch.Height = 24;
        win.VoiceNotchContainer.Visibility = Visibility.Visible;
        win.VoiceNotchContainer.Opacity = 1;
        win.CompactButton.Visibility = Visibility.Collapsed;
        win.UpdateLayout();
        win.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
        RenderVisual(win.CompactNotch, 236, 24, Path.Combine(outputDir, "voice_notch_preview.png"), new SolidColorBrush(Color.FromRgb(80, 85, 100)));

        // 11b. Voice Notch Snapshot (Active speech responding to voice - ChatGPT undulating wave)
        double[] activeHeights = { 3.5, 4.0, 5.0, 7.0, 9.5, 12.0, 14.0, 15.5, 16.0, 16.5, 16.0, 15.5, 14.0, 12.0, 9.5, 7.0, 5.0, 4.0, 3.5 };
        System.Windows.Controls.Border[] previewBars = {
            win.WaveBar1, win.WaveBar2, win.WaveBar3, win.WaveBar4, win.WaveBar5,
            win.WaveBar6, win.WaveBar7, win.WaveBar8, win.WaveBar9, win.WaveBar10,
            win.WaveBar11, win.WaveBar12, win.WaveBar13, win.WaveBar14, win.WaveBar15,
            win.WaveBar16, win.WaveBar17, win.WaveBar18, win.WaveBar19
        };
        for (int i = 0; i < previewBars.Length; i++)
        {
            previewBars[i].Height = activeHeights[i];
        }
        win.UpdateLayout();
        win.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
        RenderVisual(win.CompactNotch, 236, 24, Path.Combine(outputDir, "voice_notch_active_preview.png"), new SolidColorBrush(Color.FromRgb(80, 85, 100)));

        // 12. Transcription Bar Window Snapshot (Floating Capsule matching Photo 1)
        var tBar = new TranscriptionBarWindow();
        tBar.Show();
        tBar.SetText("");
        tBar.UpdateLayout();
        tBar.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
        RenderVisual(tBar.PillBorder, 740, 70, Path.Combine(outputDir, "transcription_bar_placeholder_preview.png"));

        tBar.SetText("Hey arsh whats up? how about we go and get some coffee at 9 in the morning, ahh nevermind lets go at");
        tBar.UpdateLayout();
        tBar.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
        RenderVisual(tBar.PillBorder, 740, 70, Path.Combine(outputDir, "transcription_bar_preview.png"));
        tBar.Close();

        win.Close();
    }

    private static void RenderVisual(Visual visual, int width, int height, string filePath, Brush? background = null)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            if (background != null)
            {
                dc.DrawRectangle(background, null, new Rect(0, 0, width, height));
            }
            dc.DrawRectangle(new VisualBrush(visual), null, new Rect(0, 0, width, height));
        }

        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(filePath);
        encoder.Save(fs);
    }
}
