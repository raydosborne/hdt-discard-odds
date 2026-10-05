using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace DiscardOdds
{
	/// <summary>A friendly character that can still attack this turn.</summary>
	public sealed class LethalAttacker
	{
		public string Name;
		public int Attack;
		public int Attacks; // attacks left this turn (windfury = 2)
	}

	/// <summary>A card in hand that can put damage on the enemy hero this turn if played.</summary>
	public sealed class LethalCard
	{
		public string Name;
		public int Cost;
		public int Damage;
		public bool Approx;    // may not all reach the face (random split with enemy minions up, conditional upgrade, ...)
		public bool ViaAttack; // Charge minion / weapon: needs to attack, so enemy Taunt blocks it
		public bool IsWeapon;  // replaces the hero's current attack instead of adding to it
	}

	/// <summary>Attack-relevant tags of one friendly character (filled from HDT entity tags by GameReader).</summary>
	public sealed class AttackState
	{
		public bool IsHero;
		public bool Exhausted;       // EXHAUSTED tag
		public int TurnsInPlay;      // NUM_TURNS_IN_PLAY (0 = played/summoned this turn)
		public bool Charge, Rush, Frozen, CantAttack, Dormant, Windfury, MegaWindfury;
		public bool TitanLocked;     // Titan with abilities left (can't attack yet)
		public int AttacksThisTurn;  // NUM_ATTACKS_THIS_TURN
	}

	public sealed class LethalInput
	{
		/// <summary>Friendly characters with attack that can't attack this turn, with the reason (shown in the details).</summary>
		public List<string> NotCounted = new List<string>();
		public List<LethalAttacker> Minions = new List<LethalAttacker>();
		public int HeroAttack;
		public int HeroAttacksLeft;
		public List<LethalCard> Hand = new List<LethalCard>();
		public int Mana;
		public int OppHealth;
		public int OppArmor;
		public bool OppImmune;
		public int EnemyTaunts;
	}

	public sealed class LethalResult
	{
		public int Board, Hero, Burn, Total, Target, BlockedByTaunt;
		public bool Approx, Lethal;
		public List<string> Used = new List<string>(); // cards from hand counted as "hand" damage
		public string Weapon;                          // weapon from hand counted as the hero's attack
		public string Line;    // "Face damage: 14 vs 13 health → LETHAL (all face)"
		public string Detail;  // "board 8 + hero 3 + burn 3 (Soulfire)"
	}

	/// <summary>
	/// Factual "how much damage can reach the enemy hero this turn" count (no advice, no sequencing). Pure: no HDT
	/// types, unit-tested. First version, deliberately simple:
	///  - board: minions/hero that can still attack (callers already drop exhausted / frozen / just-played Rush),
	///    windfury counted; any enemy Taunt blocks ALL attack damage (shown, not guessed around);
	///  - hand: the best set of castable cards by mana (0/1 knapsack): direct "Deal N damage" to the face or any
	///    target, Charge minions, a weapon (replaces the hero's attack). Spell Damage is added to "$N" spell damage.
	///  - "≈" when a counted card might not all go face (random split with enemy minions up, "instead" upgrades,
	///    Combo/If conditions).
	/// Not counted: hero powers, battlecry buffs, cost changes from playing cards, board-space limits, secrets.
	/// </summary>
	public static class LethalEngine
	{
		private static readonly Regex Tags = new Regex(@"<[^>]+>|\[x\]", RegexOptions.CultureInvariant);
		private static readonly Regex Space = new Regex(@"\s+", RegexOptions.CultureInvariant);
		private static readonly Regex DealRx = new Regex(@"\bdeal (\$?)(\d+) damage([^.;]*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
		private static readonly Regex ChargeRx = new Regex(@"(^|[.\s])Charge\b", RegexOptions.CultureInvariant);
		// Damage that only happens later / on another trigger: not "if you play it now".
		private static readonly Regex SkipBefore = new Regex(@"\b(Deathrattle|Secret|At the end|At the start|Whenever|After|Spellburst|Frenzy|Overkill|Inspire|Honorable Kill|Infuse|Excavate|Quest|Questline|When you draw|Dormant)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
		// Conditional: may or may not happen when played.
		private static readonly Regex ApproxBefore = new Regex(@"\b(If|Combo|Outcast|Corrupt|Choose One|Discover|Forge|Finale|Tradeable)\b", RegexOptions.CultureInvariant);

		/// <summary>
		/// Attacks left this turn, following HDT's own BoardDamage rules: a non-hero that is EXHAUSTED or has
		/// NUM_TURNS_IN_PLAY == 0 was played this turn and can only attack with Charge (Rush can't hit the hero). The game
		/// does not always send EXHAUSTED for a minion played this turn, so the EXHAUSTED tag alone is not enough.
		/// </summary>
		public static int AttacksLeft(AttackState s) => NoAttackReason(s) != null ? 0 : Math.Max(0, AttacksPerTurn(s) - s.AttacksThisTurn);

		private static int AttacksPerTurn(AttackState s) => s.MegaWindfury ? 4 : s.Windfury ? 2 : 1;

		/// <summary>Why a character can't attack the enemy hero now, or null if it can.</summary>
		public static string NoAttackReason(AttackState s)
		{
			if(s.Frozen) return "frozen";
			if(s.CantAttack) return "can't attack";
			if(s.Dormant) return "dormant";
			if(s.TitanLocked) return "Titan abilities left";
			var justPlayed = s.Exhausted || (!s.IsHero && s.TurnsInPlay == 0);
			if(justPlayed && !(s.Charge && !s.IsHero && s.AttacksThisTurn == 0))
				return s.IsHero ? "already attacked" : s.Rush ? "Rush, played this turn" : "played this turn";
			if(s.AttacksThisTurn >= AttacksPerTurn(s)) return "already attacked";
			return null;
		}

		public static string Clean(string text) => Space.Replace(Tags.Replace(text ?? "", " ").Replace('\n', ' '), " ").Trim();

		/// <summary>
		/// What one card in hand could add to face damage this turn, or null if nothing.
		/// type: HearthDb card type name ("SPELL", "MINION", "WEAPON", ...); attack: the card's current Attack in hand.
		/// </summary>
		public static LethalCard ParseCard(string name, string type, int cost, string text, int attack, int spellDamage, bool enemyBoardEmpty)
		{
			var t = Clean(text);
			if(type == "WEAPON")
				return attack > 0 ? new LethalCard { Name = name, Cost = cost, Damage = attack, ViaAttack = true, IsWeapon = true } : null;

			var damage = 0;
			var approx = false;
			var viaAttack = false;
			if(type == "MINION" && attack > 0 && ChargeRx.IsMatch(t))
			{
				damage += attack;
				viaAttack = true;
				approx |= Regex.IsMatch(t, @"Choose One|\bgains? Charge\b|\bIf\b.*Charge", RegexOptions.IgnoreCase); // conditional Charge
			}
			var m = DealRx.Match(t);
			if(m.Success)
			{
				var before = t.Substring(0, m.Index);
				var sentence = before.Substring(Math.Max(0, before.LastIndexOf('.') + 1));
				if(!SkipBefore.IsMatch(before) || Regex.IsMatch(sentence, @"\bWhen you play\b", RegexOptions.IgnoreCase))
				{
					var n = int.Parse(m.Groups[2].Value) + (m.Groups[1].Value == "$" && type == "SPELL" ? Math.Max(0, spellDamage) : 0);
					var rest = m.Groups[3].Value.Trim().ToLowerInvariant();
					bool face;
					var a = ApproxBefore.IsMatch(sentence) || t.IndexOf("instead", m.Index, StringComparison.OrdinalIgnoreCase) >= 0;
					if(rest.Contains("random") || rest.Contains("split"))
					{
						// "randomly split among all enemies" / "to a random enemy": all face only when they have no minions
						face = !rest.Contains("minion") && !rest.Contains("your") && !rest.Contains("friendly");
						if(face && !enemyBoardEmpty) a = true;
					}
					else if(rest.Length == 0)
						face = true; // "Deal 6 damage." = any target
					else if(rest.Contains("your hero") || rest.Contains("friendly"))
						face = false;
					else if(rest.Contains("minion"))
						face = rest.Contains("enemy hero") || rest.Contains("their hero");
					else
						face = rest.Contains("enem") || rest.Contains("character") || rest.Contains("hero") || rest.Contains("opponent");
					if(face)
					{
						damage += n;
						approx |= a;
					}
				}
			}
			return damage > 0 ? new LethalCard { Name = name, Cost = cost, Damage = damage, Approx = approx, ViaAttack = viaAttack } : null;
		}

		public static LethalResult Compute(LethalInput x)
		{
			var r = new LethalResult { Target = Math.Max(0, x.OppHealth) + Math.Max(0, x.OppArmor) };
			var taunt = x.EnemyTaunts > 0;
			var boardRaw = x.Minions.Sum(a => Math.Max(0, a.Attack) * Math.Max(0, a.Attacks));
			var heroRaw = x.HeroAttack > 0 ? x.HeroAttack * Math.Max(0, x.HeroAttacksLeft) : 0;
			r.BlockedByTaunt = taunt ? boardRaw + heroRaw : 0;
			var board = taunt ? 0 : boardRaw;
			var mana = Math.Max(0, Math.Min(x.Mana, 30));

			var castable = x.Hand.Where(c => c.Damage > 0 && c.Cost <= mana && !(taunt && c.ViaAttack)).ToList();
			var spells = castable.Where(c => !c.IsWeapon).ToList();
			var weapons = castable.Where(c => c.IsWeapon && x.HeroAttacksLeft > 0).ToList();

			var best = (total: -1, hero: 0, burn: 0, used: new List<LethalCard>());
			foreach(var w in new LethalCard[] { null }.Concat(weapons))
			{
				var left = mana - (w?.Cost ?? 0);
				var hero = taunt ? 0 : w != null ? w.Damage : heroRaw;
				var (burn, used) = Knapsack(spells, left);
				var total = board + hero + burn;
				var better = total > best.total
				             || (total == best.total && used.Count(c => c.Approx) < best.used.Count(c => c.Approx));
				if(better) { best = (total, hero, burn, used); r.Weapon = w?.Name; }
			}
			r.Board = board;
			r.Hero = best.hero;
			r.Burn = best.burn;
			r.Total = best.total;
			r.Approx = best.used.Any(c => c.Approx);
			r.Used = best.used.Select(c => c.Name).ToList();
			r.Lethal = !x.OppImmune && r.Total >= r.Target && r.Target > 0;
			Format(r, x);
			return r;
		}

		/// <summary>Max damage from a subset of cards within the mana budget (0/1 knapsack; fewest ≈ cards on ties).</summary>
		private static (int damage, List<LethalCard> used) Knapsack(List<LethalCard> cards, int mana)
		{
			mana = Math.Max(0, mana);
			var best = new (int dmg, int approx, List<LethalCard> used)[mana + 1];
			for(var i = 0; i <= mana; i++) best[i] = (0, 0, new List<LethalCard>());
			foreach(var c in cards)
			{
				var cost = Math.Max(0, c.Cost);
				for(var budget = mana; budget >= cost; budget--)
				{
					var prev = best[budget - cost];
					var cand = (dmg: prev.dmg + c.Damage, approx: prev.approx + (c.Approx ? 1 : 0));
					if(cand.dmg > best[budget].dmg || (cand.dmg == best[budget].dmg && cand.approx < best[budget].approx))
						best[budget] = (cand.dmg, cand.approx, new List<LethalCard>(prev.used) { c });
				}
			}
			return (best[mana].dmg, best[mana].used);
		}

		private static void Format(LethalResult r, LethalInput x)
		{
			var total = (r.Approx ? "≈" : "") + r.Total;
			var target = x.OppArmor > 0 ? $"{r.Target} ({x.OppHealth} health + {x.OppArmor} armor)" : $"{r.Target} health";
			if(x.OppImmune)
				r.Line = $"Face damage: {total} vs {target} (enemy hero is Immune)";
			else if(r.Lethal)
				r.Line = $"Face damage: {total} vs {target} → LETHAL{(r.Approx ? "?" : "")} (all face)";
			else
				r.Line = $"Face damage: {total} vs {target} ({r.Target - r.Total} short)";
			var parts = new List<string>();
			if(r.Board > 0) parts.Add($"board {r.Board}");
			if(r.Hero > 0) parts.Add($"hero {r.Hero}" + (r.Weapon != null ? $" (equip {r.Weapon})" : ""));
			if(r.Burn > 0) parts.Add($"hand {r.Burn} ({string.Join(", ", r.Used)})");
			var detail = parts.Count > 0 ? string.Join(" + ", parts) : "nothing can reach face";
			if(r.BlockedByTaunt > 0 || x.EnemyTaunts > 0)
				detail += $" · enemy Taunt ×{x.EnemyTaunts}: {r.BlockedByTaunt} attack damage can't go face";
			if(x.NotCounted.Count > 0) detail += " · not counted: " + string.Join(", ", x.NotCounted);
			if(r.Approx) detail += " · ≈ some counted damage may not hit face";
			r.Detail = detail;
		}
	}
}
