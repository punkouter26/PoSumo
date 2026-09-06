using UnityEngine;
using UnityEngine.UIElements;

namespace PoSumo
{
    /// The tale of the tape, drawn into the empty band between the dohyo and the
    /// dock — about 37% of a portrait frame that was previously unlit crowd wall.
    ///
    /// WHY ONE COMPANION AND NOT SIX. Career identity, physique, stamina, push and
    /// damage are all "who are these two, and how are they doing" — one subject,
    /// one surface, one `enableFighterPanel` flag, one spawn line. Six flags for
    /// six rows of the same panel would be six things to keep in sync for no gain.
    ///
    /// WHAT MOVES DURING A BOUT AND WHAT DOES NOT, because this project has been
    /// bitten: `Systems_FightHud` used to pin a six-row aggregate table into the
    /// dock and it was cut, because "nobody parses a work-rate percentage while a
    /// bout is being decided" — and one of its rows cost a 13-iteration loop per
    /// fighter per frame for a number nobody acted on. So the NUMBERS here (elo,
    /// rank, record, weight) are settled facts refreshed on round boundaries, and
    /// only the BARS (stamina, push) and the damage pips move during a bout.
    /// Nothing in the live path allocates or calls GetComponent.
    ///
    /// Read-only with respect to the fight: it decides nothing and is not mirrored
    /// into `Systems_SumoMatchManager`, so no brain is affected by it.
    public sealed class Systems_FighterPanel : MonoBehaviour
    {
        public Systems_GameMatchManager manager;
        public PanelSettings panelSettings;

        /// Live bars refresh at 10 Hz. Fast enough that a stamina drain reads as
        /// motion, slow enough that it is nowhere near a per-frame cost.
        private const float LIVE_INTERVAL = 0.1f;

        /// Push is an impulse read off a one-step velocity delta, which is spiky by
        /// nature. Smoothed so the bar shows who is winning the shove rather than
        /// strobing on every contact.
        private const float PUSH_SMOOTHING = 6f;

        /// Full-scale for the push bar, in newtons.
        private const float PUSH_MAX = 400f;

        /// Every fighter rides the same 1.76 m Winter rig — there is no
        /// `heightScale` on the character asset, only mass, width and torque. So
        /// height is printed as the shared spec it is, and BUILD carries the
        /// variation instead.
        private const float RIG_HEIGHT_M = 1.76f;

        private Systems_HudRoot _hud;
        private VisualElement _panel;
        private Label _stakes;
        private Side _sideA, _sideB;
        private VisualElement _pushFillA, _pushFillB;
        private Label _pushLabelA, _pushLabelB;

        private Agent_BipedBody _bodyA, _bodyB;
        private Systems_BodyDamage _damageA, _damageB;

        private float _liveLeft;
        private float _pushA, _pushB;
        private Vector2 _prevVelA, _prevVelB;
        private bool _subscribed;

        /// One fighter's column. Held as a class of references so the two sides are
        /// built and repainted by the same code rather than mirrored by hand.
        private sealed class Side
        {
            public Label Name;
            public Label Rank;
            public Label Record;
            public Label Physique;
            public VisualElement StaminaFill;
            public VisualElement[] Pips;
            public Color[] PipShown;
        }

        // ---- Lifecycle ------------------------------------------------------

        private void OnEnable()
        {
            Subscribe();
        }

        private void Start()
        {
            if (manager == null)
            {
                manager = FindAnyObjectByType<Systems_GameMatchManager>();
            }

            Subscribe();
            BuildUi();
            ResolveBodies();
            RefreshStatic();
        }

        private void OnDisable()
        {
            Unsubscribe();
        }

        private void Subscribe()
        {
            if (manager == null || _subscribed)
            {
                return;
            }

            manager.RoundStarted += OnRoundStarted;
            manager.MatchReset += OnRoundStarted;
            _subscribed = true;
        }

        private void Unsubscribe()
        {
            if (manager == null || !_subscribed)
            {
                return;
            }

            manager.RoundStarted -= OnRoundStarted;
            manager.MatchReset -= OnRoundStarted;
            _subscribed = false;
        }

        /// Re-cache and re-read the settled numbers exactly when they can have
        /// changed — a round boundary. The 50 Hz and 10 Hz paths then carry no
        /// lookups at all.
        private void OnRoundStarted()
        {
            ResolveBodies();
            RefreshStatic();
        }

