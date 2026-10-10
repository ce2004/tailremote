using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Threading;
using Microsoft.Win32;

namespace TailRemote
{
    /// <summary>
    /// The host as a Windows service, so the remote PC can be used at the lock
    /// screen, the sign-in screen and UAC prompts, and can be sent Ctrl+Alt+Del.
    ///
    /// The service (SYSTEM, session 0) hosts, from the moment Windows starts it, before
    /// the sign-in screen (Agent.StartHosting): connections, the sound, files, and Ctrl+Alt+Del,
    /// which only a service may send. It cannot reach the screen, so it keeps an agent in
    /// whichever session is on the console, also as SYSTEM: "TailRemote.exe --agent", which
    /// types the keys it is handed (AgentLink) on whichever desktop has the keyboard.
    ///
    /// The service runs its own copy in Program Files (only administrators can
    /// change it); its settings live in ProgramData, readable only by SYSTEM and
    /// administrators, with the passwords DPAPI-encrypted for the machine.
    /// </summary>
    internal static class ServiceHost
    {
        public const string Name = "TailRemoteHost";

        public static string InstallDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "TailRemote");
        public static string InstalledExe => Path.Combine(InstallDir, "TailRemote.exe");
        public static string DataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TailRemote");
        private static string ConfigPath => Path.Combine(DataDir, "service.json");
        public static string LogPath => Path.Combine(DataDir, "service.log");

        public sealed class Config
        {
            public int Port { get; set; } = Protocol.DefaultPort;
            public string PasswordEnc { get; set; } = "";
            public string ListenPasswordEnc { get; set; } = "";
            public string CaptureDevice { get; set; } = "";
            public bool Logging { get; set; }
            public bool WeSetSasPolicy { get; set; }

            public static string Seal(string s) => s.Length == 0 ? "" : Convert.ToBase64String(Native.Protect(System.Text.Encoding.UTF8.GetBytes(s), true, machine: true));
            public static string Open(string s) => s.Length == 0 ? "" : System.Text.Encoding.UTF8.GetString(Native.Protect(Convert.FromBase64String(s), false, machine: true));
        }

        public static Config? LoadConfig()
        {
            try { return JsonSerializer.Deserialize<Config>(File.ReadAllText(ConfigPath)); }
            catch { return null; }
        }

