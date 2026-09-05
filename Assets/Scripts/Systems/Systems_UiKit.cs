using UnityEngine;
using UnityEngine.UIElements;
using UnityEngine.UIElements.Experimental;

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
    /// One consequence of that choice has to be paid for explicitly, and is, in
    /// `AddPressFeedback` below: inline styles resolve ABOVE every USS rule,
    /// including the default runtime theme's `:hover` and `:active`. Any builder
    /// here that writes `backgroundColor` inline therefore has to put interaction
    /// feedback back by hand.
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

        // ---- Motion ---------------------------------------------------------
        // Two durations, so nothing on screen moves at a speed nothing else does.
        public const int MOTION_FAST = 140;  // scrims, state flips
        public const int MOTION_BASE = 220;  // dialogs entering, emphasis pops

        // ---- Three-column grid ----------------------------------------------
        // The comparison layout the fight HUD is built on: one metric per row as
        // [fighter A's value] METRIC [fighter B's value]. Percentages rather than
        // fixed widths because the panel resolves narrower than its 720pt
        // reference on tall phones and a fixed value plus a caption overflowed the
        // row there.
        public const int COL_SIDE = 27;
        public const int COL_CENTRE = 46;

        // ---- Palette --------------------------------------------------------
        public static readonly Color Ink = new Color(0.05f, 0.045f, 0.06f, 0.95f);
        public static readonly Color Panel = new Color(0.04f, 0.04f, 0.06f, 0.78f);
        /// A raised group background: lighter than a screen's own backdrop, darker
        /// than Chip, so a card can hold chips and still read as the surface behind
        /// them. Panel and Ink are both within a hundredth of the bracket screen's
        /// own background and are invisible on it — they are for cards that sit
        /// over the ARENA, which is why neither could do this job.
        public static readonly Color Surface = new Color(0.085f, 0.08f, 0.095f, 1f);
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

        /// Marks an element AND every descendant as scenery. `NoPick` excludes
        /// only the element it is called on, so a card whose children are labels
        /// still swallows taps through those labels — harmless for a card docked
        /// at the screen edge, not harmless for one sitting over the middle of
        /// the arena. Call it once the card's children are all in place.
        public static T NoPickTree<T>(this T element) where T : VisualElement
        {
            element.pickingMode = PickingMode.Ignore;
            for (int childIndex = 0; childIndex < element.childCount; childIndex++)
            {
                element[childIndex].NoPickTree();
            }
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

        /// One row of the comparison grid: two equal side columns with a wider
        /// caption column between them. The fight HUD builds its metric rows, its
        /// bar captions and its damage row out of this, which is what keeps the
        /// values in every row vertically aligned with the name plates above them.
        public static VisualElement Triplet(VisualElement left, VisualElement centre,
                                            VisualElement right)
        {
            VisualElement row = Row();
            left.style.width = Length.Percent(COL_SIDE);
            centre.style.width = Length.Percent(COL_CENTRE);
            right.style.width = Length.Percent(COL_SIDE);
            row.Add(left);
            row.Add(centre);
            row.Add(right);
            return row;
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
            // Every label in the game is built here, which is what makes this the
            // one place a typeface has to be applied. Systems_UiFonts picks the
            // display or the UI face from the size, and writes nothing at all when
            // no font resolved — so an empty Resources/Fonts/ leaves the UI
            // rendering exactly as it did before.
            Systems_UiFonts.Apply(label, fontSize);
            return label;
        }

        /// A centred caption. Enough of these existed inline that they were the
        /// single most repeated three-line block in the UI.
        public static Label Caption(string content, int fontSize, Color color, bool bold = false)
        {
            Label label = Text(content, fontSize, color, bold);
            label.style.unityTextAlign = TextAnchor.MiddleCenter;
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

        // ---- Elevation ------------------------------------------------------
        // Everything in this game is a flat dark rectangle on another flat dark
        // rectangle, and at three or four nested levels — a card holding a row
        // holding a chip — the levels stop being distinguishable. This is the
        // spatial hierarchy that fixes it.
        //
        // **UI Toolkit has no box-shadow**, so elevation cannot be a shadow and
        // there is no point pretending otherwise. What it does have is per-edge
        // border colours, so depth here is the same bevel a physical control has:
        // a light top edge catching the room light, a dark bottom edge in its own
        // shadow, over a background that lightens as it rises. That reads as
        // depth at every size and costs one element, no overdraw and no blur.
        //
        // Use the tier that matches what the element IS, not how it should look:
        // Sunken for a track or a well, Base for a screen's own background,
        // Raised for a card, Overlay for a dialog or anything floating over the
        // arena. Four is deliberately few — the reason the old UI drifted to
        // twenty font sizes is that nothing stopped it.
        public enum Elevation
        {
            Sunken = 0,
            Base = 1,
            Raised = 2,
            Overlay = 3,
        }

        /// Background for a tier. These fan out from the existing palette rather
        /// than replacing it: Base IS `Surface`, so an untouched card looks
        /// exactly as it did.
        public static Color ElevationBackground(Elevation tier)
        {
            switch (tier)
            {
                case Elevation.Sunken: return new Color(0.045f, 0.042f, 0.052f, 1f);
                case Elevation.Raised: return new Color(0.125f, 0.118f, 0.138f, 1f);
                case Elevation.Overlay: return new Color(0.165f, 0.156f, 0.181f, 1f);
                default: return Surface;
            }
        }

        /// Applies a tier's background and bevel. The bevel INVERTS for Sunken —
        /// a well is lit from below its top edge, which is what makes a track read
        /// as cut into the surface rather than sitting on it.
        public static T Elevate<T>(this T element, Elevation tier) where T : VisualElement
        {
            bool sunken = tier == Elevation.Sunken;
            // Strength scales with the tier so an Overlay reads as further off the
            // page than a Raised card, rather than every level having the same
            // 1px outline and cancelling the hierarchy out.
            float strength = tier == Elevation.Overlay ? 0.16f : 0.1f;

            element.style.backgroundColor = ElevationBackground(tier);
            element.style.borderTopWidth = 1;
            element.style.borderBottomWidth = 1;
            element.style.borderLeftWidth = 1;
            element.style.borderRightWidth = 1;
            element.style.borderTopColor = new Color(1f, 1f, 1f, sunken ? 0.02f : strength);
            element.style.borderBottomColor = new Color(0f, 0f, 0f, sunken ? 0.05f : 0.4f);
            // The sides carry a fraction of the top's light so the bevel wraps the
            // corners instead of stopping dead at them.
            Color side = new Color(1f, 1f, 1f, sunken ? 0.015f : strength * 0.4f);
            element.style.borderLeftColor = side;
            element.style.borderRightColor = side;
            return element;
        }

        /// A card at a given elevation. The two-argument `Card` above stays as it
        /// is — plenty of surfaces want an explicit colour and no bevel.
        public static VisualElement ElevatedCard(Elevation tier, int radius = RADIUS_MD)
        {
            var card = new VisualElement();
            card.Elevate(tier);
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

        // ---- Motion ---------------------------------------------------------

        /// Fades an element up from transparent. Used for the dialog scrim, which
        /// used to snap on in one frame.
        public static void FadeIn(this VisualElement element, int durationMs = MOTION_BASE)
        {
            element.style.opacity = 0f;
            element.experimental.animation
                .Start(0f, 1f, durationMs, (target, value) => target.style.opacity = value)
                .Ease(Easing.OutQuad);
        }

        /// Slides an element up into place while fading it in. `risePoints` is how
        /// far below its laid-out position it starts. Relative offsets, so the
        /// element keeps its place in the flex flow throughout.
        public static void RiseIn(this VisualElement element, float risePoints,
                                  int durationMs = MOTION_BASE)
        {
            element.style.opacity = 0f;
            element.experimental.animation
                .Start(0f, 1f, durationMs, (target, value) =>
                {
                    target.style.opacity = value;
                    target.style.bottom = -risePoints * (1f - value);
                })
                .Ease(Easing.OutQuad);
        }

        /// Emphasis pop: the label lands at `from` and settles to `to`. Replaces
        /// the "jump big, then snap back on a timer" pattern, which had no easing
        /// in either direction and left the label oversized if the schedule was
        /// dropped by a scene change.
        public static void PopFontSize(this Label label, int from, int to,
                                       int durationMs = MOTION_BASE)
        {
            label.style.fontSize = from;
            // Cast explicitly: ITransitionAnimations.Start has one overload per
            // animatable type and two int literals would leave the choice to
            // implicit numeric conversion.
            label.experimental.animation
                .Start((float)from, (float)to, durationMs,
                       (target, value) => target.style.fontSize = value)
                .Ease(Easing.OutQuad);
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

        /// A full-width, low-emphasis control — a disclosure row or a tertiary
        /// action. The bracket's CAREER RECORD toggle hand-rolled all eleven of
        /// these style writes, including its own copy of the border reset.
        public static Button QuietButton(string text, System.Action onClick)
        {
            var button = new Button(onClick) { text = text };
            StyleButton(button, Chip, TextLow, TOUCH_MIN, FONT_SMALL);
            button.Round(RADIUS_SM);
            button.style.marginLeft = 0;
            button.style.marginRight = 0;
            return button;
        }

        /// The pause affordance, drawn as two bars rather than set as a glyph.
        /// The pause character is not in every system font and this project ships
        /// no font asset of its own, so a glyph would render as a box on any
        /// device whose default font lacks it.
        public static Button PauseButton(System.Action onClick)
        {
            var button = new Button(onClick);
            StyleButton(button, Chip, TextHi, TOUCH_MIN, FONT_SMALL);
            button.style.width = TOUCH_MIN;
            button.style.marginLeft = 0;
            button.style.marginRight = 0;
            button.style.flexDirection = FlexDirection.Row;
            button.style.alignItems = Align.Center;
            button.style.justifyContent = Justify.Center;
            button.Add(PauseBar());
            button.Add(PauseBar());
            return button;
        }

        private static VisualElement PauseBar()
        {
            var bar = new VisualElement().NoPick();
            bar.style.width = 5;
            bar.style.height = 18;
            bar.style.marginLeft = 2;
            bar.style.marginRight = 2;
            bar.style.backgroundColor = TextHi;
            bar.Round(2);
            return bar;
        }

        private static void StyleButton(Button button, Color background, Color text, int height, int fontSize)
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
            // Buttons carry their own text rather than hosting a kit Label, so
            // they need the typeface applied here too — otherwise every control
            // in the game would be the one thing still in the default font.
            Systems_UiFonts.Apply(button, fontSize);
            button.Round(RADIUS_MD);
            AddPressFeedback(button, background, text);
        }

        /// Puts hover and press feedback back.
        ///
        /// Writing `backgroundColor` inline two lines above kills it: inline styles
        /// resolve above every USS rule, so the default runtime theme's `:hover`
        /// and `:active` selectors never applied to any button in this project.
        /// Every control in the game — REMATCH, RESUME, QUIT, START TOURNAMENT,
        /// RESHUFFLE — was visually dead on press, which on a touch target is the
        /// loudest "unfinished" signal a build can give.
        ///
        /// The tint is mixed toward the button's own TEXT colour rather than a
        /// fixed black or white, so it darkens the gold primary and lightens the
        /// dark chip with the same call. Colour and opacity only: `Scale`/`Translate`
        /// would read better still, but a press has to be instantaneous anyway and
        /// these two properties cost nothing to composite.
        private static void AddPressFeedback(Button button, Color background, Color text)
        {
            Color hover = Color.Lerp(background, text, 0.10f);
            Color press = Color.Lerp(background, text, 0.24f);
            bool pointerOver = false;

            button.RegisterCallback<PointerEnterEvent>(_ =>
            {
                pointerOver = true;
                button.style.backgroundColor = hover;
            });
            button.RegisterCallback<PointerLeaveEvent>(_ =>
            {
                pointerOver = false;
                button.style.backgroundColor = background;
                button.style.opacity = 1f;
            });
            button.RegisterCallback<PointerDownEvent>(_ =>
            {
                button.style.backgroundColor = press;
                button.style.opacity = 0.86f;
            });
            button.RegisterCallback<PointerUpEvent>(_ =>
            {
                button.style.backgroundColor = pointerOver ? hover : background;
                button.style.opacity = 1f;
            });
        }
    }
}