        private void ResolveBodies()
        {
            Agent_Biped fighterA = manager != null ? manager.wrestlerA : null;
            Agent_Biped fighterB = manager != null ? manager.wrestlerB : null;
            _bodyA = fighterA != null ? fighterA.GetComponent<Agent_BipedBody>() : null;
            _bodyB = fighterB != null ? fighterB.GetComponent<Agent_BipedBody>() : null;
            _damageA = _bodyA != null ? Systems_BodyDamage.For(_bodyA) : null;
            _damageB = _bodyB != null ? Systems_BodyDamage.For(_bodyB) : null;
            _pushA = 0f;
            _pushB = 0f;
        }

        // ---- Sampling -------------------------------------------------------

        /// Push is measured the way the aggregate table measures it: the force one
        /// fighter puts into the other is that other body's mass times its velocity
        /// change along the line between them. Taken in FixedUpdate because it is a
        /// per-physics-step delta — read on the render clock it would scale with
        /// frame rate.
        private void FixedUpdate()
        {
            if (_bodyA == null || _bodyB == null)
            {
                return;
            }

            Rigidbody2D torsoA = _bodyA.Torso;
            Rigidbody2D torsoB = _bodyB.Torso;
            if (torsoA == null || torsoB == null)
            {
                return;
            }

            Vector2 velocityA = torsoA.linearVelocity;
            Vector2 velocityB = torsoB.linearVelocity;
            float step = Time.fixedDeltaTime;
            float towardB = Mathf.Sign(torsoB.position.x - torsoA.position.x);

            float rawA = Mathf.Max(0f, (velocityB.x - _prevVelB.x) * towardB) * _bodyB.TotalMass / step;
            float rawB = Mathf.Max(0f, (_prevVelA.x - velocityA.x) * towardB) * _bodyA.TotalMass / step;

            float blend = 1f - Mathf.Exp(-PUSH_SMOOTHING * step);
            _pushA = Mathf.Lerp(_pushA, rawA, blend);
            _pushB = Mathf.Lerp(_pushB, rawB, blend);

            _prevVelA = velocityA;
            _prevVelB = velocityB;
        }

        private void Update()
        {
            if (_panel == null)
            {
                return;
            }

            _liveLeft -= Time.unscaledDeltaTime;
            if (_liveLeft > 0f)
            {
                return;
            }

            _liveLeft = LIVE_INTERVAL;
            RefreshLive();
        }

        // ---- Paint ----------------------------------------------------------

        private void RefreshStatic()
        {
            if (_panel == null)
            {
                return;
            }

            PaintSide(_sideA, manager != null ? manager.wrestlerA : null, _bodyA);
            PaintSide(_sideB, manager != null ? manager.wrestlerB : null, _bodyB);
            PaintStakes();
        }

        private static void PaintSide(Side side, Agent_Biped fighter, Agent_BipedBody body)
        {
            if (side == null)
            {
                return;
            }

            if (fighter == null)
            {
                side.Name.text = "—";
                side.Rank.text = string.Empty;
                side.Record.text = string.Empty;
                side.Physique.text = string.Empty;
                return;
            }

            side.Name.text = DisplayName(fighter);
            side.Name.style.color = TeamColor(fighter);

            string behaviour = BehaviourName(fighter);
            if (behaviour == null)
            {
                // The hand-coded bot is unrated on purpose — Systems_CareerRecorder
                // banks nothing for it, so printing an elo would invent one.
                side.Rank.text = "UNRATED";
                side.Record.text = "HEURISTIC BOT";
            }
            else
            {
                Systems_CareerStats.Record record = Systems_CareerStats.Get(behaviour);
                side.Rank.text = Mathf.RoundToInt(record.elo) + "  ·  " + Systems_CareerLadder.NameFor(record);
                string streak = record.winStreak >= 2 ? "  ·  W" + record.winStreak : string.Empty;
                side.Record.text = record.matchWins + "-" + record.matchLosses + streak;
            }

            if (body != null)
            {
                side.Physique.text = body.TotalMass.ToString("F0") + " kg  ·  "
                    + RIG_HEIGHT_M.ToString("F2") + " m  ·  " + BuildWord(body.widthScale);
            }
        }

