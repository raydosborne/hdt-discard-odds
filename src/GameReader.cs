using System;
using System.Collections.Generic;
using System.Linq;
using HearthDb.Enums;
using Hearthstone_Deck_Tracker.Hearthstone;
using Hearthstone_Deck_Tracker.Hearthstone.Entities;
using HdtApi = Hearthstone_Deck_Tracker.API;

namespace DiscardOdds
{
	/// <summary>
	/// The active deck's target cards (from targets.json: the deck's own list, or an auto-matching preset).
	/// Swapped as a whole by DiscardOddsPlugin.RefreshTargets; readers just take the current reference.
	/// </summary>
	public static class Targets
	{
		public static volatile HashSet<string> Current = new HashSet<string>();
		public static volatile ResolvedTargets Resolved = new ResolvedTargets();
		public static bool Contains(string cardId) => cardId != null && Current.Contains(cardId);
	}

	/// <summary>One distinct card in the active HDT deck list.</summary>
	public sealed class DeckCardInfo
	{
		public string Id;
		public string Name;
		public int Cost;
		public int Copies;
	}

	/// <summary>Card ids used by the probes (verified against cards.json, 2026-10-04).</summary>
	public static class CardIds
	{
		public const string Duke = "CATA_493";          // Duke of Below: Rush, +2/+2 per card discarded this game (base 2/2)
		public const string Soularium = "BOT_568";      // Draw 3 cards. They are Temporary.
		public const string Catacombs = "TLC_451";      // Discover another card from your deck. Make it Temporary.
		public const string SketchArtist = "TOY_916";   // Draw a Shadow spell. Get a Temporary copy of it.
		public const string Chronoclaws = "END_016";    // weapon 4/3: after your hero attacks, discard your highest Cost card
		public const string TemporaryEnchantment = "GBL_999e"; // observed on Temporary cards in game logs (the [TEMP] probe verifies it)
		// The Coin has several ids (cosmetic coins, e.g. REV_COIN2 in the first live test). Known ids, plus any
		// 0-cost spell HearthDb names "The Coin".
		private static readonly HashSet<string> CoinIds = new HashSet<string> { "GAME_005", "REV_COIN1", "REV_COIN2", "REV_COIN3" };
		private static readonly Dictionary<string, bool> CoinCache = new Dictionary<string, bool>();
		public static bool IsCoin(string cardId)
		{
			if(cardId == null) return false;
			if(CoinIds.Contains(cardId)) return true;
			lock(CoinCache)
			{
				if(CoinCache.TryGetValue(cardId, out var known)) return known;
				var coin = false;
				try { coin = HearthDb.Cards.All.TryGetValue(cardId, out var c) && c.Type == CardType.SPELL && c.Cost == 0 && c.GetLocName(Locale.enUS) == "The Coin"; } catch { }
				CoinCache[cardId] = coin;
				return coin;
			}
		}

		// "Casts When Drawn" cards (e.g. Shreds of Time, TIME_025t) cast themselves and draw a replacement, so they
		// are not one of "the next M cards" for draw odds. HearthDb TOPDECK tag (377), else the card text.
		private static readonly Dictionary<string, bool> CwdCache = new Dictionary<string, bool>();
		public static bool IsCastsWhenDrawn(string cardId)
		{
			if(cardId == null) return false;
			lock(CwdCache)
			{
				if(CwdCache.TryGetValue(cardId, out var known)) return known;
				var cwd = false;
				try
				{
					if(HearthDb.Cards.All.TryGetValue(cardId, out var c))
						cwd = c.Entity.GetTag((GameTag)377) > 0
						      || System.Text.RegularExpressions.Regex.Replace(c.GetLocText(Locale.enUS) ?? "", "<[^>]+>", "").IndexOf("Casts When Drawn", StringComparison.OrdinalIgnoreCase) >= 0;
				}
				catch { }
				CwdCache[cardId] = cwd;
				return cwd;
			}
		}

