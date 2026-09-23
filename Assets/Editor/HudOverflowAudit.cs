using System.Text;
using UnityEngine;
using UnityEngine.UIElements;

namespace PoSumo.EditorTools
{
    /// The overflow audit over every UIDocument's live visual tree — the C# twin
    /// of the walk in Tools/portrait_check.py — PLUS the five-corners contract.
    ///
    /// Why a second copy of the walk: the python tool audits at chosen Game-view
    /// sizes across the bracket and a live round, but it cannot reach the states
    /// that are hard to schedule from outside — the result card while a decided
    /// match is on screen, the pause card, a modal open during slow motion. The
    /// flow harness is already standing in exactly those states for its own
    /// assertions, so it audits from there with the same rule.
    ///
    /// The walk's rule is worth restating: `flex-shrink` defaults to 1, so a
    /// column taller than its parent is silently COMPRESSED rather than clipped
    /// or scrolled — no error, no warning, and the only visible symptom is two
    /// unrelated things overlapping further down the screen. ScrollView frames
    /// and their content containers are exempt (holding more than they show is
    /// their job); everything inside them is still walked. Since the zero-scroll
    /// consolidation removed every ScrollView the player can reach, the Flow
    /// harness additionally asserts ZERO visible ScrollViews (see its Audit).
    ///
    /// The CORNER findings enforce checklist #4 — TL Title | TC FPS | TR Menu |
    /// BL Debug | BR Version — as a PASS/FAIL check rather than a convention:
    /// Systems_ScreenChrome names its layer and its five children, and this
    /// asserts they exist together, are exactly five (nothing else may draw into
    /// a corner), and sit in the right quadrants of the layer. A screen with no
    /// named chrome layer (enableScreenChrome off) is skipped — there is no
    /// contract to violate when chrome is deliberately absent.
    public static class HudOverflowAudit
    {
        /// Slack below which sub-pixel rounding is not a finding.
        private const float TOLERANCE_POINTS = 1.5f;

        /// The five corner elements, in TL/TC/TR/BL/BR order.
        private static readonly string[] CORNER_NAMES =
            { "ChromeTitle", "ChromeFps", "MenuButton", "DebugToggle", "ChromeVersion" };

        /// Corner index per element: 0 title, 1 fps, 2 menu, 3 debug, 4 version.
        private static readonly string[] LAYER_NAME = { "ScreenChromeLayer" };

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
                flagged += Corners(doc, report);
            }
            report.Insert(0, $"documents={docs.Length} overflowing={flagged}\n");
            return report.ToString();
        }

        // ---- The five fixed corners -----------------------------------------

        private static int Corners(UIDocument doc, StringBuilder report)
        {
            VisualElement root = doc.rootVisualElement;
            int layerCount = 0;
            VisualElement layer = FindByName(root, LAYER_NAME[0], ref layerCount);
            if (layer == null)
            {
                // Chrome deliberately off — no contract to violate.
                return 0;
            }

            int flagged = 0;
            string where = doc.gameObject.name;

            if (layerCount > 1)
            {
                flagged++;
                report.Append("  CORNER-LAYER ").Append(where)
                      .Append(" appears ").Append(layerCount)
                      .Append(" times (one chrome layer per screen)\n");
            }
            if (layer.childCount != CORNER_NAMES.Length)
            {
                flagged++;
                report.Append("  CORNER-LAYER ").Append(where)
                      .Append(" children=").Append(layer.childCount)
                      .Append(" expected=").Append(CORNER_NAMES.Length)
                      .Append(" (nothing else may draw into a corner)\n");
            }

            Rect bounds = layer.worldBound;
            if (bounds.width < 1f || bounds.height < 1f)
            {
                return flagged;
            }
            float midX = bounds.xMin + bounds.width * 0.5f;
            float midY = bounds.yMin + bounds.height * 0.5f;

            for (int nameIndex = 0; nameIndex < CORNER_NAMES.Length; nameIndex++)
            {
                int found = 0;
                VisualElement element = FindByName(layer, CORNER_NAMES[nameIndex], ref found);
                if (element == null)
                {
                    flagged++;
                    report.Append("  CORNER-MISSING ").Append(where)
                          .Append(' ').Append(CORNER_NAMES[nameIndex]).Append('\n');
                    continue;
                }

                Rect rect = element.worldBound;
                float centerX = rect.xMin + rect.width * 0.5f;
                float centerY = rect.yMin + rect.height * 0.5f;
                bool left = centerX < midX;
                bool right = centerX > midX;
                bool top = centerY < midY;
                bool bottom = centerY > midY;

                // The expected quadrant per corner, and the fps special case
                // (top band, horizontally centred by design).
                bool ok = nameIndex switch
                {
                    0 => left && top,                     // TL title
                    1 => top && Mathf.Abs(centerX - midX) < bounds.width * 0.2f, // TC fps
                    2 => right && top,                    // TR menu
                    3 => left && bottom,                  // BL debug
                    _ => right && bottom,                 // BR version
                };
                if (!ok)
                {
                    flagged++;
                    report.Append("  CORNER-ANCHOR ").Append(where)
                          .Append(' ').Append(CORNER_NAMES[nameIndex])
                          .Append(" centre=(").Append(centerX.ToString("F0"))
                          .Append(',').Append(centerY.ToString("F0"))
                          .Append(") layer=").Append(bounds.ToString())
                          .Append('\n');
                }
            }
            return flagged;
        }

        private static VisualElement FindByName(VisualElement element, string name, ref int count)
        {
            if (element == null)
            {
                return null;
            }
            if (element.name == name)
            {
                count++;
                // Keep walking: a duplicate is a finding counted by the caller's
                // layer count, but every match must be tallied.
            }
            VisualElement first = element.name == name ? element : null;
            for (int childIndex = 0; childIndex < element.childCount; childIndex++)
            {
                VisualElement hit = FindByName(element[childIndex], name, ref count);
                if (first == null && hit != null)
                {
                    first = hit;
                }
            }
            return first;
        }

        // ---- The walk -------------------------------------------------------

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
