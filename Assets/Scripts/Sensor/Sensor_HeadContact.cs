using UnityEngine;

namespace PoSumo
{
    /// Reports whether the HEAD is on the arena FLOOR below the dohyo.
    ///
    /// The head is a compound collider on the Chest rigidbody, so the per-part
    /// `Sensor_BodyPartContact` on the chest cannot tell a head touch from a
    /// shoulder touch — both arrive as a Chest collision. This sits on the
    /// `HeadHitbox` child and only counts contacts made through that collider,
    /// which is what lets both referees rule the ring-out on the head landing
    /// on the floor (ringOutOnHeadFloor, 2026-08-26) without touching the
    /// existing IsDown machinery. The generic "head on any static collider"
    /// count was deleted with the head-touch rule on 2026-08-26.
    public sealed class Sensor_HeadContact : MonoBehaviour
    {
        private Collider2D _own;
        private int _floorTouching;

        /// The arena floor below the dohyo. Set by the referee from
        /// `Systems_SumoArena.FloorCollider`; contacts with it are counted apart
        /// from every other static collider, because a head on the MAT and a head
        /// on the FLOOR are different rules.
        [System.NonSerialized] public Collider2D floor;

        /// True while the head is on the arena floor below the dohyo.
        public bool TouchingFloor => _floorTouching > 0;

        private void Awake() { _own = GetComponent<Collider2D>(); }

        private static bool IsStatic(Collision2D c) =>
            c.rigidbody == null || c.rigidbody.bodyType == RigidbodyType2D.Static;

        private void OnCollisionEnter2D(Collision2D c)
        {
            // Callbacks reach both the collider's object and the rigidbody's;
            // guard on the collider so a chest contact routed here never counts.
            if (c.otherCollider != _own || !IsStatic(c)) return;
            if (floor != null && c.collider == floor) _floorTouching++;
        }

        private void OnCollisionExit2D(Collision2D c)
        {
            if (c.otherCollider != _own || !IsStatic(c)) return;
            if (floor != null && c.collider == floor && _floorTouching > 0) _floorTouching--;
        }

        /// Physics contact state can be stale after a teleport-style reset.
        public void Clear() { _floorTouching = 0; }
    }
}
