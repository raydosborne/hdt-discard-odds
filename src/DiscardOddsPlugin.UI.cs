using System;
using System.Diagnostics;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Hearthstone_Deck_Tracker.API;
using HdtApi = Hearthstone_Deck_Tracker.API;

namespace DiscardOdds
{
	public partial class DiscardOddsPlugin
	{
		/// <summary>
		/// Opening one-drop line (1-cost cards in the active deck list): during the mulligan and on your turn 1 only.
		/// Odds use OneDropOdds (tossed cards can't come back as their own replacements).
		/// 1-cost cards in the deck's "oneDropExclude" list (default for the Discard preset: Wicked Whispers) don't count.
		/// </summary>
		private void AddOneDropRows(List<WidgetRow> rows)
		{
			try
			{
				if(!_settings.ShowOneDrop || _probes.OpeningOver) return;
				var game = HdtApi.Core.Game;
				var player = game?.Player;
				if(player == null) return;
				var ones = WidgetPolicy.OneDropIds(_deckCards.Select(d => (d.Id, d.Cost)), Targets.Resolved?.OneDropExclude);
				var kList = _deckCards.Where(d => ones.Contains(d.Id)).Sum(d => d.Copies);
				if(kList == 0) return;
				var hand = _probes.LastHand.Where(h => !h.IsCoin).ToList();
				if(hand.Count == 0) return;
				if(hand.Any(h => ones.Contains(h.CardId)))
				{
					rows.Add(new WidgetRow { Name = "One-drop:", NameIsLabel = true, Text = "in hand ✓", Bold = true, TextColor = WidgetColors.Hit });
					return;
				}
				bool mulliganDone;
				try { mulliganDone = game.IsMulliganDone; } catch { mulliganDone = true; }
				var m = player.DeckCount;
				if(!mulliganDone)
				{
					var k = Math.Min(kList, m);
					var t = hand.Count;
					var keepName = _settings.CompactMode ? "1-drop · keep" : "1-drop by T1 · keep";
					var tossName = _settings.CompactMode ? $"1-drop · toss {t}" : $"1-drop by T1 · toss {t}";
					rows.Add(new WidgetRow { Name = keepName, NameIsLabel = true, Hit = 1 - OneDropOdds.PNoneByTurn1(m, k, 0), Detail = $"{k} one-drops in the {m} cards left; only the turn-1 draw" });
					rows.Add(new WidgetRow { Name = tossName, NameIsLabel = true, Hit = 1 - OneDropOdds.PNoneByTurn1(m, k, t), Detail = $"{t} replacements from the {m} cards left (tossed cards can't come back), then the turn-1 draw" });
					return;
				}
				var open = _probes.OpeningHand?.Where(h => !h.IsCoin).ToList();
				var after = _probes.HandAfterMulligan;
				var tossed = open != null && after != null ? open.Count(h => after.All(a => a.EntityId != h.EntityId)) : 0;
				var openHadOne = open != null && open.Any(h => ones.Contains(h.CardId));
				var m0 = _probes.DeckCountAtOpening;
				var k0 = Math.Min(kList, m0);
				var label = open != null && tossed == open.Count ? "full mulligan" : $"tossing {tossed}";
				var rare = tossed > 0 && !openHadOne && m0 > 0;
				if(!_probes.Turn1DrawSeen)
				{
					var rem = GameReader.RemainingDeck();
					var k = Math.Min(m, ones.Sum(id => rem.TryGetValue(id, out var v) ? v : 0));
					rows.Add(new WidgetRow { Name = _settings.CompactMode ? "1-drop T1" : "1-drop on T1 draw", NameIsLabel = true, Hit = m > 0 ? (double)k / m : 0, Detail = $"{k} one-drops in the {m} cards left" });
					if(rare)
						rows.Add(new WidgetRow { Text = $"Chance of this (no 1-drop after {label}): {OddsEngine.Pct(OneDropOdds.PNone(m0, k0, tossed))}", Small = true, TextColor = WidgetColors.Note });
				}
				else
				{
					rows.Add(new WidgetRow { Name = "One-drop:", NameIsLabel = true, Text = "none (missed)", Bold = true, TextColor = WidgetColors.Miss });
					if(rare)
						rows.Add(new WidgetRow { Text = $"Chance of this (no 1-drop by turn 1 after {label}): {OddsEngine.Pct(OneDropOdds.PNoneByTurn1(m0, k0, tossed))}", Small = true, TextColor = WidgetColors.Note });
				}
			}
			catch(Exception ex)
			{
				ProbeLog.Line("ERR", "one-drop: " + ex.Message);
			}
		}

