// EDITOR ONLY, and the whole file is behind the define rather than only the
// spawn. A headless training env is a player build: with this guard the class
// does not exist in it at all, so there is nothing to spawn, nothing to tick
// and no camera to render — "zero effect on training" is a property of the
// compiler here, not of a runtime check somebody could later get wrong.
#if UNITY_EDITOR
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering.Universal;

namespace PoSumo
{
    /// Lets a SCN_TRAIN_* scene be WATCHED in the Editor.
    ///
    /// The training scenes hold no camera and no light — a headless env runs
    /// `--no-graphics` and needs neither — so pressing Play on one showed "No
    /// cameras rendering" and nothing else. This spawns a camera rig and one
    /// flat global light when, and only when, all three hold: this is the
    /// Editor, the scene has a training referee, and nothing else is already
    /// rendering. It is NOT in any scene: like every other companion in this
    /// project it is built from code, which is also what keeps it out of the
    /// env builds' scene data.
    ///
    /// READ-ONLY with respect to the simulation. It reads transforms in
    /// LateUpdate and writes only its own cameras. No physics, observation,
    /// reward or episode call is made, so a brain trains identically with it
    /// present (which only happens when the trainer is pointed at the Editor
    /// instead of an env build).
    ///
    /// VIEWS. A training scene is three things that are nowhere near each other
    /// — two self-play arenas 30 m apart and a walk lane 60 m below them — and
    /// the Game view is portrait, so no single frame holds them. Hence:
    ///
    ///     OVERVIEW   every arena and one walker, stacked in horizontal bands
    ///     ARENA n    one arena, full screen
    ///     WALKER n   one walk agent, followed, full screen
    ///
    /// TAB / RIGHT = next view, LEFT = previous, 0 = overview, 1-9 = that view.
    /// From the bridge: `Systems_TrainingSpectator.Instance.Next()` or
    /// `.Show(index)`; `ViewName` says what is on screen.
    public sealed class Systems_TrainingSpectator : MonoBehaviour
    {
        /// Metres of clay either side of the full mat kept in an arena shot, so
        /// a ring-out is seen leaving rather than vanishing at the frame edge.
        private const float ARENA_MARGIN = 1.6f;
        private const float ARENA_HALF_FALLBACK = 3.5f;
        /// Camera centre above the mat in an arena shot: roughly waist height,
        /// which keeps the feet, the head and the drop off the rim all in band.
        private const float ARENA_CENTRE_HEIGHT = 0.9f;
        /// Half-width a walker shot frames. Wide enough to see a stride and the
        /// target it is walking to (5 m ahead at spawn).
        private const float WALKER_HALF_WIDTH = 3.6f;
        private const float WALKER_CENTRE_HEIGHT = 0.8f;
        private const float CAMERA_Z = -10f;
        private const float FOLLOW_SMOOTHING = 6f;
        /// Dark neutral, close to Systems_ArenaLighting.arenaBackground, so the
        /// bodies read against it.
        private static readonly Color Background = new Color(0.15f, 0.12f, 0.13f);

        public static Systems_TrainingSpectator Instance { get; private set; }

        private Systems_SumoMatchManager[] _arenas;
        private Agent_Biped[] _walkers;
        /// One camera per overview band: every arena, plus one for a walker.
        private Camera[] _cameras;
        /// 0 = overview, then 1..arenas, then the walkers.
        private int _view;
        private int ViewCount => 1 + _arenas.Length + _walkers.Length;

        /// Domain reload is off: without this a destroyed instance from the
        /// previous Play session would still be reachable from the bridge.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetInstance() => Instance = null;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Spawn()
        {
            if (!Application.isEditor) return;
            // A training scene is one with a training referee. The game scenes
            // carry Systems_GameMatchManager instead and their own camera.
            if (FindAnyObjectByType<Systems_SumoMatchManager>() == null) return;
            // Somebody already renders (a camera added to the scene by hand):
            // leave it alone rather than fight it for the frame.
            if (Camera.allCamerasCount > 0) return;

            new GameObject("TrainingSpectator").AddComponent<Systems_TrainingSpectator>();
        }

