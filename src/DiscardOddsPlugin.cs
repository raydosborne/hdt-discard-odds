using System;
using System.Diagnostics;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Hearthstone_Deck_Tracker.API;
using Hearthstone_Deck_Tracker.Plugins;
using HdtApi = Hearthstone_Deck_Tracker.API;

namespace DiscardOdds
{
	/// <summary>
	/// Discard Odds: live odds for your chosen target cards in any deck (next draw + "if you play it" hit/miss),
	/// plus the M0 probes for the Discard Warlock card models. Shows numbers only (no advice).
	/// Uses only live HDT game state; no HSReplay data.
	/// </summary>
	public class DiscardOddsPlugin : IPlugin
	{
		private Probes _probes;
		private PayoffWidget _widget;
		private PluginSettings _settings;
		private MenuItem _menu;
		private MenuItem _unlockItem;
		private MenuItem _updateItem;
		private Updater _updater;
		// HDT GameEvents can't be unsubscribed, and HDT only prunes a disabled plugin's handlers when an event fires
		// while it is disabled. A quick unload/load (HDT does this at startup) therefore leaves old handler sets
		// registered, and every event ran 4x. Each OnLoad registers one set stamped with a new generation; sets from
		// earlier generations are no-ops. (A plain "subscribe once" would go deaf after HDT pruned that one set.)
		private static int s_generation;
		// One updater per HDT process: the startup check runs once even if HDT loads the plugin several times.
		private static Updater s_updater;
		private static bool s_startupCheckDone;
		private bool _enabled;
		private DateTime _lastWidgetUpdate = DateTime.MinValue;
		private DateTime _lastGameStart = DateTime.MinValue;
		private TargetConfig _targetConfig = TargetConfig.CreateDefault();
		private string _targetsDeckKey;
		private List<DeckCardInfo> _deckCards = new List<DeckCardInfo>();

		private static string TargetsPath => Path.Combine(ProbeLog.RootDir, "targets.json");

		public string Name => "Discard Odds (M0 probe)";
		public string Description => "Live odds for the target cards you pick in any deck: next-draw chance, plus hit/miss if you play a draw or discard card now. Targets are per deck (Plugins menu, or targets.json in %AppData%\\HearthstoneDeckTracker\\DiscardOdds). Includes a Discard Warlock preset and M0 probe logging. Checks GitHub for updates on start (can be turned off). Numbers only, no advice. MIT license.";
		public string ButtonText => "Open log folder";
		public string Author => "Ray Osborne";
		// From the assembly (set by <Version> in the csproj, or by the release workflow from the git tag).
		public Version Version => UpdateLogic.Normalize(typeof(DiscardOddsPlugin).Assembly.GetName().Version);
		public MenuItem MenuItem => _menu ??= BuildMenu();

		public void OnLoad()
		{
			_enabled = true;
			_settings = PluginSettings.Load();
			_probes = new Probes { HdtConfigVerbose = _settings.VerboseDeckDump };
			LoadTargetConfig();
			RefreshTargets(true);
			ProbeLog.Line("PLUGIN", $"loaded v{Version} | HDT assembly {typeof(HdtApi.Core).Assembly.GetName().Version} | log {ProbeLog.CurrentTextLogPath}");

			// Subscribe from inside the IPlugin class (ActionList.Add identifies the plugin by the caller's type).
			var gen = Interlocked.Increment(ref s_generation);
			ProbeLog.Line("PLUGIN", $"event handlers registered (generation {gen}; earlier generations are inert)");
			void On(Action a) { if(gen == Volatile.Read(ref s_generation)) Guard(a); }
			GameEvents.OnGameStart.Add(() => On(() =>
			{
				if((DateTime.Now - _lastGameStart).TotalSeconds < 1) return; // guard against a doubled game-start event
				_lastGameStart = DateTime.Now;
				RefreshTargets(true); _probes.OnGameStart(); UpdateWidget(true);
			}));
			GameEvents.OnGameEnd.Add(() => On(() => _probes.OnGameEnd()));
			GameEvents.OnInMenu.Add(() => On(() => UpdateWidget(true)));
			GameEvents.OnTurnStart.Add(p => On(() => { _probes.OnTurnStart(p); UpdateWidget(true); }));
			GameEvents.OnPlayerDraw.Add(c => On(() => { _probes.OnPlayerDraw(c); UpdateWidget(true); }));
			GameEvents.OnPlayerGet.Add(c => On(() => { _probes.OnPlayerGet(c); UpdateWidget(true); }));
			GameEvents.OnPlayerPlay.Add(c => On(() => { _probes.OnPlayerPlay(c); UpdateWidget(true); }));
			GameEvents.OnPlayerHandDiscard.Add(c => On(() => { _probes.OnPlayerHandDiscard(c); UpdateWidget(true); }));
			GameEvents.OnPlayerMulligan.Add(c => On(() => { _probes.OnPlayerMulligan(c); UpdateWidget(true); }));
			GameEvents.OnPlayerDeckDiscard.Add(c => On(() => UpdateWidget(true)));
			GameEvents.OnPlayerCreateInDeck.Add(c => On(() => UpdateWidget(true)));
			GameEvents.OnPlayerPlayToDeck.Add(c => On(() => UpdateWidget(true)));

			try
			{
				_widget = new PayoffWidget(_settings);
				_widget.Attach();
				UpdateWidget(true);
			}
			catch(Exception ex)
			{
				ProbeLog.Line("ERR", "widget attach failed: " + ex);
			}

			// Update check: GitHub Releases of this repo only, in the background; silent if offline.
			_updater = s_updater ??= new Updater(Version);
			_updater.Changed = OnUpdateNotice; // assignment, not +=, so reloads don't stack callbacks
			if(_settings.CheckForUpdates && !s_startupCheckDone)
			{
				s_startupCheckDone = true;
				_updater.CheckInBackground(_settings.AutoUpdate, false);
			}
		}

