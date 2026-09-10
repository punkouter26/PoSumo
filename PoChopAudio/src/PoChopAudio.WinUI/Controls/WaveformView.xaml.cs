using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using PoChopAudio.Services.Dsp;
using PoChopAudio.Services.Chop;
using PoChopAudio.WinUI.Common;
using PoChopAudio.WinUI.Models;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using Windows.UI;

namespace PoChopAudio.WinUI.Controls;

/// <summary>Visual color palette themes for the spectrogram view.</summary>
public enum SpectrogramPalette
{
    Classic,
    Cyberpunk,
    Bioluminescence,
    Thermal
}

/// <summary>
/// The recording, drawn on the GPU: segment bands, an amplitude trace or a spectrogram, and a
/// playhead that lives on the compositor thread.
/// </summary>
public sealed partial class WaveformView : UserControl
{
    private const float FooterHeight = 18f;

    // Colours are held as Win2D colours rather than brushes because a drawing session takes colours
    // directly; a ThemeResource lookup per bar was never going to be the right shape here.
    private static readonly Color SegmentEvenFill = Color.FromArgb(60, 59, 130, 246);
    private static readonly Color SegmentOddFill = Color.FromArgb(60, 16, 185, 129);
    private static readonly Color SegmentEvenEdge = Color.FromArgb(190, 59, 130, 246);
    private static readonly Color SegmentOddEdge = Color.FromArgb(190, 16, 185, 129);

    private ChopFileItem? _item;
    private CanvasControl? _surface;
    private CanvasTextFormat? _badgeFormat;
    private SpriteVisual? _playhead;
    private bool _isScrubbing;

    // Laser slice and spark particle system
    private readonly ParticleField _sparks = new(64);
    private float _sliceLaserAlpha;
    private float _snapX;
    private float _snapRippleAlpha;
    private DateTimeOffset _lastSnapTime = DateTimeOffset.MinValue;
    private DateTimeOffset _lastRenderTime = DateTimeOffset.UtcNow;
    private bool _isAnimTicking;

    /// <summary>Raised on click or on the end of a drag, with the segment under the pointer if any.</summary>
    public event Action<ChopFileItem, ChopSegment?>? SegmentClicked;

    /// <summary>Raised while dragging, with a position in seconds. Lets the page seek playback.</summary>
    public event Action<ChopFileItem, double>? Scrubbed;

    public WaveformView()
    {
        InitializeComponent();

        RootGrid.PointerPressed += OnPointerPressed;
        RootGrid.PointerMoved += OnPointerMoved;
        RootGrid.PointerReleased += OnPointerReleased;
        RootGrid.PointerExited += OnPointerExited;
        RootGrid.RightTapped += (s, e) =>
        {
            if (_item?.ShowSpectrogram == true)
            {
                NextPalette();
                e.Handled = true;
            }
        };

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public static readonly DependencyProperty PaletteProperty =
        DependencyProperty.Register(nameof(Palette), typeof(SpectrogramPalette), typeof(WaveformView),
            new PropertyMetadata(SpectrogramPalette.Classic, (d, _) => ((WaveformView)d).Redraw()));

    public SpectrogramPalette Palette
    {
        get => (SpectrogramPalette)GetValue(PaletteProperty);
        set => SetValue(PaletteProperty, value);
    }

    public static readonly DependencyProperty ItemProperty =
        DependencyProperty.Register(nameof(Item), typeof(ChopFileItem), typeof(WaveformView),
            new PropertyMetadata(null, (d, e) => ((WaveformView)d).OnItemChanged(e.NewValue as ChopFileItem)));

    public ChopFileItem? Item
    {
        get => (ChopFileItem?)GetValue(ItemProperty);
        set => SetValue(ItemProperty, value);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        EnsureSurface();
        EnsurePlayhead();
        Redraw();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_item is not null)
        {
            _item.PropertyChanged -= OnItemPropertyChanged;
        }

        StopAnimTicking();
        ReleaseSurface();
    }

