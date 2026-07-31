using System.Text.Json;
using Godot;

namespace UndoAndRestartCode;

internal static class UndoAndRestartConfig
{
    private const int DefaultSnapshotLimit = 100;
    private static readonly string ConfigPath = Path.Combine(OS.GetUserDataDir(), "mod_configs", "UndoAndRestart.json");

    public static int SnapshotLimit { get; private set; } = DefaultSnapshotLimit;
    public static bool ShowActionHistoryOverlay { get; private set; } = true;
    public static bool IncludeCardChoiceSnapshots { get; private set; } = true;
    public static bool HasAcknowledgedFeatureAnnouncement { get; private set; }

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
            SnapshotLimit = Math.Max(0, settings?.SnapshotLimit ?? DefaultSnapshotLimit);
            ShowActionHistoryOverlay = settings?.ShowActionHistoryOverlay ?? true;
            IncludeCardChoiceSnapshots = settings?.IncludeCardChoiceSnapshots ?? true;
            HasAcknowledgedFeatureAnnouncement =
                settings?.HasAcknowledgedFeatureAnnouncement ?? false;
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"Failed to load config, using defaults: {ex.Message}");
            SnapshotLimit = DefaultSnapshotLimit;
            ShowActionHistoryOverlay = true;
            IncludeCardChoiceSnapshots = true;
            HasAcknowledgedFeatureAnnouncement = false;
        }
    }

    public static bool SaveSettings(
        int snapshotLimit,
        bool showActionHistoryOverlay,
        bool includeCardChoiceSnapshots)
    {
        int oldSnapshotLimit = SnapshotLimit;
        bool oldShowActionHistoryOverlay = ShowActionHistoryOverlay;
        bool oldIncludeCardChoiceSnapshots = IncludeCardChoiceSnapshots;
        SnapshotLimit = Math.Max(0, snapshotLimit);
        ShowActionHistoryOverlay = showActionHistoryOverlay;
        IncludeCardChoiceSnapshots = includeCardChoiceSnapshots;
        if (Save())
        {
            return true;
        }

        SnapshotLimit = oldSnapshotLimit;
        ShowActionHistoryOverlay = oldShowActionHistoryOverlay;
        IncludeCardChoiceSnapshots = oldIncludeCardChoiceSnapshots;
        return false;
    }

    public static bool AcknowledgeFeatureAnnouncement()
    {
        HasAcknowledgedFeatureAnnouncement = true;
        if (Save())
        {
            return true;
        }

        HasAcknowledgedFeatureAnnouncement = false;
        return false;
    }

    public static string LimitRangeText => UndoText.LimitRangeText;

    private static bool Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            string json = JsonSerializer.Serialize(new Settings
            {
                SnapshotLimit = SnapshotLimit,
                ShowActionHistoryOverlay = ShowActionHistoryOverlay,
                IncludeCardChoiceSnapshots = IncludeCardChoiceSnapshots,
                HasAcknowledgedFeatureAnnouncement = HasAcknowledgedFeatureAnnouncement,
            }, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ConfigPath, json);
            return true;
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"Failed to save config: {ex.Message}");
            return false;
        }
    }

    private sealed class Settings
    {
        public int SnapshotLimit { get; set; } = DefaultSnapshotLimit;
        public bool ShowActionHistoryOverlay { get; set; } = true;
        public bool IncludeCardChoiceSnapshots { get; set; } = true;
        public bool HasAcknowledgedFeatureAnnouncement { get; set; }
    }
}
