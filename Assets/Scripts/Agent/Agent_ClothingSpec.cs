using UnityEngine;

namespace PoSumo
{
    /// What one fighter wears, region by region.
    ///
    /// Visual ONLY: a garment changes a body part's texture and renderer colour
    /// and nothing else — no mass, no collider, no joint, no observation — so it
    /// cannot invalidate a trained brain. `Agent_BipedBody.Build` reads this once
    /// at Awake (after `Systems_MatchRoster` has had its -500 pass) and paints the
    /// PART_DEFS art from it.
    ///
    /// Each region is a garment enum plus the colour it is tinted with. The
    /// fabric PATTERN comes from the garment (jeans are denim, sweatpants are
    /// fleece) and is generated procedurally by `Agent_Fabric`, so a character
    /// sheet never has to carry a texture asset.
    ///
    /// Coverage is decided by `Agent_BipedBody.PartColor`, which owns the
    /// PART_DEFS index map:
    ///
    ///   legs   jeans/sweatpants = pelvis + thighs + shins,
    ///          shorts/mawashi   = pelvis + thighs only (shins bare)
    ///   torso  all four         = the three trunk segments
    ///   arms   short sleeve     = upper arms only, long sleeve = both segments
    ///   feet   any              = the two feet and the two toes
    ///
    /// The head is never clothed — it carries the face art.
    [System.Serializable]
    public sealed class Agent_ClothingSpec
    {
        public enum LegWear { None, Jeans, Sweatpants, Shorts, Mawashi }
        public enum TorsoWear { None, TShirt, TankTop, Sweater, Singlet }
        public enum ArmWear { None, ShortSleeve, LongSleeve }
        public enum FootWear { None, Sneakers, Socks, Boots }

        [Tooltip("Garment for pelvis + thighs (+ shins when full length).")]
        public LegWear legs = LegWear.None;
        [Tooltip("Colour the leg garment is tinted with; the weave pattern comes from the garment itself.")]
        public Color legsColor = new Color(0.22f, 0.26f, 0.40f);

        [Tooltip("Garment for the three trunk segments (lower back, upper back, chest).")]
        public TorsoWear torso = TorsoWear.None;
        public Color torsoColor = new Color(0.93f, 0.92f, 0.90f);

        [Tooltip("Sleeves. Short covers the upper arms only; long covers upper arm and forearm.")]
        public ArmWear arms = ArmWear.None;
        public Color armsColor = new Color(0.93f, 0.92f, 0.90f);

        [Tooltip("Footwear for both feet and both toes.")]
        public FootWear feet = FootWear.None;
        public Color feetColor = new Color(0.95f, 0.95f, 0.93f);
    }
}
