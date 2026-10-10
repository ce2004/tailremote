using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TailRemote
{
    /// <summary>A PC in the Saved PCs list; its password is DPAPI-encrypted like the others.</summary>
    internal sealed class SavedPc
    {
        public string Address { get; set; } = "";
        public int Port { get; set; } = Protocol.DefaultPort;
        public string PasswordEnc { get; set; } = "";
        public override string ToString() => Port == Protocol.DefaultPort ? Address : Address + ", port " + Port;
    }

    /// <summary>Saved in %APPDATA%\TailRemote\settings.json; the passwords are DPAPI-encrypted.</summary>
    internal sealed class Settings
    {
        public bool HostMode { get; set; }
        public string Address { get; set; } = "";
        public int Port { get; set; } = Protocol.DefaultPort;
        public string PasswordEnc { get; set; } = "";
        public string OutputDevice { get; set; } = "";
        /// <summary>Host: the output whose sound is sent ("" = Windows' default).</summary>
        public string CaptureDevice { get; set; } = "";
        /// <summary>Where Send files and Send a folder from the other PC are saved; empty: Downloads\TailRemote.</summary>
        public string ReceiveFolder { get; set; } = "";
        public string ListenPasswordEnc { get; set; } = "";
        public bool Logging { get; set; }
        /// <summary>Catch up by speeding up the sound instead of skipping (changes the pitch a little).</summary>
        public bool CatchUpBySpeed { get; set; }
        public bool AnnounceQuality { get; set; }
        /// <summary>Piano tones for connecting, clipboard, files and the rest (Sounds.cs).</summary>
        public bool Sounds { get; set; } = true;
        /// <summary>Which sound each event makes (event name to sound name); events not here get the piano.</summary>
        public System.Collections.Generic.Dictionary<string, string> SoundChoices { get; set; } = new();
        /// <summary>The major key the sounds play in, by pitch class (C 0 ... B 11). B flat until changed.</summary>
        public int SoundKey { get; set; } = 10;
        /// <summary>Sound quality: -1 follows the connection (Variable); otherwise the bitrate step it is locked to.</summary>
        public int SoundQuality { get; set; } = -1;
        public System.Collections.Generic.List<SavedPc> SavedPcs { get; set; } = new();

        [JsonIgnore]
        public string ListenPassword
        {
            get => Unprotect(ListenPasswordEnc);
            set => ListenPasswordEnc = Protect(value);
        }

        public static string Protect(string value) =>
            value.Length == 0 ? "" : Convert.ToBase64String(Native.Protect(Encoding.UTF8.GetBytes(value), true));

        public static string Unprotect(string enc)
        {
            if (enc.Length == 0) return "";
            try { return Encoding.UTF8.GetString(Native.Protect(Convert.FromBase64String(enc), false)); }
            catch { return ""; }
        }
        /// <summary>Every port TailRemote has used here, so Port editor can still close it later.</summary>
        public System.Collections.Generic.List<int> KnownPorts { get; set; } = new();
        /// <summary>What was running when TailRemote last closed: "host", "connect" or "". Used by --resume.</summary>
        public string ResumeState { get; set; } = "";

        private static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TailRemote");
        internal static string FilePath => Path.Combine(Dir, "settings.json");
        // Save() writes here first, then atomically replaces FilePath, so a crash or power loss
        // mid-write can never leave a half-written settings.json as the live file.
        private static string TempPath => FilePath + ".tmp";

        [JsonIgnore]
        public string Password
        {
            get => Unprotect(PasswordEnc);
            set => PasswordEnc = Protect(value);
        }

        public static Settings Load()
        {
            try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings(); }
            catch
            {
                // The main file is missing, empty or corrupt - possibly from a crash or power loss
                // mid-write before Save() below wrote atomically. A temp file left over from such an
                // interrupted save might still be a complete, valid settings file: try it once before
                // giving up and starting fresh.
                try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(TempPath)) ?? new Settings(); }
                catch { return new Settings(); }
            }
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(TempPath, json);
                // Atomic: the live settings file is either the old complete one or the new complete
                // one, never a half-written mix, even if this process is killed partway through.
                if (File.Exists(FilePath)) File.Replace(TempPath, FilePath, null);
                else File.Move(TempPath, FilePath);
            }
            catch { }
        }
    }
}
