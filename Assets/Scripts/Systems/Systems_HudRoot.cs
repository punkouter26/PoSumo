using UnityEngine;
using UnityEngine.UIElements;

namespace PoSumo
{
    /// The one UIDocument the match screen draws through, and the three-band
    /// layout every other HUD component attaches into.
    ///
    /// Why one document. Systems_GameMatchManager, Systems_FightHud and
    /// several HUD systems each used to add their own UIDocument against the
    /// same PanelSettings, all at sorting order 0. Three panels at equal sorting
    /// order have no defined draw or pick order, which produced two real defects:
    /// the match manager's dim backdrop could not darken the stat panels or the
    /// graph because they were on a different panel, and the fight HUD's
    /// full-screen root — which never set PickingMode.Ignore — could sit above the
    /// match manager's panel and swallow taps aimed at REMATCH or pause. Sharing
    /// one document makes stacking a matter of child order, which is inspectable
    /// and deterministic.
    ///
    /// The layout, which replaces a set of unrelated absolute offsets (the graph
    /// at top:96, the score at bottom:480, four dialogs at 32/34/36/49 percent):
    ///
    ///     root .......... full bleed, never hit-tested
    ///     +- content .... SAFE-AREA INSET, never hit-tested
    ///     |  +- column .. top-to-bottom flex
    ///     |     +- TopBar .. pause | scorebug, content height
    ///     |     +- Stage ... the view of the fight, plus between-round detail
    ///     |     +- Dock .... the always-on live strip
    ///     +- backdrop ... dim behind a blocking dialog, NOT inset
    ///     +- modalSafe .. SAFE-AREA INSET
    ///        +- modal ... centred dialogs, above everything
    ///
    /// Bands size themselves, so nothing is pinned to a pixel offset that was
    /// measured against one aspect ratio — and Stage carries a proportional floor
    /// so the two HUD bands can never squeeze the view of the fight below 45% of
    /// the panel, whatever the aspect ratio. The panel scales on WIDTH (the
    /// PanelSettings' match is 0), so band heights in points are constant while
    /// the panel's height in points is not: on a 4:3 tablet in portrait a
    /// content-sized dock takes a proportionally much larger bite than it does on
    /// the 9:16 reference. The floor is what makes that safe; do NOT "fix" it by
    /// moving the PanelSettings to a balanced match instead, because that narrows
    /// the panel below 720pt on tall phones and overflows the bracket's chip row.
    ///
    /// Two exclusivity layers are enforced here rather than left to callers:
    /// `ShowCentre` for the transient callouts drawn over the arena (countdown,
    /// round banner) and `ShowModal` for dialogs that take over the screen
    /// (result card, pause menu). Showing one member of a layer hides its
    /// siblings, so the 112pt countdown digit can no longer land on top of the
    /// result card. The two layers are deliberately independent: pausing during a
    /// countdown should dim the countdown, not race with it.
    ///
    /// Created on demand — whichever component asks first builds it. Start order
    /// between the match manager and the fight HUD is undefined, so neither can
    /// own it.
    public sealed class Systems_HudRoot : MonoBehaviour
    {
        /// The view of the fight is never allowed below this share of the panel.
        private const int STAGE_MIN_PERCENT = 45;

        /// And the live strip is never allowed above this share of it.
        private const int DOCK_MAX_PERCENT = 28;

        private UIDocument _document;
        private VisualElement _backdrop;
        private VisualElement _centre;
        private VisualElement _modal;

        /// Row across the top: pause on the left, scorebug in the middle.
        public VisualElement TopBarLeft { get; private set; }
        public VisualElement TopBarCentre { get; private set; }
        public VisualElement TopBarRight { get; private set; }

        /// The band between the bars — the view of the fight. Carries the
        /// transient callouts and the fight HUD's between-round detail card, both
        /// of which are hidden while a round is live, so during play this band is
        /// clear glass.
        public VisualElement Stage { get; private set; }

        /// The bottom band. The fight HUD's always-on live strip lives here — the
        /// widest, safest strip of a portrait screen.
        public VisualElement Dock { get; private set; }

        /// Returns the scene's HUD root, building it if this is the first caller.
        /// `settings` is only consulted when the root does not exist yet.
        public static Systems_HudRoot Ensure(Transform owner, PanelSettings settings)
        {
            Systems_HudRoot existing = FindAnyObjectByType<Systems_HudRoot>();
            if (existing != null)
            {
                return existing;
            }
            var go = new GameObject("HudRoot");
            if (owner != null)
            {
                go.transform.SetParent(owner, false);
            }
            Systems_HudRoot created = go.AddComponent<Systems_HudRoot>();
            created.Build(settings);
            return created;
        }

