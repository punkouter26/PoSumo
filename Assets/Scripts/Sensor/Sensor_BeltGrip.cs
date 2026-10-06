using UnityEngine;

namespace PoSumo
{
    /// A hand that can take the opponent's belt. Sits on each forearm.
    ///
    /// When the forearm touches the OTHER fighter's pelvis (the mawashi) it pins
    /// itself there with a free hinge — a hand on a belt pivots, it does not weld —
    /// and holds until the pull on it exceeds GRIP_BREAK_FORCE, at which point
    /// Unity destroys the joint and the hand is free again. While it holds, the
    /// two bodies are physically tied at that point, so lifting, twisting and
    /// pulling the opponent work through ordinary joint torques.
    ///
    /// ponytail: the grip is AUTOMATIC — reach the belt and you have it, pull hard
    /// enough and it lets go. There is no grip action and no grip observation, so
    /// the brain contract stays 13 actions / 51 slots. The policy controls it only
    /// by where it puts its hands. Upgrade path if that is too sticky: a 14th/15th
    /// action for grip intent plus two observation slots — which is a new input
    /// AND output layer for every brain.
    ///
    /// Lives on the body, so it is identical in the game and in a training env.
    public sealed class Sensor_BeltGrip : MonoBehaviour
    {
        /// Peak one-handed pull a strong grip holds, newtons.
        private const float GRIP_BREAK_FORCE = 700f;
        /// A hand that just lost the belt cannot re-take it this soon.
        private const float REGRIP_SECONDS = 0.5f;
        /// PART_DEFS index of the pelvis — the belt.
        private const int BELT_PART = 0;

        private Agent_BipedBody _own;
        private HingeJoint2D _grip;
        private bool _wasHolding;
        private float _freeUntil;

        private void Awake()
        {
            _own = GetComponentInParent<Agent_BipedBody>();
        }

        private void FixedUpdate()
        {
            // The joint vanished: it broke. Start the re-grip clock once.
            if (_wasHolding && _grip == null)
            {
                _freeUntil = Time.fixedTime + REGRIP_SECONDS;
                Systems_Log.Info($"[GRIP] {_own.name} lost the belt");
            }
            _wasHolding = _grip != null;
        }

        private void OnCollisionEnter2D(Collision2D collision)
        {
            if (_grip != null || _own == null || !_own.beltGrips || _own.IsLimp) return;
            if (Time.fixedTime < _freeUntil) return;
            Rigidbody2D other = collision.rigidbody;
            if (other == null || collision.contactCount == 0) return;
            var otherBody = other.GetComponentInParent<Agent_BipedBody>();
            if (otherBody == null || otherBody == _own || other != otherBody.Parts[BELT_PART]) return;

            Vector2 point = collision.GetContact(0).point;
            _grip = gameObject.AddComponent<HingeJoint2D>();
            _grip.connectedBody = other;
            _grip.autoConfigureConnectedAnchor = false;
            _grip.anchor = transform.InverseTransformPoint(point);
            _grip.connectedAnchor = other.transform.InverseTransformPoint(point);
            _grip.enableCollision = true;
            _grip.breakForce = GRIP_BREAK_FORCE;
            Systems_Log.Info($"[GRIP] {_own.name} took {otherBody.name}'s belt");
        }

        /// Let go. Called on every pose reset and when the body goes limp.
        public void Release()
        {
            if (_grip != null)
            {
                // Disabled first: Destroy is deferred to the end of the frame, and a
                // physics step in between would yank two just-teleported bodies together.
                _grip.enabled = false;
                Destroy(_grip);
            }
            _grip = null;
            _wasHolding = false;
            _freeUntil = 0f;
        }
    }
}
