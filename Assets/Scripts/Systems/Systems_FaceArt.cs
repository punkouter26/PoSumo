using System.Collections.Generic;
using UnityEngine;
using UnityEngine.U2D;

namespace PoSumo
{
    /// The one place face sprites are looked up by name.
    ///
    /// The 21 face PNGs used to live in `Assets/Resources/Faces/` and were loaded
    /// with `Resources.Load<Sprite>("Faces/<name>")`. That defeated the atlas:
    /// **a sprite inside a Resources folder is never packed into a Sprite Atlas**
    /// (measured 2026-08-26 — `FaceAtlas` packed in 7 ms with `sprite.packed`
    /// false on every face, including the ones it listed), so every expression
    /// swap was its own texture and its own draw call.
    ///
    /// The PNGs now sit in `Assets/Art/Faces/` (same GUIDs — `git mv`), the atlas
    /// itself is the Resources asset (`Resources/Atlases/FaceAtlas`), and this
    /// hands out `SpriteAtlas.GetSprite(name)` results. Those are CLONES on every
    /// call, so they are cached: `Systems_FaceMood` asks for seven per fighter at
    /// Start and would otherwise leak a sprite per lookup.
    public static class Systems_FaceArt
    {
        private const string ATLAS_PATH = "Atlases/FaceAtlas";

        private static SpriteAtlas _atlas;
        private static bool _atlasLookedUp;
        private static readonly Dictionary<string, Sprite> Cache = new Dictionary<string, Sprite>();

        /// Domain reload is off in this project, so statics survive between Play
        /// sessions; clear the cache so a stale atlas from the last session is not
        /// handed out.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Reset()
        {
            _atlas = null;
            _atlasLookedUp = false;
            Cache.Clear();
        }

        /// A face sprite by bare name (e.g. "Nick_Happy_2"), or null if the atlas
        /// does not carry it. Never logs — callers decide whether a missing face
        /// is worth a warning (Systems_FaceMood counts what loaded).
        public static Sprite Load(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (Cache.TryGetValue(name, out Sprite cached)) return cached;

            if (!_atlasLookedUp)
            {
                _atlasLookedUp = true;
                _atlas = Resources.Load<SpriteAtlas>(ATLAS_PATH);
                if (_atlas == null)
                {
                    Debug.LogWarning($"Systems_FaceArt: no SpriteAtlas at Resources/{ATLAS_PATH} — faces will not load.");
                }
            }

            Sprite sprite = _atlas != null ? _atlas.GetSprite(name) : null;
            Cache[name] = sprite;   // null cached too: one miss, not one per frame
            return sprite;
        }
    }
}
