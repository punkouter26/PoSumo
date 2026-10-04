using UnityEngine;

namespace PoSumo
{
    /// The sound of a loaded foot being dragged across clay: one looping
    /// filtered-noise voice per fighter whose gain follows
    /// planted-foot slip speed x foot load.
    ///
    /// This is the continuous half of something Systems_MatchAudio only does in
    /// discrete form. Its `SFX_Scuff` one-shots fire on a foot's
    /// OnCollisionEnter2D, i.e. when a foot LANDS moving — but the thing a sumo
    /// bout is mostly made of is a planted foot giving ground under a shove, and
    /// a foot that never leaves the mat raises no collision-enter at all. Being
    /// driven backwards toward the rim was therefore silent.
    ///
    /// Both terms of the product are needed, and neither alone is the event:
    ///
    ///  - slip without load is a foot swinging just above the mat, or a severed
    ///    limb sliding — fast, and weightless;
    ///  - load without slip is a fighter standing still.
    ///
    /// Load is `Agent_BipedBody.FootLoadNear/Far`, which the body already samples
    /// every physics step for the contact observations, so this reads two floats
    /// and two velocities per fighter and touches no physics state.
    ///
    /// SILENT MEANS PAUSED, not volume 0. This project has already removed two
    /// continuous noise layers at the player's request (the breathing loop and
    /// the crowd bed — see Systems_MatchAudio.ENABLE_BREATHING) because filtered
    /// noise that never stops reads as hiss. So the ramp opens well above the
    /// speed of a foot merely settling, and below the floor the source is paused
    /// outright: a fighter standing still makes no sound and holds no voice.
    ///
    /// Routed the way Systems_MatchAudio routes everything: `spatialBlend = 0`
    /// with a manual pan by world x (true 3D falloff pumps with the follow
    /// camera's zoom), the `Systems_AudioMix` SFX level, the shared arena reverb,
    /// and a low-pass that drops with `Time.timeScale` so a slow-motion finish
    /// darkens this with the rest of the mix. It owns its own bus rather than
    /// borrowing MatchAudio's, because a companion must not reach into another.
    ///
    /// Presentation only. Spawned by Systems_GameMatchManager behind
    /// `enableFootScrape`; the training referee has no audio at all.
    public sealed class Systems_FootScrape : MonoBehaviour
    {
        [Header("Mapping")]
        // The ramp is fitted to a MEASURED distribution, not chosen. 135 975
        // planted-foot samples over two live bouts (2026-10-04) gave a horizontal
        // slide speed of p50 0.12, p75 0.29, p90 0.65, p95 1.00, p99 1.91 m/s.
        // 0.5 -> 2.0 therefore opens at about p86 and saturates at p99: inside
        // the range, so the term is connected, and high enough in it that the
        // loop sounds on 12% of planted-foot samples rather than on all of them.
        // (0.35 -> 1.6 was the first guess and sounded on 18%.)
        [Tooltip("Planted-foot slide speed (m/s, along the mat) below which nothing sounds. About the 86th percentile of measured planted-foot slip. Must stay above the creep of a foot that is merely settling (median 0.12 m/s), or the loop never stops.")]
        public float slipMinSpeed = 0.5f;
        [Tooltip("Slide speed treated as a full-strength scrape. About the 99th percentile of measured planted-foot slip.")]
        public float slipFullSpeed = 2f;
        [Tooltip("Foot load, as a fraction of body weight, that counts as fully weighted. Two-footed stance is ~0.5 each.")]
        [Range(0.05f, 1f)] public float loadFull = 0.45f;
        [Tooltip("Loop gain at a full-strength scrape, before the SFX mix level. Low on purpose: this sits UNDER the thuds, it is texture, not an event.")]
        [Range(0f, 1f)] public float maxVolume = 0.2f;

        [Header("Response")]
        [Tooltip("Gain rise per second. Fast — a skid starts abruptly.")]
        public float attack = 9f;
        [Tooltip("Gain fall per second. Slower than the attack so a stuttering slide reads as one scrape rather than a tremolo.")]
        public float release = 3.5f;

        [Header("Space")]
        [Tooltip("Arena half-width mapped to full stereo pan. Matches Systems_MatchAudio.panWidth so a scrape and the thud beside it sit in the same place.")]
        public float panWidth = 3.4f;

        private const string CLIP_PATH = "Audio/SFX_ScrapeLoop";
        private const int FIGHTER_COUNT = 2;
        /// Below this gain the source is paused. Roughly -50 dB: inaudible, and
        /// far enough from 0 that the release ramp reaches it in finite time.
        private const float SILENT_GAIN = 0.003f;

        // Slow-mo filtering, the same two corners Systems_MatchAudio uses.
        private const float LPF_OPEN = 22000f;
        private const float LPF_SLOWMO = 900f;

        private readonly AudioSource[] _sources = new AudioSource[FIGHTER_COUNT];
        private readonly Agent_BipedBody[] _bodies = new Agent_BipedBody[FIGHTER_COUNT];
        private readonly float[] _gains = new float[FIGHTER_COUNT];
        private readonly bool[] _sounding = new bool[FIGHTER_COUNT];

        private AudioLowPassFilter _lowPass;
        private Systems_GameMatchManager _manager;
        private float _centreX;

