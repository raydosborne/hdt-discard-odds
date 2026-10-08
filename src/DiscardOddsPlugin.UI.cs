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
		/// <summary>True once a 1-drop / extra opener hit was in hand after the mulligan this game (so playing it on turn 1
		/// keeps the ✓ line instead of turning into a miss). Reset at game start.</summary>
		private bool _openerHitLatched;

		/// <summary>
		/// Opening line (mulligan + your turn 1 only): the chance of having a turn-1 play, i.e. a 1-Cost card that isn't in
		/// the deck's "oneDropExclude" list (Discard preset: Wicked Whispers, Entropic Continuity) or one of its
		/// "openerExtraHits" (Discard preset: Cursed Catacombs). Odds use OneDropOdds (tossed cards can't come back as their
		/// own replacements). After the turn-1 draw: "✓" if you have one, else "Missed: X% chance" (how likely that miss was,
		/// given your mulligan). Hidden once your turn 1 is over.
		/// </summary>
		private void AddOneDropRows(List<WidgetRow> rows)
		{
			try
			{
				if(!_settings.ShowOneDrop || _probes.OpeningOver) return;
				var game = HdtApi.Core.Game;
				var player = game?.Player;
				if(player == null) return;
				var resolved = Targets.Resolved;
				var hits = WidgetPolicy.OpenerHitIds(_deckCards.Select(d => (d.Id, d.Cost)), resolved?.OneDropExclude, resolved?.OpenerExtraHits);
				var kList = _deckCards.Where(d => hits.Contains(d.Id)).Sum(d => d.Copies);
				if(kList == 0) return;
				var label = WidgetPolicy.OpenerLabel(_deckCards.Where(d => hits.Contains(d.Id) && d.Cost != 1).Select(d => d.Name));
				var what = label == "1-drop" ? "1-drops" : label.Replace("/", " / ") + " cards";
				var hand = _probes.LastHand.Where(h => !h.IsCoin).ToList();
				if(hand.Count == 0) return;
				bool mulliganDone;
				try { mulliganDone = game.IsMulliganDone; } catch { mulliganDone = true; }
				var hasHit = hand.Any(h => hits.Contains(h.CardId));
				if(mulliganDone && hasHit) _openerHitLatched = true;
				if(hasHit || _openerHitLatched)
				{
					rows.Add(new WidgetRow { Name = label + ":", NameIsLabel = true, Text = "in hand ✓", Bold = true, TextColor = WidgetColors.Hit });
					return;
				}
				var m = player.DeckCount;
				if(!mulliganDone)
				{
					var k = Math.Min(kList, m);
					var t = hand.Count;
					var by = _settings.CompactMode ? "" : " by T1";
					rows.Add(new WidgetRow { Name = $"{label}{by} · keep", NameIsLabel = true, Hit = 1 - OneDropOdds.PNoneByTurn1(m, k, 0), Detail = $"{k} {what} in the {m} cards left; only the turn-1 draw" });
					rows.Add(new WidgetRow { Name = $"{label}{by} · toss {t}", NameIsLabel = true, Hit = 1 - OneDropOdds.PNoneByTurn1(m, k, t), Detail = $"{t} replacements from the {m} cards left (tossed cards can't come back), then the turn-1 draw" });
					return;
				}
				if(!_probes.Turn1DrawSeen)
				{
					var rem = GameReader.RemainingDeck();
					var k = Math.Min(m, hits.Sum(id => rem.TryGetValue(id, out var v) ? v : 0));
					rows.Add(new WidgetRow { Name = _settings.CompactMode ? $"{label} T1" : $"{label} on T1 draw", NameIsLabel = true, Hit = m > 0 ? (double)k / m : 0, Detail = $"{k} {what} in the {m} cards left" });
					return;
				}
				// Turn 1, after the draw, none in hand: how likely this miss was, from the opening hand and your mulligan.
				var open = _probes.OpeningHand?.Where(h => !h.IsCoin).ToList();
				var after = _probes.HandAfterMulligan;
				var m0 = _probes.DeckCountAtOpening;
				string chance = null;
				if(open != null && after != null && m0 > 0)
				{
					var tossedCards = open.Where(h => after.All(a => a.EntityId != h.EntityId)).ToList();
					var k0 = Math.Max(0, kList - open.Count(h => hits.Contains(h.CardId)));
					var p = OneDropOdds.PNoneByTurn1(m0, k0, tossedCards.Count, tossedCards.Count(h => hits.Contains(h.CardId)));
					chance = OddsEngine.Pct(p);
				}
				rows.Add(new WidgetRow
				{
					Text = chance != null ? $"Missed: {chance} chance" : "Missed",
					Bold = true, TextColor = WidgetColors.Miss,
					Detail = $"no {what.TrimEnd('s')} in hand after the turn-1 draw"
				});
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
			var details = new MenuItem { Header = "Show details (reasons; off by default)", IsCheckable = true, IsChecked = _settings?.ShowDetails ?? false };
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
