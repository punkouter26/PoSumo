using UnityEngine;

namespace PoSumo
{
    /// How much of the HUD a live bout draws: MINIMAL, BROADCAST or FULL.
    ///
    /// One player-facing setting in place of a dozen `enable*` tick boxes. The
    /// tuning asset still decides whether a feature EXISTS; this can only hide a
    /// presentation companion the asset has switched on, never add one and never
    /// touch anything a referee sees. So it is deliberately a list of HUD
    /// questions (`ShowsCaster`, `ShowsWinOdds`, ...) rather than a setter over
    /// the manager's flags — strike impulse, crowd momentum, body damage, the
    /// arena mutators and the walk-in have no question here and cannot acquire
    /// one by accident.
    ///
    ///     MINIMAL ..... score, the MAT meter, one stamina bar per fighter
    ///     BROADCAST ... + caster line, fighter panel, win odds
    ///     FULL ........ + damage figures, stamina history, hardest-hit readout
    ///
    /// Read when a bout BUILDS its HUD, not live: the companions are spawned
    /// once per match in Systems_GameMatchManager.Start, so a change made from
    /// the settings sheet mid-bout lands on the next one. The sheet says so.
    ///
    /// Static, cached, and therefore carries the mandatory SubsystemRegistration
    /// reload — domain reload is off in this project, so without it a second
    /// Play session would read whatever the first one left in memory rather
    /// than what is in PlayerPrefs.
    public static class Systems_HudDensity
    {
        public enum Level
        {
            Minimal = 0,
            Broadcast = 1,
            Full = 2,
        }

        private const string PREF_KEY = "posumo.hudDensity";

        private static Level _current = Level.Minimal;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Reload()
        {
            _current = (Level)Mathf.Clamp(PlayerPrefs.GetInt(PREF_KEY, (int)Level.Minimal),
                                          (int)Level.Minimal, (int)Level.Full);
        }

        public static Level Current
        {
            get { return _current; }
            set
            {
                if (_current == value)
                {
                    return;
                }
                _current = value;
                PlayerPrefs.SetInt(PREF_KEY, (int)value);
                PlayerPrefs.Save();
            }
        }

        /// The caster's one-line chyron under the scorebug.
        public static bool ShowsCaster => _current >= Level.Broadcast;

        /// The tale-of-the-tape card: names, rank, record, build, push bar.
        public static bool ShowsFighterPanel => _current >= Level.Broadcast;

        /// The win-chance row in the dock. The engine behind it keeps running at
        /// every level — the camera director reads it — only the row is hidden.
        public static bool ShowsWinOdds => _current >= Level.Broadcast;

        /// The two six-region damage figures in the dock.
        public static bool ShowsDamageFigures => _current >= Level.Full;

        /// The stamina history sparklines and the hardest-hit readout.
        public static bool ShowsBiometrics => _current >= Level.Full;

        public static string Name(Level level)
        {
            switch (level)
            {
                case Level.Broadcast: return "BROADCAST";
                case Level.Full: return "FULL";
                default: return "MINIMAL";
            }
        }
    }
}
