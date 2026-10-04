using System;
using System.Collections.Generic;
using System.Linq;
using HearthDb.Enums;
using Hearthstone_Deck_Tracker.Enums;
using Hearthstone_Deck_Tracker.Hearthstone;
using Hearthstone_Deck_Tracker.Hearthstone.Entities;
using HdtApi = Hearthstone_Deck_Tracker.API;
using HdtConfig = Hearthstone_Deck_Tracker.Config;
using HdtDeckList = Hearthstone_Deck_Tracker.DeckList;

namespace DiscardOdds
{
	/// <summary>
	/// M0 probes. Each one logs evidence for an open question about the game rules:
	///  [TEMP]  how Temporary cards are marked on hand entities (GBL_999e attachment vs GameTag 785/GHOSTLY)
	///  [DUKE]  where the "cards discarded this game" count lives (EntitiesDiscardedFromHand vs a tag) and Duke's live stats
	///  [TIE]   which card lowest/highest-cost outlets discard when costs tie
	///  [DECK]  what PlayerCardList contains under RemoveCardsFromDeck / HighlightCardsInHand / ShowPlayerGet
	///  [MULL]  mulligan redraw: can a tossed entity / same card come straight back
	///  [ODDS]  the core feature: for every rule card played, the predicted hit/miss odds at that moment and the real outcome
	///          (cards drawn / discovered-offered / discarded), so predictions can be calibrated against reality
	///  [PLATY] Platysaur: N/M before its battlecry draw, which card it drew, how HDT/the game links Platysaur to "it"
	///          (TLC_603e* enchantments + tag changes), and the outcome (drawn card played first vs discarded on death)
	/// Every public method is called from GameEvents handlers or OnUpdate and is exception-safe.
	/// </summary>
	public class Probes
	{
		private const double OutletWindowSeconds = 5.0;
		private const double TempCheckDelaySeconds = 1.5;
		private const double ChoiceWindowSeconds = 30.0; // "choose"/Discover effects wait for the player's pick

		private static double WindowFor(string rule) => rule == "choose" || rule == "look3" ? ChoiceWindowSeconds : OutletWindowSeconds;

		private static double WindowFor(OddsKind kind) => kind == OddsKind.DiscardChoose || kind == OddsKind.DiscardLook3 || kind == OddsKind.DiscoverFromDeck
			? ChoiceWindowSeconds : OutletWindowSeconds;

		private List<HandCard> _lastHand = new List<HandCard>();
		private readonly Dictionary<int, PendingEntry> _newHandEntries = new Dictionary<int, PendingEntry>();
		private readonly HashSet<int> _tempLoggedEntities = new HashSet<int>();
		private PendingOutlet _pendingOutlet;
		private (string cardId, DateTime at, int turn) _lastPlayed;
		private int _turn;
		private bool _myTurn;

		// duke / discard-count probe
		private int _lastDiscardCount = -1;
		private Dictionary<GameTag, int> _lastPlayerEntityTags = new Dictionary<GameTag, int>();
		private Dictionary<GameTag, int> _lastHeroTags = new Dictionary<GameTag, int>();
		private readonly Dictionary<int, Dictionary<GameTag, int>> _lastDukeTags = new Dictionary<int, Dictionary<GameTag, int>>();
		private int _discardEventsThisGame;

		// mulligan probe
		private List<HandCard> _openingHand;
		private readonly List<string> _mulliganTossEvents = new List<string>();
		private bool _mulliganLogged;
		private int _deckCountAtOpening;
		private bool _gameActive;
		// [DECK] draw lines are logged from the first settled tick after the draw (PlayerCardList lags DeckCount in the handler).
		private readonly List<(string name, string id, DateTime at)> _pendingDrawLogs = new List<(string, string, DateTime)>();
		// Chronoclaws only discards "after your hero attacks": remember the last hero attack and the hand just before it.
		private int _heroAttacksSeen;
		private DateTime _lastHeroAttackAt = DateTime.MinValue;
		private List<HandCard> _handAtHeroAttack;
		private const double DrawSettleTimeoutSeconds = 0.3;
		private const double HeroAttackWindowSeconds = 3.0;

		// odds prediction vs outcome
		private readonly List<Prediction> _predictions = new List<Prediction>();
		public List<HandCard> LastHand => _lastHand;

		private class Prediction
		{
			public OddsRule Rule;
			public CardOdds Odds;
			public DateTime At;
			public int Turn;
			public readonly List<string> Drawn = new List<string>();
			public bool Resolved;
			public bool IsDraw => Rule.Kind == OddsKind.DrawK || Rule.Kind == OddsKind.DrawShadowSpell || Rule.Kind == OddsKind.DrawThenDiscardIt;
			public int DrawsExpected => Rule.Kind == OddsKind.DrawK ? Rule.K : 1;
		}

		// platysaur probe
		private readonly List<PlatyTrack> _platys = new List<PlatyTrack>();

		private class PlatyTrack
		{
			public int PlatyEntityId;
			public DateTime PlayedAt;
			public int Turn;
			public int PayoffsBefore;
			public int DeckBefore;
			public int DrawnEntityId;
			public string DrawnCardId;
			public bool DrawnIsPayoff;
			public bool LinkLogged;
			public bool LeftPlayLogged;
			public bool Resolved;
			public Dictionary<GameTag, int> TagsAtPlay;
			public HashSet<int> HandAtPlay;   // hand entity ids just before Platysaur was played (baseline for "the drawn card")
			public bool DrawSeen;             // OnPlayerDraw fired for this Platysaur's battlecry
			public bool LateResolveLogged;
			public bool EnchantLinked;
		}

		private class PendingOutlet
		{
			public string CardId;
			public string Rule;
			public List<HandCard> HandBefore;
			public DateTime At;
			public int Turn;
			public int Resolved;
		}

		private class PendingEntry
		{
			public HandCard Card;
			public DateTime EnteredAt;
			public string Context;
			public bool ExpectedTemp;
		}

		// ------------------------------------------------------------------ lifecycle

