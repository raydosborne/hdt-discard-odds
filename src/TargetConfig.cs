using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace DiscardOdds
{
	// User-editable target-card configuration (no HDT/WPF types, so it is unit-tested).
	// File: %AppData%\HearthstoneDeckTracker\DiscardOdds\targets.json
	//
	// "Targets" are the cards you care about drawing (or discarding) in a deck: payoffs, combo pieces, win conditions.
	// Each deck gets its own list, keyed by HDT deck id (falls back to the deck name, so a re-imported deck still matches).
	// Presets are example lists; a preset with autoApplyMinMatches > 0 is used for any deck that has no list of its own
	// but contains at least that many of the preset's cards.

	public sealed class TargetCard
	{
		public string Id;
		public string Name; // optional, for readability only
	}

	public sealed class DeckTargets
	{
		public string DeckId;
		public string DeckName;
		public List<TargetCard> Targets = new List<TargetCard>();
	}

	public sealed class TargetPreset
	{
		public string Name;
		public string Note;
		public int AutoApplyMinMatches;
		public List<TargetCard> Targets = new List<TargetCard>();
	}

	public sealed class ResolvedTargets
	{
		public HashSet<string> Ids = new HashSet<string>();
		/// <summary>"deck", "preset", or "none".</summary>
		public string Source = "none";
		public string PresetName;

		public string Describe() => Source == "deck" ? "your list for this deck"
			: Source == "preset" ? $"preset '{PresetName}' (auto)"
			: "none set";
	}

	public sealed class TargetConfig
	{
		public const int CurrentVersion = 1;
		public List<DeckTargets> Decks = new List<DeckTargets>();
		public List<TargetPreset> Presets = new List<TargetPreset>();

		/// <summary>Built-in example preset. Shipped as data in targets.json, not used unless a deck matches it.</summary>
		public static TargetConfig CreateDefault() => new TargetConfig
		{
			Presets =
			{
				new TargetPreset
				{
					Name = "Discard Warlock payoffs",
					Note = "Example preset: cards with a 'when discarded' effect. Used automatically for decks with no list of their own that contain 3+ of these.",
					AutoApplyMinMatches = 3,
					Targets =
					{
						new TargetCard { Id = "RLK_534", Name = "Soul Barrage" },
						new TargetCard { Id = "RLK_532", Name = "Walking Dead" },
						new TargetCard { Id = "BT_300", Name = "Hand of Gul'dan" },
						new TargetCard { Id = "CATA_499", Name = "Disposable Acolytes" },
						new TargetCard { Id = "KAR_205", Name = "Silverware Golem" },
					}
				}
			}
		};

		public DeckTargets FindDeck(string deckId, string deckName)
		{
			if(!string.IsNullOrEmpty(deckId))
			{
				var byId = Decks.FirstOrDefault(d => string.Equals(d.DeckId, deckId, StringComparison.OrdinalIgnoreCase));
				if(byId != null) return byId;
			}
			if(!string.IsNullOrEmpty(deckName))
				return Decks.FirstOrDefault(d => string.Equals(d.DeckName?.Trim(), deckName.Trim(), StringComparison.OrdinalIgnoreCase));
			return null;
		}

		public void SetDeckTargets(string deckId, string deckName, IEnumerable<TargetCard> targets)
		{
			var entry = FindDeck(deckId, deckName);
			if(entry == null)
			{
				entry = new DeckTargets();
				Decks.Add(entry);
			}
			entry.DeckId = string.IsNullOrEmpty(deckId) ? entry.DeckId : deckId;
			entry.DeckName = string.IsNullOrEmpty(deckName) ? entry.DeckName : deckName;
			entry.Targets = targets.Where(t => !string.IsNullOrWhiteSpace(t?.Id))
				.GroupBy(t => t.Id.Trim()).Select(g => new TargetCard { Id = g.Key, Name = g.First().Name }).ToList();
		}

		public bool RemoveDeck(string deckId, string deckName)
		{
			var entry = FindDeck(deckId, deckName);
			return entry != null && Decks.Remove(entry);
		}

		/// <summary>Target set for a deck: its own list if present (even if empty), else the first auto-matching preset.</summary>
		public ResolvedTargets Resolve(string deckId, string deckName, IEnumerable<string> deckCardIds)
		{
			var r = new ResolvedTargets();
			var own = FindDeck(deckId, deckName);
			if(own != null)
			{
				r.Source = "deck";
				r.Ids = new HashSet<string>(own.Targets.Select(t => t.Id));
				return r;
			}
			var inDeck = new HashSet<string>(deckCardIds ?? Enumerable.Empty<string>());
			foreach(var p in Presets.Where(p => p.AutoApplyMinMatches > 0))
			{
				var ids = new HashSet<string>(p.Targets.Select(t => t.Id));
				if(ids.Count(inDeck.Contains) >= p.AutoApplyMinMatches)
				{
					r.Source = "preset";
					r.PresetName = p.Name;
					r.Ids = ids;
					return r;
				}
			}
			return r;
		}

		// ------------------------------------------------------------------ JSON

		public static TargetConfig Parse(string json)
		{
			var root = MiniJson.Parse(json) as Dictionary<string, object>
			           ?? throw new FormatException("targets.json: top level must be an object");
			var cfg = new TargetConfig();
			if(root.TryGetValue("decks", out var decks) && decks is List<object> dl)
				foreach(var o in dl.OfType<Dictionary<string, object>>())
					cfg.Decks.Add(new DeckTargets { DeckId = Str(o, "deckId"), DeckName = Str(o, "deckName"), Targets = Cards(o) });
			if(root.TryGetValue("presets", out var presets) && presets is List<object> pl)
				foreach(var o in pl.OfType<Dictionary<string, object>>())
					cfg.Presets.Add(new TargetPreset
					{
						Name = Str(o, "name") ?? "preset", Note = Str(o, "note"),
						AutoApplyMinMatches = o.TryGetValue("autoApplyMinMatches", out var n) && n is double d ? (int)d : 0,
						Targets = Cards(o)
					});
			return cfg;
		}

		private static string Str(Dictionary<string, object> o, string k) => o.TryGetValue(k, out var v) ? v as string : null;

		private static List<TargetCard> Cards(Dictionary<string, object> o)
		{
			var res = new List<TargetCard>();
			if(!o.TryGetValue("targets", out var t) || !(t is List<object> list)) return res;
			foreach(var item in list)
			{
				if(item is string s && s.Trim().Length > 0) res.Add(new TargetCard { Id = s.Trim() });
				else if(item is Dictionary<string, object> c && Str(c, "id") is string id && id.Trim().Length > 0)
					res.Add(new TargetCard { Id = id.Trim(), Name = Str(c, "name") });
			}
			return res;
		}

		public string ToJson()
		{
			var sb = new StringBuilder();
			sb.Append("{\n");
			sb.Append("  \"_help\": \"Target cards per deck. Edit here or via HDT: Plugins > Discard Odds > Choose target cards. Targets are HearthstoneJSON card ids (e.g. RLK_534); 'name' is only a label. A deck matches by deckId first, then by deckName. Presets with autoApplyMinMatches > 0 are used for decks without their own list that contain at least that many preset cards.\",\n");
			sb.Append("  \"version\": ").Append(CurrentVersion).Append(",\n");
			sb.Append("  \"decks\": [");
			for(var i = 0; i < Decks.Count; i++)
			{
				var d = Decks[i];
				sb.Append(i == 0 ? "\n" : ",\n");
				sb.Append("    {\n");
				sb.Append("      \"deckId\": ").Append(Q(d.DeckId)).Append(",\n");
				sb.Append("      \"deckName\": ").Append(Q(d.DeckName)).Append(",\n");
				sb.Append("      \"targets\": ");
				WriteCards(sb, d.Targets, "      ");
				sb.Append("\n    }");
			}
			sb.Append(Decks.Count > 0 ? "\n  ],\n" : "],\n");
			sb.Append("  \"presets\": [");
			for(var i = 0; i < Presets.Count; i++)
			{
				var p = Presets[i];
				sb.Append(i == 0 ? "\n" : ",\n");
				sb.Append("    {\n");
				sb.Append("      \"name\": ").Append(Q(p.Name)).Append(",\n");
				if(p.Note != null) sb.Append("      \"note\": ").Append(Q(p.Note)).Append(",\n");
				sb.Append("      \"autoApplyMinMatches\": ").Append(p.AutoApplyMinMatches.ToString(CultureInfo.InvariantCulture)).Append(",\n");
				sb.Append("      \"targets\": ");
				WriteCards(sb, p.Targets, "      ");
				sb.Append("\n    }");
			}
			sb.Append(Presets.Count > 0 ? "\n  ]\n" : "]\n");
			sb.Append("}\n");
			return sb.ToString();
		}

		private static void WriteCards(StringBuilder sb, List<TargetCard> cards, string indent)
		{
			if(cards.Count == 0) { sb.Append("[]"); return; }
			sb.Append("[\n");
			for(var i = 0; i < cards.Count; i++)
			{
				sb.Append(indent).Append("  { \"id\": ").Append(Q(cards[i].Id));
				if(!string.IsNullOrEmpty(cards[i].Name)) sb.Append(", \"name\": ").Append(Q(cards[i].Name));
				sb.Append(" }").Append(i < cards.Count - 1 ? ",\n" : "\n");
			}
			sb.Append(indent).Append(']');
		}

		private static string Q(string s) => s == null ? "null" : Json.Serialize(s);

		// ------------------------------------------------------------------ file I/O

		/// <summary>
		/// Loads the file; creates it with the default presets if missing. On a parse error the file is left untouched,
		/// the defaults are used in memory and <paramref name="error"/> says why.
		/// </summary>
		public static TargetConfig Load(string path, out string error)
		{
			error = null;
			try
			{
				if(!File.Exists(path))
				{
					var def = CreateDefault();
					def.Save(path);
					return def;
				}
				return Parse(File.ReadAllText(path, Encoding.UTF8));
			}
			catch(Exception ex)
			{
				error = ex.Message;
				return CreateDefault();
			}
		}

		/// <summary>Writes atomically; keeps the previous file as targets.json.bak.</summary>
		public void Save(string path)
		{
			var dir = Path.GetDirectoryName(path);
			if(!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
			var tmp = path + ".tmp";
			File.WriteAllText(tmp, ToJson(), new UTF8Encoding(false));
			if(File.Exists(path))
			{
				File.Copy(path, path + ".bak", true);
				File.Delete(path);
			}
			File.Move(tmp, path);
		}
	}

	/// <summary>Minimal JSON reader: objects, arrays, strings, numbers (double), true/false/null.</summary>
	public static class MiniJson
	{
		public static object Parse(string s)
		{
			var i = 0;
			var v = Value(s, ref i);
			Ws(s, ref i);
			if(i != s.Length) throw Err(s, i, "trailing characters");
			return v;
		}

		private static FormatException Err(string s, int i, string what)
		{
			var line = 1 + s.Take(Math.Min(i, s.Length)).Count(c => c == '\n');
			return new FormatException($"JSON error at line {line}: {what}");
		}

		private static void Ws(string s, ref int i)
		{
			while(i < s.Length && char.IsWhiteSpace(s[i])) i++;
		}

		private static object Value(string s, ref int i)
		{
			Ws(s, ref i);
			if(i >= s.Length) throw Err(s, i, "unexpected end");
			var c = s[i];
			if(c == '{') return Obj(s, ref i);
			if(c == '[') return Arr(s, ref i);
			if(c == '"') return Str(s, ref i);
			if(c == '-' || char.IsDigit(c)) return Num(s, ref i);
			if(string.CompareOrdinal(s, i, "true", 0, 4) == 0) { i += 4; return true; }
			if(string.CompareOrdinal(s, i, "false", 0, 5) == 0) { i += 5; return false; }
			if(string.CompareOrdinal(s, i, "null", 0, 4) == 0) { i += 4; return null; }
			throw Err(s, i, $"unexpected '{c}'");
		}

		private static Dictionary<string, object> Obj(string s, ref int i)
		{
			var d = new Dictionary<string, object>();
			i++;
			Ws(s, ref i);
			if(i < s.Length && s[i] == '}') { i++; return d; }
			while(true)
			{
				Ws(s, ref i);
				if(i >= s.Length || s[i] != '"') throw Err(s, i, "expected a \"key\"");
				var k = Str(s, ref i);
				Ws(s, ref i);
				if(i >= s.Length || s[i] != ':') throw Err(s, i, "expected ':'");
				i++;
				d[k] = Value(s, ref i);
				Ws(s, ref i);
				if(i < s.Length && s[i] == ',') { i++; continue; }
				if(i < s.Length && s[i] == '}') { i++; return d; }
				throw Err(s, i, "expected ',' or '}'");
			}
		}

		private static List<object> Arr(string s, ref int i)
		{
			var l = new List<object>();
			i++;
			Ws(s, ref i);
			if(i < s.Length && s[i] == ']') { i++; return l; }
			while(true)
			{
				l.Add(Value(s, ref i));
				Ws(s, ref i);
				if(i < s.Length && s[i] == ',') { i++; continue; }
				if(i < s.Length && s[i] == ']') { i++; return l; }
				throw Err(s, i, "expected ',' or ']'");
			}
		}

		private static string Str(string s, ref int i)
		{
			var sb = new StringBuilder();
			i++;
			while(i < s.Length)
			{
				var c = s[i++];
				if(c == '"') return sb.ToString();
				if(c != '\\') { sb.Append(c); continue; }
				if(i >= s.Length) break;
				var e = s[i++];
				switch(e)
				{
					case 'n': sb.Append('\n'); break;
					case 't': sb.Append('\t'); break;
					case 'r': sb.Append('\r'); break;
					case 'b': sb.Append('\b'); break;
					case 'f': sb.Append('\f'); break;
					case 'u':
						if(i + 4 > s.Length) throw Err(s, i, "bad \\u escape");
						sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
						i += 4;
						break;
					default: sb.Append(e); break;
				}
			}
			throw Err(s, i, "unterminated string");
		}

		private static double Num(string s, ref int i)
		{
			var start = i;
			while(i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
			if(!double.TryParse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
				throw Err(s, start, "bad number");
			return d;
		}
	}
}
