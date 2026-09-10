using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Windows.UI.ViewManagement;

namespace PoChopAudio.WinUI.Common;

/// <summary>
/// Composition animation helpers, and the one setting every one of them has to obey.
///
/// <para>
/// <see cref="AnimationsEnabled"/> is not a nicety. Windows exposes "Show animations" precisely
/// because motion makes some people ill, and an app that animates anyway has taken that choice away
/// from them. Every animated thing in this project routes through here, so honouring the setting is
/// one check rather than a rule everyone has to remember.
/// </para>
/// <para>
/// Implicit animations are the mechanism throughout: they run on the compositor thread, so a list
/// settling into place stays smooth while the UI thread is busy rendering an export, and they need
/// no storyboard per element.
/// </para>
/// </summary>
public static class Motion
{
    private static readonly UISettings Settings = new();

    /// <summary>
    /// Whether the system wants animation. Read fresh each time rather than cached: the user can
    /// change it while the app is open, and a cached "yes" would keep moving after they said stop.
    /// </summary>
    public static bool AnimationsEnabled
    {
        get
        {
            try
            {
                return Settings.AnimationsEnabled;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // The setting is unreadable on some configurations; assume motion is unwanted
                // rather than assuming consent.
                return false;
            }
        }
    }

    /// <summary>
    /// Makes <paramref name="element"/> fade and slide when it is added to or removed from a
    /// panel, and settle smoothly when siblings move it. Safe to call more than once.
    /// </summary>
    public static void EnableListItemAnimations(UIElement element, double offsetY = 18, double milliseconds = 260)
    {
        ArgumentNullException.ThrowIfNull(element);

        var visual = ElementCompositionPreview.GetElementVisual(element);
        var compositor = visual.Compositor;

        if (!AnimationsEnabled)
        {
            ElementCompositionPreview.SetImplicitShowAnimation(element, null);
            ElementCompositionPreview.SetImplicitHideAnimation(element, null);
            visual.ImplicitAnimations = null;
            return;
        }

        var duration = TimeSpan.FromMilliseconds(milliseconds);
        var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1f));

        var fadeIn = compositor.CreateScalarKeyFrameAnimation();
        fadeIn.InsertKeyFrame(0f, 0f);
        fadeIn.InsertKeyFrame(1f, 1f, easing);
        fadeIn.Duration = duration;
        fadeIn.Target = nameof(Visual.Opacity);

        var slideIn = compositor.CreateVector3KeyFrameAnimation();
        slideIn.InsertKeyFrame(0f, new Vector3(0f, (float)offsetY, 0f));
        slideIn.InsertKeyFrame(1f, Vector3.Zero, easing);
        slideIn.Duration = duration;

        // "Translation" is an attached composition property rather than a member of Visual, so it
        // is targeted by name; SetIsTranslationEnabled below is what brings it into existence.
        slideIn.Target = "Translation";

        var show = compositor.CreateAnimationGroup();
        show.Add(fadeIn);
        show.Add(slideIn);

        var fadeOut = compositor.CreateScalarKeyFrameAnimation();
        fadeOut.InsertKeyFrame(1f, 0f, easing);
        fadeOut.Duration = TimeSpan.FromMilliseconds(milliseconds * 0.6);
        fadeOut.Target = nameof(Visual.Opacity);

        // Translation is a separate property from Offset and has to be opted into per element,
        // otherwise the slide silently does nothing.
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        ElementCompositionPreview.SetImplicitShowAnimation(element, show);
        ElementCompositionPreview.SetImplicitHideAnimation(element, fadeOut);

        // Repositioning when a sibling above is removed.
        var reposition = compositor.CreateVector3KeyFrameAnimation();
        reposition.InsertExpressionKeyFrame(1f, "this.FinalValue", easing);
        reposition.Duration = duration;
        reposition.Target = nameof(Visual.Offset);

        var implicits = compositor.CreateImplicitAnimationCollection();
        implicits[nameof(Visual.Offset)] = reposition;
        visual.ImplicitAnimations = implicits;
    }

    /// <summary>
    /// A short spring on scale, for a control that should feel like it responds to being pressed.
    /// Does nothing when animation is off.
    /// </summary>
    public static void Pulse(UIElement element, float to = 1.04f, double milliseconds = 180)
    {
        ArgumentNullException.ThrowIfNull(element);

        if (!AnimationsEnabled)
        {
            return;
        }

        var visual = ElementCompositionPreview.GetElementVisual(element);
        var compositor = visual.Compositor;

        if (element is FrameworkElement { ActualWidth: > 0, ActualHeight: > 0 } sized)
        {
            // Without a centred origin the element grows out of its top-left corner, which reads as
            // a jolt rather than a press.
            visual.CenterPoint = new Vector3((float)sized.ActualWidth / 2f, (float)sized.ActualHeight / 2f, 0f);
        }

        var pulse = compositor.CreateVector3KeyFrameAnimation();
        pulse.InsertKeyFrame(0f, Vector3.One);
        pulse.InsertKeyFrame(0.5f, new Vector3(to, to, 1f));
        pulse.InsertKeyFrame(1f, Vector3.One);
        pulse.Duration = TimeSpan.FromMilliseconds(milliseconds);

        visual.StartAnimation(nameof(Visual.Scale), pulse);
    }

    /// <summary>
    /// Attaches 2.5D tilt tracking to <paramref name="card"/> using GPU composition rotation.
    /// Resets cleanly on pointer exit and obeys <see cref="AnimationsEnabled"/>.
    /// </summary>
    public static void EnableCardTilt(FrameworkElement card, Action? onHover = null)
    {
        ArgumentNullException.ThrowIfNull(card);

        var visual = ElementCompositionPreview.GetElementVisual(card);
        var compositor = visual.Compositor;

        var lastHover = DateTimeOffset.MinValue;

        card.PointerEntered += (_, _) =>
        {
            if (!AnimationsEnabled)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            if (onHover is not null && (now - lastHover).TotalMilliseconds > 350)
            {
                lastHover = now;
                onHover();
            }
        };

        card.PointerMoved += (_, e) =>
        {
            if (!AnimationsEnabled)
            {
                return;
            }

            var w = (float)card.ActualWidth;
            var h = (float)card.ActualHeight;
            if (w <= 0 || h <= 0)
            {
                return;
            }

            var pos = e.GetCurrentPoint(card).Position;
            var normX = Math.Clamp(((float)pos.X - (w / 2f)) / (w / 2f), -1f, 1f);
            var normY = Math.Clamp(((float)pos.Y - (h / 2f)) / (h / 2f), -1f, 1f);

            visual.CenterPoint = new Vector3(w / 2f, h / 2f, 0f);
            visual.RotationAxis = new Vector3(-normY, normX, 0f);
            var dist = MathF.Sqrt((normX * normX) + (normY * normY));
            visual.RotationAngleInDegrees = Math.Min(dist * 2.8f, 3.5f);
        };

        card.PointerExited += (_, _) =>
        {
            if (!AnimationsEnabled)
            {
                visual.RotationAngleInDegrees = 0f;
                return;
            }

            var returnAnim = compositor.CreateScalarKeyFrameAnimation();
            returnAnim.InsertKeyFrame(1f, 0f, compositor.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1f)));
            returnAnim.Duration = TimeSpan.FromMilliseconds(220);
            visual.StartAnimation(nameof(Visual.RotationAngleInDegrees), returnAnim);
        };
    }
}