		private string _lastLethalLog;
		private DateTime _unsettledSince = DateTime.MinValue;
		private bool _wasUnsettled;

		/// <summary>Lethal check on the player's turn: a factual count of damage that can reach the enemy hero.</summary>
		private void ShowLethal()
		{
			if(_widget == null) return;
			if(!_settings.ShowLethalCheck) { _widget.SetLethal(null, null, false); return; }
			try
			{
				var input = GameReader.BuildLethalInput(_probes.LastHand);
				if(input == null) { _widget.SetLethal(null, null, false); return; }
				var r = LethalEngine.Compute(input);
				_widget.SetLethal(r.Line, r.Detail, r.Lethal);
				var log = $"{r.Line} | {r.Detail} | mana {input.Mana} | attackers [{string.Join(", ", input.Minions.Select(a => $"{a.Name} {a.Attack}x{a.Attacks}"))}] hero {input.HeroAttack}x{input.HeroAttacksLeft} | hand [{string.Join(", ", input.Hand.Select(h => $"{h.Name} c{h.Cost} {h.Damage}{(h.IsWeapon ? "w" : "")}"))}]";
				if(log != _lastLethalLog) { _lastLethalLog = log; ProbeLog.Line("LETHAL", log); }
			}
			catch(Exception ex)
			{
				_widget.SetLethal(null, null, false);
				ProbeLog.Line("ERR", "lethal: " + ex.Message);
			}
		}

