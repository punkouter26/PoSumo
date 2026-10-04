using UnityEngine;

namespace PoSumo
{
    /// The player's REALISTIC MODE switch: no dismemberment.
    ///
    /// ON turns off limb loss, decapitation and the gib for every bout built
    /// while it is set. Bruising, blood from a head knockout, the KO itself and
    /// the knockouts-to-lose-the-match rule are untouched — those are damage,
    /// not dismemberment, and the KO rule is a referee rule that must not depend
    /// on a presentation preference.
    ///
    /// OFF is the default and is exactly the game as it shipped before this
    /// existed.
    ///
    /// It is read ONCE per bout, by `Systems_BodyDamage.Start`, which is why the
    /// settings sheet says "applies from the next bout": flipping it mid-fight
    /// would put a limb back on a body that is already bleeding from the stump,
    /// or take one off with no blow to explain it.
    ///
    /// No cached static on purpose. The value lives in PlayerPrefs and is read
    /// through on every call, so there is no game state here to clear on
    /// SubsystemRegistration (Enter Play Mode domain reload is off in this
    /// project) and nothing that can disagree with what is on disk. It is read a
    /// handful of times per bout, never per frame.
    ///
    /// GAME-ONLY. A training env has no damage model at all, so this cannot reach
    /// a brain.
    public static class Systems_RealisticMode
    {
        private const string PREF_KEY = "posumo.realisticMode";

        public static bool Enabled
        {
            get { return PlayerPrefs.GetInt(PREF_KEY, 0) == 1; }
            set
            {
                PlayerPrefs.SetInt(PREF_KEY, value ? 1 : 0);
                // Flushed here, like every other preference in the settings
                // sheet: a switch changed and then lost to a killed app is the
                // one outcome a settings screen must not have.
                PlayerPrefs.Save();
            }
        }
    }
}
