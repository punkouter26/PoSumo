using Unity.MLAgents;
using UnityEngine;
using UnityEngine.UIElements;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace PoSumo
{
    /// F1-broadcast-style telemetry HUD: one panel per wrestler on the screen
    /// edges, toggled with Tab. Shows fighter card, live telemetry (speed,
    /// lean, edge distance, balance), an estimated push-power meter, and match
    /// stats from the Systems_GameMatchManager. UI Toolkit only.
    public class Systems_FightHud : MonoBehaviour
    {
        public Systems_GameMatchManager manager;
        public PanelSettings panelSettings;
        public bool startVisible = true;
        [Tooltip("Static pedigree line per wrestler, e.g. '8.0M steps · ELO 1351'.")]
        public string pedigreeA = "8.0M steps";
        public string pedigreeB = "7.3M steps";

        VisualElement _panelA, _panelB;
        Label _spdA, _spdB, _leanA, _leanB, _edgeA, _edgeB, _timeA, _timeB, _statsA, _statsB;
        VisualElement _balFillA, _balFillB, _pushFillA, _pushFillB;
        Label _pushValA, _pushValB;
        bool _visible;
        Vector2 _prevVelA, _prevVelB;
        float _pushDispA, _pushDispB;
        const float PUSH_MAX = 2200f;

        void Start()
        {
            if (manager == null) manager = FindAnyObjectByType<Systems_GameMatchManager>();
            _visible = startVisible;
            BuildUi();
        }

        VisualElement MakePanel(bool left, Color accent, string name, string phys, string pedigree,
            out Label spd, out Label lean, out Label edge, out VisualElement balFill,
            out VisualElement pushFill, out Label pushVal, out Label time, out Label stats)
        {
            var p = new VisualElement();
            p.style.position = Position.Absolute;
            p.style.bottom = Length.Percent(2.5f); // below the ring, clear of the wrestlers
            if (left) p.style.left = 8; else p.style.right = 8;
            p.style.width = 172;
            p.style.backgroundColor = new Color(0.05f, 0.05f, 0.07f, 0.72f);
            p.style.borderTopWidth = 3;
            p.style.borderTopColor = accent;
            p.style.paddingLeft = 10; p.style.paddingRight = 10;
            p.style.paddingTop = 6; p.style.paddingBottom = 8;

            Label L(string txt, int size, Color c, bool bold = false)
            {
                var l = new Label(txt);
                l.style.fontSize = size;
                l.style.color = c;
                if (bold) l.style.unityFontStyleAndWeight = FontStyle.Bold;
                l.style.marginTop = 1; l.style.marginBottom = 1;
                p.Add(l);
                return l;
            }

            var dim = new Color(0.75f, 0.73f, 0.68f);
            var bright = new Color(0.95f, 0.94f, 0.9f);

            L(name, 24, accent, true);
            L(phys, 13, dim);
            L(pedigree, 12, dim);
            L("— TELEMETRY —", 11, accent);
            spd = L("SPD  0.0 m/s", 14, bright);
            lean = L("LEAN 0°", 14, bright);
            edge = L("EDGE 0.0 m", 14, bright);

            L("BALANCE", 11, dim);
            var balBar = new VisualElement();
            balBar.style.height = 9;
            balBar.style.backgroundColor = new Color(0.22f, 0.22f, 0.25f);
            balFill = new VisualElement();
            balFill.style.height = Length.Percent(100);
            balFill.style.width = Length.Percent(100);
            balFill.style.backgroundColor = new Color(0.35f, 0.75f, 0.4f);
            balBar.Add(balFill);
            p.Add(balBar);

            L("PUSH", 11, dim);
            var pushBar = new VisualElement();
            pushBar.style.height = 9;
            pushBar.style.backgroundColor = new Color(0.22f, 0.22f, 0.25f);
            pushFill = new VisualElement();
            pushFill.style.height = Length.Percent(100);
            pushFill.style.width = 0;
            pushFill.style.backgroundColor = new Color(0.95f, 0.55f, 0.2f);
            pushBar.Add(pushFill);
            p.Add(pushBar);
            pushVal = L("0 N", 12, dim);

            L("— MATCH —", 11, accent);
            time = L("TIME 0.0s", 13, bright);
            stats = L("", 12, dim);

            return p;
        }

        void BuildUi()
        {
            var doc = gameObject.AddComponent<UIDocument>();
            if (panelSettings != null) doc.panelSettings = panelSettings;
            var root = doc.rootVisualElement;
            root.style.flexGrow = 1;

            var a = manager.wrestlerA; var b = manager.wrestlerB;
            string physA = a != null ? $"{a.GetComponent<Agent_BipedBody>().TotalMass:F0} kg · 1.76 m" : "";
            string physB = b != null ? $"{b.GetComponent<Agent_BipedBody>().TotalMass:F0} kg · 1.76 m" : "";

            _panelA = MakePanel(true, manager.colorA, manager.nameA, physA, pedigreeA,
                out _spdA, out _leanA, out _edgeA, out _balFillA, out _pushFillA, out _pushValA, out _timeA, out _statsA);
            _panelB = MakePanel(false, manager.colorB, manager.nameB, physB, pedigreeB,
                out _spdB, out _leanB, out _edgeB, out _balFillB, out _pushFillB, out _pushValB, out _timeB, out _statsB);
            root.Add(_panelA);
            root.Add(_panelB);

            // Touch-friendly toggle chip (mobile) — Tab still works too.
            var toggle = new Button(() => { _visible = !_visible; ApplyVisibility(); }) { text = "STATS" };
            toggle.style.position = Position.Absolute;
            toggle.style.top = 12;
            toggle.style.right = 10;
            toggle.style.width = 84;
            toggle.style.height = 40;
            toggle.style.fontSize = 16;
            toggle.style.unityFontStyleAndWeight = FontStyle.Bold;
            toggle.style.color = new Color(0.92f, 0.9f, 0.84f);
            toggle.style.backgroundColor = new Color(0.12f, 0.11f, 0.13f, 0.75f);
            toggle.style.borderTopLeftRadius = 8;
            toggle.style.borderTopRightRadius = 8;
            toggle.style.borderBottomLeftRadius = 8;
            toggle.style.borderBottomRightRadius = 8;
            root.Add(toggle);

            ApplyVisibility();
        }

        void ApplyVisibility()
        {
            var d = _visible ? DisplayStyle.Flex : DisplayStyle.None;
            if (_panelA != null) _panelA.style.display = d;
            if (_panelB != null) _panelB.style.display = d;
        }

        bool TabPressed()
        {
#if ENABLE_INPUT_SYSTEM
            return Keyboard.current != null && Keyboard.current.tabKey.wasPressedThisFrame;
#else
            return Input.GetKeyDown(KeyCode.Tab);
#endif
        }

        void FixedUpdate()
        {
            // Push power estimate: opponent momentum change directed away from me.
            var a = manager != null ? manager.wrestlerA : null;
            var b = manager != null ? manager.wrestlerB : null;
            if (a == null || b == null) return;
            float dt = Time.fixedDeltaTime;

            Vector2 velA = a.Torso.linearVelocity, velB = b.Torso.linearVelocity;
            float mA = a.GetComponent<Agent_BipedBody>().TotalMass;
            float mB = b.GetComponent<Agent_BipedBody>().TotalMass;
            float dirAB = Mathf.Sign(b.TorsoX - a.TorsoX);

            float pushA = Mathf.Max(0f, (velB.x - _prevVelB.x) * dirAB) * mB / dt;   // A shoving B away
            float pushB = Mathf.Max(0f, (_prevVelA.x - velA.x) * dirAB) * mA / dt;   // B shoving A away
            bool touching = Mathf.Abs(a.TorsoX - b.TorsoX) < 1.2f;
            if (!touching) { pushA = 0f; pushB = 0f; }
            _pushDispA = Mathf.Lerp(_pushDispA, pushA, 0.25f);
            _pushDispB = Mathf.Lerp(_pushDispB, pushB, 0.25f);
            _prevVelA = velA; _prevVelB = velB;
        }

        void Update()
        {
            if (TabPressed()) { _visible = !_visible; ApplyVisibility(); }
            if (!_visible || manager == null || manager.wrestlerA == null) return;

            UpdatePanel(manager.wrestlerA, manager.ScoreA, manager.MatchWinsA, _pushDispA,
                        _spdA, _leanA, _edgeA, _balFillA, _pushFillA, _pushValA, _timeA, _statsA);
            UpdatePanel(manager.wrestlerB, manager.ScoreB, manager.MatchWinsB, _pushDispB,
                        _spdB, _leanB, _edgeB, _balFillB, _pushFillB, _pushValB, _timeB, _statsB);
        }

        void UpdatePanel(Agent_Biped w, int score, int matchWins, float push,
            Label spd, Label lean, Label edge, VisualElement balFill,
            VisualElement pushFill, Label pushVal, Label time, Label stats)
        {
            var body = w.GetComponent<Agent_BipedBody>();
            float speed = w.Torso.linearVelocity.magnitude;
            float leanDeg = Vector2.SignedAngle(Vector2.up, body.Chest.transform.up);
            float edgeDist = Mathf.Max(0f, w.ringHalfWidth - Mathf.Abs(w.TorsoX - w.arenaCenterX));
            float balance = Mathf.Clamp01(Vector2.Dot(body.Chest.transform.up, Vector2.up));

            spd.text = $"SPD  {speed:F1} m/s";
            lean.text = $"LEAN {Mathf.Abs(leanDeg):F0}°";
            edge.text = $"EDGE {edgeDist:F1} m";
            edge.style.color = edgeDist < 0.5f ? new Color(1f, 0.4f, 0.3f) : new Color(0.95f, 0.94f, 0.9f);

            balFill.style.width = Length.Percent(balance * 100f);
            balFill.style.backgroundColor = balance > 0.7f
                ? new Color(0.35f, 0.75f, 0.4f)
                : balance > 0.4f ? new Color(0.9f, 0.75f, 0.25f) : new Color(0.9f, 0.35f, 0.25f);

            float pushN = Mathf.Clamp(push, 0f, PUSH_MAX);
            pushFill.style.width = Length.Percent(pushN / PUSH_MAX * 100f);
            pushVal.text = $"{pushN:F0} N";

            time.text = manager.RoundActive ? $"TIME {manager.RoundElapsed:F1}s" : "TIME —";
            stats.text = $"ROUNDS {score}  ·  MATCHES {matchWins}\nLONGEST {manager.LongestRound:F0}s";
        }
    }
}