		public void OnGameStart()
		{
			ProbeLog.GameIndex++;
			_gameActive = true;
			_lastHand = new List<HandCard>();
			_newHandEntries.Clear();
			_tempLoggedEntities.Clear();
			_pendingOutlet = null;
			_lastPlayed = (null, DateTime.MinValue, 0);
			_turn = 0;
			_myTurn = false;
			_lastDiscardCount = -1;
			_lastPlayerEntityTags = new Dictionary<GameTag, int>();
			_lastHeroTags = new Dictionary<GameTag, int>();
			_lastDukeTags.Clear();
			_discardEventsThisGame = 0;
			_openingHand = null;
			_mulliganTossEvents.Clear();
			_mulliganLogged = false;
			_deckCountAtOpening = 0;
			_platys.Clear();
			_predictions.Clear();

			var deck = HdtDeckList.Instance.ActiveDeck;
			var game = HdtApi.Core.Game;
			ProbeLog.Line("GAME", $"===== game #{ProbeLog.GameIndex} start | deck='{deck?.Name}' id={deck?.DeckId} | type={Safe(() => game.CurrentGameType.ToString())} format={Safe(() => game.CurrentFormat?.ToString())} | HDT {typeof(HdtApi.Core).Assembly.GetName().Version} | targets: {Targets.Resolved.Describe()} [{string.Join(",", Targets.Current)}]");
			ProbeLog.Record("game_start", new Dictionary<string, object>
			{
				["deck_name"] = deck?.Name, ["deck_id"] = deck?.DeckId.ToString(),
				["game_type"] = Safe(() => game.CurrentGameType.ToString()),
				["format"] = Safe(() => game.CurrentFormat?.ToString()),
				["hdt_version"] = typeof(HdtApi.Core).Assembly.GetName().Version?.ToString(),
				["config"] = ConfigFlags(),
				["targets"] = Targets.Current.ToList(), ["targets_source"] = Targets.Resolved.Source
			});
		}

		public void OnGameEnd()
		{
			if(!_gameActive) return;
			_gameActive = false;
			LogDuke("game_end", force: true);
			foreach(var pt in _platys.Where(x => !x.Resolved))
				LogPlatyOutcome(pt, "game_end_unresolved", null);
			ProbeLog.Line("GAME", $"===== game #{ProbeLog.GameIndex} end | discard events seen={_discardEventsThisGame}");
			ProbeLog.Record("game_end", new Dictionary<string, object> { ["discard_events"] = _discardEventsThisGame });
		}

		public void OnTurnStart(ActivePlayer who)
		{
			_myTurn = who == ActivePlayer.Player;
			try { _turn = HdtApi.Core.Game.GetTurnNumber(); } catch { }
			ProbeLog.Line("TURN", $"turn {_turn} start: {who} | hand={Fmt(_lastHand)}");
			if(_myTurn)
				DumpDeck("own_turn_start", HdtConfigVerbose);
			LogDuke("turn_start", force: false);
		}

		public bool HdtConfigVerbose { get; set; } = true;

		// ------------------------------------------------------------------ periodic

		/// <summary>Called from IPlugin.OnUpdate (~100 ms).</summary>
		public void Tick()
		{
			var game = HdtApi.Core.Game;
			if(game == null || game.IsInMenu || game.Player == null)
				return;
			var now = DateTime.Now;
			var hand = GameReader.SnapshotHand();

			// --- mulligan probe: keep the latest pre-toss opening hand; log once mulligan is done
			var mulliganDone = Safe(() => game.IsMulliganDone);
			if(!mulliganDone && _mulliganTossEvents.Count == 0 && hand.Count >= 3)
			{
				if(_openingHand == null || _openingHand.Count != hand.Count)
				{
					_openingHand = hand;
					_deckCountAtOpening = Safe(() => game.Player.DeckCount);
					ProbeLog.Line("MULL", $"opening hand ({hand.Count} cards, deck {_deckCountAtOpening}): {Fmt(hand)}");
				}
			}
			if(mulliganDone && !_mulliganLogged && _openingHand != null)
			{
				_mulliganLogged = true;
				LogMulliganResult(hand);
				DumpDeck("mulligan_done", true);
			}

			// --- new hand entries + delayed Temporary marker check
			var prevIds = new HashSet<int>(_lastHand.Select(h => h.EntityId));
			foreach(var h in hand.Where(h => !prevIds.Contains(h.EntityId) && !_newHandEntries.ContainsKey(h.EntityId)))
			{
				var ctxWindow = _lastPlayed.cardId == CardIds.Catacombs ? ChoiceWindowSeconds : OutletWindowSeconds;
				var ctx = _lastPlayed.cardId != null && (now - _lastPlayed.at).TotalSeconds < ctxWindow && _lastPlayed.turn == _turn
					? _lastPlayed.cardId : null;
				var expectedTemp = ctx == CardIds.Soularium || ctx == CardIds.Catacombs || ctx == CardIds.SketchArtist;
				_newHandEntries[h.EntityId] = new PendingEntry { Card = h, EnteredAt = now, Context = ctx, ExpectedTemp = expectedTemp };
			}
			foreach(var kv in _newHandEntries.ToList())
			{
				if((now - kv.Value.EnteredAt).TotalSeconds < TempCheckDelaySeconds)
					continue;
				_newHandEntries.Remove(kv.Key);
				var current = hand.FirstOrDefault(x => x.EntityId == kv.Key);
				if(current == null)
					continue; // left hand already
				var marked = current.HasTempEnchant; // GBL_999e only (19/19 in the first live test; GHOSTLY never used)
				if(!marked && !kv.Value.ExpectedTemp)
					continue; // ordinary card, nothing to learn
				ProbeLog.Line("TEMP", $"{current.Short} entered hand after {Name(kv.Value.Context) ?? "-"} | expectedTemp={kv.Value.ExpectedTemp} GBL_999e={current.HasTempEnchant} GHOSTLY(785)={current.HasGhostlyTag} attached=[{string.Join(",", current.Attached)}]");
				ProbeLog.Record("temp_marker", new Dictionary<string, object>
				{
					["card"] = current.ToDict(), ["source"] = kv.Value.Context, ["expected_temp"] = kv.Value.ExpectedTemp,
					["gbl_999e"] = current.HasTempEnchant, ["ghostly_785"] = current.HasGhostlyTag, ["turn"] = _turn
				});
				if(marked && _tempLoggedEntities.Add(current.EntityId))
					DumpEntityTags("TEMP", current.EntityId);
			}

			TickHeroAttacks();
			TickDrawLogs(now);
			TickPlatysaur(hand);
			TickPredictions(now);
			_lastHand = hand;
		}

		private static int HeroAttacksNow()
		{
			try
			{
				var hero = GameReader.Player?.Board.FirstOrDefault(e => e.IsHero);
				return hero?.GetTag(GameTag.NUM_ATTACKS_THIS_TURN) ?? 0;
			}
			catch { return 0; }
		}

		private void TickHeroAttacks()
		{
			var a = HeroAttacksNow();
			if(a > _heroAttacksSeen)
			{
				_lastHeroAttackAt = DateTime.Now;
				_handAtHeroAttack = _lastHand; // the hand before this tick = just before the attack's trigger
			}
			_heroAttacksSeen = a; // drops back to 0 at turn start
		}