        private void Awake()
        {
            Instance = this;

            _arenas = FindObjectsByType<Systems_SumoMatchManager>();
            // Left to right, so "ARENA 1" is the same arena every session.
            System.Array.Sort(_arenas, (a, b) => a.transform.position.x.CompareTo(b.transform.position.x));

            Agent_Biped[] agents = FindObjectsByType<Agent_Biped>();
            int walkerCount = 0;
            for (int agentIndex = 0; agentIndex < agents.Length; agentIndex++)
            {
                if (agents[agentIndex].mode == Agent_Biped.Mode.Walk) walkerCount++;
            }
            _walkers = new Agent_Biped[walkerCount];
            int next = 0;
            for (int agentIndex = 0; agentIndex < agents.Length; agentIndex++)
            {
                if (agents[agentIndex].mode == Agent_Biped.Mode.Walk) _walkers[next++] = agents[agentIndex];
            }
            System.Array.Sort(_walkers, (a, b) => a.transform.position.x.CompareTo(b.transform.position.x));

            int bandCount = _arenas.Length + (_walkers.Length > 0 ? 1 : 0);
            _cameras = new Camera[Mathf.Max(1, bandCount)];
            for (int cameraIndex = 0; cameraIndex < _cameras.Length; cameraIndex++)
            {
                var host = new GameObject("SpectatorCamera_" + cameraIndex);
                host.transform.SetParent(transform, false);
                Camera cam = host.AddComponent<Camera>();
                cam.orthographic = true;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = Background;
                cam.depth = cameraIndex;
                _cameras[cameraIndex] = cam;
            }

            // The bodies are drawn with a LIT sprite shader, and a lit sprite
            // with no Light2D in the scene renders solid black. One Global light
            // has no position and no falloff: it is an exposure, nothing more.
            if (FindAnyObjectByType<Light2D>() == null)
            {
                var lightHost = new GameObject("SpectatorLight");
                lightHost.transform.SetParent(transform, false);
                Light2D light = lightHost.AddComponent<Light2D>();
                light.lightType = Light2D.LightType.Global;
                light.intensity = 1f;
            }

            Show(0);
            Systems_Log.Info($"[SPECTATOR] Editor-only training camera: {_arenas.Length} arenas, " +
                             $"{_walkers.Length} walkers. TAB/RIGHT next view, LEFT previous, " +
                             "0 overview, 1-9 a view.");
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        /// What is on screen, for the console and for anything driving this
        /// from the bridge.
        public string ViewName
        {
            get
            {
                if (_view == 0) return "OVERVIEW";
                if (_view <= _arenas.Length) return "ARENA " + _view;
                return "WALKER " + (_view - _arenas.Length);
            }
        }

        public void Next() => Show(_view + 1);

        public void Previous() => Show(_view - 1);

        /// Selects a view by index (0 = overview); wraps at both ends.
        public void Show(int view)
        {
            int count = ViewCount;
            _view = ((view % count) + count) % count;

            bool overview = _view == 0;
            for (int cameraIndex = 0; cameraIndex < _cameras.Length; cameraIndex++)
            {
                Camera cam = _cameras[cameraIndex];
                cam.enabled = overview || cameraIndex == 0;
                if (overview)
                {
                    // Bands top to bottom in scene order: arenas first, the
                    // walker last — the same way the scene itself is laid out.
                    float height = 1f / _cameras.Length;
                    cam.rect = new Rect(0f, 1f - height * (cameraIndex + 1), 1f, height);
                }
                else
                {
                    cam.rect = new Rect(0f, 0f, 1f, 1f);
                }
            }
            Frame(snap: true);
            Systems_Log.Info($"[SPECTATOR] view {_view}/{count - 1}: {ViewName}");
        }

        private void Update()
        {
            Keyboard keyboard = Keyboard.current;
            if (keyboard == null) return;

            if (keyboard.tabKey.wasPressedThisFrame || keyboard.rightArrowKey.wasPressedThisFrame) Next();
            else if (keyboard.leftArrowKey.wasPressedThisFrame) Previous();
            else if (keyboard.digit0Key.wasPressedThisFrame) Show(0);
            else if (keyboard.digit1Key.wasPressedThisFrame) ShowIfPresent(1);
            else if (keyboard.digit2Key.wasPressedThisFrame) ShowIfPresent(2);
            else if (keyboard.digit3Key.wasPressedThisFrame) ShowIfPresent(3);
            else if (keyboard.digit4Key.wasPressedThisFrame) ShowIfPresent(4);
            else if (keyboard.digit5Key.wasPressedThisFrame) ShowIfPresent(5);
            else if (keyboard.digit6Key.wasPressedThisFrame) ShowIfPresent(6);
            else if (keyboard.digit7Key.wasPressedThisFrame) ShowIfPresent(7);
            else if (keyboard.digit8Key.wasPressedThisFrame) ShowIfPresent(8);
            else if (keyboard.digit9Key.wasPressedThisFrame) ShowIfPresent(9);
        }

        /// A digit past the last view does nothing rather than wrapping: "9" on
        /// a scene with eight views silently showing the overview would read as
        /// the key being broken.
        private void ShowIfPresent(int view)
        {
            if (view < ViewCount) Show(view);
        }

        /// After every body has moved for the frame, like Systems_CameraFollow.
        private void LateUpdate() => Frame(snap: false);

        private void Frame(bool snap)
        {
            if (_view == 0)
            {
                for (int arenaIndex = 0; arenaIndex < _arenas.Length; arenaIndex++)
                {
                    FrameArena(_cameras[arenaIndex], _arenas[arenaIndex]);
                }
                if (_walkers.Length > 0)
                {
                    // The overview's walker band follows the first walker; the
                    // other five are one key away.
                    FrameWalker(_cameras[_cameras.Length - 1], _walkers[0], snap);
                }
                return;
            }

            if (_view <= _arenas.Length)
            {
                FrameArena(_cameras[0], _arenas[_view - 1]);
                return;
            }
            FrameWalker(_cameras[0], _walkers[_view - _arenas.Length - 1], snap);
        }

        /// A FIXED shot of the whole mat. Not a follow: the thing to read in a
        /// training arena is where the fighters are ON the mat, and a camera
        /// that tracks them hides exactly that.
        private static void FrameArena(Camera cam, Systems_SumoMatchManager arena)
        {
            if (arena == null) return;
            // The FULL configured width, not the live one: the mat randomises
            // per round and shrinks, and a frame that breathed with it would
            // make every round look the same size.
            float half = arena.ringHalfWidth > 0f ? arena.ringHalfWidth : ARENA_HALF_FALLBACK;
            Vector3 centre = arena.transform.position;
            cam.orthographicSize = (half + ARENA_MARGIN) / Mathf.Max(0.01f, cam.aspect);
            cam.transform.position = new Vector3(centre.x, centre.y + ARENA_CENTRE_HEIGHT, CAMERA_Z);
        }

        private static void FrameWalker(Camera cam, Agent_Biped walker, bool snap)
        {
            if (walker == null || walker.Torso == null) return;
            cam.orthographicSize = WALKER_HALF_WIDTH / Mathf.Max(0.01f, cam.aspect);
            // Height from the lane, not from the torso: a walker that falls
            // should be seen falling, not have the frame fall with it.
            var target = new Vector3(walker.Torso.position.x,
                                     walker.arenaGroundY + WALKER_CENTRE_HEIGHT, CAMERA_Z);
            // An episode reset teleports the walker back to its start line, so
            // the follow is smoothed only over ordinary travel.
            Vector3 current = cam.transform.position;
            bool jumped = Mathf.Abs(current.x - target.x) > WALKER_HALF_WIDTH
                          || Mathf.Abs(current.y - target.y) > 1f;
            float blend = snap || jumped ? 1f : 1f - Mathf.Exp(-FOLLOW_SMOOTHING * Time.unscaledDeltaTime);
            cam.transform.position = Vector3.Lerp(current, target, blend);
        }
    }
}
#endif
