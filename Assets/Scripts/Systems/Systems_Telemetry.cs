using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Unity.MLAgents;
using UnityEngine;

namespace PoSumo
{
    /// Real-time telemetry: a loopback HTTP endpoint serving a JSON snapshot, plus
    /// the same numbers pushed into ML-Agents' `StatsRecorder` so they land on
    /// TensorBoard beside reward and ELO.
    ///
    /// Two consumers, deliberately: TensorBoard answers "how did this run go", the
    /// HTTP endpoint answers "what is this env doing RIGHT NOW", which is the
    /// question you actually have when a headless run has gone quiet and you cannot
    /// see the Game view.
    ///
    /// Raw `TcpListener`, not `HttpListener`. HttpListener needs a `netsh http
    /// add urlacl` reservation to bind a prefix as a non-admin user on Windows, and
    /// a telemetry endpoint that fails on a fresh machine with an access-denied
    /// exception is worse than none. A minimal HTTP/1.1 response over a loopback
    /// socket needs no such registration.
    ///
    /// THREADING: the socket thread never touches a Unity API. The main thread
    /// builds the JSON on a timer and swaps it into `_snapshot` under a lock; the
    /// socket thread only ever reads that string. Unity's API is main-thread-only
    /// and a background `FindObjectsByType` would be an immediate crash.
    [DefaultExecutionOrder(500)]
    public sealed class Systems_Telemetry : MonoBehaviour
    {
        /// First port tried. Parallel training envs each want their own, so the
        /// bind walks upward until it finds a free one — enough for the 4-8
        /// concurrent envs the training workflow calls for.
        private const int BASE_PORT = 8787;
        private const int PORT_ATTEMPTS = 12;

        /// Sampling period. 2 Hz is far below the physics rate on purpose: this
        /// enumerates agents and allocates a JSON string, so it must not sit in the
        /// per-frame budget. Nothing here changes meaningfully inside half a second.
        private const float SAMPLE_INTERVAL = 0.5f;

        private static Systems_Telemetry _instance;

        private TcpListener _listener;
        private Thread _serverThread;
        private volatile bool _running;

        private readonly object _snapshotLock = new object();
        private string _snapshot = "{}";
        private readonly StringBuilder _builder = new StringBuilder(1024);

        private Agent_Biped[] _agents = System.Array.Empty<Agent_Biped>();
        /// One TensorBoard key per agent, built once when the agent is first
        /// sampled. Interpolating `$"Body/{name}/Stamina"` per sample was a fresh
        /// string twice a second per biped for the whole life of the process.
        private readonly Dictionary<Agent_Biped, string> _staminaKeys =
            new Dictionary<Agent_Biped, string>();
        private float _nextSample;
        private float _nextRescan;
        private int _boundPort = -1;

        /// Rescan for agents this often. They are created in Awake and destroyed
        /// only on scene change, so this exists to pick up a scene load, not to
        /// track churn.
        private const float RESCAN_INTERVAL = 5f;

        /// Spawns the endpoint once per player, before any scene has loaded.
        ///
        /// Development and Editor only. A shipped Android build has no use for a
        /// listening socket, and opening one on a user's phone is a needless
        /// attack surface — the endpoint answers anything that can reach the port
        /// and performs no authentication, which is only acceptable because it is
        /// bound to the loopback interface of a developer's own machine.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Spawn()
        {
            if (!Debug.isDebugBuild && !Application.isEditor)
            {
                return;
            }
            if (_instance != null)
            {
                return;
            }
            // Plain DontDestroyOnLoad, no HideAndDontSave: a hidden undestroyable
            // object survives Play-mode exit in the Editor and leaks a bound socket
            // per session, which then eats the port range this walks through.
            var go = new GameObject("Telemetry");
            _instance = go.AddComponent<Systems_Telemetry>();
            DontDestroyOnLoad(go);
        }

        private void Start()
        {
            for (int attempt = 0; attempt < PORT_ATTEMPTS; attempt++)
            {
                int port = BASE_PORT + attempt;
                try
                {
                    _listener = new TcpListener(IPAddress.Loopback, port);
                    _listener.Start();
                    _boundPort = port;
                    break;
                }
                catch (SocketException)
                {
                    // Port in use — almost always a sibling training env. Try the next.
                    _listener = null;
                }
            }

            if (_listener == null)
            {
                Debug.LogWarning($"[TELEMETRY] no free port in {BASE_PORT}..{BASE_PORT + PORT_ATTEMPTS - 1}; endpoint disabled.");
                enabled = false;
                return;
            }

            _running = true;
            _serverThread = new Thread(Serve) { IsBackground = true, Name = "PoSumoTelemetry" };
            _serverThread.Start();
            Systems_Log.Info($"TELEMETRY RESULT: listening on http://127.0.0.1:{_boundPort}/metrics");
        }

        private void Update()
        {
            if (Time.unscaledTime < _nextSample)
            {
                return;
            }
            _nextSample = Time.unscaledTime + SAMPLE_INTERVAL;

            if (Time.unscaledTime >= _nextRescan)
            {
                _nextRescan = Time.unscaledTime + RESCAN_INTERVAL;
                // Parameterless overload: the FindObjectsSortMode one is obsolete on
                // Unity 6.2 and this never wanted a sort order anyway.
                _agents = FindObjectsByType<Agent_Biped>(FindObjectsInactive.Exclude);
            }

            BuildSnapshot();
        }

