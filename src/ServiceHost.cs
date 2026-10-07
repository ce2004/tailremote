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
    /// The service (SYSTEM, session 0) cannot reach the screen itself, so it
    /// keeps an agent running in whichever session is on the console, also as
    /// SYSTEM: "TailRemote.exe --agent". The agent does the hosting and follows
    /// the input desktop, which is how keys reach the secure desktop. Only a
    /// service may send Ctrl+Alt+Del, so the agent asks the service for it over
    /// a pipe that only SYSTEM can open.
    ///
    /// The service runs its own copy in Program Files (only administrators can
    /// change it); its settings live in ProgramData, readable only by SYSTEM and
    /// administrators, with the passwords DPAPI-encrypted for the machine.
    /// </summary>
    internal static class ServiceHost
    {
        public const string Name = "TailRemoteHost";
        private const string PipeName = "TailRemoteSas";

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
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 512 * 1024) File.Delete(LogPath);
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
                if (Process.GetProcessesByName("TailRemote").Length == 0) return;
                bool any = false;
                foreach (var p in Process.GetProcessesByName("TailRemote"))
                {
                    try { if (string.Equals(p.MainModule?.FileName, InstalledExe, StringComparison.OrdinalIgnoreCase)) any = true; } catch { }
                }
                if (!any) return;
                Thread.Sleep(250);
            }
        }

        private static int RunHidden(string file, string args)
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
            new Thread(SasPipe) { IsBackground = true, Name = "TailRemote SAS" }.Start();
            Report(NativeService.SERVICE_RUNNING);
            Log("Service started.");

            Process? agent = null;
            uint agentSession = uint.MaxValue;
            long lastStart = 0;
            while (!Stop.WaitOne(0))
            {
                uint session = NativeService.WTSGetActiveConsoleSessionId();
                bool alive = agent != null && !agent.HasExited;
                if (session != 0xFFFFFFFF && (!alive || session != agentSession) && Environment.TickCount64 - lastStart > 2000)
                {
                    try { if (alive) agent!.Kill(); } catch { }
                    lastStart = Environment.TickCount64;
                    agent = StartAgent(session);
                    agentSession = session;
                }
                WaitHandle.WaitAny(new WaitHandle[] { Stop, Wake }, 1000);
            }
            try { if (agent != null && !agent.HasExited) agent.Kill(); } catch { }
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

        /// <summary>Sends Ctrl+Alt+Del when the agent asks. Only SYSTEM can open this pipe.</summary>
        private static void SasPipe()
        {
            var sec = new PipeSecurity();
            sec.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
            while (!Stop.WaitOne(0))
            {
                try
                {
                    using var pipe = NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.None, 0, 0, sec);
                    pipe.WaitForConnection();
                    if (pipe.ReadByte() == 1)
                    {
                        NativeService.SendSAS(false);
                        Log("Sent Control Alt Delete.");
                    }
                }
                catch (Exception e) { Log("SAS pipe: " + e.Message); Thread.Sleep(1000); }
            }
        }

        /// <summary>From the agent: ask the service to send Ctrl+Alt+Del.</summary>
        public static bool RequestSas()
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                pipe.Connect(2000);
                pipe.WriteByte(1);
                return true;
            }
            catch { return false; }
        }
    }
}