		private void OnUpdateNotice()
		{
			if(!_enabled) return;
			UpdateWidget(true);
			try
			{
				var item = _updateItem;
				if(item == null) return;
				void Apply()
				{
					var n = _updater?.Notice;
					item.Header = n ?? "";
					item.Visibility = n == null ? Visibility.Collapsed : Visibility.Visible;
				}
				if(item.Dispatcher.CheckAccess()) Apply();
				else item.Dispatcher.BeginInvoke(new Action(Apply));
			}
			catch(Exception ex) { ProbeLog.Line("ERR", "update notice: " + ex.Message); }
		}

		public void OnUnload()
		{
			_enabled = false;
			Interlocked.Increment(ref s_generation); // this load's handlers go inert even if HDT keeps them
			try { _widget?.Detach(); } catch { }
			_widget = null;
			_settings?.Save();
			ProbeLog.Line("PLUGIN", "unloaded");
			ProbeLog.Close();
		}

		public void OnButtonPress() => OpenLogFolder();

		public void OnUpdate()
		{
			if(!_enabled) return;
			Guard(() =>
			{
				_probes.Tick();
				UpdateWidget(false);
				ProbeLog.Flush();
			});
		}

		private void Guard(Action a)
		{
			if(!_enabled) return;
			try { a(); }
			catch(Exception ex) { ProbeLog.Line("ERR", ex.ToString()); }
		}

		// ------------------------------------------------------------------ targets

		private void LoadTargetConfig()
		{
			_targetConfig = TargetConfig.Load(TargetsPath, out var error);
			if(error != null)
				ProbeLog.Line("TARGETS", $"targets.json could not be read ({error}); using built-in presets until it is fixed. The file was not changed.");
			else
				ProbeLog.Line("TARGETS", $"loaded {TargetsPath}: {_targetConfig.Decks.Count} deck list(s), {_targetConfig.Presets.Count} preset(s)");
		}

		/// <summary>Re-resolves the active deck's targets when the deck changes (or when forced).</summary>
		private void RefreshTargets(bool force)
		{
			var (id, name, cards) = GameReader.ActiveDeck();
			var key = (id ?? "") + "|" + (name ?? "") + "|" + cards.Sum(c => c.Copies);
			if(!force && key == _targetsDeckKey) return;
			_targetsDeckKey = key;
			_deckCards = cards;
			var resolved = _targetConfig.Resolve(id, name, cards.Select(c => c.Id));
			Targets.Resolved = resolved;
			Targets.Current = resolved.Ids;
			ProbeLog.Line("TARGETS", $"deck '{name}' ({id}): {resolved.Describe()} -> [{string.Join(",", resolved.Ids)}]");
		}

		private void OpenTargetsWindow()
		{
			try
			{
				LoadTargetConfig(); // pick up hand edits first
				var (id, name, cards) = GameReader.ActiveDeck();
				var resolved = _targetConfig.Resolve(id, name, cards.Select(c => c.Id));
				var w = new TargetsWindow(_targetConfig, id, name ?? "(none)", cards, resolved.Ids, resolved.Describe(), () =>
				{
					_targetConfig.Save(TargetsPath);
					RefreshTargets(true);
					UpdateWidget(true);
				});
				w.Show();
			}
			catch(Exception ex)
			{
				ProbeLog.Line("ERR", "targets window: " + ex);
			}
		}

