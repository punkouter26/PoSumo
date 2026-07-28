using Unity.MLAgents;
using UnityEngine;

namespace PoSumo
{
    /// Static subsystem guard: keeps the player loop running when the window
    /// loses focus (background training), enforces solver precision, and
    /// disposes the ML-Agents Academy on exit to prevent hangs.
    public static class Systems_AcademyLifecycle
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Init()
        {
            Application.runInBackground = true;
            Physics2D.gravity = new Vector2(0f, -9.81f);
            // Raised for the 14-body, 13-joint ragdoll on the widened ring. Two
            // things got harder at once: the mat doubled so collisions arrive with
            // more closing speed, and the KO knockback applies a mass-scaled
            // impulse to all 14 parts on one frame. Under-solved, a hinge chain
            // that long visibly separates at the joints when hit hard.
            Physics2D.positionIterations = 16;
            Physics2D.velocityIterations = 14;

            Application.quitting += DisposeAcademy;
        }

        static void DisposeAcademy()
        {
            if (Academy.IsInitialized)
            {
                Academy.Instance.Dispose();
            }
        }
    }
}
