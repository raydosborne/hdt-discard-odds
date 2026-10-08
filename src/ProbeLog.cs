using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace DiscardOdds
{
	/// <summary>
	/// Thread-safe file logger. Writes two files per day into
	/// %AppData%\HearthstoneDeckTracker\DiscardOdds\logs\ (outside the Plugins dir, which HDT syncs/cleans):
	///   m0_YYYY-MM-DD.log     human-readable probe log
	///   m0_YYYY-MM-DD.jsonl   one JSON object per probe finding (for offline analysis)
	/// </summary>
	public static class ProbeLog
	{
		private static readonly object Lock = new object();
		private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
		// Lines are buffered and appended in short open-write-close bursts (on every plugin tick, or when the buffer
		// fills). No handle is held between bursts, so the logs can be opened, copied or tailed while HDT runs, even
		// by tools that open with FileShare.Read. If a reader blocks a burst, lines stay buffered for the next one.
		private static readonly List<string> PendingText = new List<string>();
		private static readonly List<string> PendingJson = new List<string>();
		private const int FlushAt = 200;
		private const int MaxPending = 20000;
		private static string _day;
		private static string _jsonPath;

		public static string RootDir => Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HearthstoneDeckTracker", "DiscardOdds");

		public static string LogDir => Path.Combine(RootDir, "logs");

		public static string CurrentTextLogPath { get; private set; }

		public static int GameIndex { get; set; }

		private static void EnsureDay()
		{
			var day = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
			if(_day == day)
				return;
			if(_day != null)
				FlushInternal(); // finish yesterday's file first
			Directory.CreateDirectory(LogDir);
			_day = day;
			CurrentTextLogPath = Path.Combine(LogDir, $"m0_{day}.log");
			_jsonPath = Path.Combine(LogDir, $"m0_{day}.jsonl");
		}

		/// <summary>Human-readable line: "HH:mm:ss.fff [CAT] message".</summary>
		public static void Line(string category, string message)
		{
			try
			{
				lock(Lock)
				{
					EnsureDay();
					PendingText.Add($"{DateTime.Now:HH:mm:ss.fff} [{category}] {message}");
					if(PendingText.Count >= FlushAt) FlushInternal();
				}
			}
			catch
			{
				// never let logging break HDT
			}
		}

		/// <summary>Structured record. Values may be string, bool, numbers, null, IEnumerable, or IDictionary&lt;string, object&gt;.</summary>
		public static void Record(string probe, IDictionary<string, object> fields)
		{
			try
			{
				var all = new Dictionary<string, object>
				{
					["ts"] = DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture),
					["game"] = GameIndex,
					["probe"] = probe
				};
				foreach(var kv in fields)
					all[kv.Key] = kv.Value;
				var json = Json.Serialize(all);
				lock(Lock)
				{
					EnsureDay();
					PendingJson.Add(json);
					if(PendingJson.Count >= FlushAt) FlushInternal();
				}
			}
			catch
			{
			}
		}

		/// <summary>Writes buffered lines (called on every plugin tick, ~10x per second, and on unload).</summary>
		public static void Flush()
		{
			try { lock(Lock) FlushInternal(); } catch { }
		}

		public static void Close() => Flush();

		private static void FlushInternal()
		{
			if(_day == null) return;
			Append(CurrentTextLogPath, PendingText);
			Append(_jsonPath, PendingJson);
		}

		private static void Append(string path, List<string> pending)
		{
			if(pending.Count == 0) return;
			try
			{
				using(var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
				using(var w = new StreamWriter(fs, Utf8))
				{
					foreach(var l in pending) w.WriteLine(l);
				}
				pending.Clear();
			}
			catch(Exception)
			{
				// e.g. a reader has the file open without write sharing: keep the lines for the next flush
				if(pending.Count > MaxPending) pending.RemoveRange(0, pending.Count - MaxPending);
			}
		}
	}

	/// <summary>Tiny JSON writer so the plugin has no dependency beyond HDT/HearthDb/.NET.</summary>
	public static class Json
	{
		public static string Serialize(object value)
		{
			var sb = new StringBuilder();
			Write(sb, value);
			return sb.ToString();
		}

		private static void Write(StringBuilder sb, object v)
		{
			switch(v)
			{
				case null:
					sb.Append("null");
					break;
				case string s:
					WriteString(sb, s);
					break;
				case bool b:
					sb.Append(b ? "true" : "false");
					break;
				case int or long or short or byte:
					sb.Append(Convert.ToString(v, CultureInfo.InvariantCulture));
					break;
				case double d:
					sb.Append(double.IsNaN(d) || double.IsInfinity(d) ? "null" : d.ToString("0.######", CultureInfo.InvariantCulture));
					break;
				case float f:
					Write(sb, (double)f);
					break;
				case IDictionary<string, object> dict:
					sb.Append('{');
					var first = true;
					foreach(var kv in dict)
					{
						if(!first) sb.Append(',');
						first = false;
						WriteString(sb, kv.Key);
						sb.Append(':');
						Write(sb, kv.Value);
					}
					sb.Append('}');
					break;
				case System.Collections.IEnumerable list:
					sb.Append('[');
					var f2 = true;
					foreach(var item in list)
					{
						if(!f2) sb.Append(',');
						f2 = false;
						Write(sb, item);
					}
					sb.Append(']');
					break;
				default:
					WriteString(sb, v.ToString());
					break;
			}
		}

		private static void WriteString(StringBuilder sb, string s)
		{
			sb.Append('"');
			foreach(var c in s)
			{
				switch(c)
				{
					case '"': sb.Append("\\\""); break;
					case '\\': sb.Append("\\\\"); break;
					case '\n': sb.Append("\\n"); break;
					case '\r': sb.Append("\\r"); break;
					case '\t': sb.Append("\\t"); break;
					default:
						if(c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
						else sb.Append(c);
						break;
				}
			}
			sb.Append('"');
		}
	}

	/// <summary>Very small key=value settings file at %AppData%\HearthstoneDeckTracker\DiscardOdds\settings.ini.</summary>
	public class PluginSettings
	{
		// Default widget position (fractions of the overlay canvas). Lower-center-left of the board, above the
		// player's hand: clear of HDT's opponent deck list (left edge) and player deck list (right edge).
		public const double DefaultLeftFraction = 0.22;
		public const double DefaultTopFraction = 0.64;
		// Default used by earlier builds (top-left; overlapped the opponent's deck list). Older settings.ini files
		// always stored the position, even if never moved, so this value is treated as "not customised" on load.
		private const double LegacyDefaultLeft = 0.02;
		private const double LegacyDefaultTop = 0.35;

		public double WidgetLeftFraction = DefaultLeftFraction;   // fraction of overlay canvas width
		public double WidgetTopFraction = DefaultTopFraction;     // fraction of overlay canvas height
		public bool WidgetPositionSaved = false;                  // true once the user has dragged the widget; saved position then wins
		public bool WidgetEnabled = true;
		public bool ShowInMenus = false;
		public bool VerboseDeckDump = true;        // full PlayerCardList dump at game start / mulligan / each own turn
		public bool CheckForUpdates = true;        // on HDT start, ask this repo's GitHub Releases for the latest version
		public bool AutoUpdate = true;             // ...and if newer, download + verify it; it's swapped in on the next HDT restart
		public bool ShowLethalCheck = true;        // "Face damage: X vs Y health" line on your turn
		public bool ShowDetails = false;           // dim second line with the reason behind each number (default OFF)
		public bool ShowOneDrop = true;            // opening one-drop odds (mulligan + your turn 1 only)
		public bool CompactMode = true;            // smaller font, tighter padding, short header (default ON)
		// v0.1.6 lines, each with its own menu toggle (all on by default)
		public bool ShowSoulariumOdds = true;      // "Soularium 1+ 76% · 2+ 31% · 3/3 4% · whiff 24%"
		public bool ShowSoulariumRisk = true;      // "Risk @2: Payoff 1+ 76% · Waste 1+ 45% · avg 0.6 wasted"
		public bool ShowSoulariumResult = true;    // "Soularium → 2 payoffs: 30% chance" (rest of that turn)
		public bool ShowNextDraw = true;           // "Next draw: Payoff 33% · Playable 60%"
		public bool ShowPayoffsLeft = true;        // "5 payoffs left"
		public bool ShowLethalNextDraw = true;     // "Lethal next draw ~12%" under a short lethal check
		/// <summary>2 = v0.1.5. Files from older versions get ShowDetails reset to off once (it is still in the menu).</summary>
		public const int CurrentSettingsVersion = 2;
		public int SettingsVersion = CurrentSettingsVersion;

		private static string PathOnDisk => Path.Combine(ProbeLog.RootDir, "settings.ini");

		public static PluginSettings Load()
		{
			try
			{
				if(File.Exists(PathOnDisk))
					return Parse(File.ReadAllLines(PathOnDisk));
			}
			catch(Exception ex)
			{
				ProbeLog.Line("SETTINGS", "load failed: " + ex.Message);
			}
			return new PluginSettings();
		}

		/// <summary>Parses settings.ini lines (no file I/O, so it is unit-tested).</summary>
		public static PluginSettings Parse(IEnumerable<string> lines)
		{
			var s = new PluginSettings();
			try
			{
				double? left = null, top = null;
				bool? saved = null;
				var fileVersion = 1;
				foreach(var raw in lines)
				{
					var line = raw.Trim();
					if(line.Length == 0 || line.StartsWith("#")) continue;
					var i = line.IndexOf('=');
					if(i <= 0) continue;
					var k = line.Substring(0, i).Trim();
					var v = line.Substring(i + 1).Trim();
					switch(k)
					{
						case nameof(WidgetLeftFraction): left = ParseD(v, DefaultLeftFraction); break;
						case nameof(WidgetTopFraction): top = ParseD(v, DefaultTopFraction); break;
						case nameof(WidgetPositionSaved): saved = ParseB(v, false); break;
						case nameof(WidgetEnabled): s.WidgetEnabled = ParseB(v, s.WidgetEnabled); break;
						case nameof(ShowInMenus): s.ShowInMenus = ParseB(v, s.ShowInMenus); break;
						case nameof(VerboseDeckDump): s.VerboseDeckDump = ParseB(v, s.VerboseDeckDump); break;
						case nameof(CheckForUpdates): s.CheckForUpdates = ParseB(v, s.CheckForUpdates); break;
						case nameof(AutoUpdate): s.AutoUpdate = ParseB(v, s.AutoUpdate); break;
						case nameof(ShowLethalCheck): s.ShowLethalCheck = ParseB(v, s.ShowLethalCheck); break;
						case nameof(ShowDetails): s.ShowDetails = ParseB(v, s.ShowDetails); break;
						case nameof(ShowOneDrop): s.ShowOneDrop = ParseB(v, s.ShowOneDrop); break;
						case nameof(CompactMode): s.CompactMode = ParseB(v, s.CompactMode); break;
						case nameof(ShowSoulariumOdds): s.ShowSoulariumOdds = ParseB(v, s.ShowSoulariumOdds); break;
						case nameof(ShowSoulariumRisk): s.ShowSoulariumRisk = ParseB(v, s.ShowSoulariumRisk); break;
						case nameof(ShowSoulariumResult): s.ShowSoulariumResult = ParseB(v, s.ShowSoulariumResult); break;
						case nameof(ShowNextDraw): s.ShowNextDraw = ParseB(v, s.ShowNextDraw); break;
						case nameof(ShowPayoffsLeft): s.ShowPayoffsLeft = ParseB(v, s.ShowPayoffsLeft); break;
						case nameof(ShowLethalNextDraw): s.ShowLethalNextDraw = ParseB(v, s.ShowLethalNextDraw); break;
						case nameof(SettingsVersion): fileVersion = int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var fv) ? fv : 1; break;
					}
				}
				// Show details is off by default; a file from v0.1.4 or earlier gets it switched off once.
				if(fileVersion < CurrentSettingsVersion) s.ShowDetails = false;
				s.SettingsVersion = CurrentSettingsVersion;
				// A user-saved position takes precedence over the default. Legacy files (no WidgetPositionSaved key)
				// count as saved only if the stored position differs from the old built-in default.
				var isSaved = saved ?? (left.HasValue && top.HasValue
				                        && !(Near(left.Value, LegacyDefaultLeft) && Near(top.Value, LegacyDefaultTop)));
				if(isSaved && left.HasValue && top.HasValue)
				{
					s.WidgetLeftFraction = left.Value;
					s.WidgetTopFraction = top.Value;
					s.WidgetPositionSaved = true;
				}
			}
			catch(Exception ex)
			{
				ProbeLog.Line("SETTINGS", "parse failed: " + ex.Message);
			}
			return s;
		}

		public void Save()
		{
			try
			{
				Directory.CreateDirectory(ProbeLog.RootDir);
				File.WriteAllLines(PathOnDisk, new[]
				{
					"# Discard Odds settings. Fractions are relative to the HDT overlay size.",
					$"{nameof(SettingsVersion)}={SettingsVersion}",
					$"{nameof(WidgetLeftFraction)}={WidgetLeftFraction.ToString("0.####", CultureInfo.InvariantCulture)}",
					$"{nameof(WidgetTopFraction)}={WidgetTopFraction.ToString("0.####", CultureInfo.InvariantCulture)}",
					$"{nameof(WidgetPositionSaved)}={WidgetPositionSaved}",
					$"{nameof(WidgetEnabled)}={WidgetEnabled}",
					$"{nameof(ShowInMenus)}={ShowInMenus}",
					$"{nameof(VerboseDeckDump)}={VerboseDeckDump}",
					$"{nameof(ShowLethalCheck)}={ShowLethalCheck}",
					$"{nameof(ShowDetails)}={ShowDetails}",
					$"{nameof(ShowOneDrop)}={ShowOneDrop}",
					$"{nameof(CompactMode)}={CompactMode}",
					$"{nameof(ShowSoulariumOdds)}={ShowSoulariumOdds}",
					$"{nameof(ShowSoulariumRisk)}={ShowSoulariumRisk}",
					$"{nameof(ShowSoulariumResult)}={ShowSoulariumResult}",
					$"{nameof(ShowNextDraw)}={ShowNextDraw}",
					$"{nameof(ShowPayoffsLeft)}={ShowPayoffsLeft}",
					$"{nameof(ShowLethalNextDraw)}={ShowLethalNextDraw}",
					"# Updates: CheckForUpdates asks github.com/" + UpdateLogic.Owner + "/" + UpdateLogic.Repo + " for the latest release on HDT start.",
					"# AutoUpdate also downloads it (SHA-256 verified); it is swapped in when HDT restarts. Set either to False to opt out.",
					$"{nameof(CheckForUpdates)}={CheckForUpdates}",
					$"{nameof(AutoUpdate)}={AutoUpdate}"
				});
			}
			catch(Exception ex)
			{
				ProbeLog.Line("SETTINGS", "save failed: " + ex.Message);
			}
		}

		/// <summary>Back to the built-in default position (used by "Reset widget position").</summary>
		public void ResetPosition()
		{
			WidgetLeftFraction = DefaultLeftFraction;
			WidgetTopFraction = DefaultTopFraction;
			WidgetPositionSaved = false;
		}

		private static bool Near(double a, double b) => Math.Abs(a - b) < 1e-3;
		private static double ParseD(string v, double d) => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var r) ? r : d;
		private static bool ParseB(string v, bool d) => bool.TryParse(v, out var r) ? r : d;
	}
}
