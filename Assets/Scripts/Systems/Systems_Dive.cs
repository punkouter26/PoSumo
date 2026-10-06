using UnityEngine;

namespace PoSumo
{
    /// The last-ditch DIVE: a fighter who has his opponent near the bales
    /// sometimes throws his whole body at him to carry him out.
    ///
    /// It is a real gamble under touchDownLoses, exactly as in the sport: the
    /// diver is going to land on the clay, so he wins only if the other man is
    /// out or down first.
    ///
    /// Called by BOTH referees from FixedUpdate, so a policy trains against the
    /// same dive it meets in the game. Stateless — each referee owns its own
    /// `nextCheck` clock — so there is nothing here to reset between sessions.
    ///
    /// ponytail: the leap is a referee-applied impulse, not something the policy
    /// chooses. It fires at random when the geometry is right. Upgrade path if
    /// the fighters should decide for themselves: a 14th action for dive intent,
    /// which is a new output layer for every brain.
    public static class Systems_Dive
    {
        /// How often the geometry is tested, seconds.
        private const float CHECK_INTERVAL = 0.25f;
        /// After a dive, nobody dives again this soon.
        private const float COOLDOWN = 4f;
        /// The target must be within this of the rim behind him, metres.
        private const float EDGE_ZONE = 0.9f;
        /// Torso-to-torso gap the leap can cover, metres.
        private const float MIN_GAP = 0.3f;
        private const float MAX_GAP = 1.5f;
        /// THE STAND-OFF LUNGE. Two fighters planted this far apart and this still
        /// are blocking each other with their feet instead of wrestling (measured
        /// 2026-10-05: 1.2 m apart for 55 s). One of them charges, anywhere on the mat.
        private const float STANDOFF_MIN_GAP = 0.7f;
        private const float STANDOFF_MAX_SPEED = 0.4f;
        /// A charge to close the gap, not a leap to clear the rim.
        private const float LUNGE_SPEED_SHARE = 0.7f;

        public static void Tick(ref float nextCheck, float elapsed,
                                Agent_Biped a, Agent_BipedBody bodyA,
                                Agent_Biped b, Agent_BipedBody bodyB,
                                float centreX, float ringHalfWidth,
                                float chancePerSecond, float speed)
        {
            if (chancePerSecond <= 0f || elapsed < nextCheck) return;
            nextCheck = elapsed + CHECK_INTERVAL;
            if (Random.value > chancePerSecond * CHECK_INTERVAL) return;

            if (TryDive(a, bodyA, b, centreX, ringHalfWidth, speed)
                || TryDive(b, bodyB, a, centreX, ringHalfWidth, speed)
                || TryLunge(a, bodyA, b, bodyB, speed * LUNGE_SPEED_SHARE))
            {
                nextCheck = elapsed + COOLDOWN;
            }
        }

        private static bool TryLunge(Agent_Biped a, Agent_BipedBody bodyA,
                                     Agent_Biped b, Agent_BipedBody bodyB, float speed)
        {
            if (a == null || b == null || bodyA == null || bodyB == null) return false;
            float distance = Mathf.Abs(b.TorsoX - a.TorsoX);
            if (distance < STANDOFF_MIN_GAP || distance > MAX_GAP) return false;
            if (Mathf.Abs(a.Torso.linearVelocity.x) > STANDOFF_MAX_SPEED
                || Mathf.Abs(b.Torso.linearVelocity.x) > STANDOFF_MAX_SPEED) return false;

            // Either man may go; a coin decides, and he must be on his feet.
            bool aGoes = Random.value < 0.5f;
            Agent_Biped attacker = aGoes ? a : b;
            Agent_BipedBody body = aGoes ? bodyA : bodyB;
            Agent_Biped target = aGoes ? b : a;
            if (body.IsLimp || attacker.IsDown) return false;
            if (!body.FootDownNear && !body.FootDownFar) return false;

            body.Launch(Mathf.Sign(target.TorsoX - attacker.TorsoX), speed);
            Systems_Log.Info($"[DIVE] {body.name} lunges at {target.name}, {distance:F2} m");
            return true;
        }

        private static bool TryDive(Agent_Biped attacker, Agent_BipedBody body, Agent_Biped target,
                                    float centreX, float ringHalfWidth, float speed)
        {
            if (attacker == null || body == null || target == null) return false;
            if (body.IsLimp || attacker.IsDown) return false;
            // He has to push off something.
            if (!body.FootDownNear && !body.FootDownFar) return false;

            float targetOffset = target.TorsoX - centreX;
            if (ringHalfWidth - Mathf.Abs(targetOffset) > EDGE_ZONE) return false;

            float gap = target.TorsoX - attacker.TorsoX;
            float distance = Mathf.Abs(gap);
            if (distance < MIN_GAP || distance > MAX_GAP) return false;
            // Only outward: the attacker is on the inside, driving toward that rim.
            if (Mathf.Sign(gap) != Mathf.Sign(targetOffset)) return false;

            body.Launch(Mathf.Sign(gap), speed);
            Systems_Log.Info($"[DIVE] {body.name} dives at {target.name}, {distance:F2} m");
            return true;
        }
    }
}
