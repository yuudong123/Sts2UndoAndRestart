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
    public static bool EnableMouseSideButtons { get; private set; }
    public static bool EnableScreenButtons { get; private set; } = true;
    /// <summary>Dragged Undo button position as a fraction of the viewport; null = beside the energy orb.</summary>
    public static Vector2? ScreenUndoButtonPosition { get; private set; }
    public static Vector2? ScreenRedoButtonPosition { get; private set; }
    /// <summary>Dragged top-bar restart button X as a fraction of the top bar width; null = left of the map button.</summary>
    public static float? ScreenRestartButtonX { get; private set; }
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
            EnableMouseSideButtons = settings?.EnableMouseSideButtons ?? false;
            EnableScreenButtons = settings?.EnableScreenButtons ?? true;
            ScreenUndoButtonPosition = ToVector(settings?.ScreenUndoButtonPosition);
            ScreenRedoButtonPosition = ToVector(settings?.ScreenRedoButtonPosition);
            ScreenRestartButtonX = settings?.ScreenRestartButtonX is { } x && float.IsFinite(x) ? Math.Clamp(x, 0f, 1f) : null;
            HasAcknowledgedFeatureAnnouncement =
                settings?.HasAcknowledgedFeatureAnnouncement ?? false;
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"Failed to load config, using defaults: {ex.Message}");
            SnapshotLimit = DefaultSnapshotLimit;
            ShowActionHistoryOverlay = true;
            IncludeCardChoiceSnapshots = true;
            EnableMouseSideButtons = false;
            EnableScreenButtons = true;
            ScreenUndoButtonPosition = null;
            ScreenRedoButtonPosition = null;
            ScreenRestartButtonX = null;
            HasAcknowledgedFeatureAnnouncement = false;
        }
    }

    public static bool SaveSettings(
        int snapshotLimit,
        bool showActionHistoryOverlay,
        bool includeCardChoiceSnapshots,
        bool enableMouseSideButtons,
        bool enableScreenButtons)
    {
        int oldSnapshotLimit = SnapshotLimit;
        bool oldShowActionHistoryOverlay = ShowActionHistoryOverlay;
        bool oldIncludeCardChoiceSnapshots = IncludeCardChoiceSnapshots;
        bool oldEnableMouseSideButtons = EnableMouseSideButtons;
        bool oldEnableScreenButtons = EnableScreenButtons;
        SnapshotLimit = Math.Max(0, snapshotLimit);
        ShowActionHistoryOverlay = showActionHistoryOverlay;
        IncludeCardChoiceSnapshots = includeCardChoiceSnapshots;
        EnableMouseSideButtons = enableMouseSideButtons;
        EnableScreenButtons = enableScreenButtons;
        if (Save())
        {
            return true;
        }

        SnapshotLimit = oldSnapshotLimit;
        ShowActionHistoryOverlay = oldShowActionHistoryOverlay;
        IncludeCardChoiceSnapshots = oldIncludeCardChoiceSnapshots;
        EnableMouseSideButtons = oldEnableMouseSideButtons;
        EnableScreenButtons = oldEnableScreenButtons;
        return false;
    }

    public static bool SaveScreenButtonPositions(Vector2? undo, Vector2? redo)
    {
        Vector2? oldUndo = ScreenUndoButtonPosition;
        Vector2? oldRedo = ScreenRedoButtonPosition;
        ScreenUndoButtonPosition = undo;
        ScreenRedoButtonPosition = redo;
        if (Save())
        {
            return true;
        }

        ScreenUndoButtonPosition = oldUndo;
        ScreenRedoButtonPosition = oldRedo;
        return false;
    }

    public static bool SaveScreenRestartButtonX(float? x)
    {
        float? old = ScreenRestartButtonX;
        ScreenRestartButtonX = x;
        if (Save())
        {
            return true;
        }

        ScreenRestartButtonX = old;
        return false;
    }

    public static bool ResetScreenButtonPositions()
    {
        float? oldRestart = ScreenRestartButtonX;
        ScreenRestartButtonX = null;
        if (SaveScreenButtonPositions(null, null))
        {
            return true;
        }

        ScreenRestartButtonX = oldRestart;
        return false;
    }

    private static Vector2? ToVector(float[]? value)
    {
        return value is { Length: 2 } && float.IsFinite(value[0]) && float.IsFinite(value[1])
            ? new Vector2(Math.Clamp(value[0], 0f, 1f), Math.Clamp(value[1], 0f, 1f))
            : null;
    }

    private static float[]? FromVector(Vector2? value)
    {
        return value is { } v ? new[] { v.X, v.Y } : null;
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
                EnableMouseSideButtons = EnableMouseSideButtons,
                EnableScreenButtons = EnableScreenButtons,
                ScreenUndoButtonPosition = FromVector(ScreenUndoButtonPosition),
                ScreenRedoButtonPosition = FromVector(ScreenRedoButtonPosition),
                ScreenRestartButtonX = ScreenRestartButtonX,
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
        public bool EnableMouseSideButtons { get; set; }
        public bool EnableScreenButtons { get; set; } = true;
        public float[]? ScreenUndoButtonPosition { get; set; }
        public float[]? ScreenRedoButtonPosition { get; set; }
        public float? ScreenRestartButtonX { get; set; }
        public bool HasAcknowledgedFeatureAnnouncement { get; set; }
    }
}
