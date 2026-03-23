using System;
using System.IO;
using UnityEngine;
using Newtonsoft.Json;

namespace MateEngine.Agent
{
    public class MateAgentConfig : MonoBehaviour
    {
        public static MateAgentConfig Instance { get; private set; }

        public MateSettings Settings { get; private set; }
        public string SystemPrompt { get; private set; }

        static string ConfigDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".mateengine");
        static string SettingsPath => Path.Combine(ConfigDir, "settings.json");
        static string SystemPromptPath => Path.Combine(ConfigDir, "system.md");

        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
            LoadOrCreateDefaults();
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        public void LoadOrCreateDefaults()
        {
            try { Directory.CreateDirectory(ConfigDir); }
            catch (Exception e) { Debug.LogWarning($"[MateAgent] Cannot create config dir: {e.Message}"); }

            Settings = LoadSettings();
            SystemPrompt = LoadSystemPrompt();
        }

        MateSettings LoadSettings()
        {
            if (File.Exists(SettingsPath))
            {
                try
                {
                    string json = File.ReadAllText(SettingsPath);
                    var s = JsonConvert.DeserializeObject<MateSettings>(json);
                    if (s != null) return s;
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[MateAgent] Failed to parse settings.json: {e.Message}");
                }
            }

            var defaults = new MateSettings();
            SaveSettings(defaults);
            return defaults;
        }

        string LoadSystemPrompt()
        {
            if (File.Exists(SystemPromptPath))
            {
                try { return File.ReadAllText(SystemPromptPath); }
                catch (Exception e) { Debug.LogWarning($"[MateAgent] Failed to read system.md: {e.Message}"); }
            }

            const string defaultPrompt =
@"You are a friendly desktop companion. You live on the user's screen as a small avatar.
You can see what window the user has open and react with expressions, dances, and speech bubbles.
Keep reactions short and playful. Use actions sparingly - don't be annoying.
When the user talks to you directly, be helpful and conversational.";

            try { File.WriteAllText(SystemPromptPath, defaultPrompt); }
            catch { /* ignore */ }
            return defaultPrompt;
        }

        public void SaveSettings(MateSettings settings = null)
        {
            settings ??= Settings;
            try
            {
                string json = JsonConvert.SerializeObject(settings, Formatting.Indented);
                File.WriteAllText(SettingsPath, json);
            }
            catch (Exception e) { Debug.LogWarning($"[MateAgent] Failed to save settings.json: {e.Message}"); }
        }

        public void Reload() => LoadOrCreateDefaults();

    }

    [Serializable]
    public class MateSettings
    {
        public ApiServerConfig api_server = new ApiServerConfig();
        public EventsConfig events = new EventsConfig();
    }

    [Serializable]
    public class ApiServerConfig
    {
        public bool enabled = true;
        public int port = 19800;
        public string bearer_token = "";
    }

    [Serializable]
    public class EventsConfig
    {
        public bool monitor_windows = true;
        public bool monitor_idle = true;
        public int idle_threshold_sec = 300;
    }
}
