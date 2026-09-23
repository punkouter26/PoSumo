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
        /// active pane.
        private VisualElement _content;
        /// The two tab panes and their host: BRACKET (title area, palette and
        /// rounds) and RECORD (promotion news, career/banzuke, bot ladder, rules).
        /// Replaces the ScrollView — the zero-scroll constraint (checklist #3)
        /// means every pane fits the viewport, and the overflow audit polices it.
        private const int TAB_BRACKET = 0;
        private const int TAB_RECORD = 1;
        private int _activeTab = TAB_BRACKET;
        private readonly List<Button> _tabButtons = new List<Button>();
        private VisualElement _paneHost;
        private VisualElement _paneBracket;
        private VisualElement _paneRecord;
        private Systems_CareerScreen _careerScreen;
        private Systems_PromotionCeremony _promotionCeremony;
        /// Promotion banner. Shown once, on the first Refresh after returning from
        /// a match that moved somebody up or down the banzuke. Lives on the
        /// BRACKET pane — that is where the player lands coming back from a bout.
        private Label _rankNews;
        private VisualElement _dragGhost;
        private Label _statusLabel;
        private Button _actionButton;
        private Button _resetButton;
        private VisualElement _paletteRow;
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

            // TAB ROW + PANE HOST — replaces the ScrollView (zero-scroll,
            // checklist #3). The tab row carries the TOP chrome reservation the
            // old scroll viewport carried: a margin takes the band out of flow, so
            // the FPS readout and title can never overlap a tab. The BOTTOM band
            // belongs to the pinned footer below, exactly as before. Each pane is
            // a plain column sized to fit the viewport — HudOverflowAudit (run by
            // BracketTestHarness) is what proves it fits at every aspect.
            var tabRow = Systems_UiKit.Row();
            tabRow.style.marginTop = Systems_UiKit.TOUCH_MIN + Systems_UiKit.SPACE_3;
            tabRow.style.paddingLeft = Systems_UiKit.SPACE_3;
            tabRow.style.paddingRight = Systems_UiKit.SPACE_3;
            tabRow.style.flexShrink = 0;
            screen.Add(tabRow);
            AddTab(tabRow, TAB_BRACKET, "BRACKET");
            AddTab(tabRow, TAB_RECORD, "RECORD");

            _paneHost = Systems_UiKit.Column();
            _paneHost.style.flexGrow = 1;
            _paneHost.style.minHeight = 0;
            screen.Add(_paneHost);

            // SCREEN CHROME — the five fixed corners, identical here and in the
            // arena: title, frame rate, menu, DBG, build version.
            //
            // This replaces a lone build stamp that used to be pinned top-left here
            // and existed on NO other screen, so the version was visible on the boot
            // screen and nowhere during a bout. One component owning all five
            // corners on both screens is what stops that kind of split reappearing.
            //
            // On `screen`, NOT `_root` and NOT `_content`, for the reasons the old
            // stamp was:
            //  - `_content` is inside the ScrollView, so chrome would scroll off.
            //  - `_root` is deliberately un-inset (see above), so on a notched
            //    device a top-left absolute child lands UNDER the cutout.
            //  - `screen` carries the safe-area inset, and absolute offsets resolve
            //    against the parent's PADDING box, so 0,0 here is the first safe
            //    pixel.
            // Added after the ScrollView so it draws over the content, and the layer
            // is NoPick so it cannot eat a pointer-down meant for the fighter
            // palette behind it.
            // Built here but PARENTED LAST, after the pinned footer below, so it
            // draws over it. Added at this point in the method it sat under the
            // footer and the DBG chip came out half-hidden behind RESHUFFLE.
            // Attaching children to an unparented element is fine — they come with
            // it when it is added.
            var chromeLayer = new VisualElement().Fill().NoPick();
            // The debug PANEL gets its own layer (arena parity): it is a
            // full-screen overlay, not a corner item, and HudOverflowAudit's
            // corner contract asserts the chrome layer holds exactly the five
            // corners — measured live 2026-09-22: sharing put the panel in as a
            // sixth child and failed the gate on the first run.
            var debugLayer = new VisualElement().Fill().NoPick();
            Systems_AgentDebug bracketDebug = Systems_AgentDebug.Attach(transform, debugLayer, null);
            Systems_ScreenChrome bracketChrome = Systems_ScreenChrome.Attach(
                transform, chromeLayer,
                // Resolved on press. The TR menu IS the route to the RECORD tab —
                // the career overlay it used to open no longer exists (it was
                // merged into that pane), so there is one entry point and no
                // second surface. The panes are built further down this method.
                () => SelectTab(TAB_RECORD),
                bracketDebug != null ? (System.Action)bracketDebug.Toggle : null);
            if (bracketDebug != null)
            {
                bracketDebug.BindChrome(bracketChrome);
            }

            // ---- Pane 1: BRACKET (banner + hint + palette + rounds) ----------
            //
            // No hero "TOURNAMENT" title: Systems_ScreenChrome owns TL — Title
            // per the enforced anchor contract, so the screen had TWO titles
            // stacked (checklist #4), and the hero was 46pt of vertical budget
            // the 4:3 panel does not have. The promotion banner sits at the top
            // so a player returning from a bout sees it without switching panes.
            _paneBracket = MakePane();
            _paneRecord = MakePane();
            _content = _paneBracket;

            _rankNews = Systems_UiKit.Text("", Systems_UiKit.FONT_SMALL, Systems_UiKit.Gold, true);
            _rankNews.style.unityTextAlign = TextAnchor.MiddleCenter;
            _rankNews.style.whiteSpace = WhiteSpace.Normal;
            _rankNews.style.display = DisplayStyle.None;
            _content.Add(_rankNews);

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

            // ---- Pane 2: RECORD (career/banzuke + bot ladder + rules) --------
            //
            // The old inline CAREER button, the top-3 standings block and the
            // career overlay's ScrollView are all folded in here (screen merging,
            // checklist #1): Systems_CareerScreen builds its segmented
            // BANZUKE/FIGHTERS view straight into this pane, the ladder card
            // follows, and the rules footnote closes it. The palette stays on the
            // BRACKET pane because dragging a fighter onto a SLOT only works when
            // the roster and the slots are on screen together — a ROSTER tab
            // would have severed every drag.
            _content = _paneRecord;
            _careerScreen = new Systems_CareerScreen(_paneRecord);
            BuildLadderCard();
            BuildRulesNote();

            // flex-shrink 0 on every pane child (was one pass over the scroll
            // content): a pane column taller than the viewport must OVERFLOW
            // visibly so HudOverflowAudit flags it, never compress silently.
            LockChildren(_paneBracket);
            LockChildren(_paneRecord);
            SelectTab(TAB_BRACKET);

            // The status line and the action buttons live OUTSIDE the pane host,
            // pinned to the bottom of the panel. A primary action must never
            // depend on the aspect ratio — at 16:9 a button inside the content
            // column once sat ~130px below the fold of a screen that otherwise
            // looked complete.
            var footer = new VisualElement();
            footer.style.paddingLeft = Systems_UiKit.SPACE_3;
            footer.style.paddingRight = Systems_UiKit.SPACE_3;
            // Clears the bottom chrome band — the DBG chip on the left and the
            // build stamp on the right. This footer IS the bottom of the screen, so
            // it is the element that has to make room for them; measured at
            // 1080x2400 without this, RESHUFFLE was drawn straight across the DBG
            // chip. One touch target plus its own inset, the same reservation
            // Systems_HudRoot.ReserveChrome makes for the arena's dock.
            footer.style.paddingBottom = Systems_UiKit.SPACE_3
                                       + Systems_UiKit.TOUCH_MIN + Systems_UiKit.SPACE_2;
            footer.style.flexShrink = 0;

            _statusLabel = Systems_UiKit.Text("", Systems_UiKit.FONT_LEAD, Systems_UiKit.Gold);
            _statusLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
            _statusLabel.style.whiteSpace = WhiteSpace.Normal;
            _statusLabel.style.marginTop = Systems_UiKit.SPACE_2;
            footer.Add(_statusLabel);

            // ONE ROW, not a stack (density, checklist #2): the primary action
            // shares the row with whichever secondary is up. RESHUFFLE (seeding
            // only) and AUTO (running only) are display-gated to be mutually
            // exclusive in Refresh, so the row holds 1, never 3 — and the footer
            // came down by one full control (~64pt) at every aspect.
            var buttonRow = Systems_UiKit.Row();
            buttonRow.style.marginTop = Systems_UiKit.SPACE_2;
            footer.Add(buttonRow);

            _actionButton = Systems_UiKit.PrimaryButton("", OnAction);
            _actionButton.style.flexGrow = 1;
            _actionButton.style.flexBasis = 0;
            buttonRow.Add(_actionButton);

            // Only offered while the draw is still editable. It was previously
            // always visible, which made it both redundant and dangerous: on a
            // finished bracket it did exactly what NEW TOURNAMENT does (OnAction
            // forwards to OnReset when complete), and between two matches of a
            // running bracket — this screen is shown for _betweenMatchSeconds
            // after every bout — one tap silently destroyed the tournament in
            // progress with no confirmation.
            _resetButton = Systems_UiKit.GhostButton("RESHUFFLE", OnReset);
            _resetButton.style.flexGrow = 1;
            _resetButton.style.flexBasis = 0;
            _resetButton.style.marginLeft = Systems_UiKit.SPACE_1;
            buttonRow.Add(_resetButton);

            // Manual play has always existed — `_autoPlay` is a serialized field
            // and the action button already reads "PLAY MATCH" when it is off —
            // but there was no way to reach it without the Inspector. So a player
            // who pressed START was committed to watching all seven bouts run
            // themselves with no pause, no skip and no way back except QUIT MATCH
            // from inside a bout. Shown only while a bracket is running, because
            // that is the only time the distinction means anything. Compact label:
            // the row splits ~190pt a side at 4:3 and FONT_LEAD, so the old
            // two-clause caption would clip (status line still carries detail).
            _autoButton = Systems_UiKit.GhostButton("", ToggleAuto);
            _autoButton.style.flexGrow = 1;
            _autoButton.style.flexBasis = 0;
            _autoButton.style.marginLeft = Systems_UiKit.SPACE_1;
            buttonRow.Add(_autoButton);

            screen.Add(footer);

            // Debug panel first, chrome last, so the five corners draw above both
            // the panes, the pinned footer AND the debug panel. Both layers are
            // NoPick, so being on top costs the controls underneath nothing.
            screen.Add(debugLayer);
            screen.Add(chromeLayer);

            // Floating ghost that follows the pointer during a drag.
            _dragGhost = MakeGhostChip(null);
            _dragGhost.style.display = DisplayStyle.None;
            _root.Add(_dragGhost);

            // Added to `_root` LAST so it draws over the footer and the ghost. It
            // is a modal: while it is open nothing behind it should be reachable,
            // and UI Toolkit resolves both draw and pick order by document order
            // here. (The career view is no longer a modal — it was built into the
            // RECORD pane back in BuildUi.)
            //
            // The ceremony stays a full-screen overlay: it is the FALLBACK
            // announcement for a rank change the arena's result card never showed
            // (a reveal that never ran), and it must draw over everything when it
            // does fire.
            _promotionCeremony = new Systems_PromotionCeremony(_root);

            Systems_SafeArea.Attach(transform, screen, _promotionCeremony.SafeAreaTarget);

            _root.RegisterCallback<PointerMoveEvent>(OnPointerMove);
            _root.RegisterCallback<PointerUpEvent>(OnPointerUp);
        }

        /// A fresh pane in the pane host: a padded column, hidden until its tab
        /// is selected. flexGrow so the visible pane claims the whole area
        /// between the tab row and the pinned footer.
        private VisualElement MakePane()
        {
            var pane = Systems_UiKit.Column();
            pane.style.flexGrow = 1;
            pane.style.paddingTop = Systems_UiKit.SPACE_2;
            pane.style.paddingLeft = Systems_UiKit.SPACE_3;
            pane.style.paddingRight = Systems_UiKit.SPACE_3;
            pane.style.paddingBottom = Systems_UiKit.SPACE_4;
            pane.style.display = DisplayStyle.None;
            _paneHost.Add(pane);
            return pane;
        }

        /// One tab chip. ChipButton so the press feedback comes with it — a
        /// hand-rolled chip is visually dead on press because StyleButton writes
        /// backgroundColor inline.
        private void AddTab(VisualElement row, int tab, string label)
        {
            Button button = Systems_UiKit.ChipButton(label, () => SelectTab(tab), 0);
            button.style.flexGrow = 1;
            button.style.flexBasis = 0;
            button.style.marginLeft = Systems_UiKit.SPACE_1;
            button.style.marginRight = Systems_UiKit.SPACE_1;
            _tabButtons.Add(button);
            row.Add(button);
        }

        /// Shows one pane, hides the other, repaints the selection state.
        /// Retained elements throughout — the buttons are styled, never rebuilt.
        /// The RECORD pane rebuilds the career view on entry so its banzuke and
        /// records are current (the accordion's open row survives via
        /// Systems_CareerScreen's own `_expandedFighter`).
        private void SelectTab(int tab)
        {
            if (tab != TAB_BRACKET && tab != TAB_RECORD)
            {
                return;
            }
            _activeTab = tab;
            if (_paneBracket != null)
            {
                _paneBracket.style.display = tab == TAB_BRACKET ? DisplayStyle.Flex : DisplayStyle.None;
            }
            if (_paneRecord != null)
            {
                _paneRecord.style.display = tab == TAB_RECORD ? DisplayStyle.Flex : DisplayStyle.None;
            }
            for (int buttonIndex = 0; buttonIndex < _tabButtons.Count; buttonIndex++)
            {
                bool selected = buttonIndex == tab;
                Button button = _tabButtons[buttonIndex];
                button.style.color = selected ? Systems_UiKit.Gold : Systems_UiKit.TextHi;
                button.style.borderBottomWidth = selected ? 3 : 0;
                button.style.borderBottomColor = Systems_UiKit.Gold;
            }
            if (tab == TAB_RECORD)
            {
                _careerScreen?.Rebuild();
            }
        }

        /// flex-shrink 0 on every child of a pane. A pane column TALLER than the
        /// viewport would otherwise be silently COMPRESSED (flex-shrink defaults
        /// to 1) — no error, no scroller, just two things painted over each other.
        /// Locking shrink turns that silent squash into a visible overflow, which
        /// is exactly what HudOverflowAudit flags. MEASURED 2026-08-25 at
        /// 1080x1920 before the original version of this pass: the palette laid
        /// its three wrapped lines out to y=210 inside a 139pt row.
        private static void LockChildren(VisualElement content)
        {
            for (int childIndex = 0; childIndex < content.childCount; childIndex++)
            {
                content[childIndex].style.flexShrink = 0;
            }
        }

        /// The only place the rules are stated anywhere on this screen. A player
        /// watching a bout has no way to work out that the mat closing is the
        /// deadline — it reads as two fighters milling about until one falls.
        private void BuildRulesNote()
        {
            Label rules = Systems_UiKit.Text(
                "WIN A ROUND BY PUTTING YOUR OPPONENT OFF THE DOHYO. "
                + "THE MAT SHRINKS UNTIL SOMEBODY FALLS.",
                Systems_UiKit.FONT_MICRO, Systems_UiKit.TextLow);
            rules.style.whiteSpace = WhiteSpace.Normal;
            rules.style.unityTextAlign = TextAnchor.MiddleCenter;
            rules.style.marginTop = Systems_UiKit.SPACE_3;
            rules.style.paddingLeft = Systems_UiKit.SPACE_3;
            rules.style.paddingRight = Systems_UiKit.SPACE_3;
            rules.NoPick();
            _content.Add(rules);
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
            if (change.Promoted && !string.IsNullOrEmpty(change.Note))
            {
                _rankNews.text += $"  ·  {change.Note}";
            }
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

            VisualElement card = Systems_UiKit.ElevatedCard(
                Systems_UiKit.Elevation.Raised, Systems_UiKit.RADIUS_MD);
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
            // Still comfortably over TOUCH_MIN (44): these are drag handles, not
            // just labels. But 66 -> 48: with the ScrollView gone the palette and
            // ALL THREE round cards share one viewport, and at 4:3 (a 960pt
            // panel) the pre-seed stack measured ~66pt over the line at 66. The
            // chip shrinks so the screen does not overflow — the audit is what
            // says so, and this is the dial it pushed.
            chip.style.height = 48;
            chip.style.backgroundColor = Systems_UiKit.Chip;
            // Belt and braces against a name longer than the chip: clip it here
            // rather than let it spill over the neighbouring slot.
            chip.style.overflow = Overflow.Hidden;
            chip.Round(Systems_UiKit.RADIUS_SM);
            chip.style.borderLeftWidth = 4;
            chip.style.borderLeftColor = character != null ? character.teamColor : Systems_UiKit.Chip;
            chip.style.paddingLeft = Systems_UiKit.SPACE_1;

            // 38, tracking the 48pt chip (see CHIP height above): the portrait
            // gives up 8pt so the pre-seed stack fits one viewport, and the name
            // keeps its ~105pt for STANDARD at FONT_SMALL bold.
            var icon = new VisualElement();
            icon.style.width = 38;
            icon.style.height = 38;
            // The portrait is fixed furniture: it must never be the thing that
            // gives way when a long name overflows the chip. Without this the
            // name wins and the icon collapses to nothing.
            icon.style.flexShrink = 0;
            icon.Round(19);
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
            // Half the chip height (48), so the ghost sits under the finger —
            // this was 27f, half of a 54pt chip that stopped existing long ago.
            _dragGhost.style.top = position.y - 24f;
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
            RefreshLadder();
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

            // The promotion banner and the career view both move after every bout,
            // and this screen is shown again between matches — so both are
            // refreshed here rather than only at build time. The career view only
            // rebuilds when its pane is on screen: no point repainting a hidden
            // pane on every drag (SelectTab rebuilds it on entry anyway).
            RefreshRankNews();
            if (_activeTab == TAB_RECORD)
            {
                _careerScreen?.Rebuild();
            }

            // Only meaningful mid-bracket: before START there is nothing to step
            // through, and once a champion is crowned nothing is left to play.
            bool running = Systems_TournamentState.Active && !Systems_TournamentState.IsComplete;
            if (_autoButton != null)
            {
                _autoButton.style.display = running ? DisplayStyle.Flex : DisplayStyle.None;
                // Compact (checklist #2): the footer row splits its width three
                // ways at FONT_LEAD, and the old two-clause caption clipped. The
                // status line above still says what stepping manually does.
                _autoButton.text = _autoPlay ? "AUTO: ON" : "AUTO: OFF";
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

        // ---- BOT LADDER ----------------------------------------------------------
        //
        // A card under the career row: pick a challenger, pick a rung, fight the
        // Bot. Lives on this screen rather than its own because the roster, the
        // arena launch and the "you came back with a result" news line are all
        // already here. State is Systems_BotLadderState; this is only the view.

        private readonly List<Button> _ladderChallengerButtons = new List<Button>();
        private readonly List<Button> _ladderTierButtons = new List<Button>();
        private Label _ladderStatus;
        private Label _ladderNews;

        /// Selects the RECORD tab, where the ladder card lives. Kept under its
        /// old name because the screenshot flow calls it — but there is nothing
        /// to scroll any more: the zero-scroll constraint put the card on a pane
        /// that fits the viewport, so bringing it into view IS switching to it.
        public void ScrollToLadder()
        {
            SelectTab(TAB_RECORD);
        }
        private Agent_CharacterDefinition _ladderChallenger;
        private Agent_CharacterDefinition _ladderBot;

        private void BuildLadderCard()
        {
            if (_roster == null) return;
            _ladderBot = null;
            for (int index = 0; index < _roster.Length; index++)
            {
                if (_roster[index] != null && _roster[index].useBot) { _ladderBot = _roster[index]; break; }
            }
            if (_ladderBot == null) return;   // no Bot in the roster, no ladder

            VisualElement card = AddRound("BOT LADDER");
            card.style.paddingBottom = Systems_UiKit.SPACE_3;

            Label hint = Systems_UiKit.Caption("beat the bot at each rung to unlock the next",
                                               Systems_UiKit.FONT_MICRO, Systems_UiKit.TextLow);
            hint.style.unityTextAlign = TextAnchor.MiddleCenter;
            hint.style.marginBottom = Systems_UiKit.SPACE_2;
            card.Add(hint);

            // Challenger chips: every trained fighter, 2-up like the palette.
            VisualElement challengers = Systems_UiKit.Row();
            challengers.style.flexWrap = Wrap.Wrap;
            challengers.style.justifyContent = Justify.Center;
            card.Add(challengers);
            _ladderChallengerButtons.Clear();
            for (int index = 0; index < _roster.Length; index++)
            {
                Agent_CharacterDefinition character = _roster[index];
                if (character == null || character.useBot) continue;
                if (_ladderChallenger == null) _ladderChallenger = character;
                Agent_CharacterDefinition captured = character;
                Button chip = Systems_UiKit.ChipButton(character.behaviorName.ToUpperInvariant(),
                                                      () => { _ladderChallenger = captured; RefreshLadder(); },
                                                      0);
                chip.style.flexGrow = 1;
                chip.style.flexBasis = Length.Percent(46f);
                chip.style.marginLeft = Systems_UiKit.SPACE_1;
                chip.style.marginRight = Systems_UiKit.SPACE_1;
                chip.style.marginBottom = Systems_UiKit.SPACE_1;
                chip.userData = character;
                challengers.Add(chip);
                _ladderChallengerButtons.Add(chip);
            }

            // Tier buttons: EASY / MEDIUM / HARD. Locked until the one below is beaten.
            VisualElement tiers = Systems_UiKit.Row();
            tiers.style.justifyContent = Justify.Center;
            tiers.style.marginTop = Systems_UiKit.SPACE_2;
            card.Add(tiers);
            _ladderTierButtons.Clear();
            for (int tier = 0; tier < Systems_BotLadderState.TIER_COUNT; tier++)
            {
                int captured = tier;
                Button button = Systems_UiKit.ChipButton(Systems_BotLadderState.TierNames[tier],
                                                        () => PressLadder(captured), 0);
                button.style.flexGrow = 1;
                button.style.flexBasis = 0;
                button.style.marginLeft = Systems_UiKit.SPACE_1;
                button.style.marginRight = Systems_UiKit.SPACE_1;
                tiers.Add(button);
                _ladderTierButtons.Add(button);
            }

            _ladderStatus = Systems_UiKit.Caption("", Systems_UiKit.FONT_MICRO, Systems_UiKit.TextLow);
            _ladderStatus.style.unityTextAlign = TextAnchor.MiddleCenter;
            _ladderStatus.style.marginTop = Systems_UiKit.SPACE_2;
            card.Add(_ladderStatus);

            _ladderNews = Systems_UiKit.Text("", Systems_UiKit.FONT_SMALL, Systems_UiKit.Gold, true);
            _ladderNews.style.unityTextAlign = TextAnchor.MiddleCenter;
            _ladderNews.style.whiteSpace = WhiteSpace.Normal;
            _ladderNews.style.display = DisplayStyle.None;
            card.Add(_ladderNews);

            RefreshLadder();
        }

        /// Launch a ladder bout at `tier` for the selected challenger. Public so a
        /// harness can drive it without a tap.
        public void PressLadder(int tier)
        {
            if (_ladderChallenger == null || _ladderBot == null) return;
            if (!Systems_BotLadderState.IsUnlocked(_ladderChallenger.behaviorName, tier))
            {
                if (_ladderStatus != null) _ladderStatus.text = "beat the rung below first";
                return;
            }
            // A ladder bout is played OUTSIDE the bracket; a tournament in progress
            // stays exactly where it is and resumes when you come back.
            Systems_BotLadderState.Begin(_ladderChallenger, _ladderBot, tier);
            SceneManager.LoadScene(ARENA_SCENE);
        }

        /// Writes text and style on retained elements — never rebuilds.
        private void RefreshLadder()
        {
            if (_ladderStatus == null || _ladderChallenger == null) return;
            string name = _ladderChallenger.behaviorName;
            int beaten = Systems_BotLadderState.RungsBeaten(name);

            for (int index = 0; index < _ladderChallengerButtons.Count; index++)
            {
                Button chip = _ladderChallengerButtons[index];
                var character = (Agent_CharacterDefinition)chip.userData;
                bool selected = character == _ladderChallenger;
                chip.style.color = selected ? character.teamColor : Systems_UiKit.TextLow;
                chip.style.borderBottomWidth = selected ? 3 : 0;
                chip.style.borderBottomColor = character.teamColor;
            }
            for (int tier = 0; tier < _ladderTierButtons.Count; tier++)
            {
                Button button = _ladderTierButtons[tier];
                bool unlocked = beaten >= tier;
                bool done = beaten > tier;
                // Plain ASCII suffixes: no font asset ships, so a glyph is a box on
                // any device whose default font lacks it (see PauseButton).
                button.text = done ? Systems_BotLadderState.TierNames[tier] + " - BEATEN"
                            : unlocked ? Systems_BotLadderState.TierNames[tier]
                            : Systems_BotLadderState.TierNames[tier] + " - LOCKED";
                button.style.color = done ? Systems_UiKit.Good : unlocked ? Systems_UiKit.Gold : Systems_UiKit.TextLow;
                button.style.opacity = unlocked ? 1f : 0.55f;
            }
            _ladderStatus.text = beaten >= Systems_BotLadderState.TIER_COUNT
                ? $"{name.ToUpperInvariant()} has cleared the ladder"
                : $"{name.ToUpperInvariant()} · {beaten}/{Systems_BotLadderState.TIER_COUNT} rungs beaten · tap a rung to fight";

            if (Systems_BotLadderState.TryTakeResult(out Systems_BotLadderState.Result result))
            {
                string tierName = Systems_BotLadderState.TierNames[result.Tier];
                string who = (result.Challenger ?? "?").ToUpperInvariant();
                _ladderNews.text = result.Won
                    ? (result.Tier + 1 < Systems_BotLadderState.TIER_COUNT
                        ? $"{who} BEAT THE {tierName} BOT — {Systems_BotLadderState.TierNames[result.Tier + 1]} UNLOCKED"
                        : $"{who} BEAT THE {tierName} BOT — LADDER CLEARED")
                    : $"THE {tierName} BOT HELD {who}";
                _ladderNews.style.color = result.Won ? Systems_UiKit.Gold : Systems_UiKit.Bad;
                _ladderNews.style.display = DisplayStyle.Flex;
                _ladderNews.FadeIn();
                Systems_Log.Info($"[LADDER] {_ladderNews.text}");
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