        /// Weight follows geometry here — mass is 69.6 * massScale *
        /// (0.546*w^2 + 0.454) — so widthScale is the thing that actually separates
        /// a 96 kg Kim from a 57 kg Nick, and it deserves a word rather than a bare
        /// multiplier.
        private static string BuildWord(float widthScale)
        {
            if (widthScale >= 1.2f)
            {
                return "HEAVY";
            }

            if (widthScale >= 1.05f)
            {
                return "SOLID";
            }

            if (widthScale <= 0.9f)
            {
                return "LIGHT";
            }

            return "EVEN";
        }

        /// Head-to-head plus what is at stake. Both are read from the career
        /// record, which is keyed by behaviour name — the only fighter identity
        /// stable across folder and asset renames.
        private void PaintStakes()
        {
            if (_stakes == null)
            {
                return;
            }

            Agent_Biped fighterA = manager != null ? manager.wrestlerA : null;
            Agent_Biped fighterB = manager != null ? manager.wrestlerB : null;
            string nameA = BehaviourName(fighterA);
            string nameB = BehaviourName(fighterB);

            string head = "FIRST MEETING";
            if (nameA != null && nameB != null)
            {
                Systems_CareerStats.Record record = Systems_CareerStats.Get(nameA);
                int index = record.vsNames.IndexOf(nameB);
                if (index >= 0)
                {
                    int wins = record.vsWins[index];
                    int losses = record.vsLosses[index];
                    if (wins + losses > 0)
                    {
                        if (wins == losses)
                        {
                            head = "LEVEL " + wins + "-" + losses;
                        }
                        else if (wins > losses)
                        {
                            head = DisplayName(fighterA).ToUpperInvariant() + " LEADS " + wins + "-" + losses;
                        }
                        else
                        {
                            head = DisplayName(fighterB).ToUpperInvariant() + " LEADS " + losses + "-" + wins;
                        }
                    }
                }
            }

            string stage = Systems_TournamentState.Active
                ? Systems_TournamentState.RoundName(Systems_TournamentState.CurrentMatch)
                : "EXHIBITION";

            _stakes.text = stage + "  ·  " + head;
        }

        /// The only per-tick paint: two stamina bars, the push tug-of-war, and the
        /// damage pips. Each pip caches its last colour, because damage changes far
        /// more slowly than 10 Hz and writing an identical style still dirties the
        /// element.
        private void RefreshLive()
        {
            PaintStamina(_sideA, _bodyA);
            PaintStamina(_sideB, _bodyB);
            PaintPips(_sideA, _damageA);
            PaintPips(_sideB, _damageB);
            PaintPush();
        }

        private static void PaintStamina(Side side, Agent_BipedBody body)
        {
            if (side == null || side.StaminaFill == null)
            {
                return;
            }

            float stamina = body != null ? Mathf.Clamp01(body.Stamina) : 0f;
            side.StaminaFill.style.width = Length.Percent(stamina * 100f);
            side.StaminaFill.style.backgroundColor =
                stamina > 0.6f ? Systems_UiKit.Good
                : stamina > 0.3f ? Systems_UiKit.Warn
                : Systems_UiKit.Bad;
        }

        private static void PaintPips(Side side, Systems_BodyDamage damage)
        {
            if (side == null || side.Pips == null)
            {
                return;
            }

            for (int regionIndex = 0; regionIndex < side.Pips.Length; regionIndex++)
            {
                Color want;
                if (damage == null)
                {
                    want = Systems_UiKit.Track;
                }
                else if (damage.RegionDetached((Systems_BodyDamage.Region)regionIndex))
                {
                    // A missing limb is not "very damaged", it is gone — so it reads
                    // as absence rather than as the hot end of the damage ramp.
                    want = new Color(0.25f, 0.05f, 0.07f, 1f);
                }
                else
                {
                    float hurt = Mathf.Clamp01(damage.RegionDamage01((Systems_BodyDamage.Region)regionIndex));
                    want = hurt < 0.5f
                        ? Color.Lerp(Systems_UiKit.Good, Systems_UiKit.Warn, hurt * 2f)
                        : Color.Lerp(Systems_UiKit.Warn, Systems_UiKit.Bad, (hurt - 0.5f) * 2f);
                }

                if (side.PipShown[regionIndex] == want)
                {
                    continue;
                }

                side.PipShown[regionIndex] = want;
                side.Pips[regionIndex].style.backgroundColor = want;
            }
        }

