using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PoChopAudio.WinUI.Common;
using PoChopAudio.WinUI.Models;
using Windows.Foundation;
using Windows.UI;

namespace PoChopAudio.WinUI.Controls;

/// <summary>
/// One cut-out photo on a checkerboard, with an optional edge outline and holographic scan FX.
/// </summary>
public sealed partial class CutoutPreview : UserControl
{
    private const float CheckerSize = 10f;

    private static readonly Color CheckerLight = Color.FromArgb(255, 62, 68, 80);
    private static readonly Color CheckerDark = Color.FromArgb(255, 48, 54, 64);
    private static readonly Color EdgeGlowColor = Color.FromArgb(255, 250, 204, 21);

    private struct Voxel
    {
        public float RelX;
        public float OffsetY;
        public float Size;
        public float Speed;
        public int ColorIndex;
    }

    private readonly Voxel[] _voxels = new Voxel[20];
    private CutoutFileItem? _item;
    private CanvasControl? _surface;
    private CanvasBitmap? _cutout;
    private CanvasBitmap? _original;

    private bool _scanning;
    private float _scanPhase;
    private DateTimeOffset _lastScanTick = DateTimeOffset.UtcNow;

    public CutoutPreview()
    {
        InitializeComponent();

        var rng = new Random(42);
        for (var i = 0; i < _voxels.Length; i++)
        {
            _voxels[i] = new Voxel
            {
                RelX = (float)rng.NextDouble(),
                OffsetY = -(float)(rng.NextDouble() * 50.0),
                Size = 3f + (float)(rng.NextDouble() * 3.5),
                Speed = 35f + (float)(rng.NextDouble() * 45.0),
                ColorIndex = rng.Next(2),
            };
        }

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public static readonly DependencyProperty ItemProperty =
        DependencyProperty.Register(nameof(Item), typeof(CutoutFileItem), typeof(CutoutPreview),
            new PropertyMetadata(null, (d, e) => ((CutoutPreview)d).OnItemChanged(e.NewValue as CutoutFileItem)));

    public CutoutFileItem? Item
    {
        get => (CutoutFileItem?)GetValue(ItemProperty);
        set => SetValue(ItemProperty, value);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        EnsureSurface();
        _ = ReloadBitmapAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_item is not null)
        {
            _item.PropertyChanged -= OnItemPropertyChanged;
        }

        StopScanner();
        DisposeBitmap();
        ReleaseSurface();
    }

    /// <summary>
    /// Creates the drawing surface if this control does not currently have one.
    /// <para>
    /// Called from <c>Loaded</c> rather than the constructor because the results list virtualizes:
    /// a card scrolled out of view is unloaded, has its surface released, and is later reloaded
    /// against a different photo. A <see cref="CanvasControl"/> cannot be revived after
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

