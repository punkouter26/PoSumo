using UnityEngine;

namespace PoSumo
{
    /// Reports whether THIS body part is resting on the arena floor — the
    /// ring-out since 2026-08-26 (`ringOutOnFloorContact` in both referees):
    /// any part of a fighter touching the ground below the dohyo loses.
    ///
    /// "Floor" is geometric, not a specific collider: a static contact whose
    /// contact point is below `floorLevelY` (the referee sets it to the mat top
    /// minus a margin). That covers `ArenaFloor` AND the lower face of the
    /// platform a body slides down, while the mat top and the tawara bands sit
    /// above the line and never count. Its predecessor compared against the one
    /// `FloorCollider`, and a measured bracket stalled forever with two limp
    /// fighters resting on each other and the platform side, touching nothing
    /// that qualified.
    ///
    /// Evaluated from `OnCollisionStay2D` every physics step rather than by
    /// enter/exit counting, so a part that slides from the mat top down the
    /// platform face is caught the step its contact point crosses the line, and
    /// a teleport-style reset needs no bookkeeping — the flag simply expires.
    public sealed class Sensor_FloorContact : MonoBehaviour
    {
        /// Contacts below this world Y count as the floor. Set by the referee
        /// through `Agent_Biped.BindArenaFloor`; -inf until then, so nothing counts.
        [System.NonSerialized] public float floorLevelY = float.NegativeInfinity;

        private Collider2D _own;
        private float _lastFloorTime = float.NegativeInfinity;

        /// True while this part touched the floor during the current or the
        /// previous physics step.
        public bool TouchingFloor => Time.fixedTime - _lastFloorTime <= Time.fixedDeltaTime * 1.5f;

        private void Awake() { _own = GetComponent<Collider2D>(); }

        private static bool IsStatic(Collision2D c) =>
            c.rigidbody == null || c.rigidbody.bodyType == RigidbodyType2D.Static;

        private void OnCollisionStay2D(Collision2D c)
        {
            // UNBOUND: no referee has called BindArenaFloor on this fighter, so
            // floorLevelY is -inf and no contact can ever qualify. Cheap, and it is
            // not a rare case — six of the ten agents in every SCN_TRAIN_* scene are
            // walk-lane agents with no referee at all, so 6/10 of the bodies in a
            // training env were scanning every contact of every part, every physics
            // step, to reach a comparison that cannot be true.
            if (float.IsNegativeInfinity(floorLevelY)) return;

            // Already recorded this step by another contact on this part. The result
            // is a single bool for the whole step, so the rest of the scan cannot
            // change it.
            if (_lastFloorTime == Time.fixedTime) return;

            // Callbacks reach both the collider's object and the rigidbody's; the
            // head hitbox shares the chest rigidbody, so guard on the collider.
            if (c.otherCollider != _own || !IsStatic(c)) return;
            int count = c.contactCount;
            for (int contactIndex = 0; contactIndex < count; contactIndex++)
            {
                if (c.GetContact(contactIndex).point.y < floorLevelY)
                {
                    _lastFloorTime = Time.fixedTime;
                    return;
                }
            }
        }

        public void Clear() { _lastFloorTime = float.NegativeInfinity; }
    }
}
