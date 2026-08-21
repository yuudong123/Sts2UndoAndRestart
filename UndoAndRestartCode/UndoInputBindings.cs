using Godot;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.Settings;

namespace UndoAndRestartCode;

internal static class UndoInputBindings
{
    public static readonly StringName UndoAction = "undo_and_restart_undo";
    public static readonly StringName RedoAction = "undo_and_restart_redo";
    public static readonly StringName RestartAction = "undo_and_restart_restart";

    private static bool _waitingForInputMap;

    public static void EnsureRegistered()
    {
        AddKeyboardAction(UndoAction);
        AddKeyboardAction(RedoAction);
        AddKeyboardAction(RestartAction);
        AddSettingsTitles();
        EnsureKeyboardEntries();
    }

    public static void EnsureSettingsPanelEntries(NInputSettingsPanel panel)
    {
        EnsureRegistered();

        HashSet<StringName> existingActions = panel.Content
            .GetChildren()
            .OfType<NInputSettingsEntry>()
            .Select(entry => entry.InputName)
            .ToHashSet();

        int addedCount = 0;
        foreach (StringName action in GetActions())
        {
            if (existingActions.Contains(action))
            {
                continue;
            }

            NInputSettingsEntry entry = NInputSettingsEntry.Create(action);
            entry.Connect(
                NClickableControl.SignalName.Released,
                Callable.From<NClickableControl>(_ =>
                {
                    ReflectionUtil.Method(
                            typeof(NInputSettingsPanel),
                            "SetAsListeningEntry",
                            typeof(NInputSettingsEntry))
                        ?.Invoke(panel, new object[] { entry });
                }));
            panel.Content.AddChild(entry);
            addedCount++;
        }

        if (addedCount > 0)
        {
            ReflectionUtil.Method(typeof(NInputSettingsPanel), "UpdateNavigation")
                ?.Invoke(panel, null);
            MainFile.Logger.Info(
                $"Restored {addedCount} missing input settings entries.");
        }
    }

    public static void EnsureKeyboardEntriesWhenReady(NInputManager inputManager)
    {
        if (_waitingForInputMap)
        {
            return;
        }

        _waitingForInputMap = true;
        TaskHelper.RunSafely(WaitForKeyboardMap(inputManager));
    }

    public static bool ShouldHandleDefaultKeyFallback(StringName action)
    {
        NInputManager? inputManager = NInputManager.Instance;
        if (inputManager == null)
        {
            return true;
        }

        return inputManager.GetCurrentHotkey(action) == Key.None;
    }

    public static bool IsUndoAction(InputEventAction action)
    {
        return action.Action == UndoAction;
    }

    public static bool IsRedoAction(InputEventAction action)
    {
        return action.Action == RedoAction;
    }

    public static bool IsRestartAction(InputEventAction action)
    {
        return action.Action == RestartAction;
    }

    public static string LabelFor(StringName action)
    {
        if (action == UndoAction)
        {
            return UndoText.InputUndo;
        }

        if (action == RedoAction)
        {
            return UndoText.InputRedo;
        }

        if (action == RestartAction)
        {
            return UndoText.InputRestart;
        }

        return "";
    }

    private static async Task WaitForKeyboardMap(NInputManager inputManager)
    {
        try
        {
            for (int attempt = 0; attempt < 120; attempt++)
            {
                if (TryGetKeyboardMaps(
                        inputManager,
                        out Dictionary<StringName, Key> mouseKeyboardMap,
                        out Dictionary<StringName, Key> keyboardOnlyMap) &&
                    mouseKeyboardMap.Count > 0 &&
                    keyboardOnlyMap.Count > 0)
                {
                    EnsureKeyboardEntries(inputManager);
                    return;
                }

                if (Engine.GetMainLoop() is SceneTree sceneTree)
                {
                    await sceneTree.ToSignal(sceneTree, SceneTree.SignalName.ProcessFrame);
                }
                else
                {
                    await Task.Delay(16);
                }
            }
        }
        finally
        {
            _waitingForInputMap = false;
        }
    }

