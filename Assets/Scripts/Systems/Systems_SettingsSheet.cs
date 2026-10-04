using UnityEngine;
using UnityEngine.UIElements;

namespace PoSumo
{
    /// The one settings surface in the game: a bottom sheet with three tabs —
    /// AUDIO, DISPLAY, GAMEPLAY — opened from the top-right menu button on both
    /// screens.
    ///
    /// A plain C# class that builds a card, exactly like Systems_CareerScreen:
    /// it adds NO UIDocument of its own. In the arena the card is registered as
    /// a modal of Systems_HudRoot (it IS the pause card, so RESUME and QUIT
    /// MATCH ride in its footer); on the bracket it sits on a modal layer inside
    /// the bracket's document. Whoever hosts it owns the scrim and the safe-area
    /// inset — see `Assets/UI Toolkit/README.md` for why those two cannot live
    /// on the same element.
    ///
    /// Before this the settings were scattered: a SOUND on/off button on the
    /// pause card, five persisted mix levels with no UI at all, a haptics switch
    /// that could only be flipped from code, and nothing whatsoever on the
    /// bracket screen.
    ///
    /// Every control is built through Systems_UiKit so it has press feedback,
    /// and nothing here uses a glyph: the steppers are the ASCII letters and
    /// signs every font carries, and the level meter is drawn boxes.
    public sealed class Systems_SettingsSheet
    {
        private const int TAB_AUDIO = 0;
        private const int TAB_DISPLAY = 1;
        private const int TAB_GAMEPLAY = 2;
        private const int TAB_COUNT = 3;

        /// Height the tab body always reserves — see the constructor.
        private const int BODY_MIN_HEIGHT = 316;

        /// Ten boxes per level meter, so one press of a stepper is one box.
        private const int LEVEL_STEPS = 10;

        /// Same key Systems_GameMatchManager has always used for its mute, so a
        /// player who muted the game before this sheet existed stays muted.
        private const string MUTE_KEY = "posumo.muted";

        private readonly Button[] _tabButtons = new Button[TAB_COUNT];
        private readonly VisualElement[] _tabBodies = new VisualElement[TAB_COUNT];
        private readonly Button[] _densityButtons = new Button[3];
        private readonly VisualElement _footer;
        private Button _muteButton;
        private Button _hapticsButton;
        private Label _densityNote;

        /// The sheet itself. Hand it to the host's modal layer.
        public VisualElement Card { get; }

        /// `title` is PAUSED in the arena and SETTINGS on the bracket; `rules` is
        /// the one-paragraph "how a round is won" note shown on the GAMEPLAY tab.
        public Systems_SettingsSheet(string title, string rules)
        {
            // Ink, but OPAQUE. The kit's Ink is 95% so the result card lets a hint
            // of the finish through; on the bracket that 5% put the gold START
            // button and the status line legibly behind the sheet's own controls.
            Card = Systems_UiKit.Card(
                new Color(Systems_UiKit.Ink.r, Systems_UiKit.Ink.g, Systems_UiKit.Ink.b, 1f),
                Systems_UiKit.RADIUS_LG);
            Card.style.width = Length.Percent(100);
            Card.style.maxWidth = 520;
            Card.Pad(Systems_UiKit.SPACE_4, Systems_UiKit.SPACE_4);

            Label heading = Systems_UiKit.Text(title, Systems_UiKit.FONT_TITLE, Systems_UiKit.Gold, true);
            heading.style.unityTextAlign = TextAnchor.MiddleCenter;
            Card.Add(heading);

            VisualElement tabRow = Systems_UiKit.Row();
            tabRow.style.marginTop = Systems_UiKit.SPACE_3;
            Card.Add(tabRow);
            AddTab(tabRow, TAB_AUDIO, "AUDIO");
            AddTab(tabRow, TAB_DISPLAY, "DISPLAY");
            AddTab(tabRow, TAB_GAMEPLAY, "GAMEPLAY");

            // One fixed-height body shared by the three tabs, so switching tabs
            // never changes the sheet's height and the footer buttons under the
            // thumb do not jump. AUDIO is the tallest — six rows of one touch
            // target each, which measured 312pt with the theme's own button
            // margins, not the 288 the arithmetic gives.
            var bodyHost = new VisualElement();
            bodyHost.style.marginTop = Systems_UiKit.SPACE_3;
            bodyHost.style.minHeight = BODY_MIN_HEIGHT;
            Card.Add(bodyHost);
            for (int tabIndex = 0; tabIndex < TAB_COUNT; tabIndex++)
            {
                _tabBodies[tabIndex] = Systems_UiKit.Column();
                bodyHost.Add(_tabBodies[tabIndex]);
            }

            BuildAudio(_tabBodies[TAB_AUDIO]);
            BuildDisplay(_tabBodies[TAB_DISPLAY]);
            BuildGameplay(_tabBodies[TAB_GAMEPLAY], rules);

            _footer = Systems_UiKit.Column();
            Card.Add(_footer);

            SelectTab(TAB_AUDIO);
        }

