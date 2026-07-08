using Godot;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens.Settings;

namespace UndoAndRestartCode;

internal static class UndoInputBindings
{
    public static readonly StringName UndoAction = "undo_and_restart_undo";
    public static readonly StringName RedoAction = "undo_and_restart_redo";
    public static readonly StringName RestartAction = "undo_and_restart_restart";

    private static bool _registrationAttempted;
    private static bool _waitingForInputMap;

    public static void EnsureRegistered()
    {
        if (!_registrationAttempted)
        {
            AddKeyboardAction(UndoAction);
            AddKeyboardAction(RedoAction);
            AddKeyboardAction(RestartAction);
            AddSettingsTitles();
            _registrationAttempted = true;
        }

        EnsureKeyboardEntries();
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

        Dictionary<StringName, Key>? map = ReflectionUtil.GetField<Dictionary<StringName, Key>>(
            inputManager,
            "_keyboardInputMap");
        if (map == null || map.Count == 0)
        {
            return true;
        }

        return !map.TryGetValue(action, out Key boundKey) || boundKey == Key.None;
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
                Dictionary<StringName, Key>? map = ReflectionUtil.GetField<Dictionary<StringName, Key>>(
                    inputManager,
                    "_keyboardInputMap");
                if (map?.Count > 0)
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
        if (NInputManager.remappableKeyboardInputs is List<StringName> keyboardInputs &&
            !keyboardInputs.Contains(action))
        {
            keyboardInputs.Add(action);
        }
    }

    private static void AddSettingsTitles()
    {
        Dictionary<StringName, string>? titleMap =
            ReflectionUtil.GetStaticField<Dictionary<StringName, string>>(
                typeof(NInputSettingsEntry),
                "_commandToLocTitle");
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
        Dictionary<StringName, Key>? map = ReflectionUtil.GetField<Dictionary<StringName, Key>>(
            inputManager,
            "_keyboardInputMap");
        if (map == null || map.Count == 0)
        {
            return;
        }

        bool changed = false;
        changed |= AddUnboundEntry(map, UndoAction);
        changed |= AddUnboundEntry(map, RedoAction);
        changed |= AddUnboundEntry(map, RestartAction);
        if (changed)
        {
            ReflectionUtil.Method(typeof(NInputManager), "SaveKeyboardInputMapping")?.Invoke(inputManager, null);
        }
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
