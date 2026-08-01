using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace PoSumo
{
    /// March-Madness style bracket screen for an 8-entrant single-elimination
    /// tournament. The bracket auto-seeds each character twice (shuffled) and the
    /// user can drag to rearrange before starting; each match is then played for
    /// real in SCN_SUMO and the winner is filled in on return.
    ///
    /// UI Toolkit, built in code — same approach as the fight HUD, so there is no
    /// UXML/USS asset to keep in sync.
    public sealed class Systems_TournamentBracket : MonoBehaviour
    {
        [Tooltip("Characters available as entrants. With 4, each appears twice in the 8-slot bracket.")]
        [SerializeField] private Agent_CharacterDefinition[] _roster;
        [SerializeField] private PanelSettings _panelSettings;
        [Tooltip("Shared match tuning. Only used to state the correct best-of on this screen, so the status line cannot drift from the rule the matches actually run.")]
        [SerializeField] private Systems_GameTuning _tuning;
        // Every bout is fought in SCN_SUMO. The bracket used to rotate across
        // SCN_SUMO / SCN_SUMO_ICE / SCN_SUMO_STICKY; the ice and sticky arenas are
        // gone, so there is nothing to cycle.
        //
        // Deliberately a const and NOT a [SerializeField] string[]. SCN_TOURNAMENT
        // serializes its own copy of every serialized field, and this project has
        // been bitten three times by a stale scene value silently overriding the
        // code default (enableWalkIn, maxOrtho, wideOrtho). A const cannot drift,
        // and it makes the old serialized array in the scene file inert.
        private const string ARENA_SCENE = "SCN_SUMO";
        [Tooltip("Play the whole bracket unattended once it is seeded, pausing on this screen between matches.")]
        [SerializeField] private bool _autoPlay = true;
        [Tooltip("Seconds the updated bracket is shown before the next match starts.")]
        [SerializeField] private float _betweenMatchSeconds = 2.5f;

        private float _autoTimer;

        // Sized off the longest roster name. At the original 116 there was less
        // room left after the 42pt portrait than "STANDARD" needed, and since the
        // portrait was allowed to shrink, flexbox resolved the overflow by
        // crushing it to zero width — Standard was the one fighter rendering with
        // no portrait anywhere in the bracket, which read as missing art rather
        // than a layout fault. With the portrait pinned (flexShrink 0) the name
        // clipped instead, so the chip has to actually be wide enough: 164 leaves
        // ~114pt for the name against ~92pt for "STANDARD" at FONT_SMALL bold.
        // This is also the ceiling — the palette row puts four of them side by
        // side with 4pt margins, and 4*164+32 = 688 is just inside the 696pt of
        // usable width a 720pt panel has after its gutters.
        private const int SLOT_SIZE = 164;

        private readonly List<VisualElement> _seedSlots = new List<VisualElement>();
        private readonly List<VisualElement> _winnerSlots = new List<VisualElement>();
        private VisualElement _root;
        /// Everything on the screen except the drag ghost. The ghost stays on
        /// `_root` so its absolute pointer coordinates are not offset by the
        /// scroll position.
        private VisualElement _content;
        private VisualElement _careerPanel;
        private VisualElement _dragGhost;
        private Label _statusLabel;
        private Button _actionButton;
        private Button _resetButton;
        private VisualElement _paletteRow;
        private Label _hint;

        // Drag bookkeeping. _dragSeedIndex is -1 when dragging from the roster
        // palette instead of an existing seed slot.
        private bool _dragging;
        private int _dragSeedIndex = -1;
        private Agent_CharacterDefinition _dragCharacter;

        private void Start()
        {
            if (_roster == null || _roster.Length == 0)
            {
                Debug.LogError("Systems_TournamentBracket: no roster assigned.");
                return;
            }

            // Returning from a match mid-tournament: keep the existing bracket.
            // Otherwise this is a fresh visit, so draw a new field.
            if (!Systems_TournamentState.SeedsReady())
            {
                // NOT Time.frameCount: SCN_TOURNAMENT is build index 0, so this
                // Start runs on the first frame of the session and the salt was
                // the same small constant on every cold launch — ten launches,
                // ten byte-identical draws. Invisible unless you compare cold
                // starts, because RESHUFFLE runs late enough for frameCount to
                // vary. TickCount is wall-clock and unrelated to the frame loop.
                Systems_TournamentState.AutoSeed(_roster, System.Environment.TickCount);
            }

            BuildUi();
            Refresh();
        }

        private void BuildUi()
        {
            var doc = gameObject.AddComponent<UIDocument>();
            if (_panelSettings != null) doc.panelSettings = _panelSettings;
            _root = doc.rootVisualElement;
            _root.style.flexGrow = 1;
            _root.style.backgroundColor = new Color(0.05f, 0.045f, 0.05f, 1f);
            // Systems_SafeArea drives the ROOT's padding, so the screen's own
            // gutters cannot live there — it overwrote all four of them, and on a
            // device with no notch (every desktop, and the editor Game view) that
            // meant zero padding on all sides. The gutters belong one level in.
            Systems_SafeArea.Attach(transform, _root);

            // The bracket is taller than a phone screen once the career table is
            // expanded, and there was nothing to scroll: the overflow was simply
            // clipped, so the RESHUFFLE button and the standings could not be
            // reached at all.
            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.style.flexGrow = 1;
            // Vertical mode alone still leaves the horizontal scroller on Auto, so
            // a row a few points too wide draws a bar across the bottom of the
            // screen. Nothing here is meant to scroll sideways.
            scroll.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            _root.Add(scroll);

            _content = scroll.contentContainer;
            // Fill the viewport when the bracket is shorter than the screen, so
            // the spacer added below can push the action buttons down to the
            // thumb instead of leaving 45% of a portrait screen empty. When the
            // content IS taller — career table expanded — this costs nothing and
            // the ScrollView takes over.
            _content.style.flexGrow = 1;
            _content.style.paddingTop = Systems_UiKit.SPACE_4;
            _content.style.paddingLeft = Systems_UiKit.SPACE_3;
            _content.style.paddingRight = Systems_UiKit.SPACE_3;
            _content.style.paddingBottom = Systems_UiKit.SPACE_5;

            Label title = Systems_UiKit.Text("TOURNAMENT", Systems_UiKit.FONT_HERO, Systems_UiKit.Gold, true);
            title.style.unityTextAlign = TextAnchor.MiddleCenter;
            _content.Add(title);

            _hint = Systems_UiKit.Text("drag a fighter onto a slot to change it",
                                       Systems_UiKit.FONT_SMALL, Systems_UiKit.TextLow);
            _hint.style.unityTextAlign = TextAnchor.MiddleCenter;
            _hint.style.marginBottom = Systems_UiKit.SPACE_2;
            _content.Add(_hint);

            BuildPalette();

            _seedSlots.Clear();
            _winnerSlots.Clear();

            AddRoundHeader("QUARTERFINALS");
            for (int match = 0; match < 4; match++)
            {
                AddPairRow(seedA: match * 2, seedB: match * 2 + 1, winnerMatch: match);
            }

            AddRoundHeader("SEMIFINALS");
            AddResultRow(feederA: 0, feederB: 1, winnerMatch: 4);
            AddResultRow(feederA: 2, feederB: 3, winnerMatch: 5);

            AddRoundHeader("FINAL");
            AddResultRow(feederA: 4, feederB: 5, winnerMatch: Systems_TournamentState.FINAL_MATCH);

            // Absorbs whatever vertical slack is left, so the bracket sits under
            // the title and the controls sit at the bottom edge. Collapses to
            // zero the moment the content needs the room.
            var spacer = new VisualElement();
            spacer.style.flexGrow = 1;
            spacer.style.minHeight = Systems_UiKit.SPACE_3;
            _content.Add(spacer);

            // The status line and the two action buttons live OUTSIDE the
            // ScrollView, pinned to the bottom of the panel.
            //
            // Inside it they were the last thing in a 1409 px column. On the test
            // device the viewport happened to be 1558 px so they fitted, but the
            // panel scales on WIDTH (match=0) against a 720x1280 reference: on a
            // 16:9 phone the viewport is 1280 px and START TOURNAMENT sat ~130 px
            // below the fold, reachable only by scrolling a screen that otherwise
            // looks complete. A primary action must not depend on the aspect ratio.
            var footer = new VisualElement();
            footer.style.paddingLeft = Systems_UiKit.SPACE_3;
            footer.style.paddingRight = Systems_UiKit.SPACE_3;
            footer.style.paddingBottom = Systems_UiKit.SPACE_3;
            footer.style.flexShrink = 0;

            _statusLabel = Systems_UiKit.Text("", Systems_UiKit.FONT_LEAD, Systems_UiKit.Gold);
            _statusLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
            _statusLabel.style.whiteSpace = WhiteSpace.Normal;
            _statusLabel.style.marginTop = Systems_UiKit.SPACE_2;
            footer.Add(_statusLabel);

            _actionButton = Systems_UiKit.PrimaryButton("", OnAction);
            _actionButton.style.marginTop = Systems_UiKit.SPACE_2;
            footer.Add(_actionButton);

            // Only offered while the draw is still editable. It was previously
            // always visible, which made it both redundant and dangerous: on a
            // finished bracket it did exactly what NEW TOURNAMENT does (OnAction
            // forwards to OnReset when complete), and between two matches of a
            // running bracket — this screen is shown for _betweenMatchSeconds
            // after every bout — one tap silently destroyed the tournament in
            // progress with no confirmation.
            _resetButton = Systems_UiKit.GhostButton("RESHUFFLE", OnReset);
            _resetButton.style.marginTop = Systems_UiKit.SPACE_2;
            footer.Add(_resetButton);

            // Career table stays INSIDE the scroll — it is optional detail and is
            // exactly the thing that makes the column overflow when expanded.
            BuildCareerTable();

            _root.Add(footer);

            // Floating ghost that follows the pointer during a drag.
            _dragGhost = MakeChip(null, SLOT_SIZE);
            _dragGhost.style.position = Position.Absolute;
            _dragGhost.style.display = DisplayStyle.None;
            _dragGhost.style.opacity = 0.85f;
            _root.Add(_dragGhost);

            _root.RegisterCallback<PointerMoveEvent>(OnPointerMove);
            _root.RegisterCallback<PointerUpEvent>(OnPointerUp);
        }

        /// Career standings: the reason a tournament result matters beyond the
        /// session it was played in. Collapsed by default so the bracket stays the
        /// focus on a phone screen.
        private void BuildCareerTable()
        {
            // The match count rides on the toggle rather than sitting in a footer
            // row inside the table. It costs nothing here, it is readable while
            // the table is still collapsed, and it buys back a whole line of
            // vertical space in the panel for larger type.
            var toggle = new Button { text = CareerToggleText(open: false) };
            toggle.style.height = Systems_UiKit.TOUCH_MIN;
            toggle.style.marginTop = Systems_UiKit.SPACE_3;
            toggle.style.marginLeft = 0;
            toggle.style.marginRight = 0;
            toggle.style.fontSize = Systems_UiKit.FONT_SMALL;
            toggle.style.unityFontStyleAndWeight = FontStyle.Bold;
            toggle.style.color = Systems_UiKit.TextLow;
            toggle.style.backgroundColor = Systems_UiKit.Chip;
            toggle.style.borderTopWidth = 0;
            toggle.style.borderBottomWidth = 0;
            toggle.style.borderLeftWidth = 0;
            toggle.style.borderRightWidth = 0;
            toggle.Round(Systems_UiKit.RADIUS_SM);
            _content.Add(toggle);

            _careerPanel = Systems_UiKit.Card(Systems_UiKit.Ink, Systems_UiKit.RADIUS_SM);
            _careerPanel.Pad(Systems_UiKit.SPACE_3, Systems_UiKit.SPACE_2);
            _careerPanel.style.marginTop = Systems_UiKit.SPACE_1;
            _careerPanel.style.display = DisplayStyle.None;
            _content.Add(_careerPanel);

            toggle.clicked += () =>
            {
                bool open = _careerPanel.style.display == DisplayStyle.None;
                _careerPanel.style.display = open ? DisplayStyle.Flex : DisplayStyle.None;
                if (open) RefreshCareerTable();
                toggle.text = CareerToggleText(open);
            };

            RefreshCareerTable();
        }

        private static string CareerToggleText(bool open)
        {
            int played = Systems_CareerStats.MatchesPlayed;
            string count = played > 0 ? $"  ·  {played} MATCHES" : string.Empty;
            return $"CAREER RECORD{count}  {(open ? "▴" : "▾")}";
        }

        private void RefreshCareerTable()
        {
            if (_careerPanel == null) return;
            _careerPanel.Clear();

            var records = Systems_CareerStats.Ranked();
            if (records.Count == 0)
            {
                Label empty = Systems_UiKit.Text("no matches played yet",
                                                 Systems_UiKit.FONT_SMALL, Systems_UiKit.TextLow);
                empty.style.unityTextAlign = TextAnchor.MiddleCenter;
                _careerPanel.Add(empty);
                return;
            }

            _careerPanel.Add(CareerRow("FIGHTER", "ELO", "W-L", "TITLES", header: true));
            // A rule under the header does the separating that the old layout
            // asked a colour difference and a size difference to do at 14pt.
            _careerPanel.Add(Systems_UiKit.Divider(Systems_UiKit.SPACE_1));
            for (int recordIndex = 0; recordIndex < records.Count; recordIndex++)
            {
                Systems_CareerStats.Record r = records[recordIndex];
                _careerPanel.Add(CareerRow(
                    r.fighter.ToUpperInvariant(),
                    Mathf.RoundToInt(r.elo).ToString(),
                    $"{r.matchWins}-{r.matchLosses}",
                    r.titles > 0 ? new string('★', Mathf.Min(r.titles, 5)) : "—",
                    header: false));
            }
        }

        /// Round W-L used to sit between W-L and TITLES. It was dropped: match
        /// W-L already says who wins, and the round tally repeated that shape in
        /// bigger numbers for no extra read. The width it freed goes to the
        /// fighter name.
        private static VisualElement CareerRow(string name, string elo, string wl,
                                               string titles, bool header)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.marginTop = header ? 0 : 2;
            Color colour = header ? Systems_UiKit.TextLow : Systems_UiKit.TextHi;
            // Two steps up the type scale from the old 14/17. The standings are
            // the payoff for playing a bracket and were the smallest text on the
            // screen; dropping the footer row paid for the extra height.
            int size = header ? Systems_UiKit.FONT_SMALL : Systems_UiKit.FONT_BODY;
            // Fixed-width numeric columns, with the name column absorbing the
            // remainder. The five columns used to be percentages adding to exactly
            // 100, which overflowed the panel and pushed TITLES off the right edge
            // — percentages resolve before padding and rounding is not on your
            // side. Fixed widths that add to less than the panel cannot overflow,
            // and the numbers stay aligned whatever the panel width.
            Color numberColour = header ? Systems_UiKit.TextLow : Systems_UiKit.Gold;
            row.Add(CareerName(name, colour, size));
            row.Add(CareerCell(elo, 100, numberColour, size));
            row.Add(CareerCell(wl, 100, colour, size));
            row.Add(CareerCell(titles, 92, numberColour, size));
            return row;
        }

        /// The one flexible column: takes whatever the numeric columns leave and
        /// clips rather than pushing them off the row.
        private static Label CareerName(string text, Color colour, int fontSize)
        {
            var label = new Label(text);
            label.style.flexGrow = 1;
            label.style.flexShrink = 1;
            label.style.overflow = Overflow.Hidden;
            label.style.fontSize = fontSize;
            label.style.color = colour;
            label.style.unityTextAlign = TextAnchor.MiddleLeft;
            return label;
        }

        /// A right-aligned numeric column of fixed width. flexShrink 0 so a long
        /// fighter name can never squeeze the numbers.
        private static Label CareerCell(string text, int width, Color colour, int fontSize)
        {
            var label = new Label(text);
            label.style.width = width;
            label.style.flexShrink = 0;
            label.style.fontSize = fontSize;
            label.style.color = colour;
            label.style.unityTextAlign = TextAnchor.MiddleRight;
            return label;
        }

        private void BuildPalette()
        {
            VisualElement row = Systems_UiKit.Row();
            row.style.justifyContent = Justify.Center;
            row.style.marginBottom = Systems_UiKit.SPACE_3;
            _content.Add(row);
            _paletteRow = row;

            for (int rosterIndex = 0; rosterIndex < _roster.Length; rosterIndex++)
            {
                Agent_CharacterDefinition character = _roster[rosterIndex];
                // Same width as a bracket slot. At the old 88 the longest name on
                // the roster — STANDARD — ran straight out of its chip and over
                // the one beside it.
                VisualElement chip = MakeChip(character, SLOT_SIZE);
                // 2, not 4. Four chips at SLOT_SIZE came to 688pt against 696pt of
                // usable width — inside the panel, but not inside it once the
                // ScrollView reserves room for a scroller, which tipped the row
                // into horizontal overflow and put a scrollbar across the screen.
                chip.style.marginLeft = 2;
                chip.style.marginRight = 2;
                Agent_CharacterDefinition captured = character;
                chip.RegisterCallback<PointerDownEvent>(evt => BeginDrag(evt, -1, captured));
                row.Add(chip);
            }
        }

        private void AddRoundHeader(string text)
        {
            Label header = Systems_UiKit.Text(text, Systems_UiKit.FONT_SMALL, Systems_UiKit.TextLow, true);
            header.style.marginTop = Systems_UiKit.SPACE_3;
            header.style.marginBottom = Systems_UiKit.SPACE_1;
            _content.Add(header);
        }

        /// A quarterfinal row: two draggable seed slots plus the winner readout.
        private void AddPairRow(int seedA, int seedB, int winnerMatch)
        {
            var row = MakeRow();
            row.Add(MakeSeedSlot(seedA));
            row.Add(MakeVs());
            row.Add(MakeSeedSlot(seedB));
            row.Add(MakeArrow());
            row.Add(MakeWinnerSlot(winnerMatch));
            _content.Add(row);
        }

        /// A semifinal/final row: both entrants come from earlier winners, so
        /// nothing here is draggable.
        private void AddResultRow(int feederA, int feederB, int winnerMatch)
        {
            var row = MakeRow();
            row.Add(MakeWinnerSlot(feederA));
            row.Add(MakeVs());
            row.Add(MakeWinnerSlot(feederB));
            row.Add(MakeArrow());
            row.Add(MakeWinnerSlot(winnerMatch));
            _content.Add(row);
        }

        private static VisualElement MakeRow()
        {
            VisualElement row = Systems_UiKit.Row();
            row.style.marginBottom = Systems_UiKit.SPACE_1;
            return row;
        }

        private static Label MakeVs()
        {
            Label label = Systems_UiKit.Text("v", Systems_UiKit.FONT_SMALL, Systems_UiKit.TextLow);
            label.style.marginLeft = Systems_UiKit.SPACE_1;
            label.style.marginRight = Systems_UiKit.SPACE_1;
            return label;
        }

        private static Label MakeArrow()
        {
            Label label = Systems_UiKit.Text("→", Systems_UiKit.FONT_BODY, Systems_UiKit.TextLow);
            label.style.marginLeft = Systems_UiKit.SPACE_2;
            label.style.marginRight = Systems_UiKit.SPACE_2;
            return label;
        }

        private VisualElement MakeSeedSlot(int seedIndex)
        {
            VisualElement slot = MakeChip(Systems_TournamentState.GetSeed(seedIndex), SLOT_SIZE);
            slot.userData = seedIndex;
            _seedSlots.Add(slot);
            int captured = seedIndex;
            slot.RegisterCallback<PointerDownEvent>(evt =>
                BeginDrag(evt, captured, Systems_TournamentState.GetSeed(captured)));
            return slot;
        }

        private VisualElement MakeWinnerSlot(int matchIndex)
        {
            VisualElement slot = MakeChip(Systems_TournamentState.GetWinner(matchIndex), SLOT_SIZE);
            slot.userData = matchIndex;
            // Several rows show the SAME match: match 0 is both the QF-0 winner
            // readout and the semifinal's left entrant. Keeping one chip per match
            // index meant the later slot overwrote the earlier one and Refresh()
            // never repainted the orphan — stale winners survived a reshuffle.
            _winnerSlots.Add(slot);
            return slot;
        }

        /// A fighter chip: face sprite when the character has one, otherwise a
        /// colour block (Standard ships without face art), plus the name.
        private static VisualElement MakeChip(Agent_CharacterDefinition character, int width)
        {
            VisualElement chip = Systems_UiKit.Row();
            chip.style.width = width;
            // Comfortably over TOUCH_MIN: these are drag handles, not just labels.
            chip.style.height = 54;
            chip.style.backgroundColor = Systems_UiKit.Chip;
            // Belt and braces against a name longer than the chip: clip it here
            // rather than let it spill over the neighbouring slot.
            chip.style.overflow = Overflow.Hidden;
            chip.Round(Systems_UiKit.RADIUS_SM);
            chip.style.borderLeftWidth = 4;
            chip.style.borderLeftColor = character != null ? character.teamColor : Systems_UiKit.Chip;
            chip.style.paddingLeft = Systems_UiKit.SPACE_1;

            var icon = new VisualElement();
            icon.style.width = 42;
            icon.style.height = 42;
            // The portrait is fixed furniture: it must never be the thing that
            // gives way when a long name overflows the chip. Without this the
            // name wins and the icon collapses to nothing.
            icon.style.flexShrink = 0;
            icon.Round(21);
            icon.style.backgroundColor = character != null
                ? character.teamColor
                : new Color(0.25f, 0.23f, 0.24f);
            if (character != null && character.headSprite != null)
            {
                icon.style.backgroundImage = new StyleBackground(character.headSprite);
                icon.style.backgroundColor = Color.clear;
            }
            chip.Add(icon);

            Label name = Systems_UiKit.Text(
                character != null ? character.behaviorName.ToUpperInvariant() : "—",
                Systems_UiKit.FONT_SMALL,
                character != null ? character.teamColor : Systems_UiKit.TextLow,
                true);
            name.style.marginLeft = Systems_UiKit.SPACE_1;
            chip.Add(name);
            return chip;
        }

        // --- drag and drop -------------------------------------------------

        /// The draw can only be edited BEFORE a tournament starts.
        ///
        /// This used to test `Active` alone, but ReportWinner clears Active on the
        /// final — so a finished bracket became editable again. Dropping a fighter
        /// onto a quarterfinal then left the seed changed and the recorded winner
        /// untouched, giving rows like "STANDARD v NICK -> MATT" with a champion
        /// who was no longer in the draw.
        private static bool BracketLocked =>
            Systems_TournamentState.Active || Systems_TournamentState.IsComplete;

        private void BeginDrag(PointerDownEvent evt, int seedIndex, Agent_CharacterDefinition character)
        {
            if (BracketLocked) return;
            if (character == null) return;
            _dragging = true;
            _dragSeedIndex = seedIndex;
            _dragCharacter = character;
            RebuildGhost(character);
            MoveGhost(evt.position);
            _dragGhost.style.display = DisplayStyle.Flex;
            _root.CapturePointer(evt.pointerId);
            evt.StopPropagation();
        }

        private void RebuildGhost(Agent_CharacterDefinition character)
        {
            _root.Remove(_dragGhost);
            _dragGhost = MakeChip(character, SLOT_SIZE);
            _dragGhost.style.position = Position.Absolute;
            _dragGhost.style.opacity = 0.85f;
            _root.Add(_dragGhost);
        }

        private void OnPointerMove(PointerMoveEvent evt)
        {
            if (!_dragging) return;
            MoveGhost(evt.position);
        }

        private void MoveGhost(Vector3 position)
        {
            _dragGhost.style.left = position.x - SLOT_SIZE * 0.5f;
            _dragGhost.style.top = position.y - 27f;
        }

        private void OnPointerUp(PointerUpEvent evt)
        {
            if (!_dragging) return;
            _dragging = false;
            _dragGhost.style.display = DisplayStyle.None;
            _root.ReleasePointer(evt.pointerId);

            int target = SeedSlotUnder(evt.position);
            if (target >= 0)
            {
                if (_dragSeedIndex >= 0)
                {
                    Systems_TournamentState.SwapSeeds(_dragSeedIndex, target);
                }
                else
                {
                    Systems_TournamentState.SetSeed(target, _dragCharacter);
                }
                Refresh();
            }
            _dragSeedIndex = -1;
            _dragCharacter = null;
        }

        private int SeedSlotUnder(Vector2 position)
        {
            for (int seedSlotIndex = 0; seedSlotIndex < _seedSlots.Count; seedSlotIndex++)
            {
                if (_seedSlots[seedSlotIndex].worldBound.Contains(position))
                {
                    return (int)_seedSlots[seedSlotIndex].userData;
                }
            }
            return -1;
        }

        // --- refresh / flow ------------------------------------------------

        private void Refresh()
        {
            for (int seedSlotIndex = 0; seedSlotIndex < _seedSlots.Count; seedSlotIndex++)
            {
                int seedIndex = (int)_seedSlots[seedSlotIndex].userData;
                ApplyChip(_seedSlots[seedSlotIndex], Systems_TournamentState.GetSeed(seedIndex));
            }
            for (int winnerSlotIndex = 0; winnerSlotIndex < _winnerSlots.Count; winnerSlotIndex++)
            {
                int matchIndex = (int)_winnerSlots[winnerSlotIndex].userData;
                ApplyChip(_winnerSlots[winnerSlotIndex], Systems_TournamentState.GetWinner(matchIndex));
            }

            // The seeding controls only exist while seeding is possible. Left up,
            // they invited a drag that BeginDrag then silently ignored — a palette
            // and a "drag a fighter onto a slot" instruction that do nothing are
            // worse than no palette at all.
            DisplayStyle seedingControls = BracketLocked ? DisplayStyle.None : DisplayStyle.Flex;
            if (_hint != null) _hint.style.display = seedingControls;
            if (_paletteRow != null) _paletteRow.style.display = seedingControls;
            if (_resetButton != null) _resetButton.style.display = seedingControls;

            if (Systems_TournamentState.IsComplete)
            {
                var champion = Systems_TournamentState.Champion;
                _statusLabel.text = $"CHAMPION — {champion.behaviorName.ToUpperInvariant()}";
                _statusLabel.style.color = champion.teamColor;
                _actionButton.text = "NEW TOURNAMENT";
                return;
            }

            if (Systems_TournamentState.Active)
            {
                Systems_TournamentState.GetEntrants(Systems_TournamentState.CurrentMatch, out var a, out var b);
                string aName = a != null ? a.behaviorName.ToUpperInvariant() : "?";
                string bName = b != null ? b.behaviorName.ToUpperInvariant() : "?";
                // No arena suffix any more: every bout is on the same clay, so
                // naming it on every line was noise rather than information.
                _statusLabel.text = $"MATCH {Systems_TournamentState.CurrentMatch + 1} of " +
                                    $"{Systems_TournamentState.MATCH_COUNT} — {aName} v {bName}";
                _actionButton.text = _autoPlay ? "PLAYING…" : "PLAY MATCH";
                return;
            }

            // Built from the same constants the bracket actually runs on. The
            // entrant count and the best-of were a hardcoded sentence, so editing
            // tournamentPointsToWin on GameTuning.asset — which is exactly what
            // the project's tuning convention tells you to do — left this screen
            // stating a rule the game no longer followed.
            _statusLabel.text = $"{Systems_TournamentState.SEED_COUNT} entrants · single elimination"
                                + BestOfClause();
            _actionButton.text = "START TOURNAMENT";
        }

        /// Repaint one chip in place. Rebuilding the element would lose the
        /// registered drag callbacks, so only the visuals are swapped.
        private static void ApplyChip(VisualElement chip, Agent_CharacterDefinition character)
        {
            var icon = chip[0];
            var name = (Label)chip[1];
            chip.style.borderLeftColor = character != null ? character.teamColor : Systems_UiKit.Chip;
            icon.style.backgroundColor = character != null && character.headSprite == null
                ? character.teamColor
                : (character != null ? Color.clear : new Color(0.25f, 0.23f, 0.24f));
            icon.style.backgroundImage = character != null && character.headSprite != null
                ? new StyleBackground(character.headSprite)
                : new StyleBackground();
            name.text = character != null ? character.behaviorName.ToUpperInvariant() : "—";
            name.style.color = character != null ? character.teamColor : Systems_UiKit.TextLow;
        }

        /// Once the bracket is seeded and running, matches chain on their own —
        /// the user seeds the field, then watches the whole tournament play out.
        private void Update()
        {
            if (!_autoPlay) return;
            if (!Systems_TournamentState.Active || Systems_TournamentState.IsComplete) return;
            _autoTimer += Time.deltaTime;
            if (_autoTimer >= _betweenMatchSeconds)
            {
                _autoTimer = 0f;
                LaunchCurrentMatch();
            }
        }

        private void OnAction()
        {
            if (Systems_TournamentState.IsComplete)
            {
                OnReset();
                return;
            }
            if (!Systems_TournamentState.Active)
            {
                if (!Systems_TournamentState.SeedsReady())
                {
                    _statusLabel.text = "every slot needs a fighter";
                    return;
                }
                Systems_TournamentState.BeginTournament();
            }
            LaunchCurrentMatch();
        }

        private void LaunchCurrentMatch()
        {
            SceneManager.LoadScene(ARENA_SCENE);
        }

        /// " · best of N per match", derived from the tuning asset. Omitted rather
        /// than guessed when no asset is assigned, so the line can never be wrong.
        private string BestOfClause()
        {
            if (_tuning == null || _tuning.tournamentPointsToWin < 1)
            {
                return string.Empty;
            }
            return $" · best of {_tuning.tournamentPointsToWin * 2 - 1} per match";
        }

        private void OnReset()
        {
            Systems_TournamentState.ResetAll();
            // Wipe accumulated bruises and KO blood so a new bracket starts on
            // clean bodies. Systems_BodyDamage.ClearAll has documented that this
            // is its call site since it was written, but nothing ever called it:
            // damage is keyed by behaviour name in a static store that only clears
            // at play-session start, so every fighter carried the previous
            // tournament's blood into the next one — and into exhibition matches.
            Systems_BodyDamage.ClearAll();
            Systems_RingBlood.ClearAll();
            Systems_TournamentState.AutoSeed(_roster, Time.frameCount);
            Refresh();
        }
    }
}
