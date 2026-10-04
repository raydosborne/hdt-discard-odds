using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace DiscardOdds
{
	// Pure math, no HDT/WPF types, so it is unit-tested on any OS (tests/OddsEngine.Tests).
	// Every rule below comes from the card's real text in HearthstoneJSON (api.hearthstonejson.com/v1/latest/enUS, 2026-10-04).
	// "Hit" means the card's effect lands on (draws, discovers, or discards) one of your target cards.
	// Targets are per deck (targets.json / the "Choose target cards" window), so this works for any deck.
	// Cards in CardRules use a card-specific model; any other card whose text says "Draw a card" / "Draw N cards"
	// gets a generic DrawK rule from DrawText.Parse. The numbers are odds only. They never say what to play.

	public enum OddsKind
	{
		DrawK,            // draw k random cards from the deck
		DrawShadowSpell,  // Sketch Artist: draw a Shadow spell from the deck
		DiscoverFromDeck, // Cursed Catacombs: Discover another card from your deck
		DiscardLowest,    // Wicked Whispers
		DiscardHighest,   // Expired Merchant, Chronoclaws
		DiscardRandom,    // Soulfire, Darkshire Librarian
		DiscardChoose,    // Ocular Occultist, Gemstone Hoarder
		DiscardLook3,     // Chamber of Viscidus: look at 3 cards in hand, choose one
		DrawThenDiscardIt // Platysaur: draw 1, deathrattle discards that card
	}

	public sealed class OddsRule
	{
		public string CardId;
		public string Name;
		public string Text;   // HearthstoneJSON text, for tooltips/README
		public OddsKind Kind;
		public int K = 1;     // cards drawn for DrawK
		public bool Approx;   // model not verified against game rules yet, or the draw is conditional (flagged with ≈)
		public string Clause; // generic rules: text before the draw, e.g. "Deathrattle" (shown in the detail)
		public bool Generic;  // built from card text by DrawText.Parse

		/// <summary>Card-specific models (discard outlets and special draws). These win over the generic text rule.</summary>
		public static readonly Dictionary<string, OddsRule> CardRules = new[]
		{
			new OddsRule { CardId = "TLC_603", Name = "Platysaur", Kind = OddsKind.DrawThenDiscardIt, Text = "Battlecry: Draw a card. Deathrattle: Discard it." },
			new OddsRule { CardId = "BOT_568", Name = "The Soularium", Kind = OddsKind.DrawK, K = 3, Text = "Draw 3 cards. They are Temporary." },
			new OddsRule { CardId = "TLC_451", Name = "Cursed Catacombs", Kind = OddsKind.DiscoverFromDeck, Approx = true, Text = "Discover another card from your deck. Make it Temporary." },
			new OddsRule { CardId = "DMF_119", Name = "Wicked Whispers", Kind = OddsKind.DiscardLowest, Text = "Discard your lowest Cost card. Give your minions +1/+1." },
			new OddsRule { CardId = "ULD_163", Name = "Expired Merchant", Kind = OddsKind.DiscardHighest, Text = "Battlecry: Discard your highest Cost card. Deathrattle: Add 2 copies of it to your hand." },
			new OddsRule { CardId = "END_016", Name = "Chronoclaws", Kind = OddsKind.DiscardHighest, Text = "After your hero attacks, discard your highest Cost card." },
			new OddsRule { CardId = "EX1_308", Name = "Soulfire", Kind = OddsKind.DiscardRandom, Text = "Deal 4 damage. Discard a random card." },
			new OddsRule { CardId = "OG_109", Name = "Darkshire Librarian", Kind = OddsKind.DiscardRandom, Text = "Battlecry: Discard a random card. Deathrattle: Draw a card." },
			new OddsRule { CardId = "CATA_490", Name = "Ocular Occultist", Kind = OddsKind.DiscardChoose, Text = "Taunt. Battlecry: Choose a card in your hand to discard." },
			new OddsRule { CardId = "CATA_897", Name = "Gemstone Hoarder", Kind = OddsKind.DiscardChoose, Text = "Battlecry: Choose a card in your hand to discard. Deathrattle: Get it back. It costs (1) less." },
			new OddsRule { CardId = "WON_103", Name = "Chamber of Viscidus", Kind = OddsKind.DiscardLook3, Approx = true, Text = "Look at 3 cards in your hand and choose one to discard. Draw two cards." },
			new OddsRule { CardId = "LOOT_014", Name = "Kobold Librarian", Kind = OddsKind.DrawK, K = 1, Text = "Battlecry: Draw a card. Deal 2 damage to your hero." },
			new OddsRule { CardId = "BT_300", Name = "Hand of Gul'dan", Kind = OddsKind.DrawK, K = 3, Text = "When you play or discard this, draw 3 cards." },
			new OddsRule { CardId = "TOY_916", Name = "Sketch Artist", Kind = OddsKind.DrawShadowSpell, Approx = true, Text = "Battlecry: Draw a Shadow spell. Get a Temporary copy of it." },
		}.ToDictionary(r => r.CardId);

		/// <summary>Specific rule if one exists, else a generic draw rule from the card text, else null.</summary>
		public static OddsRule ForCard(string cardId, string name, string cardText)
		{
			if(cardId == null) return null;
			if(CardRules.TryGetValue(cardId, out var specific)) return specific;
			var d = DrawText.Parse(cardText);
			if(d == null) return null;
			return new OddsRule { CardId = cardId, Name = name ?? cardId, Kind = OddsKind.DrawK, K = d.Count, Approx = d.Conditional, Clause = d.Clause, Generic = true, Text = cardText };
		}
	}

	/// <summary>What DrawText found in a card's text.</summary>
	public sealed class DrawInfo
	{
		public int Count;         // cards drawn
		public bool Conditional;  // inside a trigger/condition (Deathrattle, If..., Whenever..., for each...)
		public string Clause;     // text before the draw in the same sentence, e.g. "Deathrattle" or "If you're holding a Dragon"
	}

	/// <summary>Parses HearthstoneJSON card text for plain "draw a card / draw N cards" effects from your own deck.</summary>
	public static class DrawText
	{
		private static readonly Regex Tags = new Regex(@"<[^>]+>|\[x\]", RegexOptions.Compiled);
		private static readonly Regex Draw = new Regex(@"\bdraw (a|an|one|two|three|four|five|six|seven|\d+) cards?\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
		private static readonly Regex ConditionWords = new Regex(
			@"\b(if|when|whenever|after|while|at the|start|end|deathrattle|combo|outcast|overheal|corrupt|frenzy|finale|overkill|honorable kill|choose|secret|quest|questline|spend|infuse|spellburst|inspire|manathirst|forge|excavate|imbue|invoke|dormant|or)\b",
			RegexOptions.Compiled | RegexOptions.IgnoreCase);
		private static readonly Regex TriggerSubject = new Regex(@"\b(you|they|player|players|opponent|it|no longer)\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
		private static readonly string[] Numbers = { "zero", "one", "two", "three", "four", "five", "six", "seven" };

		public static DrawInfo Parse(string text)
		{
			if(string.IsNullOrWhiteSpace(text)) return null;
			var t = Tags.Replace(text, "").Replace('\n', ' ').Replace("_", " ");
			t = Regex.Replace(t, @"\s+", " ").Trim();
			// Skip "Whenever you draw a card" / "After you draw 4 cards" (triggers on drawing, not a draw effect).
			var m = Draw.Match(t);
			while(m.Success && TriggerSubject.IsMatch(t.Substring(0, m.Index)))
				m = m.NextMatch();
			if(!m.Success) return null;
			var after = t.Substring(m.Index + m.Length);
			if(Regex.IsMatch(after, @"^\s*from (your|the) opponent", RegexOptions.IgnoreCase)) return null; // not your deck
			var w = m.Groups[1].Value.ToLowerInvariant();
			int n;
			if(w == "a" || w == "an") n = 1;
			else if(!int.TryParse(w, out n)) n = Array.IndexOf(Numbers, w);
			if(n <= 0) return null;
			// The clause: same sentence, text before "draw".
			var sentenceStart = Math.Max(t.LastIndexOf(". ", m.Index, StringComparison.Ordinal), t.LastIndexOf(".", m.Index, StringComparison.Ordinal));
			var prefix = t.Substring(sentenceStart < 0 ? 0 : sentenceStart + 1, m.Index - (sentenceStart < 0 ? 0 : sentenceStart + 1)).Trim().TrimEnd(',', ':', ' ');
			var sentenceEnd = after.IndexOf('.');
			var rest = sentenceEnd < 0 ? after : after.Substring(0, sentenceEnd);
			// Unconditional unless the clause has a trigger/condition word, granted text in quotes, or a cost ("... to draw").
			var unconditional = prefix.Length == 0
			                    || !(prefix.IndexOf('"') >= 0 || ConditionWords.IsMatch(prefix) || Regex.IsMatch(prefix, @"\bto$", RegexOptions.IgnoreCase));
			var variable = Regex.IsMatch(rest, @"\b(for each|per|until|if|when)\b", RegexOptions.IgnoreCase);
			return new DrawInfo
			{
				Count = n,
				Conditional = !unconditional || variable,
				Clause = prefix.Length == 0 ? null : prefix
			};
		}
	}

	/// <summary>What the engine needs to know about one hand card.</summary>
	public sealed class OddsHandCard
	{
		public int EntityId;
		public string CardId;
		public string Name;
		public int Cost;
		public bool InGroup;
	}

	public sealed class OddsState
	{
		public List<OddsHandCard> Hand = new List<OddsHandCard>();
		/// <summary>Known remaining deck contents: cardId -> copies.</summary>
		public Dictionary<string, int> Deck = new Dictionary<string, int>();
		/// <summary>True deck size (HDT Player.DeckCount). Cards beyond the known list count as non-group.</summary>
		public int DeckCount;
		public ISet<string> Group = new HashSet<string>();
		public Func<string, bool> IsShadowSpell = _ => false;
		/// <summary>Card text by id (HearthDb at runtime), used for the generic draw rule.</summary>
		public Func<string, string> CardText = _ => null;
	}

	public sealed class CardOdds
	{
		public string CardId;
		public string Name;
		public OddsKind Kind;
		public double Hit;        // P(effect hits a group card)
		public double Miss => 1 - Hit;
		public double ExpectedHits;
		public bool Approx;
		public string Detail;

		public override string ToString() => $"{Name}: hit {OddsEngine.Pct(Hit)} / miss {OddsEngine.Pct(Miss)}{(Approx ? " ≈" : "")} ({Detail})";
	}

	public static class OddsEngine
	{
		/// <summary>Culture-independent "72%".</summary>
		public static string Pct(double p) => (Math.Round(p * 100)).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "%";

		/// <summary>P(at least one success in k draws without replacement) for n successes among m cards.</summary>
		public static double PAtLeastOne(int n, int m, int k)
		{
			if(m <= 0 || n <= 0 || k <= 0) return 0;
			if(k >= m || m - n < k) return 1;
			double pMiss = 1;
			for(var i = 0; i < k; i++)
				pMiss *= (double)(m - n - i) / (m - i);
			return 1 - pMiss;
		}

		/// <summary>Odds for one card if it were played now. The card itself (one copy, by entity) is excluded from the hand.</summary>
		public static CardOdds Compute(OddsRule rule, OddsState s, int playedEntityId = 0)
		{
			var others = s.Hand.Where(h => h.EntityId != playedEntityId).ToList();
			if(playedEntityId == 0)
			{
				var self = s.Hand.FirstOrDefault(h => h.CardId == rule.CardId);
				if(self != null) others = s.Hand.Where(h => h.EntityId != self.EntityId).ToList();
			}
			// m = the real deck size (callers pass DeckCount minus Casts-When-Drawn cards). The known list can briefly
			// exceed it (HDT updates PlayerCardList after DeckCount); callers wait for a settled list, and n is clamped.
			var m = s.DeckCount;
			var n = Math.Min(m, s.Deck.Where(kv => s.Group.Contains(kv.Key)).Sum(kv => kv.Value));
			var r = new CardOdds { CardId = rule.CardId, Name = rule.Name, Kind = rule.Kind, Approx = rule.Approx };

			switch(rule.Kind)
			{
				case OddsKind.DrawK:
				case OddsKind.DrawThenDiscardIt:
				{
					var k = rule.Kind == OddsKind.DrawK ? rule.K : 1;
					r.Hit = PAtLeastOne(n, m, k);
					r.ExpectedHits = m > 0 ? (double)k * n / m : 0;
					r.Detail = (rule.Clause != null ? rule.Clause + ": " : "") + $"{n} targets in {m} cards, draw {k}";
					if(rule.Kind == OddsKind.DrawThenDiscardIt)
						r.Detail += "; on death it discards the drawn card";
					break;
				}
				case OddsKind.DrawShadowSpell:
				{
					var shadow = s.Deck.Where(kv => s.IsShadowSpell(kv.Key)).ToList();
					var total = shadow.Sum(kv => kv.Value);
					var hits = shadow.Where(kv => s.Group.Contains(kv.Key)).Sum(kv => kv.Value);
					r.Hit = total > 0 ? (double)hits / total : 0;
					r.ExpectedHits = r.Hit;
					r.Detail = total > 0 ? $"{hits} of {total} Shadow spells in deck are targets" : "no Shadow spells left";
					break;
				}
				case OddsKind.DiscoverFromDeck:
				{
					// Model (unverified, M0 logs the real offers): 3 distinct card ids, uniform over distinct ids left in the deck,
					// excluding Cursed Catacombs itself ("another card").
					var ids = s.Deck.Where(kv => kv.Value > 0 && kv.Key != rule.CardId).Select(kv => kv.Key).ToList();
					var d = ids.Count;
					var dp = ids.Count(id => s.Group.Contains(id));
					r.Hit = PAtLeastOne(dp, d, Math.Min(3, d));
					r.ExpectedHits = d > 0 ? Math.Min(3, d) * (double)dp / d : 0;
					r.Detail = $"{dp} of {d} different cards left are targets, 3 offered";
					break;
				}
				case OddsKind.DiscardLowest:
				case OddsKind.DiscardHighest:
				{
					if(others.Count == 0) { r.Hit = 0; r.Detail = "hand empty, nothing to discard"; break; }
					var target = rule.Kind == OddsKind.DiscardLowest ? others.Min(h => h.Cost) : others.Max(h => h.Cost);
					var tied = others.Where(h => h.Cost == target).ToList();
					var hits = tied.Count(h => h.InGroup);
					r.Hit = (double)hits / tied.Count; // ties assumed uniformly random (M0 logs real tie-breaks)
					r.ExpectedHits = r.Hit;
					r.Detail = (rule.Kind == OddsKind.DiscardLowest ? "lowest" : "highest") + $" cost {target}: " +
					           string.Join(", ", tied.Select(h => h.Name + (h.InGroup ? "*" : ""))) + (tied.Count > 1 ? " (tie)" : "");
					break;
				}
				case OddsKind.DiscardRandom:
				{
					var hits = others.Count(h => h.InGroup);
					r.Hit = others.Count > 0 ? (double)hits / others.Count : 0;
					r.ExpectedHits = r.Hit;
					r.Detail = $"{hits} targets among {others.Count} other cards";
					break;
				}
				case OddsKind.DiscardChoose:
				{
					var hits = others.Count(h => h.InGroup);
					r.Hit = hits > 0 ? 1 : 0;
					r.ExpectedHits = r.Hit;
					r.Detail = hits > 0 ? $"you choose; {hits} target(s) in hand" : "you choose; no target in hand";
					break;
				}
				case OddsKind.DiscardLook3:
				{
					// Model (unverified): the 3 cards are random among your other hand cards.
					var hits = others.Count(h => h.InGroup);
					var shown = Math.Min(3, others.Count);
					r.Hit = PAtLeastOne(hits, others.Count, shown);
					r.ExpectedHits = r.Hit;
					r.Detail = $"{hits} targets among {others.Count} other cards, sees {shown}";
					break;
				}
			}
			return r;
		}

		/// <summary>
		/// Odds for every odds card currently in hand (one line per distinct card id): card-specific rules first,
		/// then generic "draw N" rules from the card text (via s.CardText).
		/// </summary>
		public static List<CardOdds> ForHand(OddsState s, IDictionary<string, OddsRule> rules)
		{
			var result = new List<CardOdds>();
			foreach(var h in s.Hand.GroupBy(x => x.CardId).Select(g => g.First()))
			{
				if(h.CardId == null) continue;
				OddsRule rule;
				if(!rules.TryGetValue(h.CardId, out rule))
				{
					string text = null;
					try { text = s.CardText(h.CardId); } catch { }
					rule = OddsRule.ForCard(h.CardId, h.Name, text);
					if(rule == null || !rule.Generic) continue;
				}
				result.Add(Compute(rule, s, h.EntityId));
			}
			return result;
		}
	}
}