        private void PaintPush()
        {
            if (_pushFillA == null || _pushFillB == null)
            {
                return;
            }

            float forceA = Mathf.Clamp(_pushA, 0f, PUSH_MAX);
            float forceB = Mathf.Clamp(_pushB, 0f, PUSH_MAX);
            _pushFillA.style.width = Length.Percent(forceA / PUSH_MAX * 100f);
            _pushFillB.style.width = Length.Percent(forceB / PUSH_MAX * 100f);
            _pushLabelA.text = forceA.ToString("F0") + " N";
            _pushLabelB.text = forceB.ToString("F0") + " N";
        }

        // ---- Identity helpers ----------------------------------------------

        private static string DisplayName(Agent_Biped fighter)
        {
            if (fighter == null)
            {
                return "—";
            }

            if (!string.IsNullOrEmpty(fighter.displayNameOverride))
            {
                return fighter.displayNameOverride;
            }

            return fighter.character != null ? fighter.character.behaviorName : fighter.name;
        }

        private static Color TeamColor(Agent_Biped fighter)
        {
            if (fighter == null || fighter.character == null)
            {
                return Systems_UiKit.TextHi;
            }

            return fighter.character.teamColor;
        }

        /// Null for the heuristic bot, matching Systems_CareerRecorder: a bot bout
        /// is unrated, so anything keyed on a career record must skip it rather
        /// than open a record under its name.
        private static string BehaviourName(Agent_Biped fighter)
        {
            if (fighter == null || fighter.character == null)
            {
                return null;
            }

            if (fighter.character.useBot)
            {
                return null;
            }

            return fighter.character.behaviorName;
        }

        // ---- Build ----------------------------------------------------------

        private void BuildUi()
        {
            PanelSettings settings = panelSettings != null ? panelSettings
                : manager != null ? manager.panelSettings : null;
            _hud = Systems_HudRoot.Ensure(transform, settings);
            if (_hud == null || _hud.Stage == null)
            {
                return;
            }

            // Anchored to the BOTTOM of the stage band rather than added to its
            // flow: Stage centres its children, and the between-rounds detail card
            // already lives there. An absolute child cannot push that card around.
            _panel = new VisualElement().NoPick();
            _panel.style.position = Position.Absolute;
            _panel.style.left = 0;
            _panel.style.right = 0;
            _panel.style.bottom = 0;

            VisualElement card = Systems_UiKit.Card(Systems_UiKit.Panel).NoPick();
            card.Pad(Systems_UiKit.SPACE_3, Systems_UiKit.SPACE_2);
            card.Elevate(Systems_UiKit.Elevation.Base);
            _panel.Add(card);

            _stakes = Systems_UiKit.Caption(string.Empty, Systems_UiKit.FONT_MICRO, Systems_UiKit.TextLow, true);
            _stakes.style.unityTextAlign = TextAnchor.MiddleCenter;
            _stakes.style.marginBottom = Systems_UiKit.SPACE_1;
            card.Add(_stakes);

            VisualElement left = Systems_UiKit.Column();
            VisualElement centre = Systems_UiKit.Column();
            VisualElement right = Systems_UiKit.Column();
            _sideA = BuildSide(left, true);
            _sideB = BuildSide(right, false);
            BuildPush(centre);
            card.Add(Systems_UiKit.Triplet(left, centre, right).NoPick());

            _hud.Stage.Add(_panel);
        }