        /// Built from Ensure rather than Awake: AddComponent runs Awake before the
        /// caller can hand over its PanelSettings, and a UIDocument built without
        /// one renders nothing at all.
        private void Build(PanelSettings settings)
        {
            _document = gameObject.AddComponent<UIDocument>();
            if (settings != null)
            {
                _document.panelSettings = settings;
            }

            VisualElement root = _document.rootVisualElement;
            root.style.flexGrow = 1;
            root.NoPick();

            // Three full-bleed sibling layers, in draw order. The safe-area inset
            // is applied to the first and third but deliberately NOT to the scrim
            // between them: an absolutely positioned child resolves left/right/
            // top/bottom against its parent's PADDING box, so a scrim under the
            // inset stopped at the notch and left an undimmed strip across the top
            // and bottom of the screen behind every dialog. The inset used to sit
            // on the document root, which is what put it there.
            VisualElement content = new VisualElement().Fill().NoPick();
            root.Add(content);

            _backdrop = new VisualElement().Fill();
            _backdrop.style.backgroundColor = Systems_UiKit.Backdrop;
            _backdrop.style.display = DisplayStyle.None;
            root.Add(_backdrop);

            VisualElement modalSafe = new VisualElement().Fill().NoPick();
            root.Add(modalSafe);

            // One watcher for the whole HUD. Previously the match manager and the
            // graph each attached one and the fight HUD attached none, so its
            // panels sat under the notch and the gesture bar on every device that
            // has them.
            Systems_SafeArea.Attach(transform, content, modalSafe);

            VisualElement column = new VisualElement().Fill().NoPick();
            column.style.flexDirection = FlexDirection.Column;
            content.Add(column);

            column.Add(BuildTopBar());

            Stage = new VisualElement().NoPick();
            Stage.style.flexGrow = 1;
            Stage.style.minHeight = Length.Percent(STAGE_MIN_PERCENT);
            Stage.style.justifyContent = Justify.Center;
            Stage.style.alignItems = Align.Center;
            column.Add(Stage);

            _centre = new VisualElement().NoPick();
            _centre.style.width = Length.Percent(100);
            _centre.style.alignItems = Align.Center;
            Stage.Add(_centre);

            Dock = new VisualElement().NoPick();
            Dock.style.flexShrink = 0;
            Dock.style.maxHeight = Length.Percent(DOCK_MAX_PERCENT);
            Dock.style.paddingLeft = Systems_UiKit.SPACE_3;
            Dock.style.paddingRight = Systems_UiKit.SPACE_3;
            Dock.style.paddingBottom = Systems_UiKit.SPACE_3;
            column.Add(Dock);

            _modal = new VisualElement().Fill().NoPick();
            // Bottom-anchored, not centred. A centred dialog lands squarely on
            // the fighters — and the result card in particular appears exactly
            // when the camera has pulled back to show the finish, so centring it
            // covered the one thing the shot exists to show. Down here it is also
            // nearer the thumb. The gutters live here rather than on modalSafe,
            // because the safe-area watcher owns that element's padding outright.
            _modal.style.justifyContent = Justify.FlexEnd;
            _modal.style.alignItems = Align.Center;
            _modal.style.paddingLeft = Systems_UiKit.SPACE_5;
            _modal.style.paddingRight = Systems_UiKit.SPACE_5;
            _modal.style.paddingBottom = Systems_UiKit.SPACE_5;
            modalSafe.Add(_modal);

            // ...but bottom-anchored measured as ON TOP OF the Dock, because the
            // modal layer fills the whole panel while the Dock only owns the last
            // band of it. The match-end result card sat at y 1791-1970 against a
            // Dock of 1832-1994: 138 of the Dock's 162pt hidden, i.e. the live
            // DOMINANCE strip and the round line were 85% covered at exactly the
            // moment a player looks at them to see how the match was won.
            //
            // Padding the modal layer by the Dock's height moves the rest position
            // up to the Dock's top edge, which keeps every reason for bottom
            // anchoring above (off the fighters, near the thumb) and costs nothing.
            //
            // Driven off the Dock's own GeometryChangedEvent rather than a constant:
            // the Dock is `maxHeight: 28%` of a panel that scales on WIDTH, so its
            // height is a different number on every aspect ratio, and this is the
            // only value that is right on all of them. It fires again whenever the
            // Dock's contents change size, so the modals follow it.
            Dock.RegisterCallback<GeometryChangedEvent>(_ =>
            {
                float dockHeight = Dock.resolvedStyle.height;
                _modal.style.paddingBottom = Systems_UiKit.SPACE_5 + dockHeight;
            });
        }