		// Platysaur (TLC_603, 1-mana 1/2 Beast): "Battlecry: Draw a card. Deathrattle: Discard it." (HearthstoneJSON, 2026-10-04)
		// Related enchantments in HearthstoneJSON:
		//   TLC_603e  "When this dies, discard the card it drew."   (expected on the Platysaur)
		//   TLC_603e2 "Discard when Platysaur dies."                (expected on the drawn card in hand)
		//   TLC_603e3 "Deathrattle: Discard {0}."                   (expected on the Platysaur, names the card)
		// Which entity each one attaches to, and which tag links Platysaur to "it", is what the [PLATY] probe logs.
		public const string Platysaur = "TLC_603";
		// First live test (3/3): TLC_603e3 is attached to the Platysaur and its TAG_SCRIPT_DATA_NUM_1 is the drawn
		// entity id. TLC_603e / TLC_603e2 never appeared, and the drawn card carries no Platysaur enchantment.
		public const string PlatysaurLinkEnchant = "TLC_603e3";
		public static readonly HashSet<string> PlatysaurEnchants = new HashSet<string> { "TLC_603e", "TLC_603e2", "TLC_603e3" };

		/// <summary>Discard outlets and the card-text rule we want to verify.</summary>
		public static readonly Dictionary<string, string> OutletRule = new Dictionary<string, string>
		{
			["DMF_119"] = "lowest",  // Wicked Whispers
			["ULD_163"] = "highest", // Expired Merchant (battlecry)
			["END_016"] = "highest", // Chronoclaws (after hero attack)
			["EX1_308"] = "random",  // Soulfire
			["OG_109"] = "random",   // Darkshire Librarian
			["CATA_490"] = "choose", // Ocular Occultist
			["CATA_897"] = "choose", // Gemstone Hoarder
			["WON_103"] = "look3",   // Chamber of Viscidus
			["TLC_603"] = "drawn",   // Platysaur: deathrattle discards the card it drew (tracked separately by the [PLATY] probe)
		};

		// HearthDb's GameTag.GHOSTLY = 785. Cast by number so this compiles against older HearthDb builds too.
		public const GameTag GhostlyTag = (GameTag)785;
	}

	/// <summary>Immutable copy of one hand card at a moment in time.</summary>
	public sealed class HandCard
	{
		public int EntityId;
		public string CardId;
		public string Name;
		public int Cost;
		public int ZonePos;
		public bool Created;
		public bool HasTempEnchant;     // attached GBL_999e
		public bool HasGhostlyTag;      // GameTag 785 on the card itself
		public bool HasPlatysaurMark;   // any TLC_603e* enchantment attached
		public List<string> Attached = new List<string>();

		public bool IsCoin => CardIds.IsCoin(CardId);
		public bool IsTarget => Targets.Contains(CardId);

		public string Short => $"{Name ?? CardId ?? "?"}#{EntityId}(c{Cost}{(HasTempEnchant ? ",TEMP" : "")}{(HasPlatysaurMark ? ",PLATY" : "")})";

		public Dictionary<string, object> ToDict() => new Dictionary<string, object>
		{
			["id"] = EntityId, ["card"] = CardId, ["name"] = Name, ["cost"] = Cost, ["pos"] = ZonePos,
			["created"] = Created, ["temp_ench"] = HasTempEnchant, ["ghostly"] = HasGhostlyTag, ["platy_mark"] = HasPlatysaurMark, ["attached"] = Attached
		};
	}

	/// <summary>All reads of HDT state go through here, wrapped so a changed HDT internal never crashes the plugin.</summary>
	public static class GameReader
	{
		public static Player Player => HdtApi.Core.Game?.Player;

		public static List<Entity> AllEntities()
		{
			for(var attempt = 0; attempt < 3; attempt++)
			{
				try
				{
					return HdtApi.Core.Game.Entities.Values.ToList();
				}
				catch(InvalidOperationException)
				{
					// collection modified while copying; retry
				}
				catch(Exception)
				{
					break;
				}
			}
			return new List<Entity>();
		}