		private void TickDrawLogs(DateTime now)
		{
			if(_pendingDrawLogs.Count == 0) return;
			var settled = GameReader.DeckSettled();
			if(!settled && (now - _pendingDrawLogs[0].at).TotalSeconds < DrawSettleTimeoutSeconds) return;
			try
			{
				var (n, m, known) = GameReader.PayoffCount(Targets.Current);
				var cwd = GameReader.CastsWhenDrawnInDeck();
				foreach(var d in _pendingDrawLogs)
					ProbeLog.Line("DECK", $"draw {d.name} ({d.id}) | settled={settled} M={m} knownInList={known} unknown={m - known} targetsLeft N={n}{(cwd > 0 ? $" (+{cwd} Casts-When-Drawn skipped)" : "")} | {FlagsShort()}");
			}
			catch(Exception ex) { ProbeLog.Line("ERR", "draw log: " + ex.Message); }
			_pendingDrawLogs.Clear();
		}

		// ------------------------------------------------------------------ game events

		public void OnPlayerPlay(Card card)
		{
			var now = DateTime.Now;
			_lastPlayed = (card?.Id, now, _turn);
			if(card?.Id == CardIds.Platysaur)
				StartPlatyTrack(now);
			// Specific rules, plus generic unconditional "draw N" cards (conditional draws may not happen on play).
			var oddsRule = card?.Id == null || card.Id == CardIds.Chronoclaws ? null : OddsRule.ForCard(card.Id, card.Name, GameReader.CardText(card.Id));
			if(oddsRule != null && !(oddsRule.Generic && oddsRule.Approx))
				StartPrediction(oddsRule, card, now);
			if(card?.Id != null && CardIds.OutletRule.TryGetValue(card.Id, out var rule) && rule != "drawn")
			{
				// The card has already left the hand in HDT's state; _lastHand is the snapshot from just before.
				var current = new HashSet<int>(GameReader.SnapshotHand().Select(h => h.EntityId));
				var played = _lastHand.FirstOrDefault(h => h.CardId == card.Id && !current.Contains(h.EntityId));
				var before = _lastHand.Where(h => played == null || h.EntityId != played.EntityId).ToList();
				_pendingOutlet = new PendingOutlet { CardId = card.Id, Rule = rule, HandBefore = before, At = now, Turn = _turn };
				ProbeLog.Line("TIE", $"outlet played: {card.Name} rule={rule} | hand before (excl. outlet): {Fmt(before)} | {TieSummary(before)}");
			}
			if(card?.Id == CardIds.Duke)
				LogDuke("duke_played", force: true);
			RefreshLastHandSoon();
		}

		public void OnPlayerDraw(Card card)
		{
			LinkPlatyDraw(card);
			FeedDrawPrediction(card);
			// N/M are not read here: HDT's PlayerCardList still holds the card just drawn (DeckCount has already dropped).
			_pendingDrawLogs.Add((card?.Name, card?.Id, DateTime.Now));
		}

		public void OnPlayerGet(Card card)
		{
			ProbeLog.Line("HAND", $"get (created in hand) {card?.Name} ({card?.Id})");
		}

		public void OnPlayerMulligan(Card card)
		{
			_mulliganTossEvents.Add(card?.Id);
			ProbeLog.Line("MULL", $"toss event: {card?.Name} ({card?.Id})");
		}

		public void OnPlayerHandDiscard(Card card)
		{
			_discardEventsThisGame++;
			var now = DateTime.Now;
			try
			{
				var currentHand = GameReader.SnapshotHand();
				var currentIds = new HashSet<int>(currentHand.Select(h => h.EntityId));

				// Which entity left? Prefer the outlet's pre-play snapshot if one is pending.
				var outlet = _pendingOutlet != null && (now - _pendingOutlet.At).TotalSeconds < WindowFor(_pendingOutlet.Rule) && _pendingOutlet.Turn == _turn && _pendingOutlet.Resolved == 0
					? _pendingOutlet : null;
				var chronoclawsEquipped = PlayerHasWeapon(CardIds.Chronoclaws);
				// Chronoclaws only after a real hero attack: seen by the tick, or happening right now (same log batch).
				var attackingNow = HeroAttacksNow() > _heroAttacksSeen;
				var heroAttacked = attackingNow || (now - _lastHeroAttackAt).TotalSeconds < HeroAttackWindowSeconds;
				var chronoclaws = outlet == null && chronoclawsEquipped && heroAttacked;
				var before = outlet?.HandBefore ?? (chronoclaws && !attackingNow && _handAtHeroAttack != null ? _handAtHeroAttack : _lastHand);
				var discarded = before.FirstOrDefault(h => h.CardId == card?.Id && !currentIds.Contains(h.EntityId))
				               ?? before.FirstOrDefault(h => h.CardId == card?.Id);

				string cause;
				string rule;
				// Attribution order (findings §4.4): Platysaur link -> pending outlet -> GBL_999e -> Chronoclaws (only
				// after a hero attack) -> unattributed.
				var platy = discarded == null ? null : _platys.FirstOrDefault(x => !x.Resolved && x.DrawnEntityId > 0 && x.DrawnEntityId == discarded.EntityId);
				var platyInPlay = platy != null && PlatyInPlay(platy);
				if(platy != null && !platyInPlay)
				{
					cause = CardIds.Platysaur;
					rule = "drawn";
					LogPlatyOutcome(platy, "discarded_on_death", discarded);
				}
				else if(outlet != null)
				{
					cause = outlet.CardId;
					rule = outlet.Rule;
					outlet.Resolved++;
				}
				else if(discarded != null && discarded.HasTempEnchant)
				{
					cause = "temporary_expiry";
					rule = "temporary";
				}
				else if(chronoclaws)
				{
					cause = CardIds.Chronoclaws;
					rule = "highest";
				}
				else
				{
					cause = "unattributed";
					rule = "unknown";
				}

				if(platy != null && platyInPlay)
					LogPlatyOutcome(platy, "discarded_by_" + (Name(cause) ?? cause), discarded); // Platysaur alive: something else took it

				var candidates = before.Where(h => !h.IsCoin || rule == "lowest" || rule == "highest").ToList();
				var min = candidates.Count > 0 ? candidates.Min(h => h.Cost) : 0;
				var max = candidates.Count > 0 ? candidates.Max(h => h.Cost) : 0;
				var tieSet = rule == "lowest" ? candidates.Where(h => h.Cost == min).ToList()
					: rule == "highest" ? candidates.Where(h => h.Cost == max).ToList()
					: new List<HandCard>();
				var inTieSet = discarded != null && tieSet.Any(t => t.EntityId == discarded.EntityId);

				ProbeLog.Line("TIE", $"discard: {discarded?.Short ?? card?.Name} | cause={Name(cause) ?? cause} rule={rule} | min={min} max={max} tieSet[{tieSet.Count}]={Fmt(tieSet)} inTieSet={inTieSet} | payoff={discarded?.IsTarget} | myTurn={_myTurn}");
				ProbeLog.Record("discard", new Dictionary<string, object>
				{
					["card"] = discarded?.ToDict() ?? (object)card?.Id,
					["cause"] = cause, ["rule"] = rule, ["turn"] = _turn, ["my_turn"] = _myTurn,
					["hand_before"] = before.Select(h => h.ToDict()).ToList(),
					["min_cost"] = min, ["max_cost"] = max,
					["tie_set"] = tieSet.Select(t => t.EntityId).ToList(), ["tie_size"] = tieSet.Count, ["in_tie_set"] = inTieSet,
					["was_temp_marked"] = discarded != null && discarded.HasTempEnchant,
					["payoff"] = discarded?.IsTarget
				});
				ResolveDiscardPrediction(cause, before, discarded);
				if(rule == "temporary" || cause == "unattributed")
					ProbeLog.Line("TEMP", $"burn/unattributed discard of {discarded?.Short ?? card?.Name}: GBL_999e={discarded?.HasTempEnchant} GHOSTLY={discarded?.HasGhostlyTag}");

				_lastHand = currentHand; // so a chain of discards (3 Temporary burns) diffs correctly
			}
			catch(Exception ex)
			{
				ProbeLog.Line("ERR", "OnPlayerHandDiscard: " + ex);
			}
			LogDuke("discard", force: false);
		}

