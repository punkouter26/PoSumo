using System.Runtime.InteropServices;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace PoChopAudio.WinUI.Common;

public static class WindowHelper
{
    public static nint GetHwnd(this Window window)
    {
        return WinRT.Interop.WindowNative.GetWindowHandle(window);
    }

    public static void InitWithWindow(object target, Window window)
    {
        var hwnd = window.GetHwnd();
        WinRT.Interop.InitializeWithWindow.Initialize(target, hwnd);
    }

    /// <summary>
    /// Sizes the window to a fraction of the monitor's work area and centres it.
    /// <para>
    /// Deliberately does no DPI arithmetic. The previous fixed <c>Resize(1280, 840)</c> opened the
    /// app at roughly 853x560 effective pixels on a 150% display -- far too narrow for the Chop
    /// page, whose right-hand controls were clipped as a result. Correcting that with
    /// <c>GetDpiForWindow</c> is not reliable: during window construction it can report the wrong
    /// scale, because the window has not yet been shown on its target monitor, and the measured
    /// results were not reproducible between runs. <c>DisplayArea.WorkArea</c> and
    /// <c>AppWindow.Resize</c> share a coordinate space, so deriving the size from the work area
    /// needs no conversion and stays correct at any scale factor.
    /// </para>
    /// </summary>
    /// <param name="widthFraction">Share of the work area's width to occupy, 0-1.</param>
    /// <param name="heightFraction">Share of the work area's height to occupy, 0-1.</param>
    public static void SetWindowSizeToWorkAreaFraction(
        this Window window, double widthFraction, double heightFraction)
    {
        var hwnd = window.GetHwnd();
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
        if (appWindow is null)
        {
            return;
        }

        var area = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(
            windowId, Microsoft.UI.Windowing.DisplayAreaFallback.Nearest);
        if (area is null)
        {
            return;
        }

        var work = area.WorkArea;
        var width = (int)(work.Width * Math.Clamp(widthFraction, 0.1, 1.0));
        var height = (int)(work.Height * Math.Clamp(heightFraction, 0.1, 1.0));

        appWindow.MoveAndResize(new RectInt32(
            work.X + ((work.Width - width) / 2),
            work.Y + ((work.Height - height) / 2),
            width,
            height));
    }

    /// <summary>
    /// Refuses to let the user drag the window below a usable size.
    /// <para>
    /// Without this the window resizes to nothing, and the pages have a floor below which no amount
    /// of reflowing helps: the Cutout viewfinder and its shutter button, and the Chop record card,
    /// stop fitting at all. Setting a minimum is cheaper and less surprising than letting content
    /// be clipped or scrolled out of reach.
    /// </para>
    /// <para>
    /// Enforced by pushing the size back rather than by declaring it.
    /// <c>OverlappedPresenter.PreferredMinimumWidth</c> would be the direct way to say this, but it
    /// only exists from Windows App SDK 1.7 and this app is pinned to 1.6 (moving that pin drags
    /// WinUI and Win2D with it — see the Win2D note in CLAUDE.md). The alternative, subclassing the
    /// window procedure to answer <c>WM_GETMINMAXINFO</c>, is a lot of interop for a floor. Watching
    /// <c>Changed</c> and resizing back costs one frame of overshoot while dragging and nothing at
    /// all otherwise.
    /// </para>
    /// <para>
    /// Sizes are in the same coordinate space as <see cref="SetWindowSizeToWorkAreaFraction"/> —
    /// which is <b>physical pixels</b>, not effective ones. That matters here in a way it does not
    /// there: a fraction of the work area is correct at any scale, but a fixed floor is not. Passing
    /// 480x560 gave a real floor of 320x373 effective pixels on a 150% display, well under the size
    /// this is supposed to guarantee. The values are therefore scaled by the window's DPI, read
    /// lazily inside the handler rather than at registration: by the time the user is dragging an
    /// edge the window is on a monitor and <c>GetDpiForWindow</c> answers for that monitor, which
    /// also keeps the floor correct after a drag onto a display at a different scale.
    /// </para>
    /// </summary>
    /// <param name="minWidth">Minimum width in effective (device-independent) pixels.</param>
    /// <param name="minHeight">Minimum height in effective (device-independent) pixels.</param>
    public static void SetMinimumSize(this Window window, int minWidth, int minHeight)
    {
        var hwnd = window.GetHwnd();
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
        if (appWindow is null)
        {
            return;
        }

        // Resizing from inside the Changed handler raises Changed again; without this the first
        // undersized drag recurses until the stack runs out.
        var clamping = false;

        appWindow.Changed += (sender, args) =>
        {
            if (!args.DidSizeChange || clamping)
            {
                return;
            }

            var scale = GetDpiForWindow(hwnd) / 96.0;
            if (scale <= 0)
            {
                scale = 1.0;
            }

            var size = sender.Size;
            var width = Math.Max(size.Width, (int)(minWidth * scale));
            var height = Math.Max(size.Height, (int)(minHeight * scale));

            if (width == size.Width && height == size.Height)
            {
                return;
            }

            clamping = true;
            try
            {
                sender.Resize(new SizeInt32(width, height));
            }
            finally
            {
                clamping = false;
            }
        };
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    public static bool TrySetMicaBackdrop(this Window window)
    {
        if (MicaController.IsSupported())
        {
            window.SystemBackdrop = new MicaBackdrop();
            return true;
        }
        else if (DesktopAcrylicController.IsSupported())
        {
            window.SystemBackdrop = new DesktopAcrylicBackdrop();
            return true;
        }
        return false;
    }
}

