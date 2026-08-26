using UnityEngine;
using UnityEngine.UIElements;

namespace PoSumo
{
    /// The banzuke promotion moment: a full-screen ceremony when a fighter climbs
    /// (or falls) a rung after a bracket match.
    ///
    /// Replaces a single line of text. The rank machinery was already complete —
    /// `Systems_CareerLadder` computes the rung, `Systems_CareerRecorder` captures
    /// the change into a consume-once static — and the reward was landing as
    /// `"MATT PROMOTED TO SEKIWAKE"` in a small label. Measured promotion pacing is
    /// matches 1, 4, 7, 11, 17 and 26 against equal opposition, so this fires often
    /// enough to matter and rarely enough to stay special.
    ///
    /// **Built into the host document's root, NOT a new `UIDocument`** — the same
    /// rule `Systems_CareerScreen` follows, and for the same reason: two documents
    /// at equal sorting order have no defined draw or pick order, and taps get
    /// swallowed. It is a plain C# class, not a MonoBehaviour, because it owns no
    /// lifetime of its own; the bracket constructs it and drives it.
    public sealed class Systems_PromotionCeremony
    {
        /// Full-bleed dim behind the ceremony. Heavier than the career screen's,
        /// because this is a moment rather than a browsable table.
        private static readonly Color Backdrop = new Color(0f, 0f, 0f, 0.86f);

        private readonly VisualElement _scrim;
        private readonly VisualElement _card;
        private readonly Label _kicker;
        private readonly Label _fighter;
        private readonly Label _fromRank;
        private readonly Label _arrow;
        private readonly Label _toRank;
        private readonly Label _footnote;

        public VisualElement Root { get; }

        /// The safe-area inset belongs on this layer, not on the scrim — an
        /// absolutely positioned child resolves its offsets against its parent's
        /// PADDING box, so insetting the scrim leaves undimmed strips at the notch.
        public VisualElement SafeAreaTarget { get; }

        public bool IsOpen => Root.style.display == DisplayStyle.Flex;

        public Systems_PromotionCeremony(VisualElement host)
        {
            Root = new VisualElement().Fill();
            Root.style.display = DisplayStyle.None;
            host.Add(Root);

            _scrim = new VisualElement().Fill();
            _scrim.style.backgroundColor = Backdrop;
            _scrim.RegisterCallback<ClickEvent>(_ => Hide());
            Root.Add(_scrim);

            var modal = new VisualElement().Fill();
            modal.style.justifyContent = Justify.Center;
            modal.style.alignItems = Align.Center;
            Root.Add(modal);
            SafeAreaTarget = modal;

            _card = Systems_UiKit.Card(Systems_UiKit.Surface, Systems_UiKit.RADIUS_LG);
            _card.style.paddingTop = Systems_UiKit.SPACE_5;
            _card.style.paddingBottom = Systems_UiKit.SPACE_5;
            _card.style.paddingLeft = Systems_UiKit.SPACE_4;
            _card.style.paddingRight = Systems_UiKit.SPACE_4;
            _card.style.alignItems = Align.Center;
            // Elastic rather than fixed: the usable content column is ~578pt, not
            // the ~696 a 720pt reference panel implies, and a fixed width here is
            // exactly what pushed the bracket's winner chip off its card.
            _card.style.width = Length.Percent(88f);
            _card.style.maxWidth = 520;
            modal.Add(_card);

            _kicker = Systems_UiKit.Caption("", Systems_UiKit.FONT_SMALL,
                                            Systems_UiKit.TextLow, true);
            _kicker.style.unityTextAlign = TextAnchor.MiddleCenter;
            _card.Add(_kicker);

            _fighter = Systems_UiKit.Text("", Systems_UiKit.FONT_TITLE,
                                          Systems_UiKit.TextHi, true);
            _fighter.style.unityTextAlign = TextAnchor.MiddleCenter;
            _fighter.style.marginTop = Systems_UiKit.SPACE_1;
            _card.Add(_fighter);

            _card.Add(Systems_UiKit.Divider(Systems_UiKit.SPACE_3));

            // from → to, on one row.
            VisualElement rankRow = Systems_UiKit.Row();
            rankRow.style.justifyContent = Justify.Center;

            _fromRank = Systems_UiKit.Text("", Systems_UiKit.FONT_BODY,
                                           Systems_UiKit.TextLow);
            _arrow = Systems_UiKit.Text("  →  ", Systems_UiKit.FONT_BODY,
                                        Systems_UiKit.TextLow);
            _toRank = Systems_UiKit.Text("", Systems_UiKit.FONT_HERO,
                                         Systems_UiKit.Gold, true);
            _toRank.style.textShadow = Systems_UiKit.Outline;

            rankRow.Add(_fromRank);
            rankRow.Add(_arrow);
            rankRow.Add(_toRank);
            _card.Add(rankRow);

            _footnote = Systems_UiKit.Caption("", Systems_UiKit.FONT_MICRO,
                                              Systems_UiKit.TextLow);
            _footnote.style.unityTextAlign = TextAnchor.MiddleCenter;
            _footnote.style.marginTop = Systems_UiKit.SPACE_3;
            _card.Add(_footnote);

            Button close = Systems_UiKit.PrimaryButton("CONTINUE", Hide);
            close.style.marginTop = Systems_UiKit.SPACE_4;
            _card.Add(close);
        }

