using System;
using System.Text;
using UnityEngine;

namespace PoSumo.EditorTools
{
    /// Counts console output during a harness run so the result line can carry
    /// the tally with it.
    ///
    /// Why: the harnesses' verdicts were previously independent of the console.
    /// A bracket could PASS its assertions while the console filled with errors,
    /// because nothing asserted on the console at all — the exact gap the
    /// 2026-09-06 stale-brain incident exploited (all four models rejected
    /// silently, bracket still ran to completion, `Bot_v01` won). Every harness
    /// result now ends with `console: errors=N warnings=N layout=N`.
    ///
    /// Gating: errors gate (a healthy run prints none — legit faults like the
    /// `[OBS] STALE BRAIN` line are Error-level on purpose); warnings and
    /// UI-Toolkit layout warnings report but do not gate, because the font
    /// teardown on Play exit and partial face-sprite sets produce benign ones.
    ///
    /// Counts EVERYTHING logged while active, including the harnesses' own
    /// failure lines — a FAIL verdict's LogError is part of the run it failed.
    /// Attach after the pre-flight checks, detach in Finish/Fail.
    public static class ConsoleSentinel
    {
        /// Cap on captured error samples so a pathological run cannot build an
        /// unbounded report string.
        private const int MAX_SAMPLE_CHARS = 4000;

        private static bool _active;
        private static int _errors;
        private static int _warnings;
        private static int _layout;
        private static readonly StringBuilder Samples = new StringBuilder();

        public static int Errors => _errors;
        public static int Warnings => _warnings;
        public static int LayoutWarnings => _layout;

        /// Starts counting. Detaches any instance left over from a previous run
        /// first — a harness that exited Play mode without calling Stop() would
        /// otherwise leave the handler attached and double-count the next run.
        public static void Start()
        {
            Stop();
            _errors = 0;
            _warnings = 0;
            _layout = 0;
            Samples.Clear();
            Application.logMessageReceived += OnMessage;
            _active = true;
        }

        public static void Stop()
        {
            if (_active)
            {
                Application.logMessageReceived -= OnMessage;
                _active = false;
            }
        }

        /// The tally line appended to a harness result, plus up to ten error
        /// samples when anything errored.
        public static string Tally()
        {
            var sb = new StringBuilder();
            sb.Append("console: errors=").Append(_errors)
              .Append(" warnings=").Append(_warnings)
              .Append(" layout=").Append(_layout);
            if (_errors > 0 && Samples.Length > 0)
            {
                sb.Append('\n').Append(Samples);
            }
            return sb.ToString();
        }

        private static void OnMessage(string condition, string stackTrace, LogType type)
        {
            switch (type)
            {
                case LogType.Assert:
                case LogType.Exception:
                case LogType.Error:
                    _errors++;
                    if (Samples.Length < MAX_SAMPLE_CHARS)
                    {
                        string firstLine = condition.Split('\n')[0];
                        Samples.AppendLine("  ERROR: " + firstLine);
                    }
                    break;
                case LogType.Warning:
                    _warnings++;
                    if (IsLayoutMessage(condition))
                    {
                        _layout++;
                    }
                    break;
                case LogType.Log:
                    if (IsLayoutMessage(condition))
                    {
                        _layout++;
                    }
                    break;
            }
        }

        /// UI Toolkit's layout engine warns with a small, stable vocabulary.
        /// Substring match rather than exact strings so new phrasings still count.
        private static bool IsLayoutMessage(string message)
        {
            return message.IndexOf("layout update", StringComparison.OrdinalIgnoreCase) >= 0
                || message.IndexOf("uitoolkit", StringComparison.OrdinalIgnoreCase) >= 0
                || message.IndexOf("ui toolkit", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
