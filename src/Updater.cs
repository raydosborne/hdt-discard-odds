using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace DiscardOdds
{
	/// <summary>
	/// Self-update from this repository's GitHub Releases.
	///
	/// HDT loads plugins from its local "Plugins" folder (that DLL is locked while HDT runs) and, on every start,
	/// copies newer files over from %AppData%\HearthstoneDeckTracker\Plugins. So an update is: download the release's
	/// DiscardOdds.dll to a staging file, verify its SHA-256 against the release's DiscardOdds.dll.sha256 and its
	/// assembly name/version against the tag, then replace the (unlocked) AppData copy. HDT's own sync swaps it in on
	/// the next restart. Every network or file error is logged and otherwise ignored (offline = silent).
	/// </summary>
	public class Updater
	{
		public static string StagingDir => Path.Combine(ProbeLog.RootDir, "update");

		private readonly Version _running;
		private int _busy;

		/// <summary>Short user-facing status for the widget/menu, or null when there is nothing to say.</summary>
		public volatile string Notice;
		public volatile ReleaseInfo Latest;
		private volatile string _installedTag;

		/// <summary>Called (on a worker thread) whenever Notice changes. A single callback, replaced on plugin reload.</summary>
		public volatile Action Changed;

		public Updater(Version running) => _running = UpdateLogic.Normalize(running);

		public void CheckInBackground(bool autoUpdate, bool manual)
		{
			if(Interlocked.Exchange(ref _busy, 1) == 1) return;
			Task.Run(async () =>
			{
				try { await Run(autoUpdate, manual).ConfigureAwait(false); }
				catch(Exception ex) { ProbeLog.Line("UPDATE", "check failed (ignored): " + ex.Message); if(manual) Set("Update check failed (offline?)"); }
				finally { Interlocked.Exchange(ref _busy, 0); }
			});
		}

		private async Task Run(bool autoUpdate, bool manual)
		{
			try
			{
				// Only matters if the process pinned an explicit protocol list; SystemDefault already allows TLS 1.2+.
				var sp = ServicePointManager.SecurityProtocol;
				if(sp != SecurityProtocolType.SystemDefault && (sp & SecurityProtocolType.Tls12) == 0)
					ServicePointManager.SecurityProtocol = sp | SecurityProtocolType.Tls12;
			}
			catch { }
			using(var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) })
			{
				http.DefaultRequestHeaders.UserAgent.ParseAdd("DiscardOdds-HDT-plugin/" + _running);
				http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

				string json;
				using(var resp = await http.GetAsync(UpdateLogic.LatestReleaseApi).ConfigureAwait(false))
				{
					if(!resp.IsSuccessStatusCode)
					{
						ProbeLog.Line("UPDATE", $"latest-release check: HTTP {(int)resp.StatusCode} (ignored)");
						if(manual) Set("Update check failed (GitHub said " + (int)resp.StatusCode + ")");
						return;
					}
					json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
				}
				var rel = UpdateLogic.ParseRelease(json);
				if(rel == null)
				{
					ProbeLog.Line("UPDATE", "latest release has no usable version tag / DLL + checksum assets (ignored)");
					if(manual) Set("No usable release found");
					return;
				}
				Latest = rel;
				if(!UpdateLogic.IsNewer(rel.Version, _running))
				{
					ProbeLog.Line("UPDATE", $"up to date (running {_running}, latest {rel.Tag})");
					Set($"Up to date (v{_running})"); // shown in the Plugins menu only
					return;
				}
				if(_installedTag == rel.Tag)
				{
					Set($"Update {rel.Tag} ready: restart HDT to finish");
					return;
				}
				ProbeLog.Line("UPDATE", $"newer release {rel.Tag} (running {_running}); auto-update {(autoUpdate ? "on" : "off")}");
				if(!autoUpdate)
				{
					Set($"Update {rel.Tag} available (auto-update off): open release page");
					return;
				}

				// ---- download + verify
				var expected = UpdateLogic.ParseChecksum(await GetString(http, rel.ChecksumUrl, 4096).ConfigureAwait(false), UpdateLogic.DllAsset);
				if(expected == null) { Fail(rel, "checksum file unreadable"); return; }
				var bytes = await GetBytes(http, rel.DllUrl, UpdateLogic.MaxDllBytes).ConfigureAwait(false);
				if(rel.DllSize > 0 && bytes.Length != rel.DllSize) { Fail(rel, $"size {bytes.Length} != {rel.DllSize}"); return; }
				var actual = UpdateLogic.Sha256Hex(bytes);
				if(actual != expected) { Fail(rel, $"SHA-256 mismatch (got {actual}, release says {expected})"); return; }

				Directory.CreateDirectory(StagingDir);
				var staged = Path.Combine(StagingDir, UpdateLogic.DllAsset);
				File.WriteAllBytes(staged, bytes);
				var an = AssemblyName.GetAssemblyName(staged); // reads metadata only; does not load it
				if(an.Name != "DiscardOdds" || UpdateLogic.Normalize(an.Version).CompareTo(rel.Version) != 0)
				{
					Fail(rel, $"downloaded assembly is {an.Name} {an.Version}, expected DiscardOdds {rel.Version}");
					TryDelete(staged);
					return;
				}
				ProbeLog.Line("UPDATE", $"downloaded {rel.Tag} ({bytes.Length} bytes, sha256 {actual}) to {staged}");

				// ---- swap into HDT's AppData plugin folder (picked up on next HDT start)
				var target = FindAppDataPluginDll();
				if(target == null)
				{
					ProbeLog.Line("UPDATE", "could not locate DiscardOdds.dll under " + SafeAppDataPluginDir() + "; left staged");
					Set($"Discard Odds {rel.Tag} downloaded: copy it from {StagingDir} into your HDT Plugins folder");
					return;
				}
				var tmp = target + ".download";
				File.Copy(staged, tmp, true);
				var backup = Path.Combine(StagingDir, "DiscardOdds.previous.dll");
				TryDelete(backup);
				try { File.Replace(tmp, target, backup, true); }
				catch(IOException) { File.Copy(tmp, target, true); TryDelete(tmp); } // e.g. backup on another volume
				File.SetLastWriteTimeUtc(target, DateTime.UtcNow); // HDT syncs files newer than its local copy
				_installedTag = rel.Tag;
				ProbeLog.Line("UPDATE", $"installed {rel.Tag} to {target} (previous kept at {backup}); restart HDT to finish");
				Set($"Update {rel.Tag} ready: restart HDT to finish");
			}
		}

		/// <summary>The AppData copy of the running DLL: the same relative path under the AppData Plugins folder.</summary>
		private static string FindAppDataPluginDll()
		{
			var appData = AppDataPluginDir;
			var local = Path.GetFullPath("Plugins"); // HDT's LocalPluginDirectory: "Plugins" relative to its working dir
			var running = typeof(Updater).Assembly.Location;
			if(!string.IsNullOrEmpty(running) && running.StartsWith(local.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
			{
				var candidate = Path.Combine(appData, running.Substring(local.TrimEnd('\\', '/').Length + 1));
				if(File.Exists(candidate)) return candidate;
			}
			if(!Directory.Exists(appData)) return null;
			var found = Directory.GetFiles(appData, UpdateLogic.DllAsset, SearchOption.AllDirectories);
			return found.Length == 1 ? found[0] : null;
		}

		// Same paths HDT's (internal) PluginManager uses: PluginDirectory = Config.AppDataPath\Plugins.
		private static string AppDataPluginDir => Path.Combine(Hearthstone_Deck_Tracker.Config.AppDataPath, "Plugins");

		private static string SafeAppDataPluginDir()
		{
			try { return AppDataPluginDir; } catch { return "%AppData%\\HearthstoneDeckTracker\\Plugins"; }
		}

		private void Fail(ReleaseInfo rel, string why)
		{
			ProbeLog.Line("UPDATE", $"{rel.Tag} NOT installed: {why}");
			Set($"Update {rel.Tag} available (auto-update failed verification; not installed)");
		}

		private void Set(string notice)
		{
			Notice = notice;
			try { Changed?.Invoke(); } catch { }
		}

		private static async Task<byte[]> GetBytes(HttpClient http, string url, long max)
		{
			using(var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
			{
				resp.EnsureSuccessStatusCode();
				if(resp.Content.Headers.ContentLength > max) throw new InvalidDataException("asset too large");
				using(var s = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
				using(var ms = new MemoryStream())
				{
					var buf = new byte[81920];
					int n;
					while((n = await s.ReadAsync(buf, 0, buf.Length).ConfigureAwait(false)) > 0)
					{
						ms.Write(buf, 0, n);
						if(ms.Length > max) throw new InvalidDataException("asset too large");
					}
					return ms.ToArray();
				}
			}
		}

		private static async Task<string> GetString(HttpClient http, string url, long max) =>
			System.Text.Encoding.UTF8.GetString(await GetBytes(http, url, max).ConfigureAwait(false));

		private static void TryDelete(string path)
		{
			try { if(File.Exists(path)) File.Delete(path); } catch { }
		}
	}
}
