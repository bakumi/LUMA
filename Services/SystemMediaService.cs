using System.IO;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace LUMA.Services;

public record IslandMediaState(
    string Title,
    string Artist,
    BitmapSource? Thumbnail,
    double CurrentSeconds,
    double TotalSeconds,
    bool IsPlaying,
    bool IsSystemSession
);

public class SystemMediaService : IDisposable
{
    private GlobalSystemMediaTransportControlsSessionManager? _sessionManager;
    private GlobalSystemMediaTransportControlsSession? _currentSession;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _smoothTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };

    public event Action<IslandMediaState>? StateUpdated;

    // Fallback playlist when no Windows media is playing
    private record DemoTrack(string Title, string Artist, double DurationSeconds);
    private readonly DemoTrack[] _demoPlaylist =
    [
        new("lofi beats to study and relax", "lofi radio", 3735), // 62:15
        new("coffee morning in kyoto", "chillhop music", 2140),
        new("midnight train back home", "sleepy vibes", 2890)
    ];
    private int _demoTrackIndex = 0;
    private double _demoPosition = 57.0;
    private bool _demoPlaying = true;
    private DateTime _lastTickTime = DateTime.UtcNow;

    public IslandMediaState CurrentState { get; private set; }

    public SystemMediaService(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        CurrentState = new IslandMediaState(
            _demoPlaylist[0].Title,
            _demoPlaylist[0].Artist,
            null,
            57.0,
            _demoPlaylist[0].DurationSeconds,
            true,
            false
        );

        _smoothTimer.Tick += SmoothTimer_Tick;
        _smoothTimer.Start();

        Task.Run(InitializeSessionManagerAsync);
    }

    private async Task InitializeSessionManagerAsync()
    {
        try
        {
            _sessionManager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            if (_sessionManager != null)
            {
                _sessionManager.CurrentSessionChanged += SessionManager_CurrentSessionChanged;
                await RefreshSessionAsync();
            }
        }
        catch { }
    }

    private async void SessionManager_CurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
    {
        await RefreshSessionAsync();
    }

    private async Task RefreshSessionAsync()
    {
        try
        {
            if (_sessionManager == null) return;

            var session = _sessionManager.GetCurrentSession();
            if (_currentSession != null)
            {
                _currentSession.MediaPropertiesChanged -= CurrentSession_MediaPropertiesChanged;
                _currentSession.PlaybackInfoChanged -= CurrentSession_PlaybackInfoChanged;
                _currentSession.TimelinePropertiesChanged -= CurrentSession_TimelinePropertiesChanged;
            }

            _currentSession = session;
            if (_currentSession != null)
            {
                _currentSession.MediaPropertiesChanged += CurrentSession_MediaPropertiesChanged;
                _currentSession.PlaybackInfoChanged += CurrentSession_PlaybackInfoChanged;
                _currentSession.TimelinePropertiesChanged += CurrentSession_TimelinePropertiesChanged;
                await UpdateFromSessionAsync();
            }
            else
            {
                NotifyFallback();
            }
        }
        catch
        {
            NotifyFallback();
        }
    }

    private async void CurrentSession_MediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
    {
        await UpdateFromSessionAsync();
    }

    private async void CurrentSession_PlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
    {
        await UpdateFromSessionAsync();
    }

    private async void CurrentSession_TimelinePropertiesChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args)
    {
        await UpdateFromSessionAsync();
    }

    private async Task UpdateFromSessionAsync()
    {
        if (_currentSession == null)
        {
            NotifyFallback();
            return;
        }

        try
        {
            var mediaProps = await _currentSession.TryGetMediaPropertiesAsync();
            var playback = _currentSession.GetPlaybackInfo();
            var timeline = _currentSession.GetTimelineProperties();

            var title = mediaProps?.Title;
            var artist = mediaProps?.Artist;
            if (string.IsNullOrWhiteSpace(title))
            {
                title = _currentSession.SourceAppUserModelId ?? "System Audio";
            }
            if (string.IsNullOrWhiteSpace(artist))
            {
                artist = "Now Playing";
            }

            var isPlaying = playback?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            var totalSec = timeline?.EndTime.TotalSeconds ?? 0;
            var curSec = timeline?.Position.TotalSeconds ?? 0;

            if (totalSec <= 0) totalSec = 180; // default 3 min if streaming

            BitmapSource? thumbnail = null;
            if (mediaProps?.Thumbnail != null)
            {
                try
                {
                    using var stream = await mediaProps.Thumbnail.OpenReadAsync();
                    if (stream != null && stream.Size > 0)
                    {
                        var memStream = new MemoryStream();
                        await stream.AsStreamForRead().CopyToAsync(memStream);
                        memStream.Position = 0;

                        _dispatcher.Invoke(() =>
                        {
                            var bmp = new BitmapImage();
                            bmp.BeginInit();
                            bmp.CacheOption = BitmapCacheOption.OnLoad;
                            bmp.StreamSource = memStream;
                            bmp.EndInit();
                            bmp.Freeze();
                            thumbnail = bmp;
                        });
                    }
                }
                catch { }
            }

            _dispatcher.Invoke(() =>
            {
                CurrentState = new IslandMediaState(
                    title,
                    artist,
                    thumbnail,
                    curSec,
                    totalSec,
                    isPlaying,
                    true
                );
                _lastTickTime = DateTime.UtcNow;
                StateUpdated?.Invoke(CurrentState);
            });
        }
        catch
        {
            NotifyFallback();
        }
    }

    private void NotifyFallback()
    {
        _dispatcher.Invoke(() =>
        {
            var track = _demoPlaylist[_demoTrackIndex];
            CurrentState = new IslandMediaState(
                track.Title,
                track.Artist,
                null,
                _demoPosition,
                track.DurationSeconds,
                _demoPlaying,
                false
            );
            _lastTickTime = DateTime.UtcNow;
            StateUpdated?.Invoke(CurrentState);
        });
    }

    private void SmoothTimer_Tick(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        var delta = (now - _lastTickTime).TotalSeconds;
        _lastTickTime = now;

        if (!CurrentState.IsPlaying || delta <= 0 || delta > 1.0)
        {
            return;
        }

        var newPos = CurrentState.CurrentSeconds + delta;
        if (newPos >= CurrentState.TotalSeconds)
        {
            if (!CurrentState.IsSystemSession)
            {
                _demoTrackIndex = (_demoTrackIndex + 1) % _demoPlaylist.Length;
                _demoPosition = 0;
                NotifyFallback();
                return;
            }
            newPos = CurrentState.TotalSeconds;
        }

        CurrentState = CurrentState with { CurrentSeconds = newPos };
        StateUpdated?.Invoke(CurrentState);
    }

    public async Task TogglePlayPauseAsync()
    {
        if (_currentSession != null)
        {
            try
            {
                await _currentSession.TryTogglePlayPauseAsync();
                await UpdateFromSessionAsync();
                return;
            }
            catch { }
        }

        _demoPlaying = !_demoPlaying;
        CurrentState = CurrentState with { IsPlaying = _demoPlaying };
        StateUpdated?.Invoke(CurrentState);
    }

    public async Task SkipNextAsync()
    {
        if (_currentSession != null)
        {
            try
            {
                await _currentSession.TrySkipNextAsync();
                await UpdateFromSessionAsync();
                return;
            }
            catch { }
        }

        _demoTrackIndex = (_demoTrackIndex + 1) % _demoPlaylist.Length;
        _demoPosition = 0;
        NotifyFallback();
    }

    public async Task SkipPreviousAsync()
    {
        if (_currentSession != null)
        {
            try
            {
                await _currentSession.TrySkipPreviousAsync();
                await UpdateFromSessionAsync();
                return;
            }
            catch { }
        }

        _demoTrackIndex = (_demoTrackIndex - 1 + _demoPlaylist.Length) % _demoPlaylist.Length;
        _demoPosition = 0;
        NotifyFallback();
    }

    public async Task SeekAsync(double seconds)
    {
        var targetSec = Math.Clamp(seconds, 0.0, CurrentState.TotalSeconds);
        if (_currentSession != null)
        {
            try
            {
                var ticks = (long)(targetSec * 10_000_000);
                await _currentSession.TryChangePlaybackPositionAsync(ticks);
                CurrentState = CurrentState with { CurrentSeconds = targetSec };
                StateUpdated?.Invoke(CurrentState);
                return;
            }
            catch { }
        }

        _demoPosition = targetSec;
        CurrentState = CurrentState with { CurrentSeconds = targetSec };
        StateUpdated?.Invoke(CurrentState);
    }

    public void Dispose()
    {
        _smoothTimer.Stop();
        if (_sessionManager != null)
        {
            _sessionManager.CurrentSessionChanged -= SessionManager_CurrentSessionChanged;
        }
        if (_currentSession != null)
        {
            _currentSession.MediaPropertiesChanged -= CurrentSession_MediaPropertiesChanged;
            _currentSession.PlaybackInfoChanged -= CurrentSession_PlaybackInfoChanged;
            _currentSession.TimelinePropertiesChanged -= CurrentSession_TimelinePropertiesChanged;
        }
    }
}
