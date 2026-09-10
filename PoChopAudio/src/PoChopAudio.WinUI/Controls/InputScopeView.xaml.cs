using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using Windows.UI;

namespace PoChopAudio.WinUI.Controls;

/// <summary>
/// A rolling view of the microphone signal, newest on the right, with proper meter ballistics.
///
/// <para>
/// This replaces a <c>ProgressBar</c>. A bar answers "how loud right now" and nothing else; the
/// question someone setting up a take actually has is "was that consistent, and did anything spike"
/// — which needs history. The ring buffer holds a few seconds of it.
/// </para>
/// <para>
/// The ballistics are the standard ones and they are not decoration: <b>peak</b> attacks instantly
/// and falls slowly, so a single clipped consonant stays visible long enough to notice, while
/// <b>RMS</b> follows the body of the sound. A meter that decays as fast as the audio does is a
/// meter you cannot read.
/// </para>
/// </summary>
public sealed partial class InputScopeView : UserControl
{
    /// <summary>Roughly four seconds of 25 ms buffers at 48 points each.</summary>
    private const int Capacity = 1024;

    /// <summary>How far the peak-hold marker falls per second, in normalised amplitude.</summary>
    private const float PeakFallPerSecond = 0.55f;

    private static readonly Color TraceColor = Color.FromArgb(230, 96, 165, 250);
    private static readonly Color TraceHotColor = Color.FromArgb(230, 249, 115, 22);
    private static readonly Color TraceClipColor = Color.FromArgb(240, 239, 68, 68);
    private static readonly Color RmsColor = Color.FromArgb(120, 34, 197, 94);
    private static readonly Color GridColor = Color.FromArgb(40, 148, 163, 184);

    private readonly float[] _points = new float[Capacity];
    private int _head;
    private int _filled;

    private float _peakHold;
    private DateTimeOffset _lastFall = DateTimeOffset.UtcNow;
    private double _peakDb = -100;
    private double _rmsDb = -100;
    private bool _isClipping;
    private CanvasTextFormat? _readoutFormat;

    public InputScopeView()
    {
        InitializeComponent();
        Unloaded += (_, _) => Surface.RemoveFromVisualTree();
    }

    /// <summary>Appends one buffer's worth of decimated peaks. Safe to call from the capture thread.</summary>
    public void Push(float[] points)
    {
        ArgumentNullException.ThrowIfNull(points);

        DispatcherQueue.TryEnqueue(() =>
        {
            foreach (var point in points)
            {
                _points[_head] = point;
                _head = (_head + 1) % Capacity;
                _filled = Math.Min(Capacity, _filled + 1);
                _peakHold = Math.Max(_peakHold, point);
            }

            IdleText.Visibility = Visibility.Collapsed;
            Surface.Invalidate();
        });
    }

    /// <summary>Updates the numeric readout and the clip badge.</summary>
    public void UpdateLevel(double peakDb, double rmsDb, bool isClipping)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _peakDb = peakDb;
            _rmsDb = rmsDb;
            _isClipping = isClipping;

            ClipBadge.Visibility = isClipping ? Visibility.Visible : Visibility.Collapsed;

            AutomationProperties.SetName(
                Surface,
                isClipping
                    ? $"Live input scope, clipping, peak {peakDb:F0} decibels"
                    : $"Live input scope, peak {peakDb:F0} decibels");

            Surface.Invalidate();
        });
    }

    public void Reset()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            Array.Clear(_points);
            _head = 0;
            _filled = 0;
            _peakHold = 0;
            _peakDb = -100;
            _rmsDb = -100;
            _isClipping = false;
            ClipBadge.Visibility = Visibility.Collapsed;
            IdleText.Visibility = Visibility.Visible;
            Surface.Invalidate();
        });
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

        var midY = height / 2f;

        // -6 dB guides. Anything reaching the outer pair is close to clipping.
        foreach (var level in stackalloc[] { 0.5f, 1f })
        {
            session.DrawLine(0, midY - (midY * level * 0.92f), width, midY - (midY * level * 0.92f), GridColor);
            session.DrawLine(0, midY + (midY * level * 0.92f), width, midY + (midY * level * 0.92f), GridColor);
        }

        session.DrawLine(0, midY, width, midY, GridColor);

        if (_filled == 0)
        {
            return;
        }

        DecayPeakHold();

        // Newest sample on the right. Older data scrolls off the left, which is the direction the
        // eye already expects from every other meter and DAW.
        var visible = Math.Min(_filled, Capacity);
        var step = width / visible;

        for (var i = 0; i < visible; i++)
        {
            var index = (_head - visible + i + Capacity) % Capacity;
            var amplitude = Math.Clamp(_points[index], 0f, 1f);
            var barHeight = Math.Max(1f, amplitude * (height - 6f));
            var x = i * step;

            var color = amplitude >= 0.98f ? TraceClipColor
                : amplitude > 0.7f ? TraceHotColor
                : TraceColor;

            session.DrawLine(x, midY - (barHeight / 2f), x, midY + (barHeight / 2f), color, Math.Max(1f, step));
        }

        if (_peakHold > 0.01f)
        {
            var y = _peakHold * (height - 6f) / 2f;
            session.DrawLine(0, midY - y, width, midY - y, TraceClipColor, 1f);
            session.DrawLine(0, midY + y, width, midY + y, TraceClipColor, 1f);
        }

        DrawReadout(session, width, height);
    }

    private void DrawReadout(CanvasDrawingSession session, float width, float height)
    {
        _readoutFormat ??= new CanvasTextFormat
        {
            FontSize = 11,
            FontFamily = "Consolas",
            WordWrapping = CanvasWordWrapping.NoWrap,
        };

        var text = _peakDb <= -99
            ? "-∞ dB"
            : $"pk {_peakDb,6:F1}  rms {_rmsDb,6:F1} dB";

        session.FillRectangle(new Rect(4, height - 18, 190, 15), Color.FromArgb(90, 0, 0, 0));
        session.DrawText(text, new Vector2(8, height - 17), _isClipping ? TraceClipColor : RmsColor, _readoutFormat);
    }

    private void DecayPeakHold()
    {
        var now = DateTimeOffset.UtcNow;
        var elapsed = (float)(now - _lastFall).TotalSeconds;
        _lastFall = now;

        _peakHold = Math.Max(0f, _peakHold - (PeakFallPerSecond * Math.Min(elapsed, 0.5f)));
    }
}
