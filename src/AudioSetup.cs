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
            if (d == null || Wasapi.ReadString(d, PkeyDevice, 2) != DeviceName || ExtraOutputs().Count > 0) return false;
            try
            {
                Wasapi.Enumerator().GetDefaultAudioEndpoint(Wasapi.eRender, Wasapi.eConsole, out var def);
                def.GetId(out string defId);
                d.GetId(out string id);
                return id == defId;
            }
            catch { return false; }
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
                // Program Files: only administrators can change files there between
                // the signature check and running the installer.
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "TailRemote-vbcable");
                byte[] zip = await DownloadAsync(report, ct);
                try
                {
                    report("Checking the download.", 100);
                    if (Directory.Exists(dir)) Directory.Delete(dir, true);
                    using (var ms = new MemoryStream(zip)) ZipFile.ExtractToDirectory(ms, dir);
                    string setup = Path.Combine(dir, "VBCABLE_Setup_x64.exe"); // VB-Audio's installer on ARM64 too
                    if (!File.Exists(setup) || !SignedByVbAudio(setup))
                        throw new InvalidOperationException("The VB-Cable download did not check out as genuine, so it was not run. Try again later.");

                    report("VB-Cable's installer is open. Press Install Driver, then wait for it to finish.", -1);
                    using var p = Process.Start(new ProcessStartInfo(setup) { UseShellExecute = true, WorkingDirectory = dir })
                        ?? throw new InvalidOperationException("VB-Cable's installer did not start.");
                    await p.WaitForExitAsync(ct);
                }
                finally
                {
                    // The installer has copied what it needs into Windows; the download goes.
                    try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
                }

                report("Waiting for Windows to add the device.", -1);
                for (int i = 0; i < 60 && FindCable() == null; i++) await Task.Delay(500, ct);
                if (FindCable() == null)
                {
                    if (DriverInstalled())
                        throw new InvalidOperationException("VB-Cable is installed, but Windows needs a restart to finish. Restart, then press Set up audio device again to finish.");
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
            DisableExtraOutputs();
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

        // ---- Removal: default elsewhere, then force VB-Cable off the PC ----

        /// <summary>Whether any VB-Cable device is installed, working or not.</summary>
        public static bool Installed() => DriverInstalled() || FindCable() != null;

        /// <summary>The output that will become the default when VB-Cable goes, or null if there is none.</summary>
        public static (string Id, string Name)? OtherOutput()
        {
            foreach (var d in Wasapi.OutputDevices())
            {
                try
                {
                    Wasapi.Enumerator().GetDevice(d.Id, out var dev);
                    if (Wasapi.ReadString(dev, PkeyInterface, 2) != "VB-Audio Virtual Cable") return d;
                }
                catch { }
            }
            return null;
        }

        /// <summary>Starts the elevated removal window and waits for it. False if refused or it failed.</summary>
        public static async Task<bool> RemoveElevatedAsync()
        {
            try
            {
                var p = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--remove-audio") { UseShellExecute = true, Verb = "runas" });
                if (p == null) return false;
                await p.WaitForExitAsync();
                return p.ExitCode == 0;
            }
            catch { return false; }
        }

        /// <summary>The whole removal. Returns the sentence to finish with.</summary>
        public static async Task<string> RemoveAsync(Action<string, int> report, CancellationToken ct)
        {
            report("Making another output the default.", 10);
            var other = OtherOutput();
            if (other != null) SetDefault(other.Value.Id);

            // Programs following the default (TailRemote's own capture among
            // them) move off the cable within a second; the rest are cut off.
            report("Letting programs move off it.", 25);
            await Task.Delay(1500, ct);

            report("Removing VB-Cable's devices.", 45);
            bool reboot = false;
            var infs = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            RemoveDevices(infs, ref reboot);

            report("Removing VB-Cable's driver.", 75);
            foreach (string inf in infs) SetupUninstallOEMInfW(inf, 1 /* SUOI_FORCEDELETE */, IntPtr.Zero);
            RunHidden("sc.exe", "delete VBAudioVACMME");
            string drivers = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers");
            foreach (string sys in new[] { "vbaudio_cable64_win10.sys", "vbaudio_cable64arm_win10.sys" })
            {
                string f = Path.Combine(drivers, sys);
                if (!File.Exists(f)) continue;
                try { File.Delete(f); }
                catch { MoveFileExW(f, null, 4 /* MOVEFILE_DELAY_UNTIL_REBOOT */); reboot = true; }
            }

            report("Checking.", 95);
            for (int i = 0; i < 10 && FindCable() != null; i++) await Task.Delay(300, ct);
            if (FindCable() != null) reboot = true;
            string now = other != null ? " The default output is now " + other.Value.Name + "." : " This PC has no other sound output now.";
            return reboot
                ? "VB-Cable is removed. Restart Windows to finish." + now
                : "VB-Cable is removed." + now;
        }

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, IntPtr enumerator, IntPtr hwnd, uint flags);
        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref SP_DEVINFO_DATA data);
        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiGetDeviceRegistryPropertyW(IntPtr set, ref SP_DEVINFO_DATA data, uint property, out uint regType, byte[]? buffer, uint size, out uint required);
        [DllImport("setupapi.dll")]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
        [DllImport("newdev.dll", SetLastError = true)]
        private static extern bool DiUninstallDevice(IntPtr hwnd, IntPtr set, ref SP_DEVINFO_DATA data, uint flags, out bool reboot);
        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool SetupUninstallOEMInfW(string inf, uint flags, IntPtr reserved);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool MoveFileExW(string from, string? to, uint flags);

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVINFO_DATA { public uint cbSize; public Guid ClassGuid; public uint DevInst; public IntPtr Reserved; }

        /// <summary>Uninstalls every VB-Cable device, present or not, noting their driver packages.</summary>
        private static void RemoveDevices(System.Collections.Generic.HashSet<string> infs, ref bool reboot)
        {
            var media = new Guid("4d36e96c-e325-11ce-bfc1-08002be10318");
            IntPtr set = SetupDiGetClassDevsW(ref media, IntPtr.Zero, IntPtr.Zero, 0); // 0: not only present ones
            if (set == new IntPtr(-1)) return;
            try
            {
                var data = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
                for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref data); i++)
                {
                    if (!(Property(set, ref data, 1 /* SPDRP_HARDWAREID */) ?? "").Contains("VBAudioVACWDM", StringComparison.OrdinalIgnoreCase)) continue;
                    if (Property(set, ref data, 9 /* SPDRP_DRIVER */) is string key)
                    {
                        using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\" + key);
                        if (k?.GetValue("InfPath") is string inf && inf.StartsWith("oem", StringComparison.OrdinalIgnoreCase)) infs.Add(inf);
                    }
                    if (DiUninstallDevice(IntPtr.Zero, set, ref data, 0, out bool r) && r) reboot = true;
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
        }

        private static string? Property(IntPtr set, ref SP_DEVINFO_DATA data, uint prop)
        {
            SetupDiGetDeviceRegistryPropertyW(set, ref data, prop, out _, null, 0, out uint need);
            if (need == 0) return null;
            byte[] buf = new byte[need];
            if (!SetupDiGetDeviceRegistryPropertyW(set, ref data, prop, out _, buf, need, out _)) return null;
            return System.Text.Encoding.Unicode.GetString(buf).Replace('\0', ' ').Trim();
        }

        private static void RunHidden(string file, string args)
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo(file, args) { CreateNoWindow = true, UseShellExecute = false });
                p?.WaitForExit(10000);
            }
            catch { }
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
            [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string id, int visible);
        }

        /// <summary>
        /// VB-Cable's driver always adds a second output, "CABLE In 16 Ch". Only
        /// the first cable is wanted, so the extra one is disabled, the same as
        /// Disable in the Sound settings.
        /// </summary>
        private static void DisableExtraOutputs()
        {
            var pc = (IPolicyConfig)new PolicyConfigCo();
            foreach (string id in ExtraOutputs()) pc.SetEndpointVisibility(id, 0);
        }

        private static System.Collections.Generic.List<string> ExtraOutputs()
        {
            var ids = new System.Collections.Generic.List<string>();
            try
            {
                Wasapi.Enumerator().EnumAudioEndpoints(Wasapi.eRender, Wasapi.DEVICE_STATE_ACTIVE, out var coll);
                coll.GetCount(out uint n);
                for (uint i = 0; i < n; i++)
                {
                    coll.Item(i, out var dev);
                    if (Wasapi.ReadString(dev, PkeyInterface, 2) != "VB-Audio Virtual Cable") continue;
                    if (Wasapi.ReadString(dev, PkeyDevice, 2) is "TailRemote" or "CABLE Input") continue;
                    dev.GetId(out string id);
                    ids.Add(id);
                }
            }
            catch { }
            return ids;
        }

        public static void SetDefault(string id)
        {
            var pc = (IPolicyConfig)new PolicyConfigCo();
            for (int role = 0; role < 3; role++) pc.SetDefaultEndpoint(id, role);
        }
    }
}
