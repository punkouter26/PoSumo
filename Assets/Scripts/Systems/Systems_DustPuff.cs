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
                go.transform.position = pos + new Vector3(Random.Range(-0.25f, 0.25f), Random.Range(0f, 0.25f), 0f);
                float s = Random.Range(0.08f, 0.22f);
                go.transform.localScale = new Vector3(s, s, 1f);
                var sr = go.AddComponent<SpriteRenderer>();
                sr.sprite = Agent_BipedBody.CircleSprite();
                sr.color = new Color(0.78f, 0.68f, 0.52f, Random.Range(0.5f, 0.85f));
                sr.sortingOrder = 5;
                var p = go.AddComponent<Systems_DustPuff>();
                p._sr = sr;
                p._vel = new Vector2(Random.Range(-1.6f, 1.6f), Random.Range(0.6f, 2.2f));
                p._maxLife = p._life = Random.Range(0.35f, 0.7f);
            }
        }

        void Update()
        {
            _life -= Time.deltaTime;
            if (_life <= 0f) { Destroy(gameObject); return; }
            _vel += new Vector2(0f, -3f) * Time.deltaTime;
            transform.position += (Vector3)(_vel * Time.deltaTime);
            var c = _sr.color;
            c.a = (_life / _maxLife) * 0.8f;
            _sr.color = c;
            transform.localScale *= 1f + 1.2f * Time.deltaTime;
        }
    }
}
