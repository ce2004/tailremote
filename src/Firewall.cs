using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace TailRemote
{
    /// <summary>
    /// Opens and closes ports in Windows Firewall for TailRemote. Every rule it
    /// makes is named "TailRemote port N" (one for TCP, one for UDP), and it only
    /// ever touches rules with that name, so nothing another program set up is
    /// changed. The ports are remembered in the settings, so a port used once
    /// can still be closed after the port setting has moved on.
    /// </summary>
    internal static class Firewall
    {
        public static string RuleName(int port) => "TailRemote port " + port;

        /// <summary>Whether TailRemote's rule for this port exists. Needs no administrator rights.</summary>
        public static bool IsOpen(int port) => Run("netsh", "advfirewall firewall show rule name=\"" + RuleName(port) + "\"") == 0;

        /// <summary>Opens or closes a port through an elevated copy of TailRemote. False if refused or failed.</summary>
        public static Task<bool> SetAsync(int port, bool open) => Task.Run(() =>
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--firewall " + (open ? "open " : "close ") + port)
                { UseShellExecute = true, Verb = "runas" });
                p?.WaitForExit();
            }
            catch { return false; } // the administrator prompt was declined
            return IsOpen(port) == open;
        });

        /// <summary>The elevated half.</summary>
        public static int Apply(bool open, int port)
        {
            if (Protocol.PortProblem(port.ToString(), out _) != null) return 1;
            string name = RuleName(port);
            Run("netsh", "advfirewall firewall delete rule name=\"" + name + "\"");
            if (!open) return 0;
            int tcp = Run("netsh", "advfirewall firewall add rule name=\"" + name + "\" dir=in action=allow enable=yes profile=any protocol=TCP localport=" + port);
            int udp = Run("netsh", "advfirewall firewall add rule name=\"" + name + "\" dir=in action=allow enable=yes profile=any protocol=UDP localport=" + port);
            return tcp == 0 && udp == 0 ? 0 : 1;
        }

        private static int Run(string file, string args) => Native.RunHidden(file, args);
    }
}
