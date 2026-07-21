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
            Physics2D.positionIterations = 12;
            Physics2D.velocityIterations = 8;

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
