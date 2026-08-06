using Unity.MLAgents;
using UnityEngine;

namespace PoSumo
{
    /// Static subsystem guard: keeps the player loop running when the window
    /// loses focus (background training), enforces solver precision and the
    /// fixed timestep, caps the play frame rate, and disposes the ML-Agents
    /// Academy on exit to prevent hangs.
    public static class Systems_AcademyLifecycle
    {
        private const int TARGET_FRAME_RATE = 60;
        private const float FIXED_TIMESTEP = 0.02f;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Init()
        {
            Application.runInBackground = true;
            Physics2D.gravity = new Vector2(0f, -9.81f);
            // Asserted at runtime for the same reason gravity is: the brains were
            // trained against a 50 Hz physics tick, and every torque ramp in
            // Agent_BipedBody (activation tau, passive damping) integrates against
            // Time.fixedDeltaTime. A quality-level or platform override here would
            // change the dynamics the policies were fitted to without any error.
            // Nothing writes fixedDeltaTime elsewhere — the slow-mo finish moves
            // Time.timeScale only, which rescales the tick's wall-clock cadence
            // and leaves the simulated step deterministic.
            Time.fixedDeltaTime = FIXED_TIMESTEP;
            // Raised for the 14-body, 13-joint ragdoll on the widened ring. Two
            // things got harder at once: the mat doubled so collisions arrive with
            // more closing speed, and the KO knockback applies a mass-scaled
            // impulse to all 14 parts on one frame. Under-solved, a hinge chain
            // that long visibly separates at the joints when hit hard.
            Physics2D.positionIterations = 16;
            Physics2D.velocityIterations = 14;

            Application.quitting += DisposeAcademy;
        }

        /// Locks the played game to 60 FPS — but never a training run.
        ///
        /// Capping the frame rate caps the rate the player loop (and therefore
        /// FixedUpdate catch-up) is driven at, so applying this to a headless env
        /// throttles training to roughly real time and costs hours per million
        /// steps. Two gates keep it off: `isBatchMode` covers the `--no-graphics`
        /// env builds, and the communicator check covers the Editor attached to a
        /// live `mlagents-learn`.
        ///
        /// Runs AfterSceneLoad rather than BeforeSceneLoad on purpose: agents
        /// bring the Academy up in OnEnable during scene load, so the communicator
        /// is only answerable once that has happened. Reading `IsInitialized`
        /// first matters — `Academy.Instance` would otherwise construct one here
        /// and make every build look like it is training.
        ///
        /// `vSyncCount` must be zeroed or targetFrameRate is ignored outright; the
        /// project's Medium-and-above quality levels ship it at 1.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void ApplyFrameRateCap()
        {
            if (Application.isBatchMode)
            {
                return;
            }
            if (Academy.IsInitialized && Academy.Instance.IsCommunicatorOn)
            {
                return;
            }

            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = TARGET_FRAME_RATE;
        }

        private static void DisposeAcademy()
        {
            if (Academy.IsInitialized)
            {
                Academy.Instance.Dispose();
            }
        }
    }
}