		public static List<HandCard> SnapshotHand()
		{
			var result = new List<HandCard>();
			try
			{
				var player = Player;
				if(player == null)
					return result;
				var hand = player.Hand.ToList();
				if(hand.Count == 0)
					return result;
				var handIds = new HashSet<int>(hand.Select(e => e.Id));
				var attachedByTarget = new Dictionary<int, List<string>>();
				foreach(var e in AllEntities())
				{
					var target = e.GetTag(GameTag.ATTACHED);
					if(target <= 0 || !handIds.Contains(target))
						continue;
					if(!attachedByTarget.TryGetValue(target, out var list))
						attachedByTarget[target] = list = new List<string>();
					list.Add(string.IsNullOrEmpty(e.CardId) ? $"?#{e.Id}" : e.CardId);
				}
				foreach(var e in hand.OrderBy(x => x.GetTag(GameTag.ZONE_POSITION)))
				{
					attachedByTarget.TryGetValue(e.Id, out var att);
					result.Add(new HandCard
					{
						EntityId = e.Id,
						CardId = e.CardId,
						Name = SafeName(e),
						Cost = SafeCost(e),
						ZonePos = e.GetTag(GameTag.ZONE_POSITION),
						Created = e.Info != null && (e.Info.Created || e.Info.Stolen),
						Attached = att ?? new List<string>(),
						HasTempEnchant = att != null && att.Contains(CardIds.TemporaryEnchantment),
						HasGhostlyTag = e.GetTag(CardIds.GhostlyTag) > 0,
						HasPlatysaurMark = att != null && att.Any(a => CardIds.PlatysaurEnchants.Contains(a))
					});
				}
			}
			catch(Exception ex)
			{
				ProbeLog.Line("ERR", "SnapshotHand: " + ex.Message);
			}
			return result;
		}

		public static string SafeName(Entity e)
		{
			try { return string.IsNullOrEmpty(e.CardId) ? null : e.Card?.Name; } catch { return null; }
		}

		public static int SafeCost(Entity e)
		{
			try { return e.Cost; } catch { return e.GetTag(GameTag.COST); }
		}

		public static string TagName(GameTag t)
		{
			var n = Enum.GetName(typeof(GameTag), t);
			return n ?? ((int)t).ToString();
		}

		public static Dictionary<GameTag, int> CopyTags(Entity e)
		{
			try { return e?.Tags != null ? new Dictionary<GameTag, int>(e.Tags) : new Dictionary<GameTag, int>(); }
			catch { return new Dictionary<GameTag, int>(); }
		}

		/// <summary>"TAG: old->new" for every tag that differs.</summary>
		public static List<string> DiffTags(Dictionary<GameTag, int> before, Dictionary<GameTag, int> after)
		{
			var diffs = new List<string>();
			foreach(var k in before.Keys.Union(after.Keys))
			{
				before.TryGetValue(k, out var a);
				after.TryGetValue(k, out var b);
				if(a != b)
					diffs.Add($"{TagName(k)}:{a}->{b}");
			}
			diffs.Sort(StringComparer.Ordinal);
			return diffs;
		}

		/// <summary>
		/// Live payoff count per M0 counting rule (plan §3):
		///   M = Player.DeckCount;
		///   N = Σ Count over PlayerCardList rows with Count &gt; 0 and Id in group,
		///       minus CreatedCardsInHand copies (those rows are appended only when Config.ShowPlayerGet is on).
		/// </summary>
		public static (int n, int m, int knownTotal) PayoffCount(ISet<string> group)
		{
			var player = Player;
			if(player == null)
				return (0, 0, 0);
			var m = player.DeckCount;
			var list = player.PlayerCardList;
			var createdInHand = Hearthstone_Deck_Tracker.Config.Instance.ShowPlayerGet
				? player.CreatedCardsInHand.ToList()
				: new List<Card>();
			int Adjusted(Card c)
			{
				var count = c.Count;
				if(c.IsCreated)
					count -= createdInHand.Where(x => x.Id == c.Id).Sum(x => x.Count);
				return Math.Max(0, count);
			}
			var positive = list.Where(c => c.Count > 0).ToList();
			// Casts-When-Drawn copies leave M (and the known list): drawing one replaces itself.
			var cwd = positive.Where(c => CardIds.IsCastsWhenDrawn(c.Id)).Sum(Adjusted);
			positive = positive.Where(c => !CardIds.IsCastsWhenDrawn(c.Id)).ToList();
			var n = positive.Where(c => group.Contains(c.Id)).Sum(Adjusted);
			var known = positive.Sum(Adjusted);
			return (n, Math.Max(0, m - cwd), known);
		}

		/// <summary>Casts-When-Drawn copies still in the deck (excluded from M; shown as "skipped").</summary>
		public static int CastsWhenDrawnInDeck()
		{
			var player = Player;
			if(player == null) return 0;
			return player.PlayerCardList.Where(c => c.Count > 0 && CardIds.IsCastsWhenDrawn(c.Id)).Sum(c => c.Count);
		}

