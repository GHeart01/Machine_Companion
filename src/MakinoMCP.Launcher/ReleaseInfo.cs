#nullable disable
using System;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;

namespace MakinoMCP.Launcher
{
    // Reads GitHub's "latest release" answer and the release's .sha256 file. Only a full release whose tag is
    // v<major>.<minor>.<patch> and that carries both the zip and its .sha256 counts.
    // Also compiled into the tests, so keep it to plain System APIs.
    internal static class ReleaseInfo
    {
        private static readonly Regex TagPattern = new Regex(@"^v(\d+)\.(\d+)\.(\d+)$");
        private static readonly Regex ShaLine = new Regex(@"^([0-9A-Fa-f]{64})\s+\*?(\S+)\s*$");

        [DataContract]
        private sealed class ApiRelease
        {
            [DataMember(Name = "tag_name")] public string TagName { get; set; }
            [DataMember(Name = "draft")] public bool Draft { get; set; }
            [DataMember(Name = "prerelease")] public bool Prerelease { get; set; }
            [DataMember(Name = "assets")] public ApiAsset[] Assets { get; set; }
        }

        [DataContract]
        private sealed class ApiAsset
        {
            [DataMember(Name = "name")] public string Name { get; set; }
            [DataMember(Name = "browser_download_url")] public string Url { get; set; }
        }

        /// <summary>The release, or null when the answer isn't a usable full release.</summary>
        internal static Release Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            ApiRelease r;
            try
            {
                using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                    r = new DataContractJsonSerializer(typeof(ApiRelease)).ReadObject(ms) as ApiRelease;
            }
            catch
            {
                return null;
            }
            if (r == null || r.Draft || r.Prerelease || r.TagName == null) return null;
            var m = TagPattern.Match(r.TagName);
            if (!m.Success) return null;
            Version version;
            try { version = new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value)); }
            catch { return null; }

            string zipName = "MakinoMCP-" + version.ToString(3) + "-win-x64.zip";
            var assets = r.Assets ?? new ApiAsset[0];
            var zip = assets.FirstOrDefault(a => a != null && a.Name == zipName);
            var sha = assets.FirstOrDefault(a => a != null && a.Name == zipName + ".sha256");
            if (zip?.Url == null || sha?.Url == null) return null;
            return new Release { Tag = r.TagName, Version = version, ZipName = zipName, ZipUrl = zip.Url, ShaUrl = sha.Url };
        }

        /// <summary>True for https://github.com/&lt;repository&gt;/releases/download/... and nothing else.</summary>
        internal static bool IsAllowedDownload(string url, string repository)
        {
            if (url == null || repository == null) return false;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
            if (u.Scheme != Uri.UriSchemeHttps || !string.Equals(u.Host, "github.com", StringComparison.OrdinalIgnoreCase)) return false;
            if (!u.IsDefaultPort || url.Contains("..")) return false;
            return u.AbsolutePath.StartsWith("/" + repository + "/releases/download/", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The lowercase hash from a "&lt;hash&gt;  &lt;file name&gt;" line naming expectedFileName, or null.</summary>
        internal static string ParseSha256(string text, string expectedFileName)
        {
            if (text == null) return null;
            foreach (string raw in text.Split('\n'))
            {
                var m = ShaLine.Match(raw.Trim());
                if (m.Success && string.Equals(m.Groups[2].Value, expectedFileName, StringComparison.Ordinal))
                    return m.Groups[1].Value.ToLowerInvariant();
            }
            return null;
        }
    }

    internal sealed class Release
    {
        public string Tag { get; set; }
        public Version Version { get; set; }
        public string ZipName { get; set; }
        public string ZipUrl { get; set; }
        public string ShaUrl { get; set; }
    }
}
