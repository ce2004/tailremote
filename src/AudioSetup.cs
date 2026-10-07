using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace TailRemote
{
    /// <summary>
    /// Gives a host with no sound card an output called TailRemote, made from
    /// VB-Cable (free, donationware, signed by Microsoft for x64 and ARM64).
    ///
    /// VB-Cable's licence does not allow building it into another program's
    /// installer, so TailRemote downloads the official package from vb-audio.com,
    /// checks it is really signed by VB-Audio, and opens VB-Audio's own installer
    /// for the one Install Driver press. Everything after that is configuration:
    /// the "CABLE Input" output is renamed TailRemote and made the default.
    ///
    /// All of it runs in an elevated copy of TailRemote (--setup-audio), so one
    /// administrator prompt covers the installer and the renaming.
    /// </summary>
    internal static class AudioSetup
    {
        public const string DeviceName = "TailRemote";

        // Newest first; a missing pack answers 404 at once and the next is tried.
        private static readonly string[] PackUrls =
        {
            "https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack47.zip",
            "https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack46.zip",
            "https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack45.zip",
        };

        private static readonly Guid PkeyDevice = new("a45c254e-df1c-4efd-8020-67d146a850e0");    // pid 2: the endpoint's own name
        private static readonly Guid PkeyInterface = new("026e516e-b814-414b-83cd-856d6fef4822"); // pid 2: the driver's name

        /// <summary>VB-Cable's output (renamed or not), if it is installed.</summary>
        public static Wasapi.IMMDevice? FindCable()
        {
            try
            {
                Wasapi.Enumerator().EnumAudioEndpoints(Wasapi.eRender, Wasapi.DEVICE_STATE_ACTIVE, out var coll);
                coll.GetCount(out uint n);
                Wasapi.IMMDevice? cable = null;
                for (uint i = 0; i < n; i++)
                {
                    coll.Item(i, out var dev);
                    if (Wasapi.ReadString(dev, PkeyInterface, 2) != "VB-Audio Virtual Cable") continue;
                    string? name = Wasapi.ReadString(dev, PkeyDevice, 2);
                    if (name == DeviceName) return dev;
                    if (name == "CABLE Input") cable ??= dev; // not "CABLE In 16 Ch"
                }
                return cable;
            }
            catch { return null; }
        }

        /// <summary>Set up: VB-Cable is named TailRemote and is the default output.</summary>
        public static bool IsReady()
        {
            var d = FindCable();
            if (d == null || Wasapi.ReadString(d, PkeyDevice, 2) != DeviceName) return false;
            try
            {
                Wasapi.Enumerator().GetDefaultAudioEndpoint(Wasapi.eRender, Wasapi.eConsole, out var def);
                def.GetId(out string defId);
                d.GetId(out string id);
                return id == defId;
            }
            catch { return false; }
        }

        /// <summary>
        /// For a host running as administrator: if VB-Cable is there but not yet
        /// named and default (say, it needed a restart), finish without asking.
        /// </summary>
        public static bool FinishQuietly()
        {
            if (IsReady() || FindCable() == null || !Startup.IsElevated()) return false;
            try { Configure(); return true; } catch { return false; }
        }

        /// <summary>Starts the elevated setup window and waits for it. False if refused or it failed.</summary>
        public static async Task<bool> RunElevatedAsync()
        {
            try
            {
                var p = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--setup-audio") { UseShellExecute = true, Verb = "runas" });
                if (p == null) return false;
                await p.WaitForExitAsync();
                return p.ExitCode == 0;
            }
            catch { return false; } // the administrator prompt was declined
        }

        /// <summary>The whole setup, reporting (text, percent or -1) as it goes.</summary>
        public static async Task RunAsync(Action<string, int> report, CancellationToken ct)
        {
            if (FindCable() == null)
            {
                string dir = Path.Combine(Path.GetTempPath(), "TailRemote-vbcable");
                byte[] zip = await DownloadAsync(report, ct);

                report("Checking the download.", 100);
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
                using (var ms = new MemoryStream(zip)) ZipFile.ExtractToDirectory(ms, dir);
                string setup = Path.Combine(dir, "VBCABLE_Setup_x64.exe"); // VB-Audio's installer on ARM64 too
                if (!File.Exists(setup) || !SignedByVbAudio(setup))
                    throw new InvalidOperationException("The VB-Cable download did not check out as genuine, so it was not run. Try again later.");

                report("VB-Cable's installer is open. Press Install Driver, then wait for it to finish.", -1);
                using (var p = Process.Start(new ProcessStartInfo(setup) { UseShellExecute = true, WorkingDirectory = dir })
                    ?? throw new InvalidOperationException("VB-Cable's installer did not start."))
                {
                    await p.WaitForExitAsync(ct);
                }

                report("Waiting for Windows to add the device.", -1);
                for (int i = 0; i < 60 && FindCable() == null; i++) await Task.Delay(500, ct);
                if (FindCable() == null)
                {
                    if (DriverInstalled())
                        throw new InvalidOperationException("VB-Cable is installed, but Windows needs a restart to finish. Restart, then start hosting: TailRemote finishes the setup by itself.");
                    throw new InvalidOperationException("VB-Cable was not installed. Press Set up audio device to try again, and press Install Driver in its window.");
                }
            }

            report("Naming the device TailRemote and making it the default output.", 100);
            Configure();
        }

        private static void Configure()
        {
            var dev = FindCable() ?? throw new InvalidOperationException("VB-Cable's output was not found.");
            Wasapi.WriteString(dev, PkeyDevice, 2, DeviceName);
            dev.GetId(out string id);
            SetDefault(id);
        }

        private static bool DriverInstalled() =>
            Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\VBAudioVACMME") != null;

        /// <summary>Downloads the newest pack, retrying through network trouble for up to 3 minutes.</summary>
        private static async Task<byte[]> DownloadAsync(Action<string, int> report, CancellationToken ct)
        {
            using var h = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            h.DefaultRequestHeaders.UserAgent.ParseAdd("TailRemote/" + Updater.Current);
            var giveUp = DateTime.UtcNow.AddMinutes(3);
            while (true)
            {
                report("Downloading VB-Cable from vb-audio.com.", 0);
                try
                {
                    foreach (string url in PackUrls)
                    {
                        using var resp = await h.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                        if (resp.StatusCode == HttpStatusCode.NotFound) continue;
                        resp.EnsureSuccessStatusCode();
                        long total = resp.Content.Headers.ContentLength ?? 0;
                        using var s = await resp.Content.ReadAsStreamAsync(ct);
                        var ms = new MemoryStream();
                        byte[] buf = new byte[65536];
                        int n;
                        while ((n = await s.ReadAsync(buf, ct)) > 0)
                        {
                            ms.Write(buf, 0, n);
                            if (total > 0) report("Downloading VB-Cable from vb-audio.com.", (int)(ms.Length * 100 / total));
                        }
                        return ms.ToArray();
                    }
                    throw new InvalidOperationException("vb-audio.com no longer has the VB-Cable download where TailRemote expects it. Check for a TailRemote update.");
                }
                catch (Exception e) when (e is HttpRequestException || e is IOException || (e is TaskCanceledException && !ct.IsCancellationRequested))
                {
                    if (DateTime.UtcNow > giveUp)
                        throw new InvalidOperationException("Could not reach vb-audio.com for 3 minutes. Check this PC's internet connection and try again.");
                    for (int left = 5; left > 0; left--)
                    {
                        report("Can't reach vb-audio.com. Trying again in " + left + " seconds.", -1);
                        await Task.Delay(1000, ct);
                    }
                }
            }
        }

        // ---- Is the installer really VB-Audio's? Windows' own signature check. ----

        [StructLayout(LayoutKind.Sequential)]
        private struct WINTRUST_FILE_INFO { public uint cbStruct; public IntPtr pcwszFilePath, hFile, pgKnownSubject; }

        [StructLayout(LayoutKind.Sequential)]
        private struct WINTRUST_DATA
        {
            public uint cbStruct;
            public IntPtr pPolicyCallbackData, pSIPClientData;
            public uint dwUIChoice, fdwRevocationChecks, dwUnionChoice;
            public IntPtr pFile;
            public uint dwStateAction;
            public IntPtr hWVTStateData, pwszURLReference;
            public uint dwProvFlags, dwUIContext;
            public IntPtr pSignatureSettings;
        }

        [DllImport("wintrust.dll")]
        private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WINTRUST_DATA data);

        internal static bool SignedByVbAudio(string path)
        {
            IntPtr pathPtr = Marshal.StringToCoTaskMemUni(path);
            var info = new WINTRUST_FILE_INFO { cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(), pcwszFilePath = pathPtr };
            IntPtr infoPtr = Marshal.AllocCoTaskMem(Marshal.SizeOf<WINTRUST_FILE_INFO>());
            try
            {
                Marshal.StructureToPtr(info, infoPtr, false);
                var data = new WINTRUST_DATA
                {
                    cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                    dwUIChoice = 2,    // no UI
                    dwUnionChoice = 1, // a file
                    pFile = infoPtr,
                };
                var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE"); // WINTRUST_ACTION_GENERIC_VERIFY_V2
                if (WinVerifyTrust(IntPtr.Zero, ref action, ref data) != 0) return false;
#pragma warning disable SYSLIB0057
                var cert = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
                return cert.Subject.Contains("BUREL VINCENT", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
            finally
            {
                Marshal.FreeCoTaskMem(infoPtr);
                Marshal.FreeCoTaskMem(pathPtr);
            }
        }

        // ---- Default device: IPolicyConfig, what the Sound control panel uses ----

        [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
        private class PolicyConfigCo { }

        [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPolicyConfig
        {
            void GetMixFormat(); void GetDeviceFormat(); void ResetDeviceFormat(); void SetDeviceFormat();
            void GetProcessingPeriod(); void SetProcessingPeriod(); void GetShareMode(); void SetShareMode();
            void GetPropertyValue(); void SetPropertyValue();
            [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
        }

        public static void SetDefault(string id)
        {
            var pc = (IPolicyConfig)new PolicyConfigCo();
            for (int role = 0; role < 3; role++) pc.SetDefaultEndpoint(id, role);
        }
    }
}