        /// Shows the ceremony for one recorded rank change.
        ///
        /// `fromRank` is passed in rather than recomputed: `Systems_CareerStats.Get`
        /// hands back the LIVE record out of its list, so by the time this runs the
        /// "before" state no longer exists anywhere to be read. The recorder is the
        /// only thing that saw it.
        public void Show(Systems_CareerRecorder.RankChange change, string fromRank)
        {
            bool up = change.Promoted;

            _kicker.text = up ? "BANZUKE PROMOTION" : "BANZUKE DEMOTION";
            _kicker.style.color = up ? Systems_UiKit.Gold : Systems_UiKit.Bad;

            _fighter.text = change.Fighter.ToUpperInvariant();

            _fromRank.text = string.IsNullOrEmpty(fromRank) ? "UNRANKED" : fromRank;
            _toRank.text = change.ToRank;
            _toRank.style.color = up ? Systems_UiKit.Gold : Systems_UiKit.Bad;
            _arrow.text = up ? "  →  " : "  ↓  ";

            // A streak or upset bonus is the headline of a promotion when there
            // was one: it is the thing the player did, not a rule about the rung.
            _footnote.text = up && !string.IsNullOrEmpty(change.Note)
                ? change.Note + "\n" + FootnoteFor(change.ToRank, up)
                : FootnoteFor(change.ToRank, up);

            Root.style.display = DisplayStyle.Flex;
            Root.FadeIn();
            _card.FadeIn();
        }

        public void Hide() => Root.style.display = DisplayStyle.None;

        /// A line of context for the rung reached. The top two are gated on TITLES
        /// as well as rating — winning a tournament is the only route to Ozeki, as
        /// in the sport — and that is worth saying out loud at the moment it lands,
        /// because it is the one promotion rule a player cannot infer from Elo.
        private static string FootnoteFor(string rank, bool promoted)
        {
            if (!promoted) return "Lose ground and the banzuke moves against you.";

            switch (rank)
            {
                case "YOKOZUNA":
                    return "Grand champion. The highest rank in sumo — two titles and the rating to hold it.";
                case "OZEKI":
                    return "Champion. Reached only by winning a tournament, never by rating alone.";
                case "SEKIWAKE":
                case "KOMUSUBI":
                    return "A titled rank. Ozeki is gated on winning a tournament outright.";
                case "MAEGASHIRA":
                case "JURYO":
                    return "Salaried ranks. The banzuke is published before every tournament.";
                default:
                    return "The banzuke is redrawn after every tournament.";
            }
        }
    }
}
