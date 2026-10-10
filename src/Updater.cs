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

        /// <summary>
        /// Downloads, verifies and swaps in the new exe, then starts it with the arguments
        /// beforeStart returns. beforeStart runs once the new copy is in place, just before it
        /// starts: hosting and the connection carry on through the whole download, and are let
        /// go only then.
        /// </summary>
        public static async Task InstallAsync(Release r, Func<string> beforeStart)
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
                // The running copy is swapped for the new one (Windows lets a running program be
                // renamed or replaced, not deleted). If an older leftover cannot be deleted (a
                // TailRemote still running from it, or a virus scan holding it), it is left alone
                // and this copy gets a new name: that used to stop the update with "Access to the
                // path ... .old is denied".
                if (!TryDelete(old)) old = exe + ".old-" + DateTime.Now.ToString("yyyyMMddHHmmss");
                // File.Replace (Win32 ReplaceFile) does the swap as one filesystem operation instead
                // of two separate File.Move calls, so a kill between steps can no longer leave no exe
                // at all behind. It deletes/overwrites the backup path itself if one already exists,
                // so it does not need "old" to be absent first.
                File.Replace(fresh, exe, old, ignoreMetadataErrors: true);
            }
            catch
            {
                // Leave nothing half-done behind: the running exe stays where it was.
                try { if (!File.Exists(exe) && File.Exists(old)) File.Move(old, exe); } catch { }
                try { File.Delete(fresh); } catch { }
                throw;
            }
            string args = beforeStart();
            try
            {
                Process.Start(new ProcessStartInfo(exe, args + " --after-update " + Environment.ProcessId) { UseShellExecute = false });
            }
            catch (Exception e)
            {
                // The swap already succeeded: the exe on disk is the new, valid version. Rolling it
                // back would only undo a working update for no benefit, so it is left as is; the
                // caller just needs a clear, user-facing explanation instead of a raw Process.Start
                // failure or a silent crash.
                throw new InvalidOperationException("Updated to version " + r.Version +
                    ", but it could not restart automatically (" + e.Message + "). Please start TailRemote yourself.", e);
            }
        }

        /// <summary>At startup after an update: wait for the old copy to exit, then delete every leftover it can.</summary>
        public static void FinishUpdate(int oldPid)
        {
            try { Process.GetProcessById(oldPid).WaitForExit(15000); } catch { }
            for (int i = 0; i < 20 && !CleanLeftovers(); i++) System.Threading.Thread.Sleep(250);
        }

        /// <summary>Deletes TailRemote's own leftovers from earlier updates (name.old, name.old-...). True if none are left.</summary>
        public static bool CleanLeftovers()
        {
            string exe = Environment.ProcessPath!;
            // A service update not yet proven: its old copy is what roll back would restore.
            if (File.Exists(exe + ".rollback")) return true;
            bool all = true;
            try
            {
                foreach (string f in Directory.GetFiles(Path.GetDirectoryName(exe)!, Path.GetFileName(exe) + ".old*"))
                    all &= TryDelete(f);
            }
            catch { }
            return all;
        }

        private static bool TryDelete(string path)
        {
            try
            {
                if (!File.Exists(path)) return true;
                File.SetAttributes(path, FileAttributes.Normal); // a read-only leftover too
                File.Delete(path);
                return true;
            }
            catch { return false; }
        }
    }
}
