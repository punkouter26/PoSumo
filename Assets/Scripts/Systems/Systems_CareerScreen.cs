using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace PoSumo
{
    /// The career view: the banzuke ladder every fighter is climbing, plus the
    /// full record behind each of them — built DIRECTLY into the bracket's
    /// RECORD tab.
    ///
    /// It was a full-screen overlay with its own ScrollView. The zero-scroll
    /// consolidation merged it into the bracket as a tab pane (checklist #1/#3):
    /// no scrim, no navigation step, no scroller — the pane fits ONE viewport by
    /// showing a single sub-view at a time (BANZUKE / FIGHTERS, segmented control
    /// on top) and the fighters list is an ACCORDION with at most one open card,
    /// so five full record cards can never stack past the panel edge. Every
    /// number the overlay showed is still here: rank, promotion progress, round
    /// record, titles, head-to-head.
    ///
    /// Still built into the host document's tree, NOT a new `UIDocument` — three
    /// components in this project once each added their own at equal sorting
    /// order, which has no defined draw or pick order, and taps aimed at REMATCH
    /// were silently swallowed. One document per screen; panes inside it.
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

        /// Which sub-view is showing: 0 = the banzuke, 1 = the fighter cards.
        private int _viewIndex;
        /// Behavior name of the one open accordion row, or null for none.
        private string _expandedFighter;
        private readonly List<Button> _segmentButtons = new List<Button>();
        private readonly VisualElement _view;

        public Systems_CareerScreen(VisualElement host)
        {
            // Segmented control: the merged screen's step selector. ChipButton so
            // the press feedback comes with it — a hand-rolled chip is visually
            // dead on press because StyleButton writes backgroundColor inline.
            VisualElement segment = Systems_UiKit.Row();
            segment.style.paddingLeft = Systems_UiKit.SPACE_1;
            segment.style.paddingRight = Systems_UiKit.SPACE_1;
            segment.style.marginBottom = Systems_UiKit.SPACE_2;
            string[] labels = { "BANZUKE", "FIGHTERS" };
            for (int viewIndex = 0; viewIndex < labels.Length; viewIndex++)
            {
                int captured = viewIndex;
                Button button = Systems_UiKit.ChipButton(
                    labels[viewIndex], () => SelectView(captured), 0);
                button.style.flexGrow = 1;
                button.style.flexBasis = 0;
                button.style.marginLeft = Systems_UiKit.SPACE_1;
                button.style.marginRight = Systems_UiKit.SPACE_1;
                _segmentButtons.Add(button);
                segment.Add(button);
            }
            host.Add(segment);

            _view = Systems_UiKit.Column();
            // The pane's LockChildren pass sets flexShrink 0 on its children; this
            // one must never be squashed silently, and the overflow audit is the
            // check that it is not.
            _view.style.flexShrink = 0;
            host.Add(_view);

            SelectView(0);
        }

        /// Switches sub-view and repaints the selection state. Retained elements:
        /// the segment buttons are styled, never rebuilt.
        private void SelectView(int viewIndex)
        {
            _viewIndex = viewIndex;
            for (int buttonIndex = 0; buttonIndex < _segmentButtons.Count; buttonIndex++)
            {
                bool selected = buttonIndex == viewIndex;
                Button button = _segmentButtons[buttonIndex];
                button.style.color = selected ? Systems_UiKit.Gold : Systems_UiKit.TextHi;
                button.style.borderBottomWidth = selected ? 3 : 0;
                button.style.borderBottomColor = Systems_UiKit.Gold;
            }
            Rebuild();
        }

        /// Rebuilt on demand rather than kept in sync: it is read-only over a
        /// record that only changes between visits (the bracket's Refresh calls
        /// this), and a full rebuild costs one frame of layout. The accordion
        /// state lives in `_expandedFighter`, so a rebuild preserves which
        /// fighter is open.
        public void Rebuild()
        {
            _view.Clear();

            List<Systems_CareerStats.Record> records = Systems_CareerStats.Ranked();
            if (records.Count == 0)
            {
                _view.Add(Systems_UiKit.Caption("no matches played yet",
                                                Systems_UiKit.FONT_BODY, Systems_UiKit.TextLow));
                return;
            }

            if (_viewIndex == 0)
            {
                AddBanzuke(records);
            }
            else
            {
                AddFighters(records);
            }
        }

        // ---- The ladder ------------------------------------------------------

        /// Every rung, highest first, with whoever currently holds it.
        ///
        /// Empty rungs are drawn rather than skipped: the gap between where you are
        /// and YOKOZUNA is the whole point of showing a ladder, and a list of only
        /// the occupied ranks would hide exactly that.
        private void AddBanzuke(List<Systems_CareerStats.Record> records)
        {
            var card = Systems_UiKit.Card(Systems_UiKit.Ink, Systems_UiKit.RADIUS_SM);
            card.Pad(Systems_UiKit.SPACE_3, Systems_UiKit.SPACE_2);
            _view.Add(card);

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

        // ---- Per-fighter detail (accordion) ----------------------------------

        /// One row per fighter; tapping it expands the full card in place.
        ///
        /// The overlay used to lay all five full cards out at once — ~750pt of
        /// record, which is why it needed a ScrollView and why it cannot be a
        /// zero-scroll pane in that form. Collapsed, a fighter is one 40pt row
        /// (name, rank, Elo, disclosure); expanded, it is the same card as
        /// before — promotion bar, the four stat cells, head-to-head. At most one
        /// is open (tapping the open row closes it), so the worst case is four
        /// rows plus one card ~350pt, which fits the pane at 4:3.
        private void AddFighters(List<Systems_CareerStats.Record> records)
        {
            for (int recordIndex = 0; recordIndex < records.Count; recordIndex++)
            {
                Systems_CareerStats.Record record = records[recordIndex];
                string fighterName = record.fighter;
                bool expanded = fighterName == _expandedFighter;

                var block = Systems_UiKit.Column();
                block.style.marginBottom = Systems_UiKit.SPACE_1;

                // The disclosure row. Pickable by default — it IS the control —
                // and the label children bubble their clicks up to it.
                var head = Systems_UiKit.Row();
                head.style.paddingLeft = Systems_UiKit.SPACE_2;
                head.style.paddingRight = Systems_UiKit.SPACE_2;
                head.style.paddingTop = 6;
                head.style.paddingBottom = 6;
                head.style.backgroundColor = Systems_UiKit.Ink;
                head.Round(Systems_UiKit.RADIUS_SM);
                head.RegisterCallback<ClickEvent>(_ =>
                {
                    _expandedFighter = expanded ? null : fighterName;
                    Rebuild();
                });

                Label name = Systems_UiKit.Text(fighterName.ToUpperInvariant(),
                                                 Systems_UiKit.FONT_BODY,
                                                 Systems_UiKit.TextHi, true);
                name.style.flexGrow = 1;
                name.style.flexShrink = 1;
                name.style.overflow = Overflow.Hidden;
                head.Add(name);

                head.Add(Systems_UiKit.Text(Systems_CareerLadder.NameFor(record),
                                            Systems_UiKit.FONT_SMALL, Systems_UiKit.Gold, true));
                Label elo = Systems_UiKit.Text(Mathf.RoundToInt(record.elo).ToString(),
                                               Systems_UiKit.FONT_SMALL, Systems_UiKit.TextMid);
                elo.style.marginLeft = Systems_UiKit.SPACE_2;
                head.Add(elo);

                // Disclosure mark: plain ASCII, drawn as text — no font asset
                // ships, so a chevron glyph could render as a box.
                head.Add(Systems_UiKit.Text(expanded ? "^" : "v",
                                            Systems_UiKit.FONT_MICRO, Systems_UiKit.TextLow));
                block.Add(head);

                if (expanded)
                {
                    block.Add(FighterCardBody(record));
                }
                _view.Add(block);
            }
        }

        /// The full record, shown under its open accordion row.
        private static VisualElement FighterCardBody(Systems_CareerStats.Record record)
        {
            var card = Systems_UiKit.Card(Systems_UiKit.Ink, Systems_UiKit.RADIUS_SM);
            card.Pad(Systems_UiKit.SPACE_3, Systems_UiKit.SPACE_2);
            card.style.marginTop = Systems_UiKit.SPACE_1;

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
    }
}
