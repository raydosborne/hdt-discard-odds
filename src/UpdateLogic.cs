using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace DiscardOdds
{
	/// <summary>One GitHub release of this plugin, reduced to what the updater needs.</summary>
	public class ReleaseInfo
	{
		public string Tag;
		public Version Version;
		public string HtmlUrl;
		public string DllUrl;
		public long DllSize;
		public string ChecksumUrl;
	}

	/// <summary>
	/// Pure update logic (no network, no file I/O, no HDT types) so it is unit-tested. The updater only ever talks
	/// to this repository's GitHub Releases, over HTTPS, and only accepts assets hosted under this repository's
	/// release-download path.
	/// </summary>
	public static class UpdateLogic
	{
		public const string Owner = "raydosborne";
		public const string Repo = "hdt-discard-odds";
		public const string LatestReleaseApi = "https://api.github.com/repos/" + Owner + "/" + Repo + "/releases/latest";
		public const string ReleasesPage = "https://github.com/" + Owner + "/" + Repo + "/releases";
		public const string DllAsset = "DiscardOdds.dll";
		public const string ChecksumAsset = "DiscardOdds.dll.sha256";
		public const long MaxDllBytes = 5 * 1024 * 1024; // sanity cap; the real DLL is well under 1 MB
		private const string AssetPrefix = "https://github.com/" + Owner + "/" + Repo + "/releases/download/";

		private static readonly Regex TagRx = new Regex(@"^v?(\d+)\.(\d+)(?:\.(\d+))?$", RegexOptions.CultureInvariant);
		private static readonly Regex HexRx = new Regex(@"^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant);

		/// <summary>"v0.2.0" / "0.2" / "v1.2.3" to a 3-part Version. Pre-release suffixes ("v1.0.0-beta") are rejected (null).</summary>
		public static Version ParseTag(string tag)
		{
			if(string.IsNullOrWhiteSpace(tag)) return null;
			var m = TagRx.Match(tag.Trim());
			if(!m.Success) return null;
			try
			{
				return new Version(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
					int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
					m.Groups[3].Success ? int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) : 0);
			}
			catch(OverflowException) { return null; }
		}

		/// <summary>Major.Minor.Build only (assembly versions carry a 4th part that releases don't).</summary>
		public static Version Normalize(Version v) => v == null ? null : new Version(v.Major, v.Minor, Math.Max(0, v.Build));

		public static bool IsNewer(Version remote, Version running)
		{
			if(remote == null) return false;
			if(running == null) return true;
			return Normalize(remote).CompareTo(Normalize(running)) > 0;
		}

		/// <summary>Only HTTPS download URLs for this repository's own release assets are accepted.</summary>
		public static bool IsTrustedAssetUrl(string url, string assetName)
		{
			if(string.IsNullOrEmpty(url) || !url.StartsWith(AssetPrefix, StringComparison.Ordinal)) return false;
			if(!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != "https" || u.Host != "github.com") return false;
			if(u.Query.Length > 0 || u.Fragment.Length > 0 || url.Contains("..")) return false;
			return url.EndsWith("/" + assetName, StringComparison.Ordinal);
		}

		/// <summary>
		/// Parses the /releases/latest JSON. Returns null for drafts, pre-releases, non-version tags, or a release
		/// without both the DLL and checksum assets (from this repository).
		/// </summary>
		public static ReleaseInfo ParseRelease(string json)
		{
			if(!(MiniJson.Parse(json) is Dictionary<string, object> root)) return null;
			if(Bool(root, "draft") || Bool(root, "prerelease")) return null;
			var tag = Str(root, "tag_name");
			var version = ParseTag(tag);
			if(version == null) return null;
			var r = new ReleaseInfo { Tag = tag, Version = version, HtmlUrl = Str(root, "html_url") };
			if(root.TryGetValue("assets", out var a) && a is List<object> assets)
			{
				foreach(var o in assets)
				{
					if(!(o is Dictionary<string, object> asset)) continue;
					var name = Str(asset, "name");
					var url = Str(asset, "browser_download_url");
					if(name == DllAsset && IsTrustedAssetUrl(url, DllAsset))
					{
						r.DllUrl = url;
						r.DllSize = asset.TryGetValue("size", out var s) && s != null ? Convert.ToInt64(s, CultureInfo.InvariantCulture) : 0;
					}
					else if(name == ChecksumAsset && IsTrustedAssetUrl(url, ChecksumAsset))
						r.ChecksumUrl = url;
				}
			}
			if(r.DllUrl == null || r.ChecksumUrl == null) return null;
			if(r.HtmlUrl == null || !r.HtmlUrl.StartsWith(ReleasesPage + "/", StringComparison.Ordinal)) r.HtmlUrl = ReleasesPage;
			return r;
		}

		/// <summary>
		/// Reads a sha256sum-style checksum file ("&lt;64 hex&gt;  DiscardOdds.dll", or just the hex). Returns lower-case
		/// hex, or null if there's no well-formed entry for the file.
		/// </summary>
		public static string ParseChecksum(string text, string fileName)
		{
			if(string.IsNullOrEmpty(text)) return null;
			foreach(var raw in text.Split('\n'))
			{
				var line = raw.Trim();
				if(line.Length == 0 || line.StartsWith("#")) continue;
				var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
				if(!HexRx.IsMatch(parts[0])) continue;
				if(parts.Length == 1) return parts[0].ToLowerInvariant();
				var name = parts[parts.Length - 1].TrimStart('*');
				if(name == fileName) return parts[0].ToLowerInvariant();
			}
			return null;
		}

		public static string Sha256Hex(byte[] data)
		{
			using(var sha = SHA256.Create())
			{
				var hash = sha.ComputeHash(data);
				var sb = new StringBuilder(64);
				foreach(var b in hash) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
				return sb.ToString();
			}
		}

		private static string Str(Dictionary<string, object> d, string k) => d.TryGetValue(k, out var v) ? v as string : null;
		private static bool Bool(Dictionary<string, object> d, string k) => d.TryGetValue(k, out var v) && v is bool b && b;
	}
}
