using UnityEngine;

namespace PoSumo
{
    /// Presentation-companion spawning for `Systems_GameMatchManager`.
    ///
    /// Split out of the referee on 2026-08-25. The manager had reached 2294 lines
    /// holding the round state machine, scoring, the countdown, the ceremony camera
    /// beats, the HUD AND the construction of every presentation system, and both
    /// `.claude/rules/architecture.md` and `CLAUDE.md` call out separating the
    /// referee from the presentation spawning as the wanted refactor.
    ///
    /// It is a PARTIAL of the same class rather than a new one, deliberately:
    ///
    ///  - the fourteen `enable*` flags are private fields resolved from
    ///    `GameTuning` in `Start`, with the code defaults as the fallback for a
    ///    scene that has no tuning asset. Handing them to a separate type means
    ///    either exposing all fourteen or passing a fourteen-field struct, and both
    ///    add more surface than the split removes;
    ///  - the spawn helpers parent to `transform` and read `wrestlerA`/`wrestlerB`,
    ///    so a separate type would need those too;
    ///  - and it keeps the change behaviour-preserving. Nothing here moved except
    ///    which file it sits in, so this cannot alter a match.
    ///
    /// The next step, if the referee is ever properly extracted, is to make the
    /// flags a `Systems_GameTuning`-shaped record this can take by reference — at
    /// which point this file becomes a real class with no edits to its bodies.
    public sealed partial class Systems_GameMatchManager
    {
        /// Presentation, audio and face-mood are runtime-spawned children so
        /// scenes stay manager-only and older scenes pick them up automatically.
        private void SpawnCompanionSystems()
        {
            SpawnCompanion<Systems_MatchPresentation>(enablePresentation, "Presentation");
            SpawnCompanion<Systems_MatchAudio>(enableAudio, "MatchAudio");
            // One mood driver per fighter that actually has face art.
            if (enableFaceMood && FindAnyObjectByType<Systems_FaceMood>() == null)
            {
                SpawnFaceMood(wrestlerA);
                SpawnFaceMood(wrestlerB);
            }
            if (enableBodyDamage && FindAnyObjectByType<Systems_BodyDamage>() == null)
            {
                SpawnBodyDamage(wrestlerA);
                SpawnBodyDamage(wrestlerB);
            }
            // Sweat and clay are terms in the BodyLit shader, so they ride the
            // lighting flag rather than earning a GameTuning bool of their own —
            // with the lighting rig off there is no shaded surface to wet or stain,
            // and Systems_BodySurface disables itself on the flat-shading path.
            if (enableLighting && FindAnyObjectByType<Systems_BodySurface>() == null)
            {
                SpawnBodySurface(wrestlerA);
                SpawnBodySurface(wrestlerB);
            }
            if (enableVoice && FindAnyObjectByType<Systems_FighterVoice>() == null)
            {
                SpawnVoice(wrestlerA, 1f);
                // The bracket seeds every fighter twice, so both wrestlers can be
                // the same character. Drop the second one's pitch so a Matt-vs-Matt
                // bout does not sound like one man arguing with himself.
                bool mirrorMatch = wrestlerA != null && wrestlerB != null
                                   && wrestlerA.behaviorName == wrestlerB.behaviorName;
                SpawnVoice(wrestlerB, mirrorMatch ? mirrorPitchScale : 1f);
            }
            SpawnCompanion<Systems_ImpactFx>(enableImpactFx, "ImpactFx");
            SpawnCompanion<Systems_StrikeImpulse>(enableStrikeImpulse, "StrikeImpulse");
            SpawnCompanion<Systems_KimariteCaller>(enableKimarite, "Kimarite");
            SpawnCompanion<Systems_CrowdMomentum>(enableCrowdMomentum, "CrowdMomentum");
            // Haptics and event shake. Spawned AFTER Systems_ImpactFx on purpose:
            // both subscribe to Sensor_Impact.AnyImpact, and the ordinary-collision
            // shake should be applied before the discrete-event shake stacks on top
            // of it in the same frame. Nothing depends on that ordering for
            // correctness — trauma is commutative — but it keeps the reading order
            // of the two effects the same as the order they are documented in.
            SpawnCompanion<Systems_FeelFx>(enableFeelFx, "FeelFx");
            // Draws into the shared Systems_HudRoot like Systems_FightHud does,
            // so it needs no PanelSettings of its own and cannot fight the HUD
            // for draw order — that was the bug three separate UIDocuments at
            // equal sorting order caused, and one root is the fix that stuck.
            SpawnCompanion<Systems_FighterPanel>(enableFighterPanel, "FighterPanel");
            // Also after Systems_ImpactFx, and for the same reason: both watch
            // Sensor_Impact.AnyImpact independently. The shock ring has a much
            // higher speed gate (6.5 m/s against 2.2) so the two do not fire on
            // the same contacts except during a genuine slam, which is exactly
            // when both are wanted.
            SpawnCompanion<Systems_ShockwaveFx>(enableShockwave, "ShockwaveFx");
            SpawnCompanion<Systems_RingSqueezeCue>(enableRingSqueezeCue, "RingSqueezeCue");
            // ANDed with the build type, not left to the flag alone. `enablePerfHud`
            // defaults TRUE in code and is ABSENT from GameTuning.asset, so the code
            // default is what actually runs — and Systems_PerfHud carries no guard of
            // its own. A release APK therefore shipped with the frame-time/GC overlay
            // drawn over the fight (measured on 2026-08-25).
            //
            // Same gate Systems_Telemetry uses, and for the same reason: a diagnostic
            // should be impossible to ship by forgetting a tick box. The flag still
            // works — it turns the overlay off during development — it just cannot
            // turn it ON in a release player.
            bool developmentBuild = Debug.isDebugBuild || Application.isEditor;
            SpawnCompanion<Systems_PerfHud>(enablePerfHud && developmentBuild, "PerfHud");
            SpawnCompanion<Systems_ArenaLighting>(enableLighting, "ArenaLighting");
            SpawnCompanion<Systems_CareerRecorder>(recordCareerStats, "CareerRecorder");
            SpawnCompanion<Systems_ArenaAtmosphere>(enableAtmosphere, "Atmosphere");
            SpawnCompanion<Systems_MusicDirector>(enableMusic, "Music");
        }