    /// <summary>
    /// Creates the drawing surface if this control does not currently have one.
    /// <para>
    /// Called from <c>Loaded</c> rather than the constructor because the card lists virtualize: an
    /// element scrolled out of view is unloaded, has its surface released, and is later reloaded
    /// against a different item. A <see cref="CanvasControl"/> cannot be revived after
    /// <c>RemoveFromVisualTree</c>, so re-entering the tree means building a new one.
    /// </para>
    /// </summary>
    private void EnsureSurface()
    {
        if (_surface is not null)
        {
            return;
        }

        _surface = new CanvasControl { ClearColor = Colors.Transparent };
        _surface.Draw += OnDraw;
        _surface.CreateResources += OnCreateResources;

        // Index 0: everything else in the grid - playhead host, time markers, hover tip, busy
        // badge - is an overlay and has to stay above the drawing.
        RootGrid.Children.Insert(0, _surface);
    }

    private void ReleaseSurface()
    {
        if (_surface is null)
        {
            return;
        }

        _surface.Draw -= OnDraw;
        _surface.CreateResources -= OnCreateResources;

        // Win2D holds a device per control; without this they are released only when the finalizer
        // eventually runs, and a scrolling batch creates and drops these often.
        _surface.RemoveFromVisualTree();
        _surface = null;
    }

    private void OnCreateResources(CanvasControl sender, CanvasCreateResourcesEventArgs args)
    {
        _badgeFormat = new CanvasTextFormat
        {
            FontSize = 10,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            WordWrapping = CanvasWordWrapping.NoWrap,
        };
    }

    private void OnItemChanged(ChopFileItem? newItem)
    {
        if (_item is not null)
        {
            _item.PropertyChanged -= OnItemPropertyChanged;
        }

        _item = newItem;

        if (_item is not null)
        {
            _item.PropertyChanged += OnItemPropertyChanged;
        }

        Redraw();
    }

