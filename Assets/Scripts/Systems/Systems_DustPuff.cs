using UnityEngine;

namespace PoSumo
{
    /// Small clay-dust burst for impacts and ring-outs. Spawn via Burst();
    /// each particle is a fading sprite that destroys itself.
    public class Systems_DustPuff : MonoBehaviour
    {
        Vector2 _vel;
        float _life, _maxLife;
        SpriteRenderer _sr;

        public static void Burst(Vector3 pos, int count = 14)
        {
            for (int i = 0; i < count; i++)
            {
                var go = new GameObject("Dust");
                go.transform.position = pos + new Vector3(Random.Range(-0.22f, 0.22f), Random.Range(0f, 0.2f), 0f);
                // Small and faint: at the old size and opacity a hard shove buried
                // both fighters behind a wall of tan circles.
                float s = Random.Range(0.04f, 0.11f);
                go.transform.localScale = new Vector3(s, s, 1f);
                var sr = go.AddComponent<SpriteRenderer>();
                sr.sprite = Agent_BipedBody.CircleSprite();
                sr.color = new Color(0.8f, 0.72f, 0.56f, Random.Range(0.18f, 0.38f));
                sr.sortingOrder = 5;
                var p = go.AddComponent<Systems_DustPuff>();
                p._sr = sr;
                p._vel = new Vector2(Random.Range(-1.3f, 1.3f), Random.Range(0.5f, 1.8f));
                p._maxLife = p._life = Random.Range(0.25f, 0.5f);
            }
        }

        void Update()
        {
            _life -= Time.deltaTime;
            if (_life <= 0f) { Destroy(gameObject); return; }
            _vel += new Vector2(0f, -3f) * Time.deltaTime;
            transform.position += (Vector3)(_vel * Time.deltaTime);
            var c = _sr.color;
            c.a = (_life / _maxLife) * 0.35f;
            _sr.color = c;
            transform.localScale *= 1f + 0.9f * Time.deltaTime;
        }
    }
}
