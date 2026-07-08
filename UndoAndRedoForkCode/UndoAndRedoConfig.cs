using System.Text.Json;
using Godot;

namespace UndoAndRedoForkCode;

internal static class UndoAndRedoConfig
{
    private const int DefaultSnapshotLimit = 100;
    private static readonly string ConfigPath = Path.Combine(OS.GetUserDataDir(), "mod_configs", "UndoAndRestart.json");

    public static int SnapshotLimit { get; private set; } = DefaultSnapshotLimit;
    public static bool ShowActionHistoryOverlay { get; private set; } = true;

    public static void Load()
    {
        try
        {
            if (!File.Exists(ConfigPath))
            {
                Save();
                return;
            }

            string json = File.ReadAllText(ConfigPath);
            Settings? settings = JsonSerializer.Deserialize<Settings>(json);
            SetSnapshotLimit(settings?.SnapshotLimit ?? DefaultSnapshotLimit, save: false);
            ShowActionHistoryOverlay = settings?.ShowActionHistoryOverlay ?? true;
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"Failed to load config, using defaults: {ex.Message}");
            SnapshotLimit = DefaultSnapshotLimit;
            ShowActionHistoryOverlay = true;
        }
    }

    public static bool TrySetSnapshotLimit(string text, out int value)
    {
        if (!int.TryParse(text, out value))
        {
            value = SnapshotLimit;
            return false;
        }

        SetSnapshotLimit(value, save: true);
        value = SnapshotLimit;
        return true;
    }

    public static void SetSnapshotLimit(int value, bool save)
    {
        SnapshotLimit = Math.Max(0, value);
        if (save)
        {
            Save();
        }
    }

    public static void SetShowActionHistoryOverlay(bool value, bool save)
    {
        ShowActionHistoryOverlay = value;
        if (save)
        {
            Save();
        }
    }

    public static string LimitRangeText => UndoText.LimitRangeText;

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            string json = JsonSerializer.Serialize(new Settings
            {
                SnapshotLimit = SnapshotLimit,
                ShowActionHistoryOverlay = ShowActionHistoryOverlay,
            }, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ConfigPath, json);
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"Failed to save config: {ex.Message}");
        }
    }

    private sealed class Settings
    {
        public int SnapshotLimit { get; set; } = DefaultSnapshotLimit;
        public bool ShowActionHistoryOverlay { get; set; } = true;
    }
}
