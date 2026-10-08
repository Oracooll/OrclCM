// Update check against the GitHub releases of Oracooll/OrclCM, and in-place self-update:
// a running exe can be renamed on Windows, so the new exe takes its name and is started
// with /restart (it waits for this instance's single-instance mutex to be released).
using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace OrclCM
{
    sealed class ReleaseInfo
    {
        public string Version;      // "1.6.000"
        public string PageUrl;      // release page
        public string DownloadUrl;  // OrclCM.exe asset, or null
        public string Sha256;       // from the release notes, or null
    }

    static class Updater
    {
        public const string LatestApi = "https://api.github.com/repos/Oracooll/OrclCM/releases/latest";
        public const string RestartArgument = "/restart";
        public const string StartedEventName = @"Local\OrclCM.UpdateStarted";
        public const string DownloadPrefix = "https://github.com/Oracooll/OrclCM/releases/download/";
        const string AssetName = "OrclCM.exe";

        /// <summary>Compares "1.X.XXX" versions; null or malformed counts as oldest.</summary>
        public static int Compare(string a, string b)
        {
            int[] pa = Parts(a), pb = Parts(b);
            for (int i = 0; i < 3; i++)
                if (pa[i] != pb[i]) return pa[i].CompareTo(pb[i]);
            return 0;
        }

        static int[] Parts(string v)
        {
            var m = Regex.Match(v ?? "", @"^v?(\d+)\.(\d+)\.(\d+)$");
            return m.Success ? new[] { int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value) } : new[] { -1, -1, -1 };
        }

        public static bool IsNewer(ReleaseInfo r) => r != null && Compare(r.Version, AppInfo.Version) > 0;

        /// <summary>Self-install only with a download link from this project and a published checksum;
        /// otherwise the release page is opened instead.</summary>
        public static bool CanInstall(ReleaseInfo r) => r?.DownloadUrl != null && r.Sha256 != null;

        /// <summary>Reads the fields we need from the GitHub "latest release" JSON.</summary>
        public static ReleaseInfo Parse(string json)
        {
            string Field(string name) => Unescape(Regex.Match(json, "\"" + name + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"").Groups[1].Value);
            string tag = Field("tag_name");
            if (string.IsNullOrEmpty(tag)) throw new InvalidDataException("No release found");
            string download = null;
            foreach (Match m in Regex.Matches(json, "\"browser_download_url\"\\s*:\\s*\"([^\"]+)\""))
                if (m.Groups[1].Value.EndsWith("/" + AssetName, StringComparison.OrdinalIgnoreCase)
                    && m.Groups[1].Value.StartsWith(DownloadPrefix, StringComparison.OrdinalIgnoreCase))  // only this project's releases
                    download = m.Groups[1].Value;
            var sha = Regex.Match(Field("body"), @"SHA-256[^:]*:\s*([0-9a-fA-F]{64})");
            return new ReleaseInfo
            {
                Version = tag.TrimStart('v', 'V'), PageUrl = Field("html_url"), DownloadUrl = download,
                Sha256 = sha.Success ? sha.Groups[1].Value.ToLowerInvariant() : null,
            };
        }

        static string Unescape(string s) =>
            Regex.Replace(s.Replace("\\\"", "\"").Replace("\\/", "/").Replace("\\n", "\n").Replace("\\r", "\r").Replace("\\\\", "\\"),
                          @"\\u([0-9a-fA-F]{4})", m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());

        static HttpWebRequest Request(string url, int timeoutMs, string accept)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;  // older defaults lack TLS 1.2
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = AppInfo.Name + "/" + AppInfo.Version;
            req.Accept = accept;
            req.Timeout = req.ReadWriteTimeout = timeoutMs;
            return req;
        }

        /// <summary>Blocking; call from a background thread.</summary>
        public static ReleaseInfo FetchLatest()
        {
            using (var resp = (HttpWebResponse)Request(LatestApi, 15000, "application/vnd.github+json").GetResponse())
            using (var reader = new StreamReader(resp.GetResponseStream()))
                return Parse(reader.ReadToEnd());
        }

        /// <summary>Downloads and verifies the new exe next to the current one. Blocking.</summary>
        public static string Download(ReleaseInfo r, string exePath)
        {
            if (!CanInstall(r)) throw new InvalidDataException(L.T("The download does not match the published checksum."));
            string target = exePath + ".new";
            using (var resp = Request(r.DownloadUrl, 120000, "application/octet-stream").GetResponse())
            using (var src = resp.GetResponseStream())
            using (var dst = File.Create(target))
                src.CopyTo(dst);
            try
            {
                Verify(target, r.Sha256);
            }
            catch
            {
                TryDelete(target);
                throw;
            }
            return target;
        }

        public static void Verify(string path, string expectedSha256)
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length < 1024 || bytes[0] != 'M' || bytes[1] != 'Z')
                throw new InvalidDataException(L.T("The download is not a valid program."));
            if (expectedSha256 == null)
                throw new InvalidDataException(L.T("The download does not match the published checksum."));
            {
                using (var sha = SHA256.Create())
                {
                    string actual = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
                    if (actual != expectedSha256)
                        throw new InvalidDataException(L.T("The download does not match the published checksum."));
                }
            }
        }

        /// <summary>Puts the new exe in place of the running one, starts it and waits until it
        /// reports that it is running. On any failure the current version is put back and the
        /// exception is rethrown, so OrclCM stays installed and working.</summary>
        public static void InstallAndRestart(string newExe, string exePath, int waitMs = 20000)
        {
            string old = exePath + ".old";
            TryDelete(old);
            File.Move(exePath, old);  // allowed while running
            bool swapped = false;
            try
            {
                File.Move(newExe, exePath);
                swapped = true;
                using (var started = new System.Threading.EventWaitHandle(false, System.Threading.EventResetMode.ManualReset, StartedEventName))
                {
                    started.Reset();
                    // the new copy waits for this process (by id) to exit before taking over
                    string args = RestartArgument + " " + Process.GetCurrentProcess().Id;
                    using (var proc = Process.Start(new ProcessStartInfo(exePath, args) { UseShellExecute = false }))
                    {
                        if (!started.WaitOne(waitMs))
                        {
                            try { if (!proc.HasExited) proc.Kill(); } catch (Exception) { }
                            throw new InvalidOperationException(L.T("The new version did not start."));
                        }
                    }
                }
            }
            catch
            {
                Restore(exePath, old, swapped ? newExe : null);
                throw;
            }
        }

        static void Restore(string exePath, string old, string failedTarget)
        {
            for (int attempt = 0; attempt < 10; attempt++)
            {
                try
                {
                    if (failedTarget != null && File.Exists(exePath)) { TryDelete(failedTarget); File.Move(exePath, failedTarget); }
                    if (!File.Exists(exePath)) File.Copy(old, exePath);  // copy, not move: works even if .old is briefly locked
                    TryDelete(old);
                    return;
                }
                catch (Exception) { System.Threading.Thread.Sleep(300); }
            }
        }

        /// <summary>Started by an update: tell the previous version we are running.</summary>
        public static void SignalStarted()
        {
            try
            {
                using (var started = System.Threading.EventWaitHandle.OpenExisting(StartedEventName)) started.Set();
            }
            catch (Exception) { }
        }

        /// <summary>Remove what an update left behind (only on a normal start, so the previous
        /// version stays available until the new one has proven itself).</summary>
        public static void CleanUp(string exePath)
        {
            TryDelete(exePath + ".old");
            TryDelete(exePath + ".new");
        }

        static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch (Exception) { }
        }
    }
}
