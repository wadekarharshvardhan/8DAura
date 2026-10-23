using Windows.Media.Control;

namespace EightDAura;

internal sealed class MediaSessionService
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private string _title = string.Empty;
    private string _artist = string.Empty;
    private DateTime _lastMetadataReadUtc;

    public async Task InitializeAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        }
        catch
        {
            _manager = null;
        }
    }

    public async Task<MediaSessionState> RefreshAsync()
    {
        if (_manager is null)
            return MediaSessionState.Unavailable;

        try
        {
            GlobalSystemMediaTransportControlsSession? active = _manager.GetCurrentSession();
            if (!ReferenceEquals(active, _session))
            {
                _session = active;
                _title = string.Empty;
                _artist = string.Empty;
                _lastMetadataReadUtc = DateTime.MinValue;
            }

            if (_session is null)
                return MediaSessionState.Unavailable;

            if ((DateTime.UtcNow - _lastMetadataReadUtc).TotalSeconds >= 2)
            {
                _lastMetadataReadUtc = DateTime.UtcNow;
                var properties = await _session.TryGetMediaPropertiesAsync();
                _title = properties?.Title ?? string.Empty;
                _artist = properties?.Artist ?? string.Empty;
            }

            var playback = _session.GetPlaybackInfo();
            var controls = playback?.Controls;
            bool playing = playback?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            bool toggleSupported = controls is not null &&
                (controls.IsPlayPauseToggleEnabled || (playing ? controls.IsPauseEnabled : controls.IsPlayEnabled));
            return new MediaSessionState(
                HasSession: true,
                Title: _title,
                Artist: _artist,
                Source: _session.SourceAppUserModelId,
                IsPlaying: playing,
                CanToggle: toggleSupported,
                CanNext: controls?.IsNextEnabled == true,
                CanPrevious: controls?.IsPreviousEnabled == true);
        }
        catch
        {
            return MediaSessionState.Unavailable;
        }
    }

    public async Task<bool> TogglePlayPauseAsync()
    {
        try { return _session is not null && await _session.TryTogglePlayPauseAsync(); }
        catch { return false; }
    }

    public async Task<bool> SkipNextAsync()
    {
        try { return _session is not null && await _session.TrySkipNextAsync(); }
        catch { return false; }
    }

    public async Task<bool> SkipPreviousAsync()
    {
        try { return _session is not null && await _session.TrySkipPreviousAsync(); }
        catch { return false; }
    }

    public sealed record MediaSessionState(
        bool HasSession,
        string Title,
        string Artist,
        string Source,
        bool IsPlaying,
        bool CanToggle,
        bool CanNext,
        bool CanPrevious)
    {
        public static MediaSessionState Unavailable { get; } = new(false, "", "", "", false, false, false, false);
    }
}
