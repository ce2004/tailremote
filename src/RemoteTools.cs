using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace TailRemote
{
    /// <summary>
    /// What a host answers for the controlling PC's Get files from the remote PC and Remote
    /// PC info. Only controllers get here (never listeners); whoever has the main password can
    /// type anything on this PC anyway, so this adds no reach, only convenience.
    /// </summary>
    internal static class RemoteTools
    {
        public const int MaxEntries = 5000;

        /// <summary>
        /// A folder's contents, one line per entry, tab-separated: kind (D folder, F file),
        /// full path, name to show, bytes, last-modified ticks (UTC). "" lists the usual
        /// folders and the drives. A problem is one line: E, then what went wrong.
        /// </summary>
        public static string ListFolder(string path)
        {
            var sb = new StringBuilder();
            void Line(char kind, string full, string name, long bytes = 0, long ticks = 0) =>
                sb.Append(kind).Append('\t').Append(full).Append('\t').Append(name).Append('\t').Append(bytes).Append('\t').Append(ticks).Append('\n');
            try
            {
                if (path.Length == 0)
                {
                    // The usual folders of whoever is signed in here (the service runs as the system, which has none worth showing).
                    if (!System.Security.Principal.WindowsIdentity.GetCurrent().IsSystem)
                    {
                        foreach (var (folder, name) in new[]
                        {
                            (Environment.SpecialFolder.Desktop, "Desktop"),
                            (Environment.SpecialFolder.MyDocuments, "Documents"),
                        })
                        {
                            string f = Environment.GetFolderPath(folder);
                            if (f.Length > 0 && Directory.Exists(f)) Line('D', f, name);
                        }
                        string downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                        if (Directory.Exists(downloads)) Line('D', downloads, "Downloads");
                    }
                    else if (Directory.Exists(@"C:\Users")) Line('D', @"C:\Users", "Users (everyone's folders)");
                    foreach (var d in DriveInfo.GetDrives())
                    {
                        string name = d.Name.TrimEnd('\\');
                        try
                        {
                            if (d.IsReady)
                                name += (d.VolumeLabel.Length > 0 ? " " + d.VolumeLabel : "") + ", " + FileChannel.Size(d.AvailableFreeSpace) + " free of " + FileChannel.Size(d.TotalSize);
                            else name += ", not ready";
                        }
                        catch { }
                        Line('D', d.Name, name);
                    }
                    return sb.ToString();
                }
                var dir = new DirectoryInfo(path);
                if (!dir.Exists) return "E\tThat folder is not there any more.\n";
                int n = 0;
                const FileAttributes hiddenSystem = FileAttributes.Hidden | FileAttributes.System;
                // At most MaxEntries + 1 of each are read before sorting: a folder of a million files must not be read whole.
                foreach (var d in dir.EnumerateDirectories().Where(d => (d.Attributes & hiddenSystem) != hiddenSystem).Take(MaxEntries + 1).OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase))
                {
                    if (++n > MaxEntries) break;
                    Line('D', d.FullName, d.Name, 0, d.LastWriteTimeUtc.Ticks);
                }
                foreach (var f in dir.EnumerateFiles().Where(f => (f.Attributes & hiddenSystem) != hiddenSystem).Take(MaxEntries + 1).OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase))
                {
                    if (++n > MaxEntries) break;
                    Line('F', f.FullName, f.Name, f.Length, f.LastWriteTimeUtc.Ticks);
                }
                return sb.ToString();
            }
            catch (UnauthorizedAccessException) { return "E\tTailRemote on the remote PC is not allowed into that folder.\n"; }
            catch (Exception e) { return "E\t" + e.Message.Replace('\n', ' ').Replace('\t', ' ') + "\n"; }
        }

        /// <summary>About this PC, one "Name: value" line each, for Remote PC info.</summary>
        public static string Info(string tailRemote)
        {
            var lines = new List<string> { "Name: " + Environment.MachineName };
            lines.Add("Windows: " + WindowsName());
            string user = SignedInUser();
            lines.Add("Signed in: " + (user.Length == 0 ? "nobody (the sign-in screen)" : user));
            lines.Add("On for: " + MainForm.Duration(TimeSpan.FromMilliseconds(Environment.TickCount64)));
            lines.Add("Processor busy: " + CpuPercent() + " percent, " + Environment.ProcessorCount + " cores");
            var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (GlobalMemoryStatusEx(ref mem))
                lines.Add("Memory: " + mem.dwMemoryLoad + " percent used, " + FileChannel.Size((long)mem.ullAvailPhys) + " free of " + FileChannel.Size((long)mem.ullTotalPhys));
            if (GetSystemPowerStatus(out var power))
            {
                if (WlanStreaming.State is string wifi) lines.Add(wifi);
                if (power.BatteryFlag == 255) lines.Add("Battery: unknown"); // Windows cannot tell
                else if ((power.BatteryFlag & 128) != 0) lines.Add("Battery: none (plugged in)");
                else lines.Add("Battery: " + (power.BatteryLifePercent <= 100 ? power.BatteryLifePercent + " percent" : "unknown") +
                    (power.ACLineStatus == 1 ? ", plugged in" + ((power.BatteryFlag & 8) != 0 ? " and charging" : "") : ", on battery") +
                    (power.ACLineStatus != 1 && power.BatteryLifeTime != uint.MaxValue ? ", about " + MainForm.Duration(TimeSpan.FromSeconds(power.BatteryLifeTime)) + " left" : ""));
            }
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (d.DriveType == DriveType.Fixed && d.IsReady)
                        lines.Add("Drive " + d.Name.TrimEnd('\\') + ": " + FileChannel.Size(d.AvailableFreeSpace) + " free of " + FileChannel.Size(d.TotalSize));
                }
                catch { }
            }
            lines.Add("TailRemote: " + tailRemote);
            return string.Join("\n", lines);
        }

        private static string WindowsName()
        {
            try
            {
                using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
                int build = Environment.OSVersion.Version.Build;
                // ProductName still says Windows 10 on Windows 11, so the build decides.
                string product = (k?.GetValue("ProductName") as string ?? "Windows").Replace("Windows 10", build >= 22000 ? "Windows 11" : "Windows 10");
                string release = k?.GetValue("DisplayVersion") as string ?? "";
                return product + (release.Length > 0 ? " " + release : "") + ", build " + build + ", " + RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();
            }
            catch { return RuntimeInformation.OSDescription; }
        }

        /// <summary>Who is signed in at the screen (the service runs as the system, so it asks Windows).</summary>
        private static string SignedInUser()
        {
            try
            {
                uint session = NativeService.WTSGetActiveConsoleSessionId();
                if (session == 0xFFFFFFFF) return "";
                if (!WTSQuerySessionInformationW(IntPtr.Zero, session, 5 /* WTSUserName */, out IntPtr buf, out _)) return "";
                try { return Marshal.PtrToStringUni(buf) ?? ""; }
                finally { WTSFreeMemory(buf); }
            }
            catch { return ""; }
        }

        /// <summary>How busy the processor is, measured over half a second.</summary>
        private static int CpuPercent()
        {
            if (!GetSystemTimes(out long idle1, out long kernel1, out long user1)) return 0;
            System.Threading.Thread.Sleep(500);
            if (!GetSystemTimes(out long idle2, out long kernel2, out long user2)) return 0;
            long total = (kernel2 - kernel1) + (user2 - user1); // kernel time includes idle
            return total <= 0 ? 0 : (int)Math.Clamp(100 - (idle2 - idle1) * 100 / total, 0, 100);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength, dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SYSTEM_POWER_STATUS
        {
            public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
            public uint BatteryLifeTime, BatteryFullLifeTime;
        }

        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);
        [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS s);
        [DllImport("kernel32.dll")] private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
        [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode)] private static extern bool WTSQuerySessionInformationW(IntPtr server, uint session, int infoClass, out IntPtr buffer, out uint bytes);
        [DllImport("wtsapi32.dll")] private static extern void WTSFreeMemory(IntPtr memory);
    }
}