		// ------------------------------------------------------------------ DUKE / discard count

		private void LogDuke(string why, bool force)
		{
			try
			{
				var player = GameReader.Player;
				if(player == null) return;
				var discarded = player.EntitiesDiscardedFromHand.Count;
				var distinct = player.EntitiesDiscardedFromHand.Select(e => e.Id).Distinct().Count();
				var playerEntity = HdtApi.Core.Game.PlayerEntity;
				var pTags = GameReader.CopyTags(playerEntity);
				var hero = player.Hero;
				var hTags = GameReader.CopyTags(hero);
				var pDiff = GameReader.DiffTags(_lastPlayerEntityTags, pTags);
				var hDiff = GameReader.DiffTags(_lastHeroTags, hTags);
				var changed = discarded != _lastDiscardCount;
				if(!changed && !force)
				{
					_lastPlayerEntityTags = pTags;
					_lastHeroTags = hTags;
					return;
				}
				var discardTags = pTags.Where(kv => GameReader.TagName(kv.Key).IndexOf("DISCARD", StringComparison.OrdinalIgnoreCase) >= 0)
					.Select(kv => $"{GameReader.TagName(kv.Key)}={kv.Value}").ToList();

				// Only Dukes in HAND or PLAY: DECK copies are hidden (0/0), GRAVEYARD ones reset to 2/2, SETASIDE are Discover offers.
				var dukes = GameReader.AllEntities().Where(e => e.CardId == CardIds.Duke && SafeBool(() => e.IsControlledBy(player.Id))
					&& ((Zone)e.GetTag(GameTag.ZONE) == Zone.HAND || (Zone)e.GetTag(GameTag.ZONE) == Zone.PLAY)).ToList();
				var dukeInfo = new List<object>();
				foreach(var d in dukes)
				{
					var dTags = GameReader.CopyTags(d);
					_lastDukeTags.TryGetValue(d.Id, out var prev);
					var dDiff = GameReader.DiffTags(prev ?? new Dictionary<GameTag, int>(), dTags);
					_lastDukeTags[d.Id] = dTags;
					var zone = (Zone)d.GetTag(GameTag.ZONE);
					var predicted = 2 + 2 * discarded;
					dukeInfo.Add(new Dictionary<string, object>
					{
						["id"] = d.Id, ["zone"] = zone.ToString(), ["atk"] = d.Attack, ["health"] = d.Health,
						["predicted_from_EntitiesDiscardedFromHand"] = predicted, ["tag_changes"] = dDiff
					});
					ProbeLog.Line("DUKE", $"  Duke#{d.Id} zone={zone} atk/hp={d.Attack}/{d.Health} predicted(2+2*{discarded})={predicted} match={d.Attack == predicted} tagChanges=[{string.Join(" ", dDiff.Take(25))}]");
				}
				ProbeLog.Line("DUKE", $"{why}: EntitiesDiscardedFromHand={discarded} (distinct {distinct}) prev={_lastDiscardCount} | player tags w/ DISCARD: [{string.Join(" ", discardTags)}] | playerEntity changes: [{string.Join(" ", pDiff.Take(30))}] | hero changes: [{string.Join(" ", hDiff.Take(30))}]");
				ProbeLog.Record("duke", new Dictionary<string, object>
				{
					["why"] = why, ["entities_discarded_from_hand"] = discarded, ["distinct"] = distinct,
					["player_discard_tags"] = discardTags, ["player_entity_tag_changes"] = pDiff, ["hero_tag_changes"] = hDiff,
					["dukes"] = dukeInfo
				});
				_lastDiscardCount = discarded;
				_lastPlayerEntityTags = pTags;
				_lastHeroTags = hTags;
			}
			catch(Exception ex)
			{
				ProbeLog.Line("ERR", "LogDuke: " + ex.Message);
			}
		}

		// ------------------------------------------------------------------ DECK contents

