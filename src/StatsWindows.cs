using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Hearthstone_Deck_Tracker.Enums;
using Hearthstone_Deck_Tracker.Hearthstone;
using Hearthstone_Deck_Tracker.Stats;
using HdtConfig = Hearthstone_Deck_Tracker.Config;
using HdtDeckList = Hearthstone_Deck_Tracker.DeckList;

namespace DiscardOdds
{
	/// <summary>Reads HDT's own saved games for the active deck (all decks with the same name and class).</summary>
	public static class DeckHistory
	{
		private static readonly ConcurrentDictionary<string, ReplayCards> s_replayCache = new ConcurrentDictionary<string, ReplayCards>(StringComparer.OrdinalIgnoreCase);

		public static Deck ActiveDeck => HdtDeckList.Instance.ActiveDeck;

		/// <summary>The active deck plus any other HDT deck with the same name and class (HDT sometimes keeps re-imports apart).</summary>
		public static List<Deck> SameNameDecks(Deck active)
		{
			if(active == null) return new List<Deck>();
			return HdtDeckList.Instance.Decks.Where(d => d != null && !d.IsArenaDeck && d.Name == active.Name && d.Class == active.Class)
				.OrderBy(d => d.DeckId == active.DeckId ? 1 : 0).ThenBy(d => d.LastEdited).ToList();
		}

		public static int Result(GameStats g) => g.Result == GameResult.Win ? 1 : g.Result == GameResult.Loss ? -1 : 0;

		public static List<GameStats> Games(Deck d)
		{
			try { return d?.DeckStats?.Games?.Where(g => g != null).ToList() ?? new List<GameStats>(); }
			catch { return new List<GameStats>(); }
		}

		public static string ReplayPath(GameStats g)
		{
			if(string.IsNullOrEmpty(g?.ReplayFile)) return null;
			try
			{
				var p = Path.IsPathRooted(g.ReplayFile) ? g.ReplayFile : Path.Combine(HdtConfig.Instance.ReplayDir, g.ReplayFile);
				return File.Exists(p) ? p : null;
			}
			catch { return null; }
		}

		/// <summary>Parses an .hdtreplay (zip with the game's Power.log as output_log.txt); null if missing/unreadable.</summary>
		public static ReplayCards ReadReplay(string path)
		{
			if(path == null) return null;
			if(s_replayCache.TryGetValue(path, out var cached)) return cached;
			ReplayCards r = null;
			try
			{
				using(var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
				using(var zip = new ZipArchive(fs, ZipArchiveMode.Read))
				{
					var entry = zip.GetEntry("output_log.txt") ?? zip.Entries.FirstOrDefault(e => e.Name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase));
					if(entry != null)
						using(var reader = new StreamReader(entry.Open()))
						{
							var lines = new List<string>();
							string l;
							while((l = reader.ReadLine()) != null) lines.Add(l);
							r = StatsEngine.ParsePowerLog(lines);
						}
				}
			}
			catch(Exception ex) { ProbeLog.Line("STATS", "replay read failed: " + Path.GetFileName(path) + ": " + ex.Message); }
			if(r != null) s_replayCache[path] = r;
			return r;
		}

		public static (string name, int cost) CardInfo(string id)
		{
			try { if(HearthDb.Cards.All.TryGetValue(id, out var c)) return (c.Name ?? id, c.Cost); } catch { }
			return (id, 0);
		}

		public static Dictionary<string, (string name, int count)> CardMap(Deck d)
		{
			var map = new Dictionary<string, (string, int)>();
			foreach(var c in d?.Cards ?? Enumerable.Empty<Hearthstone_Deck_Tracker.Hearthstone.Card>())
			{
				if(c?.Id == null) continue;
				map[c.Id] = (c.Name ?? c.Id, (map.TryGetValue(c.Id, out var x) ? x.Item2 : 0) + c.Count);
			}
			return map;
		}
	}