        private void Awake()
        {
            AudioClip clip = Resources.Load<AudioClip>(CLIP_PATH);
            if (clip == null)
            {
                // PoSumo -> Generate Audio has not been run since this clip was
                // added. Silent, like a fighter with no voice set: a missing
                // texture layer is not a fault worth a console line every bout.
                enabled = false;
                return;
            }

            // One bus object: the filters apply to every source on it.
            var bus = new GameObject("Bus_FootScrape");
            bus.transform.SetParent(transform, false);
            for (int fighterIndex = 0; fighterIndex < FIGHTER_COUNT; fighterIndex++)
            {
                AudioSource source = bus.AddComponent<AudioSource>();
                source.playOnAwake = false;
                source.loop = true;
                source.clip = clip;
                source.volume = 0f;
                // 2D with manual panning, for the reason NewSource in
                // Systems_MatchAudio records: 3D falloff pumps with camera zoom.
                source.spatialBlend = 0f;
                // Half a loop apart, so two fighters skidding at once are not the
                // same noise twice — identical loops in phase just sum to louder.
                source.timeSamples = fighterIndex * (clip.samples / FIGHTER_COUNT);
                _sources[fighterIndex] = source;
            }
            _lowPass = bus.AddComponent<AudioLowPassFilter>();
            _lowPass.cutoffFrequency = LPF_OPEN;
            Systems_AudioMix.AddArenaReverb(bus);
        }

        private void Start()
        {
            _manager = FindAnyObjectByType<Systems_GameMatchManager>();
            if (_manager == null)
            {
                enabled = false;
                return;
            }
            _centreX = _manager.transform.position.x;
            _bodies[0] = BodyOf(_manager.wrestlerA);
            _bodies[1] = BodyOf(_manager.wrestlerB);
        }

        private static Agent_BipedBody BodyOf(Agent_Biped fighter) =>
            fighter != null ? fighter.GetComponent<Agent_BipedBody>() : null;

        private void OnDisable()
        {
            // A paused game, a flag flipped mid-bout or a scene teardown must not
            // leave a loop running at whatever gain it last had.
            for (int fighterIndex = 0; fighterIndex < FIGHTER_COUNT; fighterIndex++)
            {
                _gains[fighterIndex] = 0f;
                Silence(fighterIndex);
            }
        }

        private void Update()
        {
            // Unscaled: the gain envelope is a property of the SOUND. On scaled
            // time a slow-motion finish would stretch the release fourfold and
            // smear a skid that has already stopped across the whole replay.
            float dt = Time.unscaledDeltaTime;
            float timeScale = Mathf.Clamp01(Time.timeScale);
            // A paused game (timeScale 0) still reports the velocities the bodies
            // were frozen with, so without this the loop would hold its last note
            // for as long as the pause menu is open.
            bool frozen = timeScale < 0.01f;
            float slow = 1f - Mathf.Clamp01(Mathf.InverseLerp(0.2f, 0.85f, timeScale));
            float mix = Systems_AudioMix.SfxLevel;

            for (int fighterIndex = 0; fighterIndex < FIGHTER_COUNT; fighterIndex++)
            {
                Agent_BipedBody body = _bodies[fighterIndex];
                float target = frozen || body == null ? 0f : Scrape(body) * maxVolume;
                float rate = target > _gains[fighterIndex] ? attack : release;
                float gain = Mathf.MoveTowards(_gains[fighterIndex], target, rate * dt);
                _gains[fighterIndex] = gain;

                if (gain <= SILENT_GAIN)
                {
                    Silence(fighterIndex);
                    continue;
                }

                AudioSource source = _sources[fighterIndex];
                if (!_sounding[fighterIndex])
                {
                    _sounding[fighterIndex] = true;
                    // Play, not UnPause: Play on a paused source resumes from
                    // where it stopped, and is also correct the very first time,
                    // when the source has never been started.
                    source.Play();
                }
                source.volume = gain * mix;
                source.panStereo =
                    Mathf.Clamp((body.Torso.position.x - _centreX) / Mathf.Max(0.01f, panWidth), -1f, 1f) * 0.75f;
                // A harder scrape is a slightly brighter one, and slow motion
                // drops the pitch with everything else on the SFX side.
                source.pitch = Mathf.Lerp(0.92f, 1.1f, gain / Mathf.Max(0.001f, maxVolume))
                               * Mathf.Lerp(1f, 0.82f, slow);
            }

            if (_lowPass != null)
            {
                float cutoff = Mathf.Lerp(LPF_OPEN, LPF_SLOWMO, slow);
                _lowPass.cutoffFrequency =
                    Mathf.Lerp(_lowPass.cutoffFrequency, cutoff, 1f - Mathf.Exp(-12f * dt));
            }
        }

        private void Silence(int fighterIndex)
        {
            if (!_sounding[fighterIndex])
            {
                return;
            }
            _sounding[fighterIndex] = false;
            AudioSource source = _sources[fighterIndex];
            if (source != null)
            {
                source.volume = 0f;
                source.Pause();
            }
        }

        /// 0..1 for one fighter: the louder of its two feet.
        private float Scrape(Agent_BipedBody body)
        {
            float near = body.FootNearAttached && body.FootDownNear
                ? FootScrape(body.FootNear, body.FootLoadNear) : 0f;
            float far = body.FootFarAttached && body.FootDownFar
                ? FootScrape(body.FootFar, body.FootLoadFar) : 0f;
            return Mathf.Max(near, far);
        }

        private float FootScrape(Rigidbody2D foot, float load)
        {
            if (foot == null)
            {
                return 0f;
            }
            // The mat is static, so the foot's own velocity IS its velocity
            // relative to the surface. Horizontal component only: a foot coming
            // straight down is a step, which the one-shot scuff already covers.
            float slip = Mathf.Abs(foot.linearVelocity.x);
            float slip01 = Mathf.Clamp01((slip - slipMinSpeed) / Mathf.Max(0.01f, slipFullSpeed - slipMinSpeed));
            return slip01 * Mathf.Clamp01(load / Mathf.Max(0.01f, loadFull));
        }
    }
}