		public void DumpDeck(string why, bool full)
		{
			try
			{
				var player = GameReader.Player;
				if(player == null) return;
				var list = player.PlayerCardList;
				var createdInHand = player.CreatedCardsInHand.ToList();
				var knownInDeck = player.KnownCardsInDeck.ToList();
				var (n, m, known) = GameReader.PayoffCount(Targets.Current);
				var naiveSum = list.Sum(c => c.Count);
				var positiveSum = list.Where(c => c.Count > 0).Sum(c => c.Count);
				var deckEntities = player.Deck.ToList();
				var deckWithId = deckEntities.Count(e => e.HasCardId);

				ProbeLog.Line("DECK", $"{why}: {FlagsShort()} | DeckCount={m} deckEntities={deckEntities.Count} (withCardId {deckWithId}) | list rows={list.Count} sum(all)={naiveSum} sum(Count>0)={positiveSum} rule-known={known} unknown={m - known} | payoffs N={n} | createdInHand={createdInHand.Sum(c => c.Count)} knownInDeck(created/jousted)={knownInDeck.Sum(c => c.Count)}");
				if(full)
				{
					foreach(var c in list)
						ProbeLog.Line("DECK", $"    row {c.Name,-28} {c.Id,-14} Count={c.Count} IsCreated={c.IsCreated} HighlightInHand={c.HighlightInHand} Jousted={c.Jousted}");
					foreach(var c in createdInHand)
						ProbeLog.Line("DECK", $"    createdInHand {c.Name} {c.Id} x{c.Count}");
					foreach(var c in knownInDeck)
						ProbeLog.Line("DECK", $"    knownInDeck {c.Name} {c.Id} x{c.Count} created={c.IsCreated}");
				}
				ProbeLog.Record("deck", new Dictionary<string, object>
				{
					["why"] = why, ["config"] = ConfigFlags(), ["deck_count"] = m, ["deck_entities"] = deckEntities.Count,
					["deck_entities_with_card_id"] = deckWithId, ["rows"] = list.Count, ["sum_all"] = naiveSum,
					["sum_positive"] = positiveSum, ["rule_known"] = known, ["payoffs_left"] = n,
					["list"] = full ? list.Select(c => (object)new Dictionary<string, object>
					{
						["id"] = c.Id, ["count"] = c.Count, ["created"] = c.IsCreated, ["highlight"] = c.HighlightInHand, ["jousted"] = c.Jousted
					}).ToList() : null,
					["created_in_hand"] = createdInHand.Select(c => (object)new Dictionary<string, object> { ["id"] = c.Id, ["count"] = c.Count }).ToList()
				});
			}
			catch(Exception ex)
			{
				ProbeLog.Line("ERR", "DumpDeck: " + ex.Message);
			}
		}

		// ------------------------------------------------------------------ MULLIGAN

		private void LogMulliganResult(List<HandCard> finalHand)
		{
			try
			{
				var final = finalHand.Where(h => !h.IsCoin).ToList();
				var openIds = new HashSet<int>(_openingHand.Select(h => h.EntityId));
				var finalIds = new HashSet<int>(final.Select(h => h.EntityId));
				var tossed = _openingHand.Where(h => !finalIds.Contains(h.EntityId)).ToList();
				var kept = _openingHand.Where(h => finalIds.Contains(h.EntityId)).ToList();
				var replacements = final.Where(h => !openIds.Contains(h.EntityId)).ToList();
				// If HDT reported more toss events than entities that actually left, a tossed entity came straight back.
				var tossedEntityReturned = _mulliganTossEvents.Count > tossed.Count;
				var mulliganedFlagInHand = GameReader.Player.Hand.Count(e => e.Info != null && e.Info.Mulliganed);

				var deck = HdtDeckList.Instance.ActiveDeckVersion;
				var sameCardRedraws = new List<object>();
				foreach(var r in replacements)
				{
					var tossedSame = tossed.Count(t => t.CardId == r.CardId);
					if(tossedSame == 0) continue;
					var copiesInList = deck?.Cards.Where(c => c.Id == r.CardId).Sum(c => c.Count) ?? -1;
					var copiesInOpening = _openingHand.Count(h => h.CardId == r.CardId);
					// copies that were in the deck (not in the opening hand) when replacements were drawn
					var otherCopiesInDeck = copiesInList < 0 ? -1 : copiesInList - copiesInOpening;
					sameCardRedraws.Add(new Dictionary<string, object>
					{
						["card"] = r.CardId, ["replacement_entity"] = r.EntityId,
						["tossed_entities"] = tossed.Where(t => t.CardId == r.CardId).Select(t => t.EntityId).ToList(),
						["copies_in_list"] = copiesInList, ["other_copies_in_deck_at_mulligan"] = otherCopiesInDeck
					});
					ProbeLog.Line("MULL", $"  SAME-NAME REDRAW: {r.Short} (tossed {tossedSame}, list has {copiesInList}, other copies left in deck {otherCopiesInDeck}) {(otherCopiesInDeck <= 0 ? "<-- would mean a TOSSED copy came back!" : "(another copy was available; consistent with rule)")}");
				}
				var deckAfter = Safe(() => GameReader.Player.DeckCount);
				ProbeLog.Line("MULL", $"result: opening={_openingHand.Count} tossEvents={_mulliganTossEvents.Count} tossed={Fmt(tossed)} kept={Fmt(kept)} replacements={Fmt(replacements)} | tossedEntityReturned={tossedEntityReturned} | Info.Mulliganed in hand={mulliganedFlagInHand} | deck {_deckCountAtOpening}->{deckAfter}");
				ProbeLog.Record("mulligan", new Dictionary<string, object>
				{
					["going_first"] = _openingHand.Count == 3, ["opening"] = _openingHand.Select(h => h.ToDict()).ToList(),
					["toss_events"] = _mulliganTossEvents.ToList(), ["tossed"] = tossed.Select(h => h.ToDict()).ToList(),
					["kept"] = kept.Select(h => h.EntityId).ToList(), ["replacements"] = replacements.Select(h => h.ToDict()).ToList(),
					["tossed_entity_returned"] = tossedEntityReturned, ["same_card_redraws"] = sameCardRedraws,
					["deck_before"] = _deckCountAtOpening, ["deck_after"] = deckAfter
				});
			}
			catch(Exception ex)
			{
				ProbeLog.Line("ERR", "LogMulliganResult: " + ex.Message);
			}
		}

		// ------------------------------------------------------------------ ODDS: prediction vs outcome

		private void StartPrediction(OddsRule rule, Card card, DateTime now)
		{
			try
			{
				var current = new HashSet<int>(GameReader.SnapshotHand().Select(h => h.EntityId));
				var played = _lastHand.FirstOrDefault(h => h.CardId == card.Id && !current.Contains(h.EntityId));
				// Hand as it was just before the play; the played entity is excluded inside Compute.
				var state = GameReader.BuildOddsState(_lastHand, Targets.Current);
				var odds = OddsEngine.Compute(rule, state, played?.EntityId ?? 0);
				var pred = new Prediction { Rule = rule, Odds = odds, At = now, Turn = _turn };
				_predictions.Add(pred);
				ProbeLog.Line("ODDS", $"PLAY {odds}");
				ProbeLog.Record("odds_prediction", new Dictionary<string, object>
				{
					["card"] = rule.CardId, ["name"] = rule.Name, ["kind"] = rule.Kind.ToString(), ["hit"] = odds.Hit, ["expected_hits"] = odds.ExpectedHits,
					["approx"] = odds.Approx, ["detail"] = odds.Detail, ["deck_count"] = state.DeckCount, ["known_deck"] = state.Deck.Values.Sum(),
					["turn"] = _turn
				});
			}
			catch(Exception ex)
			{
				ProbeLog.Line("ERR", "StartPrediction: " + ex.Message);
			}
		}

