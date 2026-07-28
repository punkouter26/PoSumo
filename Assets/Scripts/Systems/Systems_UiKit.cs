using UnityEngine;
using UnityEngine.UIElements;

namespace PoSumo
{
    /// The project's single set of UI design tokens, plus the builders that apply
    /// them.
    ///
    /// Before this existed the four UI files carried 325 inline `.style.` writes
    /// between them, using twenty distinct font sizes and fourteen padding values
    /// with no ratio between any of them — every card re-derived its own look, and
    /// the same four-line corner-radius block was pasted in four places.
    ///
    /// UI Toolkit would normally put this in a `.uss` stylesheet, and that is the
    /// better answer once the UI is authored as assets. It is C# here on purpose:
    /// every screen in this project is built from code at runtime, a StyleSheet has
    /// to be imported by the editor and then resolved by GUID or Resources path,
    /// and a stylesheet that silently fails to load leaves the game with unstyled
    /// UI. Tokens in code cannot fail to load and are checked by the compiler.
    ///
    /// Rules for using it: pick a size from the type scale, a gap from the space
    /// scale, and a colour from the palette. If a screen needs a value that is not
    /// here, add it here rather than inline.
    public static class Systems_UiKit
    {
        // ---- Type scale ----------------------------------------------------
        // Seven steps on a ~1.27 ratio. Replaces the previous twenty ad-hoc sizes.
        public const int FONT_MICRO = 14;   // footnotes, table footers
        public const int FONT_SMALL = 17;   // stat captions, hints
        public const int FONT_BODY = 21;    // stat values, fighter names
        public const int FONT_LEAD = 27;    // clock, secondary card lines
        public const int FONT_TITLE = 34;   // score digits, card titles
        public const int FONT_HERO = 46;    // result title, round banner
        public const int FONT_MEGA = 112;   // the countdown digit, and nothing else

        // ---- Space scale, 4pt grid -----------------------------------------
        public const int SPACE_1 = 4;
        public const int SPACE_2 = 8;
        public const int SPACE_3 = 12;
        public const int SPACE_4 = 16;
        public const int SPACE_5 = 24;

        // ---- Corner radii ---------------------------------------------------
        public const int RADIUS_SM = 8;
        public const int RADIUS_MD = 12;
        public const int RADIUS_LG = 16;

        /// Minimum comfortable thumb target in points, per both platforms' HIGs.
        /// The old STATS chip was 34 and the pause button 44 — this is the number
        /// that disagreement should have been resolved against.
        public const int TOUCH_MIN = 44;

        // ---- Palette --------------------------------------------------------
        public static readonly Color Ink = new Color(0.05f, 0.045f, 0.06f, 0.95f);
        public static readonly Color Panel = new Color(0.04f, 0.04f, 0.06f, 0.78f);
        public static readonly Color Chip = new Color(0.12f, 0.11f, 0.13f, 0.8f);
        public static readonly Color Line = new Color(1f, 1f, 1f, 0.12f);
        public static readonly Color Track = new Color(0.2f, 0.2f, 0.24f);
        public static readonly Color TextHi = new Color(0.96f, 0.95f, 0.92f);
        public static readonly Color TextMid = new Color(0.8f, 0.77f, 0.72f);
        public static readonly Color TextLow = new Color(0.62f, 0.6f, 0.56f);
        public static readonly Color Gold = new Color(1f, 0.85f, 0.3f);
        public static readonly Color OnGold = new Color(0.08f, 0.06f, 0.05f);
        public static readonly Color Good = new Color(0.4f, 0.82f, 0.45f);
        public static readonly Color Warn = new Color(0.9f, 0.75f, 0.25f);
        public static readonly Color Bad = new Color(0.92f, 0.42f, 0.32f);
        public static readonly Color Backdrop = new Color(0f, 0f, 0f, 0.72f);

        /// Drop shadow for text that sits over the arena rather than over a card.
        public static TextShadow Outline => new TextShadow
        {
            offset = new Vector2(0f, 2f),
            blurRadius = 4f,
            color = new Color(0f, 0f, 0f, 0.85f),
        };

        // ---- Primitives -----------------------------------------------------

