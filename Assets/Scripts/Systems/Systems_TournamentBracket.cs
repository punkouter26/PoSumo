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
    public class Systems_TournamentBracket : MonoBehaviour
    {
        [Tooltip("Characters available as entrants. With 4, each appears twice in the 8-slot bracket.")]
        [SerializeField] private Agent_CharacterDefinition[] _roster;
        [SerializeField] private PanelSettings _panelSettings;
        [Tooltip("Scene loaded to play each match.")]
        [SerializeField] private string _arenaScene = "SCN_SUMO";

        private const int SLOT_SIZE = 116;

        private readonly List<VisualElement> _seedSlots = new List<VisualElement>();
        private readonly List<VisualElement> _winnerSlots = new List<VisualElement>();
        private VisualElement _root;
        private VisualElement _dragGhost;
        private Label _statusLabel;
        private Button _actionButton;

        // Drag bookkeeping. _dragSeedIndex is -1 when dragging from the roster
        // palette instead of an existing seed slot.
        private bool _dragging;
        private int _dragSeedIndex = -1;
        private Agent_CharacterDefinition _dragCharacter;

        private static readonly Color PanelBg = new Color(0.09f, 0.08f, 0.09f, 0.95f);
        private static readonly Color SlotBg = new Color(0.16f, 0.14f, 0.15f, 1f);
        private static readonly Color Gold = new Color(1f, 0.85f, 0.3f);
        private static readonly Color TextDim = new Color(0.72f, 0.69f, 0.64f);

        private void Start()
        {
            if (_roster == null || _roster.Length == 0)
            {
                Debug.LogError("Systems_TournamentBracket: no roster assigned.");
                return;
            }

            // Returning from a match mid-tournament: keep the existing bracket.
            // Otherwise this is a fresh visit, so draw a new field.
            if (!Tournament_State.SeedsReady())
            {
                Tournament_State.AutoSeed(_roster, Time.frameCount);
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
            _root.style.paddingTop = 18;
            _root.style.paddingLeft = 14;
            _root.style.paddingRight = 14;

            var title = new Label("TOURNAMENT");
            title.style.unityTextAlign = TextAnchor.MiddleCenter;
            title.style.fontSize = 42;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.color = Gold;
            _root.Add(title);

            var hint = new Label("drag a fighter onto a slot to change it");
            hint.style.unityTextAlign = TextAnchor.MiddleCenter;
            hint.style.fontSize = 17;
            hint.style.color = TextDim;
            hint.style.marginBottom = 8;
            _root.Add(hint);

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
            AddResultRow(feederA: 4, feederB: 5, winnerMatch: Tournament_State.FINAL_MATCH);

            _statusLabel = new Label();
            _statusLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
            _statusLabel.style.fontSize = 24;
            _statusLabel.style.color = Gold;
            _statusLabel.style.marginTop = 10;
            _root.Add(_statusLabel);

            _actionButton = new Button(OnAction);
            _actionButton.style.height = 74;
            _actionButton.style.marginTop = 8;
            _actionButton.style.fontSize = 30;
            _actionButton.style.unityFontStyleAndWeight = FontStyle.Bold;
            _actionButton.style.color = new Color(0.08f, 0.06f, 0.05f);
            _actionButton.style.backgroundColor = Gold;
            Round(_actionButton, 12);
            _root.Add(_actionButton);

            var resetButton = new Button(OnReset) { text = "RESHUFFLE" };
            resetButton.style.height = 50;
            resetButton.style.marginTop = 6;
            resetButton.style.fontSize = 20;
            resetButton.style.color = TextDim;
            resetButton.style.backgroundColor = new Color(0.18f, 0.16f, 0.17f);
            Round(resetButton, 10);
            _root.Add(resetButton);

            // Floating ghost that follows the pointer during a drag.
            _dragGhost = MakeChip(null, SLOT_SIZE);
            _dragGhost.style.position = Position.Absolute;
            _dragGhost.style.display = DisplayStyle.None;
            _dragGhost.style.opacity = 0.85f;
            _root.Add(_dragGhost);

            _root.RegisterCallback<PointerMoveEvent>(OnPointerMove);
            _root.RegisterCallback<PointerUpEvent>(OnPointerUp);
        }

        private void BuildPalette()
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.justifyContent = Justify.Center;
            row.style.marginBottom = 10;
            _root.Add(row);

            for (int rosterIndex = 0; rosterIndex < _roster.Length; rosterIndex++)
            {
                Agent_CharacterDefinition character = _roster[rosterIndex];
                VisualElement chip = MakeChip(character, 88);
                chip.style.marginLeft = 5;
                chip.style.marginRight = 5;
                Agent_CharacterDefinition captured = character;
                chip.RegisterCallback<PointerDownEvent>(evt => BeginDrag(evt, -1, captured));
                row.Add(chip);
            }
        }

        private void AddRoundHeader(string text)
        {
            var header = new Label(text);
            header.style.fontSize = 18;
            header.style.color = TextDim;
            header.style.unityFontStyleAndWeight = FontStyle.Bold;
            header.style.marginTop = 10;
            header.style.marginBottom = 3;
            _root.Add(header);
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
            _root.Add(row);
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
            _root.Add(row);
        }

        private static VisualElement MakeRow()
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.marginBottom = 5;
            return row;
        }

        private static Label MakeVs()
        {
            var label = new Label("v");
            label.style.fontSize = 16;
            label.style.color = TextDim;
            label.style.marginLeft = 4;
            label.style.marginRight = 4;
            return label;
        }

        private static Label MakeArrow()
        {
            var label = new Label("→");
            label.style.fontSize = 20;
            label.style.color = TextDim;
            label.style.marginLeft = 6;
            label.style.marginRight = 6;
            return label;
        }

        private VisualElement MakeSeedSlot(int seedIndex)
        {
            VisualElement slot = MakeChip(Tournament_State.GetSeed(seedIndex), SLOT_SIZE);
            slot.userData = seedIndex;
            _seedSlots.Add(slot);
            int captured = seedIndex;
            slot.RegisterCallback<PointerDownEvent>(evt =>
                BeginDrag(evt, captured, Tournament_State.GetSeed(captured)));
            return slot;
        }

        private VisualElement MakeWinnerSlot(int matchIndex)
        {
            VisualElement slot = MakeChip(Tournament_State.GetWinner(matchIndex), SLOT_SIZE);
            slot.userData = matchIndex;
            while (_winnerSlots.Count <= matchIndex) _winnerSlots.Add(null);
            _winnerSlots[matchIndex] = slot;
            return slot;
        }

        /// A fighter chip: face sprite when the character has one, otherwise a
        /// colour block (Standard ships without face art), plus the name.
        private static VisualElement MakeChip(Agent_CharacterDefinition character, int width)
        {
            var chip = new VisualElement();
            chip.style.width = width;
            chip.style.height = 54;
            chip.style.flexDirection = FlexDirection.Row;
            chip.style.alignItems = Align.Center;
            chip.style.backgroundColor = SlotBg;
            Round(chip, 8);
            chip.style.borderLeftWidth = 4;
            chip.style.borderLeftColor = character != null ? character.teamColor : SlotBg;
            chip.style.paddingLeft = 4;

            var icon = new VisualElement();
            icon.style.width = 42;
            icon.style.height = 42;
            Round(icon, 21);
            icon.style.backgroundColor = character != null
                ? character.teamColor
                : new Color(0.25f, 0.23f, 0.24f);
            if (character != null && character.headSprite != null)
            {
                icon.style.backgroundImage = new StyleBackground(character.headSprite);
                icon.style.backgroundColor = Color.clear;
            }
            chip.Add(icon);

            var name = new Label(character != null ? character.behaviorName.ToUpperInvariant() : "—");
            name.style.fontSize = 16;
            name.style.unityFontStyleAndWeight = FontStyle.Bold;
            name.style.color = character != null ? character.teamColor : TextDim;
            name.style.marginLeft = 5;
            chip.Add(name);
            return chip;
        }

        private static void Round(VisualElement element, int radius)
        {
            element.style.borderTopLeftRadius = radius;
            element.style.borderTopRightRadius = radius;
            element.style.borderBottomLeftRadius = radius;
            element.style.borderBottomRightRadius = radius;
        }

        // --- drag and drop -------------------------------------------------

        private void BeginDrag(PointerDownEvent evt, int seedIndex, Agent_CharacterDefinition character)
        {
            if (Tournament_State.Active) return;   // bracket is locked once running
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
                    Tournament_State.SwapSeeds(_dragSeedIndex, target);
                }
                else
                {
                    Tournament_State.SetSeed(target, _dragCharacter);
                }
                Refresh();
            }
            _dragSeedIndex = -1;
            _dragCharacter = null;
        }

        private int SeedSlotUnder(Vector2 position)
        {
            for (int i = 0; i < _seedSlots.Count; i++)
            {
                if (_seedSlots[i].worldBound.Contains(position))
                {
                    return (int)_seedSlots[i].userData;
                }
            }
            return -1;
        }

        // --- refresh / flow ------------------------------------------------

        private void Refresh()
        {
            for (int i = 0; i < _seedSlots.Count; i++)
            {
                int seedIndex = (int)_seedSlots[i].userData;
                ApplyChip(_seedSlots[i], Tournament_State.GetSeed(seedIndex));
            }
            for (int match = 0; match < _winnerSlots.Count; match++)
            {
                if (_winnerSlots[match] != null)
                {
                    ApplyChip(_winnerSlots[match], Tournament_State.GetWinner(match));
                }
            }

            if (Tournament_State.IsComplete)
            {
                var champion = Tournament_State.Champion;
                _statusLabel.text = $"CHAMPION — {champion.behaviorName.ToUpperInvariant()}";
                _statusLabel.style.color = champion.teamColor;
                _actionButton.text = "NEW TOURNAMENT";
                return;
            }

            if (Tournament_State.Active)
            {
                Tournament_State.GetEntrants(Tournament_State.CurrentMatch, out var a, out var b);
                string aName = a != null ? a.behaviorName.ToUpperInvariant() : "?";
                string bName = b != null ? b.behaviorName.ToUpperInvariant() : "?";
                _statusLabel.text = $"MATCH {Tournament_State.CurrentMatch + 1} of 7 — {aName} v {bName}";
                _actionButton.text = "PLAY MATCH";
                return;
            }

            _statusLabel.text = "8 entrants · single elimination · one round per match";
            _actionButton.text = "START TOURNAMENT";
        }

        /// Repaint one chip in place. Rebuilding the element would lose the
        /// registered drag callbacks, so only the visuals are swapped.
        private static void ApplyChip(VisualElement chip, Agent_CharacterDefinition character)
        {
            var icon = chip[0];
            var name = (Label)chip[1];
            chip.style.borderLeftColor = character != null ? character.teamColor : SlotBg;
            icon.style.backgroundColor = character != null && character.headSprite == null
                ? character.teamColor
                : (character != null ? Color.clear : new Color(0.25f, 0.23f, 0.24f));
            icon.style.backgroundImage = character != null && character.headSprite != null
                ? new StyleBackground(character.headSprite)
                : new StyleBackground();
            name.text = character != null ? character.behaviorName.ToUpperInvariant() : "—";
            name.style.color = character != null ? character.teamColor : TextDim;
        }

        private void OnAction()
        {
            if (Tournament_State.IsComplete)
            {
                OnReset();
                return;
            }
            if (!Tournament_State.Active)
            {
                if (!Tournament_State.SeedsReady())
                {
                    _statusLabel.text = "every slot needs a fighter";
                    return;
                }
                Tournament_State.BeginTournament();
            }
            SceneManager.LoadScene(_arenaScene);
        }

        private void OnReset()
        {
            Tournament_State.ResetAll();
            Tournament_State.AutoSeed(_roster, Time.frameCount);
            Refresh();
        }
    }
}
