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

        /// SCN_TOURNAMENT shipped without an AudioListener — only SCN_SUMO has one
        /// — and since the bracket is build index 0 the game always BOOTS into the
        /// silent scene, logging a "no audio listener" warning on the first screen
        /// the player ever sees. Added here rather than in the scene file so it
        /// cannot be lost to a scene re-save, and deliberately NOT persistent:
        /// LoadScene(ARENA_SCENE) is Single mode, so this dies with the scene and
        /// never fights the arena's own listener.
        private static void EnsureAudioListener()
        {
            if (FindAnyObjectByType<AudioListener>() != null)
            {
                return;
            }
            Camera target = Camera.main;
            if (target != null)
            {
                target.gameObject.AddComponent<AudioListener>();
                return;
            }
            new GameObject("AudioListener").AddComponent<AudioListener>();
        }
        [Tooltip("Play the whole bracket unattended once it is seeded, pausing on this screen between matches.")]
        [SerializeField] private bool _autoPlay = true;
        [Tooltip("Seconds the updated bracket is shown before the next match starts.")]
        [SerializeField] private float _betweenMatchSeconds = 2.5f;

        private float _autoTimer;

        // Chips are ELASTIC now, and this is the floor rather than the size.
        //
        // Every chip used to be pinned to exactly 164pt, which had to satisfy two
        // conditions at once that only just fitted: wide enough for "STANDARD" at
        // FONT_SMALL bold beside a 42pt portrait (~92 + 42 + padding), and narrow
        // enough that four of them in the palette row cleared the ~696pt of usable
        // width a 720pt panel has after its gutters. 4*164+32 = 688 — 8pt of slack,
        // which the ScrollView's own scroller then ate, so the palette margins had
        // already been shaved to 2pt to buy it back.
        //
        // It also left the bracket visibly off-centre: three pinned chips plus the
        // separators came to ~538pt inside a ~696pt column, so every row sat in the
        // left three-quarters of a screen whose title was centred.
        //
        // With flexGrow the row divides whatever width it is given, so it fills the
        // panel on any aspect ratio and cannot overflow at any of them. This value
        // survives as minWidth (the name still needs the room) and as the fixed
        // width of the floating drag ghost, which is absolutely positioned and has
        // no row to take its share from.
        //
        // 164 -> 150, and it is now a FLOOR THAT NEVER BINDS, which is the point.
        //
        // MEASURED, because the arithmetic that produced 164 was against the wrong
        // number: a live 1170x2532 capture puts the usable content column at about
        // 578pt, not the ~696 a 720pt reference panel implies. Three 164pt chips
        // plus the separators came to ~549 against a round card's ~553pt interior —
        // inside it by four points, which is why it looked fine and why one step up
        // the type scale (190) immediately clipped the winner chip off the card.
        //
        // At 150 the floor sits well under the ~165pt flexGrow actually hands each
        // chip, so the row is sized by the space available rather than by a
        // constant that has to be re-derived every time a font or an icon changes.
        // A floor that binds is a floor that overflows.
        private const int SLOT_SIZE = 150;

        private readonly List<VisualElement> _seedSlots = new List<VisualElement>();
        private readonly List<VisualElement> _winnerSlots = new List<VisualElement>();
        private VisualElement _root;
        /// Everything on the screen except the drag ghost. The ghost stays on
        /// `_root` so its absolute pointer coordinates are not offset by the
        /// scroll position.
        private VisualElement _content;
        private Systems_CareerScreen _careerScreen;
        private Systems_PromotionCeremony _promotionCeremony;
        private Button _careerButton;
        /// Promotion banner. Shown once, on the first Refresh after returning from
        /// a match that moved somebody up or down the banzuke.
        private Label _rankNews;
        private VisualElement _dragGhost;
        private Label _statusLabel;
        private Button _actionButton;
        private Button _resetButton;
        private VisualElement _paletteRow;
        private VisualElement _standings;
        private Button _autoButton;
        private Label _hint;

        // Drag bookkeeping. _dragSeedIndex is -1 when dragging from the roster
        // palette instead of an existing seed slot.
        private bool _dragging;
        private int _dragSeedIndex = -1;
        private Agent_CharacterDefinition _dragCharacter;

        private void Start()
        {
            EnsureAudioListener();

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

            // `_root` is deliberately UNPADDED and the safe-area inset moved one
            // level in, onto `screen` and the career overlay's own modal layer.
            //
            // The rule (see Assets/UI Toolkit/README.md) is that an absolutely
            // positioned child resolves its offsets against its parent's PADDING
            // box. The career screen's scrim is an absolute child that has to reach
            // the physical edges of the display, so it cannot hang off an inset
            // parent — under the inset it stops at the notch and leaves an undimmed
            // strip top and bottom.
            //
            // Moving the inset off `_root` also fixes a latent bug in the drag
            // ghost, which is likewise absolute on `_root` and positioned from raw
            // pointer coordinates: while `_root` carried the inset, the ghost was
            // displaced by exactly the notch height on any device that has one.
            var screen = new VisualElement();
            screen.style.flexGrow = 1;
            _root.Add(screen);
            // Systems_SafeArea takes several targets precisely so the content layer
            // and the modal layer can be inset without the scrim between them being.
            // The screen's own gutters cannot live on an inset element either — the
            // watcher overwrites all four paddings, and on a device with no notch
            // (every desktop, and the editor Game view) that means zero padding on
            // all sides. The gutters belong one level further in again.

            // The bracket is taller than a phone screen, and there was nothing to
            // scroll: the overflow was simply clipped, so the RESHUFFLE button
            // could not be reached at all.
            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.style.flexGrow = 1;
            // Vertical mode alone still leaves the horizontal scroller on Auto, so
            // a row a few points too wide draws a bar across the bottom of the
            // screen. Nothing here is meant to scroll sideways.
            scroll.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            screen.Add(scroll);

            // BUILD STAMP, top-left of the boot screen.
            //
            // The game ships to a phone by sideload, and two installs an hour apart
            // are indistinguishable once the APK is on the device — there is no
            // build number anywhere on screen, and `adb shell dumpsys package` is
            // not available to whoever is holding the phone. `Application.version`
            // IS `PlayerSettings.bundleVersion`, so this is the shipped number
            // rather than a constant that can drift away from it.
            //
            // On `screen`, NOT `_root` and NOT `_content`:
            //  - `_content` is inside the ScrollView, so the stamp would scroll off.
            //  - `_root` is deliberately un-inset (see above), so on a notched device
            //    a top-left absolute child lands UNDER the cutout.
            //  - `screen` carries the safe-area inset, and absolute offsets resolve
            //    against the parent's PADDING box, so 0,0 here is exactly the first
            //    safe pixel.
            // Added after the ScrollView so it draws over the content, and NoPick so
            // it cannot eat a pointer-down meant for the fighter palette behind it.
            Label version = Systems_UiKit.Text("v" + Application.version,
                                               Systems_UiKit.FONT_MICRO,
                                               Systems_UiKit.TextLow);
            version.style.position = Position.Absolute;
            version.style.left = Systems_UiKit.SPACE_2;
            version.style.top = Systems_UiKit.SPACE_2;
            screen.Add(version.NoPick());

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

            VisualElement quarterfinals = AddRound("QUARTERFINALS");
            for (int match = 0; match < 4; match++)
            {
                AddPairRow(quarterfinals, seedA: match * 2, seedB: match * 2 + 1, winnerMatch: match);
            }

            VisualElement semifinals = AddRound("SEMIFINALS");
            AddResultRow(semifinals, feederA: 0, feederB: 1, winnerMatch: 4);
            AddResultRow(semifinals, feederA: 2, feederB: 3, winnerMatch: 5);

            VisualElement final = AddRound("FINAL");
            AddResultRow(final, feederA: 4, feederB: 5,
                         winnerMatch: Systems_TournamentState.FINAL_MATCH);

            // Career table stays INSIDE the scroll — it is optional detail and is
            // exactly the thing that makes the column overflow when expanded.
            //
            // BEFORE the spacer, and that ordering is the fix for a screen with a
            // hole in it: built after the spacer, the CAREER RECORD toggle was
            // pushed to the bottom of the scroll content and sat alone under ~600
            // px of empty backdrop. The bracket is what should own the top of the
            // screen, its disclosure row belongs directly under it, and the slack
            // belongs below both.
            BuildCareerTable();

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

            // Manual play has always existed — `_autoPlay` is a serialized field
            // and the action button already reads "PLAY MATCH" when it is off —
            // but there was no way to reach it without the Inspector. So a player
            // who pressed START was committed to watching all seven bouts run
            // themselves with no pause, no skip and no way back except QUIT MATCH
            // from inside a bout. Shown only while a bracket is running, because
            // that is the only time the distinction means anything.
            _autoButton = Systems_UiKit.GhostButton("", ToggleAuto);
            _autoButton.style.marginTop = Systems_UiKit.SPACE_2;
            footer.Add(_autoButton);

            screen.Add(footer);

            // Floating ghost that follows the pointer during a drag.
            _dragGhost = MakeGhostChip(null);
            _dragGhost.style.display = DisplayStyle.None;
            _root.Add(_dragGhost);

            // Added to `_root` LAST so it draws over the footer and the ghost. It is
            // a modal: while it is open nothing behind it should be reachable, and
            // UI Toolkit resolves both draw and pick order by document order here.
            _careerScreen = new Systems_CareerScreen(_root);

            // After the career screen, for the same document-order reason: a
            // promotion lands the moment you return from a bout and must draw over
            // everything, including the career table if it happens to be open.
            _promotionCeremony = new Systems_PromotionCeremony(_root);

            Systems_SafeArea.Attach(transform, screen, _careerScreen.SafeAreaTarget);
            Systems_SafeArea.Attach(transform, screen, _promotionCeremony.SafeAreaTarget);

            _root.RegisterCallback<PointerMoveEvent>(OnPointerMove);
            _root.RegisterCallback<PointerUpEvent>(OnPointerUp);
        }

        /// Entry point to the banzuke: the reason a tournament result matters
        /// beyond the session it was played in.
        ///
        /// This used to be a disclosure toggle over a four-column ELO / W-L /
        /// TITLES table built inline in this scroll column. The table is gone and
        /// `Systems_CareerScreen` replaces it — one career UI, not two. It was the
        /// thing that made this column overflow when expanded, it competed with the
        /// bracket for the same ~578pt of usable width, and there was no room in it
        /// for the rank, the promotion bar, the round record or the head-to-head
        /// that make a career readable as a climb rather than a scoreboard.
        private void BuildCareerTable()
        {
            _rankNews = Systems_UiKit.Text("", Systems_UiKit.FONT_SMALL, Systems_UiKit.Gold, true);
            _rankNews.style.unityTextAlign = TextAnchor.MiddleCenter;
            _rankNews.style.whiteSpace = WhiteSpace.Normal;
            _rankNews.style.marginTop = Systems_UiKit.SPACE_3;
            _rankNews.style.display = DisplayStyle.None;
            _content.Add(_rankNews);

            // QuietButton rather than a hand-rolled control: the kit builder brings
            // the press feedback with it, and a button built by hand is visually
            // dead on press because StyleButton writes backgroundColor inline and
            // inline styles resolve above the runtime theme's :active rule.
            _careerButton = Systems_UiKit.QuietButton("", () => _careerScreen.Show());
            _careerButton.style.marginTop = Systems_UiKit.SPACE_2;
            _content.Add(_careerButton);

            _standings = new VisualElement();
            _standings.style.marginTop = Systems_UiKit.SPACE_2;
            _content.Add(_standings);

            RefreshCareerButton();
            RefreshStandings();
        }

        /// Top of the banzuke, in the slack between the bracket and the footer.
        ///
        /// That slack is deliberate — the spacer below exists so the bracket sits
        /// under the title and the controls stay pinned to the bottom edge, and an
        /// earlier layout that put content BELOW the spacer left it stranded under
        /// ~600 px of backdrop. So this goes ABOVE the spacer, directly under the
        /// career row, and simply gives the empty third of the screen something
        /// worth reading rather than trying to remove it.
        ///
        /// Three rungs only. The point is "who is on top and am I climbing", not a
        /// table — `Systems_CareerScreen` already owns the full record, and the
        /// column here is ~578pt wide, which is not enough for a fourth field.
        private void RefreshStandings()
        {
            if (_standings == null)
            {
                return;
            }
            _standings.Clear();

            System.Collections.Generic.List<Systems_CareerStats.Record> ranked =
                Systems_CareerStats.Ranked();
            if (ranked == null || ranked.Count == 0)
            {
                return;
            }

            Label heading = Systems_UiKit.Caption("BANZUKE", Systems_UiKit.FONT_MICRO,
                                                  Systems_UiKit.TextLow, true);
            heading.style.unityTextAlign = TextAnchor.MiddleCenter;
            heading.style.marginBottom = Systems_UiKit.SPACE_1;
            heading.NoPick();
            _standings.Add(heading);

            // Carded like every round group above it. Left bare, the rows ran
            // edge-to-edge against the panel while the bracket beside them sat
            // inset in surfaces, which read as unfinished rather than as a
            // different kind of content.
            VisualElement card = Systems_UiKit.Card(Systems_UiKit.Surface, Systems_UiKit.RADIUS_SM);
            card.style.paddingTop = Systems_UiKit.SPACE_1;
            card.style.paddingBottom = Systems_UiKit.SPACE_1;
            card.NoPick();
            _standings.Add(card);

            int shown = Mathf.Min(3, ranked.Count);
            for (int index = 0; index < shown; index++)
            {
                Systems_CareerStats.Record record = ranked[index];
                VisualElement row = Systems_UiKit.Row();
                row.style.paddingLeft = Systems_UiKit.SPACE_2;
                row.style.paddingRight = Systems_UiKit.SPACE_2;
                row.style.paddingTop = 3;
                row.style.paddingBottom = 3;
                row.NoPick();

                Label rank = Systems_UiKit.Text(Systems_CareerLadder.NameFor(record),
                                                Systems_UiKit.FONT_MICRO,
                                                Systems_UiKit.TextLow);
                rank.style.width = Length.Percent(46f);
                rank.NoPick();

                Label who = Systems_UiKit.Text(record.fighter.ToUpperInvariant(),
                                               Systems_UiKit.FONT_MICRO,
                                               ColorFor(record.fighter), true);
                who.style.flexGrow = 1;
                who.NoPick();

                Label elo = Systems_UiKit.Text(Mathf.RoundToInt(record.elo).ToString(),
                                               Systems_UiKit.FONT_MICRO,
                                               Systems_UiKit.TextMid);
                elo.style.unityTextAlign = TextAnchor.MiddleRight;
                elo.NoPick();

                row.Add(rank);
                row.Add(who);
                row.Add(elo);
                card.Add(row);
            }

            // The only place the rules are stated anywhere in the game. A player
            // watching a bout has no way to work out that lying down for three
            // seconds forfeits the round — it looks like the game giving up — and
            // that rule ends the majority of rounds.
            Label rules = Systems_UiKit.Text(
                "WIN A ROUND BY PUSHING YOUR OPPONENT OUT, PUTTING THEM DOWN, "
                + "OR LEADING ON POSITION AT THE BELL.",
                Systems_UiKit.FONT_MICRO, Systems_UiKit.TextLow);
            rules.style.whiteSpace = WhiteSpace.Normal;
            rules.style.unityTextAlign = TextAnchor.MiddleCenter;
            rules.style.marginTop = Systems_UiKit.SPACE_3;
            rules.style.paddingLeft = Systems_UiKit.SPACE_3;
            rules.style.paddingRight = Systems_UiKit.SPACE_3;
            rules.NoPick();
            _standings.Add(rules);
        }

        /// Distinct fighters actually available to seed the draw.
        private int DistinctFighters()
        {
            if (_roster == null)
            {
                return 0;
            }
            int count = 0;
            for (int index = 0; index < _roster.Length; index++)
            {
                if (_roster[index] != null)
                {
                    count++;
                }
            }
            return count;
        }

        /// Roster colour for a behaviour name, so the standings match the chips.
        /// Falls back to mid grey for a fighter no longer on the roster — records
        /// are keyed by behaviour name and outlive an asset being removed.
        private Color ColorFor(string behaviorName)
        {
            if (_roster != null)
            {
                for (int index = 0; index < _roster.Length; index++)
                {
                    if (_roster[index] != null && _roster[index].behaviorName == behaviorName)
                    {
                        return _roster[index].teamColor;
                    }
                }
            }
            return Systems_UiKit.TextMid;
        }

        /// Label carries the match count and the current Yokozuna — the two facts
        /// worth reading without opening anything.
        private void RefreshCareerButton()
        {
            if (_careerButton == null)
            {
                return;
            }
            int played = Systems_CareerStats.MatchesPlayed;
            string count = played > 0 ? $"  ·  {played} MATCHES" : string.Empty;
            _careerButton.text = $"CAREER  ·  BANZUKE{count}  ▸";
        }

        /// Announces a promotion or demotion once, on the first Refresh after the
        /// match that caused it. Consume-once on the recorder's side, so it does not
        /// re-announce on every subsequent Refresh of this screen.
        private void RefreshRankNews()
        {
            if (_rankNews == null)
            {
                return;
            }
            if (!Systems_CareerRecorder.TryTakeRankChange(out Systems_CareerRecorder.RankChange change))
            {
                return;
            }

            string fighter = change.Fighter.ToUpperInvariant();
            _rankNews.text = change.Promoted
                ? $"{fighter} PROMOTED TO {change.ToRank}"
                : $"{fighter} DEMOTED TO {change.ToRank}";
            _rankNews.style.color = change.Promoted ? Systems_UiKit.Gold : Systems_UiKit.Bad;
            _rankNews.style.display = DisplayStyle.Flex;
            _rankNews.FadeIn();

            // The label STAYS. It is the persistent record on the bracket page; the
            // ceremony is the one-shot moment over the top of it. Removing the label
            // would mean a player who dismissed the ceremony has no way to see what
            // happened, and TryTakeRankChange is consume-once.
            _promotionCeremony?.Show(change, change.FromRank);
            Systems_Log.Info($"[BANZUKE] {fighter} {(change.Promoted ? "PROMOTED" : "DEMOTED")} " +
                             $"{change.FromRank} -> {change.ToRank}");
        }

        private void BuildPalette()
        {
            VisualElement row = Systems_UiKit.Row();
            row.style.justifyContent = Justify.Center;
            row.style.marginBottom = Systems_UiKit.SPACE_3;
            _content.Add(row);
            _paletteRow = row;

            // A 2-UP GRID, not a strip. Four chips side by side is more width than
            // the panel has to give each of them, and simply allowing wrap put
            // three on the first line and left KIM alone across the full width of
            // the second — which reads as a layout fault rather than a roster.
            //
            // Each chip rides in a 50%-wide CELL and the gutter is the cell's
            // padding, so two cells come to exactly 100% at any panel width. A
            // margin on the chip itself would push the pair past 100% and wrap them
            // one per line — the failure this replaced, in a different disguise.
            row.style.flexWrap = Wrap.Wrap;

            for (int rosterIndex = 0; rosterIndex < _roster.Length; rosterIndex++)
            {
                Agent_CharacterDefinition character = _roster[rosterIndex];

                // A ROW, not a bare VisualElement. The chip sizes itself with
                // flexGrow/flexBasis, and flex-basis applies to the MAIN axis — in
                // a default column container that is HEIGHT, so `flexBasis: 0`
                // collapsed every palette chip to zero height and the roster
                // disappeared off the screen with no error to show for it.
                VisualElement cell = Systems_UiKit.Row();
                cell.style.width = Length.Percent(50f);
                cell.style.paddingRight = rosterIndex % 2 == 0 ? Systems_UiKit.SPACE_1 : 0;
                cell.style.paddingLeft = rosterIndex % 2 == 0 ? 0 : Systems_UiKit.SPACE_1;
                cell.style.paddingBottom = Systems_UiKit.SPACE_1;

                VisualElement chip = MakeChip(character);
                // The bracket's 164pt floor is for a row of three; two-up cells are
                // already wider than that, and leaving it on would fight the cell.
                chip.style.minWidth = 0;
                chip.Add(DragGrip());
                Agent_CharacterDefinition captured = character;
                chip.RegisterCallback<PointerDownEvent>(evt => BeginDrag(evt, -1, captured));

                cell.Add(chip);
                row.Add(cell);
            }
        }

        /// Opens a round: a centred header over a card that holds that round's
        /// rows, and returns the card for them to be added to.
        ///
        /// The three rounds used to be seven rows in a single flat column, told
        /// apart only by a left-aligned 17pt caption and a 4pt gap — the same 4pt
        /// that separated the rows WITHIN a round, so nothing on the screen said
        /// where one round ended. Boxing each round is what lets the gaps mean
        /// something: SPACE_2 between rows of the same round, SPACE_4 between
        /// rounds, and a surface behind each group.
        private VisualElement AddRound(string text)
        {
            Label header = Systems_UiKit.Caption(text, Systems_UiKit.FONT_SMALL,
                                                 Systems_UiKit.TextLow, true);
            header.style.marginTop = Systems_UiKit.SPACE_4;
            header.style.marginBottom = Systems_UiKit.SPACE_1;
            _content.Add(header);

            VisualElement card = Systems_UiKit.Card(Systems_UiKit.Surface, Systems_UiKit.RADIUS_MD);
            card.Pad(Systems_UiKit.SPACE_3, Systems_UiKit.SPACE_3);
            // The last row's own bottom margin supplies the card's bottom padding,
            // so a round with one row and a round with four are inset identically.
            // Padding both would double up under the final row of every card.
            card.style.paddingBottom = 0;
            _content.Add(card);
            return card;
        }

        /// A quarterfinal row: two draggable seed slots plus the winner readout.
        private void AddPairRow(VisualElement round, int seedA, int seedB, int winnerMatch)
        {
            var row = MakeRow();
            row.Add(MakeSeedSlot(seedA));
            row.Add(MakeVs());
            row.Add(MakeSeedSlot(seedB));
            row.Add(MakeArrow());
            row.Add(MakeWinnerSlot(winnerMatch));
            round.Add(row);
        }

        /// A semifinal/final row: both entrants come from earlier winners, so
        /// nothing here is draggable.
        private void AddResultRow(VisualElement round, int feederA, int feederB, int winnerMatch)
        {
            var row = MakeRow();
            row.Add(MakeWinnerSlot(feederA));
            row.Add(MakeVs());
            row.Add(MakeWinnerSlot(feederB));
            row.Add(MakeArrow());
            row.Add(MakeWinnerSlot(winnerMatch));
            round.Add(row);
        }

        /// Rows inside a round card. The bottom margin is both the gap between
        /// rows and — on the last row — the card's bottom inset; see AddRound.
        private static VisualElement MakeRow()
        {
            VisualElement row = Systems_UiKit.Row();
            row.style.marginBottom = Systems_UiKit.SPACE_3;
            return row;
        }

        /// The separators are fixed furniture between elastic chips: flexShrink 0
        /// so the row takes its slack out of the chips (which have a minWidth and
        /// clip gracefully) rather than out of a two-character label.
        private static Label MakeVs()
        {
            Label label = Systems_UiKit.Text("v", Systems_UiKit.FONT_SMALL, Systems_UiKit.TextLow);
            label.style.flexShrink = 0;
            label.style.marginLeft = Systems_UiKit.SPACE_1;
            label.style.marginRight = Systems_UiKit.SPACE_1;
            return label;
        }

        private static Label MakeArrow()
        {
            Label label = Systems_UiKit.Text("→", Systems_UiKit.FONT_BODY, Systems_UiKit.TextLow);
            label.style.flexShrink = 0;
            label.style.marginLeft = Systems_UiKit.SPACE_2;
            label.style.marginRight = Systems_UiKit.SPACE_2;
            return label;
        }

        private VisualElement MakeSeedSlot(int seedIndex)
        {
            VisualElement slot = MakeChip(Systems_TournamentState.GetSeed(seedIndex));
            slot.userData = seedIndex;
            _seedSlots.Add(slot);
            int captured = seedIndex;
            slot.RegisterCallback<PointerDownEvent>(evt =>
                BeginDrag(evt, captured, Systems_TournamentState.GetSeed(captured)));
            return slot;
        }

        private VisualElement MakeWinnerSlot(int matchIndex)
        {
            VisualElement slot = MakeChip(Systems_TournamentState.GetWinner(matchIndex));
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
        ///
        /// Elastic: it takes an equal share of whatever its row has left after the
        /// separators, down to SLOT_SIZE. flexBasis 0 is what makes the shares
        /// EQUAL — with the default `auto` basis the row would divide only the
        /// slack, and a chip holding "STANDARD" would end up wider than one holding
        /// "KIM". Use MakeGhostChip for the drag ghost, which has no row.
        private static VisualElement MakeChip(Agent_CharacterDefinition character)
        {
            VisualElement chip = Systems_UiKit.Row();
            chip.style.flexGrow = 1;
            chip.style.flexBasis = 0;
            chip.style.minWidth = SLOT_SIZE;
            // Comfortably over TOUCH_MIN: these are drag handles, not just labels.
            // 54 -> 66 and the name a step up the type scale, spending some of the
            // slack the bracket leaves above the pinned footer. The bracket is the
            // screen's content; it should not be the smallest thing on it.
            chip.style.height = 66;
            chip.style.backgroundColor = Systems_UiKit.Chip;
            // Belt and braces against a name longer than the chip: clip it here
            // rather than let it spill over the neighbouring slot.
            chip.style.overflow = Overflow.Hidden;
            chip.Round(Systems_UiKit.RADIUS_SM);
            chip.style.borderLeftWidth = 4;
            chip.style.borderLeftColor = character != null ? character.teamColor : Systems_UiKit.Chip;
            chip.style.paddingLeft = Systems_UiKit.SPACE_1;

            // 46, not 50. Every point the portrait takes comes straight off the
            // name, and the roster's longest — STANDARD — needs ~105pt at
            // FONT_SMALL bold against the ~165 flexGrow gives the whole chip.
            var icon = new VisualElement();
            icon.style.width = 46;
            icon.style.height = 46;
            // The portrait is fixed furniture: it must never be the thing that
            // gives way when a long name overflows the chip. Without this the
            // name wins and the icon collapses to nothing.
            icon.style.flexShrink = 0;
            icon.Round(23);
            icon.style.backgroundColor = character != null
                ? character.teamColor
                : new Color(0.25f, 0.23f, 0.24f);
            if (character != null && character.headSprite != null)
            {
                icon.style.backgroundImage = new StyleBackground(character.headSprite);
                icon.style.backgroundColor = Color.clear;
            }
            chip.Add(icon);

            // FONT_SMALL, not a step up: "STANDARD" bold at 21pt needs ~110pt and
            // the chip only has ~165 after the 50pt portrait and its padding, so
            // the longest name on the roster clipped. The chip gained its emphasis
            // in HEIGHT instead, which costs nothing horizontally.
            Label name = Systems_UiKit.Text(
                ChipName(character),
                Systems_UiKit.FONT_SMALL,
                character != null ? character.teamColor : Systems_UiKit.TextLow,
                true);
            name.style.marginLeft = Systems_UiKit.SPACE_1;
            chip.Add(name);
            return chip;
        }

        /// Three stacked bars at the right edge of a palette chip: the universal
        /// "this can be dragged" affordance.
        ///
        /// Without it the ONLY cue that the roster is interactive was the hint
        /// line above it, which is one small grey sentence on a screen where
        /// nothing else moves. On a touch screen there is no hover to discover it
        /// with, so a player who did not read the line had no way to learn the
        /// draw is editable at all.
        ///
        /// DRAWN, not a glyph. A hamburger character would be the obvious way to
        /// do this and is the wrong one here: the project ships no font asset, so
        /// anything outside the default UI font's coverage renders as a box. Three
        /// VisualElements cannot fail that way.
        ///
        /// Non-pickable, or it would swallow the PointerDownEvent that starts the
        /// very drag it is advertising.
        private static VisualElement DragGrip()
        {
            VisualElement grip = Systems_UiKit.Column();
            grip.style.width = 14;
            grip.style.flexShrink = 0;
            // Auto, so the grip is pushed to the CHIP'S RIGHT EDGE rather than
            // sitting against the end of the name. The name has no flexGrow, so
            // without this the grip would float mid-chip and read as punctuation.
            grip.style.marginLeft = StyleKeyword.Auto;
            grip.style.marginRight = Systems_UiKit.SPACE_1;
            grip.style.justifyContent = Justify.Center;
            grip.NoPick();

            for (int bar = 0; bar < 3; bar++)
            {
                var line = new VisualElement();
                line.style.height = 2;
                line.style.marginTop = bar == 0 ? 0 : 3;
                line.style.backgroundColor = Systems_UiKit.TextLow;
                line.NoPick();
                grip.Add(line);
            }
            return grip;
        }

        /// Chip label, with a brainless entrant marked as such.
        ///
        /// A character with no `inferenceModel` has no policy: it collapses as a
        /// ragdoll, loses on `downOutSeconds`, and since 2026-08-07 its bouts are
        /// unrated. `Bot_v01` is exactly this, deliberately. Presenting it in the
        /// palette and the draw with the same treatment as a trained fighter told
        /// the player it was a peer, and it is not — a measured bracket had it
        /// WINNING a quarterfinal, which reads as a broken fighter rather than an
        /// intentional dummy.
        ///
        /// The separator is the interpunct already used elsewhere on this screen
        /// ("CAREER · BANZUKE"), and the suffix is plain ASCII on purpose: this
        /// project ships no font asset, so an unsupported glyph draws as a box.
        /// "BOT" is short enough that the suffix fits the width "STANDARD" needs;
        /// the chip clips rather than spills if a longer brainless name is added.
        private static string ChipName(Agent_CharacterDefinition character)
        {
            if (character == null)
            {
                return "—";
            }
            string label = character.behaviorName.ToUpperInvariant();
            return character.inferenceModel == null ? label + "  ·  DUMMY" : label;
        }

        /// The floating drag ghost. Absolutely positioned, so it is outside the
        /// flex flow entirely and has to carry a real width — MoveGhost centres it
        /// on the pointer against this same number.
        private static VisualElement MakeGhostChip(Agent_CharacterDefinition character)
        {
            VisualElement chip = MakeChip(character);
            chip.style.position = Position.Absolute;
            chip.style.flexGrow = 0;
            chip.style.width = SLOT_SIZE;
            chip.style.opacity = 0.85f;
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
            _dragGhost = MakeGhostChip(character);
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

            // The match count on the career button moves after every bout, and this
            // screen is shown again between matches — so it is refreshed here rather
            // than only at build time.
            RefreshCareerButton();
            RefreshRankNews();
            RefreshStandings();

            // Only meaningful mid-bracket: before START there is nothing to step
            // through, and once a champion is crowned nothing is left to play.
            bool running = Systems_TournamentState.Active && !Systems_TournamentState.IsComplete;
            if (_autoButton != null)
            {
                _autoButton.style.display = running ? DisplayStyle.Flex : DisplayStyle.None;
                _autoButton.text = _autoPlay ? "AUTO-PLAY: ON  ·  TAP TO STEP MANUALLY"
                                             : "AUTO-PLAY: OFF  ·  TAP TO RESUME";
            }

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
            // SLOTS, not entrants. SEED_COUNT is 8 but the roster is 5, so three
            // fighters are drawn TWICE and can meet themselves — and a mirror bout
            // scores for nobody (`Systems_CareerRecorder` logs a warning saying so).
            // Calling eight slots "8 entrants" told the player there were eight
            // distinct fighters and made the repeats look like a seeding bug.
            _statusLabel.text = $"{Systems_TournamentState.SEED_COUNT} slots · {DistinctFighters()} fighters"
                                + " · single elimination" + BestOfClause();
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
            name.text = ChipName(character);
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

        /// The START / PLAY MATCH / RESET action, reachable without a pointer.
        ///
        /// The button is a UI Toolkit `clicked` callback, so until this existed the
        /// only way to begin a tournament was a real tap: MatchTestHarness could
        /// chain exhibition matches but could not touch the bracket at all, which
        /// left the path the shipped game always takes — boot into SCN_TOURNAMENT,
        /// press START — with no automated coverage. That is how two
        /// NullReferenceExceptions per bout survived in it unnoticed.
        public void PressAction() => OnAction();

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

        /// Flip between watching the bracket play itself and stepping it by hand.
        /// Resets the between-match timer so turning AUTO back on does not fire a
        /// match instantly with whatever the timer had already accumulated.
        private void ToggleAuto()
        {
            _autoPlay = !_autoPlay;
            _autoTimer = 0f;
            Refresh();
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
