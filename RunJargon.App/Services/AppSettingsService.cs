using System.IO;
using System.Text.Json;
using RunJargon.App.Models;

namespace RunJargon.App.Services;

public sealed class AppSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };
    private const string LegacyAppDataFolderName = "TabTranslate";
    private const string AppDataFolderName = "Run Jargon";

    private readonly string _settingsPath;

    public AppSettingsService()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appDataDirectory = Path.Combine(localAppData, AppDataFolderName);
        var legacySettingsPath = Path.Combine(localAppData, LegacyAppDataFolderName, "settings.json");

        Directory.CreateDirectory(appDataDirectory);
        _settingsPath = Path.Combine(appDataDirectory, "settings.json");
        TryMigrateLegacySettings(legacySettingsPath, _settingsPath);
    }

    public TranslationSettings Load()
    {
        if (!File.Exists(_settingsPath))
        {
            return new TranslationSettings(string.Empty, "global", "https://api.cognitive.microsofttranslator.com");
        }

        try
        {
            var json = File.ReadAllText(_settingsPath);
            return JsonSerializer.Deserialize<TranslationSettings>(json, JsonOptions)
                   ?? new TranslationSettings(string.Empty, "global", "https://api.cognitive.microsofttranslator.com");
        }
        catch
        {
            return new TranslationSettings(string.Empty, "global", "https://api.cognitive.microsofttranslator.com");
        }
    }

    public void Save(TranslationSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.AzureApiKey)
            && string.IsNullOrWhiteSpace(settings.AzureRegion)
            && string.IsNullOrWhiteSpace(settings.AzureEndpoint))
        {
            if (File.Exists(_settingsPath))
            {
                File.Delete(_settingsPath);
            }

            return;
        }

        var json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(_settingsPath, json);
    }

    private static void TryMigrateLegacySettings(string legacySettingsPath, string currentSettingsPath)
    {
        if (File.Exists(currentSettingsPath) || !File.Exists(legacySettingsPath))
        {
            return;
        }

        try
        {
            File.Copy(legacySettingsPath, currentSettingsPath, overwrite: false);
        }
        catch
        {
            // If migration fails, the app will continue with defaults and save the new settings later.
        }
    }
}