        // ---- Host hooks -------------------------------------------------------

        /// Adds a full-width button under the tabs — RESUME and QUIT MATCH in the
        /// arena, CLOSE on the bracket. They are outside the tab bodies on
        /// purpose: the way out of the sheet must not depend on which tab is up.
        public void AddFooterButton(Button button)
        {
            button.style.marginTop = Systems_UiKit.SPACE_3;
            _footer.Add(button);
        }

        /// An ON/OFF row on the DISPLAY tab whose state the host owns.
        public void AddDisplayToggle(string label, System.Func<bool> read, System.Action toggle)
        {
            AddToggleRow(_tabBodies[TAB_DISPLAY], label, read, toggle);
        }

        /// An action row on the DISPLAY tab — the route to the fighter debug
        /// panel and the engine overlay, which the host may or may not have.
        public void AddDisplayAction(string label, string buttonText, System.Action onClick)
        {
            VisualElement row = LabelledRow(_tabBodies[TAB_DISPLAY], label);
            row.Add(Systems_UiKit.ChipButton(buttonText, onClick, 96));
        }

        /// An ON/OFF row on the GAMEPLAY tab (the bracket's auto-play).
        public void AddGameplayToggle(string label, System.Func<bool> read, System.Action toggle)
        {
            // Above the rules paragraph, which stays the tab's footnote.
            VisualElement body = _tabBodies[TAB_GAMEPLAY];
            VisualElement row = AddToggleRow(body, label, read, toggle);
            row.SendToBack();
        }

        /// Re-applies the stored mute. `AudioListener.volume` is runtime state
        /// that a fresh Play session and every scene load reset to 1, so the
        /// preference has to be pushed back in whenever a screen builds.
        public static void ApplyMutePreference()
        {
            AudioListener.volume = Muted ? 0f : 1f;
        }

        // ---- Tabs -------------------------------------------------------------

        private void AddTab(VisualElement row, int tab, string label)
        {
            Button button = Systems_UiKit.ChipButton(label, () => SelectTab(tab), 0);
            button.style.flexGrow = 1;
            button.style.flexBasis = 0;
            button.style.marginLeft = tab == 0 ? 0 : Systems_UiKit.SPACE_1;
            _tabButtons[tab] = button;
            row.Add(button);
        }

        private void SelectTab(int tab)
        {
            for (int tabIndex = 0; tabIndex < TAB_COUNT; tabIndex++)
            {
                bool selected = tabIndex == tab;
                _tabBodies[tabIndex].style.display = selected ? DisplayStyle.Flex : DisplayStyle.None;
                // Same selection grammar as the bracket's BRACKET / RECORD tabs:
                // gold text over a gold underline.
                _tabButtons[tabIndex].style.color = selected ? Systems_UiKit.Gold : Systems_UiKit.TextHi;
                _tabButtons[tabIndex].style.borderBottomWidth = selected ? 3 : 0;
                _tabButtons[tabIndex].style.borderBottomColor = Systems_UiKit.Gold;
            }
        }

        // ---- AUDIO ------------------------------------------------------------

        private void BuildAudio(VisualElement body)
        {
            VisualElement soundRow = LabelledRow(body, "SOUND");
            _muteButton = Systems_UiKit.ChipButton("", ToggleMute, 96);
            soundRow.Add(_muteButton);
            PaintOnOff(_muteButton, !Muted);

            AddLevelRow(body, "MASTER", () => Systems_AudioMix.Master, value => Systems_AudioMix.Master = value);
            AddLevelRow(body, "EFFECTS", () => Systems_AudioMix.Sfx, value => Systems_AudioMix.Sfx = value);
            AddLevelRow(body, "CROWD", () => Systems_AudioMix.Crowd, value => Systems_AudioMix.Crowd = value);
            AddLevelRow(body, "MUSIC", () => Systems_AudioMix.Music, value => Systems_AudioMix.Music = value);
            AddLevelRow(body, "VOICES", () => Systems_AudioMix.Voice, value => Systems_AudioMix.Voice = value);
        }