        // Index 0: the label and the toggle are overlays and have to stay above the drawing.
        RootContainer.Children.Insert(0, _surface);
    }

    private void ReleaseSurface()
    {
        if (_surface is null)
        {
            return;
        }

        _surface.Draw -= OnDraw;
        _surface.RemoveFromVisualTree();
        _surface = null;
    }

    private void OnItemChanged(CutoutFileItem? newItem)
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

        _ = ReloadBitmapAsync();
    }

    private void OnItemPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CutoutFileItem.Status) or nameof(CutoutFileItem.CutoutImage))
        {
            DispatcherQueue.TryEnqueue(() => _ = ReloadBitmapAsync());
        }
    }

    private void OnRedrawRequested(object sender, RoutedEventArgs e) => _surface?.Invalidate();

    /// <summary>
    /// Decodes the cut-out PNG into a Win2D bitmap. Works from the byte array the item already
    /// holds rather than the <c>BitmapImage</c> thumbnail, because a XAML image source cannot be
    /// handed to a drawing session and re-encoding one to get at its pixels would be absurd.
    /// </summary>
    private void StartScanner()
    {
        if (_scanning || !Motion.AnimationsEnabled)
        {
            return;
        }

        _lastScanTick = DateTimeOffset.UtcNow;
        CompositionTarget.Rendering += OnScannerRendering;
        _scanning = true;
    }

    private void StopScanner()
    {
        if (!_scanning)
        {
            return;
        }

        CompositionTarget.Rendering -= OnScannerRendering;
        _scanning = false;
    }

    private void OnScannerRendering(object? sender, object e)
    {
        var now = DateTimeOffset.UtcNow;
        var dt = (float)(now - _lastScanTick).TotalSeconds;
        _lastScanTick = now;

        _scanPhase += dt * 2.4f;

        for (var i = 0; i < _voxels.Length; i++)
        {
            _voxels[i].OffsetY -= _voxels[i].Speed * dt;
            if (_voxels[i].OffsetY < -50f)
            {
                _voxels[i].OffsetY = 0f;
                _voxels[i].RelX = (float)(((i * 37) % 100) / 100.0);
            }
        }

        _surface?.Invalidate();
    }

    /// <summary>
    /// Decodes the cut-out PNG into a Win2D bitmap. Works from the byte array the item already
    /// holds rather than the <c>BitmapImage</c> thumbnail, because a XAML image source cannot be
    /// handed to a drawing session and re-encoding one to get at its pixels would be absurd.
    /// </summary>
    private async Task ReloadBitmapAsync()
    {
        DisposeBitmap();

        if (_item is null)
        {
            StopScanner();
            _surface?.Invalidate();
            return;
        }

        if (_item.Status == ItemProcessingStatus.Analyzing)
        {
            StartScanner();

            if (_original is null && _item.OriginalPngBytes is { Length: > 0 } origBytes)
            {
                try
                {
                    using var origStream = new MemoryStream(origBytes);
                    _original = await CanvasBitmap.LoadAsync(
                        CanvasDevice.GetSharedDevice(), origStream.AsRandomAccessStream());
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    // Fall back to tech grid HUD if original decode fails
                }
            }
        }
        else
        {
            StopScanner();
        }

        try
        {
            if (_item.CutoutPngBytes is { Length: > 0 } cutoutBytes)
            {
                using var stream = new MemoryStream(cutoutBytes);
                _cutout = await CanvasBitmap.LoadAsync(
                    CanvasDevice.GetSharedDevice(), stream.AsRandomAccessStream());
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // A preview that will not decode is not worth an error banner; the card already shows
            // the item's own failure state if the cutout itself failed.
            DisposeBitmap();
        }

        _surface?.Invalidate();
    }

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var session = args.DrawingSession;
        var width = (float)sender.ActualWidth;
        var height = (float)sender.ActualHeight;

        if (width <= 0 || height <= 0)
        {
            return;
        }

        DrawCheckerboard(session, width, height);

        if (_cutout is not null)
        {
            var destination = Fit(_cutout, width, height);
            session.DrawImage(_cutout, destination);

            if (EdgeToggle.IsChecked == true)
            {
                DrawEdgeGlow(session, destination);
            }
        }
        else if (_item?.Status == ItemProcessingStatus.Analyzing)
        {
            DrawHolographicScanner(session, width, height);
        }
    }

    private static void DrawCheckerboard(CanvasDrawingSession session, float width, float height)
    {
        for (var y = 0f; y < height; y += CheckerSize)
        {
            for (var x = 0f; x < width; x += CheckerSize)
            {
                var isLight = (int)((x / CheckerSize) + (y / CheckerSize)) % 2 == 0;
                session.FillRectangle(x, y, CheckerSize, CheckerSize, isLight ? CheckerLight : CheckerDark);
            }
        }
    }

    /// <summary>
    /// Outlines the mask boundary by running an edge-detection effect over the cut-out and tinting
    /// what survives. The alpha channel is where the boundary lives, so the source is the cut-out
    /// itself rather than anything derived.
    /// </summary>
    private void DrawEdgeGlow(CanvasDrawingSession session, Rect destination)
    {
        using var edges = new EdgeDetectionEffect
        {
            Source = _cutout,
            Amount = 0.6f,
            BlurAmount = 0.4f,
            Mode = EdgeDetectionEffectMode.Sobel,
        };

        using var tinted = new ColorMatrixEffect
        {
            Source = edges,
            ColorMatrix = new Matrix5x4
            {
                // Collapse whatever the detector produced onto one colour, keeping its strength as
                // alpha, so the outline reads as a single highlight rather than a rainbow fringe.
                M11 = 0, M12 = 0, M13 = 0, M14 = 0,
                M21 = 0, M22 = 0, M23 = 0, M24 = 1,
                M31 = 0, M32 = 0, M33 = 0, M34 = 0,
                M41 = 0, M42 = 0, M43 = 0, M44 = 0,
                M51 = EdgeGlowColor.R / 255f,
                M52 = EdgeGlowColor.G / 255f,
                M53 = EdgeGlowColor.B / 255f,
                M54 = 0,
            },
        };

        // An effect has no intrinsic size the way a bitmap does, so the source rectangle has to say
        // which part of it to sample; the cut-out's own bounds are that region.
        session.DrawImage(tinted, destination, _cutout!.Bounds);
    }

    /// <summary>Letterboxes <paramref name="bitmap"/> into the control, preserving aspect ratio.</summary>
    private static Rect Fit(CanvasBitmap bitmap, float width, float height)
    {
        var source = bitmap.Size;

        if (source.Width <= 0 || source.Height <= 0)
        {
            return new Rect(0, 0, width, height);
        }

        var scale = Math.Min(width / source.Width, height / source.Height);
        var drawWidth = source.Width * scale;
        var drawHeight = source.Height * scale;

        return new Rect((width - drawWidth) / 2, (height - drawHeight) / 2, drawWidth, drawHeight);
    }

    private void DrawHolographicScanner(CanvasDrawingSession session, float width, float height)
    {
        Rect destination;
        if (_original is not null)
        {
            destination = Fit(_original, width, height);
            session.DrawImage(_original, destination);
            session.FillRectangle(destination, Color.FromArgb(32, 6, 182, 212));
        }
        else
        {
            var boxW = width * 0.72f;
            var boxH = height * 0.72f;
            destination = new Rect((width - boxW) / 2f, (height - boxH) / 2f, boxW, boxH);
            session.FillRectangle(destination, Color.FromArgb(24, 56, 189, 248));
        }

        DrawHudBrackets(session, destination);

        var progress = (MathF.Sin(_scanPhase) * 0.5f) + 0.5f;
        var scanY = (float)(destination.Y + (destination.Height * progress));

        // Soft ambient laser glow band
        var bandTop = MathF.Max((float)destination.Y, scanY - 14f);
        var bandBottom = MathF.Min((float)(destination.Y + destination.Height), scanY + 14f);
        if (bandBottom > bandTop)
        {
            session.FillRectangle((float)destination.X, bandTop, (float)destination.Width, bandBottom - bandTop, Color.FromArgb(42, 6, 182, 212));
        }

        // Intense core laser beam
        session.DrawLine((float)destination.X, scanY, (float)(destination.X + destination.Width), scanY, Color.FromArgb(245, 240, 253, 250), 2f);
        session.DrawLine((float)destination.X, scanY, (float)(destination.X + destination.Width), scanY, Color.FromArgb(130, 56, 189, 248), 5f);

        // Voxel dissolution particles drifting upward
        foreach (var voxel in _voxels)
        {
            var vx = (float)(destination.X + (destination.Width * voxel.RelX));
            var vy = scanY + voxel.OffsetY;
            if (vy >= destination.Y && vy <= destination.Y + destination.Height)
            {
                var alpha = Math.Clamp(1f - (MathF.Abs(voxel.OffsetY) / 50f), 0f, 1f);
                var color = voxel.ColorIndex == 0
                    ? Color.FromArgb((byte)(220 * alpha), 56, 189, 248)
                    : Color.FromArgb((byte)(220 * alpha), 52, 211, 153);
                session.FillRectangle(vx, vy, voxel.Size, voxel.Size, color);
            }
        }
    }

    private static void DrawHudBrackets(CanvasDrawingSession session, Rect bounds)
    {
        var x = (float)bounds.X;
        var y = (float)bounds.Y;
        var w = (float)bounds.Width;
        var h = (float)bounds.Height;
        const float arm = 14f;
        var col = Color.FromArgb(180, 56, 189, 248);
        const float stroke = 2f;

        // Top-left
        session.DrawLine(x, y, x + arm, y, col, stroke);
        session.DrawLine(x, y, x, y + arm, col, stroke);

        // Top-right
        session.DrawLine(x + w, y, x + w - arm, y, col, stroke);
        session.DrawLine(x + w, y, x + w, y + arm, col, stroke);

        // Bottom-left
        session.DrawLine(x, y + h, x + arm, y + h, col, stroke);
        session.DrawLine(x, y + h, x, y + h - arm, col, stroke);

        // Bottom-right
        session.DrawLine(x + w, y + h, x + w - arm, y + h, col, stroke);
        session.DrawLine(x + w, y + h, x + w, y + h - arm, col, stroke);
    }

    private void DisposeBitmap()
    {
        _cutout?.Dispose();
        _cutout = null;
        _original?.Dispose();
        _original = null;
    }
}
