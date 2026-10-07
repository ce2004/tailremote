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
        public string ListenPasswordEnc { get; set; } = "";
        public bool ShareClipboard { get; set; } = true;
        public bool Logging { get; set; }
        /// <summary>Catch up by speeding up the sound instead of skipping (changes the pitch a little).</summary>
        public bool CatchUpBySpeed { get; set; }
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
        private static string FilePath => Path.Combine(Dir, "settings.json");

        [JsonIgnore]
        public string Password
        {
            get => Unprotect(PasswordEnc);
            set => PasswordEnc = Protect(value);
        }

        public static Settings Load()
        {
            try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings(); }
            catch { return new Settings(); }
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }
    }
}
