using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace EightDAura;

public partial class MainWindow : Window
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private bool _devicesLoaded;
    private bool _isResetting;
    private string? _defaultInputDeviceId;
    private string? _defaultOutputDeviceId;
    private WasapiLoopbackCapture? _capture;
    private WasapiOut? _output;
    private BufferedWaveProvider? _buffer;
    private MMDevice? _inputDevice;
    private MMDevice? _outputDevice;
    private readonly DispatcherTimer _visualTimer;
    private readonly DispatcherTimer _mediaTimer;
    private readonly MediaSessionService _mediaSessionService = new();
    private bool _mediaRefreshInProgress;
    private double _phase;
    private double _visualPhase;
    private double _visualRadius = 1.0;
    private double _targetPhase;
    private double _targetRadius = 1.0;
    private double _phaseOffset;
    private double _smoothedPhaseOffset;
    private int _movementPattern;
    private bool _isDraggingOrbit;
    private float _lastPeak;
    private string _lastPositionLabel = string.Empty;
    // UI-only drawing state. The visualizer consumes only the existing scalar peak meter.
    private readonly List<Rectangle> _levelBars = [];
    private readonly List<Line> _levelWaveSegments = [];
    private double _visualizerTime;
    private double _visualEnergy;
    private double _lastDisplayedDb = double.NaN;
    private double _lastLevelBarValue = double.NaN;
    private double _levelVisualizerWidth = -1;
    private readonly float[] _itdHistoryLeft = new float[256], _itdHistoryRight = new float[256];
    private int _itdWriteIndex;
    private readonly float[] _hrtfFarAlpha = new float[OrbitTableSize], _hrtfFarGain = new float[OrbitTableSize];
    private float _headShadowLeft, _headShadowRight;
    private double _currentRearCue;
    private EqCoefficients _hrtfRearNotch = EqCoefficients.Identity;
    private EqCoefficients _hrtfElevationUp = EqCoefficients.Identity, _hrtfElevationDown = EqCoefficients.Identity;
    private BiquadState _rearNotchLeft, _rearNotchRight, _elevationLeft, _elevationRight;
    private double _speed = 0.22;
    private double _volume = 0.85;
    private double _smoothedVolume = 0.85;
    private double _targetSpatialMix = 0.65, _currentSpatialMix = 0.65;
    private double _targetStereoWidth = 1.0, _currentStereoWidth = 1.0;
    private double _targetRearTone = 0.08, _currentRearTone = 0.08;
    private double _targetHaasMs = 7.0, _currentHaasMs = 7.0;
    private double _targetHaasLevel = 0.16, _currentHaasLevel = 0.16;
    private double _targetPresetGain = 1.0, _currentPresetGain = 1.0;
    private double _targetEchoMix, _currentEchoMix;
    private double _targetEchoDelayMs = 120, _currentEchoDelayMs = 120;
    private double _targetEchoFeedback = 0.14, _currentEchoFeedback = 0.14;
    private double _targetReverbMix, _currentReverbMix;
    private double _targetReverbDecay = 1.2, _targetReverbDamping = 0.35;
    private double _currentReverbDecay = 1.2, _currentReverbDamping = 0.35;
    private float[] _echoLeft = Array.Empty<float>(), _echoRight = Array.Empty<float>();
    private int _echoWriteIndex;
    private StereoRoomReverb? _roomReverb;
    static MainWindow()
    {
        for (int i = 0; i < OrbitTableSize; i++)
        {
            double phase = i * Math.Tau / OrbitTableSize;
            double pan = Math.Sin(phase);
            OrbitPan[i] = (float)pan;
            OrbitFront[i] = (float)Math.Cos(phase);
            // Near-ear level stays close to unity; the far ear is gently shadowed by the head.
            OrbitLeftGain[i] = (float)(pan > 0 ? 1.0 - 0.25 * pan : 1.0);
            OrbitRightGain[i] = (float)(pan < 0 ? 1.0 + 0.25 * pan : 1.0);
        }
    }

    private static readonly AudioPreset[] Presets = [
        new("360 Surround", "Strong, continuous headphone movement with a clear but controlled Haas cue.", 0.94, 1.05, 0.15, 0.96, 7.0, 0.20, 0.14),
        new("Studio Natural", "Balanced movement with moderate Haas space and natural source width.", 0.72, 1.00, 0.10, 0.98, 5.5, 0.10, 0.12),
        new("Wide Atmosphere", "Broad stereo sides and a stronger delayed far-channel cue.", 0.86, 1.16, 0.14, 0.94, 7.5, 0.16, 0.16),
        new("Deep Orbit", "Strong rear movement with restrained low-level spatial depth.", 0.90, 1.08, 0.19, 0.92, 8.5, 0.22, 0.20),
        new("Vocal Clarity", "Gentler motion and a restrained delay to keep centered voices stable.", 0.58, 0.92, 0.08, 0.99, 4.0, 0.07, 0.09)
    ];
    private static readonly SoundSpacePreset[] SoundSpaces = [
        new("Headphones", "Clear and close; room effects stay very subtle.", 0.00, 0.025, 115, 0.08, 0.55, 0.58),
        new("Speakers", "A small-room feel with short reflections and a short decay.", 0.045, 0.09, 88, 0.10, 0.72, 0.50),
        new("Hall", "A wide, smooth room tail with restrained early echo.", 0.10, 0.22, 125, 0.16, 1.85, 0.38),
        new("Auditorium", "Longer, spacious reflections with a controlled hall tail.", 0.13, 0.30, 175, 0.22, 2.65, 0.32)
    ];
    private static readonly MovementPattern[] MovementPatterns = [
        new("Clockwise orbit", "Smooth full 360° movement, clockwise."),
        new("Counter-clockwise orbit", "Smooth full 360° movement in reverse."),
        new("Front arc", "Sound sweeps between the front-left and front-right."),
        new("Rear arc", "Sound sweeps behind you from rear-left to rear-right."),
        new("Horizontal sweep", "A broad side-to-side sweep across the horizontal sound field."),
        new("Figure eight", "Two smooth crossing loops that pass in front of the listener."),
        new("Star path", "Travels around a five-point star with gentle in-and-out distance movement."),
        new("Parallelogram", "Moves around a slanted four-sided path."),
        new("Rhombus", "Moves around a diamond path, passing front, right, rear and left."),
        new("Octagon", "A smooth eight-sided orbit with gentle corner turns."),
        new("Pentagon", "A five-sided orbit around the listening field.")
    ];
    private static readonly PositionPoint[][] PolygonPaths = [
        CreateStarPath(), CreateParallelogramPath(), CreateRhombusPath(), CreateRegularPolygon(8), CreateRegularPolygon(5), CreateFigureEightPath(128)
    ];
    private volatile bool _running;
    private float[] _decoded = Array.Empty<float>();
    private byte[] _rendered = Array.Empty<byte>();
    private float _filterLeft, _filterRight;
    private const int EqBandCount = 10;
    private static readonly double[] EqFrequencies = [31, 62, 125, 250, 500, 1000, 2000, 4000, 8000, 16000];
    private readonly BiquadState[] _eqLeft = new BiquadState[EqBandCount + 3];
    private readonly BiquadState[] _eqRight = new BiquadState[EqBandCount + 3];
    private readonly EqCoefficients[] _currentEq = Enumerable.Repeat(EqCoefficients.Identity, EqBandCount + 3).ToArray();
    private EqTarget _eqTarget = new(Enumerable.Repeat(EqCoefficients.Identity, EqBandCount + 3).ToArray(), 1f, true);
    private double _eqSampleRate = 48000;
    private double _currentPreamp = 1.0;
    private Slider[] _basicEqSliders = []; 
    private Slider[] _advancedEqSliders = [];
    private TextBlock[] _basicEqLabels = [];
    private TextBlock[] _advancedEqLabels = [];
    private readonly float[] _haasHistory = new float[8192];
    private int _haasWriteIndex;
    private const int OrbitTableSize = 4096;
    private static readonly float[] OrbitPan = new float[OrbitTableSize];
    private static readonly float[] OrbitFront = new float[OrbitTableSize];
    private static readonly float[] OrbitLeftGain = new float[OrbitTableSize];
    private static readonly float[] OrbitRightGain = new float[OrbitTableSize];

