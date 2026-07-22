using UnityEngine;
using UnityEngine.UIElements;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace PoSumo
{
    /// "Brain view" overlay: live per-joint motor commands (13 signed bars),
    /// episode reward and total effort for each wrestler — what the policy is
    /// doing, readable at a glance. Toggle with B or the BRAIN chip. Spawned at
    /// runtime by Systems_GameMatchManager. UI Toolkit only.
    public class Systems_BrainView : MonoBehaviour
    {
        public bool startVisible = false;

        static readonly string[] JointLabels =
        {
            "HIP·N", "KNE·N", "ANK·N", "HIP·F", "KNE·F", "ANK·F",
            "SPN·1", "SPN·2", "SPN·3", "SHO·N", "ELB·N", "SHO·F", "ELB·F"
        };

        Systems_GameMatchManager _manager;
        VisualElement _panelA, _panelB;
        VisualElement[] _fillsA, _fillsB;
        Label _rewardA, _rewardB, _effortA, _effortB;
        bool _visible;

        static readonly Color PositiveColor = new Color(0.95f, 0.6f, 0.2f);
        static readonly Color NegativeColor = new Color(0.35f, 0.6f, 0.95f);

        void Start()
        {
            _manager = FindAnyObjectByType<Systems_GameMatchManager>();
            if (_manager == null || _manager.panelSettings == null) return;
            _visible = startVisible;
            BuildUi();
        }

        void BuildUi()
        {
            var doc = gameObject.AddComponent<UIDocument>();
            doc.panelSettings = _manager.panelSettings;
            var root = doc.rootVisualElement;
            root.style.flexGrow = 1;

            _panelA = MakePanel(true, _manager.colorA, _manager.nameA,
                out _fillsA, out _rewardA, out _effortA);
            _panelB = MakePanel(false, _manager.colorB, _manager.nameB,
                out _fillsB, out _rewardB, out _effortB);
            root.Add(_panelA);
            root.Add(_panelB);

            var toggle = new Button(() => { _visible = !_visible; ApplyVisibility(); }) { text = "BRAIN" };
            toggle.style.position = Position.Absolute;
            toggle.style.bottom = 10;
            toggle.style.right = 94;
            toggle.style.width = 76;
            toggle.style.height = 34;
            toggle.style.fontSize = 14;
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

        VisualElement MakePanel(bool left, Color accent, string fighterName,
            out VisualElement[] fills, out Label reward, out Label effort)
        {
            var p = new VisualElement();
            p.style.position = Position.Absolute;
            p.style.top = 262; // stacks below the STATS panels when both are open
            if (left) p.style.left = 8; else p.style.right = 8;
            p.style.width = 150;
            p.style.backgroundColor = new Color(0.05f, 0.05f, 0.07f, 0.72f);
            p.style.borderTopWidth = 3;
            p.style.borderTopColor = accent;
            p.style.paddingLeft = 8; p.style.paddingRight = 8;
            p.style.paddingTop = 5; p.style.paddingBottom = 7;

            var title = new Label($"BRAIN · {fighterName}");
            title.style.fontSize = 13;
            title.style.color = accent;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.marginBottom = 3;
            p.Add(title);

            fills = new VisualElement[JointLabels.Length];
            for (int j = 0; j < JointLabels.Length; j++)
            {
                var row = new VisualElement();
                row.style.flexDirection = FlexDirection.Row;
                row.style.alignItems = Align.Center;
                row.style.height = 11;

                var lbl = new Label(JointLabels[j]);
                lbl.style.fontSize = 9;
                lbl.style.width = 38;
                lbl.style.color = new Color(0.72f, 0.7f, 0.65f);
                row.Add(lbl);

                var bar = new VisualElement();
                bar.style.flexGrow = 1;
                bar.style.height = 7;
                bar.style.backgroundColor = new Color(0.2f, 0.2f, 0.24f);

                var center = new VisualElement();
                center.style.position = Position.Absolute;
                center.style.left = Length.Percent(50);
                center.style.width = 1;
                center.style.height = Length.Percent(100);
                center.style.backgroundColor = new Color(1f, 1f, 1f, 0.25f);

                var fill = new VisualElement();
                fill.style.position = Position.Absolute;
                fill.style.height = Length.Percent(100);
                fill.style.left = Length.Percent(50);
                fill.style.width = 0;
                fill.style.backgroundColor = PositiveColor;

                bar.Add(fill);
                bar.Add(center);
                row.Add(bar);
                p.Add(row);
                fills[j] = fill;
            }

            reward = new Label("REWARD 0.000");
            reward.style.fontSize = 11;
            reward.style.marginTop = 4;
            reward.style.color = new Color(0.9f, 0.88f, 0.82f);
            p.Add(reward);

            effort = new Label("EFFORT 0%");
            effort.style.fontSize = 11;
            effort.style.color = new Color(0.72f, 0.7f, 0.65f);
            p.Add(effort);

            return p;
        }

        void ApplyVisibility()
        {
            var d = _visible ? DisplayStyle.Flex : DisplayStyle.None;
            if (_panelA != null) _panelA.style.display = d;
            if (_panelB != null) _panelB.style.display = d;
        }

        bool BrainKeyPressed()
        {
#if ENABLE_INPUT_SYSTEM
            return Keyboard.current != null && Keyboard.current.bKey.wasPressedThisFrame;
#else
            return Input.GetKeyDown(KeyCode.B);
#endif
        }

        void Update()
        {
            if (BrainKeyPressed()) { _visible = !_visible; ApplyVisibility(); }
            if (!_visible || _manager == null) return;

            UpdatePanel(_manager.wrestlerA, _fillsA, _rewardA, _effortA);
            UpdatePanel(_manager.wrestlerB, _fillsB, _rewardB, _effortB);
        }

        void UpdatePanel(Agent_Biped w, VisualElement[] fills, Label reward, Label effort)
        {
            if (w == null || fills == null) return;

            float totalEffort = 0f;
            for (int j = 0; j < fills.Length; j++)
            {
                float a = w.LastActions[j];
                totalEffort += Mathf.Abs(a);
                float half = Mathf.Abs(a) * 50f;
                fills[j].style.left = Length.Percent(a >= 0f ? 50f : 50f - half);
                fills[j].style.width = Length.Percent(half);
                fills[j].style.backgroundColor = a >= 0f ? PositiveColor : NegativeColor;
            }

            reward.text = $"REWARD {w.EpisodeReward:+0.000;-0.000}";
            effort.text = $"EFFORT {totalEffort / fills.Length * 100f:F0}%";
        }
    }
}
