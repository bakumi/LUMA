using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace LUMA;

public partial class TranscriptionBarWindow : Window
{
    public event Action? InsertRequested;
    public event Action? CancelRequested;

    private readonly List<string> _currentWords = new();

    public TranscriptionBarWindow()
    {
        InitializeComponent();
        Loaded += TranscriptionBarWindow_Loaded;
    }

    private void TranscriptionBarWindow_Loaded(object sender, RoutedEventArgs e)
    {
        PositionAboveTaskbar();
    }

    public void PositionAboveTaskbar()
    {
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Left + (workArea.Width - Width) / 2;
        Top = workArea.Bottom - Height - 12; // 12px above taskbar
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            InsertRequested?.Invoke();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CancelRequested?.Invoke();
        }
    }

    public void SetText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            PlaceholderTextBlock.Visibility = Visibility.Visible;
            WordsContainer.Children.Clear();
            _currentWords.Clear();
            return;
        }

        // When text appears, completely dismiss "Слушаю вас..."
        PlaceholderTextBlock.Visibility = Visibility.Collapsed;

        var targetWords = text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

        // If target has fewer words than currently displayed (e.g. phrase was revised), trim
        while (WordsContainer.Children.Count > targetWords.Length)
        {
            WordsContainer.Children.RemoveAt(WordsContainer.Children.Count - 1);
            if (_currentWords.Count > WordsContainer.Children.Count)
            {
                _currentWords.RemoveAt(_currentWords.Count - 1);
            }
        }

        try
        {
            // Update or append words with smooth glow & color animation
            for (int i = 0; i < targetWords.Length; i++)
            {
                var word = targetWords[i];

                if (i < WordsContainer.Children.Count)
                {
                    // Word already exists in container; update text if changed
                    if (WordsContainer.Children[i] is TextBlock existingTb && existingTb.Text != word + " ")
                    {
                        existingTb.Text = word + " ";
                        if (i < _currentWords.Count)
                        {
                            _currentWords[i] = word;
                        }
                        else
                        {
                            _currentWords.Add(word);
                        }
                    }
                }
            else
            {
                // Newly arriving word: create with smooth glow and transition from grey to white
                var tb = new TextBlock
                {
                    Text = word + " ",
                    FontSize = 13.5,
                    FontWeight = FontWeights.Normal,
                    VerticalAlignment = VerticalAlignment.Center,
                    RenderTransformOrigin = new Point(0.5, 0.5)
                };

                // Animate foreground from muted grey (#FFB0B0B8) to brilliant white (#FFFFFFFF)
                var greyColor = Color.FromRgb(176, 176, 184);
                var whiteColor = Colors.White;
                var brush = new SolidColorBrush(greyColor);
                tb.Foreground = brush;

                var colorAnim = new ColorAnimation(greyColor, whiteColor, TimeSpan.FromMilliseconds(320))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                brush.BeginAnimation(SolidColorBrush.ColorProperty, colorAnim);

                // Subtle soft ethereal glow effect that dissipates as word settles
                var glow = new DropShadowEffect
                {
                    Color = Colors.White,
                    BlurRadius = 5.0,
                    ShadowDepth = 0,
                    Opacity = 0.75
                };
                tb.Effect = glow;

                var glowFade = new DoubleAnimation(0.75, 0.0, TimeSpan.FromMilliseconds(380))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                var blurFade = new DoubleAnimation(5.0, 0.0, TimeSpan.FromMilliseconds(380));
                glowFade.Completed += (_, _) =>
                {
                    tb.Effect = null; // Clean up effect after animation finishes
                };
                glow.BeginAnimation(DropShadowEffect.OpacityProperty, glowFade);
                glow.BeginAnimation(DropShadowEffect.BlurRadiusProperty, blurFade);

                // Subtle organic scale pop-in
                var scale = new ScaleTransform(0.92, 0.92);
                tb.RenderTransform = scale;
                var scaleAnim = new DoubleAnimation(0.92, 1.0, TimeSpan.FromMilliseconds(220))
                {
                    EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.18 }
                };
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim);

                WordsContainer.Children.Add(tb);
                _currentWords.Add(word);
            }
        }

        // Smoothly follow the trailing text
        WordsScrollViewer.ScrollToRightEnd();
        }
        catch { }
    }

    public void ShowAnimated()
    {
        PositionAboveTaskbar();
        Opacity = 0;
        Visibility = Visibility.Visible;
        SetText("");

        var anim = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        BeginAnimation(OpacityProperty, anim);
    }

    public void HideAnimated(Action? onComplete = null)
    {
        var anim = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(160))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        anim.Completed += (_, _) =>
        {
            Visibility = Visibility.Collapsed;
            onComplete?.Invoke();
        };
        BeginAnimation(OpacityProperty, anim);
    }

    private void SendBtn_MouseDown(object sender, MouseButtonEventArgs e)
    {
        InsertRequested?.Invoke();
    }
}