public MainWindow()
    {
        InitializeComponent();
        InitializeLevelVisualizer();
        InitializeEqualiserControls();
        PresetCombo.ItemsSource = Presets;
        SoundSpaceCombo.ItemsSource = SoundSpaces;
        MovementCombo.ItemsSource = MovementPatterns;
        SpeedSlider.ValueChanged += (_, _) => { Volatile.Write(ref _speed, SpeedSlider.Value); SpeedText.Text = $"{SpeedSlider.Value:0.00} Hz"; MarkSettingsChanged(); };
        VolumeSlider.ValueChanged += (_, _) => { Volatile.Write(ref _volume, VolumeSlider.Value); VolumeText.Text = $"{VolumeSlider.Value:P0}"; MarkSettingsChanged(); };
        BlendSlider.ValueChanged += (_, _) => { Volatile.Write(ref _targetSpatialMix, BlendSlider.Value); BlendText.Text = $"{BlendSlider.Value:P0}"; MarkSettingsChanged(); };
        WidthSlider.ValueChanged += (_, _) => { Volatile.Write(ref _targetStereoWidth, WidthSlider.Value); WidthText.Text = $"{WidthSlider.Value:P0}"; MarkSettingsChanged(); };
        HaasDelaySlider.ValueChanged += (_, _) => { Volatile.Write(ref _targetHaasMs, HaasDelaySlider.Value); HaasDelayText.Text = $"{HaasDelaySlider.Value:0.0} ms"; MarkSettingsChanged(); };
        HaasLevelSlider.ValueChanged += (_, _) => { Volatile.Write(ref _targetHaasLevel, HaasLevelSlider.Value); HaasLevelText.Text = $"{HaasLevelSlider.Value:P0}"; MarkSettingsChanged(); };
        EchoSlider.ValueChanged += (_, _) => { Volatile.Write(ref _targetEchoMix, EchoSlider.Value); EchoText.Text = $"{EchoSlider.Value:P0}"; MarkSettingsChanged(); };
        ReverbSlider.ValueChanged += (_, _) => { Volatile.Write(ref _targetReverbMix, ReverbSlider.Value); ReverbText.Text = $"{ReverbSlider.Value:P0}"; MarkSettingsChanged(); };
        SoundSpaceCombo.SelectionChanged += (_, _) => { ApplySelectedSoundSpace(); MarkSettingsChanged(); };
        PresetCombo.SelectionChanged += (_, _) => { ApplySelectedPreset(); MarkSettingsChanged(); };
        MovementCombo.SelectionChanged += (_, _) =>
        {
            int pattern = Math.Max(0, MovementCombo.SelectedIndex);
            Volatile.Write(ref _movementPattern, pattern);
            GetMovementPosition(_phase + _smoothedPhaseOffset, pattern, out double angle, out double radius);
            Volatile.Write(ref _targetPhase, angle);
            Volatile.Write(ref _targetRadius, radius);
            MarkSettingsChanged();
        };
        InputDeviceCombo.SelectionChanged += (_, _) => MarkSettingsChanged();
        OutputDeviceCombo.SelectionChanged += (_, _) => MarkSettingsChanged();

        _isResetting = true;
        PresetCombo.SelectedIndex = 0;
        SoundSpaceCombo.SelectedIndex = 0;
        MovementCombo.SelectedIndex = 0;
        _isResetting = false;
        InputDeviceCombo.IsEnabled = false;
        OutputDeviceCombo.IsEnabled = false;
        _ = LoadDevicesAsync();
        _visualTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _visualTimer.Tick += (_, _) => UpdateVisualizer();
        _visualTimer.Start();
        _mediaTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _mediaTimer.Tick += async (_, _) => await RefreshMediaSessionAsync();
        _mediaTimer.Start();
        _ = InitializeMediaSessionAsync();
    }

    private async Task InitializeMediaSessionAsync()
    {
        await _mediaSessionService.InitializeAsync();
        await RefreshMediaSessionAsync();
    }

    private async Task RefreshMediaSessionAsync()
    {
        if (_mediaRefreshInProgress) return;
        _mediaRefreshInProgress = true;
        try
        {
            MediaSessionService.MediaSessionState state = await _mediaSessionService.RefreshAsync();
            if (!state.HasSession)
            {
                TrackTitleText.Text = "No supported media session";
                TrackArtistText.Text = "Start playback in YouTube Music or another media app";
                MediaSourceText.Text = string.Empty;
                MediaSourceBorder.Visibility = Visibility.Collapsed;
                PreviousTrackButton.IsEnabled = PlayPauseButton.IsEnabled = NextTrackButton.IsEnabled = false;
                SetPlayPauseGlyph("▶");
                return;
            }

            TrackTitleText.Text = string.IsNullOrWhiteSpace(state.Title) ? "Unknown track" : state.Title;
            TrackArtistText.Text = state.Artist;
            MediaSourceText.Text = FormatMediaSource(state.Source);
            MediaSourceBorder.Visibility = Visibility.Visible;
            PreviousTrackButton.IsEnabled = state.CanPrevious;
            NextTrackButton.IsEnabled = state.CanNext;
            PlayPauseButton.IsEnabled = state.CanToggle;
            SetPlayPauseGlyph(state.IsPlaying ? "⏸" : "▶");
            PlayPauseButton.ToolTip = state.IsPlaying ? "Pause" : "Play";
        }
        finally
        {
            _mediaRefreshInProgress = false;
        }
    }

    private static string FormatMediaSource(string source)
    {
        if (source.Contains("firefox", StringComparison.OrdinalIgnoreCase)) return "Firefox";
        if (source.Contains("chrome", StringComparison.OrdinalIgnoreCase)) return "Chrome";
        if (source.Contains("msedge", StringComparison.OrdinalIgnoreCase) || source.Contains("edge", StringComparison.OrdinalIgnoreCase)) return "Microsoft Edge";
        if (source.Contains("spotify", StringComparison.OrdinalIgnoreCase)) return "Spotify";
        if (source.Contains("vlc", StringComparison.OrdinalIgnoreCase)) return "VLC media player";
        if (source.Contains("foobar", StringComparison.OrdinalIgnoreCase)) return "foobar2000";
        return "Windows media";
    }

    private void SetPlayPauseGlyph(string glyph)
    {
        if (PlayPauseButton.Content is TextBlock current && current.Text == glyph) return;
        PlayPauseButton.Content = new TextBlock
        {
            Text = glyph,
            FontFamily = new System.Windows.Media.FontFamily("Segoe UI Symbol"),
            FontSize = glyph == "▶" ? 16 : 17,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    private async void PlayPauseButton_Click(object sender, RoutedEventArgs e)
    {
        await _mediaSessionService.TogglePlayPauseAsync();
        await RefreshMediaSessionAsync();
    }

    private async void NextTrackButton_Click(object sender, RoutedEventArgs e)
    {
        await _mediaSessionService.SkipNextAsync();
        await RefreshMediaSessionAsync();
    }

    private async void PreviousTrackButton_Click(object sender, RoutedEventArgs e)
    {
        await _mediaSessionService.SkipPreviousAsync();
        await RefreshMediaSessionAsync();
    }

    private void InitializeEqualiserControls()
    {
        _basicEqSliders = [BassEqSlider, MidEqSlider, TrebleEqSlider];
        _basicEqLabels = [BassEqText, MidEqText, TrebleEqText];
        for (int i = 0; i < _basicEqSliders.Length; i++)
        {
            int index = i;
            _basicEqSliders[i].ValueChanged += (_, _) => { _basicEqLabels[index].Text = FormatDb(_basicEqSliders[index].Value); RefreshEqualiserTarget(); MarkSettingsChanged(); };
        }
        _advancedEqSliders = new Slider[EqBandCount];
        _advancedEqLabels = new TextBlock[EqBandCount];
        for (int i = 0; i < EqBandCount; i++)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 12, 10) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(78) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) });
            var card = new Border { Background = new SolidColorBrush(Color.FromRgb(21, 28, 39)), BorderBrush = new SolidColorBrush(Color.FromRgb(38, 52, 73)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(12), Child = row };
            var frequency = new TextBlock { Text = EqFrequencies[i] >= 1000 ? $"{EqFrequencies[i]/1000:0.#} kHz" : $"{EqFrequencies[i]:0} Hz", VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold };
            var slider = new Slider { Minimum = -12, Maximum = 12, Value = 0, TickFrequency = 1, IsMoveToPointEnabled = true, Margin = new Thickness(4, 0, 8, 0), MinHeight = 42 };
            var label = new TextBlock { Text = "0.0 dB", VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, Foreground = new SolidColorBrush(Color.FromRgb(114, 182, 255)) };
            Grid.SetColumn(slider, 1); Grid.SetColumn(label, 2); row.Children.Add(frequency); row.Children.Add(slider); row.Children.Add(label);
            AdvancedBandsPanel.Children.Add(card); _advancedEqSliders[i] = slider; _advancedEqLabels[i] = label;
            int band = i;
            slider.ValueChanged += (_, _) => { _advancedEqLabels[band].Text = FormatDb(_advancedEqSliders[band].Value); RefreshEqualiserTarget(); MarkSettingsChanged(); };
        }
        PreampSlider.ValueChanged += (_, _) => { PreampText.Text = FormatDb(PreampSlider.Value); RefreshEqualiserTarget(); MarkSettingsChanged(); };
        EqEnabledCheckBox.Checked += (_, _) => { RefreshEqualiserTarget(); MarkSettingsChanged(); };
        EqEnabledCheckBox.Unchecked += (_, _) => { RefreshEqualiserTarget(); MarkSettingsChanged(); };
        RefreshEqualiserTarget();
    }

    private static string FormatDb(double value) => value > 0.05 ? $"+{value:0.0} dB" : value < -0.05 ? $"{value:0.0} dB" : "0.0 dB";

    private void RefreshEqualiserTarget()
    {
        double sr = Math.Max(8000, _eqSampleRate);
        var bands = new EqCoefficients[EqBandCount + 3];
        bands[0] = MakeShelf(110, 0.707, BassEqSlider.Value, sr, true);
        bands[1] = MakePeak(1000, 0.9, MidEqSlider.Value, sr);
        bands[2] = MakeShelf(8000, 0.707, TrebleEqSlider.Value, sr, false);
        for (int i = 0; i < EqBandCount; i++) bands[i + 3] = MakePeak(EqFrequencies[i], 0.9, _advancedEqSliders[i]?.Value ?? 0, sr);
        Volatile.Write(ref _eqTarget, new EqTarget(bands, (float)Math.Pow(10, PreampSlider.Value / 20), EqEnabledCheckBox.IsChecked == true));
    }

    private static EqCoefficients MakePeak(double f, double q, double db, double sr)
    {
        if (Math.Abs(db) < 0.0001) return EqCoefficients.Identity;
        double w=2*Math.PI*Math.Clamp(f,20,sr*0.45)/sr, c=Math.Cos(w), a=Math.Sin(w)/(2*q), A=Math.Pow(10,db/40), a0=1+a/A;
        return new((float)((1+a*A)/a0),(float)(-2*c/a0),(float)((1-a*A)/a0),(float)(-2*c/a0),(float)((1-a/A)/a0));
    }
    private static EqCoefficients MakeShelf(double f, double q, double db, double sr, bool low)
    {
        if (Math.Abs(db) < 0.0001) return EqCoefficients.Identity;
        double w=2*Math.PI*Math.Clamp(f,20,sr*0.45)/sr, c=Math.Cos(w), sn=Math.Sin(w), A=Math.Pow(10,db/40), alpha=sn/(2*q), beta=2*Math.Sqrt(A)*alpha;
        double b0,b1,b2,a0,a1,a2;
        if (low) { b0=A*((A+1)-(A-1)*c+beta); b1=2*A*((A-1)-(A+1)*c); b2=A*((A+1)-(A-1)*c-beta); a0=(A+1)+(A-1)*c+beta; a1=-2*((A-1)+(A+1)*c); a2=(A+1)+(A-1)*c-beta; }
        else { b0=A*((A+1)+(A-1)*c+beta); b1=-2*A*((A-1)+(A+1)*c); b2=A*((A+1)+(A-1)*c-beta); a0=(A+1)-(A-1)*c+beta; a1=2*((A-1)-(A+1)*c); a2=(A+1)-(A-1)*c-beta; }
        return new((float)(b0/a0),(float)(b1/a0),(float)(b2/a0),(float)(a1/a0),(float)(a2/a0));
    }

    private void EqResetButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var slider in _basicEqSliders) slider.Value = 0;
        foreach (var slider in _advancedEqSliders) slider.Value = 0;
        PreampSlider.Value = 0; EqEnabledCheckBox.IsChecked = true;
    }

    private void ApplySelectedSoundSpace()
    {
        if (SoundSpaceCombo.SelectedItem is not SoundSpacePreset space) return;
        SoundSpaceDescription.Text = space.Description;
        EchoSlider.Value = space.EchoMix;
        ReverbSlider.Value = space.ReverbMix;
        Volatile.Write(ref _targetEchoDelayMs, space.EchoDelayMs);
        Volatile.Write(ref _targetEchoFeedback, space.EchoFeedback);
        Volatile.Write(ref _targetReverbDecay, space.ReverbDecaySeconds);
        Volatile.Write(ref _targetReverbDamping, space.Damping);
    }

    private void ApplySelectedPreset()
    {
        if (PresetCombo.SelectedItem is not AudioPreset preset) return;
        PresetDescription.Text = preset.Description;
        SpeedSlider.Value = preset.Speed;
        BlendSlider.Value = preset.Blend;
        WidthSlider.Value = preset.Width;
        HaasDelaySlider.Value = preset.HaasMs;
        HaasLevelSlider.Value = preset.HaasLevel;
        Volatile.Write(ref _targetRearTone, preset.RearTone);
        Volatile.Write(ref _targetPresetGain, preset.Headroom);
    }

    private readonly record struct EqCoefficients(float B0, float B1, float B2, float A1, float A2)
    {
        public static EqCoefficients Identity => new(1, 0, 0, 0, 0);
        public EqCoefficients Lerp(EqCoefficients t, double a) => new((float)(B0+(t.B0-B0)*a),(float)(B1+(t.B1-B1)*a),(float)(B2+(t.B2-B2)*a),(float)(A1+(t.A1-A1)*a),(float)(A2+(t.A2-A2)*a));
    }
    private sealed record EqTarget(EqCoefficients[] Bands, float Preamp, bool Enabled);
    private struct BiquadState
    {
        private float _z1, _z2;
        public float Process(float x, EqCoefficients c) { float y=c.B0*x+_z1; _z1=c.B1*x-c.A1*y+_z2; _z2=c.B2*x-c.A2*y; return y; }
    }

    private sealed record AudioPreset(string Name, string Description, double Blend, double Width, double RearTone, double Headroom, double HaasMs, double HaasLevel, double Speed);
    private sealed record SoundSpacePreset(string Name, string Description, double EchoMix, double ReverbMix, double EchoDelayMs, double EchoFeedback, double ReverbDecaySeconds, double Damping);
    private sealed record MovementPattern(string Name, string Description);
    private readonly record struct PositionPoint(double X, double Z);

    private static PositionPoint[] CreateRegularPolygon(int sides)
    {
        var points = new PositionPoint[sides];
        for (int i = 0; i < sides; i++)
        {
            double angle = Math.Tau * i / sides;
            points[i] = new PositionPoint(Math.Sin(angle), Math.Cos(angle));
        }
        return points;
    }

    private static PositionPoint[] CreateStarPath()
    {
        var points = new PositionPoint[10];
        for (int i = 0; i < points.Length; i++)
        {
            double angle = Math.Tau * i / points.Length;
            double radius = i % 2 == 0 ? 1.0 : 0.40;
            points[i] = new PositionPoint(Math.Sin(angle) * radius, Math.Cos(angle) * radius);
        }
        return points;
    }

    private static PositionPoint[] CreateParallelogramPath() => [
        new(-0.58, 0.86), new(0.88, 0.86), new(0.58, -0.86), new(-0.88, -0.86)
    ];

    private static PositionPoint[] CreateRhombusPath() => [
        new(0, 1), new(0.82, 0), new(0, -1), new(-0.82, 0)
    ];

    private static PositionPoint[] CreateFigureEightPath(int samples)
    {
        var points = new PositionPoint[samples];
        for (int i = 0; i < samples; i++)
        {
            double phase = Math.Tau * i / samples;
            points[i] = new PositionPoint(0.82 * Math.Sin(phase), 0.22 + 0.78 * Math.Cos(2 * phase));
        }
        return points;
    }

    private static void GetMovementPosition(double phase, int pattern, out double angle, out double radius)
    {
        if (pattern < 5)
        {
            angle = pattern switch
            {
                1 => -phase,
                2 => Math.Sin(phase) * Math.PI / 2,
                3 => Math.PI - Math.Sin(phase) * Math.PI / 2,
                4 => Math.Sin(phase) * Math.PI,
                _ => phase
            };
            radius = 1.0;
            return;
        }

        int pathIndex = pattern == 5 ? PolygonPaths.Length - 1 : pattern - 6;
        PositionPoint[] path = PolygonPaths[Math.Clamp(pathIndex, 0, PolygonPaths.Length - 1)];
        double normalizedPhase = ((phase % Math.Tau) + Math.Tau) % Math.Tau / Math.Tau;
        double pathPosition = normalizedPhase * path.Length;
        int segment = (int)pathPosition;
        double blend = pathPosition - segment;
        PositionPoint start = path[segment % path.Length];
        PositionPoint end = path[(segment + 1) % path.Length];
        double x = start.X + (end.X - start.X) * blend;
        double z = start.Z + (end.Z - start.Z) * blend;
        radius = Math.Clamp(Math.Sqrt(x * x + z * z), 0.12, 1.0);
        angle = Math.Atan2(x, z);
    }

    private async Task LoadDevicesAsync()
    {
        try
        {
            // Only immutable strings cross threads. WASAPI COM device objects are reacquired
            // on the WPF UI apartment when the user starts audio routing.
            List<AudioDeviceOption> devices = await Task.Run(() =>
            {
                using var enumerator = new MMDeviceEnumerator();
                return enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                    .Select(device => new AudioDeviceOption(device.ID, device.FriendlyName))
                    .ToList();
            });

            bool wasResetting = _isResetting;
            _isResetting = true;
            InputDeviceCombo.ItemsSource = devices;
            OutputDeviceCombo.ItemsSource = devices;
            var cable = devices.FirstOrDefault(d => d.FriendlyName.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase) || d.FriendlyName.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase));
            InputDeviceCombo.SelectedItem = cable ?? devices.FirstOrDefault();
            OutputDeviceCombo.SelectedItem = devices.FirstOrDefault(d => d.Id != cable?.Id && (d.FriendlyName.Contains("headphone", StringComparison.OrdinalIgnoreCase) || d.FriendlyName.Contains("earbud", StringComparison.OrdinalIgnoreCase) || d.FriendlyName.Contains("speaker", StringComparison.OrdinalIgnoreCase))) ?? devices.LastOrDefault();
            _defaultInputDeviceId = (InputDeviceCombo.SelectedItem as AudioDeviceOption)?.Id;
            _defaultOutputDeviceId = (OutputDeviceCombo.SelectedItem as AudioDeviceOption)?.Id;
            _devicesLoaded = true;
            InputDeviceCombo.IsEnabled = true;
            OutputDeviceCombo.IsEnabled = true;
            _isResetting = wasResetting;
        }
        catch (Exception ex)
        {
            InputDeviceCombo.IsEnabled = true;
            OutputDeviceCombo.IsEnabled = true;
            MessageBox.Show($"Could not enumerate audio devices.\n{ex.Message}", "8D Aura", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private sealed record AudioDeviceOption(string Id, string FriendlyName);

    private void MarkSettingsChanged()
    {
        if (_isResetting) return;
        ResetSettingsButton.Visibility = Visibility.Visible;
    }

    private void ResetSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        _isResetting = true;
        PresetCombo.SelectedIndex = 0;
        MovementCombo.SelectedIndex = 0;
        SpeedSlider.Value = 0.14;
        VolumeSlider.Value = 0.85;
        BlendSlider.Value = 0.94;
        WidthSlider.Value = 1.05;
        HaasDelaySlider.Value = 8.0;
        HaasLevelSlider.Value = 0.20;
        SoundSpaceCombo.SelectedIndex = 0;
        EchoSlider.Value = 0.0;
        ReverbSlider.Value = 0.025;
        if (_devicesLoaded)
        {
            InputDeviceCombo.SelectedItem = InputDeviceCombo.Items.Cast<AudioDeviceOption>().FirstOrDefault(d => d.Id == _defaultInputDeviceId);
            OutputDeviceCombo.SelectedItem = OutputDeviceCombo.Items.Cast<AudioDeviceOption>().FirstOrDefault(d => d.Id == _defaultOutputDeviceId);
        }
        foreach (var slider in _basicEqSliders) slider.Value = 0;
        foreach (var slider in _advancedEqSliders) slider.Value = 0;
        PreampSlider.Value = 0;
        EqEnabledCheckBox.IsChecked = true;
        Volatile.Write(ref _phase, 0);
        Volatile.Write(ref _phaseOffset, 0);
        _smoothedPhaseOffset = 0;
        _visualPhase = 0;
        Volatile.Write(ref _targetRadius, 1.0);
        Volatile.Write(ref _targetPhase, 0);
        RefreshEqualiserTarget();
        _isResetting = false;
        ResetSettingsButton.Visibility = Visibility.Collapsed;
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (_running) return;
        if (InputDeviceCombo.SelectedItem is not AudioDeviceOption inputOption || OutputDeviceCombo.SelectedItem is not AudioDeviceOption outputOption) { MessageBox.Show("Select a capture endpoint and an output device first.", "8D Aura", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        if (inputOption.Id == outputOption.Id) { MessageBox.Show("Capture and output devices must be different to prevent audio feedback. Select the virtual cable as input and your headphones as output.", "Safe routing", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        StartButton.IsEnabled = false;
        try
        {
            _inputDevice = _enumerator.GetDevice(inputOption.Id);
            _outputDevice = _enumerator.GetDevice(outputOption.Id);
            _capture = new WasapiLoopbackCapture(_inputDevice);
            _eqSampleRate = _capture.WaveFormat.SampleRate;
            int echoCapacity = 1;
            while (echoCapacity < _eqSampleRate * 0.5 + 4) echoCapacity <<= 1;
            _echoLeft = new float[echoCapacity];
            _echoRight = new float[echoCapacity];
            _echoWriteIndex = 0;
            _roomReverb = new StereoRoomReverb(_eqSampleRate);
            ConfigureHrtfTables(_eqSampleRate);
            Array.Clear(_itdHistoryLeft); Array.Clear(_itdHistoryRight);
            _itdWriteIndex = 0; _headShadowLeft = _headShadowRight = 0;
            _rearNotchLeft = _rearNotchRight = default;
            _elevationLeft = _elevationRight = default;
            RefreshEqualiserTarget();
            var outputFormat = WaveFormat.CreateIeeeFloatWaveFormat(_capture.WaveFormat.SampleRate, 2);
            Array.Clear(_haasHistory);
            _haasWriteIndex = 0;
            _buffer = new BufferedWaveProvider(outputFormat) { BufferDuration = TimeSpan.FromMilliseconds(500), DiscardOnBufferOverflow = true, ReadFully = true };
            _output = new WasapiOut(_outputDevice, AudioClientShareMode.Shared, true, 40);
            _output.Init(_buffer);
            _capture.DataAvailable += Capture_DataAvailable;
            _capture.RecordingStopped += Capture_RecordingStopped;
            _running = true;
            _output.Play(); _capture.StartRecording();
            StatusText.Text = "ENGINE RUNNING"; StatusDot.Fill = new SolidColorBrush(Color.FromRgb(76, 220, 157));
            StartButton.Content = "Engine running"; StopButton.IsEnabled = true;
        }
        catch (Exception ex) { await StopEngineAsync(); MessageBox.Show("Could not start audio processing. Check the virtual cable and device formats.\n\n" + ex.Message, "8D Aura", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { StartButton.IsEnabled = !_running; }
    }

private void Capture_DataAvailable(object? sender, WaveInEventArgs e)
    {
        var capture = _capture;
        var buffer = _buffer;
        if (!_running || capture is null || buffer is null || e.BytesRecorded <= 0) return;
        try
        {
            WaveFormat format = capture.WaveFormat;
            int channels = Math.Max(1, format.Channels);
            int bytesPerSample = format.BitsPerSample / 8;
            int samples = e.BytesRecorded / bytesPerSample;
            int frames = samples / channels;
            if (frames <= 0) return;
            if (_decoded.Length < samples) _decoded = new float[NextCapacity(samples)];
            if (_rendered.Length < frames * 8) _rendered = new byte[NextCapacity(frames * 8)];
            DecodeInto(e.Buffer, samples, format, _decoded);

            double rate = format.SampleRate;
            double speed = Volatile.Read(ref _speed);
            int movementPattern = Volatile.Read(ref _movementPattern);
            double targetVolume = Volatile.Read(ref _volume);
            EqTarget eqTarget = Volatile.Read(ref _eqTarget);
            double targetPhaseOffset = Volatile.Read(ref _phaseOffset);
            double targetMix = Volatile.Read(ref _targetSpatialMix);
            double targetWidth = Volatile.Read(ref _targetStereoWidth);
            double targetRearTone = Volatile.Read(ref _targetRearTone);
            double targetHaasMs = Volatile.Read(ref _targetHaasMs);
            double targetHaasLevel = Volatile.Read(ref _targetHaasLevel);
            double targetPresetGain = Volatile.Read(ref _targetPresetGain);
            double targetEchoMix = Volatile.Read(ref _targetEchoMix);
            double targetEchoDelayMs = Volatile.Read(ref _targetEchoDelayMs);
            double targetEchoFeedback = Volatile.Read(ref _targetEchoFeedback);
            double targetReverbMix = Volatile.Read(ref _targetReverbMix);
            double targetReverbDecay = Volatile.Read(ref _targetReverbDecay);
            double targetReverbDamping = Volatile.Read(ref _targetReverbDamping);
            double smoothing = 1.0 - Math.Exp(-1.0 / (rate * 0.025));
            float peak = 0;
            _roomReverb?.SetParameters(_currentReverbDecay, _currentReverbDamping);

            for (int i = 0; i < frames; i++)
            {
                float leftIn = _decoded[i * channels];
                float rightIn = channels > 1 ? _decoded[i * channels + 1] : leftIn;
                float dryLeft = leftIn, dryRight = rightIn;
                _currentPreamp += (eqTarget.Preamp - _currentPreamp) * smoothing;
                float eqLeft = leftIn * (float)_currentPreamp, eqRight = rightIn * (float)_currentPreamp;
                for (int band = 0; band < _currentEq.Length; band++)
                {
                    EqCoefficients coefficients = _currentEq[band].Lerp(eqTarget.Bands[band], smoothing);
                    _currentEq[band] = coefficients;
                    eqLeft = _eqLeft[band].Process(eqLeft, coefficients);
                    eqRight = _eqRight[band].Process(eqRight, coefficients);
                }
                if (eqTarget.Enabled) { leftIn = eqLeft; rightIn = eqRight; }
                float mid = (leftIn + rightIn) * 0.5f;
                float side = (leftIn - rightIn) * 0.5f;
                double offsetDelta = Math.IEEERemainder(targetPhaseOffset - _smoothedPhaseOffset, Math.Tau);
                _smoothedPhaseOffset += offsetDelta * smoothing;
                GetMovementPosition(_phase + _smoothedPhaseOffset, movementPattern, out double movementAngle, out double movementRadius);
                double angle = movementAngle % Math.Tau;
                if (angle < 0) angle += Math.Tau;
                double tablePosition = angle * OrbitTableSize / Math.Tau;
                int tableIndex = (int)tablePosition & (OrbitTableSize - 1);
                int nextTableIndex = (tableIndex + 1) & (OrbitTableSize - 1);
                float tableBlend = (float)(tablePosition - Math.Floor(tablePosition));
                float pan = OrbitPan[tableIndex] + (OrbitPan[nextTableIndex] - OrbitPan[tableIndex]) * tableBlend;
                float frontBack = OrbitFront[tableIndex] + (OrbitFront[nextTableIndex] - OrbitFront[tableIndex]) * tableBlend;
                float leftPan = OrbitLeftGain[tableIndex] + (OrbitLeftGain[nextTableIndex] - OrbitLeftGain[tableIndex]) * tableBlend;
                float rightPan = OrbitRightGain[tableIndex] + (OrbitRightGain[nextTableIndex] - OrbitRightGain[tableIndex]) * tableBlend;
                float farAlpha = _hrtfFarAlpha[tableIndex] + (_hrtfFarAlpha[nextTableIndex] - _hrtfFarAlpha[tableIndex]) * tableBlend;
                float farGain = _hrtfFarGain[tableIndex] + (_hrtfFarGain[nextTableIndex] - _hrtfFarGain[tableIndex]) * tableBlend;
                float rear = (float)Math.Clamp((-frontBack + 0.15) * 0.85, 0, 1);
                _currentRearCue += (rear - _currentRearCue) * smoothing;
                _currentSpatialMix += (targetMix - _currentSpatialMix) * smoothing;
                _currentStereoWidth += (targetWidth - _currentStereoWidth) * smoothing;
                _currentRearTone += (targetRearTone - _currentRearTone) * smoothing;
                _currentHaasMs += (targetHaasMs - _currentHaasMs) * smoothing;
                _currentHaasLevel += (targetHaasLevel - _currentHaasLevel) * smoothing;
                _currentPresetGain += (targetPresetGain - _currentPresetGain) * smoothing;
                _currentEchoMix += (targetEchoMix - _currentEchoMix) * smoothing;
                _currentEchoDelayMs += (targetEchoDelayMs - _currentEchoDelayMs) * smoothing;
                _currentEchoFeedback += (targetEchoFeedback - _currentEchoFeedback) * smoothing;
                _currentReverbMix += (targetReverbMix - _currentReverbMix) * smoothing;
                _currentReverbDecay += (targetReverbDecay - _currentReverbDecay) * smoothing;
                _currentReverbDamping += (targetReverbDamping - _currentReverbDamping) * smoothing;
                _smoothedVolume += (targetVolume - _smoothedVolume) * smoothing;

_haasHistory[_haasWriteIndex] = mid;
                double delaySamples = Math.Clamp(_currentHaasMs * 0.001 * rate, 1.0, _haasHistory.Length - 2.0);
                int wholeDelay = (int)delaySamples;
                float delayFraction = (float)(delaySamples - wholeDelay);
                int historyMask = _haasHistory.Length - 1;
                int delayIndex = (_haasWriteIndex - wholeDelay) & historyMask;
                float delayedMid = _haasHistory[delayIndex] + (_haasHistory[(delayIndex - 1) & historyMask] - _haasHistory[delayIndex]) * delayFraction;
                _haasWriteIndex = (_haasWriteIndex + 1) & historyMask;
                float sideWidth = side * (float)_currentStereoWidth;
                float earL = mid * leftPan + sideWidth * 0.70710678f;
                float earR = mid * rightPan - sideWidth * 0.70710678f;
                _itdHistoryLeft[_itdWriteIndex] = earL;
                _itdHistoryRight[_itdWriteIndex] = earR;
                float itdSamples = (float)(0.00062 * Math.Abs(pan) * rate);
                float wetL = pan > 0 ? ReadItd(_itdHistoryLeft, earL, itdSamples) : earL;
                float wetR = pan < 0 ? ReadItd(_itdHistoryRight, earR, itdSamples) : earR;
                _itdWriteIndex = (_itdWriteIndex + 1) & 255;
                _headShadowLeft += farAlpha * (wetL - _headShadowLeft);
                _headShadowRight += farAlpha * (wetR - _headShadowRight);
                if (pan > 0) wetL = (wetL + (_headShadowLeft - wetL) * Math.Abs(pan)) * farGain;
                else if (pan < 0) wetR = (wetR + (_headShadowRight - wetR) * Math.Abs(pan)) * farGain;
                float rearCue = (float)_currentRearCue;
                EqCoefficients rearCoefficients = EqCoefficients.Identity.Lerp(_hrtfRearNotch, rearCue);
                wetL = _rearNotchLeft.Process(wetL, rearCoefficients);
                wetR = _rearNotchRight.Process(wetR, rearCoefficients);
                double elevationBlend = Math.Clamp((frontBack + 1.0) * 0.5, 0, 1);
                EqCoefficients elevationCoefficients = _hrtfElevationDown.Lerp(_hrtfElevationUp, elevationBlend);
                wetL = _elevationLeft.Process(wetL, elevationCoefficients);
                wetR = _elevationRight.Process(wetR, elevationCoefficients);
                float haasReturn = delayedMid * (float)_currentHaasLevel * Math.Abs(pan);
                if (pan > 0) wetL += haasReturn; else wetR += haasReturn;
                float rearTint = rear * (float)_currentRearTone;
                float lpAlpha = 0.82f - rearTint * 0.30f;
                _filterLeft += lpAlpha * (wetL - _filterLeft);
                _filterRight += lpAlpha * (wetR - _filterRight);
                wetL += (_filterLeft - wetL) * rearTint;
                wetR += (_filterRight - wetR) * rearTint;
                float rearDistanceGain = 1f - rear * 0.07f;
                float pathDistanceGain = (float)(0.90 + 0.10 * movementRadius);
                wetL *= rearDistanceGain * pathDistanceGain;
                wetR *= rearDistanceGain * pathDistanceGain;

                float blend = (float)_currentSpatialMix;
                float left = leftIn + (wetL - leftIn) * blend;
                float right = rightIn + (wetR - rightIn) * blend;
                ProcessEcho(left, right, rate, _currentEchoDelayMs, _currentEchoFeedback, out float echoL, out float echoR);
                float echoMix = (float)_currentEchoMix;
                left += echoL * echoMix;
                right += echoR * echoMix;
                if (_roomReverb is not null)
                {
                    _roomReverb.Process(left, right, out float reverbL, out float reverbR);
                    float reverbMix = (float)_currentReverbMix;
                    left += reverbL * reverbMix;
                    right += reverbR * reverbMix;
                }
                float gain = (float)(_smoothedVolume * _currentPresetGain);
                left = SoftLimit(left * gain);
                right = SoftLimit(right * gain);
                peak = Math.Max(peak, Math.Max(Math.Abs(left), Math.Abs(right)));
                int offset = i * 8;
                BinaryPrimitives.WriteInt32LittleEndian(_rendered.AsSpan(offset, 4), BitConverter.SingleToInt32Bits(left));
                BinaryPrimitives.WriteInt32LittleEndian(_rendered.AsSpan(offset + 4, 4), BitConverter.SingleToInt32Bits(right));
                _phase += speed * Math.Tau / rate;
                if (_phase >= Math.Tau) _phase -= Math.Tau;
            }
            GetMovementPosition(_phase + _smoothedPhaseOffset, movementPattern, out double targetAngle, out double targetRadius);
            Volatile.Write(ref _targetPhase, targetAngle);
            Volatile.Write(ref _targetRadius, targetRadius);
            _lastPeak = peak;
            buffer.AddSamples(_rendered, 0, frames * 8);
        }
        catch { /* Keep exceptions inside the capture callback. */ }
    }

    private void ConfigureHrtfTables(double sampleRate)
    {
        for (int i = 0; i < OrbitTableSize; i++)
        {
            double side = Math.Abs(OrbitPan[i]);
            double cutoff = 18000.0 - 12500.0 * side;
            _hrtfFarAlpha[i] = (float)(1.0 - Math.Exp(-Math.Tau * cutoff / sampleRate));
            _hrtfFarGain[i] = (float)(1.0 - 0.16 * side);
        }
        _hrtfRearNotch = MakePeak(6500, 1.15, -3.2, sampleRate);
        _hrtfElevationUp = MakeShelf(9500, 0.707, 0.65, sampleRate, false);
        _hrtfElevationDown = MakeShelf(9500, 0.707, -0.65, sampleRate, false);
    }

    private float ReadItd(float[] history, float current, float delaySamples)
    {
        if (delaySamples < 1f) return current;
        int whole = (int)delaySamples;
        float fraction = delaySamples - whole;
        int read = (_itdWriteIndex - whole) & 255;
        return history[read] + (history[(read - 1) & 255] - history[read]) * fraction;
    }

    private void ProcessEcho(float left, float right, double sampleRate, double delayMs, double feedback, out float echoLeft, out float echoRight)
    {
        if (_echoLeft.Length == 0) { echoLeft = echoRight = 0; return; }
        int mask = _echoLeft.Length - 1;
        double delay = Math.Clamp(delayMs * sampleRate / 1000.0, 2.0, _echoLeft.Length - 2.0);
        int whole = (int)delay;
        float fraction = (float)(delay - whole);
        int read = (_echoWriteIndex - whole) & mask;
        echoLeft = _echoLeft[read] + (_echoLeft[(read - 1) & mask] - _echoLeft[read]) * fraction;
        echoRight = _echoRight[read] + (_echoRight[(read - 1) & mask] - _echoRight[read]) * fraction;
        float fb = (float)Math.Clamp(feedback, 0, 0.38);
        // A restrained cross-feed makes the echoes read as a stereo space without collapsing the source.
        _echoLeft[_echoWriteIndex] = Math.Clamp(left + echoRight * fb * 0.35f, -2f, 2f);
        _echoRight[_echoWriteIndex] = Math.Clamp(right + echoLeft * fb * 0.35f, -2f, 2f);
        _echoWriteIndex = (_echoWriteIndex + 1) & mask;
    }

    private sealed class StereoRoomReverb
    {
        private readonly CombFilter[] _leftCombs;
        private readonly CombFilter[] _rightCombs;
        private readonly AllPassFilter _leftDiffuserA, _leftDiffuserB, _rightDiffuserA, _rightDiffuserB;

        public StereoRoomReverb(double sampleRate)
        {
            double[] leftTimes = [0.0297, 0.0371, 0.0411, 0.0437];
            double[] rightTimes = [0.0311, 0.0359, 0.0423, 0.0451];
            _leftCombs = leftTimes.Select(t => new CombFilter(Math.Max(32, (int)(sampleRate * t)), sampleRate)).ToArray();
            _rightCombs = rightTimes.Select(t => new CombFilter(Math.Max(32, (int)(sampleRate * t)), sampleRate)).ToArray();
            _leftDiffuserA = new AllPassFilter(Math.Max(16, (int)(sampleRate * 0.0051)));
            _leftDiffuserB = new AllPassFilter(Math.Max(16, (int)(sampleRate * 0.0017)));
            _rightDiffuserA = new AllPassFilter(Math.Max(16, (int)(sampleRate * 0.0057)));
            _rightDiffuserB = new AllPassFilter(Math.Max(16, (int)(sampleRate * 0.0019)));
        }

        public void SetParameters(double decaySeconds, double damping)
        {
            double decay = Math.Clamp(decaySeconds, 0.45, 3.2);
            double damp = Math.Clamp(damping, 0.15, 0.8);
            foreach (var comb in _leftCombs) comb.SetParameters(decay, damp);
            foreach (var comb in _rightCombs) comb.SetParameters(decay, damp);
        }

        public void Process(float left, float right, out float wetLeft, out float wetRight)
        {
            float sumL = 0, sumR = 0;
            foreach (var comb in _leftCombs) sumL += comb.Process(left);
            foreach (var comb in _rightCombs) sumR += comb.Process(right);
            wetLeft = _leftDiffuserB.Process(_leftDiffuserA.Process(sumL * 0.25f));
            wetRight = _rightDiffuserB.Process(_rightDiffuserA.Process(sumR * 0.25f));
        }
    }

    private sealed class CombFilter
    {
        private readonly float[] _buffer;
        private readonly double _sampleRate;
        private int _index;
        private float _damped;
        private float _feedback = 0.5f;
        private float _damping = 0.35f;
        public CombFilter(int length, double sampleRate) { _buffer = new float[length]; _sampleRate = sampleRate; }
        public void SetParameters(double decay, double damping)
        {
            _feedback = (float)Math.Clamp(Math.Pow(10.0, -3.0 * _buffer.Length / (_sampleRate * decay)), 0.05, 0.97);
            _damping = (float)damping;
        }
        public float Process(float input)
        {
            float delayed = _buffer[_index];
            _damped += (delayed - _damped) * (1f - _damping);
            _buffer[_index] = Math.Clamp(input + _damped * _feedback, -2f, 2f);
            if (++_index == _buffer.Length) _index = 0;
            return delayed;
        }
    }

    private sealed class AllPassFilter
    {
        private readonly float[] _buffer;
        private int _index;
        private const float Feedback = 0.5f;
        public AllPassFilter(int length) => _buffer = new float[length];
        public float Process(float input)
        {
            float delayed = _buffer[_index];
            float output = delayed - Feedback * input;
            _buffer[_index] = input + Feedback * delayed;
            if (++_index == _buffer.Length) _index = 0;
            return output;
        }
    }

    private static int NextCapacity(int length) { int value = 256; while (value < length) value = checked(value * 2); return value; }

    private static void DecodeInto(byte[] bytes, int samples, WaveFormat format, float[] result)
    {
        int bits = format.BitsPerSample;
        if (format.Encoding == WaveFormatEncoding.IeeeFloat && bits == 32)
        {
            for (int i = 0; i < samples; i++) { float v = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(i * 4, 4))); result[i] = float.IsFinite(v) ? v : 0; }
        }
        else if (format.Encoding == WaveFormatEncoding.Pcm && bits == 16)
            for (int i = 0; i < samples; i++) result[i] = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(i * 2, 2)) / 32768f;
        else if (format.Encoding == WaveFormatEncoding.Pcm && bits == 24)
            for (int i = 0; i < samples; i++) { int j = i * 3; int v = bytes[j] | bytes[j + 1] << 8 | bytes[j + 2] << 16; if ((v & 0x800000) != 0) v |= unchecked((int)0xFF000000); result[i] = v / 8388608f; }
        else if (format.Encoding == WaveFormatEncoding.Pcm && bits == 32)
            for (int i = 0; i < samples; i++) result[i] = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(i * 4, 4)) / 2147483648f;
        else throw new NotSupportedException($"Unsupported capture format: {format}");
    }

    private static float SoftLimit(float value)
    {
        float magnitude = Math.Abs(value);
        if (magnitude <= 0.92f) return value;
        float limited = 0.92f + 0.08f * (float)Math.Tanh((magnitude - 0.92f) / 0.08f);
        return MathF.CopySign(limited, value);
    }

    private void UpdateVisualizer()
    {
        double target = Volatile.Read(ref _targetPhase);
        // Interpolate the drawn position on the shortest circular path for fluid motion between audio callbacks.
        double delta = Math.IEEERemainder(target - _visualPhase, Math.Tau);
        _visualPhase += delta * 0.16;
        double targetRadius = Volatile.Read(ref _targetRadius);
        _visualRadius += (targetRadius - _visualRadius) * 0.16;
        double angle = _visualPhase - Math.PI / 2;
        double x = 250 + Math.Cos(angle) * 195 * _visualRadius, y = 220 + Math.Sin(angle) * 195 * _visualRadius;
        Canvas.SetLeft(OrbitDot, x - 5); Canvas.SetTop(OrbitDot, y - 5);
        Canvas.SetLeft(OrbitGlow, x - 13); Canvas.SetTop(OrbitGlow, y - 13);
        string direction = GetDirectionName(_visualPhase);
        if (!string.Equals(direction, _lastPositionLabel, StringComparison.Ordinal))
        {
            PositionText.Text = direction;
            _lastPositionLabel = direction;
        }
        double db = _lastPeak <= 0.00001 ? -90 : 20 * Math.Log10(_lastPeak);
        double displayedDb = Math.Round(db, 1);
        if (displayedDb != _lastDisplayedDb)
        {
            LevelText.Text = db <= -89 ? "−∞ dBFS" : $"{displayedDb:0.0} dBFS";
            _lastDisplayedDb = displayedDb;
        }
        double levelBarValue = Math.Clamp((db + 60) / 60 * 100, 0, 100);
        if (Math.Abs(levelBarValue - _lastLevelBarValue) >= 0.1)
        {
            LevelBar.Value = levelBarValue;
            _lastLevelBarValue = levelBarValue;
        }
        // Read-only visual response: no samples or spectrum are requested from the audio path.
        double peak = Math.Clamp(_lastPeak, 0f, 1f);
        double targetEnergy = _running ? Math.Sqrt(peak) : 0.025;
        _visualEnergy += (targetEnergy - _visualEnergy) * (_running ? 0.22 : 0.035);
        _visualizerTime += 0.016;
        double energy = Math.Clamp(_visualEnergy, 0, 1);
        OrbHalo.Opacity = 0.11 + energy * 0.29;
        OrbCore.Opacity = 0.91 + energy * 0.09;
        UpdateLevelVisualizer(energy);
    }

    private void InitializeLevelVisualizer()
    {
        const int barCount = 28;
        for (int i = 0; i < barCount; i++)
        {
            var bar = new Rectangle
            {
                RadiusX = 2,
                RadiusY = 2,
                Height = 3,
                Fill = new LinearGradientBrush(Color.FromRgb(66, 220, 255), Color.FromRgb(136, 105, 255), 90),
                Opacity = 0.82
            };
            LevelVisualizerCanvas.Children.Add(bar);
            _levelBars.Add(bar);
        }

        const int segmentCount = 48;
        for (int i = 0; i < segmentCount; i++)
        {
            var segment = new Line
            {
                Stroke = new SolidColorBrush(Color.FromRgb(117, 173, 255)),
                StrokeThickness = 1.35,
                Opacity = 0.68,
                SnapsToDevicePixels = true
            };
            LevelVisualizerCanvas.Children.Add(segment);
            _levelWaveSegments.Add(segment);
        }
        LevelVisualizerCanvas.SizeChanged += (_, _) => UpdateLevelVisualizer(Math.Clamp(_visualEnergy, 0, 1));
    }

    private void UpdateLevelVisualizer(double energy)
    {
        double width = LevelVisualizerCanvas.ActualWidth;
        double height = LevelVisualizerCanvas.ActualHeight;
        if (width < 2 || height < 2) return;

        // Level-reactive illustration only; frequency-specific FFT telemetry is not exposed.
        double baseline = height * 0.51;
        double maxAmplitude = height * (0.08 + energy * 0.40);
        bool resized = Math.Abs(width - _levelVisualizerWidth) >= 0.5;
        if (resized) _levelVisualizerWidth = width;
        for (int i = 0; i < _levelWaveSegments.Count; i++)
        {
            double t1 = (double)i / _levelWaveSegments.Count;
            double t2 = (double)(i + 1) / _levelWaveSegments.Count;
            Line line = _levelWaveSegments[i];
            if (resized)
            {
                line.X1 = width * i / _levelWaveSegments.Count;
                line.X2 = width * (i + 1) / _levelWaveSegments.Count;
            }
            line.Y1 = baseline + LevelWaveAt(t1, _visualizerTime, energy) * maxAmplitude;
            line.Y2 = baseline + LevelWaveAt(t2, _visualizerTime, energy) * maxAmplitude;
            line.Opacity = 0.28 + energy * 0.65;
        }

        double slot = width / _levelBars.Count;
        for (int i = 0; i < _levelBars.Count; i++)
        {
            double position = (double)i / (_levelBars.Count - 1);
            double envelope = 0.25 + 0.75 * Math.Pow(Math.Sin(Math.PI * position), 0.65);
            double motion = 0.22 + 0.78 * Math.Abs(Math.Sin(_visualizerTime * (1.7 + position * 0.9) + position * 8.2));
            double barHeight = Math.Clamp(3 + energy * height * 0.68 * envelope * motion, 3, height - 4);
            Rectangle bar = _levelBars[i];
            if (resized)
            {
                bar.Width = Math.Max(2, slot * 0.43);
                Canvas.SetLeft(bar, i * slot + (slot - bar.Width) / 2);
            }
            bar.Height = barHeight;
            Canvas.SetTop(bar, height - barHeight);
            bar.Opacity = 0.30 + energy * 0.58;
        }
    }

    private static double LevelWaveAt(double t, double time, double energy)
    {
        double envelope = 0.26 + 0.74 * Math.Abs(Math.Sin(time * 1.6 + t * 5.4));
        double carrier = Math.Sin(t * Math.Tau * 2.15 + time * 3.2)
                       + 0.32 * Math.Sin(t * Math.Tau * 5.1 - time * 2.1);
        return carrier * envelope * (0.32 + energy * 0.68);
    }

    private static string GetDirectionName(double phase)
    {
        double degrees = ((phase * 180 / Math.PI) % 360 + 360) % 360;
        return degrees switch
        {
            < 22.5 or >= 337.5 => "FRONT",
            < 67.5 => "FRONT RIGHT",
            < 112.5 => "RIGHT SIDE",
            < 157.5 => "REAR RIGHT",
            < 202.5 => "REAR",
            < 247.5 => "REAR LEFT",
            < 292.5 => "LEFT SIDE",
            _ => "FRONT LEFT"
        };
    }

    private void OrbitCanvas_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _isDraggingOrbit = true;
        OrbitCanvas.CaptureMouse();
        SetOrbitPosition(e.GetPosition(OrbitCanvas));
        e.Handled = true;
    }

    private void OrbitCanvas_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_isDraggingOrbit && e.LeftButton == System.Windows.Input.MouseButtonState.Pressed)
            SetOrbitPosition(e.GetPosition(OrbitCanvas));
    }

    private void OrbitCanvas_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _isDraggingOrbit = false;
        OrbitCanvas.ReleaseMouseCapture();
    }

    private void SetOrbitPosition(Point point)
    {
        double dx = point.X - 250;
        double dy = point.Y - 220;
        if (Math.Sqrt(dx * dx + dy * dy) < 44) return;
        double requestedPhase = Math.Atan2(dy, dx) + Math.PI / 2;
        int pattern = Volatile.Read(ref _movementPattern);
        if (pattern >= 5) requestedPhase = FindClosestPathPhase(pattern, dx / 195.0, -dy / 195.0);
        else if (pattern == 1) requestedPhase = -requestedPhase;
        else if (pattern == 2)
        {
            double frontArcAngle = Math.Clamp(Math.IEEERemainder(requestedPhase, Math.Tau), -Math.PI / 2, Math.PI / 2);
            requestedPhase = Math.Asin(Math.Clamp(frontArcAngle / (Math.PI / 2), -1, 1));
        }
        else if (pattern == 3)
        {
            double rearArcAngle = Math.Clamp(Math.IEEERemainder(requestedPhase - Math.PI, Math.Tau), -Math.PI / 2, Math.PI / 2);
            requestedPhase = Math.Asin(Math.Clamp(-rearArcAngle / (Math.PI / 2), -1, 1));
        }
        else if (pattern == 4)
        {
            double sweepAngle = Math.Clamp(Math.IEEERemainder(requestedPhase, Math.Tau), -Math.PI, Math.PI);
            requestedPhase = Math.Asin(Math.Clamp(sweepAngle / Math.PI, -1, 1));
        }
        double offset = Math.IEEERemainder(requestedPhase - _phase, Math.Tau);
        Volatile.Write(ref _phaseOffset, offset);
        MarkSettingsChanged();
        if (!_running)
        {
            GetMovementPosition(_phase + offset, pattern, out double angle, out double radius);
            Volatile.Write(ref _targetPhase, angle);
            Volatile.Write(ref _targetRadius, radius);
        }
    }

    private static double FindClosestPathPhase(int pattern, double x, double z)
    {
        int pathIndex = pattern == 5 ? PolygonPaths.Length - 1 : pattern - 6;
        PositionPoint[] path = PolygonPaths[Math.Clamp(pathIndex, 0, PolygonPaths.Length - 1)];
        double bestDistance = double.PositiveInfinity;
        double bestPosition = 0;
        for (int i = 0; i < path.Length; i++)
        {
            PositionPoint start = path[i];
            PositionPoint end = path[(i + 1) % path.Length];
            double dx = end.X - start.X, dz = end.Z - start.Z;
            double lengthSquared = dx * dx + dz * dz;
            double blend = lengthSquared > 0 ? Math.Clamp(((x - start.X) * dx + (z - start.Z) * dz) / lengthSquared, 0, 1) : 0;
            double px = start.X + dx * blend, pz = start.Z + dz * blend;
            double distance = (x - px) * (x - px) + (z - pz) * (z - pz);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestPosition = (i + blend) / path.Length;
            }
        }
        return bestPosition * Math.Tau;
    }

    private void Capture_RecordingStopped(object? sender, StoppedEventArgs e) => Dispatcher.BeginInvoke(new Action(async () => await StopEngineAsync()));
    private async void StopButton_Click(object sender, RoutedEventArgs e) => await StopEngineAsync();

    private async Task StopEngineAsync()
    {
        _running = false;
        if (_capture is not null) { _capture.DataAvailable -= Capture_DataAvailable; _capture.RecordingStopped -= Capture_RecordingStopped; try { _capture.StopRecording(); } catch { } }
        try { _output?.Stop(); } catch { }
        await Task.Delay(40);
        try { _capture?.Dispose(); } catch { } try { _output?.Dispose(); } catch { }
        _capture = null; _output = null; _buffer = null;
        StatusText.Text = "ENGINE OFFLINE"; StatusDot.Fill = new SolidColorBrush(Color.FromRgb(117, 128, 147));
        StartButton.Content = "Start real-time 8D"; StartButton.IsEnabled = true; StopButton.IsEnabled = false;
    }

    private void DeveloperLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    private async void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _visualTimer.Stop(); _mediaTimer.Stop(); await StopEngineAsync(); _inputDevice?.Dispose(); _outputDevice?.Dispose(); _enumerator.Dispose();
    }
}



