		/// <summary>
		/// False while HDT's PlayerCardList still lags DeckCount (inside OnPlayerDraw the list still holds the card just
		/// drawn: 88/88 reads in the first live test). Settled = known list total &lt;= DeckCount.
		/// </summary>
		public static bool DeckSettled()
		{
			var player = Player;
			if(player == null) return true;
			var (_, m, known) = PayoffCount(new HashSet<string>());
			return known <= m;
		}

		public static double PAtLeastOne(int n, int m, int k) => OddsEngine.PAtLeastOne(n, m, k);

		/// <summary>Known remaining deck contents (cardId -> copies) using the same counting rule as PayoffCount.</summary>
		public static Dictionary<string, int> RemainingDeck()
		{
			var result = new Dictionary<string, int>();
			var player = Player;
			if(player == null) return result;
			var createdInHand = Hearthstone_Deck_Tracker.Config.Instance.ShowPlayerGet
				? player.CreatedCardsInHand.ToList()
				: new List<Card>();
			foreach(var c in player.PlayerCardList.Where(c => c.Count > 0 && !CardIds.IsCastsWhenDrawn(c.Id)))
			{
				var count = c.Count;
				if(c.IsCreated)
					count -= createdInHand.Where(x => x.Id == c.Id).Sum(x => x.Count);
				if(count <= 0) continue;
				result.TryGetValue(c.Id, out var prev);
				result[c.Id] = prev + count;
			}
			return result;
		}

		/// <summary>Card text (HearthDb, game language) for the generic draw rule; cached per id.</summary>
		private static readonly Dictionary<string, string> TextCache = new Dictionary<string, string>();

		public static string CardText(string cardId)
		{
			if(cardId == null) return null;
			lock(TextCache)
			{
				if(TextCache.TryGetValue(cardId, out var cached)) return cached;
				string text = null;
				try
				{
					if(HearthDb.Cards.All.TryGetValue(cardId, out var c))
						text = c.GetLocText(HearthDb.Enums.Locale.enUS) ?? c.Text;
				}
				catch { }
				TextCache[cardId] = text;
				return text;
			}
		}

		/// <summary>Active HDT deck: id, name and its distinct cards (from the selected deck version).</summary>
		public static (string id, string name, List<DeckCardInfo> cards) ActiveDeck()
		{
			try
			{
				var deck = Hearthstone_Deck_Tracker.DeckList.Instance.ActiveDeck;
				if(deck == null) return (null, null, new List<DeckCardInfo>());
				var version = Hearthstone_Deck_Tracker.DeckList.Instance.ActiveDeckVersion ?? deck;
				var cards = version.Cards
					.GroupBy(c => c.Id)
					.Select(g => new DeckCardInfo { Id = g.Key, Name = g.First().LocalizedName ?? g.First().Name, Cost = g.First().Cost, Copies = g.Sum(c => c.Count) })
					.OrderBy(c => c.Cost).ThenBy(c => c.Name)
					.ToList();
				return (deck.DeckId.ToString(), deck.Name, cards);
			}
			catch(Exception ex)
			{
				ProbeLog.Line("ERR", "ActiveDeck: " + ex.Message);
				return (null, null, new List<DeckCardInfo>());
			}
		}

		public static bool IsShadowSpell(string cardId)
		{
			try
			{
				return cardId != null && HearthDb.Cards.All.TryGetValue(cardId, out var c)
				       && c.Type == CardType.SPELL && c.SpellSchool == (int)SpellSchool.SHADOW;
			}
			catch { return false; }
		}

		/// <summary>Attacks this character can still make this turn (0 if exhausted, frozen, can't attack, or a fresh Rush).</summary>
		private static int AttacksLeft(Entity e)
		{
			if(e.GetTag(GameTag.FROZEN) > 0 || e.GetTag(GameTag.CANT_ATTACK) > 0 || e.GetTag(GameTag.DORMANT) > 0 || e.GetTag(GameTag.EXHAUSTED) > 0)
				return 0;
			// Rush without Charge on its first turn can only hit minions.
			if(e.IsMinion && e.GetTag(GameTag.RUSH) > 0 && e.GetTag(GameTag.CHARGE) == 0 && e.GetTag(GameTag.NUM_TURNS_IN_PLAY) == 0)
				return 0;
			var per = e.GetTag(GameTag.WINDFURY) > 1 ? e.GetTag(GameTag.WINDFURY) : e.GetTag(GameTag.WINDFURY) > 0 ? 2 : 1;
			return Math.Max(0, per - e.GetTag(GameTag.NUM_ATTACKS_THIS_TURN));
		}