        public static T Round<T>(this T element, int radius) where T : VisualElement
        {
            element.style.borderTopLeftRadius = radius;
            element.style.borderTopRightRadius = radius;
            element.style.borderBottomLeftRadius = radius;
            element.style.borderBottomRightRadius = radius;
            return element;
        }

        public static T Pad<T>(this T element, int horizontal, int vertical) where T : VisualElement
        {
            element.style.paddingLeft = horizontal;
            element.style.paddingRight = horizontal;
            element.style.paddingTop = vertical;
            element.style.paddingBottom = vertical;
            return element;
        }

        /// Stretches an element over its parent's whole box.
        public static T Fill<T>(this T element) where T : VisualElement
        {
            element.style.position = Position.Absolute;
            element.style.left = 0;
            element.style.right = 0;
            element.style.top = 0;
            element.style.bottom = 0;
            return element;
        }

        /// Marks an element as scenery: it is drawn but never hit-tested, so it
        /// cannot swallow a tap meant for a button behind or below it. Children
        /// are unaffected — `PickingMode.Ignore` excludes only this element.
        public static T NoPick<T>(this T element) where T : VisualElement
        {
            element.pickingMode = PickingMode.Ignore;
            return element;
        }

        public static VisualElement Row(Align alignItems = Align.Center)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = alignItems;
            return row;
        }

        public static VisualElement Column(Align alignItems = Align.Stretch)
        {
            var column = new VisualElement();
            column.style.flexDirection = FlexDirection.Column;
            column.style.alignItems = alignItems;
            return column;
        }

        public static Label Text(string content, int fontSize, Color color, bool bold = false)
        {
            var label = new Label(content);
            label.style.fontSize = fontSize;
            label.style.color = color;
            if (bold)
            {
                label.style.unityFontStyleAndWeight = FontStyle.Bold;
            }
            return label;
        }

        /// A dark rounded surface. Every panel, drawer and dialog in the game is
        /// one of these, so they share a radius and a background by construction.
        public static VisualElement Card(Color background, int radius = RADIUS_MD)
        {
            var card = new VisualElement();
            card.style.backgroundColor = background;
            card.Round(radius);
            return card;
        }

        public static VisualElement Divider(int margin = SPACE_2)
        {
            var divider = new VisualElement();
            divider.style.height = 1;
            divider.style.backgroundColor = Line;
            divider.style.marginTop = margin;
            divider.style.marginBottom = margin;
            return divider.NoPick();
        }

        // ---- Controls -------------------------------------------------------

        /// The one call-to-action on a screen: gold, tall, unmissable.
        public static Button PrimaryButton(string text, System.Action onClick)
        {
            var button = new Button(onClick) { text = text };
            StyleButton(button, Gold, OnGold, 72, FONT_TITLE);
            return button;
        }

        /// The secondary choice next to a PrimaryButton.
        public static Button GhostButton(string text, System.Action onClick)
        {
            var button = new Button(onClick) { text = text };
            StyleButton(button, new Color(0.22f, 0.2f, 0.21f), TextMid, 64, FONT_LEAD);
            return button;
        }

        /// A small persistent control docked to the edge of the HUD. Always at
        /// least TOUCH_MIN tall, whatever its label.
        public static Button ChipButton(string text, System.Action onClick, int width)
        {
            var button = new Button(onClick) { text = text };
            StyleButton(button, Chip, TextHi, TOUCH_MIN, FONT_SMALL);
            button.style.width = Mathf.Max(TOUCH_MIN, width);
            button.style.marginLeft = 0;
            button.style.marginRight = 0;
            return button;
        }

        static void StyleButton(Button button, Color background, Color text, int height, int fontSize)
        {
            button.style.height = Mathf.Max(TOUCH_MIN, height);
            button.style.fontSize = fontSize;
            button.style.unityFontStyleAndWeight = FontStyle.Bold;
            button.style.color = text;
            button.style.backgroundColor = background;
            button.style.borderTopWidth = 0;
            button.style.borderBottomWidth = 0;
            button.style.borderLeftWidth = 0;
            button.style.borderRightWidth = 0;
            button.Round(RADIUS_MD);
        }
    }
}
