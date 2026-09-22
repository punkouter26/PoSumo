using System.Text;
using UnityEngine;
using UnityEngine.UIElements;

namespace PoSumo.EditorTools
{
    /// The overflow audit over every UIDocument's live visual tree — the C# twin
    /// of the walk in Tools/portrait_check.py.
    ///
    /// Why a second copy: the python tool audits at chosen Game-view sizes across
    /// the bracket and a live round, but it cannot reach the states that are hard
    /// to schedule from outside — the result card while a decided match is on
    /// screen, the pause card, a modal open during slow motion. The flow harness
    /// is already standing in exactly those states for its own assertions, so it
    /// audits from there with the same rule.
    ///
    /// The rule itself is unchanged and worth restating: `flex-shrink` defaults
    /// to 1, so a column taller than its parent is silently COMPRESSED rather
    /// than clipped or scrolled — no error, no warning, and the only visible
    /// symptom is two unrelated things overlapping further down the screen.
    /// ScrollView frames and their content containers are exempt (holding more
    /// than they show is their job); everything inside them is still walked.
    public static class HudOverflowAudit
    {
        /// Slack below which sub-pixel rounding is not a finding.
        private const float TOLERANCE_POINTS = 1.5f;

        public static string Run()
        {
            // The parameterless overload — the FindObjectsSortMode one is
            // deprecated in this Unity version and warning-noise here.
            UIDocument[] docs = Object.FindObjectsByType<UIDocument>();
            var report = new StringBuilder();
            int flagged = 0;
            for (int docIndex = 0; docIndex < docs.Length; docIndex++)
            {
                UIDocument doc = docs[docIndex];
                if (doc == null || doc.rootVisualElement == null)
                {
                    continue;
                }
                flagged += Walk(doc.rootVisualElement, doc.gameObject.name, report);
            }
            report.Insert(0, $"documents={docs.Length} overflowing={flagged}\n");
            return report.ToString();
        }

        private static int Walk(VisualElement element, string path, StringBuilder report)
        {
            if (element.resolvedStyle.display == DisplayStyle.None)
            {
                return 0;
            }

            int flagged = 0;
            bool scroller = element is ScrollView
                || (element.parent != null && element.parent is ScrollView)
                || element.ClassListContains("unity-scroll-view__content-container")
                || element.ClassListContains("unity-scroll-view__content-viewport");

            if (!scroller && element.childCount > 0 && element.resolvedStyle.height > 1f)
            {
                float lowest = 0f;
                float furthest = 0f;
                for (int childIndex = 0; childIndex < element.childCount; childIndex++)
                {
                    VisualElement child = element[childIndex];
                    if (child.resolvedStyle.display == DisplayStyle.None)
                    {
                        continue;
                    }
                    if (child.resolvedStyle.position == Position.Absolute)
                    {
                        continue;
                    }
                    float bottom = child.resolvedStyle.top + child.resolvedStyle.height;
                    if (bottom > lowest)
                    {
                        lowest = bottom;
                    }
                    float right = child.resolvedStyle.left + child.resolvedStyle.width;
                    if (right > furthest)
                    {
                        furthest = right;
                    }
                }

                if (lowest > element.resolvedStyle.height + TOLERANCE_POINTS)
                {
                    flagged++;
                    report.Append("  OVERFLOW-V ").Append(path).Append(' ')
                          .Append(element.GetType().Name)
                          .Append(" height=").Append(element.resolvedStyle.height.ToString("F0"))
                          .Append(" content=").Append(lowest.ToString("F0"))
                          .Append(" over=").Append((lowest - element.resolvedStyle.height).ToString("F0"))
                          .Append(" children=").Append(element.childCount)
                          .Append(" flexShrink=").Append(element.resolvedStyle.flexShrink.ToString("F0"))
                          .Append('\n');
                }
                if (furthest > element.resolvedStyle.width + TOLERANCE_POINTS)
                {
                    flagged++;
                    report.Append("  OVERFLOW-H ").Append(path).Append(' ')
                          .Append(element.GetType().Name)
                          .Append(" width=").Append(element.resolvedStyle.width.ToString("F0"))
                          .Append(" content=").Append(furthest.ToString("F0"))
                          .Append(" over=").Append((furthest - element.resolvedStyle.width).ToString("F0"))
                          .Append(" children=").Append(element.childCount)
                          .Append('\n');
                }
            }

            for (int childIndex = 0; childIndex < element.childCount; childIndex++)
            {
                flagged += Walk(element[childIndex], path + "/" + childIndex, report);
            }
            return flagged;
        }
    }
}