		/// <summary>Live board/hand/mana for the lethal check, or null when it isn't the player's turn.</summary>
		public static LethalInput BuildLethalInput(List<HandCard> hand)
		{
			var game = HdtApi.Core.Game;
			var player = game?.Player;
			var opp = game?.Opponent;
			if(player == null || opp == null || game.PlayerEntity == null || !game.PlayerEntity.IsCurrentPlayer)
				return null;
			var mine = player.Board.ToList();
			var theirs = opp.Board.ToList();
			var x = new LethalInput();
			foreach(var e in mine.Where(e => e.IsMinion && e.Attack > 0))
			{
				var left = AttacksLeft(e);
				if(left > 0) x.Minions.Add(new LethalAttacker { Name = SafeName(e) ?? e.CardId, Attack = e.Attack, Attacks = left });
			}
			var hero = mine.FirstOrDefault(e => e.IsHero);
			if(hero != null)
			{
				x.HeroAttack = hero.Attack;
				var weapon = mine.FirstOrDefault(e => e.IsWeapon);
				x.HeroAttacksLeft = AttacksLeft(hero);
				if(weapon != null && weapon.GetTag(GameTag.WINDFURY) > 0 && hero.GetTag(GameTag.WINDFURY) == 0)
					x.HeroAttacksLeft = Math.Max(0, 2 - hero.GetTag(GameTag.NUM_ATTACKS_THIS_TURN));
			}
			var oppHero = theirs.FirstOrDefault(e => e.IsHero);
			x.OppHealth = oppHero?.Health ?? 0;
			x.OppArmor = oppHero?.GetTag(GameTag.ARMOR) ?? 0;
			x.OppImmune = oppHero != null && oppHero.GetTag(GameTag.IMMUNE) > 0;
			var enemyMinions = theirs.Where(e => e.IsMinion && e.GetTag(GameTag.DORMANT) == 0).ToList();
			x.EnemyTaunts = enemyMinions.Count(e => e.GetTag(GameTag.TAUNT) > 0 && e.GetTag(GameTag.STEALTH) == 0);
			var pe = game.PlayerEntity;
			x.Mana = Math.Max(0, pe.GetTag(GameTag.RESOURCES) + pe.GetTag(GameTag.TEMP_RESOURCES) - pe.GetTag(GameTag.RESOURCES_USED) - pe.GetTag(GameTag.OVERLOAD_LOCKED));
			var spellDamage = mine.Where(e => e.IsMinion || e.IsHero || e.IsWeapon).Sum(e => e.GetTag(GameTag.SPELLPOWER)); // not enchantments (they can carry the tag too)
			foreach(var h in hand)
			{
				try
				{
					if(h.CardId == null || !HearthDb.Cards.All.TryGetValue(h.CardId, out var c)) continue;
					var atk = game.Entities.TryGetValue(h.EntityId, out var he) ? he.Attack : c.Attack;
					var card = LethalEngine.ParseCard(h.Name ?? h.CardId, c.Type.ToString(), h.Cost, CardText(h.CardId), atk, spellDamage, enemyMinions.Count == 0);
					if(card != null) x.Hand.Add(card);
				}
				catch { }
			}
			return x;
		}

		/// <summary>Builds the pure-math input from live HDT state.</summary>
		public static OddsState BuildOddsState(List<HandCard> hand, ISet<string> group)
		{
			var player = Player;
			return new OddsState
			{
				Hand = hand.Select(h => new OddsHandCard { EntityId = h.EntityId, CardId = h.CardId, Name = h.Name ?? h.CardId, Cost = h.Cost, InGroup = h.IsTarget }).ToList(),
				Deck = RemainingDeck(),
				DeckCount = Math.Max(0, (player?.DeckCount ?? 0) - CastsWhenDrawnInDeck()),
				Group = group,
				IsShadowSpell = IsShadowSpell,
				CardText = CardText
			};
		}
	}
}