        private static Side BuildSide(VisualElement host, bool leftAligned)
        {
            Side side = new Side();
            TextAnchor anchor = leftAligned ? TextAnchor.MiddleLeft : TextAnchor.MiddleRight;

            side.Name = Systems_UiKit.Text("—", Systems_UiKit.FONT_BODY, Systems_UiKit.TextHi, true);
            side.Name.style.unityTextAlign = anchor;
            host.Add(side.Name);

            side.Rank = Systems_UiKit.Caption(string.Empty, Systems_UiKit.FONT_SMALL, Systems_UiKit.Gold, true);
            side.Rank.style.unityTextAlign = anchor;
            host.Add(side.Rank);

            side.Record = Systems_UiKit.Caption(string.Empty, Systems_UiKit.FONT_MICRO, Systems_UiKit.TextMid);
            side.Record.style.unityTextAlign = anchor;
            host.Add(side.Record);

            side.Physique = Systems_UiKit.Caption(string.Empty, Systems_UiKit.FONT_MICRO, Systems_UiKit.TextLow);
            side.Physique.style.unityTextAlign = anchor;
            host.Add(side.Physique);

            // Stamina: a plain track with a fill. Drains from the outer edge on each
            // side so both bars empty toward the centre of the screen, which reads
            // as two fighters wearing down rather than as one shared meter.
            VisualElement track = new VisualElement().Round(3).NoPick();
            track.style.height = 6;
            track.style.marginTop = Systems_UiKit.SPACE_1;
            track.style.backgroundColor = Systems_UiKit.Track;
            track.style.flexDirection = leftAligned ? FlexDirection.Row : FlexDirection.RowReverse;
            side.StaminaFill = new VisualElement().Round(3).NoPick();
            side.StaminaFill.style.height = 6;
            side.StaminaFill.style.backgroundColor = Systems_UiKit.Good;
            track.Add(side.StaminaFill);
            host.Add(track);

            // Damage pips, one per region, in Systems_BodyDamage.Region order.
            VisualElement pipRow = Systems_UiKit.Row();
            pipRow.style.marginTop = Systems_UiKit.SPACE_1;
            pipRow.style.justifyContent = leftAligned ? Justify.FlexStart : Justify.FlexEnd;
            side.Pips = new VisualElement[Systems_BodyDamage.REGION_COUNT];
            side.PipShown = new Color[Systems_BodyDamage.REGION_COUNT];
            for (int regionIndex = 0; regionIndex < side.Pips.Length; regionIndex++)
            {
                VisualElement pip = new VisualElement().Round(2).NoPick();
                pip.style.width = 12;
                pip.style.height = 5;
                pip.style.marginRight = 3;
                pip.style.backgroundColor = Systems_UiKit.Track;
                side.PipShown[regionIndex] = Systems_UiKit.Track;
                side.Pips[regionIndex] = pip;
                pipRow.Add(pip);
            }

            host.Add(pipRow);

            return side;
        }

        /// The push tug-of-war: two bars growing outward from a shared centre line,
        /// so the longer one is the fighter currently driving.
        private void BuildPush(VisualElement host)
        {
            Label caption = Systems_UiKit.Caption("PUSH", Systems_UiKit.FONT_MICRO, Systems_UiKit.TextLow, true);
            caption.style.unityTextAlign = TextAnchor.MiddleCenter;
            host.Add(caption);

            VisualElement bars = Systems_UiKit.Row();
            bars.style.marginTop = Systems_UiKit.SPACE_1;

            VisualElement trackA = new VisualElement().Round(3).NoPick();
            trackA.style.height = 8;
            trackA.style.flexGrow = 1;
            trackA.style.backgroundColor = Systems_UiKit.Track;
            trackA.style.flexDirection = FlexDirection.RowReverse;
            _pushFillA = new VisualElement().Round(3).NoPick();
            _pushFillA.style.height = 8;
            _pushFillA.style.backgroundColor = Systems_UiKit.Gold;
            trackA.Add(_pushFillA);

            VisualElement trackB = new VisualElement().Round(3).NoPick();
            trackB.style.height = 8;
            trackB.style.flexGrow = 1;
            trackB.style.marginLeft = 2;
            trackB.style.backgroundColor = Systems_UiKit.Track;
            _pushFillB = new VisualElement().Round(3).NoPick();
            _pushFillB.style.height = 8;
            _pushFillB.style.backgroundColor = Systems_UiKit.Gold;
            trackB.Add(_pushFillB);

            bars.Add(trackA);
            bars.Add(trackB);
            host.Add(bars);

            VisualElement values = Systems_UiKit.Row();
            values.style.justifyContent = Justify.SpaceBetween;
            values.style.marginTop = 2;
            _pushLabelA = Systems_UiKit.Caption("0 N", Systems_UiKit.FONT_MICRO, Systems_UiKit.TextLow);
            _pushLabelB = Systems_UiKit.Caption("0 N", Systems_UiKit.FONT_MICRO, Systems_UiKit.TextLow);
            values.Add(_pushLabelA);
            values.Add(_pushLabelB);
            host.Add(values);
        }
    }
}
