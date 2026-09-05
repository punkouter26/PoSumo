using UnityEngine;
using UnityEngine.UIElements;

namespace PoSumo
{
    /// Resolves the two typefaces the UI is designed around and hands them to
    /// `Systems_UiKit`, which is the single place any label in this game is built.
    ///
    /// **The problem this exists to solve.** `Assets/UI Toolkit/Fonts/` is empty
    /// and always has been, so every screen — the 112pt countdown digit, the score
    /// digits, the result banner, the whole bracket — renders in Unity's default
    /// UI font. The project's own UI README calls this out as the biggest single
    /// visual win left, and it is: nothing else changes the look of every screen
    /// at once for so little risk.
    ///
    /// **Why this is a resolver and not just a serialized field.** Two faces have
    /// to arrive from somewhere, and a font is a licensing decision that belongs
    /// to whoever ships the game, not to whoever writes the loader. So this looks
    /// in three places, in order, and every one of them is allowed to come up
    /// empty:
    ///
    /// 1. `Resources/Fonts/<name>.ttf|otf` — drop a file in and it is used. This
    ///    is the intended path and needs no Editor work at all, which is the
    ///    reason it is a plain `Font` from Resources rather than a TextMeshPro
    ///    Font Asset: an SDF asset has to be generated in the Editor, and a UI
    ///    that silently falls back because someone forgot that step is exactly
    ///    the failure mode `Systems_UiKit` keeps its tokens in C# to avoid.
    /// 2. An OS font by family name, for the Editor and for desktop, so the look
    ///    can be judged before anyone has committed to licensing anything.
    /// 3. Nothing — `Face` returns null, the kit writes no font, and every label
    ///    renders exactly as it does today. A missing font must never be an error
    ///    or a blank screen.
    ///
    /// Resolution happens ONCE, lazily, and is cached including the null result;
    /// `Font.CreateDynamicFontFromOSFont` is not free and the kit calls into this
    /// for every label it builds.
    public static class Systems_UiFonts
    {
        /// Where a dropped-in font is looked for. `Resources.Load` takes no
        /// extension — a `.ttf` and a `.otf` are both found by the same path.
        private const string RESOURCE_DIR = "Fonts/";

        /// File names (no extension) checked under `Resources/Fonts/`, in order.
        private static readonly string[] DisplayResourceNames =
        {
            "PoSumo_Display", "Display", "Title",
        };

        private static readonly string[] UiResourceNames =
        {
            "PoSumo_UI", "UI", "Body",
        };

        /// OS families tried when no font has been dropped in. Condensed bold
        /// faces first for the display role — the score digits and the countdown
        /// want width discipline, not a text face scaled up. Every one of these is
        /// a stock Windows or Android family; `CreateDynamicFontFromOSFont`
        /// returns a usable fallback rather than null when a name is absent, which
        /// is why the result is checked against the requested name below.
        /// Windows/desktop families first, then the families Android actually
        /// ships. Android's font stack really does expose Roboto (and Noto Sans
        /// as the fallback family), so the OS path is worth taking there rather
        /// than dropping straight to the theme default — "Roboto Condensed" in
        /// particular is a genuine condensed display face and is present on the
        /// overwhelming majority of devices.
        private static readonly string[] DisplayOsNames =
        {
            "Bahnschrift Condensed", "Bahnschrift", "Arial Black",
            "Impact", "Oswald",
            "Roboto Condensed", "RobotoCondensed-Bold", "Roboto-Black",
            "Noto Sans", "Roboto",
        };

        private static readonly string[] UiOsNames =
        {
            "Segoe UI", "Inter", "Arial",
            "Roboto", "Roboto-Regular", "Noto Sans", "Droid Sans",
        };

        /// Size at or above which a label is DISPLAY type. FONT_TITLE (34) is the
        /// score digits and card titles; everything from there up is the game
        /// shouting, and everything below it is the game talking.
        public const int DISPLAY_FROM = Systems_UiKit.FONT_TITLE;

        private static Font _display;
        private static Font _ui;
        private static bool _resolved;