        public static void Log(string line)
        {
            try
            {
                // Kept, not wiped: the previous half megabyte moves to service.log.old.
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 512 * 1024) File.Move(LogPath, LogPath + ".old", overwrite: true);
                File.AppendAllText(LogPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + line + Environment.NewLine);
            }
            catch { }
        }

        // ================= Installing (elevated: --service install / remove) =================

        public static bool IsInstalled() => RunHidden("sc.exe", "query " + Name) == 0;

        /// <summary>The version of the copy the service runs, or null.</summary>
        public static Version? InstalledVersion()
        {
            try
            {
                var v = FileVersionInfo.GetVersionInfo(InstalledExe);
                return new Version(v.FileMajorPart, v.FileMinorPart, v.FileBuildPart);
            }
            catch { return null; }
        }

        /// <summary>Asks for administrator rights and runs --service install or remove. False if refused or failed.</summary>
        public static System.Threading.Tasks.Task<bool> SetAsync(bool install) => System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--service " + (install ? "install" : "remove"))
                { UseShellExecute = true, Verb = "runas" });
                if (p == null) return false;
                p.WaitForExit();
                return p.ExitCode == 0;
            }
            catch { return false; }
        });

        /// <summary>
        /// Installs, or updates the copy and settings of, the service from this
        /// user's TailRemote settings, then (re)starts it. Elevated.
        /// </summary>
        public static int Install()
        {
            try
            {
                var s = Settings.Load();
                if (s.Password.Length < MainForm.MinPasswordLength) return Fail("Set a password of at least 5 characters first.");

                RunHidden("sc.exe", "stop " + Name);
                WaitForStopped();

                Directory.CreateDirectory(InstallDir);
                if (!string.Equals(Path.GetFullPath(Environment.ProcessPath!), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase))
                {
                    for (int i = 0; ; i++)
                    {
                        try { File.Copy(Environment.ProcessPath!, InstalledExe, true); break; }
                        catch when (i < 20) { Thread.Sleep(250); } // the old agent is still letting go
                    }
                }

                var old = LoadConfig();
                var cfg = new Config
                {
                    Port = s.Port,
                    PasswordEnc = Config.Seal(s.Password),
                    ListenPasswordEnc = Config.Seal(s.ListenPassword),
                    CaptureDevice = s.CaptureDevice,
                    Logging = s.Logging,
                    WeSetSasPolicy = old?.WeSetSasPolicy ?? false,
                };
                // Ctrl+Alt+Del from a service needs this policy (1 = services may send it).
                using (var k = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System"))
                {
                    if (k.GetValue("SoftwareSASGeneration") is not int v || (v & 1) == 0)
                    {
                        k.SetValue("SoftwareSASGeneration", 1, RegistryValueKind.DWord);
                        cfg.WeSetSasPolicy = true;
                    }
                }
                WriteConfig(cfg);

                string bin = "\"" + InstalledExe + "\" --service";
                if (!IsInstalled())
                {
                    if (RunHidden("sc.exe", "create " + Name + " binPath= \"" + bin.Replace("\"", "\\\"") + "\" start= auto DisplayName= \"TailRemote host\"") != 0)
                        return Fail("Windows would not create the service.");
                }
                else RunHidden("sc.exe", "config " + Name + " binPath= \"" + bin.Replace("\"", "\\\"") + "\" start= auto");
                RunHidden("sc.exe", "description " + Name + " \"Lets another PC control this one through TailRemote, including the lock screen and UAC prompts.\"");
                RunHidden("sc.exe", "failure " + Name + " reset= 60 actions= restart/2000/restart/5000/restart/10000");

                // The service replaces the at-sign-in task, and opens its port.
                Startup.Apply(false);
                Firewall.Apply(true, s.Port);

                if (RunHidden("sc.exe", "start " + Name) != 0) return Fail("The service was installed but would not start.");
                return 0;
            }
            catch (Exception e) { return Fail(e.Message); }
        }

        public static int Remove()
        {
            try
            {
                RunHidden("sc.exe", "stop " + Name);
                WaitForStopped();
                RunHidden("sc.exe", "delete " + Name);
                var cfg = LoadConfig();
                if (cfg?.WeSetSasPolicy == true)
                {
                    using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", true);
                    k?.DeleteValue("SoftwareSASGeneration", false);
                }

                // Undo what Install() did: close the port it opened, and bring
                // back the at-sign-in task the service had replaced.
                Firewall.Apply(false, cfg?.Port ?? Protocol.DefaultPort);
                Startup.Apply(true);
                for (int i = 0; i < 20; i++)
                {
                    try
                    {
                        if (Directory.Exists(InstallDir)) Directory.Delete(InstallDir, true);
                        if (Directory.Exists(DataDir)) Directory.Delete(DataDir, true);
                        break;
                    }
                    catch { Thread.Sleep(250); }
                }
                return 0;
            }
            catch (Exception e) { return Fail(e.Message); }
        }

        private static int Fail(string why)
        {
            try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "tailremote-service.txt"), why); } catch { }
            return 1;
        }

        public static string LastError()
        {
            try { return File.ReadAllText(Path.Combine(Path.GetTempPath(), "tailremote-service.txt")); }
            catch { return "administrator permission was not given"; }
        }

        private static void WriteConfig(Config cfg)
        {
            var dir = Directory.CreateDirectory(DataDir);
            // Only SYSTEM and administrators, nothing inherited.
            var sec = new DirectorySecurity();
            sec.SetAccessRuleProtection(true, false);
            foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
                sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            dir.SetAccessControl(sec);
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true }));
        }

        private static void WaitForStopped()
        {
            for (int i = 0; i < 40; i++)
            {
                bool any = false;
                foreach (var p in Process.GetProcessesByName("TailRemote"))
                {
                    using (p) try { if (string.Equals(p.MainModule?.FileName, InstalledExe, StringComparison.OrdinalIgnoreCase)) any = true; } catch { }
                }
                if (!any) return;
                Thread.Sleep(250);
            }
        }

        private static int RunHidden(string file, string args) => Native.RunHidden(file, args);

        // ================= The service itself (--service) =================

        private static IntPtr _statusHandle;
        private static NativeService.SERVICE_STATUS _status;
        private static readonly ManualResetEvent Stop = new(false);
        private static readonly AutoResetEvent Wake = new(false);
        private static NativeService.ServiceMainProc? _main;
        private static NativeService.HandlerEx? _handler;

        public static int RunService()
        {
            _main = ServiceMain;
            _handler = Handler;
            var table = new[]
            {
                new NativeService.SERVICE_TABLE_ENTRY { Name = Marshal.StringToHGlobalUni(Name), Proc = _main },
                new NativeService.SERVICE_TABLE_ENTRY(),
            };
            return NativeService.StartServiceCtrlDispatcherW(table) ? 0 : 1;
        }

        private static void Report(int state)
        {
            _status.ServiceType = NativeService.SERVICE_WIN32_OWN_PROCESS;
            _status.CurrentState = state;
            _status.ControlsAccepted = state == NativeService.SERVICE_RUNNING
                ? NativeService.SERVICE_ACCEPT_STOP | NativeService.SERVICE_ACCEPT_SHUTDOWN | NativeService.SERVICE_ACCEPT_SESSIONCHANGE : 0;
            _status.WaitHint = state == NativeService.SERVICE_RUNNING || state == NativeService.SERVICE_STOPPED ? 0 : 5000;
            NativeService.SetServiceStatus(_statusHandle, ref _status);
        }

        private static int Handler(int control, int eventType, IntPtr data, IntPtr context)
        {
            switch (control)
            {
                case NativeService.SERVICE_CONTROL_STOP:
                case NativeService.SERVICE_CONTROL_SHUTDOWN:
                    if (control == NativeService.SERVICE_CONTROL_SHUTDOWN) _shuttingDown = true;
                    Report(NativeService.SERVICE_STOP_PENDING);
                    Stop.Set();
                    break;
                case NativeService.SERVICE_CONTROL_SESSIONCHANGE:
                    Wake.Set(); // someone signed in or out, or switched user: move the agent
                    break;
            }
            return 0;
        }

        private static void ServiceMain(int argc, IntPtr argv)
        {
            _statusHandle = NativeService.RegisterServiceCtrlHandlerExW(Name, _handler!, IntPtr.Zero);
            Report(NativeService.SERVICE_START_PENDING);
            // Hosting starts at once, from the service itself: before the sign-in screen exists.
            new Thread(Agent.StartHosting) { IsBackground = true, Name = "TailRemote service hosting" }.Start();
            new Thread(UpdateLoop) { IsBackground = true, Name = "TailRemote service updates" }.Start();
            new Thread(UpdatePipe) { IsBackground = true, Name = "TailRemote update nudge" }.Start();
            Report(NativeService.SERVICE_RUNNING);
            try { File.Delete(InstalledExe + ".old"); } catch { } // left by the last self-update
            Log("Service started, version " + Updater.Current + ".");

            Process? agent = null;
            uint agentSession = uint.MaxValue;
            long lastStart = 0, wait = 2000;
            while (!Stop.WaitOne(0))
            {
                if (Restarting)
                {
                    // A new copy is in place: close the agent and leave without saying
                    // "stopped", so Windows' recovery setting starts the new copy.
                    Agent.LeaveForUpdate(_restartingTo);
                    try { if (agent != null && !agent.HasExited) agent.Kill(); } catch { }
                    Log("Restarting into the new version.");
                    Environment.Exit(1);
                }
                uint session = NativeService.WTSGetActiveConsoleSessionId();
                bool alive = agent != null && !agent.HasExited;
                if (session != 0xFFFFFFFF && (!alive || session != agentSession) && Environment.TickCount64 - lastStart > wait)
                {
                    try { if (alive) agent!.Kill(); } catch { }
                    // An agent that keeps failing straight away is retried less and less often
                    // (up to once a minute), so it cannot fill the log or keep the CPU busy.
                    bool quickFailure = !alive && session == agentSession && Environment.TickCount64 - lastStart < 10_000;
                    wait = quickFailure ? Math.Min(wait * 2, 60_000) : 2000;
                    agent?.Dispose();
                    lastStart = Environment.TickCount64;
                    agent = StartAgent(session);
                    agentSession = session;
                }
                WaitHandle.WaitAny(new WaitHandle[] { Stop, Wake }, 1000);
            }
            try { if (agent != null && !agent.HasExited) agent.Kill(); } catch { }
            agent?.Dispose();
            Agent.StopHosting(_shuttingDown ? Protocol.LeavingShutdown : Protocol.LeavingStopped);
            Log("Service stopped.");
            Report(NativeService.SERVICE_STOPPED);
        }

        private static Process? StartAgent(uint session)
        {
            IntPtr own = IntPtr.Zero, token = IntPtr.Zero;
            try
            {
                if (!NativeService.OpenProcessToken(NativeService.GetCurrentProcess(), NativeService.TOKEN_ALL_ACCESS, out own)) throw new InvalidOperationException("OpenProcessToken " + Marshal.GetLastWin32Error());
                if (!NativeService.DuplicateTokenEx(own, NativeService.TOKEN_ALL_ACCESS, IntPtr.Zero, NativeService.SecurityImpersonation, NativeService.TokenPrimary, out token))
                    throw new InvalidOperationException("DuplicateTokenEx " + Marshal.GetLastWin32Error());
                uint sid = session;
                if (!NativeService.SetTokenInformation(token, NativeService.TokenSessionId, ref sid, 4)) throw new InvalidOperationException("SetTokenInformation " + Marshal.GetLastWin32Error());
                var si = new NativeService.STARTUPINFO { cb = Marshal.SizeOf<NativeService.STARTUPINFO>(), lpDesktop = "winsta0\\default" };
                string exe = Environment.ProcessPath!;
                if (!NativeService.CreateProcessAsUserW(token, null, "\"" + exe + "\" --agent", IntPtr.Zero, IntPtr.Zero, false,
                        NativeService.CREATE_NO_WINDOW | NativeService.CREATE_UNICODE_ENVIRONMENT, IntPtr.Zero, Path.GetDirectoryName(exe), ref si, out var pi))
                    throw new InvalidOperationException("CreateProcessAsUser " + Marshal.GetLastWin32Error());
                NativeService.CloseHandle(pi.hThread);
                NativeService.CloseHandle(pi.hProcess);
                Log("Agent started in session " + session + ".");
                return Process.GetProcessById(pi.dwProcessId);
            }
            catch (Exception e)
            {
                Log("Could not start the agent: " + e.Message);
                return null;
            }
            finally
            {
                if (own != IntPtr.Zero) NativeService.CloseHandle(own);
                if (token != IntPtr.Zero) NativeService.CloseHandle(token);
            }
        }

        // ================= The service keeps itself up to date =================

        private const string UpdatePipeName = "TailRemoteUpdate";
        private static readonly AutoResetEvent CheckNow = new(false);
        private static volatile bool Restarting;
        private static volatile string _restartingTo = "";
        private static volatile bool _shuttingDown;

        /// <summary>
        /// Never on its own: Conner chooses when to update. When TailRemote on
        /// this PC has been updated (Check for updates), it nudges the service,
        /// which then downloads the same version for itself, checks it against
        /// GitHub's fingerprint, puts it in place of the running one and restarts
        /// into it. No questions, no administrator prompt: it runs as SYSTEM.
        /// </summary>
        private static void UpdateLoop()
        {
            while (WaitHandle.WaitAny(new WaitHandle[] { Stop, CheckNow }) == 1)
            {
                // A controlling PC's request (File, Update the remote PC) is answered either way.
                // Taken as one: the version and who to answer always belong together.
                var request = Interlocked.Exchange(ref _request, null);
                var reply = request?.Reply;
                try { TryUpdate(request?.Want, reply); }
                catch (Exception e) { Log("Update failed: " + e.Message); reply?.Invoke("The remote PC could not update: " + e.Message); }
                if (Restarting) { Wake.Set(); return; }
            }
        }

        private sealed record UpdateRequest(Version Want, Action<string> Reply);
        private static UpdateRequest? _request;

        /// <summary>
        /// A controlling PC chose File, Update the remote PC: update to that version, which must be
        /// GitHub's newest. That is someone choosing to update, so the window's version is no limit.
        /// </summary>
        public static void UpdateFor(string version, Action<string> reply)
        {
            if (!Version.TryParse(version, out var want)) { reply("TailRemote on the remote PC did not understand the version " + version + "."); return; }
            if (want <= Updater.Current) { reply("The remote PC already has TailRemote " + Updater.Current + "."); return; }
            if (Restarting) { reply("TailRemote on the remote PC is already updating."); return; }
            Interlocked.Exchange(ref _request, new UpdateRequest(want, reply))?.Reply("Another controlling PC asked for an update at the same time; it is going ahead.");
            CheckNow.Set();
        }

        private static void TryUpdate(Version? requested, Action<string>? reply)
        {
            var r = Updater.CheckAsync().GetAwaiter().GetResult();
            if (requested != null && r?.Version != requested)
            {
                reply?.Invoke("The remote PC did not update: the newest TailRemote on GitHub is " + (r?.Version ?? Updater.Current) + ", not " + requested + ".");
                return;
            }
            if (r == null) return;
            var chosen = WindowVersion();
            if (requested == null && chosen != null && r.Version > chosen)
            {
                Log("GitHub has " + r.Version + ", but TailRemote on this PC is " + chosen + "; staying in step with it.");
                return;
            }
            Log("Updating the service to version " + r.Version + ".");
            reply?.Invoke("The remote PC's TailRemote service is downloading " + r.Version + ". Everything carries on until it is ready.");
            byte[] data;
            using (var h = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMinutes(10) })
            {
                h.DefaultRequestHeaders.UserAgent.ParseAdd("TailRemote-service/" + Updater.Current);
                data = h.GetByteArrayAsync(r.Url).GetAwaiter().GetResult();
            }
            if (r.Sha256 == null || !Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data)).Equals(r.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("the download did not match GitHub's fingerprint, so it was not used");
            string exe = Environment.ProcessPath!, fresh = exe + ".new", old = exe + ".old";
            File.WriteAllBytes(fresh, data);
            try { File.Delete(old); } catch { }
            // Still there (antivirus, a lingering handle): a name of its own, cleared up later
            // with the other leftovers, rather than every update failing until it is freed.
            if (File.Exists(old)) old = exe + ".old-" + DateTime.UtcNow.Ticks;
            File.Move(exe, old);       // a running exe can be renamed, not overwritten
            try { File.Move(fresh, exe); }
            catch { File.Move(old, exe); throw; }
            _restartingTo = r.Version.ToString();
            Restarting = true;
        }

        /// <summary>A nudge from TailRemote after it updated itself: check now. At most once a minute.</summary>
        private static void UpdatePipe()
        {
            var sec = new PipeSecurity();
            sec.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
            sec.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
            sec.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
            long last = -60_000;
            while (!Stop.WaitOne(0))
            {
                try
                {
                    using var pipe = NamedPipeServerStreamAcl.Create(UpdatePipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.None, 0, 0, sec);
                    pipe.WaitForConnection();
                    if (pipe.ReadByte() == 1 && Environment.TickCount64 - last >= 60_000)
                    {
                        int a = pipe.ReadByte(), b = pipe.ReadByte(), c = pipe.ReadByte();
                        _windowVersion = a >= 0 && b >= 0 && c >= 0 ? new Version(a, b, c) : null;
                        last = Environment.TickCount64;
                        CheckNow.Set();
                    }
                }
                catch { Thread.Sleep(1000); }
            }
        }

        private static Version? _windowVersion;

        /// <summary>The version the TailRemote window that nudged us runs, sent with the nudge.</summary>
        private static Version? WindowVersion() => _windowVersion;

        /// <summary>From the TailRemote window, after it updated: ask the service to check for its update now.</summary>
        public static bool NudgeUpdate()
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", UpdatePipeName, PipeDirection.Out);
                pipe.Connect(2000);
                var v = Updater.Current;
                pipe.Write(new byte[] { 1, (byte)v.Major, (byte)v.Minor, (byte)Math.Max(0, v.Build) });
                return true;
            }
            catch { return false; }
        }
    }
}
