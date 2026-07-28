using UnityEngine;
using UnityEngine.UIElements;

namespace PoSumo
{
    /// The one UIDocument the match screen draws through, and the three-band
    /// layout every other HUD component attaches into.
    ///
    /// Why one document. Systems_GameMatchManager, Systems_FightHud and
    /// Systems_MomentumGraph each used to add their own UIDocument against the
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
    ///     root .......... safe-area padded, never hit-tested
    ///     +- backdrop ... dim behind a blocking dialog
    ///     +- column ..... top-to-bottom flex, never hit-tested
    ///     |  +- TopBar .. pause | scorebug | stats, fixed height
    ///     |  +- Stage ... everything between; the fighters show through it
    ///     |  +- Dock .... momentum graph and the stats drawer
    ///     +- modal ...... centred dialogs, above everything
    ///
    /// Bands size themselves, so nothing is pinned to a pixel offset that was
    /// measured against one aspect ratio.
    ///
    /// Two exclusivity layers are enforced here rather than left to callers:
    /// `ShowCentre` for the transient callouts drawn over the arena (countdown,
    /// round banner) and `ShowModal` for dialogs that take over the screen
    /// (result card, pause menu). Showing one member of a layer hides its
    /// siblings, so the 120pt countdown digit can no longer land on top of the
    /// result card. The two layers are deliberately independent: pausing during a
    /// countdown should dim the countdown, not race with it.
    ///
    /// Created on demand — whichever component asks first builds it. Start order
    /// between the match manager and the fight HUD is undefined, so neither can
    /// own it.
    public sealed class Systems_HudRoot : MonoBehaviour
    {
        UIDocument _document;
        VisualElement _backdrop;
        VisualElement _centre;
        VisualElement _modal;

        /// Fixed-height row across the top: pause, scorebug, stats toggle.
        public VisualElement TopBarLeft { get; private set; }
        public VisualElement TopBarCentre { get; private set; }
        public VisualElement TopBarRight { get; private set; }

        /// The band between the bars. Reserved for transient callouts; it is
        /// never hit-tested, so the arena reads through it untouched.
        public VisualElement Stage { get; private set; }

        /// The bottom band. The momentum graph and the stats drawer live here —
        /// the widest, safest strip of a portrait screen, and previously empty.
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
        void Build(PanelSettings settings)
        {
            _document = gameObject.AddComponent<UIDocument>();
            if (settings != null)
            {
                _document.panelSettings = settings;
            }

            VisualElement root = _document.rootVisualElement;
            root.style.flexGrow = 1;
            root.NoPick();
            // One safe-area watcher for the whole HUD. Previously the match
            // manager and the graph each attached one and the fight HUD attached
            // none, so its panels and its STATS chip sat under the notch and the
            // gesture bar on every device that has them.
            Systems_SafeArea.Attach(transform, root);

            _backdrop = new VisualElement().Fill();
            _backdrop.style.backgroundColor = Systems_UiKit.Backdrop;
            _backdrop.style.display = DisplayStyle.None;

            VisualElement column = new VisualElement().Fill().NoPick();
            column.style.flexDirection = FlexDirection.Column;
            root.Add(column);

            column.Add(BuildTopBar());

            Stage = new VisualElement().NoPick();
            Stage.style.flexGrow = 1;
            Stage.style.justifyContent = Justify.Center;
            Stage.style.alignItems = Align.Center;
            column.Add(Stage);

            _centre = new VisualElement().NoPick();
            _centre.style.width = Length.Percent(100);
            _centre.style.alignItems = Align.Center;
            Stage.Add(_centre);

            Dock = new VisualElement().NoPick();
            Dock.style.flexShrink = 0;
            Dock.style.paddingLeft = Systems_UiKit.SPACE_3;
            Dock.style.paddingRight = Systems_UiKit.SPACE_3;
            Dock.style.paddingBottom = Systems_UiKit.SPACE_3;
            column.Add(Dock);

            // Added after the bands, so a dialog dims the top bar and the dock as
            // well as the arena. The old overlay was a sibling on a different
            // panel from the stat panels and the graph and could not dim either.
            root.Add(_backdrop);

            _modal = new VisualElement().Fill().NoPick();
            // Bottom-anchored, not centred. A centred dialog lands squarely on
            // the fighters — and the result card in particular appears exactly
            // when the camera has pulled back to show the finish, so centring it
            // covered the one thing the shot exists to show. Down here it is also
            // nearer the thumb.
            _modal.style.justifyContent = Justify.FlexEnd;
            _modal.style.alignItems = Align.Center;
            _modal.style.paddingLeft = Systems_UiKit.SPACE_5;
            _modal.style.paddingRight = Systems_UiKit.SPACE_5;
            _modal.style.paddingBottom = Systems_UiKit.SPACE_5;
            root.Add(_modal);
        }

        VisualElement BuildTopBar()
        {
            VisualElement bar = Systems_UiKit.Row(Align.FlexStart).NoPick();
            bar.style.flexShrink = 0;
            bar.style.paddingLeft = Systems_UiKit.SPACE_2;
            bar.style.paddingRight = Systems_UiKit.SPACE_2;
            bar.style.paddingTop = Systems_UiKit.SPACE_2;

            // The side slots are a fixed width so the centre slot is genuinely
            // centred on the screen rather than centred on what is left over.
            TopBarLeft = Systems_UiKit.Row().NoPick();
            TopBarLeft.style.width = 104;
            TopBarLeft.style.justifyContent = Justify.FlexStart;
            TopBarLeft.style.flexShrink = 0;

            TopBarCentre = Systems_UiKit.Column(Align.Center).NoPick();
            TopBarCentre.style.flexGrow = 1;

            TopBarRight = Systems_UiKit.Row().NoPick();
            TopBarRight.style.width = 104;
            TopBarRight.style.justifyContent = Justify.FlexEnd;
            TopBarRight.style.flexShrink = 0;

            bar.Add(TopBarLeft);
            bar.Add(TopBarCentre);
            bar.Add(TopBarRight);
            return bar;
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
            for (int i = 0; i < _centre.childCount; i++)
            {
                _centre[i].style.display = _centre[i] == element
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

        /// Shows one dialog, hides the others, and raises the dim backdrop.
        public void ShowModal(VisualElement element)
        {
            for (int i = 0; i < _modal.childCount; i++)
            {
                _modal[i].style.display = _modal[i] == element
                    ? DisplayStyle.Flex
                    : DisplayStyle.None;
            }
            _backdrop.style.display = DisplayStyle.Flex;
        }

        /// Hides every dialog and drops the backdrop.
        public void HideModal()
        {
            for (int i = 0; i < _modal.childCount; i++)
            {
                _modal[i].style.display = DisplayStyle.None;
            }
            _backdrop.style.display = DisplayStyle.None;
        }
    }
}