        /// Mandatory for static state in this project — Enter Play Mode domain
        /// reload is disabled, so a static survives between Play sessions.
        ///
        /// It matters more here than the usual bookkeeping. A font from
        /// `CreateDynamicFontFromOSFont` is a RUNTIME object with no backing
        /// asset, and Unity destroys it when Play mode exits — the Editor logs
        /// "Deleting invalid font reference" when it does. Without this reset
        /// `_resolved` would still be true on the next Play session while
        /// `_display` pointed at a destroyed object, so `Face` would hand back a
        /// Unity-null and every label would silently fall back to the default
        /// font. It would look exactly like the feature having been reverted, and
        /// it would only ever happen on the SECOND run.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatic()
        {
            _display = null;
            _ui = null;
            _resolved = false;
        }

        /// The face for a label of this size, or null when nothing resolved — in
        /// which case the caller must write no font at all and let the runtime
        /// theme's default stand.
        public static Font Face(int fontSize)
        {
            Resolve();
            return fontSize >= DISPLAY_FROM ? _display : _ui;
        }

        /// Applies the right face for `fontSize` to `element`, or leaves the
        /// element untouched when no font resolved. Safe to call on every label.
        public static void Apply(VisualElement element, int fontSize)
        {
            Font face = Face(fontSize);
            if (face == null || element == null)
            {
                return;
            }
            element.style.unityFontDefinition = FontDefinition.FromFont(face);
        }

        /// One line describing what actually resolved, for the DBG overlay — a
        /// font silently falling back is invisible otherwise, and "the UI looks
        /// wrong" is a hard thing to diagnose from a screenshot.
        public static string Describe()
        {
            Resolve();
            return "display=" + (_display != null ? _display.name : "<default>")
                 + " ui=" + (_ui != null ? _ui.name : "<default>");
        }

        private static void Resolve()
        {
            if (_resolved)
            {
                return;
            }
            // Set BEFORE the work, not after: every path below can fail, and a
            // failure must still count as resolved or the kit re-runs the OS font
            // probe for every label on every screen.
            _resolved = true;

            _display = FromResources(DisplayResourceNames) ?? FromOs(DisplayOsNames);
            _ui = FromResources(UiResourceNames) ?? FromOs(UiOsNames);

            // The display face is the one that matters; if only one resolved, use
            // it for both rather than mixing a real face with the theme default,
            // which reads as a bug rather than as a choice.
            if (_display == null && _ui != null)
            {
                _display = _ui;
            }
            else if (_ui == null && _display != null)
            {
                _ui = _display;
            }
        }

        private static Font FromResources(string[] names)
        {
            for (int nameIndex = 0; nameIndex < names.Length; nameIndex++)
            {
                var font = Resources.Load<Font>(RESOURCE_DIR + names[nameIndex]);
                if (font != null)
                {
                    return font;
                }
            }
            return null;
        }

        private static Font FromOs(string[] families)
        {
            // Runs on Android too. `GetOSInstalledFontNames` is supported there
            // and the platform genuinely exposes Roboto and Noto, so refusing to
            // look would ship the default theme font on the one platform this
            // game actually targets. It stays a FALLBACK — the Resources path is
            // checked first and is the only one under our control, because what a
            // device has installed is not guaranteed by anything.
            string[] installed = Font.GetOSInstalledFontNames();
            if (installed == null || installed.Length == 0)
            {
                return null;
            }
            for (int familyIndex = 0; familyIndex < families.Length; familyIndex++)
            {
                string wanted = families[familyIndex];
                // GetOSInstalledFontNames is checked rather than trusting
                // CreateDynamicFontFromOSFont's return: that call hands back a
                // working fallback font for a name that is not installed, so it
                // never reports failure and the first family in the list would
                // always appear to win.
                for (int installedIndex = 0; installedIndex < installed.Length; installedIndex++)
                {
                    if (!string.Equals(installed[installedIndex], wanted,
                                       System.StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    Font font = Font.CreateDynamicFontFromOSFont(wanted, 32);
                    if (font != null)
                    {
                        return font;
                    }
                }
            }
            return null;
        }
    }
}
