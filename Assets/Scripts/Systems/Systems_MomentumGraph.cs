using UnityEngine;
using UnityEngine.UIElements;

namespace PoSumo
{
    /// A single live momentum graph: who is winning, plotted over the round.
    ///
    /// This is the default HUD view. Systems_FightHud shows seven metrics for
    /// both fighters — dominance, territory, knockdowns, shoves, work, balance
    /// and push, fourteen numbers in total — on a portrait phone, during a
    /// thirty-second round, while two ragdolls wrestle. Nobody can read that while
    /// watching. One line that visibly swings toward whoever is winning says the
    /// same thing at a glance, and the full breakdown is still one tap away on the
    /// STATS chip.
    ///
    /// Reads Systems_FightHud.DominanceA rather than recomputing anything, so the
    /// graph, the fighters' faces and their voice lines all move off one signal and
    /// can never disagree.
    ///
    /// Spawned at runtime by Systems_GameMatchManager.
    public sealed class Systems_MomentumGraph : MonoBehaviour
    {
        public PanelSettings panelSettings;

        [Tooltip("Seconds of history shown. Matches the round timeout so a full round fills the width exactly.")]
        public float windowSeconds = 30f;
        [Tooltip("Samples per second. 10 is smooth at this width and costs nothing.")]
        public float sampleRate = 10f;
        [Tooltip("Height of the graph in the HUD dock. Raised from 64 once the graph moved to the bottom band: at the old height it read as a thin strip lost against the bottom edge, and the dock has the room.")]
        public float graphHeight = 104f;

        private Systems_FightHud _hud;
        private Systems_GameMatchManager _manager;

        // Ring buffer of dominance-A samples, 0..100. 50 = dead even.
        private float[] _samples;
        private int _count;
        private int _head;
        private float _nextSampleTime;

        private VisualElement _graph;
        private Color _colorA = new Color(0.85f, 0.25f, 0.2f);
        private Color _colorB = new Color(0.25f, 0.42f, 0.72f);

        private void Start()
        {
            _hud = FindAnyObjectByType<Systems_FightHud>();
            _manager = FindAnyObjectByType<Systems_GameMatchManager>();
            if (_manager == null)
            {
                enabled = false;
                return;
            }

            _colorA = _manager.colorA;
            _colorB = _manager.colorB;
            if (_manager.roundTimeoutSeconds > 1f)
            {
                windowSeconds = _manager.roundTimeoutSeconds;
            }

            _samples = new float[Mathf.Max(16, Mathf.CeilToInt(windowSeconds * sampleRate))];
            BuildUi();

            _manager.RoundStarted += ClearHistory;
            _manager.MatchReset += ClearHistory;

            // The stats table is no longer hidden on start — it is always on
            // during a match, so this component leaves it alone.
        }

        private void OnDisable()
        {
            if (_manager != null)
            {
                _manager.RoundStarted -= ClearHistory;
                _manager.MatchReset -= ClearHistory;
            }
        }

        private void ClearHistory()
        {
            _count = 0;
            _head = 0;
            _nextSampleTime = 0f;
            _graph?.MarkDirtyRepaint();
        }

        private void BuildUi()
        {
            PanelSettings settings = panelSettings != null ? panelSettings : _manager.panelSettings;
            Systems_HudRoot hud = Systems_HudRoot.Ensure(transform, settings);

            // Lives in the HUD's bottom dock, pinned below the stats drawer.
            //
            // It used to float at `top: 96` — a fixed offset under the old
            // top-corner chips, which put it over the upper arena and left the
            // whole bottom third of a portrait screen empty once the stat panels
            // were hidden by default. The dock is the widest, calmest strip of the
            // screen and nothing plays there, so the graph reads better AND the
            // fighters gain back the space it was taking.
            _graph = new VisualElement().NoPick();
            _graph.style.height = graphHeight;
            _graph.style.backgroundColor = Systems_UiKit.Panel;
            _graph.Round(Systems_UiKit.RADIUS_SM);
            _graph.generateVisualContent += Draw;
            hud.Dock.Add(_graph);
        }

        private void Update()
        {
            if (_manager == null || _hud == null || _graph == null)
            {
                return;
            }
            // Only while the fight is live; the pause between rounds should hold the
            // last shape rather than flat-line it.
            // ScoringLive: sampling through the countdown drew three seconds of
            // dead flat line at the start of every round before anything had
            // happened.
            if (!_manager.ScoringLive || Time.time < _nextSampleTime)
            {
                return;
            }
            _nextSampleTime = Time.time + 1f / Mathf.Max(1f, sampleRate);

            _samples[_head] = _hud.DominanceA;
            _head = (_head + 1) % _samples.Length;
            if (_count < _samples.Length)
            {
                _count++;
            }
            _graph.MarkDirtyRepaint();
        }

        /// Filled area from the centre line: bulging up in fighter A's colour when
        /// A leads, down in B's when B does. The shape IS the story of the round.
        private void Draw(MeshGenerationContext ctx)
        {
            Rect r = ctx.visualElement.contentRect;
            if (r.width <= 1f || r.height <= 1f)
            {
                return;
            }
            var p = ctx.painter2D;
            float mid = r.height * 0.5f;

            // Centre line, always drawn so an empty graph still reads as "even".
            p.strokeColor = new Color(1f, 1f, 1f, 0.22f);
            p.lineWidth = 1f;
            p.BeginPath();
            p.MoveTo(new Vector2(0f, mid));
            p.LineTo(new Vector2(r.width, mid));
            p.Stroke();

            if (_count < 2)
            {
                return;
            }

            // Oldest sample sits at the left edge; a partial history is drawn
            // compressed to the left rather than stretched, so the line grows
            // rightward as the round runs.
            float step = r.width / (_samples.Length - 1);
            int start = (_head - _count + _samples.Length) % _samples.Length;

            // Two passes: fill A's side above the midline, B's below.
            FillSide(p, r, mid, step, start, aboveForA: true, _colorA);
            FillSide(p, r, mid, step, start, aboveForA: false, _colorB);

            // The line itself, coloured by who currently leads.
            float latest = _samples[(_head - 1 + _samples.Length) % _samples.Length];
            p.strokeColor = latest >= 50f ? _colorA : _colorB;
            p.lineWidth = 2f;
            p.BeginPath();
            for (int i = 0; i < _count; i++)
            {
                Vector2 point = PointAt(i, start, step, mid, r);
                if (i == 0) p.MoveTo(point); else p.LineTo(point);
            }
            p.Stroke();
        }

        private Vector2 PointAt(int i, int start, float step, float mid, Rect r)
        {
            float value = _samples[(start + i) % _samples.Length];
            // 50 maps to the centre line; 0/100 to the edges.
            float y = mid - (value - 50f) / 50f * mid;
            return new Vector2(i * step, Mathf.Clamp(y, 0f, r.height));
        }

        private void FillSide(Painter2D p, Rect r, float mid, float step, int start,
                              bool aboveForA, Color colour)
        {
            p.fillColor = new Color(colour.r, colour.g, colour.b, 0.4f);
            p.BeginPath();
            p.MoveTo(new Vector2(0f, mid));
            for (int i = 0; i < _count; i++)
            {
                Vector2 point = PointAt(i, start, step, mid, r);
                // Clamp to this fighter's half so the two fills never overlap.
                point.y = aboveForA ? Mathf.Min(point.y, mid) : Mathf.Max(point.y, mid);
                p.LineTo(point);
            }
            p.LineTo(new Vector2((_count - 1) * step, mid));
            p.ClosePath();
            p.Fill();
        }
    }
}
