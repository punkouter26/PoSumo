using UnityEngine;

namespace PoSumo
{
    /// Reports ground contact of non-foot body parts to the owning agent.
    /// "Ground" here means any static collider (the arena floor).
    public class Sensor_BodyPartContact : MonoBehaviour
    {
        Agent_Biped _agent;
        int _touching;

        void Start() { _agent = GetComponentInParent<Agent_Biped>(); }

        static bool IsStatic(Collision2D c) =>
            c.rigidbody == null || c.rigidbody.bodyType == RigidbodyType2D.Static;

        void OnCollisionEnter2D(Collision2D c)
        {
            if (!IsStatic(c)) return;
            _touching++;
            if (_agent != null) _agent.NonFootGroundContacts++;
        }

        void OnCollisionExit2D(Collision2D c)
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
