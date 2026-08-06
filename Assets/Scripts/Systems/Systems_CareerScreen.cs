using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace PoSumo
{
    /// The career screen: the banzuke ladder every fighter is climbing, plus the
    /// full record behind each of them.
    ///
    /// Replaces the collapsed four-column table that used to live inline in the
    /// bracket's scroll column. That table showed ELO / W-L / TITLES and nothing
    /// else, competed with the bracket for the same ~578pt content column, and was
    /// the thing that made the column overflow when expanded. This is a full-screen
    /// overlay instead, so it can afford rank, promotion progress, round record and
    /// head-to-head without costing the bracket a single point of width.
    ///
    /// **Built into the host document's root, NOT into a new `UIDocument`.** Three
    /// components in this project once each added their own at equal sorting order,
    /// which has no defined draw or pick order, and taps aimed at REMATCH were
    /// silently swallowed. One document per screen; layers inside it.
    ///
    /// LAYERING follows the rule in `Assets/UI Toolkit/README.md`: the scrim is
    /// full-bleed and the content layer carries the safe-area inset. An absolutely
    /// positioned child resolves its offsets against its parent's PADDING box, so a
    /// scrim built inside an already-inset parent stops at the notch and leaves an
    /// undimmed strip top and bottom. `Host` must therefore be unpadded, and
    /// `SafeAreaTarget` is handed to `Systems_SafeArea` by the caller.
    public sealed class Systems_CareerScreen
    {
        /// Fixed-width numeric columns, and they must stay fixed. The five columns
        /// of the old table were percentages adding to exactly 100, which overflowed
        /// the panel and pushed TITLES off the right edge — percentages resolve
        /// before padding and rounding is not on your side.
        private const int COL_ELO = 92;
        private const int COL_RECORD = 96;
        private const int COL_TITLES = 64;
        /// Rank column in the banzuke. Wide enough for MAEGASHIRA at FONT_SMALL.
        private const int COL_RANK = 168;
        private const int BAR_HEIGHT = 6;

        /// OPAQUE, and deliberately not `Systems_UiKit.Backdrop`.
        ///
        /// Backdrop is alpha 0.72 — correct for a small dialog floating over the
        /// match, where seeing the fight behind it is the point. This is not a
        /// dialog: it is a full screen of dense text, and at 0.72 the bracket read
        /// straight through it. A live capture showed the word CAREER interleaved
        /// with TOURNAMENT ("TOCAREERNT") and the banzuke rungs competing with the
        /// quarterfinal chips behind them. The fighter cards were readable only
        /// because their own Card background happens to be near-opaque; every gap
        /// between them leaked.
        ///
        /// Matches the bracket root's own background so the transition reads as
        /// changing screens rather than stacking one on another.
        private static readonly Color ScreenBackground = new Color(0.05f, 0.045f, 0.05f, 1f);

        private readonly VisualElement _scrim;
        private readonly VisualElement _content;

        /// The whole overlay, scrim included. Absolute over the host.
        public VisualElement Root { get; }

        /// The layer the caller must register with `Systems_SafeArea` — never the
        /// scrim, and never `Root`, which contains the scrim.
        public VisualElement SafeAreaTarget { get; }

        public bool IsOpen => Root.style.display == DisplayStyle.Flex;

        public Systems_CareerScreen(VisualElement host)
        {
            Root = new VisualElement().Fill();
            Root.style.display = DisplayStyle.None;
            host.Add(Root);

            // Still full-bleed and still the dismiss target — it is the opacity that
            // changed, not the role. It has to reach the physical screen edges, which
            // is why `host` must be unpadded and the safe-area inset sits on the
            // modal layer above instead.
            _scrim = new VisualElement().Fill();
            _scrim.style.backgroundColor = ScreenBackground;
            _scrim.RegisterCallback<ClickEvent>(_ => Hide());
            Root.Add(_scrim);

            // Sits ON TOP of the scrim as a sibling, so a tap that lands on the
            // card never reaches the dismiss handler underneath.
            var modal = new VisualElement().Fill();
            Root.Add(modal);
            SafeAreaTarget = modal;

            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.style.flexGrow = 1;
            // Same reason the bracket hides it: vertical mode alone leaves the
            // horizontal scroller on Auto, so a row a few points too wide draws a
            // bar across the bottom of the screen.
            scroll.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            modal.Add(scroll);

            _content = scroll.contentContainer;
            _content.style.paddingTop = Systems_UiKit.SPACE_4;
            _content.style.paddingLeft = Systems_UiKit.SPACE_3;
            _content.style.paddingRight = Systems_UiKit.SPACE_3;
            _content.style.paddingBottom = Systems_UiKit.SPACE_5;
        }

        public void Show()
        {
            Rebuild();
            Root.style.display = DisplayStyle.Flex;
            Root.FadeIn(Systems_UiKit.MOTION_FAST);
        }

        public void Hide() => Root.style.display = DisplayStyle.None;

        /// Rebuilt on every open rather than kept in sync. It is read-only over a
        /// record that only changes between visits, and a full rebuild costs one
        /// frame of layout on a screen the player has just chosen to stop and read.
        private void Rebuild()
        {
            _content.Clear();

            Label title = Systems_UiKit.Caption("CAREER", Systems_UiKit.FONT_HERO,
                                                Systems_UiKit.Gold, true);
            _content.Add(title);

            int played = Systems_CareerStats.MatchesPlayed;
            _content.Add(Systems_UiKit.Caption(
                played == 1 ? "1 MATCH ON RECORD" : $"{played} MATCHES ON RECORD",
                Systems_UiKit.FONT_SMALL, Systems_UiKit.TextLow));

            List<Systems_CareerStats.Record> records = Systems_CareerStats.Ranked();
            if (records.Count == 0)
            {
                Label empty = Systems_UiKit.Caption("no matches played yet",
                                                   Systems_UiKit.FONT_BODY, Systems_UiKit.TextLow);
                empty.style.marginTop = Systems_UiKit.SPACE_5;
                _content.Add(empty);
                AddCloseButton();
                return;
            }

            AddBanzuke(records);
            AddFighters(records);
            AddCloseButton();
        }

        // ---- The ladder ------------------------------------------------------

        /// Every rung, highest first, with whoever currently holds it.
        ///
        /// Empty rungs are drawn rather than skipped: the gap between where you are
        /// and YOKOZUNA is the whole point of showing a ladder, and a list of only
        /// the occupied ranks would hide exactly that.
        private void AddBanzuke(List<Systems_CareerStats.Record> records)
        {
            _content.Add(SectionHeader("THE BANZUKE"));

            var card = Systems_UiKit.Card(Systems_UiKit.Ink, Systems_UiKit.RADIUS_SM);
            card.Pad(Systems_UiKit.SPACE_3, Systems_UiKit.SPACE_2);
            _content.Add(card);

            var holders = new List<string>();
            for (int rungIndex = Systems_CareerLadder.RungCount - 1; rungIndex >= 0; rungIndex--)
            {
                holders.Clear();
                for (int recordIndex = 0; recordIndex < records.Count; recordIndex++)
                {
                    if (Systems_CareerLadder.IndexFor(records[recordIndex]) == rungIndex)
                    {
                        holders.Add(records[recordIndex].fighter.ToUpperInvariant());
                    }
                }

                bool occupied = holders.Count > 0;
                var row = Systems_UiKit.Row();
                row.style.marginTop = 3;

                Label rank = Systems_UiKit.Text(
                    Systems_CareerLadder.RungAt(rungIndex).Name,
                    Systems_UiKit.FONT_SMALL,
                    occupied ? Systems_UiKit.Gold : Systems_UiKit.TextLow,
                    occupied);
                rank.style.width = COL_RANK;
                rank.style.flexShrink = 0;
                row.Add(rank);

                Label who = Systems_UiKit.Text(
                    occupied ? string.Join("  ·  ", holders) : "—",
                    Systems_UiKit.FONT_SMALL,
                    occupied ? Systems_UiKit.TextHi : Systems_UiKit.TextLow);
                // The one flexible column: takes whatever the rank column leaves and
                // clips rather than pushing it off the row.
                who.style.flexGrow = 1;
                who.style.flexShrink = 1;
                who.style.overflow = Overflow.Hidden;
                row.Add(who);

                card.Add(row);
            }
        }

        // ---- Per-fighter detail ---------------------------------------------

        private void AddFighters(List<Systems_CareerStats.Record> records)
        {
            _content.Add(SectionHeader("FIGHTERS"));

            for (int recordIndex = 0; recordIndex < records.Count; recordIndex++)
            {
                _content.Add(FighterCard(records[recordIndex]));
            }
        }

        private static VisualElement FighterCard(Systems_CareerStats.Record record)
        {
            var card = Systems_UiKit.Card(Systems_UiKit.Ink, Systems_UiKit.RADIUS_SM);
            card.Pad(Systems_UiKit.SPACE_3, Systems_UiKit.SPACE_3);
            card.style.marginTop = Systems_UiKit.SPACE_2;

            // --- name + rank ---
            var head = Systems_UiKit.Row();
            Label name = Systems_UiKit.Text(record.fighter.ToUpperInvariant(),
                                            Systems_UiKit.FONT_BODY, Systems_UiKit.TextHi, true);
            name.style.flexGrow = 1;
            name.style.flexShrink = 1;
            name.style.overflow = Overflow.Hidden;
            head.Add(name);

            head.Add(Systems_UiKit.Text(Systems_CareerLadder.NameFor(record),
                                        Systems_UiKit.FONT_SMALL, Systems_UiKit.Gold, true));
            card.Add(head);

            // --- promotion progress ---
            card.Add(PromotionBlock(record));

            // --- the numbers ---
            var stats = Systems_UiKit.Row();
            stats.style.marginTop = Systems_UiKit.SPACE_2;
            stats.Add(StatCell("ELO", Mathf.RoundToInt(record.elo).ToString(),
                               COL_ELO, Systems_UiKit.Gold));
            stats.Add(StatCell("MATCHES", $"{record.matchWins}-{record.matchLosses}",
                               COL_RECORD, Systems_UiKit.TextHi));
            stats.Add(StatCell("ROUNDS", $"{record.roundWins}-{record.roundLosses}",
                               COL_RECORD, Systems_UiKit.TextHi));
            stats.Add(StatCell("TITLES",
                               record.titles > 0 ? new string('★', Mathf.Min(record.titles, 5)) : "—",
                               COL_TITLES, Systems_UiKit.Gold));
            card.Add(stats);

            // --- head-to-head ---
            // Parallel lists, because JsonUtility cannot serialize a Dictionary and
            // Systems_CareerStats stores them flattened for that reason.
            if (record.vsNames.Count > 0)
            {
                var line = new System.Text.StringBuilder();
                for (int opponentIndex = 0; opponentIndex < record.vsNames.Count; opponentIndex++)
                {
                    if (opponentIndex > 0)
                    {
                        line.Append("   ");
                    }
                    line.Append(record.vsNames[opponentIndex].ToUpperInvariant())
                        .Append(' ')
                        .Append(record.vsWins[opponentIndex])
                        .Append('-')
                        .Append(record.vsLosses[opponentIndex]);
                }
                Label h2h = Systems_UiKit.Text(line.ToString(), Systems_UiKit.FONT_MICRO,
                                               Systems_UiKit.TextLow);
                h2h.style.marginTop = Systems_UiKit.SPACE_2;
                h2h.style.whiteSpace = WhiteSpace.Normal;
                card.Add(h2h);
            }

            return card;
        }

        /// The climb, made concrete: a bar toward the next rung and a line saying
        /// exactly what is still missing.
        private static VisualElement PromotionBlock(Systems_CareerStats.Record record)
        {
            var block = new VisualElement();
            block.style.marginTop = Systems_UiKit.SPACE_2;

            float progress = Systems_CareerLadder.ProgressToNext(record, out string requirement);
            int rung = Systems_CareerLadder.IndexFor(record);
            bool topped = rung >= Systems_CareerLadder.RungCount - 1;

            var track = new VisualElement();
            track.style.height = BAR_HEIGHT;
            track.style.backgroundColor = Systems_UiKit.Track;
            track.Round(BAR_HEIGHT / 2);
            track.NoPick();

            var fill = new VisualElement();
            fill.style.height = BAR_HEIGHT;
            fill.style.width = new Length(progress * 100f, LengthUnit.Percent);
            fill.style.backgroundColor = topped ? Systems_UiKit.Gold : Systems_UiKit.Good;
            fill.Round(BAR_HEIGHT / 2);
            track.Add(fill);
            block.Add(track);

            string caption;
            if (topped)
            {
                caption = "TOP OF THE BANZUKE";
            }
            else if (requirement != null)
            {
                // A title gate outranks the rating gap in the caption: a fighter
                // rated well past the floor but short of a tournament win would
                // otherwise be told they need 0 more Elo and never promote.
                caption = requirement;
            }
            else
            {
                int gap = Systems_CareerLadder.EloToNext(record);
                string next = Systems_CareerLadder.RungAt(rung + 1).Name;
                caption = gap > 0 ? $"{gap} ELO TO {next}" : $"PROMOTED TO {next} NEXT WIN";
            }

            Label label = Systems_UiKit.Text(caption, Systems_UiKit.FONT_MICRO,
                                             topped ? Systems_UiKit.Gold : Systems_UiKit.TextLow);
            label.style.marginTop = Systems_UiKit.SPACE_1;
            block.Add(label);

            return block;
        }

        // ---- Small parts -----------------------------------------------------

        private static Label SectionHeader(string text)
        {
            Label header = Systems_UiKit.Text(text, Systems_UiKit.FONT_SMALL,
                                              Systems_UiKit.TextLow, true);
            header.style.marginTop = Systems_UiKit.SPACE_5;
            header.style.marginBottom = Systems_UiKit.SPACE_2;
            return header;
        }

        /// A fixed-width caption-over-value pair. Column, not Row — and that is the
        /// trap the bracket's palette hit: a child built for a row and dropped into
        /// a default (column) parent collapses to zero on the cross axis with no
        /// error at all.
        private static VisualElement StatCell(string caption, string value, int width, Color colour)
        {
            var cell = new VisualElement();
            cell.style.width = width;
            cell.style.flexShrink = 0;
            cell.Add(Systems_UiKit.Text(caption, Systems_UiKit.FONT_MICRO, Systems_UiKit.TextLow));
            cell.Add(Systems_UiKit.Text(value, Systems_UiKit.FONT_BODY, colour, true));
            return cell;
        }

        private void AddCloseButton()
        {
            Button close = Systems_UiKit.GhostButton("CLOSE", Hide);
            close.style.marginTop = Systems_UiKit.SPACE_5;
            _content.Add(close);
        }
    }
}