	/// <summary>Shared window chrome: a header text, a sortable DataGrid, greyed rows below the sample minimum.</summary>
	public abstract class StatsWindowBase : Window
	{
		protected readonly TextBlock Header = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
		protected readonly TextBlock Footer = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), Foreground = Brushes.DimGray };
		protected readonly DataGrid Grid = new DataGrid
		{
			AutoGenerateColumns = false, IsReadOnly = true, CanUserSortColumns = true, CanUserAddRows = false,
			HeadersVisibility = DataGridHeadersVisibility.Column, GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
			SelectionMode = DataGridSelectionMode.Single
		};

		protected StatsWindowBase(string title, double width, double height)
		{
			Title = title;
			Width = width;
			Height = height;
			WindowStartupLocation = WindowStartupLocation.CenterScreen;
			var root = new DockPanel { Margin = new Thickness(10) };
			DockPanel.SetDock(Header, Dock.Top);
			DockPanel.SetDock(Footer, Dock.Bottom);
			root.Children.Add(Header);
			root.Children.Add(Footer);
			root.Children.Add(Grid);
			Content = root;
			var style = new Style(typeof(DataGridRow));
			var low = new DataTrigger { Binding = new Binding("LowSample"), Value = true };
			low.Setters.Add(new Setter(ForegroundProperty, Brushes.Gray));
			style.Triggers.Add(low);
			Grid.RowStyle = style;
		}

		protected void Col(string header, string path, string sortPath = null, double width = double.NaN)
		{
			var c = new DataGridTextColumn { Header = header, Binding = new Binding(path), SortMemberPath = sortPath ?? path };
			if(!double.IsNaN(width)) c.Width = new DataGridLength(width);
			Grid.Columns.Add(c);
		}
	}

	/// <summary>"Card stats…": keep win % and drawn win % per card of the active deck, from HDT's saved games and replays.</summary>
	public class CardStatsWindow : StatsWindowBase
	{
		public CardStatsWindow() : base("Discard Odds: card stats", 640, 720)
		{
			Col("Cost", "Cost", width: 45);
			Col("Card", "Name", width: 200);
			Col("Keep win %", "KeepWinText", "KeepSort");
			Col("Games kept", "KeepGames");
			Col("Drawn win %", "DrawWinText", "DrawSort");
			Col("Games drawn", "DrawGames");
			Header.Text = "Reading your saved games…";
			Loaded += async (s, e) => await Fill();
		}

		private async Task Fill()
		{
			try
			{
				var active = DeckHistory.ActiveDeck;
				if(active == null) { Header.Text = "Select a deck in HDT first."; return; }
				var decks = DeckHistory.SameNameDecks(active);
				var games = decks.SelectMany(DeckHistory.Games).Where(g => DeckHistory.Result(g) != 0).OrderBy(g => g.StartTime).ToList();
				var cards = DeckHistory.CardMap(active.GetSelectedDeckVersion()).Keys
					.Select(id => { var (n, c) = DeckHistory.CardInfo(id); return (id, n, c); }).OrderBy(x => x.c).ThenBy(x => x.n).ToList();
				var records = await Task.Run(() =>
				{
					var list = new List<GameCardRecord>();
					foreach(var g in games)
					{
						var rc = DeckHistory.ReadReplay(DeckHistory.ReplayPath(g));
						if(rc == null || !rc.Ok) continue;
						list.Add(new GameCardRecord { Result = DeckHistory.Result(g), Kept = rc.Kept, Drawn = rc.Drawn });
					}
					return list;
				});
				Grid.ItemsSource = StatsEngine.CardStats(records, cards);
				var first = games.Count > 0 ? games.First().StartTime : (DateTime?)null;
				var last = games.Count > 0 ? games.Last().StartTime : (DateTime?)null;
				Header.Text = $"Deck: {active.Name} ({active.GetSelectedDeckVersion().Version?.ShortVersionString}), cards of the selected version.\n"
				              + $"Based on {records.Count} of your {games.Count} won/lost games with this deck name ({StatsEngine.DateRange(first, last)}) that still have an HDT replay. "
				              + "Games without a replay can't show which cards were kept or drawn, so they are left out.";
				Footer.Text = "Keep win % = win rate in games where the card was in your opening hand after the mulligan. "
				              + "Drawn win % = win rate in games where you drew or played the card at any point (opening hand included; cards tossed in the mulligan and never drawn again don't count). "
				              + $"Draws and unfinished games are left out. Below {StatsEngine.MinGames} games a rate shows n/a and the row is grey. "
				              + "Click a column header to sort. Read from HDT's game history and replay files (Replays folder) on this PC; nothing is uploaded. These are past results, not advice.";
				ProbeLog.Line("STATS", $"card stats: deck '{active.Name}', {games.Count} games, {records.Count} with replay");
			}
			catch(Exception ex)
			{
				Header.Text = "Couldn't read the game history: " + ex.Message;
				ProbeLog.Line("ERR", "card stats: " + ex);
			}
		}
	}

	/// <summary>"Version compare…": record per HDT deck version of the active deck (and same-name decks).</summary>
	public class VersionCompareWindow : StatsWindowBase
	{
		public VersionCompareWindow() : base("Discard Odds: version compare", 900, 520)
		{
			Col("Version", "Version", width: 110);
			Col("Dates", "Dates", width: 170);
			Col("Record (W-L)", "Record", "Wins");
			Col("Win %", "WinText", "WinSort");
			Col("Added (vs previous)", "Added", width: 230);
			Col("Removed (vs previous)", "Removed", width: 230);
			Grid.CanUserSortColumns = true;
			try { Fill(); }
			catch(Exception ex) { Header.Text = "Couldn't read the deck versions: " + ex.Message; ProbeLog.Line("ERR", "version compare: " + ex); }
		}

		private void Fill()
		{
			var active = DeckHistory.ActiveDeck;
			if(active == null) { Header.Text = "Select a deck in HDT first."; return; }
			var decks = DeckHistory.SameNameDecks(active);
			var inputs = new List<StatsEngine.VersionInput>();
			var unversioned = new List<(DateTime, int)>();
			foreach(var d in decks)
			{
				var games = DeckHistory.Games(d);
				var all = (d.Versions ?? new List<Deck>()).Concat(new[] { d }).Where(v => v != null).OrderBy(v => v.Version).ToList();
				var suffix = decks.Count > 1 ? $" ({d.LastEdited:yyyy-MM-dd})" : "";
				foreach(var v in all)
				{
					var ver = v.Version ?? d.Version;
					inputs.Add(new StatsEngine.VersionInput
					{
						Label = (ver?.ShortVersionString ?? "?") + suffix,
						Cards = DeckHistory.CardMap(v),
						Games = games.Where(g => g.PlayerDeckVersion != null && ver != null && g.PlayerDeckVersion.Major == ver.Major && g.PlayerDeckVersion.Minor == ver.Minor)
							.Select(g => (g.StartTime, DeckHistory.Result(g))).ToList()
					});
				}
				unversioned.AddRange(games.Where(g => g.PlayerDeckVersion == null).Select(g => (g.StartTime, DeckHistory.Result(g))));
			}
			var rows = StatsEngine.Versions(inputs);
			if(unversioned.Count > 0)
			{
				var extra = StatsEngine.Versions(new[] { new StatsEngine.VersionInput { Label = "(no version)", Games = unversioned } })[0];
				extra.Added = "HDT didn't record a version for these games";
				rows.Add(extra);
			}
			Grid.ItemsSource = rows;
			Header.Text = $"Deck: {active.Name}. Every HDT version of this deck{(decks.Count > 1 ? $" and of {decks.Count - 1} other HDT deck(s) with the same name (dated by last edit)" : "")}, oldest first. "
			              + "Record and win % count won/lost games played with that version (draws left out).";
			Footer.Text = $"Added / removed = card changes compared with the row above. Dates = first to last game with that version. Below {StatsEngine.MinGames} games win % shows n/a and the row is grey. "
			              + "Click a column header to sort. From HDT's own deck and game history on this PC; nothing is uploaded.";
			ProbeLog.Line("STATS", $"version compare: deck '{active.Name}', {rows.Count} rows");
		}
	}
}