		private void FeedDrawPrediction(Card card)
		{
			var pred = _predictions.LastOrDefault(p => !p.Resolved && p.IsDraw && p.Turn == _turn && (DateTime.Now - p.At).TotalSeconds < OutletWindowSeconds);
			if(pred == null || card == null) return;
			if(CardIds.IsCastsWhenDrawn(card.Id)) { ProbeLog.Line("ODDS", $"  {card.Name} cast when drawn: not counted as one of the {pred.DrawsExpected} draws"); return; }
			pred.Drawn.Add(card.Id);
			if(pred.Drawn.Count >= pred.DrawsExpected)
				ResolvePrediction(pred, pred.Drawn.Any(id => Targets.Contains(id)), "drew " + string.Join(", ", pred.Drawn.Select(id => Name(id) ?? id)), pred.Drawn);
		}

		private void ResolveDiscardPrediction(string cause, List<HandCard> before, HandCard discarded)
		{
			try
			{
				if(cause == null) return;
				var id = cause.TrimEnd('?');
				var pred = _predictions.LastOrDefault(p => !p.Resolved && p.Rule.CardId == id && (DateTime.Now - p.At).TotalSeconds < WindowFor(p.Rule.Kind));
				if(pred == null && OddsRule.CardRules.TryGetValue(id, out var rule) && rule.Kind != OddsKind.DrawThenDiscardIt)
				{
					// e.g. Chronoclaws: no play event right before the discard, so predict from the pre-discard hand now.
					var state = GameReader.BuildOddsState(before, Targets.Current);
					state.Hand.Add(new OddsHandCard { EntityId = -1, CardId = id, Name = rule.Name }); // placeholder "self", excluded below
					pred = new Prediction { Rule = rule, Odds = OddsEngine.Compute(rule, state, -1), At = DateTime.Now, Turn = _turn };
					_predictions.Add(pred);
					ProbeLog.Line("ODDS", $"TRIGGER {pred.Odds}");
				}
				if(pred == null || pred.IsDraw) return;
				ResolvePrediction(pred, discarded?.IsTarget == true, "discarded " + (discarded?.Short ?? "?"), discarded == null ? null : new[] { discarded.CardId });
			}
			catch(Exception ex)
			{
				ProbeLog.Line("ERR", "ResolveDiscardPrediction: " + ex.Message);
			}
		}

		private void TickPredictions(DateTime now)
		{
			foreach(var pred in _predictions.Where(p => !p.Resolved).ToList())
			{
				try
				{
					if(pred.Rule.Kind == OddsKind.DiscoverFromDeck)
					{
						var offered = GameReader.Player.OfferedEntities.Where(e => e.HasCardId).ToList();
						if(offered.Count > 0)
						{
							var ids = offered.Select(e => e.CardId).ToList();
							var hit = ids.Any(i => Targets.Contains(i));
							ResolvePrediction(pred, hit, $"offered {string.Join(", ", ids.Select(i => Name(i) ?? i))} (distinct ids {ids.Distinct().Count()}/{ids.Count})", ids);
							continue;
						}
					}
					if((now - pred.At).TotalSeconds > WindowFor(pred.Rule.Kind) + 1)
					{
						var hit = pred.Drawn.Any(id => Targets.Contains(id));
						ResolvePrediction(pred, hit, pred.IsDraw ? $"timeout after {pred.Drawn.Count}/{pred.DrawsExpected} draws" : "no matching discard/offer seen", pred.Drawn);
					}
				}
				catch(Exception ex)
				{
					ProbeLog.Line("ERR", "TickPredictions: " + ex.Message);
					pred.Resolved = true;
				}
			}
		}

		private void ResolvePrediction(Prediction pred, bool hit, string what, IEnumerable<string> cards)
		{
			if(pred.Resolved) return;
			pred.Resolved = true;
			ProbeLog.Line("ODDS", $"RESULT {pred.Rule.Name}: {(hit ? "HIT" : "MISS")} (predicted hit {OddsEngine.Pct(pred.Odds.Hit)}) | {what}");
			ProbeLog.Record("odds_outcome", new Dictionary<string, object>
			{
				["card"] = pred.Rule.CardId, ["name"] = pred.Rule.Name, ["kind"] = pred.Rule.Kind.ToString(),
				["predicted_hit"] = pred.Odds.Hit, ["hit"] = hit, ["approx"] = pred.Odds.Approx, ["what"] = what,
				["cards"] = cards?.ToList(), ["turn"] = pred.Turn
			});
		}

		// ------------------------------------------------------------------ PLATYSAUR

		private void StartPlatyTrack(DateTime now)
		{
			try
			{
				var player = GameReader.Player;
				var tracked = new HashSet<int>(_platys.Select(x => x.PlatyEntityId));
				var platyEntity = player.Board.Where(e => e.CardId == CardIds.Platysaur && !tracked.Contains(e.Id)).OrderByDescending(e => e.Id).FirstOrDefault();
				var (n, m, _) = GameReader.PayoffCount(Targets.Current);
				var track = new PlatyTrack
				{
					PlatyEntityId = platyEntity?.Id ?? 0, PlayedAt = now, Turn = _turn, PayoffsBefore = n, DeckBefore = m,
					TagsAtPlay = GameReader.CopyTags(platyEntity),
					// _lastHand is the pre-play snapshot. Don't diff against _lastHand at draw time: HDT queues the draw
					// action for SHOW_ENTITY creation tags, so a tick can already have snapshotted the drawn card.
					HandAtPlay = new HashSet<int>(_lastHand.Select(h => h.EntityId))
				};
				if(platyEntity == null)
					ProbeLog.Line("PLATY", "  (no untracked Platysaur entity on board at play time; entity links unavailable for this one)");
				_platys.Add(track);
				var p = m > 0 ? (double)n / m : 0;
				ProbeLog.Line("PLATY", $"Platysaur#{track.PlatyEntityId} played turn {_turn}: before its draw N={n} payoffs / M={m} cards -> P(draws payoff)={p:P1} | hand={Fmt(_lastHand)}");
				ProbeLog.Record("platy_play", new Dictionary<string, object>
				{
					["platy_entity"] = track.PlatyEntityId, ["turn"] = _turn, ["payoffs_before"] = n, ["deck_before"] = m, ["p_payoff"] = p,
					["hand_before"] = _lastHand.Select(h => h.ToDict()).ToList()
				});
			}
			catch(Exception ex)
			{
				ProbeLog.Line("ERR", "StartPlatyTrack: " + ex.Message);
			}
		}

