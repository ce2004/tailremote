using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;

namespace TailRemote
{
    /// <summary>
    /// Updates from the latest GitHub release: downloads this architecture's
    /// exe, checks its SHA-256 against the one GitHub publishes, swaps it in
    /// place (a running exe can be renamed, not overwritten) and restarts.
    /// </summary>
    internal static class Updater
    {
        private const string Repo = "ce2004/tailremote";

        public sealed record Release(Version Version, string Notes, string Url, string? Sha256);

        public static Version Current
        {
            get
            {
                var v = typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0, 0);
                return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
            }
        }

        private static string AssetName =>
            "TailRemote-" + (RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64") + ".exe";

        private static HttpClient Http()
        {
            var h = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            h.DefaultRequestHeaders.UserAgent.ParseAdd("TailRemote/" + Current);
            return h;
        }

        /// <summary>The latest release, or null when this copy is already up to date.</summary>
        public static async Task<Release?> CheckAsync()
        {
            using var h = Http();
            using var doc = JsonDocument.Parse(await h.GetStringAsync("https://api.github.com/repos/" + Repo + "/releases/latest"));
            var root = doc.RootElement;
            var version = Version.Parse(root.GetProperty("tag_name").GetString()!.TrimStart('v'));
            if (version <= Current) return null;
            foreach (var a in root.GetProperty("assets").EnumerateArray())
            {
                if (a.GetProperty("name").GetString() != AssetName) continue;
                string? sha = a.TryGetProperty("digest", out var d) && d.GetString() is string ds && ds.StartsWith("sha256:") ? ds[7..] : null;
                return new Release(version, root.GetProperty("body").GetString() ?? "", a.GetProperty("browser_download_url").GetString()!, sha);
            }
            throw new InvalidOperationException("Version " + version + " has no " + AssetName + ".");
        }

        /// <summary>Downloads, verifies and swaps in the new exe, then starts it with the given arguments.</summary>
        public static async Task InstallAsync(Release r, string args)
        {
            string exe = Environment.ProcessPath!;
            string fresh = exe + ".new", old = exe + ".old";
            try
            {
                using (var h = Http())
                {
                    byte[] data = await h.GetByteArrayAsync(r.Url);
                    // No checksum from GitHub means nothing to check against: refuse rather than trust it.
                    if (r.Sha256 == null) throw new InvalidOperationException("GitHub gave no checksum for the download, so it was not installed. Try again later.");
                    if (!Convert.ToHexString(SHA256.HashData(data)).Equals(r.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("The download was damaged. Nothing was changed.");
                    await File.WriteAllBytesAsync(fresh, data);
                }
                if (File.Exists(old)) File.Delete(old);
                File.Move(exe, old);
                File.Move(fresh, exe);
            }
            catch
            {
                // Leave nothing half-done behind: the running exe stays where it was.
                try { if (!File.Exists(exe) && File.Exists(old)) File.Move(old, exe); } catch { }
                try { File.Delete(fresh); } catch { }
                throw;
            }
            Process.Start(new ProcessStartInfo(exe, args + " --after-update " + Environment.ProcessId) { UseShellExecute = false });
        }

        /// <summary>At startup after an update: wait for the old copy to exit, then delete it.</summary>
        public static void FinishUpdate(int oldPid)
        {
            try { Process.GetProcessById(oldPid).WaitForExit(15000); } catch { }
            string old = Environment.ProcessPath! + ".old";
            for (int i = 0; i < 20 && File.Exists(old); i++)
            {
                try { File.Delete(old); } catch { System.Threading.Thread.Sleep(250); }
            }
        }
    }
}
