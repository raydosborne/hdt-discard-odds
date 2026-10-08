using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace DiscardOdds
{
	/// <summary>How one piece of a multi-colored widget line is drawn (mapped to WidgetColors by the widget).</summary>
	public enum SegKind
	{
		Label, // light grey text
		Hit,   // bold green number (hit / payoff chance)
		Miss,  // red number (miss / waste chance)
		Dim,   // dim grey text
		Name   // gold card name
	}

	/// <summary>Soularium (draw 3) payoff split: P(0), P(1+), P(2+), P(all 3).</summary>
	public sealed class SoulariumOdds
	{
		public int M, N, Draws;
		public double[] Dist = new double[0]; // P(exactly i payoffs), i = 0..Draws
		public double P0, P1Plus, P2Plus, PAll;
	}

	/// <summary>Soularium risk: the remaining deck split into payoffs / playable / wasted for mana left after it.</summary>
	public sealed class WasteSplit
	{
		public int M, Payoffs, Playable, Wasted, Unknown, ManaLeft, Draws;
		public double Payoff1Plus, Waste1Plus, AvgWasted;
	}

	/// <summary>Next turn's draw: chance it is a payoff, and chance it costs at most next turn's mana.</summary>
	public sealed class NextDrawOdds
	{
		public int M, Payoffs, PlayableCount, Mana;
		public double Payoff, Playable;
	}

	/// <summary>Game luck: outcomes vs predictions (expected hits and the average mid-percentile of each outcome).</summary>
	public sealed class LuckTally
	{
		public int Events;
		public double Expected;
		public double Actual;
		private double _percentileSum;

		/// <summary>Yes/no prediction (hit chance p). Mid-percentile: hit = 1 - p/2, miss = (1 - p)/2.</summary>
		public void AddBinary(double p, bool hit)
		{
			p = Math.Max(0, Math.Min(1, p));
			Events++;
			Expected += p;
			Actual += hit ? 1 : 0;
			_percentileSum += hit ? 1 - p / 2 : (1 - p) / 2;
		}

		/// <summary>Count prediction (distribution of hits, e.g. Soularium payoffs among 3 draws).</summary>
		public void AddCount(double[] dist, int observed)
		{
			if(dist == null || dist.Length == 0) return;
			Events++;
			for(var i = 0; i < dist.Length; i++) Expected += i * dist[i];
			Actual += observed;
			_percentileSum += DrawMath.MidPercentile(dist, observed);
		}

		public void Merge(LuckTally o)
		{
			if(o == null) return;
			Events += o.Events;
			Expected += o.Expected;
			Actual += o.Actual;
			_percentileSum += o._percentileSum;
		}

		/// <summary>Average mid-percentile of the outcomes: 50% = exactly as expected, higher = luckier.</summary>
		public double MeanPercentile => Events > 0 ? _percentileSum / Events : 0.5;

		public static string Rating(double meanPercentile) =>
			meanPercentile >= 0.65 ? "lucky" :
			meanPercentile >= 0.55 ? "a bit lucky" :
			meanPercentile > 0.45 ? "about average" :
			meanPercentile > 0.35 ? "a bit unlucky" : "unlucky";

		public string Short() => string.Format(CultureInfo.InvariantCulture, "{0} events, hits {1:0} vs {2:0.0} expected", Events, Actual, Expected);
	}

	/// <summary>
	/// Pure draw math for the v0.1.6 lines (no HDT types; unit-tested): hypergeometric split for Soularium, the
	/// payoff/playable/wasted split, next-draw odds, result likelihood text and the luck score.
	/// </summary>
	public static class DrawMath
	{
		public const int SoulariumDraws = 3;

		public static double Choose(int n, int k)
		{
			if(n < 0 || k < 0 || k > n) return 0;
			k = Math.Min(k, n - k);
			double r = 1;
			for(var i = 1; i <= k; i++) r = r * (n - k + i) / i;
			return r;
		}

		/// <summary>P(exactly i successes), i = 0..draws, drawing draws cards from m with n successes (draws capped at m).</summary>
		public static double[] HyperDist(int m, int n, int draws)
		{
			m = Math.Max(0, m);
			n = Math.Max(0, Math.Min(n, m));
			var k = Math.Max(0, Math.Min(draws, m));
			var d = new double[k + 1];
			var total = Choose(m, k);
			if(total <= 0) { d[0] = 1; return d; }
			for(var i = 0; i <= k; i++) d[i] = Choose(n, i) * Choose(m - n, k - i) / total;
			return d;
		}

		/// <summary>P(at least one of `bad` cards among `draws` draws from m).</summary>
		public static double PAtLeastOne(int m, int bad, int draws)
		{
			var k = Math.Max(0, Math.Min(draws, m));
			if(m <= 0 || bad <= 0 || k == 0) return 0;
			return 1 - Choose(m - Math.Min(bad, m), k) / Choose(m, k);
		}

		public static SoulariumOdds Soularium(int m, int n, int draws = SoulariumDraws)
		{
			var d = HyperDist(m, n, draws);
			double At(int i) => i < d.Length ? d[i] : 0;
			return new SoulariumOdds
			{
				M = Math.Max(0, m), N = Math.Max(0, Math.Min(n, Math.Max(0, m))), Draws = draws, Dist = d,
				P0 = At(0),
				P1Plus = 1 - At(0),
				P2Plus = d.Skip(2).Sum(),
				PAll = At(draws)
			};
		}

		/// <summary>
		/// Splits the remaining deck for a draw-`draws` card: payoffs (ticked targets), playable (non-payoff with cost ≤ manaLeft)
		/// and wasted (the rest, including cards HDT can't name: m minus the known copies).
		/// </summary>
		public static WasteSplit Waste(IDictionary<string, int> deck, int m, ISet<string> payoffs, Func<string, int?> cost, int manaLeft, int draws = SoulariumDraws)
		{
			manaLeft = Math.Max(0, manaLeft); // can't pay for it now: count as 0 mana left
			var w = new WasteSplit { M = Math.Max(0, m), ManaLeft = manaLeft, Draws = Math.Max(0, Math.Min(draws, Math.Max(0, m))) };
			var known = 0;
			foreach(var kv in deck ?? new Dictionary<string, int>())
			{
				if(kv.Value <= 0) continue;
				known += kv.Value;
				if(payoffs != null && payoffs.Contains(kv.Key)) w.Payoffs += kv.Value;
				else
				{
					var c = cost?.Invoke(kv.Key);
					if(c.HasValue && c.Value <= manaLeft) w.Playable += kv.Value;
					else w.Wasted += kv.Value;
				}
			}
			w.Unknown = Math.Max(0, w.M - known);
			w.Wasted += w.Unknown;
			// Never more cards than the deck holds (HDT's list can briefly run ahead of DeckCount).
			var over = w.Payoffs + w.Playable + w.Wasted - w.M;
			if(over > 0) { var cut = Math.Min(over, w.Wasted); w.Wasted -= cut; over -= cut; }
			if(over > 0) { var cut = Math.Min(over, w.Playable); w.Playable -= cut; over -= cut; }
			if(over > 0) w.Payoffs -= Math.Min(over, w.Payoffs);
			w.Payoff1Plus = PAtLeastOne(w.M, w.Payoffs, draws);
			w.Waste1Plus = PAtLeastOne(w.M, w.Wasted, draws);
			w.AvgWasted = w.M > 0 ? (double)w.Draws * w.Wasted / w.M : 0;
			return w;
		}

		/// <summary>Mana on your next turn: max mana + 1 (capped at 10) minus Overload owed.</summary>
		public static int NextTurnMana(int maxMana, int overloadOwed) => Math.Max(0, Math.Min(10, Math.Max(0, maxMana) + 1) - Math.Max(0, overloadOwed));

		/// <summary>Next draw: payoff chance n/m and playable chance (any card, payoffs included, with cost ≤ mana; unknown cards count as not playable).</summary>
		public static NextDrawOdds NextDraw(IDictionary<string, int> deck, int m, ISet<string> payoffs, Func<string, int?> cost, int mana)
		{
			var r = new NextDrawOdds { M = Math.Max(0, m), Mana = mana };
			foreach(var kv in deck ?? new Dictionary<string, int>())
			{
				if(kv.Value <= 0) continue;
				if(payoffs != null && payoffs.Contains(kv.Key)) r.Payoffs += kv.Value;
				var c = cost?.Invoke(kv.Key);
				if(c.HasValue && c.Value <= mana) r.PlayableCount += kv.Value;
			}
			r.Payoffs = Math.Min(r.Payoffs, r.M);
			r.PlayableCount = Math.Min(r.PlayableCount, r.M);
			r.Payoff = r.M > 0 ? (double)r.Payoffs / r.M : 0;
			r.Playable = r.M > 0 ? (double)r.PlayableCount / r.M : 0;
			return r;
		}

		/// <summary>P(X &lt; observed) + P(X = observed) / 2: 50% = typical, higher = luckier.</summary>
		public static double MidPercentile(double[] dist, int observed)
		{
			if(dist == null || dist.Length == 0) return 0.5;
			double below = 0;
			for(var i = 0; i < Math.Min(observed, dist.Length); i++) below += dist[i];
			var at = observed >= 0 && observed < dist.Length ? dist[observed] : 0;
			return below + at / 2;
		}

		// ---------------------------------------------------------------- widget text (exact formats, unit-tested)

		public static string Pct(double p) => OddsEngine.Pct(Math.Max(0, Math.Min(1, p)));

		public static string Text(IEnumerable<(string text, SegKind kind)> segs) => string.Concat(segs.Select(s => s.text));

		/// <summary>"1+ 76% · 2+ 31% · 3/3 4% · whiff 24%" (after the gold "Soularium" name).</summary>
		public static List<(string, SegKind)> SoulariumSegments(SoulariumOdds o) => new List<(string, SegKind)>
		{
			("1+ ", SegKind.Label), (Pct(o.P1Plus), SegKind.Hit),
			(" · 2+ ", SegKind.Label), (Pct(o.P2Plus), SegKind.Hit),
			($" · {o.Draws}/{o.Draws} ", SegKind.Label), (Pct(o.PAll), SegKind.Hit),
			(" · whiff ", SegKind.Label), (Pct(o.P0), SegKind.Miss)
		};

		/// <summary>"Risk @2: Payoff 1+ 76% · Waste 1+ 45% · avg 0.6 wasted" (@2 = mana left after Soularium).</summary>
		public static List<(string, SegKind)> RiskSegments(WasteSplit w) => new List<(string, SegKind)>
		{
			($"Risk @{w.ManaLeft}: Payoff 1+ ", SegKind.Label), (Pct(w.Payoff1Plus), SegKind.Hit),
			(" · Waste 1+ ", SegKind.Label), (Pct(w.Waste1Plus), SegKind.Miss),
			(" · avg " + w.AvgWasted.ToString("0.0", CultureInfo.InvariantCulture) + " wasted", SegKind.Dim)
		};

		public static string RiskDetail(WasteSplit w, int soulariumCost, int manaNow) => string.Format(CultureInfo.InvariantCulture,
			"@{0} = {1} mana − Soularium cost {2}. Deck {3}: {4} payoffs, {5} playable (cost ≤ {0}), {6} wasted{7}",
			w.ManaLeft, manaNow, soulariumCost, w.M, w.Payoffs, w.Playable, w.Wasted, w.Unknown > 0 ? $" (incl. {w.Unknown} unknown)" : "");

		/// <summary>"Next draw: Payoff 33% · Playable 60%".</summary>
		public static List<(string, SegKind)> NextDrawSegments(NextDrawOdds n) => new List<(string, SegKind)>
		{
			("Next draw: Payoff ", SegKind.Label), (Pct(n.Payoff), SegKind.Hit),
			(" · Playable ", SegKind.Label), (Pct(n.Playable), SegKind.Hit)
		};

		public static string NextDrawDetail(NextDrawOdds n) => string.Format(CultureInfo.InvariantCulture,
			"{0} payoffs / {1} cards; playable = cost ≤ {2} (next turn's mana), {3} cards; unknown cards count as not playable",
			n.Payoffs, n.M, n.Mana, n.PlayableCount);

		/// <summary>"5 payoffs left" / "1 payoff left".</summary>
		public static string PayoffsLeftText(int n) => n == 1 ? "1 payoff left" : $"{Math.Max(0, n)} payoffs left";

		/// <summary>"Whiffed all 3: 8% chance", "1 payoff: 41% chance", "2 payoffs: 30% chance", "All 3 payoffs: 4% chance".</summary>
		public static string ResultText(int payoffs, int draws, double p)
		{
			string what;
			if(payoffs <= 0) what = $"Whiffed all {draws}";
			else if(payoffs >= draws && draws > 1) what = $"All {draws} payoffs";
			else what = payoffs == 1 ? "1 payoff" : $"{payoffs} payoffs";
			return $"{what}: {Pct(p)} chance";
		}

		/// <summary>One log line at game end: hits vs expected and the average outcome percentile.</summary>
		public static string LuckLine(LuckTally cards, LuckTally turnDraws)
		{
			var all = new LuckTally();
			all.Merge(cards);
			all.Merge(turnDraws);
			if(all.Events == 0) return "no predicted draws/discards resolved this game";
			var diff = all.Actual - all.Expected;
			return string.Format(CultureInfo.InvariantCulture,
				"{0} ({1}): avg outcome percentile {2} (50% = as expected, higher = luckier) · payoffs hit {3:0} vs {4:0.0} expected ({5}{6:0.0}) | card odds: {7} | turn draws: {8}",
				LuckTally.Rating(all.MeanPercentile), all.Events + (all.Events == 1 ? " event" : " events"), Pct(all.MeanPercentile),
				all.Actual, all.Expected, diff >= 0 ? "+" : "−", Math.Abs(diff),
				cards?.Short() ?? "0 events", turnDraws?.Short() ?? "0 events");
		}
	}
}
