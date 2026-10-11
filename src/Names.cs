using System;
using System.IO;

namespace TailRemote
{
    /// <summary>
    /// What Kova is called in Windows: its service, pipes, folders, firewall rules and sign-in task.
    /// Up to 2.2 the app was TailRemote, under those names; none of them is looked for any more, so a
    /// PC is moved from TailRemote to Kova by hand (see CHANGES.txt for 2.3.0).
    /// </summary>
    internal static class Names
    {
        public const string Service = "KovaHost";

        private static string ProgramFiles => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        private static string ProgramData => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

        public static string InstallDir => Path.Combine(ProgramFiles, "Kova");
        public static string InstalledExe => Path.Combine(InstallDir, "Kova.exe");
        public static string DataDir => Path.Combine(ProgramData, "Kova");

        public static string FilesPipe => "KovaFiles" + TestPipes;   // the window's link to the service
        public static string AgentPipe => "KovaAgent" + TestPipes;   // the service's link to its agent
        public static string UpdatePipe => "KovaUpdate" + TestPipes; // the window's nudge to update the service

        /// <summary>The chaos test only: its pipes get this added, so on a PC with a real Kova service it never meets that one.</summary>
        internal static string TestPipes = "";

        public static string FirewallRule(int port) => "Kova port " + port;

        public const string Task = "Kova Host";

        /// <summary>This user's settings.</summary>
        public static string UserDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Kova");

        /// <summary>This user's local, throwaway files (the clipboard's holding folder, build leftovers).</summary>
        public static string LocalDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kova");
    }
}
