using UnityEngine;

namespace PoSumo
{
    /// Reports ground contact of non-foot body parts to the owning agent.
    /// "Ground" here means any static collider (the arena floor).
    public sealed class Sensor_BodyPartContact : MonoBehaviour
    {
        private Agent_Biped _agent;
        private int _touching;
        /// Cached once: `name` allocates a new string on every read.
        private string _partName;

        private void Start()
        {
            _agent = GetComponentInParent<Agent_Biped>();
            _partName = gameObject.name;
        }

        private static bool IsStatic(Collision2D c) =>
            c.rigidbody == null || c.rigidbody.bodyType == RigidbodyType2D.Static;

        private void OnCollisionEnter2D(Collision2D c)
        {
            if (!IsStatic(c)) return;
            _touching++;
            if (_agent != null)
            {
                _agent.NonFootGroundContacts++;
                // The FIRST part down is how the fall is described afterwards.
                if (_agent.NonFootGroundContacts == 1) _agent.FirstDownPart = _partName;
            }
        }

        private void OnCollisionExit2D(Collision2D c)
        {
            if (!IsStatic(c)) return;
            if (_touching > 0)
            {
                _touching--;
                if (_agent != null) _agent.NonFootGroundContacts--;
            }
        }

        /// Physics contact state can be stale after a teleport-style reset.
        public void Clear()
        {
            if (_agent != null) _agent.NonFootGroundContacts -= _touching;
            _touching = 0;
        }
    }
}