        /// Composes the JSON payload and pushes the same values to TensorBoard.
        ///
        /// One StringBuilder, reused — this runs twice a second for the whole life
        /// of the process, and string concatenation here would be a steady drip of
        /// garbage for no reason.
        private void BuildSnapshot()
        {
            bool statsOn = Academy.IsInitialized;
            StatsRecorder stats = statsOn ? Academy.Instance.StatsRecorder : null;

            _builder.Clear();
            _builder.Append("{\"fps\":").Append((1f / Mathf.Max(1e-5f, Time.unscaledDeltaTime)).ToString("F1"));
            _builder.Append(",\"fixedDeltaTime\":").Append(Time.fixedDeltaTime.ToString("F4"));
            _builder.Append(",\"timeScale\":").Append(Time.timeScale.ToString("F2"));
            _builder.Append(",\"academy\":").Append(statsOn ? "true" : "false");
            _builder.Append(",\"trainer\":")
                    .Append(statsOn && Academy.Instance.IsCommunicatorOn ? "true" : "false");
            _builder.Append(",\"steps\":").Append(statsOn ? Academy.Instance.TotalStepCount : 0);
            _builder.Append(",\"fighters\":[");

            // Tracked separately from agentIndex: a destroyed or body-less agent is
            // skipped, so keying the separator off the loop index emits a leading
            // comma and produces JSON that no parser will accept.
            bool wroteAny = false;
            for (int agentIndex = 0; agentIndex < _agents.Length; agentIndex++)
            {
                Agent_Biped agent = _agents[agentIndex];
                if (agent == null)
                {
                    continue;
                }
                var body = agent.GetComponent<Agent_BipedBody>();
                if (body == null)
                {
                    continue;
                }

                float stamina = body.Stamina;
                if (wroteAny)
                {
                    _builder.Append(',');
                }
                wroteAny = true;
                _builder.Append("{\"name\":\"").Append(agent.behaviorName).Append('"');
                _builder.Append(",\"team\":").Append(agent.teamId);
                _builder.Append(",\"mode\":\"").Append(agent.mode.ToString()).Append('"');
                _builder.Append(",\"stamina\":").Append(stamina.ToString("F3"));
                _builder.Append(",\"x\":").Append(agent.TorsoX.ToString("F2"));
                _builder.Append(",\"down\":").Append(agent.IsDown ? "true" : "false");
                _builder.Append(",\"limp\":").Append(body.IsLimp ? "true" : "false");
                _builder.Append('}');

                // Aggregated by the trainer over the summary window, so one series
                // per behavior rather than per agent — 10 bipeds in a training scene
                // would otherwise be 10 indistinguishable lines.
                if (stats != null)
                {
                    if (!_staminaKeys.TryGetValue(agent, out string key))
                    {
                        key = "Body/" + agent.behaviorName + "/Stamina";
                        _staminaKeys.Add(agent, key);
                    }
                    stats.Add(key, stamina);
                }
            }

            _builder.Append("]}");

            string json = _builder.ToString();
            lock (_snapshotLock)
            {
                _snapshot = json;
            }
        }

        /// Accept loop. Serves `/metrics` (and `/`) as JSON, 404 for anything else.
        ///
        /// One request per connection, `Connection: close` — this is a diagnostic
        /// endpoint polled by curl and dashboards, not a web server, and keep-alive
        /// would mean tracking connection state for no benefit.
        private void Serve()
        {
            while (_running)
            {
                TcpClient client = null;
                try
                {
                    client = _listener.AcceptTcpClient();
                    using (client)
                    using (NetworkStream stream = client.GetStream())
                    {
                        stream.ReadTimeout = 2000;
                        stream.WriteTimeout = 2000;

                        var request = new byte[1024];
                        int read = stream.Read(request, 0, request.Length);
                        string head = read > 0 ? Encoding.ASCII.GetString(request, 0, read) : string.Empty;
                        bool wantsMetrics = head.StartsWith("GET /metrics") || head.StartsWith("GET / ");

                        string body;
                        string status;
                        if (wantsMetrics)
                        {
                            status = "200 OK";
                            lock (_snapshotLock)
                            {
                                body = _snapshot;
                            }
                        }
                        else
                        {
                            status = "404 Not Found";
                            body = "{\"error\":\"try /metrics\"}";
                        }

                        byte[] payload = Encoding.UTF8.GetBytes(body);
                        byte[] header = Encoding.ASCII.GetBytes(
                            $"HTTP/1.1 {status}\r\nContent-Type: application/json\r\n"
                            + $"Content-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
                        stream.Write(header, 0, header.Length);
                        stream.Write(payload, 0, payload.Length);
                        stream.Flush();
                    }
                }
                catch (SocketException)
                {
                    // Expected on shutdown: Stop() aborts the blocking Accept.
                }
                catch (System.Exception)
                {
                    // A malformed or dropped request must never take the loop down —
                    // this thread outlives the whole session.
                    client?.Close();
                }
            }
        }

        private void OnDestroy()
        {
            _running = false;
            // Stop() first: it unblocks AcceptTcpClient, which is otherwise parked
            // forever and would keep the thread (and the port) alive past teardown.
            _listener?.Stop();
            _serverThread?.Join(500);
            _listener = null;
            _serverThread = null;
            if (_instance == this)
            {
                _instance = null;
            }
        }
    }
}
