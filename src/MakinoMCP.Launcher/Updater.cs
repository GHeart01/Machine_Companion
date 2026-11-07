#nullable disable
using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace MakinoMCP.Launcher
{
    // The background update of a release-zip install: at most every 6 hours, asks GitHub for the latest release, and
    // when it is newer than the installed build downloads the zip, checks its SHA-256 against the release's .sha256
    // and hands it to UpdateApplier. Runs in its own detached process ("MakinoMCP.exe --update"), so nothing here
    // reaches the MCP client: outcomes go to %LOCALAPPDATA%\MakinoMCP\update-state.json and logs\update-<date>.log.
    // Also compiled into the tests, so keep it to plain System APIs.
    internal sealed class Updater
    {
        internal static readonly TimeSpan Interval = TimeSpan.FromHours(6);
        internal const long MaxZipBytes = 300L * 1024 * 1024;
        internal const int MaxShaBytes = 4096;
        internal const string StateFileName = "update-state.json";

        private readonly string _installDir;
        private readonly string _stateDir;
        private readonly IReleaseSource _source;
        private readonly Func<DateTime> _utcNow;
        private readonly Func<string, bool> _isSigned;
        private readonly Func<string, bool> _inUse;

        internal Updater(string installDir, string stateDir, IReleaseSource source, Func<DateTime> utcNow,
            Func<string, bool> isSigned, Func<string, bool> inUse)
        {
            _installDir = installDir;
            _stateDir = stateDir;
            _source = source;
            _utcNow = utcNow;
            _isSigned = isSigned;
            _inUse = inUse;
        }

        private string StatePath => Path.Combine(_stateDir, StateFileName);

        /// <summary>True when the last attempt is 6 hours old, or there is none (or it is in the future: the clock moved).</summary>
        internal bool Due()
        {
            var s = ReadState(StatePath);
            if (s?.LastAttemptUtc == null) return true;
            var age = _utcNow() - s.LastAttemptUtc.Value;
            return age >= Interval || age < TimeSpan.Zero;
        }

        /// <summary>One install updates at a time, across users and sessions on this PC.</summary>
        internal static string MutexName(string installDir)
        {
            using (var sha = SHA256.Create())
            {
                byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(installDir).TrimEnd('\\').ToLowerInvariant()));
                var b = new StringBuilder(@"Global\MakinoMCP-update-");
                for (int i = 0; i < 8; i++) b.Append(h[i].ToString("x2"));
                return b.ToString();
            }
        }

        internal UpdateState Run()
        {
            Mutex mutex;
            try { mutex = new Mutex(false, MutexName(_installDir)); }
            catch (Exception ex)
            {
                // Access denied: another Windows user's update holds it. Anything else: this PC forbids the object.
                Log("busy: the update lock can't be opened (" + ex.GetType().Name + ")");
                return new UpdateState { Result = "busy" };
            }
            using (mutex)
            {
                bool mine;
                try { mine = mutex.WaitOne(0); }
                catch (AbandonedMutexException) { mine = true; }
                if (!mine)
                {
                    Log("busy: another update of this folder is running");
                    return new UpdateState { Result = "busy" };
                }
                try { return RunLocked(); }
                finally { mutex.ReleaseMutex(); }
            }
        }

        private UpdateState RunLocked()
        {
            DateTime started = _utcNow();
            // Recorded first, so a check that hangs or fails still waits 6 hours before the next one.
            Save(new UpdateState { LastAttemptUtc = started, Result = "checking" });
            string zipPath = null;
            try
            {
                string repository = UpdateSource.ReadRepository(_installDir);
                if (repository == null) return Finish(started, "failed", null, "no valid " + UpdateSource.MarkerFileName);
                string build = UpdateApplier.ReadCurrent(_installDir);
                Version installed = UpdateSource.InstalledVersion(build);
                if (installed == null) return Finish(started, "failed", null, "current.txt doesn't name a release build");

                Release release = ReleaseInfo.Parse(_source.LatestJson(repository));
                if (release == null) return Finish(started, "failed", null, "GitHub's latest release isn't a usable MakinoMCP release");
                if (release.Version <= installed)
                    return Finish(started, "up-to-date", installed.ToString(3), "latest release " + release.Tag);
                if (!ReleaseInfo.IsAllowedDownload(release.ZipUrl, repository) || !ReleaseInfo.IsAllowedDownload(release.ShaUrl, repository))
                    return Finish(started, "failed", null, "the release's files aren't on github.com/" + repository);

                Log("release " + release.Tag + " is newer than " + build);
                string expected = ReleaseInfo.ParseSha256(_source.DownloadText(release.ShaUrl, MaxShaBytes), release.ZipName);
                if (expected == null) return Finish(started, "failed", null, "the release's .sha256 file couldn't be read");

                string versions = Path.Combine(_installDir, "versions");
                Directory.CreateDirectory(versions);
                zipPath = Path.Combine(versions, ".download-" + Guid.NewGuid().ToString("N") + ".zip");
                _source.Download(release.ZipUrl, zipPath, MaxZipBytes);
                if (!string.Equals(Sha256(zipPath), expected, StringComparison.Ordinal))
                    return Finish(started, "failed", null, "the download doesn't match the release's SHA-256");

                var applied = UpdateApplier.Apply(_installDir, zipPath, release.Version, _isSigned, _inUse, Log);
                return applied.Applied
                    ? Finish(started, "updated", release.Version.ToString(3), "installed " + applied.Build)
                    : Finish(started, "failed", null, applied.Reason);
            }
            catch (Exception ex)
            {
                return Finish(started, "failed", null, ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                if (zipPath != null) { try { File.Delete(zipPath); } catch { } }
            }
        }

        private UpdateState Finish(DateTime started, string result, string version, string message)
        {
            var s = new UpdateState { LastAttemptUtc = started, Result = result, Version = version, Message = message };
            Log(result + (message == null ? "" : ": " + message));
            Save(s);
            return s;
        }

        private static string Sha256(string path)
        {
            using (var sha = SHA256.Create())
            using (var s = File.OpenRead(path))
            {
                var b = new StringBuilder(64);
                foreach (byte x in sha.ComputeHash(s)) b.Append(x.ToString("x2"));
                return b.ToString();
            }
        }

        private void Log(string line) => LogLine(_stateDir, _installDir, _utcNow(), line);

        /// <summary>One line in stateDir\logs\update-&lt;date&gt;.log. Never throws.</summary>
        internal static void LogLine(string stateDir, string installDir, DateTime utcNow, string line)
        {
            try
            {
                string dir = Path.Combine(stateDir, "logs");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "update-" + utcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " [" + installDir + "] " + line + Environment.NewLine);
            }
            catch { }
        }

        private void Save(UpdateState s)
        {
            try
            {
                Directory.CreateDirectory(_stateDir);
                string tmp = StatePath + ".tmp-" + Guid.NewGuid().ToString("N");
                using (var f = File.Create(tmp)) new DataContractJsonSerializer(typeof(UpdateState)).WriteObject(f, s);
                try
                {
                    UpdateApplier.Retry(() =>
                    {
                        if (File.Exists(StatePath)) File.Replace(tmp, StatePath, null);
                        else File.Move(tmp, StatePath);
                    });
                }
                finally
                {
                    try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                }
            }
            catch (Exception ex)
            {
                Log("couldn't save " + StateFileName + ": " + ex.Message);
            }
        }

        internal static UpdateState ReadState(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                using (var f = File.OpenRead(path))
                    return new DataContractJsonSerializer(typeof(UpdateState)).ReadObject(f) as UpdateState;
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>What the last update check did. The setup window reads the same file.</summary>
    [DataContract]
    internal sealed class UpdateState
    {
        [DataMember(Name = "lastAttemptUtc", Order = 1, EmitDefaultValue = false)]
        private string LastAttempt { get; set; }

        /// <summary>checking, up-to-date, updated, failed (busy is never saved).</summary>
        [DataMember(Name = "result", Order = 2)] public string Result { get; set; }
        [DataMember(Name = "version", Order = 3, EmitDefaultValue = false)] public string Version { get; set; }
        [DataMember(Name = "message", Order = 4, EmitDefaultValue = false)] public string Message { get; set; }

        [IgnoreDataMember]
        public DateTime? LastAttemptUtc
        {
            get
            {
                return DateTime.TryParse(LastAttempt, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t) ? t : (DateTime?)null;
            }
            set { LastAttempt = value?.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture); }
        }
    }

    internal interface IReleaseSource
    {
        string LatestJson(string repository);
        void Download(string url, string path, long maxBytes);
        string DownloadText(string url, int maxBytes);
    }

    // GitHub over HTTPS: the system proxy (with the Windows login for a proxy that asks), redirects followed only to
    // https, sizes capped while streaming. Sends nothing but the request itself.
    internal sealed class GitHubReleaseSource : IReleaseSource, IDisposable
    {
        private const int MaxRedirects = 5;
        private readonly HttpClient _client;

        internal GitHubReleaseSource()
            : this(new HttpClientHandler
            {
                AllowAutoRedirect = false,
                UseProxy = true,
                DefaultProxyCredentials = CredentialCache.DefaultCredentials,
            }, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(15))
        {
        }

        private readonly TimeSpan _apiTimeout;
        private readonly TimeSpan _downloadTimeout;

        internal GitHubReleaseSource(HttpMessageHandler handler, TimeSpan apiTimeout, TimeSpan downloadTimeout)
        {
            _apiTimeout = apiTimeout;
            _downloadTimeout = downloadTimeout;
            _client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0";
            _client.DefaultRequestHeaders.UserAgent.ParseAdd("MakinoMCP-updater/" + version);
        }

        public void Dispose() => _client.Dispose();

        // Each call has one deadline for everything: the request, redirects and the whole body. When it passes, the
        // response is disposed, which also ends a read that the stream itself doesn't let be cancelled.
        public string LatestJson(string repository)
        {
            using (var cts = new CancellationTokenSource(_apiTimeout))
                return Encoding.UTF8.GetString(ReadAll("https://api.github.com/repos/" + repository + "/releases/latest",
                    "application/vnd.github+json", 1024 * 1024, null, cts.Token));
        }

        public string DownloadText(string url, int maxBytes)
        {
            using (var cts = new CancellationTokenSource(_apiTimeout))
                return Encoding.UTF8.GetString(ReadAll(url, "application/octet-stream", maxBytes, null, cts.Token));
        }

        public void Download(string url, string path, long maxBytes)
        {
            using (var cts = new CancellationTokenSource(_downloadTimeout))
            using (var to = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
                ReadAll(url, "application/octet-stream", maxBytes, to, cts.Token);
        }

        // Copies the body to `to`, or returns it when `to` is null; throws past maxBytes or the deadline.
        private byte[] ReadAll(string url, string accept, long maxBytes, Stream to, CancellationToken token)
        {
            using (var r = Get(url, accept, token))
            {
                string tooBig = "the answer is larger than " + maxBytes + " bytes";
                if (r.Content.Headers.ContentLength > maxBytes) throw new InvalidDataException(tooBig);
                var buffer = new MemoryStream();
                var target = to ?? buffer;
                using (var from = r.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
                using (token.Register(() => { try { r.Dispose(); from.Dispose(); } catch { } }))
                {
                    var chunk = new byte[81920];
                    long total = 0;
                    try
                    {
                        int n;
                        while ((n = from.ReadAsync(chunk, 0, chunk.Length, token).GetAwaiter().GetResult()) > 0)
                        {
                            total += n;
                            if (total > maxBytes) throw new InvalidDataException(tooBig);
                            target.Write(chunk, 0, n);
                        }
                    }
                    catch (Exception) when (token.IsCancellationRequested)
                    {
                        throw new TimeoutException("GitHub didn't finish answering in time");
                    }
                    if (token.IsCancellationRequested) throw new TimeoutException("GitHub didn't finish answering in time");
                }
                return to == null ? buffer.ToArray() : null;
            }
        }

        private HttpResponseMessage Get(string url, string accept, CancellationToken token)
        {
            var uri = new Uri(url);
            for (int i = 0; i <= MaxRedirects; i++)
            {
                if (uri.Scheme != Uri.UriSchemeHttps) throw new InvalidDataException("refusing a non-HTTPS address");
                var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.Accept.ParseAdd(accept);
                HttpResponseMessage response;
                try
                {
                    response = _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).GetAwaiter().GetResult();
                }
                catch (Exception) when (token.IsCancellationRequested)
                {
                    throw new TimeoutException("GitHub didn't answer in time");
                }
                int code = (int)response.StatusCode;
                if (code >= 300 && code < 400 && response.Headers.Location != null)
                {
                    var next = response.Headers.Location.IsAbsoluteUri ? response.Headers.Location : new Uri(uri, response.Headers.Location);
                    response.Dispose();
                    uri = next;
                    continue;
                }
                if (!response.IsSuccessStatusCode)
                {
                    string reason = response.ReasonPhrase;
                    response.Dispose();
                    throw new HttpRequestException("GitHub answered " + code + " " + reason);
                }
                return response;
            }
            throw new HttpRequestException("too many redirects");
        }
    }
}
