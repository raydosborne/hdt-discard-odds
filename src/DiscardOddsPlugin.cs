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
	public partial class DiscardOddsPlugin : IPlugin
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
				_openerHitLatched = false;
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
					item.Header = UpdateMenuLabel();
					item.FontWeight = UpdateNeedsAttention() ? FontWeights.Bold : FontWeights.Normal;
				}
				if(item.Dispatcher.CheckAccess()) Apply();
				else item.Dispatcher.BeginInvoke(new Action(Apply));
			}
			catch(Exception ex) { ProbeLog.Line("ERR", "update notice: " + ex.Message); }
		}

		/// <summary>Update status, shown only as the first Plugins-menu item (never on the overlay).</summary>
		private string UpdateMenuLabel()
		{
			var n = _updater?.Notice;
			if(n != null) return n;
			return _settings?.CheckForUpdates == false ? $"v{Version.ToString(3)} (update check off)" : $"v{Version.ToString(3)} · checking for updates…";
		}

		private bool UpdateNeedsAttention() => _updater?.Notice?.StartsWith("Update ", StringComparison.Ordinal) == true;

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
			ProbeLog.Line("TARGETS", $"deck '{name}' ({id}): {resolved.Describe()} -> [{string.Join(",", resolved.Ids)}]; one-drop excludes [{string.Join(",", resolved.OneDropExclude)}]");
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

			// Status states: one bold line plus small hint lines. (Update status is in the Plugins menu, never on screen.)
			void Show(string main, string sub, List<WidgetRow> extra = null)
			{
				var c = new WidgetContent { Header = new WidgetRow { Text = main, Bold = true, TextColor = WidgetColors.Text } };
				foreach(var l in (sub ?? "").Split('\n'))
					if(l.Length > 0) c.Rows.Add(new WidgetRow { Text = l, Small = true });
				if(extra != null) c.Rows.AddRange(extra);
				_widget.SetContent(c);
			}

			void Apply()
			{
				var game = HdtApi.Core.Game;
				var inGame = game != null && !game.IsInMenu && game.Player != null;
				var visible = _settings.WidgetEnabled && (inGame || _settings.ShowInMenus || _widget.Unlocked);
				_widget.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
				if(!visible) return;
				if(!inGame)
				{
					_widget.SetLethal(null, null, false);
					Show("Discard Odds", "waiting for a game");
					return;
				}
				if(Hearthstone_Deck_Tracker.DeckList.Instance.ActiveDeck == null)
				{
					Show("Discard Odds", "select your deck in HDT");
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
				var oneDrop = new List<WidgetRow>();
				AddOneDropRows(oneDrop);
				if(Targets.Current.Count == 0)
				{
					Show("No target cards set for this deck", "Plugins → Discard Odds → Choose target cards…", oneDrop);
					return;
				}
				var (_, m, known) = GameReader.PayoffCount(Targets.Current);
				var unknown = m - known;
				// No "Next X% · n/m" header and no "targets left n/m" counts: HDT's own deck list already shows remaining copies.
				var c = new WidgetContent();
				c.Rows.AddRange(oneDrop);
				// One line per odds card in hand (one per entity, never two): "if you play it" hit / miss.
				// WidgetPolicy decides placement for every line: Ocular Occultist / Gemstone Hoarder (you choose the discard) and
				// Hand of Gul'dan are never added, in any mode, not even with Show details.
				// Chronoclaws (and Expired Merchant when certain) show only "→ the card(s) it would discard".
				var lastHand = _probes.LastHand;
				var state = GameReader.BuildOddsState(lastHand, Targets.Current);
				var rowEntities = new HashSet<int>();
				var rowCards = new HashSet<string>();
				foreach(var o in OddsEngine.ForHand(state, OddsRule.CardRules))
				{
					var place = WidgetPolicy.Place(o);
					if(place == WidgetPlacement.Never) continue;
					if(!rowEntities.Add(o.EntityId) || !rowCards.Add(o.CardId)) continue;
					c.Rows.Add(new WidgetRow { CardId = o.CardId, Name = o.Name, Hit = o.Hit, Approx = o.Approx, Detail = o.Detail, DiscardRule = o.DiscardRule, DiscardNames = o.DiscardNames, DetailOnly = place == WidgetPlacement.DetailsOnly, NoOdds = o.NoOdds });
				}
				// Duke of Below: 2/2 + 2/2 per card discarded this game (EntitiesDiscardedFromHand, 4/4 in the live test).
				var discards = GameReader.Player?.EntitiesDiscardedFromHand.Count ?? 0;
				if(lastHand.Any(h => h.CardId == CardIds.Duke))
					c.Rows.Add(new WidgetRow { Name = "Duke of Below", Text = $"{2 + 2 * discards}/{2 + 2 * discards}", TextColor = WidgetColors.Text });
				foreach(var l in _probes.LivePlatysaurLinks())
				{
					// A Platysaur holding a hidden card (Ocular Occultist, Gemstone Hoarder, Hand of Gul'dan) never names it.
					var place = WidgetPolicy.Place(null, l.drawnName);
					if(place == WidgetPlacement.Never) continue;
					c.Rows.Add(new WidgetRow { Name = "Platysaur", Text = $"holds {l.drawnName}{(l.payoff ? " (target)" : "")}", Bold = l.payoff, TextColor = WidgetColors.Text, DetailOnly = place == WidgetPlacement.DetailsOnly });
				}
				var cwd = GameReader.CastsWhenDrawnInDeck();
				if(cwd > 0)
					c.Rows.Add(new WidgetRow { Text = $"+{cwd} Casts-When-Drawn card(s) in deck not counted", Small = true, DetailOnly = true, TextColor = WidgetColors.Dim });
				if(unknown > 0)
					c.Rows.Add(new WidgetRow { Text = $"{unknown} unknown card(s) in deck counted as misses", Small = true, DetailOnly = true, TextColor = WidgetColors.Dim });
				_widget.SetContent(c);
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
	}
}