		private void LinkPlatyDraw(Card card)
		{
			try
			{
				var track = _platys.LastOrDefault(x => !x.Resolved && !x.DrawSeen && x.Turn == _turn && (DateTime.Now - x.PlayedAt).TotalSeconds < OutletWindowSeconds);
				if(track == null || card == null) return;
				track.DrawSeen = true;
				track.DrawnCardId = card.Id;
				track.DrawnIsPayoff = Targets.Contains(card.Id);
				track.DrawnEntityId = FindPlatyDrawn(track, GameReader.SnapshotHand()) ?? -1; // -1 = not visible yet; retried on tick
				ProbeLog.Line("PLATY", $"Platysaur#{track.PlatyEntityId} drew {card.Name}#{(track.DrawnEntityId > 0 ? track.DrawnEntityId.ToString() : "? (resolving)")} payoff={track.DrawnIsPayoff} (expected {track.PayoffsBefore}/{track.DeckBefore})");
			}
			catch(Exception ex)
			{
				ProbeLog.Line("ERR", "LinkPlatyDraw: " + ex.Message);
			}
		}

		private static bool PlatyInPlay(PlatyTrack track)
		{
			try
			{
				return track.PlatyEntityId > 0 && HdtApi.Core.Game.Entities.TryGetValue(track.PlatyEntityId, out var pe)
				       && (Zone)pe.GetTag(GameTag.ZONE) == Zone.PLAY;
			}
			catch { return false; }
		}

		/// <summary>The drawn card: in hand now, matching card id, not in hand before Platysaur was played, not claimed by another track.</summary>
		private int? FindPlatyDrawn(PlatyTrack track, List<HandCard> hand)
		{
			var claimed = new HashSet<int>(_platys.Where(x => x != track && x.DrawnEntityId > 0).Select(x => x.DrawnEntityId));
			var drawn = hand.Where(h => h.CardId == track.DrawnCardId && !track.HandAtPlay.Contains(h.EntityId) && !claimed.Contains(h.EntityId))
				.OrderByDescending(h => h.EntityId).FirstOrDefault();
			return drawn?.EntityId;
		}

