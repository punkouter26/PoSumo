using System;
using UnityEngine;

namespace PoSumo
{
    /// Android device vibration, wrapped so the rest of the project can ask for
    /// "35 ms at 0.7" without knowing anything about JNI.
    ///
    /// A plain C# class rather than a MonoBehaviour, like the reward providers: it
    /// has no lifecycle of its own, and making it a component would mean a second
    /// GameObject and a second thing to keep enabled. `Systems_FeelFx` owns the one
    /// instance and disposes it in OnDestroy.
    ///
    /// WHY NOT `Handheld.Vibrate()`, which is one line and already available: it is
    /// a fixed buzz of roughly half a second at a fixed strength, on every device.
    /// This game wants a 12 ms tick on a landed blow and a 45 ms slam on a head
    /// knockout, and it wants them to feel different. `Handheld.Vibrate` cannot
    /// express either, and used at the rate a fight generates events it is one
    /// continuous drone.
    ///
    /// THREE ANDROID GENERATIONS, and all three still ship in the wild:
    ///
    ///  - API 31+ (Android 12): `getSystemService("vibrator")` is deprecated in
    ///    favour of `VibratorManager.getDefaultVibrator()`. The old call still
    ///    works, so this prefers the new one and falls back rather than branching
    ///    on it as if the old one were gone.
    ///  - API 26+ (Android 8): `VibrationEffect` with real amplitude control. This
    ///    is the path that makes a light tick possible at all.
    ///  - Below 26: `vibrate(long)` only — duration and nothing else. Amplitude is
    ///    silently dropped, which is the correct degradation: a weak tap becomes a
    ///    short tap.
    ///
    /// Everything here is a no-op off Android, including in the Editor. There is no
    /// desktop haptic path and pretending otherwise would mean an effect that
    /// cannot be verified where it appears to work.
    public sealed class Systems_Haptics : IDisposable
    {
        /// Android's amplitude scale is 1..255, and 0 means "off" rather than
        /// "quiet" — so a rounded-down amplitude must never reach it.
        private const int AMPLITUDE_MAX = 255;
        private const int AMPLITUDE_MIN = 1;

        /// `VibrationEffect.DEFAULT_AMPLITUDE`. Used on hardware that reports no
        /// amplitude control, where the device picks its own strength.
        private const int DEFAULT_AMPLITUDE = -1;

        private const int API_VIBRATION_EFFECT = 26;
        private const int API_VIBRATOR_MANAGER = 31;

#if UNITY_ANDROID && !UNITY_EDITOR
        private AndroidJavaObject _vibrator;
        private AndroidJavaClass _effectClass;
        private bool _useEffects;
        private bool _hasAmplitude;
#endif

        /// Latched after any failure so a device that throws does it once rather
        /// than on every blow of every round. A haptics fault must never be able to
        /// interrupt a match.
        private bool _unavailable;

        /// Whether this device can actually vibrate. Fixed once the constructor has
        /// run — it is a platform and hardware fact, not a preference — so a caller
        /// may use it to decide whether to subscribe to anything at all rather than
        /// re-testing it per event.
        public bool Available
        {
            get { return !_unavailable; }
        }

        public Systems_Haptics()
        {
            Initialize();
        }

        /// One pulse. `milliseconds` is clamped by the caller; `amplitude01` is
        /// 0..1 and is mapped onto Android's 1..255.
        public void Pulse(int milliseconds, float amplitude01)
        {
            if (_unavailable || milliseconds <= 0)
            {
                return;
            }

#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                if (_useEffects)
                {
                    int amplitude = _hasAmplitude ? ToAmplitude(amplitude01) : DEFAULT_AMPLITUDE;
                    using (AndroidJavaObject effect = _effectClass.CallStatic<AndroidJavaObject>(
                        "createOneShot", (long)milliseconds, amplitude))
                    {
                        _vibrator.Call("vibrate", effect);
                    }
                }
                else
                {
                    _vibrator.Call("vibrate", (long)milliseconds);
                }
            }
            catch (Exception error)
            {
                Fail("pulse", error);
            }
#endif
        }

