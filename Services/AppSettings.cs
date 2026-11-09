using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace TodoEdge
{
    public class AppSettings
    {
        public string? ApiKeyProtected { get; set; }
        public string Model { get; set; } = "claude-haiku-5-5";
        public string Edge { get; set; } = "Top";
        public bool OpenOnHover { get; set; } = true;
        public string MyName { get; set; } = "";
        public int? PosX { get; set; }
        public int? PosY { get; set; }
        public List<string> Tags { get; set; } = new List<string> { "VKE", "GBPS", "Personal" };

        [JsonIgnore]
        public string? ApiKey
        {
            get => Dpapi.Unprotect(ApiKeyProtected);
            set => ApiKeyProtected = Dpapi.Protect(value);
        }

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions { WriteIndented = true };

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(Paths.SettingsFile))
                {
                    var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Paths.SettingsFile));
                    if (s != null) return s;
                }
            }
            catch
            {
                // Corrupt settings: fall back to defaults.
            }
            return new AppSettings();
        }

        public void Save()
        {
            Paths.Ensure();
            Paths.WriteAllTextSafe(Paths.SettingsFile, JsonSerializer.Serialize(this, JsonOptions));
        }
    }

    internal static class StartupHelper
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "TodoEdge";

        public static bool IsEnabled
        {
            get
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
                return key?.GetValue(ValueName) != null;
            }
        }

        public static void Set(bool enabled)
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled)
            {
                string exe = Environment.ProcessPath ?? "";
                key.SetValue(ValueName, "\"" + exe + "\"");
            }
            else
            {
                key.DeleteValue(ValueName, false);
            }
        }
    }
}