		private void TickPlatysaur(List<HandCard> hand)
		{
			foreach(var track in _platys.Where(x => !x.Resolved).ToList())
			{
				try
				{
					var age = (DateTime.Now - track.PlayedAt).TotalSeconds;
					// 0a) the game's own link: TLC_603e3 attached to this Platysaur, TAG_SCRIPT_DATA_NUM_1 = drawn entity id
					if(track.PlatyEntityId > 0 && !track.EnchantLinked)
					{
						var link = GameReader.AllEntities().FirstOrDefault(e => e.CardId == CardIds.PlatysaurLinkEnchant && e.GetTag(GameTag.ATTACHED) == track.PlatyEntityId);
						var linked = link?.GetTag(GameTag.TAG_SCRIPT_DATA_NUM_1) ?? 0;
						if(linked > 0)
						{
							track.EnchantLinked = true;
							if(linked != track.DrawnEntityId)
								ProbeLog.Line("PLATY", $"  Platysaur#{track.PlatyEntityId} linked via {CardIds.PlatysaurLinkEnchant}: drawn #{linked}{(track.DrawnEntityId > 0 ? $" (hand-diff said #{track.DrawnEntityId})" : "")}");
							track.DrawnEntityId = linked;
							track.LateResolveLogged = true;
							if(track.DrawnCardId == null && HdtApi.Core.Game.Entities.TryGetValue(linked, out var de))
							{
								track.DrawnCardId = de.CardId;
								track.DrawnIsPayoff = Targets.Contains(de.CardId);
							}
						}
					}
					// 0b) fallback: hand diff against the hand at play time (retried for a few seconds)
					if(track.DrawSeen && track.DrawnEntityId <= 0 && !track.LateResolveLogged)
					{
						var id = FindPlatyDrawn(track, hand);
						if(id != null)
						{
							track.DrawnEntityId = id.Value;
							track.LateResolveLogged = true;
							ProbeLog.Line("PLATY", $"  Platysaur#{track.PlatyEntityId} drawn card resolved: {Name(track.DrawnCardId) ?? track.DrawnCardId}#{id.Value}");
						}
						else if(age > OutletWindowSeconds)
						{
							track.LateResolveLogged = true;
							ProbeLog.Line("PLATY", $"  Platysaur#{track.PlatyEntityId} drawn card {Name(track.DrawnCardId) ?? track.DrawnCardId} never appeared in hand (burned / hand full?)");
						}
					}
					// 1) ~1.5 s after the draw: log how the game links the two (enchantments + tag changes)
					if(!track.LinkLogged && track.DrawSeen && (track.DrawnEntityId > 0 || track.LateResolveLogged) && age > TempCheckDelaySeconds)
					{
						track.LinkLogged = true;
						var all = GameReader.AllEntities();
						var enchants = all.Where(e => e.CardId != null && CardIds.PlatysaurEnchants.Contains(e.CardId)).ToList();
						foreach(var en in enchants)
						{
							var interesting = GameReader.CopyTags(en)
								.Where(kv => kv.Value == track.PlatyEntityId || kv.Value == track.DrawnEntityId
								             || GameReader.TagName(kv.Key).Contains("ATTACHED") || GameReader.TagName(kv.Key).Contains("CREATOR")
								             || GameReader.TagName(kv.Key).Contains("SCRIPT_DATA") || GameReader.TagName(kv.Key) == "ZONE")
								.Select(kv => $"{GameReader.TagName(kv.Key)}={kv.Value}");
							ProbeLog.Line("PLATY", $"  enchant {en.CardId}#{en.Id}: {string.Join(" ", interesting)} (platy=#{track.PlatyEntityId}, drawn=#{track.DrawnEntityId})");
						}
						if(HdtApi.Core.Game.Entities.TryGetValue(track.PlatyEntityId, out var platyEntity))
						{
							var diff = GameReader.DiffTags(track.TagsAtPlay ?? new Dictionary<GameTag, int>(), GameReader.CopyTags(platyEntity));
							var refs = GameReader.CopyTags(platyEntity).Where(kv => kv.Value == track.DrawnEntityId).Select(kv => GameReader.TagName(kv.Key)).ToList();
							ProbeLog.Line("PLATY", $"  Platysaur#{track.PlatyEntityId} tag changes since play: [{string.Join(" ", diff.Take(30))}] | tags whose value == drawn entity id: [{string.Join(",", refs)}]");
						}
						var drawnNow = hand.FirstOrDefault(h => h.EntityId == track.DrawnEntityId);
						if(drawnNow != null)
						{
							ProbeLog.Line("PLATY", $"  drawn card in hand: {drawnNow.Short} attached=[{string.Join(",", drawnNow.Attached)}]");
							DumpEntityTags("PLATY", drawnNow.EntityId);
						}
						ProbeLog.Record("platy_link", new Dictionary<string, object>
						{
							["platy_entity"] = track.PlatyEntityId, ["drawn_entity"] = track.DrawnEntityId, ["drawn_card"] = track.DrawnCardId,
							["drawn_attached"] = drawnNow?.Attached, ["enchant_ids"] = enchants.Select(e => e.CardId + "#" + e.Id).ToList()
						});
					}

					// 2) drawn card left hand without a discard -> it was played (or otherwise moved) before Platysaur died
					var inHand = hand.Any(h => h.EntityId == track.DrawnEntityId);
					var wasInHand = _lastHand.Any(h => h.EntityId == track.DrawnEntityId);
					if(track.DrawnEntityId > 0 && wasInHand && !inHand)
					{
						if(HdtApi.Core.Game.Entities.TryGetValue(track.DrawnEntityId, out var de))
						{
							var zone = (Zone)de.GetTag(GameTag.ZONE);
							if(zone != Zone.GRAVEYARD || !de.Info.Discarded)
								LogPlatyOutcome(track, "drawn_card_left_hand_" + zone, null);
						}
					}

					// 3) Platysaur left play: log whether "it" was still in hand at that moment
					if(track.PlatyEntityId > 0 && HdtApi.Core.Game.Entities.TryGetValue(track.PlatyEntityId, out var pe))
					{
						var pz = (Zone)pe.GetTag(GameTag.ZONE);
						if(pz != Zone.PLAY && !track.Resolved && !track.LeftPlayLogged && (track.LeftPlayLogged = true))
							ProbeLog.Line("PLATY", $"  Platysaur#{track.PlatyEntityId} left play (zone {pz}); drawn #{track.DrawnEntityId} still in hand={inHand}");
						if(pz != Zone.PLAY && !inHand && !track.Resolved && age > 10)
							LogPlatyOutcome(track, "platysaur_gone_card_not_in_hand", null);
					}
				}
				catch(Exception ex)
				{
					ProbeLog.Line("ERR", "TickPlatysaur: " + ex.Message);
				}
			}
		}

		private void LogPlatyOutcome(PlatyTrack track, string outcome, HandCard discarded)
		{
			if(track.Resolved) return;
			track.Resolved = true;
			ProbeLog.Line("PLATY", $"Platysaur#{track.PlatyEntityId} outcome={outcome} | drew {Name(track.DrawnCardId) ?? track.DrawnCardId}#{track.DrawnEntityId} payoff={track.DrawnIsPayoff} | discarded={discarded?.Short ?? "-"} | turns alive={_turn - track.Turn}");
			ProbeLog.Record("platy_outcome", new Dictionary<string, object>
			{
				["platy_entity"] = track.PlatyEntityId, ["outcome"] = outcome, ["drawn_card"] = track.DrawnCardId,
				["drawn_entity"] = track.DrawnEntityId, ["drawn_payoff"] = track.DrawnIsPayoff,
				["payoffs_before"] = track.PayoffsBefore, ["deck_before"] = track.DeckBefore,
				["discarded"] = discarded?.ToDict(), ["turn_played"] = track.Turn, ["turn_resolved"] = _turn
			});
		}

		/// <summary>For the widget: Platysaurs on board whose drawn card is still in hand.</summary>
		public List<(string drawnName, bool payoff)> LivePlatysaurLinks()
		{
			var res = new List<(string, bool)>();
			foreach(var t in _platys.Where(x => !x.Resolved && x.DrawnEntityId > 0))
			{
				var h = _lastHand.FirstOrDefault(x => x.EntityId == t.DrawnEntityId);
				if(h != null) res.Add((h.Name ?? h.CardId, h.IsTarget));
			}
			return res;
		}

		// ------------------------------------------------------------------ helpers

		private void RefreshLastHandSoon()
		{
			// Do not overwrite _lastHand here: discard handlers for this play still need the pre-play snapshot.
		}

		private void DumpEntityTags(string cat, int entityId)
		{
			try
			{
				if(!HdtApi.Core.Game.Entities.TryGetValue(entityId, out var e)) return;
				var tags = GameReader.CopyTags(e).OrderBy(kv => (int)kv.Key).Select(kv => $"{GameReader.TagName(kv.Key)}={kv.Value}");
				ProbeLog.Line(cat, $"  all tags of #{entityId} {e.CardId}: {string.Join(" ", tags)}");
			}
			catch { }
		}

		private static bool PlayerHasWeapon(string cardId)
		{
			try
			{
				return GameReader.Player.Board.Any(e => e.IsWeapon && e.CardId == cardId);
			}
			catch { return false; }
		}

		private static string TieSummary(List<HandCard> hand)
		{
			var nonCoin = hand.ToList();
			if(nonCoin.Count == 0) return "empty";
			var min = nonCoin.Min(h => h.Cost);
			var max = nonCoin.Max(h => h.Cost);
			return $"lowest c{min} x{nonCoin.Count(h => h.Cost == min)}, highest c{max} x{nonCoin.Count(h => h.Cost == max)}";
		}

		private static Dictionary<string, object> ConfigFlags()
		{
			var c = HdtConfig.Instance;
			return new Dictionary<string, object>
			{
				["RemoveCardsFromDeck"] = c.RemoveCardsFromDeck,
				["HighlightCardsInHand"] = c.HighlightCardsInHand,
				["ShowPlayerGet"] = c.ShowPlayerGet
			};
		}

		private static string FlagsShort()
		{
			var c = HdtConfig.Instance;
			return $"cfg[RemoveCardsFromDeck={c.RemoveCardsFromDeck} HighlightCardsInHand={c.HighlightCardsInHand} ShowPlayerGet={c.ShowPlayerGet}]";
		}

		private static string Fmt(IEnumerable<HandCard> cards) => "[" + string.Join(", ", cards.Select(c => c.Short)) + "]";

		private static string Name(string cardId)
		{
			if(string.IsNullOrEmpty(cardId)) return null;
			try { return HearthDb.Cards.All.TryGetValue(cardId.TrimEnd('?'), out var c) ? c.Name : null; }
			catch { return null; }
		}

		private static T Safe<T>(Func<T> f)
		{
			try { return f(); } catch { return default; }
		}

		private static bool SafeBool(Func<bool> f)
		{
			try { return f(); } catch { return false; }
		}
	}
}