        /// One mix level: caption, minus, a ten-box meter, plus.
        ///
        /// A stepper rather than a Slider. The runtime theme styles a Slider
        /// through USS, and inline styles — which is all this project has —
        /// cannot reach its dragger, so it would be the one control in the game
        /// drawn in the default grey; and a 44pt stepper is an easier target for
        /// a thumb than a 520pt-wide drag in the bottom third of a phone.
        private static void AddLevelRow(VisualElement body, string label,
                                        System.Func<float> read, System.Action<float> write)
        {
            VisualElement row = LabelledRow(body, label);

            var boxes = new VisualElement[LEVEL_STEPS];
            VisualElement meter = Systems_UiKit.Row().NoPick();
            meter.style.marginLeft = Systems_UiKit.SPACE_2;
            meter.style.marginRight = Systems_UiKit.SPACE_2;
            for (int boxIndex = 0; boxIndex < LEVEL_STEPS; boxIndex++)
            {
                var box = new VisualElement().NoPick();
                box.style.width = 10;
                box.style.height = 18;
                box.style.marginRight = boxIndex == LEVEL_STEPS - 1 ? 0 : 3;
                box.Round(2);
                boxes[boxIndex] = box;
                meter.Add(box);
            }

            System.Action paint = () =>
            {
                int lit = Mathf.RoundToInt(read() * LEVEL_STEPS);
                for (int boxIndex = 0; boxIndex < LEVEL_STEPS; boxIndex++)
                {
                    boxes[boxIndex].style.backgroundColor =
                        boxIndex < lit ? Systems_UiKit.Gold : Systems_UiKit.Track;
                }
            };
            System.Action<int> step = direction =>
            {
                // Snapped to whole boxes: the stored defaults are 0.7 / 0.9 /
                // 1.0, all on the grid, and a level that sits between two boxes
                // makes the first press look like it did nothing.
                int boxesLit = Mathf.Clamp(Mathf.RoundToInt(read() * LEVEL_STEPS) + direction,
                                           0, LEVEL_STEPS);
                write(boxesLit / (float)LEVEL_STEPS);
                // Systems_AudioMix writes the pref but leaves the flush to its
                // caller; a level changed and then lost to a killed app is the
                // one outcome a settings screen must not have.
                PlayerPrefs.Save();
                paint();
            };

            row.Add(Systems_UiKit.ChipButton("-", () => step(-1), Systems_UiKit.TOUCH_MIN));
            row.Add(meter);
            row.Add(Systems_UiKit.ChipButton("+", () => step(1), Systems_UiKit.TOUCH_MIN));
            paint();
        }

        private static bool Muted
        {
            get { return PlayerPrefs.GetInt(MUTE_KEY, 0) == 1; }
            set
            {
                PlayerPrefs.SetInt(MUTE_KEY, value ? 1 : 0);
                PlayerPrefs.Save();
                AudioListener.volume = value ? 0f : 1f;
            }
        }

        private void ToggleMute()
        {
            Muted = !Muted;
            PaintOnOff(_muteButton, !Muted);
        }

        // ---- DISPLAY ----------------------------------------------------------

        private void BuildDisplay(VisualElement body)
        {
            Label caption = Systems_UiKit.Text("HUD DETAIL", Systems_UiKit.FONT_SMALL,
                                               Systems_UiKit.TextMid, true);
            body.Add(caption);

            VisualElement row = Systems_UiKit.Row();
            row.style.marginTop = Systems_UiKit.SPACE_1;
            body.Add(row);
            for (int levelIndex = 0; levelIndex < _densityButtons.Length; levelIndex++)
            {
                var level = (Systems_HudDensity.Level)levelIndex;
                Button button = Systems_UiKit.ChipButton(Systems_HudDensity.Name(level),
                                                         () => SelectDensity(level), 0);
                button.style.flexGrow = 1;
                button.style.flexBasis = 0;
                button.style.marginLeft = levelIndex == 0 ? 0 : Systems_UiKit.SPACE_1;
                button.style.fontSize = Systems_UiKit.FONT_MICRO;
                _densityButtons[levelIndex] = button;
                row.Add(button);
            }

            _densityNote = Systems_UiKit.Text("", Systems_UiKit.FONT_MICRO, Systems_UiKit.TextLow);
            _densityNote.style.whiteSpace = WhiteSpace.Normal;
            _densityNote.style.marginTop = Systems_UiKit.SPACE_1;
            _densityNote.style.marginBottom = Systems_UiKit.SPACE_2;
            body.Add(_densityNote);
            PaintDensity();

            VisualElement hapticsRow = LabelledRow(body, "VIBRATION");
            _hapticsButton = Systems_UiKit.ChipButton("", ToggleHaptics, 96);
            hapticsRow.Add(_hapticsButton);
            PaintOnOff(_hapticsButton, Systems_FeelFx.HapticsEnabled);
        }

