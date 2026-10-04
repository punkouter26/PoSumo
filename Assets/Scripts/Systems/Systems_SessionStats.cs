using UnityEngine;

namespace PoSumo
{
    /// Counters for THIS play session, read by the debug panel's NEEDS ATTENTION
    /// block.
    ///
    /// Static because they have to outlive the LoadScene between bracket bouts —
    /// the referee that writes them and the panel that reads them are both
    /// spawned fresh per match — and cleared on SubsystemRegistration because
    /// domain reload is off, so they would otherwise carry into the next Play
    /// session and report last session's rounds as this one's.
    ///
    /// Written only by Systems_GameMatchManager, at the two moments it already
    /// logs: the end of a round and the end of a walk-in. Read-only everywhere
    /// else, and nothing here feeds back into a fight.
    public static class Systems_SessionStats
    {
        /// Rounds that reached a decision this session.
        public static int Rounds { get; private set; }

        /// Of those, how many ended BEFORE the mat began to close — i.e. were
        /// settled by the fighters rather than by the floor being withdrawn.
        public static int RoundsBeforeShrink { get; private set; }

        /// Round-opening walk-ins played this session.
        public static int WalkIns { get; private set; }

        /// Of those, how many stalled or timed out short of contact.
        public static int WalkInStalls { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatic()
        {
            Rounds = 0;
            RoundsBeforeShrink = 0;
            WalkIns = 0;
            WalkInStalls = 0;
        }

        public static void RecordRound(float elapsedSeconds, float shrinkStartSeconds)
        {
            Rounds++;
            // A shrink start of zero means the mat never closes, so no round can
            // have been decided by it.
            if (shrinkStartSeconds <= 0f || elapsedSeconds < shrinkStartSeconds)
            {
                RoundsBeforeShrink++;
            }
        }

        public static void RecordWalkIn(bool reachedContact)
        {
            WalkIns++;
            if (!reachedContact)
            {
                WalkInStalls++;
            }
        }
    }
}
