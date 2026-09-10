using ComputeSharp;
using ComputeSharp.D2D1.WinUI;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PoChopAudio.WinUI.Common;
using PoChopAudio.WinUI.Shaders;
using Windows.Foundation;

namespace PoChopAudio.WinUI.Controls;

/// <summary>
/// Runs <see cref="AuroraShader"/> behind a page.
///
/// <para>
/// Two rules it has to keep. It <b>stops when it is not visible</b> — a per-frame GPU wakeup for a
/// page the user navigated away from is pure waste. And it <b>respects the system reduced-motion
/// setting</b>: with animation off the shader still draws, once, at a fixed time, so the page keeps
/// its colour without anything moving.
/// </para>
/// </summary>
public sealed partial class ShaderBackdrop : UserControl
{
    private readonly PixelShaderEffect<AuroraShader> _effect = new();

    private static ShaderBackdrop? _activeInstance;
    private float _audioEnergy;
    private bool _isClipping;

    private bool _isDark;
    private bool _animate = true;
    private float _intensity = 0.55f;
    private bool _ticking;
    private DateTimeOffset _started = DateTimeOffset.UtcNow;

    /// <summary>
    /// Reports instantaneous audio energy (0..1) and clipping to make the backdrop reactive.
    /// </summary>
    public static void ReportAudioEnergy(float energy, bool clipping = false)
    {
        if (_activeInstance is { } backdrop)
        {
            backdrop._audioEnergy = Math.Max(backdrop._audioEnergy, Math.Clamp(energy, 0f, 1f));
            backdrop._isClipping = clipping;
        }
    }

    /// <summary>
    /// Whether <see cref="OnUnloaded"/> has torn the Win2D resources down. Loaded and Unloaded were
    /// not a pair: Unloaded disposed the effect and pulled the surface out of the visual tree, and
    /// Loaded rebuilt neither. Anything that re-parented this control - which XAML is free to do -
    /// would come back to a disposed effect and die with RO_E_CLOSED, the same way the Cutout page
    /// did. Nothing re-parents it today; this makes that not matter.
    /// </summary>
    private bool _torndown;

    public ShaderBackdrop()
    {
        InitializeComponent();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        ActualThemeChanged += (_, _) => CaptureUiState();
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => ApplyRunState());
        RegisterPropertyChangedCallback(IntensityProperty, (_, _) => CaptureUiState());
    }

    /// <summary>0 hides the effect, 1 is full strength. Kept low behind readable content.</summary>
    public static readonly DependencyProperty IntensityProperty =
        DependencyProperty.Register(nameof(Intensity), typeof(double), typeof(ShaderBackdrop),
            new PropertyMetadata(0.55));

    public double Intensity
    {
        get => (double)GetValue(IntensityProperty);
        set => SetValue(IntensityProperty, value);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_torndown)
        {
            // Reloaded after a teardown. The effect cannot be revived, so there is nothing to draw
            // and nothing to animate; staying silently blank beats faulting the process.
            return;
        }

        _activeInstance = this;
        _started = DateTimeOffset.UtcNow;
        CaptureUiState();
        ApplyRunState();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_activeInstance == this)
        {
            _activeInstance = null;
        }

        if (_torndown)
        {
            return;
        }

        _torndown = true;
        StopTicking();

        // Win2D holds a device per control; without this it is released only whenever the finalizer
        // eventually runs, and pages are created and dropped every time the user navigates.
        Surface.RemoveFromVisualTree();
        _effect.Dispose();
    }

    private void CaptureUiState()
    {
        _isDark = ActualTheme == ElementTheme.Dark;
        _animate = Motion.AnimationsEnabled;
        _intensity = (float)Intensity;

        Surface.Invalidate();
    }

    private void ApplyRunState()
    {
        _animate = Motion.AnimationsEnabled;

        if (Visibility == Visibility.Visible && _animate)
        {
            StartTicking();
        }
        else
        {
            // One more frame so the still image is correct, then nothing.
            StopTicking();
            Surface.Invalidate();
        }
    }

    private void StartTicking()
    {
        if (_ticking)
        {
            return;
        }

        CompositionTarget.Rendering += OnRendering;
        _ticking = true;
    }

    private void StopTicking()
    {
        if (!_ticking)
        {
            return;
        }

        CompositionTarget.Rendering -= OnRendering;
        _ticking = false;
    }

    private void OnRendering(object? sender, object e) => Surface.Invalidate();

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var width = (float)sender.ActualWidth;
        var height = (float)sender.ActualHeight;

        if (width <= 0 || height <= 0)
        {
            return;
        }

        // Held apart from the light palette rather than derived from it: a gradient that merely
        // darkens reads as grey mud, so the dark scheme gets its own, more saturated tints over a
        // near-black base.
        var tintA = _isDark ? new float3(0.16f, 0.26f, 0.55f) : new float3(0.42f, 0.58f, 0.95f);
        var tintB = _isDark ? new float3(0.30f, 0.13f, 0.42f) : new float3(0.62f, 0.80f, 0.92f);
        var baseColor = _isDark ? new float3(0.04f, 0.05f, 0.08f) : new float3(0.96f, 0.97f, 0.99f);

        var energy = _audioEnergy;
        _audioEnergy = Math.Max(0f, _audioEnergy * 0.90f);

        if (_isClipping)
        {
            tintA = new float3(0.92f, 0.18f, 0.18f);
        }
        else if (energy > 0.55f)
        {
            var t = (energy - 0.55f) * 2.2f;
            tintA = new float3(
                (tintA.X * (1f - t)) + (0.95f * t),
                (tintA.Y * (1f - t)) + (0.45f * t),
                (tintA.Z * (1f - t)) + (0.15f * t));
        }

        // A fixed time when motion is off, so the picture is a still frame rather than a paused one
        // that jumps whenever the control is re-created.
        var seconds = _animate ? (float)(DateTimeOffset.UtcNow - _started).TotalSeconds : 12f;

        _effect.ConstantBuffer = new AuroraShader(
            seconds,
            new float2(width, height),
            tintA,
            tintB,
            baseColor,
            _intensity,
            energy);

        var bounds = new Rect(0, 0, width, height);

        // A zero-input shader has infinite output extent, so the source rectangle is what tells
        // Direct2D which pixels to actually evaluate.
        args.DrawingSession.DrawImage(_effect, bounds, bounds);
    }
}
