using System;
using System.IO;
using System.Text.Json;
using CSharpToUppaal.Backend.Models;

namespace CSharpToUppaal.GUI.Services
{
    public class GuiAppSettings
    {
        public bool OllamaEnabled { get; set; }
        public string OllamaBaseUrl { get; set; } = "http://localhost:11434/api";
        public string OllamaModel { get; set; } = "llama3:latest";
        public int OllamaTimeoutSeconds { get; set; } = 45;

        public OllamaRequirementSettings ToRequirementSettings()
        {
            return new OllamaRequirementSettings
            {
                Enabled = OllamaEnabled,
                BaseUrl = string.IsNullOrWhiteSpace(OllamaBaseUrl) ? "http://localhost:11434/api" : OllamaBaseUrl,
                Model = string.IsNullOrWhiteSpace(OllamaModel) ? "llama3:latest" : OllamaModel,
                TimeoutSeconds = OllamaTimeoutSeconds <= 0 ? 45 : OllamaTimeoutSeconds
            };
        }
    }

    public static class AppSettingsService
    {
        private static string SettingsPath
        {
            get
            {
                var root = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "CSharpToUppaal");
                Directory.CreateDirectory(root);
                return Path.Combine(root, "settings.json");
            }
        }

        public static GuiAppSettings Load()
        {
            try
            {
                if (!File.Exists(SettingsPath))
                    return new GuiAppSettings();

                var json = File.ReadAllText(SettingsPath);
                return JsonSerializer.Deserialize<GuiAppSettings>(json) ?? new GuiAppSettings();
            }
            catch
            {
                return new GuiAppSettings();
            }
        }

        public static void Save(GuiAppSettings settings)
        {
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
    }
}