        private void SelectDensity(Systems_HudDensity.Level level)
        {
            Systems_HudDensity.Current = level;
            PaintDensity();
        }

        private void PaintDensity()
        {
            Systems_HudDensity.Level current = Systems_HudDensity.Current;
            for (int levelIndex = 0; levelIndex < _densityButtons.Length; levelIndex++)
            {
                bool selected = levelIndex == (int)current;
                _densityButtons[levelIndex].style.color =
                    selected ? Systems_UiKit.Gold : Systems_UiKit.TextHi;
                _densityButtons[levelIndex].style.borderBottomWidth = selected ? 3 : 0;
                _densityButtons[levelIndex].style.borderBottomColor = Systems_UiKit.Gold;
            }
            // What the choice means, and when it lands: the HUD is built once per
            // bout, so a change made mid-fight shows from the next one.
            string shows = current == Systems_HudDensity.Level.Full
                ? "Everything: adds damage figures and stamina history."
                : current == Systems_HudDensity.Level.Broadcast
                    ? "Adds commentary, the fighter card and win chance."
                    : "Score, mat and stamina only.";
            _densityNote.text = shows + " Applies from the next bout.";
        }

        private void ToggleHaptics()
        {
            Systems_FeelFx.HapticsEnabled = !Systems_FeelFx.HapticsEnabled;
            PaintOnOff(_hapticsButton, Systems_FeelFx.HapticsEnabled);
        }

        // ---- GAMEPLAY ---------------------------------------------------------

        private static void BuildGameplay(VisualElement body, string rules)
        {
            // REALISTIC MODE. Read once per bout by Systems_BodyDamage.Start, so
            // the note has to say when it lands — in the arena this sheet is the
            // pause card, and a switch that visibly did nothing to the fight in
            // front of the player would read as broken.
            AddToggleRow(body, "REALISTIC MODE (no dismemberment)",
                         () => Systems_RealisticMode.Enabled,
                         () => Systems_RealisticMode.Enabled = !Systems_RealisticMode.Enabled);
            Label realisticNote = Systems_UiKit.Text(
                "Limbs and heads stay on. Bruising and knockouts are unchanged. Applies from the next bout.",
                Systems_UiKit.FONT_MICRO, Systems_UiKit.TextLow);
            realisticNote.style.whiteSpace = WhiteSpace.Normal;
            realisticNote.style.marginBottom = Systems_UiKit.SPACE_2;
            body.Add(realisticNote.NoPick());

            Label caption = Systems_UiKit.Text("HOW A ROUND IS WON", Systems_UiKit.FONT_SMALL,
                                               Systems_UiKit.TextMid, true);
            caption.style.marginTop = Systems_UiKit.SPACE_1;
            body.Add(caption);

            Label text = Systems_UiKit.Text(rules, Systems_UiKit.FONT_SMALL, Systems_UiKit.TextLow);
            text.style.whiteSpace = WhiteSpace.Normal;
            text.style.marginTop = Systems_UiKit.SPACE_1;
            body.Add(text.NoPick());
        }

        // ---- Row builders -----------------------------------------------------

        /// A caption on the left that takes the slack, controls on the right.
        private static VisualElement LabelledRow(VisualElement body, string label)
        {
            VisualElement row = Systems_UiKit.Row();
            row.style.marginTop = Systems_UiKit.SPACE_1;
            row.style.minHeight = Systems_UiKit.TOUCH_MIN;
            Label caption = Systems_UiKit.Text(label, Systems_UiKit.FONT_SMALL,
                                               Systems_UiKit.TextMid, true);
            caption.style.flexGrow = 1;
            row.Add(caption.NoPick());
            body.Add(row);
            return row;
        }

        private static VisualElement AddToggleRow(VisualElement body, string label,
                                                  System.Func<bool> read, System.Action toggle)
        {
            VisualElement row = LabelledRow(body, label);
            Button button = null;
            button = Systems_UiKit.ChipButton("", () =>
            {
                toggle();
                PaintOnOff(button, read());
            }, 96);
            row.Add(button);
            PaintOnOff(button, read());
            return row;
        }

        private static void PaintOnOff(Button button, bool on)
        {
            if (button == null)
            {
                return;
            }
            button.text = on ? "ON" : "OFF";
            button.style.color = on ? Systems_UiKit.Gold : Systems_UiKit.TextLow;
        }
    }
}