        /// An alternating off/on pattern, in the shape Android's `createWaveform`
        /// takes: `timings` are durations in milliseconds starting with an OFF
        /// entry, `amplitudes` are 0..255 in step with them.
        ///
        /// Kept as a separate entry point rather than built out of several `Pulse`
        /// calls on a timer, because the platform renders the whole waveform itself
        /// — a Unity-side timer would have to survive `Time.timeScale` changes, the
        /// slow-motion finish and the scene load, all of which are happening at
        /// exactly the moment a match-win pattern plays.
        public void Pattern(long[] timings, int[] amplitudes)
        {
            if (_unavailable || timings == null || amplitudes == null)
            {
                return;
            }
            if (timings.Length == 0 || timings.Length != amplitudes.Length)
            {
                return;
            }

#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                if (_useEffects && _hasAmplitude)
                {
                    using (AndroidJavaObject effect = _effectClass.CallStatic<AndroidJavaObject>(
                        "createWaveform", timings, amplitudes, -1))
                    {
                        _vibrator.Call("vibrate", effect);
                    }
                }
                else
                {
                    // No amplitude control: the same timings still describe the
                    // rhythm, which is the part that carries the meaning.
                    _vibrator.Call("vibrate", timings, -1);
                }
            }
            catch (Exception error)
            {
                Fail("pattern", error);
            }
#endif
        }

        public void Dispose()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (_effectClass != null)
            {
                _effectClass.Dispose();
                _effectClass = null;
            }
            if (_vibrator != null)
            {
                _vibrator.Dispose();
                _vibrator = null;
            }
#endif
            _unavailable = true;
        }

        private void Initialize()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                int apiLevel;
                using (var version = new AndroidJavaClass("android.os.Build$VERSION"))
                {
                    apiLevel = version.GetStatic<int>("SDK_INT");
                }

                using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (AndroidJavaObject activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
                {
                    if (activity == null)
                    {
                        Fail("no activity", null);
                        return;
                    }

                    if (apiLevel >= API_VIBRATOR_MANAGER)
                    {
                        using (AndroidJavaObject manager =
                            activity.Call<AndroidJavaObject>("getSystemService", "vibrator_manager"))
                        {
                            if (manager != null)
                            {
                                _vibrator = manager.Call<AndroidJavaObject>("getDefaultVibrator");
                            }
                        }
                    }

                    if (_vibrator == null)
                    {
                        _vibrator = activity.Call<AndroidJavaObject>("getSystemService", "vibrator");
                    }
                }

                // A tablet or an emulator genuinely has no vibrator. Bail rather
                // than throwing on every blow for the rest of the session.
                if (_vibrator == null || !_vibrator.Call<bool>("hasVibrator"))
                {
                    Fail("device has no vibrator", null);
                    return;
                }

                _useEffects = apiLevel >= API_VIBRATION_EFFECT;
                if (_useEffects)
                {
                    _effectClass = new AndroidJavaClass("android.os.VibrationEffect");
                    _hasAmplitude = _vibrator.Call<bool>("hasAmplitudeControl");
                }

                Systems_Log.Info($"[HAPTICS] ready — api={apiLevel} effects={_useEffects} " +
                                 $"amplitude={_hasAmplitude}");
            }
            catch (Exception error)
            {
                Fail("init", error);
            }
#else
            // Not a fault and not worth a warning: the overwhelming majority of
            // this project's runs are in the Editor, where there is nothing to
            // vibrate. Latching here also means every call site short-circuits on
            // a bool instead of entering a try block.
            _unavailable = true;
#endif
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        private static int ToAmplitude(float amplitude01)
        {
            int amplitude = Mathf.RoundToInt(Mathf.Clamp01(amplitude01) * AMPLITUDE_MAX);
            return Mathf.Clamp(amplitude, AMPLITUDE_MIN, AMPLITUDE_MAX);
        }
#endif

        /// Warning rather than Systems_Log.Info: this is a real fault on a device
        /// that should support it, and per the project's logging rule a fault has
        /// to stay visible in a release build. It fires at most once.
        private void Fail(string stage, Exception error)
        {
            if (_unavailable)
            {
                return;
            }
            _unavailable = true;
            Debug.LogWarning($"[HAPTICS] disabled at {stage}: {(error == null ? "unsupported" : error.Message)}");
        }
    }
}
