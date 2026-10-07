using System;
using System.Diagnostics;
using System.Security.Principal;

namespace TailRemote
{
    /// <summary>
    /// Start hosting at sign-in through a scheduled task that runs with highest
    /// privileges (so keys reach administrator windows), plus a firewall rule.
    /// Changing either needs administrator rights, so the change is made by a
    /// second, elevated copy of TailRemote run with --startup on or off.
    /// </summary>
    internal static class Startup
    {
        private const string TaskName = "TailRemote Host";
        private const string RuleName = "TailRemote";

        public static bool IsElevated() => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

        public static bool IsEnabled() => Run("schtasks", "/Query /TN \"" + TaskName + "\"") == 0;

        public static bool FirewallRuleExists() => Run("netsh", "advfirewall firewall show rule name=\"" + RuleName + "\"") == 0;

        /// <summary>Asks for administrator rights and makes the change. False if refused or failed.</summary>
        public static bool Set(bool on)
        {
            try
            {
                var p = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--startup " + (on ? "on" : "off"))
                {
                    UseShellExecute = true,
                    Verb = "runas",
                });
                p?.WaitForExit();
            }
            catch { return false; }
            return IsEnabled() == on;
        }

        /// <summary>The elevated half: runs in the copy started by Set.</summary>
        public static int Apply(bool on)
        {
            string exe = Environment.ProcessPath!;
            Run("netsh", "advfirewall firewall delete rule name=\"" + RuleName + "\"");
            if (!on)
            {
                Run("schtasks", "/Delete /TN \"" + TaskName + "\" /F");
                return 0;
            }
            Run("netsh", "advfirewall firewall add rule name=\"" + RuleName + "\" dir=in action=allow enable=yes profile=any program=\"" + exe + "\"");
            // Not schtasks /Create: its tasks stop after 3 days and refuse to start on battery.
            string ps = "$u = [Security.Principal.WindowsIdentity]::GetCurrent().Name; " +
                "$a = New-ScheduledTaskAction -Execute '" + exe.Replace("'", "''") + "' -Argument '--host'; " +
                "$t = New-ScheduledTaskTrigger -AtLogOn -User $u; " +
                "$p = New-ScheduledTaskPrincipal -UserId $u -LogonType Interactive -RunLevel Highest; " +
                "$s = New-ScheduledTaskSettingsSet -ExecutionTimeLimit 0 -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -MultipleInstances IgnoreNew; " +
                "Register-ScheduledTask -TaskName '" + TaskName + "' -Action $a -Trigger $t -Principal $p -Settings $s -Force | Out-Null";
            return Run("powershell", "-NoProfile -NonInteractive -Command \"" + ps + "\"");
        }

        private static int Run(string file, string args)
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo(file, args) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })!;
                p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                return p.ExitCode;
            }
            catch { return -1; }
        }
    }
}
