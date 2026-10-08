using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DiscardOdds;

internal static class Program
{
	private static int _fail;

	private static void Check(string name, double actual, double expected, double tol = 1e-4)
	{
		var ok = Math.Abs(actual - expected) <= tol;
		if(!ok) _fail++;
		Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}: {actual:0.0000} (expected {expected:0.0000})");
	}

	private static void CheckTrue(string name, bool ok)
	{
		if(!ok) _fail++;
		Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}");
	}

	private static double C(int n, int k)
	{
		if(k < 0 || k > n) return 0;
		double r = 1;
		for(var i = 1; i <= k; i++) r = r * (n - k + i) / i;
		return r;
	}

	private static OddsHandCard H(int id, string card, int cost, bool payoff) => new OddsHandCard { EntityId = id, CardId = card, Name = card, Cost = cost, InGroup = payoff };

	private static int Main()
	{
		var payoffs = new HashSet<string>(TargetConfig.CreateDefault().Presets[0].Targets.Select(t => t.Id));
		var rules = OddsRule.CardRules;

		// Hypergeometric vs closed form
		Check("P>=1 of 3, 8/24", OddsEngine.PAtLeastOne(8, 24, 3), 1 - C(16, 3) / C(24, 3));
		Check("P>=1 of 3, 10/25", OddsEngine.PAtLeastOne(10, 25, 3), 1 - C(15, 3) / C(25, 3));
		Check("P>=1 of 3, 10/30 (opening going first)", OddsEngine.PAtLeastOne(10, 30, 3), 0.7192, 1e-4);
		Check("edge: k>=m", OddsEngine.PAtLeastOne(1, 2, 3), 1);
		Check("edge: n=0", OddsEngine.PAtLeastOne(0, 20, 3), 0);

		// Deck: 24 cards, 8 payoffs (4 ids x2), 16 others (8 ids x2)
		var deck = new Dictionary<string, int> { ["RLK_534"] = 2, ["RLK_532"] = 2, ["BT_300"] = 2, ["KAR_205"] = 2 };
		for(var i = 0; i < 8; i++) deck["X" + i] = 2;
		var s = new OddsState { Deck = deck, DeckCount = 24, Group = payoffs };

		// Platysaur: draw 1 -> 8/24
		Check("Platysaur hit", OddsEngine.Compute(rules["TLC_603"], s).Hit, 8.0 / 24);
		// Soularium: >=1 in 3
		Check("Soularium hit", OddsEngine.Compute(rules["BOT_568"], s).Hit, 1 - C(16, 3) / C(24, 3));
		// Unknown cards in deck (DeckCount > known) count as misses
		var s2 = new OddsState { Deck = deck, DeckCount = 26, Group = payoffs };
		Check("Platysaur with 2 unknown", OddsEngine.Compute(rules["TLC_603"], s2).Hit, 8.0 / 26);
		// Catacombs model: 12 distinct ids, 4 payoff ids, 3 offered
		Check("Catacombs (distinct-id model)", OddsEngine.Compute(rules["TLC_451"], s).Hit, 1 - C(8, 3) / C(12, 3));

		// Hand outlets
		s.Hand = new List<OddsHandCard>
		{
			H(1, "DMF_119", 1, false), // Wicked Whispers (played)
			H(2, "GAME_005", 0, false), // Coin
			H(3, "TLC_451", 0, false), // Catacombs
			H(4, "RLK_532", 3, true),  // Walking Dead
			H(5, "BT_300", 6, true),   // Hand of Gul'dan
			H(6, "CATA_493", 4, false) // Duke
		};
		Check("Wicked Whispers: tie Coin/Catacombs at 0 -> 0%", OddsEngine.Compute(rules["DMF_119"], s, 1).Hit, 0);
		s.Hand.RemoveAll(h => h.EntityId == 2 || h.EntityId == 3);
		Check("Wicked Whispers: lowest is Walking Dead -> 100%", OddsEngine.Compute(rules["DMF_119"], s, 1).Hit, 1);
		s.Hand.Add(H(7, "X0", 3, false));
		Check("Wicked Whispers: tie WD/X0 at 3 -> 50%", OddsEngine.Compute(rules["DMF_119"], s, 1).Hit, 0.5);
		var wwTie = OddsEngine.Compute(rules["DMF_119"], s, 1);
		CheckTrue("tie: lowest rule lists every tied card (WD bold target + X0)", wwTie.DiscardRule == "lowest" && wwTie.DiscardNames.Count == 2
			&& wwTie.DiscardNames.Any(x => x.name == "RLK_532" && x.target) && wwTie.DiscardNames.Any(x => x.name == "X0" && !x.target));
		s.Hand.Add(H(8, "ULD_163", 2, false)); // Expired Merchant in hand
		Check("Expired Merchant: highest is Gul'dan -> 100%", OddsEngine.Compute(rules["ULD_163"], s, 8).Hit, 1);
		var em = OddsEngine.Compute(rules["ULD_163"], s, 8);
		CheckTrue("highest rule names the single top card", em.DiscardRule == "highest" && em.DiscardNames.Count == 1 && em.DiscardNames[0].name == "BT_300");
		s.Hand.Add(H(9, "EX1_308", 1, false)); // Soulfire
		// others for Soulfire: WW, WD, HoG, Duke, X0, Merchant = 6 cards, 2 payoffs
		Check("Soulfire random: 2/6", OddsEngine.Compute(rules["EX1_308"], s, 9).Hit, 2.0 / 6);
		s.Hand.Add(H(10, "WON_103", 3, false)); // Chamber
		// others: 7 cards (WW, WD, HoG, Duke, X0, Merchant, Soulfire), 2 payoffs, sees 3
		Check("Chamber look3: 1-C(5,3)/C(7,3)", OddsEngine.Compute(rules["WON_103"], s, 10).Hit, 1 - C(5, 3) / C(7, 3));
		Check("Ocular choose: payoff in hand -> 100%", OddsEngine.Compute(new OddsRule { CardId = "CATA_490", Name = "Ocular", Kind = OddsKind.DiscardChoose }, s).Hit, 1);

		// Sketch Artist: Shadow spells in deck = Soul Barrage x2, Gul'dan x2 (payoffs) + X0,X1 x2 (pretend shadow non-payoff)
		s.IsShadowSpell = id => id == "RLK_534" || id == "BT_300" || id == "X0" || id == "X1";
		Check("Sketch Artist: 4 payoff of 8 shadow", OddsEngine.Compute(rules["TOY_916"], s).Hit, 0.5);

		// Widget position: default is clear of both HDT deck lists; a user-saved position takes precedence.
		var def = new PluginSettings();
		Check("default left = 0.22", def.WidgetLeftFraction, PluginSettings.DefaultLeftFraction);
		Check("default top = 0.64", def.WidgetTopFraction, PluginSettings.DefaultTopFraction);
		CheckTrue("default left clear of opponent deck list (>= 0.15)", def.WidgetLeftFraction >= 0.15);
		CheckTrue("default widget ends left of center (0.22 + ~430px/1920 < 0.5)", def.WidgetLeftFraction + 430.0 / 1920 < 0.5);
		CheckTrue("default top in lower half, above hand (0.5..0.75)", def.WidgetTopFraction > 0.5 && def.WidgetTopFraction < 0.75);
		CheckTrue("default not marked saved", !def.WidgetPositionSaved);
		var saved = PluginSettings.Parse(new[] { "WidgetLeftFraction=0.4", "WidgetTopFraction=0.1", "WidgetPositionSaved=True" });
		Check("saved left wins", saved.WidgetLeftFraction, 0.4);
		Check("saved top wins", saved.WidgetTopFraction, 0.1);
		var legacyDefault = PluginSettings.Parse(new[] { "WidgetLeftFraction=0.02", "WidgetTopFraction=0.35", "WidgetEnabled=True" });
		Check("legacy file with old default -> new default (left)", legacyDefault.WidgetLeftFraction, PluginSettings.DefaultLeftFraction);
		Check("legacy file with old default -> new default (top)", legacyDefault.WidgetTopFraction, PluginSettings.DefaultTopFraction);
		var legacyMoved = PluginSettings.Parse(new[] { "WidgetLeftFraction=0.3", "WidgetTopFraction=0.2" });
		Check("legacy file with moved widget keeps it (left)", legacyMoved.WidgetLeftFraction, 0.3);
		CheckTrue("legacy moved -> marked saved", legacyMoved.WidgetPositionSaved);
		var notSaved = PluginSettings.Parse(new[] { "WidgetLeftFraction=0.3", "WidgetTopFraction=0.2", "WidgetPositionSaved=False" });
		Check("explicit not-saved -> default", notSaved.WidgetLeftFraction, PluginSettings.DefaultLeftFraction);
		saved.ResetPosition();
		Check("reset -> default left", saved.WidgetLeftFraction, PluginSettings.DefaultLeftFraction);
		CheckTrue("reset -> not saved", !saved.WidgetPositionSaved);

		// ---------------------------------------------------------------- generic draw text (HearthstoneJSON markup)
		void Draw(string name, string text, int? count, bool cond = false)
		{
			var d = DrawText.Parse(text);
			var ok = count == null ? d == null : d != null && d.Count == count && d.Conditional == cond;
			if(!ok) _fail++;
			Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  draw text {name}: {(d == null ? "none" : $"draw {d.Count}{(d.Conditional ? " (conditional: " + d.Clause + ")" : "")}")}");
		}
		Draw("Arcane Intellect", "Draw 2 cards.", 2);
		Draw("Kobold Librarian", "<b>Battlecry:</b> Draw a card. Deal 2 damage to your hero.", 1);
		Draw("Sprint", "Draw 4 cards.", 4);
		Draw("Shield Block", "Gain 5 Armor.\nDraw a card.", 1);
		Draw("Hand of Gul'dan text", "When you play or discard this, draw 3 cards.", 3, true);
		Draw("Loot Hoarder", "<b>Deathrattle:</b> Draw a card.", 1, true);
		Draw("Fiery War Axe-like (none)", "Deal $3 damage.", null);
		Draw("Draw a minion (type-filtered, skipped)", "<b>Battlecry:</b> Draw a minion.", null);
		Draw("Opponent deck (skipped)", "Draw a card from your opponent's deck.", null);
		Draw("[x] markup", "[x]<b>Battlecry:</b> Draw\ntwo cards.", 2);
		Draw("If-condition", "Deal $2 damage. If it dies, draw a card.", 1, true);
		Draw("For each (variable)", "Draw a card for each enemy minion.", 1, true);
		Draw("Draws (other player)", "Each player draws 2 cards.", null);
		Draw("keyword prefix (Azure Drake)", "<b>Spell Damage +1</b>\n<b>Battlecry:</b> Draw a card.", 1);
		Draw("compound effect (Cryosleep-like)", "Deal $4 damage and draw a card.", 1);
		Draw("Choose One (conditional)", "<b>Choose One -</b> Draw 2 cards; or Restore #5 Health.", 2, true);
		Draw("granted text in quotes (skip-ish, conditional)", "Give your minions \"<b>Deathrattle:</b> Draw a card.\"", 1, true);
		Draw("Whenever you draw (trigger, skipped)", "Whenever you draw a card, reduce its Cost by (1).", null);
		Draw("After you draw, then a real draw", "After you draw a card, 50% chance to draw another. Draw a card.", 1);

		// Generic rule in ForHand: unknown card id with draw text -> hit/miss for >=1 target; specific rule wins over text.
		var gs = new OddsState { Deck = deck, DeckCount = 24, Group = payoffs, CardText = id => id == "CS2_023" ? "Draw 2 cards." : id == "BOT_568" ? "Draw a card." : null };
		gs.Hand = new List<OddsHandCard> { H(21, "CS2_023", 3, false), H(22, "BOT_568", 0, false), H(23, "X5", 2, false) };
		var go = OddsEngine.ForHand(gs, rules);
		var ai = go.FirstOrDefault(o => o.CardId == "CS2_023");
		Check("generic Draw 2: >=1 target of 8/24 in 2", ai?.Hit ?? -1, 1 - C(16, 2) / C(24, 2));
		Check("specific rule wins (Soularium draws 3, not text)", go.First(o => o.CardId == "BOT_568").Hit, 1 - C(16, 3) / C(24, 3));
		CheckTrue("card without draw text gets no line", go.All(o => o.CardId != "X5"));
		CheckTrue("OddsRule.ForCard generic flag", OddsRule.ForCard("CS2_023", "Arcane Intellect", "Draw 2 cards.")?.Generic == true);

		// ---------------------------------------------------------------- targets.json
		var defCfg = TargetConfig.CreateDefault();
		CheckTrue("default preset excludes Boneweb Egg (SCH_147)", defCfg.Presets.All(p => p.Targets.All(t => t.Id != "SCH_147")));
		CheckTrue("default config has no deck lists (nothing hard-coded per deck)", defCfg.Decks.Count == 0);
		var discardDeck = new[] { "RLK_534", "RLK_532", "BT_300", "CATA_493", "TLC_603" };
		var auto = defCfg.Resolve("guid-1", "My Discard", discardDeck);
		CheckTrue("preset auto-applies at 3+ matches", auto.Source == "preset" && auto.Ids.SetEquals(payoffs));
		var noMatch = defCfg.Resolve("guid-2", "Some Mage deck", new[] { "CS2_023", "CS2_029", "RLK_534" });
		CheckTrue("unrelated deck: no targets", noMatch.Source == "none" && noMatch.Ids.Count == 0);
		defCfg.SetDeckTargets("guid-2", "Some Mage deck", new[] { new TargetCard { Id = "CS2_029", Name = "Fireball" }, new TargetCard { Id = "CS2_029" } });
		var own = defCfg.Resolve("guid-2", "Some Mage deck", new[] { "CS2_029" });
		CheckTrue("own deck list wins, de-duplicated", own.Source == "deck" && own.Ids.Count == 1 && own.Ids.Contains("CS2_029"));
		CheckTrue("deck found by name when id changed (re-import)", defCfg.FindDeck("guid-NEW", "some mage deck") != null);
		defCfg.SetDeckTargets("guid-1", "My Discard", new TargetCard[0]);
		CheckTrue("explicit empty list beats preset", defCfg.Resolve("guid-1", "My Discard", discardDeck).Source == "deck" && defCfg.Resolve("guid-1", "My Discard", discardDeck).Ids.Count == 0);
		CheckTrue("remove deck list -> preset again", defCfg.RemoveDeck("guid-1", null) && defCfg.Resolve("guid-1", "My Discard", discardDeck).Source == "preset");
		var json = defCfg.ToJson();
		var round = TargetConfig.Parse(json);
		CheckTrue("JSON round trip keeps decks/presets", round.Decks.Count == 1 && round.Decks[0].Targets[0].Name == "Fireball" && round.Presets.Count == 1 && round.Presets[0].AutoApplyMinMatches == 3 && round.Presets[0].Targets.Count == 5);
		var hand = TargetConfig.Parse("{ \"decks\": [ { \"deckName\": \"Quick \\\"edit\\\"\", \"targets\": [\"EX1_001\", {\"id\": \"EX1_002\"}] } ] }");
		CheckTrue("hand-written JSON: string ids and objects, escaped quotes", hand.Decks[0].DeckName == "Quick \"edit\"" && hand.Decks[0].Targets.Count == 2);
		var badThrows = false;
		try { TargetConfig.Parse("{ \"decks\": [ }"); } catch(FormatException) { badThrows = true; }
		CheckTrue("malformed JSON -> FormatException", badThrows);
		var tmp = Path.Combine(Path.GetTempPath(), "discardodds_test_" + Guid.NewGuid().ToString("N"));
		var path = Path.Combine(tmp, "targets.json");
		var created = TargetConfig.Load(path, out var err1);
		CheckTrue("missing file -> created with defaults", err1 == null && File.Exists(path) && created.Presets.Count == 1);
		created.SetDeckTargets("g", "D", new[] { new TargetCard { Id = "A_1" } });
		created.Save(path);
		var reloaded = TargetConfig.Load(path, out var err2);
		CheckTrue("save/load round trip + .bak kept", err2 == null && reloaded.Decks.Count == 1 && File.Exists(path + ".bak"));
		File.WriteAllText(path, "{ broken");
		var fallback = TargetConfig.Load(path, out var err3);
		CheckTrue("broken file -> defaults in memory, error reported, file untouched", err3 != null && fallback.Presets.Count == 1 && File.ReadAllText(path) == "{ broken");
		try { Directory.Delete(tmp, true); } catch { }

		// ---- auto-update logic (pure; the network/file side lives in Updater.cs)
		CheckTrue("tag v0.1.0 -> 0.1.0", UpdateLogic.ParseTag("v0.1.0") == new Version(0, 1, 0));
		CheckTrue("tag 1.2 -> 1.2.0", UpdateLogic.ParseTag("1.2") == new Version(1, 2, 0));
		CheckTrue("pre-release / junk tags rejected", UpdateLogic.ParseTag("v1.0.0-beta") == null && UpdateLogic.ParseTag("latest") == null && UpdateLogic.ParseTag(null) == null);
		CheckTrue("newer: 0.1.1 > 0.1.0.0 (assembly 4-part)", UpdateLogic.IsNewer(new Version(0, 1, 1), new Version(0, 1, 0, 0)));
		CheckTrue("same version is not newer", !UpdateLogic.IsNewer(new Version(0, 1, 0), new Version(0, 1, 0, 0)));
		CheckTrue("older is not newer (no downgrade)", !UpdateLogic.IsNewer(new Version(0, 1, 0), new Version(0, 2, 0)));
		CheckTrue("0.10.0 > 0.9.0 (numeric, not string)", UpdateLogic.IsNewer(UpdateLogic.ParseTag("v0.10.0"), UpdateLogic.ParseTag("v0.9.0")));
		const string dl = "https://github.com/raydosborne/hdt-discard-odds/releases/download/v0.2.0/";
		CheckTrue("trusted asset URL", UpdateLogic.IsTrustedAssetUrl(dl + "DiscardOdds.dll", "DiscardOdds.dll"));
		CheckTrue("other repo / http / wrong name / traversal rejected",
			!UpdateLogic.IsTrustedAssetUrl("https://github.com/someone/hdt-discard-odds/releases/download/v0.2.0/DiscardOdds.dll", "DiscardOdds.dll")
			&& !UpdateLogic.IsTrustedAssetUrl("http://github.com/raydosborne/hdt-discard-odds/releases/download/v0.2.0/DiscardOdds.dll", "DiscardOdds.dll")
			&& !UpdateLogic.IsTrustedAssetUrl(dl + "Evil.dll", "DiscardOdds.dll")
			&& !UpdateLogic.IsTrustedAssetUrl(dl + "../../x/DiscardOdds.dll", "DiscardOdds.dll")
			&& !UpdateLogic.IsTrustedAssetUrl("https://github.com/raydosborne/hdt-discard-odds/releases/download.evil.com/DiscardOdds.dll", "DiscardOdds.dll"));
		string Rel(string tag, bool pre, bool withSum) =>
			"{\"tag_name\":\"" + tag + "\",\"draft\":false,\"prerelease\":" + (pre ? "true" : "false") + ",\"html_url\":\"https://github.com/raydosborne/hdt-discard-odds/releases/tag/" + tag + "\",\"assets\":[" +
			"{\"name\":\"DiscardOdds.dll\",\"size\":61440,\"browser_download_url\":\"" + "https://github.com/raydosborne/hdt-discard-odds/releases/download/" + tag + "/DiscardOdds.dll\"}" +
			(withSum ? ",{\"name\":\"DiscardOdds.dll.sha256\",\"size\":82,\"browser_download_url\":\"https://github.com/raydosborne/hdt-discard-odds/releases/download/" + tag + "/DiscardOdds.dll.sha256\"}" : "") + "]}";
		var rel = UpdateLogic.ParseRelease(Rel("v0.2.0", false, true));
		CheckTrue("release JSON parsed (tag, both assets, size)", rel != null && rel.Version == new Version(0, 2, 0) && rel.DllUrl.EndsWith("/v0.2.0/DiscardOdds.dll") && rel.ChecksumUrl.EndsWith(".sha256") && rel.DllSize == 61440);
		CheckTrue("release without checksum asset is ignored", UpdateLogic.ParseRelease(Rel("v0.2.0", false, false)) == null);
		CheckTrue("pre-release is ignored", UpdateLogic.ParseRelease(Rel("v0.2.0", true, true)) == null);
		var hex = UpdateLogic.Sha256Hex(System.Text.Encoding.ASCII.GetBytes("abc"));
		CheckTrue("SHA-256 known vector", hex == "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
		CheckTrue("checksum file: sha256sum format", UpdateLogic.ParseChecksum(hex.ToUpperInvariant() + "  DiscardOdds.dll\n", "DiscardOdds.dll") == hex);
		CheckTrue("checksum file: binary-mode '*' and bare hex", UpdateLogic.ParseChecksum(hex + " *DiscardOdds.dll", "DiscardOdds.dll") == hex && UpdateLogic.ParseChecksum(hex, "DiscardOdds.dll") == hex);
		CheckTrue("checksum file: wrong file name / short hash rejected", UpdateLogic.ParseChecksum(hex + "  Other.dll", "DiscardOdds.dll") == null && UpdateLogic.ParseChecksum("abc123  DiscardOdds.dll", "DiscardOdds.dll") == null);
		var st = PluginSettings.Parse(new string[0]);
		CheckTrue("update settings default ON", st.CheckForUpdates && st.AutoUpdate);
		CheckTrue("AutoUpdate=False honoured", !PluginSettings.Parse(new[] { "AutoUpdate=False" }).AutoUpdate);

		// ---- lethal check (factual face-damage count)
		LethalCard P(string name, string type, int cost, string text, int atk = 0, int sd = 0, bool empty = false) => LethalEngine.ParseCard(name, type, cost, text, atk, sd, empty);
		var soulfire = P("Soulfire", "SPELL", 1, "[x]Deal $4 damage. Discard a random card.");
		CheckTrue("Soulfire: 4 to any target, exact", soulfire != null && soulfire.Damage == 4 && !soulfire.Approx);
		CheckTrue("Spell Damage +1 adds to $N spells", P("Fireball", "SPELL", 4, "Deal $6 damage.", sd: 1).Damage == 7);
		var barrage = P("Soul Barrage", "SPELL", 4, "When you play or discard this, deal $5 damage randomly split among all enemies.");
		CheckTrue("Soul Barrage with enemy minions: 5 ≈", barrage != null && barrage.Damage == 5 && barrage.Approx);
		CheckTrue("Soul Barrage, empty enemy board: 5 exact", !P("Soul Barrage", "SPELL", 4, "When you play or discard this, deal $5 damage randomly split among all enemies.", empty: true).Approx);
		CheckTrue("Chronoclaws (weapon 4 atk) counts as hero attack", P("Chronoclaws", "WEAPON", 4, "After your hero attacks, discard your highest Cost card.", atk: 4) is LethalCard cc && cc.IsWeapon && cc.Damage == 4);
		CheckTrue("minion-only damage not counted", P("Holy Smite", "SPELL", 1, "Deal $3 damage to a minion.") == null && P("Flamestrike", "SPELL", 7, "Deal $5 damage to all enemy minions.") == null);
		CheckTrue("face / character / all-characters spells counted", P("Sinister Strike", "SPELL", 1, "Deal $3 damage to the enemy hero.").Damage == 3 && P("Frostbolt", "SPELL", 2, "Deal $3 damage to a character and <b>Freeze</b> it.").Damage == 3 && P("Hellfire", "SPELL", 3, "Deal $3 damage to ALL characters.").Damage == 3);
		CheckTrue("'instead' upgrade counts base value with ≈", P("Kill Command", "SPELL", 3, "Deal $3 damage. If you control a Beast, deal $5 damage instead.") is LethalCard kc && kc.Damage == 3 && kc.Approx);
		CheckTrue("Deathrattle / Secret damage not counted", P("X", "MINION", 2, "<b>Deathrattle:</b> Deal 2 damage to the enemy hero.", atk: 1) == null);
		CheckTrue("Charge minion counted via attack", P("Leeroy Jenkins", "MINION", 5, "<b>Charge</b>. <b>Battlecry:</b> Summon two 1/1 Whelps for your opponent.", atk: 6) is LethalCard lj && lj.Damage == 6 && lj.ViaAttack);
		CheckTrue("'split among all enemies' is ≈ with enemy minions; Questline / when-drawn not counted",
			P("Tachyon Barrage", "SPELL", 5, "Deal $6 damage split among all enemies.").Approx
			&& P("The Demon Seed", "SPELL", 1, "<b>Questline:</b> Take 12 damage on your turns. <b>Reward:</b> <b>Lifesteal</b>. Deal $3 damage to the enemy hero.") == null
			&& P("Flame Leviathan", "MINION", 7, "<b>Rush</b> When you draw this, deal 2 damage to all characters except Mechs.", atk: 7) == null);
		CheckTrue("battlecry face damage counted", P("Elven Archer", "MINION", 1, "<b>Battlecry:</b> Deal 1 damage.", atk: 1).Damage == 1);
		var lx = new LethalInput { HeroAttack = 0, HeroAttacksLeft = 1, Mana = 5, OppHealth = 13 };
		lx.Minions.Add(new LethalAttacker { Name = "A", Attack = 4, Attacks = 1 });
		lx.Minions.Add(new LethalAttacker { Name = "B", Attack = 3, Attacks = 2 }); // windfury
		lx.Hand.Add(soulfire);
		lx.Hand.Add(P("Fireball", "SPELL", 4, "Deal $6 damage."));
		lx.Hand.Add(P("Lava Burst", "SPELL", 3, "Deal $5 damage. <b>Overload:</b> (2)"));
		var lr = LethalEngine.Compute(lx);
		CheckTrue("board 4+3x2 = 10, best burn in 5 mana = Soulfire+Fireball 10 -> 20 vs 13 LETHAL", lr.Board == 10 && lr.Burn == 10 && lr.Total == 20 && lr.Lethal && lr.Line.Contains("LETHAL (all face)"));
		lx.EnemyTaunts = 1;
		var lt = LethalEngine.Compute(lx);
		CheckTrue("enemy Taunt blocks attack damage, burn still counts, shown clearly", lt.Board == 0 && lt.BlockedByTaunt == 10 && lt.Total == 10 && !lt.Lethal && lt.Line.EndsWith("(3 short)") && lt.Detail.Contains("Taunt ×1"));
		var la = LethalEngine.Compute(new LethalInput { Mana = 4, OppHealth = 10, OppArmor = 3, HeroAttack = 2, HeroAttacksLeft = 1, Hand = { P("Chronoclaws", "WEAPON", 4, "", atk: 4) } });
		CheckTrue("weapon replaces hero attack (4, not 2+4); armor shown", la.Total == 4 && la.Weapon == "Chronoclaws" && la.Line.Contains("13 (10 health + 3 armor)") && la.Line.Contains("(9 short)"));
		CheckTrue("immune hero never lethal", !LethalEngine.Compute(new LethalInput { OppHealth = 1, OppImmune = true, Minions = { new LethalAttacker { Attack = 5, Attacks = 1 } } }).Lethal);
		var lap = LethalEngine.Compute(new LethalInput { Mana = 4, OppHealth = 5, Hand = { barrage } });
		CheckTrue("≈ lethal is marked LETHAL? with ≈", lap.Lethal && lap.Approx && lap.Line.StartsWith("Face damage: ≈5") && lap.Line.Contains("LETHAL?"));

		// ---- attack eligibility (HDT BoardDamage rules) and Ray's 15-vs-16 board
		var ready = new AttackState { TurnsInPlay = 2 };
		CheckTrue("minion in play since last turn: 1 attack", LethalEngine.AttacksLeft(ready) == 1);
		CheckTrue("windfury 2, mega-windfury 4, minus attacks made", LethalEngine.AttacksLeft(new AttackState { TurnsInPlay = 1, Windfury = true, AttacksThisTurn = 1 }) == 1 && LethalEngine.AttacksLeft(new AttackState { TurnsInPlay = 1, MegaWindfury = true }) == 4);
		var token = new AttackState { TurnsInPlay = 0, Exhausted = false }; // summoned this turn, EXHAUSTED not (yet) sent
		CheckTrue("summoned this turn without EXHAUSTED tag: 0 attacks (played this turn)", LethalEngine.AttacksLeft(token) == 0 && LethalEngine.NoAttackReason(token) == "played this turn");
		CheckTrue("Charge this turn: 1; Rush this turn: 0 (can't hit face)", LethalEngine.AttacksLeft(new AttackState { TurnsInPlay = 0, Charge = true }) == 1 && LethalEngine.NoAttackReason(new AttackState { TurnsInPlay = 0, Rush = true }) == "Rush, played this turn");
		CheckTrue("frozen / dormant / can't attack / Titan: 0", new[] { new AttackState { TurnsInPlay = 3, Frozen = true }, new AttackState { TurnsInPlay = 3, Dormant = true }, new AttackState { TurnsInPlay = 3, CantAttack = true }, new AttackState { TurnsInPlay = 3, TitanLocked = true } }.All(x => LethalEngine.AttacksLeft(x) == 0));
		CheckTrue("hero: not exhausted -> 1 (TurnsInPlay ignored); exhausted -> 0", LethalEngine.AttacksLeft(new AttackState { IsHero = true, TurnsInPlay = 0 }) == 1 && LethalEngine.AttacksLeft(new AttackState { IsHero = true, Exhausted = true, AttacksThisTurn = 1 }) == 0);
		var ray = new LethalInput { Mana = 4, OppHealth = 15, HeroAttacksLeft = 1, Hand = { P("Chronoclaws", "WEAPON", 4, "After your hero attacks, discard your highest Cost card.", atk: 4) } };
		foreach(var (nm, atk, hp, ast) in new[] { ("A", 3, 3, new AttackState { TurnsInPlay = 2 }), ("B", 8, 5, new AttackState { TurnsInPlay = 1 }), ("Felbeast", 1, 1, token) })
		{
			var left = LethalEngine.AttacksLeft(ast);
			if(left > 0) ray.Minions.Add(new LethalAttacker { Name = nm, Attack = atk, Attacks = left });
			else ray.NotCounted.Add($"{nm} {atk}/{hp} ({LethalEngine.NoAttackReason(ast)})");
		}
		var rr = LethalEngine.Compute(ray);
		CheckTrue("Ray's board 3/3 + 8/5 + Chronoclaws = 15 (token summoned this turn not counted, listed in details)", rr.Total == 15 && rr.Board == 11 && rr.Lethal && rr.Detail.Contains("not counted: Felbeast 1/1 (played this turn)"));

		// ---- one-drop odds (Ray's Discardo: 10 one-drops in 30)
		Check("PNone 0 one-drops in 3 of 30 (10 ones)", OneDropOdds.PNone(30, 10, 3), C(20, 3) / C(30, 3));
		Check("PNone impossible -> 0", OneDropOdds.PNone(5, 4, 2), 0);
		Check("going first, keep 3 non-ones: T1 hit 10/27", 1 - OneDropOdds.PNoneByTurn1(27, 10, 0), 10.0 / 27);
		Check("going first, full mulligan of 3: T1 hit", 1 - OneDropOdds.PNoneByTurn1(27, 10, 3), 1 - C(17, 3) / C(27, 3) * 17.0 / 27);
		Check("on coin, full mulligan of 4: T1 hit", 1 - OneDropOdds.PNoneByTurn1(26, 10, 4), 1 - C(16, 4) / C(26, 4) * 16.0 / 26);
		Check("going first full mull hit = 85.36%", 1 - OneDropOdds.PNoneByTurn1(27, 10, 3), 0.8536, 1e-4);
		Check("on coin full mull hit = 92.51%", 1 - OneDropOdds.PNoneByTurn1(26, 10, 4), 0.9251, 1e-4);

		// ---- v0.1.2: one line per entity, Hand of Gul'dan as "(discard)", choose-discards hidden, Chronoclaws names only
		var vs = new OddsState { Deck = deck, DeckCount = 24, Group = payoffs, CardText = id => id == "BT_300" ? "When you play or discard this, draw 3 cards." : null };
		vs.Hand = new List<OddsHandCard> { H(31, "BT_300", 6, true), H(32, "DMF_119", 1, false), H(33, "X1", 2, false) };
		var vo = OddsEngine.ForHand(vs, rules);
		var hogLines = vo.Where(o => o.CardId == "BT_300").ToList();
		CheckTrue("Hand of Gul'dan: exactly one line, labelled (discard), played odds only in detail",
			hogLines.Count == 1 && hogLines[0].Name == "Hand of Gul'dan (discard)" && hogLines[0].EntityId == 31
			&& hogLines[0].Detail.StartsWith("if discarded:") && hogLines[0].Detail.Contains("if played:"));
		Check("Hand of Gul'dan (discard) odds = draw 3", hogLines[0].Hit, 1 - C(16, 3) / C(24, 3));
		vs.Hand.Add(H(34, "BT_300", 6, true));
		vs.Hand.Add(H(31, "BT_300", 6, true)); // same entity reported twice
		vo = OddsEngine.ForHand(vs, rules);
		CheckTrue("no duplicate lines: two copies / a repeated entity -> one line per card, entities unique",
			vo.Count(o => o.CardId == "BT_300") == 1 && vo.Select(o => o.EntityId).Distinct().Count() == vo.Count && vo.Select(o => o.CardId).Distinct().Count() == vo.Count);
		var genDiscard = OddsRule.ForCard("ZZ_1", "Some Draw", "When you play or discard this, draw 2 cards.");
		CheckTrue("generic 'play or discard this, draw N' -> OnDiscard, not ≈", genDiscard.OnDiscard && !genDiscard.Approx && genDiscard.Clause == null);
		var ocu = new OddsRule { CardId = "CATA_490", Name = "Ocular", Kind = OddsKind.DiscardChoose };
		CheckTrue("Ocular with a target in hand: 100% by choice (ByChoice -> never on the widget)", OddsEngine.Compute(ocu, vs).ByChoice && OddsEngine.Compute(ocu, vs).Hit == 1);
		var noTarget = new OddsState { Deck = deck, DeckCount = 24, Group = payoffs, Hand = new List<OddsHandCard> { H(41, "CATA_490", 3, false), H(42, "X1", 2, false) } };
		CheckTrue("Ocular with no target: still ByChoice (never on the widget), 0%", OddsEngine.Compute(ocu, noTarget, 41).ByChoice && OddsEngine.Compute(ocu, noTarget, 41).Hit == 0);
		CheckTrue("Gemstone Hoarder also always ByChoice", OddsEngine.Compute(rules["CATA_897"], vs).ByChoice);
		var cs = new OddsState { Deck = deck, DeckCount = 24, Group = payoffs, Hand = new List<OddsHandCard> { H(51, "END_016", 4, false), H(52, "RLK_534", 4, true), H(53, "X2", 4, false), H(54, "X3", 1, false) } };
		var cl = OddsEngine.Compute(rules["END_016"], cs, 51);
		CheckTrue("Chronoclaws: no % even on a mixed tie, names both tied cards (orange), % kept in detail",
			cl.NoOdds && cl.DiscardRule == "highest" && cl.DiscardNames.Count == 2 && cl.Detail.Contains("target 50% / 50%"));
		var emMixed = OddsEngine.Compute(rules["ULD_163"], cs, 51);
		CheckTrue("Expired Merchant on a mixed tie keeps %", !emMixed.NoOdds && Math.Abs(emMixed.Hit - 0.5) < 1e-9);
		cs.Hand.RemoveAll(h => h.EntityId == 53);
		var emSure = OddsEngine.Compute(rules["ULD_163"], cs, 51);
		CheckTrue("Expired Merchant when certain -> no %, just the target", emSure.NoOdds && emSure.Hit == 1 && emSure.DiscardNames.Count == 1);
		CheckTrue("Wicked Whispers keeps % (lowest rule unchanged)", !OddsEngine.Compute(rules["DMF_119"], cs).NoOdds);

		// ---- v0.1.4: WidgetPolicy is the single gate for what the widget may draw (every mode, details on or off)
		var wp = new OddsState { Deck = deck, DeckCount = 24, Group = payoffs, CardText = id => id == "BT_300" ? "When you play or discard this, draw 3 cards." : null };
		wp.Hand = new List<OddsHandCard> { H(61, "CATA_490", 3, false), H(62, "CATA_897", 3, false), H(63, "BT_300", 6, true), H(64, "DMF_119", 1, false), H(65, "RLK_534", 4, true) };
		var wpAll = OddsEngine.ForHand(wp, rules);
		foreach(var details in new[] { false, true })
		{
			var shown = WidgetPolicy.ForWidget(wpAll, details);
			CheckTrue($"Ocular Occultist / Gemstone Hoarder never on the widget (Show details {(details ? "on" : "off")}), even with a target in hand",
				shown.All(o => o.CardId != "CATA_490" && o.CardId != "CATA_897"));
			CheckTrue($"Hand of Gul'dan never on the widget (details {(details ? "on" : "off")})", shown.All(o => o.CardId != "BT_300"));
			CheckTrue($"Wicked Whispers stays on the main widget (details {(details ? "on" : "off")})", shown.Any(o => o.CardId == "DMF_119"));
		}
		CheckTrue("Ocular with no target (0%) is still never shown", WidgetPolicy.Place(OddsEngine.Compute(ocu, noTarget, 41)) == WidgetPlacement.Never);
		CheckTrue("hidden by name too (Platysaur 'holds Ocular Occultist' row, unknown id)", WidgetPolicy.Place(null, "Ocular Occultist") == WidgetPlacement.Never
			&& WidgetPolicy.Place(null, "Gemstone Hoarder") == WidgetPlacement.Never);
		CheckTrue("Hand of Gul'dan by id / name / '(discard)' label / story id -> never (v0.1.5)", WidgetPolicy.Place(null, "Hand of Gul'dan") == WidgetPlacement.Never
			&& WidgetPolicy.Place(null, "Hand of Gul'dan (discard)") == WidgetPlacement.Never && WidgetPolicy.Place("BT_300", "Gul'dan (discard)") == WidgetPlacement.Never
			&& WidgetPolicy.Place("Story_09_HandofGuldan", null) == WidgetPlacement.Never);
		CheckTrue("no card is details-only any more", WidgetPolicy.DetailsOnlyIds.Count == 0 && WidgetPolicy.DetailsOnlyNames.Count == 0);
		CheckTrue("any choose-the-discard rule is hidden, whatever the card", WidgetPolicy.Place(new CardOdds { CardId = "ZZ_9", Name = "New Chooser", Kind = OddsKind.DiscardChoose }) == WidgetPlacement.Never);
		CheckTrue("ordinary cards and labels stay on the main widget", WidgetPolicy.Place("RLK_534", "Soul Barrage") == WidgetPlacement.Main && WidgetPolicy.Place(null, "Duke of Below") == WidgetPlacement.Main);

		// ---- v0.1.4: one-drop exclusions (Wicked Whispers doesn't count as a turn-1 play for the Discard preset)
		var oneDeck = new[] { ("DMF_119", 1), ("TLC_603", 1), ("CATA_493", 1), ("CATA_490", 3), ("RLK_534", 4) };
		CheckTrue("one-drops without exclusions: all 1-cost cards", WidgetPolicy.OneDropIds(oneDeck, null).SetEquals(new[] { "DMF_119", "TLC_603", "CATA_493" }));
		CheckTrue("one-drops with Wicked Whispers excluded", WidgetPolicy.OneDropIds(oneDeck, new HashSet<string> { "DMF_119" }).SetEquals(new[] { "TLC_603", "CATA_493" }));
		var exCfg = TargetConfig.CreateDefault();
		CheckTrue("default Discard preset excludes Wicked Whispers + Entropic Continuity from one-drops", exCfg.Presets[0].OneDropExclude.Select(t => t.Id).SequenceEqual(new[] { "DMF_119", "TIME_026" }));
		CheckTrue("default Discard preset counts Cursed Catacombs as an opener hit", exCfg.Presets[0].OpenerExtraHits.Select(t => t.Id).SequenceEqual(new[] { "TLC_451" }));
		CheckTrue("preset deck: one-drop excludes = DMF_119 + TIME_026, extra hits = TLC_451", exCfg.Resolve("g-1", "Discardo", discardDeck).OneDropExclude.SetEquals(new[] { "DMF_119", "TIME_026" })
			&& exCfg.Resolve("g-1", "Discardo", discardDeck).OpenerExtraHits.SetEquals(new[] { "TLC_451" }));
		exCfg.SetDeckTargets("g-1", "Discardo", new[] { new TargetCard { Id = "RLK_534" } });
		var exOwn = exCfg.Resolve("g-1", "Discardo", discardDeck);
		CheckTrue("own target list without oneDropExclude / openerExtraHits still inherits the preset's", exOwn.Source == "deck" && exOwn.OneDropExclude.SetEquals(new[] { "DMF_119", "TIME_026" })
			&& exOwn.OpenerExtraHits.SetEquals(new[] { "TLC_451" }));
		exCfg.FindDeck("g-1", null).OneDropExclude = new List<TargetCard>();
		CheckTrue("explicit empty oneDropExclude on the deck -> nothing excluded", exCfg.Resolve("g-1", "Discardo", discardDeck).OneDropExclude.Count == 0);
		CheckTrue("unrelated deck: no one-drop exclusions", exCfg.Resolve("g-2", "Mage", new[] { "CS2_029" }).OneDropExclude.Count == 0);
		var exRound = TargetConfig.Parse(exCfg.ToJson());
		CheckTrue("oneDropExclude / openerExtraHits round-trip (preset lists + explicit empty deck list), no re-migration", !exRound.Migrated
			&& exRound.Presets[0].OneDropExclude.Select(t => t.Id).SequenceEqual(new[] { "DMF_119", "TIME_026" }) && exRound.Presets[0].OpenerExtraHits.Single().Id == "TLC_451"
			&& exRound.Decks[0].OneDropExclude != null && exRound.Decks[0].OneDropExclude.Count == 0);
		var legacy = TargetConfig.Parse("{ \"presets\": [ { \"name\": \"Discard Warlock payoffs\", \"autoApplyMinMatches\": 3, \"targets\": [\"RLK_534\", \"RLK_532\", \"BT_300\"] }, { \"name\": \"Other\", \"targets\": [] } ] }");
		CheckTrue("pre-v0.1.4 targets.json: built-in Discard preset gets the v0.1.5 defaults, others none",
			legacy.Migrated && legacy.Presets[0].OneDropExclude.Select(t => t.Id).SequenceEqual(new[] { "DMF_119", "TIME_026" })
			&& legacy.Presets[0].OpenerExtraHits.Single().Id == "TLC_451" && legacy.Presets[1].OneDropExclude == null && legacy.Presets[1].OpenerExtraHits == null);
		// Ray's file as v0.1.4 saved it: version 1, preset with oneDropExclude [Wicked Whispers] only, own deck list.
		var v014 = TargetConfig.Parse("{ \"version\": 1, \"decks\": [ { \"deckId\": \"d-1\", \"deckName\": \"Discardo\", \"targets\": [\"RLK_534\", \"RLK_532\", \"BT_300\"] } ], \"presets\": [ { \"name\": \"Discard Warlock payoffs\", \"autoApplyMinMatches\": 3, \"targets\": [\"RLK_534\", \"RLK_532\", \"BT_300\", \"CATA_499\", \"KAR_205\"], \"oneDropExclude\": [ { \"id\": \"DMF_119\", \"name\": \"Wicked Whispers\" } ] } ] }");
		var v014r = v014.Resolve("d-1", "Discardo", discardDeck);
		CheckTrue("v0.1.4 targets.json (Whispers only) picks up Entropic Continuity + Cursed Catacombs, own deck list kept",
			v014.Migrated && v014r.Source == "deck" && v014r.OneDropExclude.SetEquals(new[] { "DMF_119", "TIME_026" }) && v014r.OpenerExtraHits.SetEquals(new[] { "TLC_451" })
			&& v014.Presets[0].OneDropExclude.Count(t => t.Id == "DMF_119") == 1);
		var v2Edited = TargetConfig.Parse("{ \"version\": 2, \"presets\": [ { \"name\": \"Discard Warlock payoffs\", \"targets\": [], \"oneDropExclude\": [\"DMF_119\"] } ] }");
		CheckTrue("version-2 file: user edits are left alone (no re-adding)", !v2Edited.Migrated && v2Edited.Presets[0].OneDropExclude.Count == 1 && v2Edited.Presets[0].OpenerExtraHits == null);
		var tmp2 = Path.Combine(Path.GetTempPath(), "discardodds_test_" + Guid.NewGuid().ToString("N"));
		var path2 = Path.Combine(tmp2, "targets.json");
		Directory.CreateDirectory(tmp2);
		File.WriteAllText(path2, "{ \"version\": 1, \"decks\": [], \"presets\": [ { \"name\": \"Discard Warlock payoffs\", \"autoApplyMinMatches\": 3, \"targets\": [\"RLK_534\"], \"oneDropExclude\": [\"DMF_119\"] } ] }");
		var loaded2 = TargetConfig.Load(path2, out var err4);
		var onDisk2 = TargetConfig.Parse(File.ReadAllText(path2));
		CheckTrue("Load writes the upgraded file once (version 2, .bak kept)", err4 == null && File.Exists(path2 + ".bak") && !onDisk2.Migrated
			&& File.ReadAllText(path2).Contains("\"version\": 2") && onDisk2.Presets[0].OneDropExclude.Any(t => t.Id == "TIME_026") && loaded2.Presets[0].OpenerExtraHits.Count == 1);
		try { Directory.Delete(tmp2, true); } catch { }
		var handEx = TargetConfig.Parse("{ \"decks\": [ { \"deckName\": \"D\", \"targets\": [], \"oneDropExclude\": [\"DMF_119\", {\"id\": \"TLC_603\"}] } ] }");
		CheckTrue("hand-written deck oneDropExclude (string ids and objects)", handEx.Resolve(null, "D", new string[0]).OneDropExclude.SetEquals(new[] { "DMF_119", "TLC_603" }));

		// ---- v0.1.5: opener line = 1-drops (minus exclusions) + extra hits (Cursed Catacombs)
		var openDeck = new[] { ("DMF_119", 1), ("TIME_026", 1), ("TLC_603", 1), ("CATA_493", 1), ("TLC_451", 0), ("RLK_534", 4) };
		var openEx = new HashSet<string> { "DMF_119", "TIME_026" };
		CheckTrue("opener hits: 1-drops minus Whispers/Entropic, plus Catacombs", WidgetPolicy.OpenerHitIds(openDeck, openEx, new HashSet<string> { "TLC_451" }).SetEquals(new[] { "TLC_603", "CATA_493", "TLC_451" }));
		CheckTrue("opener hits: no extras -> 1-drops only", WidgetPolicy.OpenerHitIds(openDeck, openEx, null).SetEquals(new[] { "TLC_603", "CATA_493" }));
		CheckTrue("opener hits: an excluded card stays out even if listed as extra", !WidgetPolicy.OpenerHitIds(openDeck, openEx, new HashSet<string> { "DMF_119" }).Contains("DMF_119"));
		CheckTrue("opener hits: an extra not in the deck adds nothing", WidgetPolicy.OpenerHitIds(new[] { ("TLC_603", 1) }, null, new HashSet<string> { "TLC_451" }).SetEquals(new[] { "TLC_603" }));
		CheckTrue("opener label: '1-drop' / '1-drop/Catacombs'", WidgetPolicy.OpenerLabel(null) == "1-drop" && WidgetPolicy.OpenerLabel(new[] { "Cursed Catacombs" }) == "1-drop/Catacombs");
		Check("miss chance with no hits tossed = old formula", OneDropOdds.PNoneByTurn1(27, 10, 3, 0), OneDropOdds.PNoneByTurn1(27, 10, 3));
		// Tossed 1 hit + 2 others from 27 (9 hits left in deck): replacements miss (18/27·17/26·16/25), turn-1 draw misses with 10 hits back in 27.
		Check("miss chance when a hit was tossed (it can be the turn-1 draw)", OneDropOdds.PNoneByTurn1(27, 9, 3, 1), (18.0 / 27 * 17 / 26 * 16 / 25) * (17.0 / 27));

		// ---- settings: new toggles round-trip; Show details off by default and reset once for pre-v0.1.5 files
		var st2 = PluginSettings.Parse(new[] { "SettingsVersion=2", "ShowDetails=True", "ShowOneDrop=False", "CompactMode=False" });
		CheckTrue("ShowDetails / ShowOneDrop / CompactMode parse (defaults off / on / on)", st2.ShowDetails && !st2.ShowOneDrop && !st2.CompactMode
			&& !PluginSettings.Parse(new string[0]).ShowDetails && PluginSettings.Parse(new string[0]).ShowOneDrop && PluginSettings.Parse(new string[0]).CompactMode);
		var oldSt = PluginSettings.Parse(new[] { "ShowDetails=True", "CompactMode=True" });
		CheckTrue("pre-v0.1.5 settings.ini with ShowDetails=True -> off once, then saved as version 2", !oldSt.ShowDetails && oldSt.SettingsVersion == PluginSettings.CurrentSettingsVersion
			&& new PluginSettings().ShowDetails == false);

		// ---- v0.1.6: Soularium 0 / 1+ / 2+ / 3 split (hypergeometric, 26 cards, 6 payoffs, 3 draws; C(26,3) = 2600)
		var so = DrawMath.Soularium(26, 6);
		Check("Soularium P(0) = C(20,3)/C(26,3)", so.P0, 1140.0 / 2600);
		Check("Soularium P(1+) = 1 - P(0)", so.P1Plus, 1 - 1140.0 / 2600);
		Check("Soularium P(2+) = (15*20 + 20)/2600", so.P2Plus, 320.0 / 2600);
		Check("Soularium P(all 3) = C(6,3)/2600", so.PAll, 20.0 / 2600);
		Check("Soularium distribution sums to 1", so.Dist.Sum(), 1);
		CheckTrue("Soularium text: '1+ 56% · 2+ 12% · 3/3 1% · whiff 44%'", DrawMath.Text(DrawMath.SoulariumSegments(so)) == "1+ 56% · 2+ 12% · 3/3 1% · whiff 44%");
		CheckTrue("Soularium colors: three green, whiff red", DrawMath.SoulariumSegments(so).Count(x => x.Item2 == SegKind.Hit) == 3 && DrawMath.SoulariumSegments(so).Last().Item2 == SegKind.Miss);
		var soSmall = DrawMath.Soularium(2, 1);
		CheckTrue("Soularium with 2 cards left: draws both, P(1+) 100%, P(all 3) 0%", soSmall.P1Plus == 1 && soSmall.PAll == 0 && soSmall.P0 == 0);
		var soNone = DrawMath.Soularium(20, 0);
		CheckTrue("Soularium with no payoffs: whiff 100%", soNone.P0 == 1 && soNone.P1Plus == 0);

		// ---- v0.1.6: Soularium risk split (payoffs / playable / wasted incl. unknown cards)
		var wDeck = new Dictionary<string, int> { ["PAY"] = 6, ["B1"] = 4, ["C3"] = 5, ["D5"] = 7 };
		var wCost = new Dictionary<string, int> { ["PAY"] = 1, ["B1"] = 1, ["C3"] = 3, ["D5"] = 5 };
		Func<string, int?> costOf = id => wCost.TryGetValue(id, out var cc) ? cc : (int?)null;
		var wPay = new HashSet<string> { "PAY" };
		var w = DrawMath.Waste(wDeck, 26, wPay, costOf, 2);
		CheckTrue("waste split @2: 6 payoffs, 4 playable, 16 wasted (12 too costly + 4 unknown)", w.Payoffs == 6 && w.Playable == 4 && w.Wasted == 16 && w.Unknown == 4);
		Check("Payoff 1+ = 1 - C(20,3)/C(26,3)", w.Payoff1Plus, 1 - 1140.0 / 2600);
		Check("Waste 1+ = 1 - C(10,3)/C(26,3)", w.Waste1Plus, 1 - 120.0 / 2600);
		Check("avg wasted = 3 * 16/26", w.AvgWasted, 3.0 * 16 / 26);
		CheckTrue("risk text: 'Risk @2: Payoff 1+ 56% · Waste 1+ 95% · avg 1.8 wasted'", DrawMath.Text(DrawMath.RiskSegments(w)) == "Risk @2: Payoff 1+ 56% · Waste 1+ 95% · avg 1.8 wasted");
		var w0 = DrawMath.Waste(wDeck, 26, wPay, costOf, -1);
		CheckTrue("can't afford it: @0, every non-payoff wasted", w0.ManaLeft == 0 && w0.Playable == 0 && w0.Wasted == 20);
		var w5 = DrawMath.Waste(wDeck, 26, wPay, costOf, 5);
		CheckTrue("@5: all known non-payoffs playable, only unknown wasted", w5.Playable == 16 && w5.Wasted == 4);
		var wOver = DrawMath.Waste(wDeck, 20, wPay, costOf, 2);
		CheckTrue("HDT list ahead of DeckCount: groups never exceed the deck", wOver.Payoffs + wOver.Playable + wOver.Wasted == 20);

		// ---- v0.1.6: next draw (payoff n/m, playable = cost <= next turn's mana, payoffs included)
		var nd = DrawMath.NextDraw(wDeck, 26, wPay, costOf, 3);
		Check("next draw payoff = 6/26", nd.Payoff, 6.0 / 26);
		Check("next draw playable = (6 + 4 + 5)/26", nd.Playable, 15.0 / 26);
		CheckTrue("next draw text: 'Next draw: Payoff 23% · Playable 58%'", DrawMath.Text(DrawMath.NextDrawSegments(nd)) == "Next draw: Payoff 23% · Playable 58%");
		CheckTrue("next turn mana: max+1, cap 10, minus overload", DrawMath.NextTurnMana(4, 0) == 5 && DrawMath.NextTurnMana(10, 0) == 10 && DrawMath.NextTurnMana(9, 2) == 8
			&& DrawMath.NextTurnMana(0, 0) == 1 && DrawMath.NextTurnMana(3, 5) == 0);
		CheckTrue("payoffs left text", DrawMath.PayoffsLeftText(5) == "5 payoffs left" && DrawMath.PayoffsLeftText(1) == "1 payoff left" && DrawMath.PayoffsLeftText(0) == "0 payoffs left");

		// ---- v0.1.6: Soularium result text (likelihood of the observed count)
		CheckTrue("result texts", DrawMath.ResultText(0, 3, 0.08) == "Whiffed all 3: 8% chance" && DrawMath.ResultText(1, 3, 0.41) == "1 payoff: 41% chance"
			&& DrawMath.ResultText(2, 3, 0.30) == "2 payoffs: 30% chance" && DrawMath.ResultText(3, 3, 0.04) == "All 3 payoffs: 4% chance");
		CheckTrue("result likelihood = P(exactly k): 2 payoffs of 6/26 = 300/2600", Math.Abs(DrawMath.HyperDist(26, 6, 3)[2] - 300.0 / 2600) < 1e-9);

		// ---- v0.1.6: luck score (mid-percentile of each outcome; 50% = as expected)
		Check("mid-percentile: 50/50, got 1 -> 75%", DrawMath.MidPercentile(new[] { 0.5, 0.5 }, 1), 0.75);
		Check("mid-percentile: 50/50, got 0 -> 25%", DrawMath.MidPercentile(new[] { 0.5, 0.5 }, 0), 0.25);
		var luck = new LuckTally();
		luck.AddBinary(0.4, true);
		luck.AddBinary(0.4, false);
		Check("luck: hit at 40% (80th) + miss at 40% (30th) -> 55%", luck.MeanPercentile, 0.55);
		CheckTrue("luck: 1 hit vs 0.8 expected, rated 'a bit lucky'", luck.Actual == 1 && Math.Abs(luck.Expected - 0.8) < 1e-9 && LuckTally.Rating(luck.MeanPercentile) == "a bit lucky");
		var luckS = new LuckTally();
		luckS.AddCount(so.Dist, 0);
		Check("luck: Soularium expected payoffs = 3*6/26", luckS.Expected, 3.0 * 6 / 26);
		Check("luck: Soularium whiff percentile = P(0)/2", luckS.MeanPercentile, 1140.0 / 2600 / 2);
		CheckTrue("luck line mentions rating and counts", DrawMath.LuckLine(luck, luckS).StartsWith("about average (3 events)") || DrawMath.LuckLine(luck, luckS).Contains("3 events"));
		CheckTrue("luck line with no events", DrawMath.LuckLine(new LuckTally(), new LuckTally()).StartsWith("no predicted"));

		// ---- v0.1.6: lethal next draw (estimate): deck cards whose draw makes next turn lethal / deck count
		LethalInput NextBase(int taunts = 0, bool immune = false) => new LethalInput
		{
			Minions = new List<LethalAttacker> { new LethalAttacker { Name = "Imp", Attack = 3, Attacks = 1 } },
			HeroAttack = 0, HeroAttacksLeft = 1, Mana = 4, OppHealth = 8, EnemyTaunts = taunts, OppImmune = immune
		};
		var lDeck = new List<(LethalCard card, int copies)>
		{
			(new LethalCard { Name = "Bolt4", Cost = 1, Damage = 4 }, 2),                          // 3 + 4 = 7: short
			(new LethalCard { Name = "Big5", Cost = 4, Damage = 5 }, 1),                           // 3 + 5 = 8: lethal
			(new LethalCard { Name = "Pricey", Cost = 6, Damage = 10 }, 2),                        // costs more than next turn's mana
			(new LethalCard { Name = "Maybe", Cost = 1, Damage = 9, Approx = true }, 3),           // ambiguous: left out
			(new LethalCard { Name = "Axe", Cost = 2, Damage = 5, ViaAttack = true, IsWeapon = true }, 1), // hero 5 + 3 = 8
			(new LethalCard { Name = "Charger", Cost = 3, Damage = 5, ViaAttack = true }, 1),      // 3 + 5 = 8
		};
		var ndl = LethalEngine.NextDrawLethal(NextBase(), lDeck, 20);
		CheckTrue("lethal next draw: 3 of 20 cards (Big5, Axe, Charger)", ndl.Hits == 3 && !ndl.AlreadyLethal && ndl.Cards.Count == 3);
		Check("lethal next draw P = 3/20", ndl.P, 0.15);
		CheckTrue("lethal next draw text: 'Lethal next draw ~15%'", ndl.Line == "Lethal next draw ~15%");
		var ndlTaunt = LethalEngine.NextDrawLethal(NextBase(taunts: 1), lDeck, 20);
		CheckTrue("enemy Taunt: attacks blocked, no single draw is enough -> ~0%", ndlTaunt.Hits == 0 && ndlTaunt.Line == "Lethal next draw ~0%");
		var baseLethal = NextBase();
		baseLethal.Hand.Add(new LethalCard { Name = "Soulfire", Cost = 1, Damage = 5 });
		var ndlBase = LethalEngine.NextDrawLethal(baseLethal, lDeck, 20);
		CheckTrue("already lethal next turn without a draw", ndlBase.AlreadyLethal && ndlBase.P == 1 && ndlBase.Line == "Lethal next turn ~100% (no draw needed)");
		CheckTrue("enemy Immune: 0%", LethalEngine.NextDrawLethal(NextBase(immune: true), lDeck, 20).P == 0);
		CheckTrue("next turn attacks: just-played windfury minion gets 2; frozen-through / can't attack get 0",
			LethalEngine.AttacksNextTurn(new AttackState { Exhausted = true, TurnsInPlay = 0, Windfury = true }, false) == 2
			&& LethalEngine.AttacksNextTurn(new AttackState { Frozen = true }, true) == 0
			&& LethalEngine.AttacksNextTurn(new AttackState { CantAttack = true }, false) == 0
			&& LethalEngine.AttacksNextTurn(new AttackState { Frozen = true }, false) == 1);

		// ---- v0.1.6: new line toggles default on, round-trip through settings.ini
		var dflt = PluginSettings.Parse(new string[0]);
		CheckTrue("new line toggles default on", dflt.ShowSoulariumOdds && dflt.ShowSoulariumRisk && dflt.ShowSoulariumResult && dflt.ShowNextDraw && dflt.ShowPayoffsLeft && dflt.ShowLethalNextDraw);
		var offs = PluginSettings.Parse(new[] { "SettingsVersion=2", "ShowSoulariumOdds=False", "ShowSoulariumRisk=False", "ShowSoulariumResult=False", "ShowNextDraw=False", "ShowPayoffsLeft=False", "ShowLethalNextDraw=False" });
		CheckTrue("new line toggles parse False", !offs.ShowSoulariumOdds && !offs.ShowSoulariumRisk && !offs.ShowSoulariumResult && !offs.ShowNextDraw && !offs.ShowPayoffsLeft && !offs.ShowLethalNextDraw);

		// ---- log files: not held open between flushes, nothing lost while a reader locks the file
		var marker = "selftest-" + Guid.NewGuid().ToString("N");
		ProbeLog.Line("TEST", marker + " a");
		ProbeLog.Flush();
		var logPath = ProbeLog.CurrentTextLogPath;
		bool exclusiveOk;
		try { using(new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.None)) exclusiveOk = true; } catch(IOException) { exclusiveOk = false; }
		CheckTrue("log file not held open after flush (exclusive open succeeds)", exclusiveOk);
		using(new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.None))
		{
			ProbeLog.Line("TEST", marker + " b");
			ProbeLog.Flush(); // blocked by the exclusive reader: must stay buffered, not throw or drop
		}
		ProbeLog.Flush();
		string logText;
		using(var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.Read)) // like Notepad / Copy-Item
		using(var rd = new StreamReader(fs)) logText = rd.ReadToEnd();
		CheckTrue("lines written while a reader blocked the file are flushed afterwards, once each",
			logText.Contains(marker + " a") && System.Text.RegularExpressions.Regex.Matches(logText, marker + " b").Count == 1);

		Console.WriteLine();
		foreach(var o in OddsEngine.ForHand(s, rules)) Console.WriteLine("  " + o);
		Console.WriteLine(_fail == 0 ? "\nALL PASS" : $"\n{_fail} FAILED");
		return _fail == 0 ? 0 : 1;
	}
}
