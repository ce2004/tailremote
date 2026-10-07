using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TailRemote
{
    /// <summary>Saved in %APPDATA%\TailRemote\settings.json; the password is DPAPI-encrypted.</summary>
    internal sealed class Settings
    {
        public bool HostMode { get; set; }
        public string Address { get; set; } = "";
        public int Port { get; set; } = Protocol.DefaultPort;
        public string PasswordEnc { get; set; } = "";
        public string OutputDevice { get; set; } = "";
        /// <summary>Every port TailRemote has used here, so Port editor can still close it later.</summary>
        public System.Collections.Generic.List<int> KnownPorts { get; set; } = new();
        /// <summary>What was running when TailRemote last closed: "host", "connect" or "". Used by --resume.</summary>
        public string ResumeState { get; set; } = "";

        private static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TailRemote");
        private static string FilePath => Path.Combine(Dir, "settings.json");

        [JsonIgnore]
        public string Password
        {
            get
            {
                if (PasswordEnc.Length == 0) return "";
                try { return Encoding.UTF8.GetString(Native.Protect(Convert.FromBase64String(PasswordEnc), false)); }
                catch { return ""; }
            }
            set => PasswordEnc = value.Length == 0 ? "" : Convert.ToBase64String(Native.Protect(Encoding.UTF8.GetBytes(value), true));
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
