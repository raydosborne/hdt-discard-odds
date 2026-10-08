using System;
using System.Collections.Generic;
using System.Linq;

namespace DiscardOdds
{
	/// <summary>Where a line may appear: on the main widget, only with Show details, or never on the widget (log only).</summary>
	public enum WidgetPlacement { Main, DetailsOnly, Never }

	/// <summary>
	/// Single place that decides which cards may appear on the overlay (no HDT/WPF types, so it is unit-tested).
	/// Every widget code path (odds rows, Platysaur row, the widget's own last-line guard) asks this class.
	/// </summary>
	public static class WidgetPolicy
	{
		/// <summary>
		/// Never on the widget, in any mode, not even with Show details: cards where you pick the discard yourself
		/// (Ocular Occultist, Gemstone Hoarder), and Hand of Gul'dan (removed completely in v0.1.5).
		/// </summary>
		public static readonly HashSet<string> NeverIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
		{
			"CATA_490", // Ocular Occultist
			"CATA_897", // Gemstone Hoarder
			"BT_300",   // Hand of Gul'dan
			"Story_09_HandofGuldan",
		};

		public static readonly HashSet<string> NeverNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
		{
			"Ocular Occultist", "Gemstone Hoarder", "Hand of Gul'dan",
		};

		/// <summary>Cards shown only with Show details. Empty since v0.1.5 (Hand of Gul'dan moved to Never).</summary>
		public static readonly HashSet<string> DetailsOnlyIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		public static readonly HashSet<string> DetailsOnlyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		/// <summary>Strips a trailing " (…)" label such as "Hand of Gul'dan (discard)".</summary>
		private static string BaseName(string name)
		{
			if(string.IsNullOrWhiteSpace(name)) return null;
			var n = name.Trim();
			var i = n.IndexOf(" (", StringComparison.Ordinal);
			return i > 0 ? n.Substring(0, i) : n;
		}

		public static bool IsNever(string cardId, string name) =>
			(cardId != null && NeverIds.Contains(cardId)) || (BaseName(name) is string b && NeverNames.Contains(b));

		public static bool IsDetailsOnly(string cardId, string name) =>
			(cardId != null && DetailsOnlyIds.Contains(cardId)) || (BaseName(name) is string b && DetailsOnlyNames.Contains(b));

		/// <summary>Placement for one card id / display name.</summary>
		public static WidgetPlacement Place(string cardId, string name)
		{
			if(IsNever(cardId, name)) return WidgetPlacement.Never;
			if(IsDetailsOnly(cardId, name)) return WidgetPlacement.DetailsOnly;
			return WidgetPlacement.Main;
		}

		/// <summary>Placement for an odds line: any "you choose the discard" card is never shown.</summary>
		public static WidgetPlacement Place(CardOdds o)
		{
			if(o == null) return WidgetPlacement.Never;
			if(o.ByChoice || o.Kind == OddsKind.DiscardChoose) return WidgetPlacement.Never;
			return Place(o.CardId, o.Name);
		}

		/// <summary>True if a line with this placement is drawn given the Show details setting.</summary>
		public static bool Visible(WidgetPlacement p, bool showDetails) =>
			p == WidgetPlacement.Main || (p == WidgetPlacement.DetailsOnly && showDetails);

		/// <summary>The odds lines the widget may draw, in order.</summary>
		public static List<CardOdds> ForWidget(IEnumerable<CardOdds> odds, bool showDetails) =>
			(odds ?? Enumerable.Empty<CardOdds>()).Where(o => Visible(Place(o), showDetails)).ToList();

		// ------------------------------------------------------------------ one-drop odds

		/// <summary>Ids of the deck's 1-cost cards that count as a turn-1 play (excluded ids removed).</summary>
		public static HashSet<string> OneDropIds(IEnumerable<(string id, int cost)> deck, ISet<string> excluded) =>
			OpenerHitIds(deck, excluded, null);

		/// <summary>
		/// Ids of the deck's cards that count as a turn-1 play for the opener line: every 1-cost card plus the extra hits
		/// (e.g. Cursed Catacombs) that are in the deck, minus the excluded ids (exclusion wins).
		/// </summary>
		public static HashSet<string> OpenerHitIds(IEnumerable<(string id, int cost)> deck, ISet<string> excluded, ISet<string> extraHits) =>
			new HashSet<string>((deck ?? Enumerable.Empty<(string id, int cost)>())
				.Where(c => c.id != null && (c.cost == 1 || (extraHits != null && extraHits.Contains(c.id)))
				            && (excluded == null || !excluded.Contains(c.id)))
				.Select(c => c.id));

		/// <summary>
		/// Short label for the opener line: "1-drop", or "1-drop/Catacombs" when extra hits are in the deck
		/// (last word of each extra card's name).
		/// </summary>
		public static string OpenerLabel(IEnumerable<string> extraNamesInDeck)
		{
			var shorts = (extraNamesInDeck ?? Enumerable.Empty<string>())
				.Where(n => !string.IsNullOrWhiteSpace(n))
				.Select(n => n.Trim().Split(' ').Last())
				.Distinct().ToList();
			return shorts.Count == 0 ? "1-drop" : "1-drop/" + string.Join("/", shorts);
		}
	}
}