        /// Pause on the left, scorebug in the middle.
        ///
        /// The side slots used to be a hardcoded 104pt each and NOTHING was ever
        /// added to either: the pause chip was moved to the hardware back button
        /// and the fight HUD's STATS chip was deleted, leaving 208pt of reserved
        /// width serving no purpose but to centre the scorebug. Equal flex basis
        /// does that job with no fixed number in it, and the left slot now carries
        /// a real pause affordance again — on Android the only way to pause was
        /// the hardware back key, which nothing on screen advertised.
        private VisualElement BuildTopBar()
        {
            VisualElement bar = Systems_UiKit.Row(Align.FlexStart).NoPick();
            bar.style.flexShrink = 0;
            bar.style.paddingLeft = Systems_UiKit.SPACE_2;
            bar.style.paddingRight = Systems_UiKit.SPACE_2;
            bar.style.paddingTop = Systems_UiKit.SPACE_2;

            TopBarLeft = SideSlot(Justify.FlexStart);
            TopBarRight = SideSlot(Justify.FlexEnd);

            TopBarCentre = Systems_UiKit.Column(Align.Center).NoPick();
            TopBarCentre.style.flexShrink = 0;

            bar.Add(TopBarLeft);
            bar.Add(TopBarCentre);
            bar.Add(TopBarRight);
            return bar;
        }

        /// Zero flex basis plus equal grow: whatever slack the bar has is split
        /// evenly between the two sides, so the centre slot is genuinely centred
        /// on the screen no matter what either side holds.
        private static VisualElement SideSlot(Justify justify)
        {
            VisualElement slot = Systems_UiKit.Row().NoPick();
            slot.style.flexGrow = 1;
            slot.style.flexBasis = 0;
            slot.style.minWidth = Systems_UiKit.TOUCH_MIN;
            slot.style.justifyContent = justify;
            return slot;
        }

        // ---- Centre layer: transient callouts over the arena ----------------

        /// Registers a callout. It starts hidden; `ShowCentre` reveals it.
        public void AddCentre(VisualElement element)
        {
            element.style.display = DisplayStyle.None;
            _centre.Add(element);
        }

        /// Shows one callout and hides every other one, so the countdown and the
        /// round banner can never be on screen together.
        public void ShowCentre(VisualElement element)
        {
            for (int childIndex = 0; childIndex < _centre.childCount; childIndex++)
            {
                _centre[childIndex].style.display = _centre[childIndex] == element
                    ? DisplayStyle.Flex
                    : DisplayStyle.None;
            }
        }

        public void HideCentre(VisualElement element)
        {
            if (element != null)
            {
                element.style.display = DisplayStyle.None;
            }
        }

        // ---- Modal layer: dialogs that own the screen -----------------------

        /// Registers a dialog. It starts hidden; `ShowModal` reveals it.
        public void AddModal(VisualElement element)
        {
            element.style.display = DisplayStyle.None;
            _modal.Add(element);
        }

        /// Shows one dialog, hides the others, and raises the dim backdrop. Both
        /// are animated: a dialog that snaps on in one frame reads as a glitch,
        /// and this one arrives while the camera is still settling on the finish.
        public void ShowModal(VisualElement element)
        {
            for (int childIndex = 0; childIndex < _modal.childCount; childIndex++)
            {
                _modal[childIndex].style.display = _modal[childIndex] == element
                    ? DisplayStyle.Flex
                    : DisplayStyle.None;
            }
            _backdrop.style.display = DisplayStyle.Flex;
            _backdrop.FadeIn(Systems_UiKit.MOTION_FAST);
            if (element != null)
            {
                element.RiseIn(48f);
            }
        }

        /// Hides every dialog and drops the backdrop. Instant on the way out: the
        /// dismissal is the player's own input, and delaying it reads as lag.
        public void HideModal()
        {
            for (int childIndex = 0; childIndex < _modal.childCount; childIndex++)
            {
                _modal[childIndex].style.display = DisplayStyle.None;
            }
            _backdrop.style.display = DisplayStyle.None;
        }
    }
}