    private void OnItemPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ChopFileItem.Segments):
                DispatcherQueue.TryEnqueue(() =>
                {
                    TriggerSliceFx();
                    Redraw();
                });
                break;

            case nameof(ChopFileItem.Waveform):
            case nameof(ChopFileItem.Spectrogram):
            case nameof(ChopFileItem.ShowSpectrogram):
                DispatcherQueue.TryEnqueue(Redraw);
                break;

            case nameof(ChopFileItem.IsBuildingSpectrogram):
                DispatcherQueue.TryEnqueue(() => BusyBadge.Visibility =
                    _item?.IsBuildingSpectrogram == true ? Visibility.Visible : Visibility.Collapsed);
                break;

            case nameof(ChopFileItem.PlayheadRatio):
            case nameof(ChopFileItem.IsPlaying):
                DispatcherQueue.TryEnqueue(UpdatePlayhead);
                break;
        }
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        Redraw();
        UpdatePlayhead();
    }

    /// <summary>Marks the surface dirty and refreshes everything that is not drawn by Win2D.</summary>
    private void Redraw()
    {
        _surface?.Invalidate();

        if (_item is null)
        {
            StartTimeText.Text = "0:00.0";
            EndTimeText.Text = "0:00.0";
            AutomationProperties.SetName(RootBorder, "Waveform, empty");
            return;
        }

        StartTimeText.Text = "0:00.0";
        EndTimeText.Text = FormatTime(_item.DurationSeconds);

        var count = _item.Segments.Count;
        var mode = _item.ShowSpectrogram ? "Frequency view" : "Waveform";

        AutomationProperties.SetName(
            RootBorder,
            $"{mode} of {_item.FileName}, {_item.DurationSeconds:F1} seconds, {count} sound{(count == 1 ? string.Empty : "s")} found");
    }

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (_item is null)
        {
            return;
        }

        var session = args.DrawingSession;
        var width = (float)sender.ActualWidth;
        var height = (float)sender.ActualHeight;

        if (width <= 0 || height <= 0)
        {
            return;
        }

        var plotHeight = Math.Max(1f, height - FooterHeight);

        if (_item.ShowSpectrogram && _item.Spectrogram is { } spectrogram)
        {
            DrawSpectrogram(session, sender, spectrogram, width, plotHeight);
        }

        DrawSegments(session, width, plotHeight);

        if (!_item.ShowSpectrogram)
        {
            DrawWaveform(session, width, plotHeight);
        }

        DrawLaserAndSparks(session, width, plotHeight);
    }

    private void DrawLaserAndSparks(CanvasDrawingSession session, float width, float height)
    {
        if (_sliceLaserAlpha > 0 && _item is not null && _item.DurationSeconds > 0)
        {
            var alpha = (byte)(255 * _sliceLaserAlpha);
            var beamGlow = Color.FromArgb((byte)(alpha * 0.45f), 96, 165, 250);
            var beamCore = Color.FromArgb(alpha, 255, 255, 255);

            foreach (var seg in _item.Segments)
            {
                var x = (float)(seg.StartSeconds / _item.DurationSeconds * width);
                session.DrawLine(x, 0, x, height, beamGlow, 5f);
                session.DrawLine(x, 0, x, height, beamCore, 1.8f);
            }
        }

        if (_sparks.HasLiveParticles)
        {
            foreach (ref readonly var spark in _sparks.Alive)
            {
                var alpha = (byte)(255 * spark.Remaining);
                var sparkColor = Color.FromArgb(alpha, 254, 240, 138);
                session.FillCircle(spark.X, spark.Y, Math.Max(1f, spark.Size * 0.75f), sparkColor);
            }
        }

        if (_snapRippleAlpha > 0)
        {
            var rippleRadius = ((1f - _snapRippleAlpha) * 28f) + 4f;
            var rippleAlpha = (byte)(220 * _snapRippleAlpha);
            session.DrawCircle(_snapX, height * 0.5f, rippleRadius, Color.FromArgb(rippleAlpha, 96, 165, 250), 1.5f);
            session.DrawLine(_snapX, 0, _snapX, height, Color.FromArgb(rippleAlpha, 255, 255, 255), 1.2f);
        }
    }

    private void DrawSegments(CanvasDrawingSession session, float width, float height)
    {
        var duration = _item!.DurationSeconds;

        if (duration <= 0 || _item.Segments.Count == 0)
        {
            return;
        }

        for (var i = 0; i < _item.Segments.Count; i++)
        {
            var segment = _item.Segments[i];
            var isEven = i % 2 == 0;

            var startX = (float)(segment.StartSeconds / duration * width);
            var endX = (float)(segment.EndSeconds / duration * width);
            var bandWidth = Math.Max(2f, endX - startX);

            var rectangle = new Rect(startX, 0, bandWidth, height);

            // Over a spectrogram the fill would wash out the picture underneath, so only the edges
            // are drawn there; over a waveform the tint is what makes the bands readable at all.
            if (!_item.ShowSpectrogram)
            {
                session.FillRoundedRectangle(rectangle, 3, 3, isEven ? SegmentEvenFill : SegmentOddFill);
            }

            var edge = isEven ? SegmentEvenEdge : SegmentOddEdge;
            session.DrawLine(startX, 0, startX, height, edge, 1.5f);
            session.DrawLine(startX + bandWidth, 0, startX + bandWidth, height, edge, 1.5f);

            if (_badgeFormat is not null && bandWidth > 46)
            {
                var label = $"Take {segment.Index} ({segment.DurationSeconds:F1}s)";
                var badge = new Rect(startX + 3, 3, Math.Min(bandWidth - 6, 96), 15);
                session.FillRoundedRectangle(badge, 3, 3, edge);
                session.DrawText(label, new Vector2((float)badge.X + 4, (float)badge.Y + 1), Colors.White, _badgeFormat);
            }
        }
    }

    private void DrawWaveform(CanvasDrawingSession session, float width, float height)
    {
        var wave = _item!.Waveform;

        if (wave.Count == 0)
        {
            return;
        }

        var midY = height / 2f;
        var bars = Math.Min(wave.Count, (int)Math.Max(50, width / 2));
        var barWidth = width / bars;
        var step = wave.Count / (double)bars;
        var thickness = Math.Max(1f, barWidth - 1f);
        var color = Color.FromArgb(210, 200, 220, 255);

        for (var i = 0; i < bars; i++)
        {
            var amplitude = Math.Clamp(wave[Math.Min(wave.Count - 1, (int)(i * step))], 0f, 1f);
            var barHeight = Math.Max(1.5f, amplitude * (height - 8f));
            var x = (i * barWidth) + (barWidth / 2f);

            session.DrawLine(x, midY - (barHeight / 2f), x, midY + (barHeight / 2f), color, thickness);
        }
    }

    private void DrawSpectrogram(
        CanvasDrawingSession session, CanvasControl sender, SpectrogramData data, float width, float height)
    {
        // One BGRA pixel per cell, uploaded as a bitmap and stretched.
        var pixels = new byte[data.Columns * data.Bins * 4];
        var palette = Palette;

        for (var column = 0; column < data.Columns; column++)
        {
            for (var bin = 0; bin < data.Bins; bin++)
            {
                // Bin 0 is the lowest frequency and belongs at the bottom of the image.
                var row = data.Bins - 1 - bin;
                var offset = ((row * data.Columns) + column) * 4;
                var (r, g, b) = Ramp(data.At(column, bin), palette);

                pixels[offset + 0] = b;
                pixels[offset + 1] = g;
                pixels[offset + 2] = r;
                pixels[offset + 3] = 255;
            }
        }

        using var bitmap = CanvasBitmap.CreateFromBytes(
            sender, pixels, data.Columns, data.Bins, DirectXPixelFormat.B8G8R8A8UIntNormalized);

        session.DrawImage(bitmap, new Rect(0, 0, width, height));
    }

    /// <summary>
    /// Magnitude to colour mapping across selectable palettes: Classic, Cyberpunk, Bioluminescence, Thermal.
    /// </summary>
    private static (byte R, byte G, byte B) Ramp(float value, SpectrogramPalette palette)
    {
        value = Math.Clamp(value, 0f, 1f);

        return palette switch
        {
            SpectrogramPalette.Cyberpunk => (
                (byte)(255 * Math.Clamp((value * 2.8f) - 0.4f, 0f, 1f)),
                (byte)(255 * Math.Clamp((value * 2.0f) - 0.9f, 0f, 1f)),
                (byte)(255 * Math.Clamp(value * 2.2f, 0f, 1f))
            ),
            SpectrogramPalette.Bioluminescence => (
                (byte)(255 * Math.Clamp((value * 2.6f) - 1.2f, 0f, 1f)),
                (byte)(255 * Math.Clamp((value * 2.1f) - 0.15f, 0f, 1f)),
                (byte)(255 * Math.Clamp(((value * 1.5f) + 0.12f) * (1f - (value * 0.4f)), 0f, 1f))
            ),
            SpectrogramPalette.Thermal => (
                (byte)(255 * Math.Clamp(value * 2.5f, 0f, 1f)),
                (byte)(255 * Math.Clamp((value * 2.5f) - 0.9f, 0f, 1f)),
                (byte)(255 * Math.Clamp((value * 3.0f) - 2.0f, 0f, 1f))
            ),
            _ => (
                (byte)(255 * Math.Clamp((value * 2.2f) - 0.35f, 0f, 1f)),
                (byte)(255 * Math.Clamp((value * 2.6f) - 1.35f, 0f, 1f)),
                (byte)(255 * Math.Clamp(value * 3.0f - 0.05f, 0f, 1f) * (1f - Math.Clamp((value * 1.6f) - 0.65f, 0f, 1f)))
            ),
        };
    }

    private void EnsurePlayhead()
    {
        if (_playhead is not null)
        {
            return;
        }

        var compositor = ElementCompositionPreview.GetElementVisual(PlayheadHost).Compositor;

        _playhead = compositor.CreateSpriteVisual();
        _playhead.Brush = compositor.CreateColorBrush(Color.FromArgb(255, 96, 165, 250));
        _playhead.Size = new Vector2(2f, 0f);
        _playhead.IsVisible = false;

        // The position arrives once per playback tick. Without an implicit animation the head
        // jumps between ticks; with one the compositor interpolates, so it stays smooth even while
        // the UI thread is busy rendering an export.
        var slide = compositor.CreateVector3KeyFrameAnimation();
        slide.InsertExpressionKeyFrame(1f, "this.FinalValue");
        slide.Duration = TimeSpan.FromMilliseconds(90);

        // Target is not optional. Registering an implicit animation under a property key does not
        // tell the animation what to animate; without this the first write to Offset below threw
        // "The triggered animation must have a target specified" out of a pointer event, which
        // ended the process. Since UpdatePlayhead only assigns Offset while a take is playing or
        // being scrubbed, that meant clicking a waveform - or playing any clip - killed the app.
        slide.Target = nameof(Visual.Offset);

        var implicits = compositor.CreateImplicitAnimationCollection();
        implicits[nameof(Visual.Offset)] = slide;
        _playhead.ImplicitAnimations = implicits;

        ElementCompositionPreview.SetElementChildVisual(PlayheadHost, _playhead);
        UpdatePlayhead();
    }

    private void UpdatePlayhead()
    {
        EnsurePlayhead();

        if (_playhead is null)
        {
            return;
        }

        if (_item is null || (!_item.IsPlaying && !_isScrubbing))
        {
            _playhead.IsVisible = false;
            return;
        }

        var width = (float)RootGrid.ActualWidth;
        var height = (float)RootGrid.ActualHeight;

        if (width <= 0 || height <= 0)
        {
            return;
        }

        _playhead.Size = new Vector2(2f, Math.Max(1f, height - FooterHeight));
        _playhead.Offset = new Vector3((float)(_item.PlayheadRatio * width), 0f, 0f);
        _playhead.IsVisible = true;
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_item is null || _item.DurationSeconds <= 0)
        {
            return;
        }

        _isScrubbing = true;
        RootGrid.CapturePointer(e.Pointer);
        ReportScrub(e);
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_item is null || _item.DurationSeconds <= 0)
        {
            return;
        }

        var position = e.GetCurrentPoint(RootGrid).Position;
        var ratio = Math.Clamp(position.X / Math.Max(1.0, RootGrid.ActualWidth), 0.0, 1.0);

        HoverTipText.Text = FormatTime(ratio * _item.DurationSeconds);
        HoverTip.Visibility = Visibility.Visible;
        HoverTip.Margin = new Thickness(Math.Max(0, position.X - 24), 6, 0, 0);

        if (_isScrubbing)
        {
            ReportScrub(e);
        }
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isScrubbing || _item is null)
        {
            return;
        }

        _isScrubbing = false;
        RootGrid.ReleasePointerCapture(e.Pointer);

        var seconds = SecondsAt(e);
        var matched = _item.Segments.FirstOrDefault(s => seconds >= s.StartSeconds && seconds <= s.EndSeconds);
        SegmentClicked?.Invoke(_item, matched);

        UpdatePlayhead();
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        HoverTip.Visibility = Visibility.Collapsed;
    }

    private void ReportScrub(PointerRoutedEventArgs e)
    {
        var seconds = SecondsAt(e);

        if (_item is not null && _item.DurationSeconds > 0 && _item.Segments.Count > 0)
        {
            const double snapToleranceSec = 0.055; // 55 ms magnetic capture zone
            foreach (var seg in _item.Segments)
            {
                if (Math.Abs(seconds - seg.StartSeconds) < snapToleranceSec)
                {
                    seconds = seg.StartSeconds;
                    TriggerSnap((float)(seconds / _item.DurationSeconds * RootGrid.ActualWidth));
                    break;
                }

                if (Math.Abs(seconds - seg.EndSeconds) < snapToleranceSec)
                {
                    seconds = seg.EndSeconds;
                    TriggerSnap((float)(seconds / _item.DurationSeconds * RootGrid.ActualWidth));
                    break;
                }
            }
        }

        _item!.PlayheadRatio = _item.DurationSeconds > 0 ? seconds / _item.DurationSeconds : 0;
        UpdatePlayhead();
        Scrubbed?.Invoke(_item, seconds);
    }

    private void TriggerSnap(float x)
    {
        if (Math.Abs(_snapX - x) > 6f || (DateTimeOffset.UtcNow - _lastSnapTime).TotalMilliseconds > 220)
        {
            _snapX = x;
            _snapRippleAlpha = 1.0f;
            _lastSnapTime = DateTimeOffset.UtcNow;
            _item?.Owner?.Cues.Play(AudioCue.ScrubTick);
            StartAnimTicking();
        }
    }

    /// <summary>Fires the laser cut line and spark burst animation.</summary>
    public void TriggerSliceFx()
    {
        if (!Motion.AnimationsEnabled || _item is null || _item.DurationSeconds <= 0)
        {
            return;
        }

        _sliceLaserAlpha = 1.0f;
        var width = (float)RootGrid.ActualWidth;
        var height = Math.Max(1f, (float)RootGrid.ActualHeight - FooterHeight);

        if (width > 0)
        {
            foreach (var seg in _item.Segments)
            {
                var x = (float)(seg.StartSeconds / _item.DurationSeconds * width);
                _sparks.EmitSparks(x, height * 0.5f, count: 6, speed: 170f);
            }
        }

        _item.Owner?.Cues.Play(AudioCue.Slice);
        StartAnimTicking();
    }

    /// <summary>Cycles through the available spectrogram color palettes.</summary>
    public void NextPalette()
    {
        Palette = (SpectrogramPalette)(((int)Palette + 1) % 4);
        Redraw();
    }

    private void StartAnimTicking()
    {
        if (_isAnimTicking)
        {
            return;
        }

        _lastRenderTime = DateTimeOffset.UtcNow;
        CompositionTarget.Rendering += OnAnimRendering;
        _isAnimTicking = true;
    }

    private void StopAnimTicking()
    {
        if (!_isAnimTicking)
        {
            return;
        }

        CompositionTarget.Rendering -= OnAnimRendering;
        _isAnimTicking = false;
    }

    private void OnAnimRendering(object? sender, object e)
    {
        var now = DateTimeOffset.UtcNow;
        var dt = (float)(now - _lastRenderTime).TotalSeconds;
        _lastRenderTime = now;

        _sparks.Step(dt);

        if (_sliceLaserAlpha > 0)
        {
            _sliceLaserAlpha = Math.Max(0f, _sliceLaserAlpha - (dt * 2.2f));
        }

        if (_snapRippleAlpha > 0)
        {
            _snapRippleAlpha = Math.Max(0f, _snapRippleAlpha - (dt * 3.2f));
        }

        if (!_sparks.HasLiveParticles && _sliceLaserAlpha <= 0 && _snapRippleAlpha <= 0)
        {
            StopAnimTicking();
        }

        _surface?.Invalidate();
    }

    private double SecondsAt(PointerRoutedEventArgs e)
    {
        var x = e.GetCurrentPoint(RootGrid).Position.X;
        var ratio = Math.Clamp(x / Math.Max(1.0, RootGrid.ActualWidth), 0.0, 1.0);
        return ratio * _item!.DurationSeconds;
    }

    private static string FormatTime(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));

        return span.TotalMinutes >= 1
            ? $"{span.Minutes}:{span.Seconds:D2}.{span.Milliseconds / 100:D1}"
            : $"{span.Seconds}.{span.Milliseconds / 100:D1}s";
    }
}