		private void UpdateWidget(bool force)
		{
			if(_widget == null) return;
			var now = DateTime.Now;
			if(!force && (now - _lastWidgetUpdate).TotalMilliseconds < 500) return;
			_lastWidgetUpdate = now;

			// Every widget state goes through Show so an update notice is always appended as the last line.
			void Show(string main, string sub)
			{
				var notice = _updater?.Notice;
				_widget.SetText(main, notice == null ? sub : sub + "\n⟳ " + notice);
			}

			void Apply()
			{
				var game = HdtApi.Core.Game;
				var inGame = game != null && !game.IsInMenu && game.Player != null;
				// A pending update notice also shows the widget in menus, so "restart HDT" is seen.
				var visible = _settings.WidgetEnabled && (inGame || _settings.ShowInMenus || _widget.Unlocked || _updater?.Notice != null);
				_widget.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
				if(!visible) return;
				if(!inGame)
				{
					_widget.SetLethal(null, null, false);
					Show("Targets left in deck: –", "waiting for a game");
					return;
				}
				if(Hearthstone_Deck_Tracker.DeckList.Instance.ActiveDeck == null)
				{
					Show("Targets left in deck: –", "select your deck in HDT");
					return;
				}
				RefreshTargets(false);
				ShowLethal();
				// HDT updates PlayerCardList a moment after DeckCount; keep the last numbers until it catches up.
				if(!GameReader.DeckSettled())
				{
					if(!_wasUnsettled) { _wasUnsettled = true; _unsettledSince = DateTime.Now; }
					if((DateTime.Now - _unsettledSince).TotalMilliseconds < 400) return; // then show what we have
				}
				else _wasUnsettled = false;
				var lines = new List<string>();
				if(Targets.Current.Count == 0)
				{
					Show("Next draw: no target cards set for this deck", "Plugins → Discard Odds → Choose target cards…");
					return;
				}
				var (n, m, known) = GameReader.PayoffCount(Targets.Current);
				var pNext = m > 0 ? (double)n / m : 0;
				var unknown = m - known;
				// Target list: only targets that are in the active deck list, with copies left / copies in list.
				var remaining = GameReader.RemainingDeck();
				var inList = _deckCards.Where(c => Targets.Current.Contains(c.Id)).ToList();
				lines.Add(inList.Count == 0
					? "none of this deck's targets are in the deck list"
					: "Targets: " + string.Join(" · ", inList.Select(c => $"{c.Name} {(remaining.TryGetValue(c.Id, out var left) ? left : 0)}/{c.Copies}")));
				// "if you play this card" hit/miss odds for every odds card in hand (specific rules + generic draw text).
				var state = GameReader.BuildOddsState(_probes.LastHand, Targets.Current);
				foreach(var o in OddsEngine.ForHand(state, OddsRule.CardRules))
					lines.Add($"{o.Name}: hit {OddsEngine.Pct(o.Hit)} · miss {OddsEngine.Pct(o.Miss)}{(o.Approx ? " ≈" : "")}  ({o.Detail})");
				var lastHand = _probes.LastHand;
				// Hand of Gul'dan: draws 3 when discarded. Shown when something could discard it this turn.
				var hog = lastHand.FirstOrDefault(h => h.CardId == "BT_300");
				var outletInHand = lastHand.Any(h => h.CardId != null && CardIds.OutletRule.ContainsKey(h.CardId) && h.CardId != CardIds.Platysaur);
				if(hog != null && (outletInHand || hog.HasTempEnchant))
					lines.Add($"Hand of Gul'dan if discarded: draws 3 · ≥1 target {OddsEngine.Pct(OddsEngine.PAtLeastOne(n, m, 3))}{(hog.HasTempEnchant ? " (Temporary: burns at end of turn)" : "")}");
				// Duke of Below: 2/2 + 2/2 per card discarded this game (EntitiesDiscardedFromHand, 4/4 in the live test).
				var discards = GameReader.Player?.EntitiesDiscardedFromHand.Count ?? 0;
				if(lastHand.Any(h => h.CardId == CardIds.Duke))
					lines.Add($"Duke of Below: {2 + 2 * discards}/{2 + 2 * discards} ({discards} discarded this game)");
				var cwd = GameReader.CastsWhenDrawnInDeck();
				if(cwd > 0)
					lines.Add($"(+{cwd} Casts-When-Drawn card(s) in deck not counted in M)");
				foreach(var l in _probes.LivePlatysaurLinks())
					lines.Add($"Platysaur holds {l.drawnName}{(l.payoff ? " (target: discarded when it dies)" : " (not a target)")}");
				if(lines.Count == 1)
					lines.Add("no draw/discard cards in hand");
				if(unknown > 0)
					lines.Add($"({unknown} unknown card(s) in deck counted as misses)");
				Show(
					$"Targets left in deck: {n} of {m} ({OddsEngine.Pct(pNext)} next draw)",
					string.Join("\n", lines));
			}

			try
			{
				if(_widget.Dispatcher.CheckAccess()) Apply();
				else _widget.Dispatcher.BeginInvoke(new Action(() => { try { Apply(); } catch(Exception ex) { ProbeLog.Line("ERR", "widget: " + ex.Message); } }));
			}
			catch(Exception ex)
			{
				ProbeLog.Line("ERR", "UpdateWidget: " + ex.Message);
			}
		}

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
			_updateItem = new MenuItem { Header = _updater?.Notice ?? "", Visibility = _updater?.Notice == null ? Visibility.Collapsed : Visibility.Visible, FontWeight = FontWeights.Bold };
			_updateItem.Click += (s, e) => OpenUrl(_updater?.Latest?.HtmlUrl ?? UpdateLogic.ReleasesPage);
			var lethal = new MenuItem { Header = "Show lethal check (your turn)", IsCheckable = true, IsChecked = _settings?.ShowLethalCheck ?? true };
			lethal.Click += (s, e) => { if(_settings == null) return; _settings.ShowLethalCheck = lethal.IsChecked; _settings.Save(); UpdateWidget(true); };
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
			root.Items.Add(lethal);
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
