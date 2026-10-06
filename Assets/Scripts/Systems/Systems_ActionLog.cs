using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace PoSumo
{
    /// Records what every fighter DID and what state he was in, so the patterns
    /// behind a win can be studied afterwards.
    ///
    /// Two flat CSV files per session, in `Logs/ActionLogs/` under the project
    /// when running in the Editor and in `persistentDataPath/ActionLogs/` on a
    /// device (pull with `adb pull`):
    ///
    ///   actions_<session>.csv   one row per fighter per sample (SAMPLE_EVERY
    ///                           physics steps = one policy decision): where he
    ///                           was, how he stood, and all 14 actions.
    ///   bouts_<session>.csv     one row per bout: who won, how, how long, and
    ///                           how many lunges each man threw.
    ///
    /// The two join on `bout`. Rows are deliberately flat and typed as plain
    /// numbers and short strings, so the same schema drops straight into a table
    /// store (PartitionKey = bout, RowKey = fighter + step) if it is ever uploaded.
    ///
    /// Read-only with respect to the fight. Spawned by Systems_GameMatchManager
    /// behind GameTuning.enableActionLog. GAME ONLY: a training env runs eight or
    /// more players at 20x and would write gigabytes an hour.
    public sealed class Systems_ActionLog : MonoBehaviour
    {
        /// One row per policy decision (decisionPeriod is 3 for every fighter).
        private const int SAMPLE_EVERY = 3;
        /// Session files beyond this many are deleted, oldest first.
        private const int KEEP_SESSIONS = 20;

        private Systems_GameMatchManager _manager;
        private Agent_BipedBody _bodyA, _bodyB;
        private StreamWriter _actions, _bouts;
        private readonly StringBuilder _line = new StringBuilder(512);
        private string _bout;
        private int _step, _lungesA, _lungesB, _seenLungesA, _seenLungesB;
        private float _boutStart;
        private bool _boutOpen;
        /// A lunge this recently before the end is treated as part of the finish.
        private const float RECENT_LUNGE_SECONDS = 2.5f;
        /// Torsos this close are wrestling, not standing off (matches Reward_SumoObjective).
        private const float CONTACT_GAP = 0.7f;
        private int _lastLungeStepA = -1, _lastLungeStepB = -1;
        private Agent_Biped _endWinner, _endLoser;
        private bool _endPending;

        // One session per app run, NOT per manager: the bracket loads a fresh arena
        // scene (and so a fresh logger) for every bout, and they must share files.
        private static string _session;
        private static int _boutNumber;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetSession()
        {
            _session = null;
            _boutNumber = 0;
        }

        private void Awake()
        {
            _manager = GetComponentInParent<Systems_GameMatchManager>();
        }

        private void OnEnable()
        {
            if (_manager == null)
            {
                enabled = false;
                return;
            }
            _manager.RoundStarted += OnRoundStarted;
            _manager.RoundEnded += OnRoundEnded;
            _manager.MatchReset += OnRoundStarted;
        }

        private void OnDisable()
        {
            if (_manager != null)
            {
                _manager.RoundStarted -= OnRoundStarted;
                _manager.RoundEnded -= OnRoundEnded;
                _manager.MatchReset -= OnRoundStarted;
            }
            Close(ref _actions);
            Close(ref _bouts);
        }

        private void Start()
        {
            try
            {
                string root = Application.isEditor
                    ? Path.Combine(Directory.GetCurrentDirectory(), "Logs", "ActionLogs")
                    : Path.Combine(Application.persistentDataPath, "ActionLogs");
                Directory.CreateDirectory(root);
                if (_session == null)
                {
                    Prune(root);
                    _session = System.DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
                }
                string actionsPath = Path.Combine(root, "actions_" + _session + ".csv");
                bool fresh = !File.Exists(actionsPath);
                _actions = new StreamWriter(actionsPath, true, Encoding.UTF8, 1 << 16);
                _bouts = new StreamWriter(Path.Combine(root, "bouts_" + _session + ".csv"), true, Encoding.UTF8, 1 << 12);
                if (!fresh)
                {
                    OpenBout();
                    return;
                }
                _line.Length = 0;
                _line.Append("bout,step,t,fighter,opponent,side,x,height,upright,vx,vy,stamina,footNear,footFar,down,gap,matLeft,lungeIntent,lunged");
                for (int actionIndex = 0; actionIndex < Agent_Biped.MotorCount; actionIndex++)
                {
                    _line.Append(",a").Append(actionIndex);
                }
                _actions.WriteLine(_line.ToString());
                _bouts.WriteLine("bout,fighterA,fighterB,winner,winnerSide,outcome,technique,cause,seconds,lungesA,lungesB,"
                    + "sinceLungeA,sinceLungeB,gapAtEnd,ringHalfWidth,"
                    + "loserFirstPartDown,loserX,loserHeight,loserUpright,loserPitchToward,loserMatLeft,loserStamina,"
                    + "winnerFirstPartDown,winnerX,winnerHeight,winnerUpright,winnerPitchToward,winnerMatLeft,winnerStamina");
                Systems_Log.Info("[ACTIONLOG] writing to " + root);
            }
            catch (IOException error)
            {
                // A log that cannot be written must never take the match down with it.
                Debug.LogWarning("[ACTIONLOG] disabled: " + error.Message);
                enabled = false;
                return;
            }
            OpenBout();
        }

        private void FixedUpdate()
        {
            if (!_boutOpen || _actions == null || _manager.wrestlerA == null || _manager.wrestlerB == null) return;
            if (_bodyA == null) _bodyA = _manager.wrestlerA.GetComponent<Agent_BipedBody>();
            if (_bodyB == null) _bodyB = _manager.wrestlerB.GetComponent<Agent_BipedBody>();
            // Frozen through the countdown, limp after the result: neither is the fight.
            if (!_bodyA.Torso.simulated || !_manager.wrestlerA.actionsEnabled || !_manager.wrestlerB.actionsEnabled) return;

            _step++;
            if (_step % SAMPLE_EVERY != 0) return;
            int beforeA = _lungesA, beforeB = _lungesB;
            Row(_manager.wrestlerA, _bodyA, _manager.wrestlerB, 'A', ref _seenLungesA, ref _lungesA);
            Row(_manager.wrestlerB, _bodyB, _manager.wrestlerA, 'B', ref _seenLungesB, ref _lungesB);
            if (_lungesA > beforeA) _lastLungeStepA = _step;
            if (_lungesB > beforeB) _lastLungeStepB = _step;
        }

        private void Row(Agent_Biped fighter, Agent_BipedBody body, Agent_Biped other, char side,
                         ref int seenLunges, ref int boutLunges)
        {
            int lunged = fighter.LungesThrown - seenLunges;
            seenLunges = fighter.LungesThrown;
            boutLunges += lunged;

            float centre = _manager.transform.position.x;
            float ground = _manager.transform.position.y;
            Vector2 position = fighter.Torso.position;
            Vector2 velocity = fighter.Torso.linearVelocity;
            // Signed toward the opponent, so "forward" means the same for both sides.
            float toward = Mathf.Sign(other.TorsoX - position.x);

            _line.Length = 0;
            _line.Append(_bout).Append(',').Append(_step).Append(',');
            Num(Time.time - _boutStart).Append(fighter.behaviorName).Append(',').Append(other.behaviorName).Append(',').Append(side).Append(',');
            Num(position.x - centre);
            Num(position.y - ground);
            Num(Vector2.Dot(body.Chest.transform.up, Vector2.up));
            Num(velocity.x * toward);
            Num(velocity.y);
            Num(body.Stamina);
            _line.Append(body.FootDownNear ? '1' : '0').Append(',').Append(body.FootDownFar ? '1' : '0').Append(',');
            _line.Append(fighter.IsDown ? '1' : '0').Append(',');
            Num(Mathf.Abs(other.TorsoX - position.x));
            // Clay left behind him before the rim.
            Num(_manager.CurrentRingHalfWidth + (position.x - centre) * toward);
            Num(fighter.LastLungeIntent);
            _line.Append(lunged);
            float[] actions = fighter.LastActions;
            for (int actionIndex = 0; actionIndex < Agent_Biped.MotorCount; actionIndex++)
            {
                _line.Append(',').Append(actions[actionIndex].ToString("F2", CultureInfo.InvariantCulture));
            }
            _actions.WriteLine(_line.ToString());
        }

        private StringBuilder Num(float value)
        {
            return _line.Append(value.ToString("F3", CultureInfo.InvariantCulture)).Append(',');
        }

        private void OnRoundStarted()
        {
            OpenBout();
        }

        private void OpenBout()
        {
            if (_actions == null || _manager.wrestlerA == null || _manager.wrestlerB == null) return;
            _boutNumber++;
            _bout = _session + "_" + _boutNumber;
            _step = 0;
            _lungesA = 0;
            _lungesB = 0;
            _lastLungeStepA = -1;
            _lastLungeStepB = -1;
            _seenLungesA = _manager.wrestlerA.LungesThrown;
            _seenLungesB = _manager.wrestlerB.LungesThrown;
            _boutStart = Time.time;
            _boutOpen = true;
        }

        private void OnRoundEnded(Agent_Biped winner, Agent_Biped loser)
        {
            if (!_boutOpen || _bouts == null) return;
            _boutOpen = false;
            _endWinner = winner;
            _endLoser = loser;
            // Written from LateUpdate of this same frame: the technique is named by
            // another companion's handler for this event, and handler order is not
            // something to depend on.
            _endPending = true;
        }

        private void LateUpdate()
        {
            if (!_endPending || _bouts == null) return;
            _endPending = false;
            Agent_Biped a = _manager.wrestlerA, b = _manager.wrestlerB;
            Agent_Biped winner = _endWinner, loser = _endLoser;
            float seconds = _step * Time.fixedDeltaTime;
            // Seconds before the end that each man last lunged; -1 = never this bout.
            float sinceLungeA = _lastLungeStepA < 0 ? -1f : (_step - _lastLungeStepA) * Time.fixedDeltaTime;
            float sinceLungeB = _lastLungeStepB < 0 ? -1f : (_step - _lastLungeStepB) * Time.fixedDeltaTime;
            float gap = Mathf.Abs(a.TorsoX - b.TorsoX);

            _line.Length = 0;
            _line.Append(_bout).Append(',').Append(a.behaviorName).Append(',').Append(b.behaviorName).Append(',')
                 .Append(winner != null ? winner.behaviorName : "draw").Append(',')
                 .Append(winner == null ? '-' : winner == a ? 'A' : 'B').Append(',')
                 .Append(_manager.LastOutcome).Append(',')
                 .Append(_manager.LastKimarite ?? "").Append(',')
                 .Append(Cause(winner, loser, a, sinceLungeA, sinceLungeB, gap)).Append(',');
            // Seconds of LIVE fight, from the sample count: wall time would include the countdown.
            Num(seconds);
            _line.Append(_lungesA).Append(',').Append(_lungesB).Append(',');
            Num(sinceLungeA);
            Num(sinceLungeB);
            Num(gap);
            Num(_manager.CurrentRingHalfWidth);
            EndState(loser, winner);
            EndState(winner, loser);
            _line.Length--;   // the trailing comma
            _bouts.WriteLine(_line.ToString());
            // A bout is the unit worth keeping: get it onto disk before anything can kill the app.
            _bouts.Flush();
            _actions.Flush();
        }

        /// One plain word for WHY it ended, from what was measured. The raw columns
        /// beside it are all there to re-derive it differently later.
        private string Cause(Agent_Biped winner, Agent_Biped loser, Agent_Biped a,
                             float sinceLungeA, float sinceLungeB, float gap)
        {
            if (winner == null || loser == null) return "draw";
            float loserLunge = loser == a ? sinceLungeA : sinceLungeB;
            float winnerLunge = winner == a ? sinceLungeA : sinceLungeB;
            bool loserJustLunged = loserLunge >= 0f && loserLunge <= RECENT_LUNGE_SECONDS;
            bool winnerJustLunged = winnerLunge >= 0f && winnerLunge <= RECENT_LUNGE_SECONDS;
            bool ringOut = _manager.LastOutcome == Systems_GameMatchManager.RoundOutcome.RingOut;
            if (loserJustLunged && !winnerJustLunged) return ringOut ? "lunged_out" : "lunged_and_fell";
            if (winnerJustLunged) return ringOut ? "lunge_drove_out" : "lunge_knocked_down";
            if (ringOut) return gap <= CONTACT_GAP ? "pushed_out" : "stepped_out";
            return gap <= CONTACT_GAP ? "put_down_in_contact" : "fell_unaided";
        }

        /// Seven columns describing one man at the end. Empty for a draw.
        private void EndState(Agent_Biped fighter, Agent_Biped other)
        {
            if (fighter == null || other == null)
            {
                _line.Append(",,,,,,,");
                return;
            }
            var body = fighter.GetComponent<Agent_BipedBody>();
            float centre = _manager.transform.position.x;
            Vector2 position = fighter.Torso.position;
            float toward = Mathf.Sign(other.TorsoX - position.x);
            Vector2 up = body.Chest.transform.up;
            _line.Append(fighter.FirstDownPart ?? "").Append(',');
            Num(position.x - centre);
            Num(position.y - _manager.transform.position.y);
            Num(up.y);
            // > 0 = pitched face-first toward the opponent, < 0 = going over backwards.
            Num(up.x * toward);
            Num(_manager.CurrentRingHalfWidth + (position.x - centre) * toward);
            Num(body.Stamina);
        }

        private static void Close(ref StreamWriter writer)
        {
            if (writer == null) return;
            writer.Flush();
            writer.Dispose();
            writer = null;
        }

        /// Keeps the newest KEEP_SESSIONS sessions. File names sort by time.
        private static void Prune(string root)
        {
            string[] files = Directory.GetFiles(root, "actions_*.csv");
            System.Array.Sort(files, System.StringComparer.Ordinal);
            for (int fileIndex = 0; fileIndex < files.Length - KEEP_SESSIONS; fileIndex++)
            {
                File.Delete(files[fileIndex]);
                File.Delete(files[fileIndex].Replace("actions_", "bouts_"));
            }
        }
    }
}
