using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace DiscardOdds
{
	/// <summary>Your cards in one game, read from its replay (Power.log lines).</summary>
	public sealed class ReplayCards
	{
		public bool Ok;                 // the log had a mulligan end (first MAIN_READY) and a friendly player
		public int FriendlyPlayer;
		public HashSet<string> Kept = new HashSet<string>();   // in your opening hand after the mulligan
		public HashSet<string> Drawn = new HashSet<string>();  // kept, or drawn / played later (from your starting deck)
	}

	/// <summary>One finished game for the card stats: result and the card ids kept / drawn.</summary>
	public sealed class GameCardRecord
	{
		public int Result;              // 1 win, -1 loss, 0 draw / unknown (not counted in win %)
		public ISet<string> Kept = new HashSet<string>();
		public ISet<string> Drawn = new HashSet<string>();
	}

	public sealed class CardStatRow
	{
		public string CardId { get; set; }
		public string Name { get; set; }
		public int Cost { get; set; }
		public int KeepWins { get; set; }
		public int KeepGames { get; set; }  // decided games (wins + losses) with the card kept
		public int DrawWins { get; set; }
		public int DrawGames { get; set; }  // decided games with the card drawn or played
		public double? KeepWinRate => KeepGames > 0 ? (double)KeepWins / KeepGames : (double?)null;
		public double? DrawWinRate => DrawGames > 0 ? (double)DrawWins / DrawGames : (double?)null;
		public bool LowSample { get; set; } // fewer than the minimum games drawn: row greyed out
		public string KeepWinText { get; set; }
		public string DrawWinText { get; set; }
		// Sort keys (n/a sorts below every number).
		public double KeepSort => KeepWinText == StatsEngine.NotAvailable ? -1 : KeepWinRate ?? -1;
		public double DrawSort => DrawWinText == StatsEngine.NotAvailable ? -1 : DrawWinRate ?? -1;
	}

	public sealed class VersionRow
	{
		public string Version { get; set; }
		public string Dates { get; set; }
		public int Wins { get; set; }
		public int Losses { get; set; }
		public string Record => $"{Wins}-{Losses}";
		public string WinText { get; set; }
		public double WinSort { get; set; }
		public string Added { get; set; }
		public string Removed { get; set; }
		public bool LowSample { get; set; }
	}

	/// <summary>
	/// Pure stats logic for the Card stats and Version compare windows (no HDT types; unit-tested):
	/// a small Power.log reader (opening hand after the mulligan, cards drawn/played), per-card win rates, deck version diffs.
	/// </summary>
	public static class StatsEngine
	{
		public const int MinGames = 5;
		public const string NotAvailable = "n/a";

		private static readonly Regex IdRx = new Regex(@"\bid=(\d+)", RegexOptions.CultureInvariant);
		private static readonly Regex CardIdInBracket = new Regex(@"\bcardId=(\S*)", RegexOptions.CultureInvariant);
		private static readonly Regex PlayerRx = new Regex(@"\bplayer=(\d+)", RegexOptions.CultureInvariant);
		private static readonly Regex FullRx = new Regex(@"^FULL_ENTITY - (?:Creating|Updating) (?:ID=(\d+)|(\[.*\]))\s*CardID=(\S*)", RegexOptions.CultureInvariant);
		private static readonly Regex ShowRx = new Regex(@"^(?:SHOW|CHANGE)_ENTITY - Updating Entity=(?:(\d+)|(\[.*\]))\s*CardID=(\S*)", RegexOptions.CultureInvariant);
		private static readonly Regex TagLineRx = new Regex(@"^tag=(\w+) value=(\S+)", RegexOptions.CultureInvariant);
		private static readonly Regex TagChangeRx = new Regex(@"^TAG_CHANGE Entity=(.*) tag=(\w+) value=(\S+)", RegexOptions.CultureInvariant);
		private const string PowerMarker = "GameState.DebugPrintPower() -";

		private sealed class Ent
		{
			public string CardId;
			public int Controller;
			public string Zone;
			public bool Origin;   // created before the mulligan ended: part of a starting deck/hand
			public bool Kept;
			public bool Seen;
		}

		/// <summary>Reads one game's Power.log lines (GameState lines only; PowerTaskList duplicates are ignored).</summary>
		public static ReplayCards ParsePowerLog(IEnumerable<string> lines)
		{
			var ents = new Dictionary<int, Ent>();
			Ent current = null;
			var mulliganOver = false;
			var friendly = 0;
			Ent Get(int id) { if(!ents.TryGetValue(id, out var e)) ents[id] = e = new Ent { Origin = !mulliganOver }; return e; }

			void SetZone(Ent e, string zone)
			{
				e.Zone = zone;
				if(mulliganOver && e.Origin && (zone == "HAND" || zone == "PLAY") && (friendly == 0 || e.Controller == friendly)) e.Seen = true;
			}

			void FromBracket(Ent e, string bracket)
			{
				var c = CardIdInBracket.Match(bracket);
				if(c.Success && c.Groups[1].Value.Length > 0 && c.Groups[1].Value != "]") e.CardId = c.Groups[1].Value.TrimEnd(']');
				var p = PlayerRx.Match(bracket);
				if(p.Success && e.Controller == 0) e.Controller = int.Parse(p.Groups[1].Value, CultureInfo.InvariantCulture);
			}

			foreach(var raw in lines ?? Enumerable.Empty<string>())
			{
				if(raw == null) continue;
				var i = raw.IndexOf(PowerMarker, StringComparison.Ordinal);
				if(i < 0) continue;
				var line = raw.Substring(i + PowerMarker.Length).Trim();
				if(line.StartsWith("CREATE_GAME", StringComparison.Ordinal)) { ents.Clear(); mulliganOver = false; friendly = 0; current = null; continue; }

				var m = FullRx.Match(line);
				if(m.Success)
				{
					var id = m.Groups[1].Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : ParseId(m.Groups[2].Value);
					if(id < 0) { current = null; continue; }
					current = Get(id);
					if(m.Groups[2].Success) FromBracket(current, m.Groups[2].Value);
					if(m.Groups[3].Value.Length > 0) current.CardId = m.Groups[3].Value;
					continue;
				}
				m = ShowRx.Match(line);
				if(m.Success)
				{
					var id = m.Groups[1].Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : ParseId(m.Groups[2].Value);
					if(id < 0) { current = null; continue; }
					current = Get(id);
					if(m.Groups[2].Success) FromBracket(current, m.Groups[2].Value);
					if(m.Groups[3].Value.Length > 0) current.CardId = m.Groups[3].Value;
					continue;
				}
				m = TagLineRx.Match(line);
				if(m.Success)
				{
					if(current == null) continue;
					var tag = m.Groups[1].Value;
					var val = m.Groups[2].Value;
					if(tag == "CONTROLLER" && int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ctl)) current.Controller = ctl;
					else if(tag == "ZONE") SetZone(current, val);
					continue;
				}
				current = null;
				m = TagChangeRx.Match(line);
				if(!m.Success) continue;
				var entity = m.Groups[1].Value;
				var t = m.Groups[2].Value;
				var v = m.Groups[3].Value;
				if(t == "STEP" && v == "MAIN_READY" && !mulliganOver)
				{
					// Mulligan over: your opening hand is the controller whose hand cards are known (the opponent's are hidden).
					friendly = ents.Values.Where(e => e.Zone == "HAND" && !string.IsNullOrEmpty(e.CardId) && e.Controller > 0)
						.GroupBy(e => e.Controller).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
					foreach(var e in ents.Values.Where(e => e.Zone == "HAND" && e.Controller == friendly && e.Origin)) { e.Kept = true; e.Seen = true; }
					mulliganOver = true;
					continue;
				}
				if(!entity.StartsWith("[", StringComparison.Ordinal)) continue;
				var eid = ParseId(entity);
				if(eid < 0) continue;
				var ent = Get(eid);
				FromBracket(ent, entity);
				if(t == "CONTROLLER" && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var c2)) ent.Controller = c2;
				else if(t == "ZONE") SetZone(ent, v);
			}

			var r = new ReplayCards { Ok = mulliganOver && friendly > 0, FriendlyPlayer = friendly };
			if(!r.Ok) return r;
			foreach(var e in ents.Values.Where(e => e.Controller == friendly && e.Origin && !string.IsNullOrEmpty(e.CardId)))
			{
				if(e.Kept) r.Kept.Add(e.CardId);
				if(e.Seen) r.Drawn.Add(e.CardId);
			}
			return r;
		}

		private static int ParseId(string bracket)
		{
			var m = IdRx.Match(bracket ?? "");
			return m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : -1;
		}

		public static string Rate(int wins, int games, int minGames = MinGames) =>
			games < Math.Max(1, minGames) ? NotAvailable : ((double)wins / games * 100).ToString("0", CultureInfo.InvariantCulture) + "%";

		/// <summary>Per-card keep / drawn win rates over decided games (draws and unknown results are skipped).</summary>
		public static List<CardStatRow> CardStats(IEnumerable<GameCardRecord> games, IEnumerable<(string id, string name, int cost)> cards, int minGames = MinGames)
		{
			var rows = new List<CardStatRow>();
			var list = (games ?? Enumerable.Empty<GameCardRecord>()).Where(g => g != null && g.Result != 0).ToList();
			foreach(var (id, name, cost) in cards ?? Enumerable.Empty<(string, string, int)>())
			{
				if(id == null || rows.Any(x => x.CardId == id)) continue;
				var row = new CardStatRow { CardId = id, Name = name ?? id, Cost = cost };
				foreach(var g in list)
				{
					var win = g.Result > 0 ? 1 : 0;
					if(g.Kept != null && g.Kept.Contains(id)) { row.KeepGames++; row.KeepWins += win; }
					if(g.Drawn != null && g.Drawn.Contains(id)) { row.DrawGames++; row.DrawWins += win; }
				}
				row.KeepWinText = Rate(row.KeepWins, row.KeepGames, minGames);
				row.DrawWinText = Rate(row.DrawWins, row.DrawGames, minGames);
				row.LowSample = row.DrawGames < minGames;
				rows.Add(row);
			}
			return rows;
		}

		/// <summary>Cards added and removed going from prev to cur (id → (name, count)), e.g. "+2 Soulfire".</summary>
		public static (List<string> added, List<string> removed) Diff(IDictionary<string, (string name, int count)> prev, IDictionary<string, (string name, int count)> cur)
		{
			var added = new List<string>();
			var removed = new List<string>();
			prev = prev ?? new Dictionary<string, (string, int)>();
			cur = cur ?? new Dictionary<string, (string, int)>();
			foreach(var id in cur.Keys.Union(prev.Keys).OrderBy(k => (cur.TryGetValue(k, out var c) ? c.name : prev[k].name) ?? k, StringComparer.OrdinalIgnoreCase))
			{
				cur.TryGetValue(id, out var c);
				prev.TryGetValue(id, out var p);
				var name = c.name ?? p.name ?? id;
				var d = c.count - p.count;
				if(d > 0) added.Add($"+{d} {name}");
				else if(d < 0) removed.Add($"−{-d} {name}");
			}
			return (added, removed);
		}

		public static string DateRange(DateTime? first, DateTime? last)
		{
			if(!first.HasValue) return "no games";
			var ci = CultureInfo.InvariantCulture;
			var a = first.Value;
			var b = last ?? a;
			if(a.Date == b.Date) return a.ToString("MMM d, yyyy", ci);
			return a.Year == b.Year ? $"{a.ToString("MMM d", ci)} – {b.ToString("MMM d, yyyy", ci)}" : $"{a.ToString("MMM d, yyyy", ci)} – {b.ToString("MMM d, yyyy", ci)}";
		}

		public sealed class VersionInput
		{
			public string Label;
			public Dictionary<string, (string name, int count)> Cards = new Dictionary<string, (string, int)>();
			public List<(DateTime start, int result)> Games = new List<(DateTime, int)>(); // result 1 win, -1 loss, 0 other
		}

		/// <summary>One row per version, in the order given; added/removed compared with the row before.</summary>
		public static List<VersionRow> Versions(IEnumerable<VersionInput> versions, int minGames = MinGames)
		{
			var rows = new List<VersionRow>();
			VersionInput prev = null;
			foreach(var v in versions ?? Enumerable.Empty<VersionInput>())
			{
				var wins = v.Games.Count(g => g.result > 0);
				var losses = v.Games.Count(g => g.result < 0);
				var (added, removed) = prev == null ? (new List<string>(), new List<string>()) : Diff(prev.Cards, v.Cards);
				var decided = wins + losses;
				rows.Add(new VersionRow
				{
					Version = v.Label,
					Dates = DateRange(v.Games.Count > 0 ? v.Games.Min(g => g.start) : (DateTime?)null, v.Games.Count > 0 ? v.Games.Max(g => g.start) : (DateTime?)null),
					Wins = wins, Losses = losses,
					WinText = Rate(wins, decided, minGames),
					WinSort = decided >= minGames ? (double)wins / decided : -1,
					Added = prev == null ? "(first version)" : added.Count > 0 ? string.Join(", ", added) : "—",
					Removed = prev == null ? "" : removed.Count > 0 ? string.Join(", ", removed) : "—",
					LowSample = decided < minGames
				});
				prev = v;
			}
			return rows;
		}
	}
}
