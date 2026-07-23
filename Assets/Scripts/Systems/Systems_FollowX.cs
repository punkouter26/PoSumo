using UnityEngine;

namespace PoSumo
{
    /// Minimal viewing-scene camera rig: tracks a target's x position only,
    /// keeping height and rotation fixed. Used by SCN_WALKVIEW.
    public class Systems_FollowX : MonoBehaviour
    {
        public Transform target;
        public float smoothing = 5f;

        void LateUpdate()
        {
            if (target == null)
            {
                var body = FindAnyObjectByType<Agent_BipedBody>();
                if (body == null || body.Torso == null) return;
                target = body.Torso.transform;
            }
            var p = transform.position;
            float t = 1f - Mathf.Exp(-smoothing * Time.deltaTime);
            transform.position = new Vector3(Mathf.Lerp(p.x, target.position.x, t), p.y, p.z);
        }
    }
}
