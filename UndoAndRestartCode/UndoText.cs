using System.Globalization;
using System.Reflection;
using System.Text.Json;
using HarmonyLib;
using MegaCrit.Sts2.Core.Localization;

namespace UndoAndRestartCode;

internal static class UndoText
{
    private const string EnglishLanguage = "eng";
    private static readonly Dictionary<string, string> EnglishTexts = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> CurrentTexts = new(StringComparer.Ordinal);
    private static string? _loadedLanguage;

    public static string ActionHistory => Get("action_history");
    public static string Close => Get("close");
    public static string SnapshotLimitTitle => Get("snapshots_to_keep");
    public static string LimitRangeText => Get("no_minimum");
    public static string SnapshotLimitHint => Format("snapshot_limit_hint", LimitRangeText);
    public static string SnapshotLimitWarning => Get("snapshot_limit_warning");
    public static string ShowHistoryTab => Get("show_history_tab");
    public static string IncludeCardChoiceSnapshots => Get("include_card_choice_snapshots");
    public static string Save => Get("save");
    public static string SaveFailed => Get("save_failed");
    public static string NumberOnly => Get("number_only");
    public static string InputUndo => Get("input_undo");
    public static string InputRedo => Get("input_redo");
    public static string InputRestart => Get("input_restart");
    public static string TurnStart => Get("turn_start");
    public static string NoTarget => Get("no_target");
    public static string RestartCombat => Get("restart_combat");
    public static string RestartCombatTooltip => Get("restart_combat_tooltip");
    public static string InitialStateTooltip => Get("initial_state_tooltip");
    public static string Redo => Get("redo");
    public static string CardChoice => Get("card_choice");
    public static string NoCardSelected => Get("no_card_selected");
    public static string FeatureAnnouncementTitle => Get("feature_announcement_title");
    public static string FeatureAnnouncementBody => Get("feature_announcement_body");
    public static string FeatureAnnouncementConfirm => Get("feature_announcement_confirm");

    public static string Turn(int turnNumber)
    {
        return Format("turn", Math.Max(1, turnNumber));
    }

    public static string DiscardSlot(uint slotIndex)
    {
        return Format("discard_slot", slotIndex + 1);
    }

    public static string InitialState(bool current)
    {
        return current
            ? Get("initial_state_current")
            : Get("initial_state");
    }

    public static string Snapshot(int snapshotIndex)
    {
        return Format("snapshot", snapshotIndex + 1);
    }

    public static string Kind(ActionHistoryEntryKind kind)
    {
        return kind switch
        {
            ActionHistoryEntryKind.Card => Get("kind_card"),
            ActionHistoryEntryKind.CardChoice => CardChoice,
            ActionHistoryEntryKind.Potion => Get("kind_potion"),
            ActionHistoryEntryKind.DiscardPotion => Get("kind_discard"),
            ActionHistoryEntryKind.TurnStart => Get("kind_turn"),
            _ => Get("kind_action"),
        };
    }

    public static void Reload(string? language = null)
    {
        string resolvedLanguage = language ??
                                  LocManager.Instance?.Language ??
                                  EnglishLanguage;
        EnglishTexts.Clear();
        CurrentTexts.Clear();
        LoadLanguageFile(EnglishLanguage, EnglishTexts, required: true);
        if (!string.Equals(resolvedLanguage, EnglishLanguage, StringComparison.OrdinalIgnoreCase))
        {
            LoadLanguageFile(resolvedLanguage, CurrentTexts, required: false);
        }

        _loadedLanguage = resolvedLanguage;
        MainFile.Logger.Info(
            $"Loaded undo UI language '{resolvedLanguage}' with {CurrentTexts.Count} localized entries.");
    }

    private static string Get(string key)
    {
        EnsureLoaded();
        if (CurrentTexts.TryGetValue(key, out string? localized))
        {
            return localized;
        }

        if (EnglishTexts.TryGetValue(key, out string? english))
        {
            return english;
        }

        MainFile.Logger.Warn($"Missing undo UI localization key: {key}");
        return key;
    }

    private static string Format(string key, params object[] args)
    {
        return string.Format(CultureInfo.CurrentCulture, Get(key), args);
    }

    private static void EnsureLoaded()
    {
        string language = LocManager.Instance?.Language ?? EnglishLanguage;
        if (!string.Equals(_loadedLanguage, language, StringComparison.OrdinalIgnoreCase))
        {
            Reload(language);
        }
    }

    private static void LoadLanguageFile(
        string language,
        Dictionary<string, string> destination,
        bool required)
    {
        string path = Path.Combine(GetLanguageDirectory(), $"{language}.json");
        if (!File.Exists(path))
        {
            if (required)
            {
                MainFile.Logger.Error($"Required undo UI language file is missing: {path}");
            }
            else
            {
                MainFile.Logger.Info(
                    $"Undo UI language file '{language}.json' was not found; using English fallback.");
            }

            return;
        }

        try
        {
            Dictionary<string, string>? loaded =
                JsonSerializer.Deserialize<Dictionary<string, string>>(
                    File.ReadAllText(path));
            if (loaded == null)
            {
                return;
            }

            foreach ((string key, string value) in loaded)
            {
                destination[key] = value;
            }
        }
        catch (Exception ex)
        {
            MainFile.Logger.Error($"Failed to load undo UI language file {path}: {ex}");
        }
    }

    private static string GetLanguageDirectory()
    {
        string assemblyPath = Assembly.GetExecutingAssembly().Location;
        return Path.Combine(Path.GetDirectoryName(assemblyPath)!, "language");
    }
}

[HarmonyPatch(typeof(LocManager), nameof(LocManager.SetLanguage))]
internal static class UndoLocaleChangedPatch
{
    private static void Postfix(string language)
    {
        UndoText.Reload(language);
        ActionHistoryOverlay.Refresh();
    }
}
