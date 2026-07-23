using UnityEngine;

namespace PoSumo
{
    /// Static roster for the planned 8-fighter tournament. Matt and Standard have
    /// trained policies; the other six are placeholders awaiting training
    /// (folders under Assets/Agents/<Folder>/ hold their manifests). When a
    /// fighter gets a model, copy the checkpoint to
    /// Assets/Agents/<Folder>/<Name>.onnx and set hasModel = true here.
    public static class Systems_FighterRoster
    {
        public readonly struct FighterDef
        {
            public readonly string name;          // display name (scoreboard)
            public readonly string behaviorName;  // ML-Agents behavior / YAML key
            public readonly string folder;        // under Assets/Agents/
            public readonly Color color;
            public readonly bool hasModel;

            public FighterDef(string name_, string behavior, string folder_,
                              Color color_, bool hasModel_)
            {
                name = name_;
                behaviorName = behavior;
                folder = folder_;
                color = color_;
                hasModel = hasModel_;
            }
        }

        public static readonly FighterDef[] Fighters =
        {
            new FighterDef("MATT",     "Matt",     "Matt_v01",     new Color(0.85f, 0.25f, 0.2f),  true),
            new FighterDef("STANDARD", "Standard", "Standard_v01", new Color(0.2f,  0.5f,  0.3f),  true),
            new FighterDef("NICK",     "Nick",     "Nick_v01",     new Color(0.25f, 0.4f,  0.75f), false),
            new FighterDef("MAYA",     "Maya",     "Maya_v01",     new Color(0.55f, 0.3f,  0.65f), false),
            new FighterDef("TONGTONG", "TongTong", "TongTong_v01", new Color(0.85f, 0.65f, 0.2f),  false),
            new FighterDef("KIM",      "Kim",      "Kim_v01",      new Color(0.2f,  0.6f,  0.6f),  false),
            new FighterDef("TARO",     "Taro",     "Taro_v01",     new Color(0.3f,  0.32f, 0.6f),  false),
            new FighterDef("HANA",     "Hana",     "Hana_v01",     new Color(0.85f, 0.45f, 0.55f), false),
        };
    }
}
