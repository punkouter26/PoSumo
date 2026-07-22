using UnityEngine;

namespace PoSumo
{
    /// Soft blob shadow that grounds a wrestler visually: tracks the torso
    /// across the dohyo top and the lower crowd floor, shrinking and fading as
    /// the body rises. Spawned by Agent_BipedBody at build time.
    public class Systems_BlobShadow : MonoBehaviour
    {
        public Rigidbody2D target;                 // pelvis rigidbody
        public float platformHalfWidth = 2.75f;    // dohyo top extent from arena center
        public float platformY = 0f;               // dohyo top surface
        public float floorY = -0.6f;               // crowd-floor surface (fall-off landings)
        public float arenaCenterX = 0f;
        public float baseWidth = 0.9f;
        public float torsoStandHeight = 0.97f;     // torso height above the ground when standing

        SpriteRenderer _renderer;
        static Sprite _softDisc;

        static Sprite SoftDisc()
        {
            if (_softDisc != null) return _softDisc;
            const int S = 128;
            var tex = new Texture2D(S, S, TextureFormat.RGBA32, false);
            float r = S / 2f - 1f;
            for (int y = 0; y < S; y++)
            {
                for (int x = 0; x < S; x++)
                {
                    float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(S / 2f, S / 2f));
                    float a = Mathf.Clamp01(1f - d / r);
                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, a * a));
                }
            }
            tex.Apply();
            _softDisc = Sprite.Create(tex, new Rect(0, 0, S, S), new Vector2(0.5f, 0.5f), S,
                                      0, SpriteMeshType.FullRect);
            return _softDisc;
        }

        void Awake()
        {
            _renderer = gameObject.AddComponent<SpriteRenderer>();
            _renderer.sprite = SoftDisc();
            _renderer.sortingOrder = -1; // above the clay, under every body part
        }

        void LateUpdate()
        {
            if (target == null) return;

            Vector2 tp = target.position;
            float groundY = Mathf.Abs(tp.x - arenaCenterX) <= platformHalfWidth ? platformY : floorY;

            // 1 when the feet are planted, fading to 0 as the body lifts away.
            float lift = Mathf.Clamp(tp.y - torsoStandHeight - groundY, 0f, 1.5f);
            float presence = 1f - lift / 1.5f;

            transform.position = new Vector3(tp.x, groundY + 0.02f, 0f);
            float w = baseWidth * (0.6f + 0.4f * presence);
            transform.localScale = new Vector3(w, w * 0.22f, 1f);
            _renderer.color = new Color(0f, 0f, 0f, 0.32f * (0.35f + 0.65f * presence));
        }
    }
}