        /// Spawns one companion as a child of the manager, if its GameTuning flag is
        /// on and the scene does not already have one. The per-fighter companions
        /// (face mood, voice, body damage) keep their own helpers because they need
        /// arguments; everything else is this one shape, and was six copies of it.
        private void SpawnCompanion<T>(bool enabled, string objectName) where T : Component
        {
            if (!enabled || FindAnyObjectByType<T>() != null) return;
            var go = new GameObject(objectName);
            go.transform.SetParent(transform, false);
            go.AddComponent<T>();
        }

        private void SpawnBodyDamage(Agent_Biped fighter)
        {
            if (fighter == null) return;
            var body = fighter.GetComponent<Agent_BipedBody>();
            if (body == null) return;
            var go = new GameObject($"Damage_{fighter.behaviorName}");
            go.transform.SetParent(transform, false);
            go.AddComponent<Systems_BodyDamage>().body = body;
        }

        /// One sweat/clay driver per fighter. Bound to the body instance rather
        /// than the behaviour name because a mirror bout has two wrestlers with the
        /// same name and each needs its own material written.
        private void SpawnBodySurface(Agent_Biped fighter)
        {
            if (fighter == null) return;
            var body = fighter.GetComponent<Agent_BipedBody>();
            if (body == null) return;
            var go = new GameObject($"Surface_{fighter.behaviorName}");
            go.transform.SetParent(transform, false);
            go.AddComponent<Systems_BodySurface>().body = body;
        }

        /// One voice per fighter. Systems_FighterVoice disables itself when that
        /// fighter has no recorded clips in Resources, so this is safe to call for
        /// everyone — today only Matt has a voice.
        private void SpawnVoice(Agent_Biped fighter, float pitchScale)
        {
            if (fighter == null) return;
            var go = new GameObject($"Voice_{fighter.behaviorName}");
            go.transform.SetParent(transform, false);
            var voice = go.AddComponent<Systems_FighterVoice>();
            // The instance, not just the name: in a mirror bout the name matches
            // both wrestlers and every voice bound to wrestlerA.
            voice.fighter = fighter;
            voice.fighterBehaviorName = fighter.behaviorName;
            voice.pitchScale = pitchScale;
        }

        private void SpawnFaceMood(Agent_Biped fighter)
        {
            if (fighter == null) return;
            var body = fighter.GetComponent<Agent_BipedBody>();
            if (body == null || body.character == null || body.character.headSprite == null) return;
            var go = new GameObject($"FaceMood_{fighter.behaviorName}");
            go.transform.SetParent(transform, false);
            var mood = go.AddComponent<Systems_FaceMood>();
            // See SpawnVoice: the instance disambiguates a mirror bout.
            mood.fighter = fighter;
            mood.fighterBehaviorName = fighter.behaviorName;
        }

    }
}