    private static void AddKeyboardAction(StringName action)
    {
        AddRemappableInput(NInputManager.remappableMKbInputs, action);
        AddRemappableInput(NInputManager.remappableKbOnlyInputs, action);
    }

    private static void AddRemappableInput(
        IReadOnlyList<StringName> remappableInputs,
        StringName action)
    {
        if (remappableInputs is ICollection<StringName> mutableInputs &&
            !mutableInputs.Contains(action))
        {
            mutableInputs.Add(action);
        }
    }

    private static IEnumerable<StringName> GetActions()
    {
        yield return UndoAction;
        yield return RedoAction;
        yield return RestartAction;
    }

    private static void AddSettingsTitles()
    {
        Dictionary<StringName, string>? titleMap =
            ReflectionUtil.GetStaticField<Dictionary<StringName, string>>(
                typeof(NInputSettingsEntry),
                "commandToLocTitle");
        if (titleMap == null)
        {
            return;
        }

        // 바닐라 _Ready에서는 기존 번역 키를 재사용하고 postfix에서 표시 문구만 교체함.
        // 누락된 번역 키로 인한 크래시를 피하기 위함.
        titleMap[UndoAction] = "left";
        titleMap[RedoAction] = "right";
        titleMap[RestartAction] = "endTurn";
    }

    private static void EnsureKeyboardEntries()
    {
        NInputManager? inputManager = NInputManager.Instance;
        if (inputManager != null)
        {
            EnsureKeyboardEntries(inputManager);
        }
    }

    private static void EnsureKeyboardEntries(NInputManager inputManager)
    {
        if (!TryGetKeyboardMaps(
                inputManager,
                out Dictionary<StringName, Key> mouseKeyboardMap,
                out Dictionary<StringName, Key> keyboardOnlyMap) ||
            mouseKeyboardMap.Count == 0 ||
            keyboardOnlyMap.Count == 0)
        {
            return;
        }

        bool changed = false;
        changed |= AddUnboundEntry(mouseKeyboardMap, UndoAction);
        changed |= AddUnboundEntry(mouseKeyboardMap, RedoAction);
        changed |= AddUnboundEntry(mouseKeyboardMap, RestartAction);
        changed |= AddUnboundEntry(keyboardOnlyMap, UndoAction);
        changed |= AddUnboundEntry(keyboardOnlyMap, RedoAction);
        changed |= AddUnboundEntry(keyboardOnlyMap, RestartAction);
        if (changed)
        {
            ReflectionUtil.Method(typeof(NInputManager), "SaveMKbInputMapping")?.Invoke(inputManager, null);
            ReflectionUtil.Method(typeof(NInputManager), "SaveFKbInputMapping")?.Invoke(inputManager, null);
        }
    }

    private static bool TryGetKeyboardMaps(
        NInputManager inputManager,
        out Dictionary<StringName, Key> mouseKeyboardMap,
        out Dictionary<StringName, Key> keyboardOnlyMap)
    {
        Dictionary<StringName, Key>? mouseKeyboardMapCandidate =
            ReflectionUtil.GetField<Dictionary<StringName, Key>>(
            inputManager,
            "_mKbInputMap");
        Dictionary<StringName, Key>? keyboardOnlyMapCandidate =
            ReflectionUtil.GetField<Dictionary<StringName, Key>>(
            inputManager,
            "_fKbInputMap");
        mouseKeyboardMap = mouseKeyboardMapCandidate!;
        keyboardOnlyMap = keyboardOnlyMapCandidate!;
        return mouseKeyboardMapCandidate != null && keyboardOnlyMapCandidate != null;
    }

    private static bool AddUnboundEntry(Dictionary<StringName, Key> map, StringName action)
    {
        if (map.ContainsKey(action))
        {
            return false;
        }

        map[action] = Key.None;
        return true;
    }
}
