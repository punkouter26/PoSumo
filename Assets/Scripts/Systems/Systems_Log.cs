using System.Diagnostics;

namespace PoSumo
{
    /// Informational match instrumentation — `[MATCH]`, `[ROUND]`, `[DAMAGE]`,
    /// `WALK-IN RESULT:`, `[TOURNAMENT]`, `TELEMETRY RESULT:`.
    ///
    /// These lines are not debug leftovers: `MatchTestHarness` and the whole
    /// verification flow in `CLAUDE.md` read them back out of the console, which is
    /// why they cannot simply be deleted to satisfy the "no Debug.Log in
    /// production" rule in `.claude/rules/performance.md`. They are gated instead.
    ///
    /// **`[Conditional]` strips the CALL SITE, arguments included.** That is the
    /// whole point of routing through a method rather than wrapping each call in
    /// `#if`: every one of these lines is an interpolated string, and an `if`
    /// guard would still build the string before discarding it. Under
    /// `[Conditional]` the interpolation is never compiled into a release player at
    /// all — no allocation, no formatting, no call.
    ///
    /// Live in the Editor and in development players (which is what
    /// `BuildTrainingEnv` produces, so a headless training env still logs);
    /// compiled out of the release Android build, where nobody reads logcat and
    /// the string building would be pure waste.
    ///
    /// Warnings and errors are deliberately NOT routed through here — a fault
    /// still needs to be visible in a shipped build. Call `Debug.LogWarning` and
    /// `Debug.LogError` directly.
    internal static class Systems_Log
    {
        [Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD")]
        internal static void Info(string message)
        {
            // Fully qualified: `using System.Diagnostics` above makes a bare
            // `Debug` ambiguous with System.Diagnostics.Debug.
            UnityEngine.Debug.Log(message);
        }
    }
}
