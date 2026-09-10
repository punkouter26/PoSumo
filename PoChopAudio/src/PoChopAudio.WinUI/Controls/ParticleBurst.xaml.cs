using System.Numerics;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PoChopAudio.Services.Dsp;
using PoChopAudio.WinUI.Common;
using Windows.Foundation;
using Windows.UI;

namespace PoChopAudio.WinUI.Controls;

/// <summary>
/// A short confetti burst, drawn on the GPU and simulated by <see cref="ParticleField"/>.
///
/// <para>
/// Scoped hard on purpose. This app records and processes audio, and CPU contention during capture
/// shows up as dropped frames in someone's take — so the particle count is capped, the simulation
/// runs only while something is alive, and the whole thing is skipped outright when the system asks
/// for reduced motion or when <see cref="IsSuppressed"/> says the microphone is open.
/// </para>
/// <para>
/// The simulation itself lives in Services because it is the only part with any logic worth
/// testing; this class is the surface it is drawn onto and nothing else.
/// </para>
/// </summary>
public sealed partial class ParticleBurst : UserControl
{
    /// <summary>Hard ceiling. Enough to read as celebratory, far too few to cost anything.</summary>
    private const int MaxParticles = 160;

    private static readonly Color[] Palette =
    [
        Color.FromArgb(255, 96, 165, 250),
        Color.FromArgb(255, 52, 211, 153),
        Color.FromArgb(255, 251, 191, 36),
        Color.FromArgb(255, 244, 114, 182),
    ];

    private readonly ParticleField _field = new(MaxParticles);

    private bool _ticking;
    private DateTimeOffset _lastFrame = DateTimeOffset.UtcNow;

    public ParticleBurst()
    {
        InitializeComponent();

        Unloaded += (_, _) =>
        {
            StopTicking();
            Surface.RemoveFromVisualTree();
        };
    }

    /// <summary>Set while the microphone is live. Blocks bursts outright.</summary>
    public bool IsSuppressed { get; set; }

    /// <summary>
    /// Fires a celebratory burst from the bottom centre of the control. Dual-stage:
    /// primary fountain launch followed by a wide sparkling canopy.
    /// Does nothing when suppressed or when the system has asked for reduced motion.
    /// </summary>
    public void Burst(int count = 90)
    {
        if (IsSuppressed || !Motion.AnimationsEnabled)
        {
            return;
        }

        var width = (float)ActualWidth;
        var height = (float)ActualHeight;

        if (width <= 0 || height <= 0)
        {
            return;
        }

        // Primary upward fountain launch
        _field.Emit(
            width / 2f,
            height * 0.75f,
            (int)(count * 0.65f),
            speed: Math.Max(320f, height * 1.35f),
            directionRadians: -MathF.PI / 2f,
            spreadRadians: MathF.PI * 0.7f,
            lifetimeSeconds: 1.35f,
            paletteSize: Palette.Length);

        // Stage 2: secondary wider sparkle canopy
        _field.Emit(
            width / 2f,
            height * 0.68f,
            (int)(count * 0.35f),
            speed: Math.Max(200f, height * 0.95f),
            directionRadians: -MathF.PI / 2f,
            spreadRadians: MathF.PI * 1.15f,
            lifetimeSeconds: 1.1f,
            paletteSize: Palette.Length);

        StartTicking();
    }

    /// <summary>
    /// Emits localized directional sparks, e.g. for guillotine slices or button impacts.
    /// </summary>
    public void EmitSparks(float x, float y, int count = 25)
    {
        if (IsSuppressed || !Motion.AnimationsEnabled)
        {
            return;
        }

        _field.EmitSparks(x, y, count, speed: 200f, paletteSize: Palette.Length);
        StartTicking();
    }

    public void Clear()
    {
        _field.Clear();
        StopTicking();
        Surface.Invalidate();
    }

    private void StartTicking()
    {
        if (_ticking)
        {
            return;
        }

        _lastFrame = DateTimeOffset.UtcNow;
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

    /// <summary>
    /// Advances the simulation once per composed frame and stops subscribing the moment the last
    /// particle dies, so an idle page costs nothing at all.
    /// </summary>
    private void OnRendering(object? sender, object e)
    {
        var now = DateTimeOffset.UtcNow;
        _field.Step((float)(now - _lastFrame).TotalSeconds);
        _lastFrame = now;

        if (!_field.HasLiveParticles)
        {
            StopTicking();
        }

        Surface.Invalidate();
    }

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var session = args.DrawingSession;
        var oldTransform = session.Transform;

        foreach (ref readonly var particle in _field.Alive)
        {
            var colour = Palette[particle.ColorIndex % Palette.Length];
            var fade = particle.Remaining;

            // Fade and shrink together; a particle that only fades leaves a visible ghost of its
            // full size at the last frame.
            var faded = Color.FromArgb((byte)(255 * fade), colour.R, colour.G, colour.B);
            var baseSize = particle.Size * (0.35f + (0.65f * fade));

            var aspect = particle.AspectRatio > 0f ? particle.AspectRatio : 1.6f;
            var w = baseSize;
            // 3D tumbling / flutter effect: scale height by cosine of tumbling phase
            var flutter = MathF.Cos((particle.Age * 9f) + particle.ColorIndex);
            var h = MathF.Max(1.5f, baseSize * aspect * MathF.Abs(flutter));

            session.Transform = Matrix3x2.CreateRotation(particle.Rotation, new Vector2(particle.X, particle.Y)) * oldTransform;

            session.FillRoundedRectangle(
                new Rect(particle.X - (w / 2f), particle.Y - (h / 2f), w, h),
                1.5f,
                1.5f,
                faded);
        }

        session.Transform = oldTransform;
    }
}