		private MenuItem BuildMenu()
		{
			var root = new MenuItem { Header = "Discard Odds" };
			_unlockItem = new MenuItem { Header = "Unlock widget (drag to move)", IsCheckable = true };
			_unlockItem.Click += (s, e) =>
			{
				if(_widget == null) return;
				_widget.SetUnlocked(_unlockItem.IsChecked);
				UpdateWidget(true);
			};
			var show = new MenuItem { Header = "Show widget", IsCheckable = true, IsChecked = _settings?.WidgetEnabled ?? true };
			show.Click += (s, e) => { if(_settings == null) return; _settings.WidgetEnabled = show.IsChecked; _settings.Save(); UpdateWidget(true); };
			var reset = new MenuItem { Header = "Reset widget position" };
			reset.Click += (s, e) =>
			{
				if(_settings == null || _widget == null) return;
				_settings.ResetPosition();
				_settings.Save();
				_widget.ApplyPosition();
			};
			var targets = new MenuItem { Header = "Choose target cards for this deck…" };
			targets.Click += (s, e) => OpenTargetsWindow();
			var reload = new MenuItem { Header = "Reload targets.json" };
			reload.Click += (s, e) => Guard(() => { LoadTargetConfig(); RefreshTargets(true); UpdateWidget(true); });
			var dump = new MenuItem { Header = "Write deck snapshot to log now" };
			dump.Click += (s, e) => Guard(() => _probes.DumpDeck("manual", true));
			var open = new MenuItem { Header = "Open log folder" };
			open.Click += (s, e) => OpenLogFolder();
			_updateItem = new MenuItem { Header = UpdateMenuLabel(), FontWeight = UpdateNeedsAttention() ? FontWeights.Bold : FontWeights.Normal };
			_updateItem.Click += (s, e) => OpenUrl(_updater?.Latest?.HtmlUrl ?? UpdateLogic.ReleasesPage);
			var lethal = new MenuItem { Header = "Show lethal check (your turn)", IsCheckable = true, IsChecked = _settings?.ShowLethalCheck ?? true };
			lethal.Click += (s, e) => { if(_settings == null) return; _settings.ShowLethalCheck = lethal.IsChecked; _settings.Save(); UpdateWidget(true); };
			var details = new MenuItem { Header = "Show details (reasons, Hand of Gul'dan)", IsCheckable = true, IsChecked = _settings?.ShowDetails ?? false };
			details.Click += (s, e) => { if(_settings == null) return; _settings.ShowDetails = details.IsChecked; _settings.Save(); UpdateWidget(true); };
			var compact = new MenuItem { Header = "Compact mode (smaller widget)", IsCheckable = true, IsChecked = _settings?.CompactMode ?? true };
			compact.Click += (s, e) => { if(_settings == null) return; _settings.CompactMode = compact.IsChecked; _settings.Save(); UpdateWidget(true); };
			var oneDrop = new MenuItem { Header = "Show one-drop odds (mulligan + turn 1)", IsCheckable = true, IsChecked = _settings?.ShowOneDrop ?? true };
			oneDrop.Click += (s, e) => { if(_settings == null) return; _settings.ShowOneDrop = oneDrop.IsChecked; _settings.Save(); UpdateWidget(true); };
			var checkOnStart = new MenuItem { Header = "Check for updates when HDT starts", IsCheckable = true, IsChecked = _settings?.CheckForUpdates ?? true };
			checkOnStart.Click += (s, e) => { if(_settings == null) return; _settings.CheckForUpdates = checkOnStart.IsChecked; _settings.Save(); };
			var auto = new MenuItem { Header = "Auto-update (download + install on next HDT restart)", IsCheckable = true, IsChecked = _settings?.AutoUpdate ?? true };
			auto.Click += (s, e) => { if(_settings == null) return; _settings.AutoUpdate = auto.IsChecked; _settings.Save(); };
			var checkNow = new MenuItem { Header = "Check for updates now" };
			checkNow.Click += (s, e) => _updater?.CheckInBackground(_settings?.AutoUpdate ?? true, true);
			var releases = new MenuItem { Header = "Open releases page" };
			releases.Click += (s, e) => OpenUrl(UpdateLogic.ReleasesPage);
			root.Items.Add(_updateItem);
			root.Items.Add(targets);
			root.Items.Add(reload);
			root.Items.Add(new Separator());
			root.Items.Add(_unlockItem);
			root.Items.Add(show);
			root.Items.Add(details);
			root.Items.Add(compact);
			root.Items.Add(lethal);
			root.Items.Add(oneDrop);
			root.Items.Add(reset);
			root.Items.Add(new Separator());
			root.Items.Add(dump);
			root.Items.Add(open);
			root.Items.Add(new Separator());
			root.Items.Add(checkOnStart);
			root.Items.Add(auto);
			root.Items.Add(checkNow);
			root.Items.Add(releases);
			return root;
		}

		private static void OpenUrl(string url)
		{
			// Only ever this repository's pages (UpdateLogic validates release URLs).
			if(url == null || !url.StartsWith(UpdateLogic.ReleasesPage, StringComparison.Ordinal)) url = UpdateLogic.ReleasesPage;
			try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
			catch(Exception ex) { ProbeLog.Line("ERR", "open url: " + ex.Message); }
		}

		private static void OpenLogFolder()
		{
			try
			{
				Directory.CreateDirectory(ProbeLog.LogDir);
				Process.Start("explorer.exe", "\"" + ProbeLog.LogDir + "\"");
			}
			catch(Exception ex)
			{
				ProbeLog.Line("ERR", "open folder: " + ex.Message);
			}
		}
	}
}
