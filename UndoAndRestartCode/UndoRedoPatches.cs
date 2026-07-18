using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens.Settings;
using MegaCrit.Sts2.addons.mega_text;

namespace UndoAndRestartCode;

[HarmonyPatch]
internal static class UndoRedoPatches
{
    private static readonly Dictionary<UsePotionAction, ActionHistoryEntry?> PotionEntries = new();
    private static readonly Dictionary<DiscardPotionGameAction, ActionHistoryEntry?> DiscardPotionEntries = new();

    [HarmonyPatch(typeof(NGame), nameof(NGame._Input))]
    [HarmonyPrefix]
    private static void OnGameInput(InputEvent inputEvent)
    {
        if (ShouldIgnoreHotkeys())
        {
            return;
        }

        UndoInputBindings.EnsureRegistered();
        if (inputEvent is InputEventAction action && action.Pressed)
        {
            HandleInputAction(action);
            return;
        }

        if (inputEvent is not InputEventKey key || !key.Pressed || key.Echo)
        {
            return;
        }

        long keyCode = (long)key.Keycode;
        long physicalKeyCode = (long)key.PhysicalKeycode;
        if ((keyCode == UndoRedoManager.LeftArrowKeyCode || physicalKeyCode == UndoRedoManager.LeftArrowKeyCode) &&
            UndoInputBindings.ShouldHandleDefaultKeyFallback(UndoInputBindings.UndoAction))
        {
            if (UndoRedoManager.HandleUndoKey())
            {
                NGame.Instance?.GetViewport()?.SetInputAsHandled();
            }
        }
        else if ((keyCode == UndoRedoManager.RightArrowKeyCode || physicalKeyCode == UndoRedoManager.RightArrowKeyCode) &&
                 UndoInputBindings.ShouldHandleDefaultKeyFallback(UndoInputBindings.RedoAction))
        {
            if (UndoRedoManager.HandleRedoKey())
            {
                NGame.Instance?.GetViewport()?.SetInputAsHandled();
            }
        }
        else if ((keyCode == UndoRedoManager.QuickRestartKeyCode || physicalKeyCode == UndoRedoManager.QuickRestartKeyCode) &&
                 UndoInputBindings.ShouldHandleDefaultKeyFallback(UndoInputBindings.RestartAction))
        {
            if (FloorRestartService.HandleQuickRestartKey())
            {
                NGame.Instance?.GetViewport()?.SetInputAsHandled();
            }
        }
    }

    private static void HandleInputAction(InputEventAction action)
    {
        if (UndoInputBindings.IsUndoAction(action))
        {
            if (UndoRedoManager.HandleUndoKey())
            {
                NGame.Instance?.GetViewport()?.SetInputAsHandled();
            }
        }
        else if (UndoInputBindings.IsRedoAction(action))
        {
            if (UndoRedoManager.HandleRedoKey())
            {
                NGame.Instance?.GetViewport()?.SetInputAsHandled();
            }
        }
        else if (UndoInputBindings.IsRestartAction(action))
        {
            if (FloorRestartService.HandleQuickRestartKey())
            {
                NGame.Instance?.GetViewport()?.SetInputAsHandled();
            }
        }
    }

    private static bool ShouldIgnoreHotkeys()
    {
        Control? focused = NGame.Instance?.GetViewport()?.GuiGetFocusOwner();
        if (focused == null)
        {
            return false;
        }

        if (focused is LineEdit || focused is TextEdit)
        {
            return true;
        }

        for (Node? node = focused; node != null; node = node.GetParent())
        {
            string name = node.Name.ToString();
            if (name.Contains("Console", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("DevConsole", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    [HarmonyPatch(typeof(CombatManager), "Reset", new[] { typeof(bool) })]
    [HarmonyPostfix]
    private static void OnCombatReset()
    {
        UndoRedoManager.Reset();
    }

    [HarmonyPatch(typeof(NInputManager), nameof(NInputManager._Ready))]
    [HarmonyPostfix]
    private static void AfterInputManagerReady(NInputManager __instance)
    {
        UndoInputBindings.EnsureRegistered();
        UndoInputBindings.EnsureKeyboardEntriesWhenReady(__instance);
    }

    [HarmonyPatch(typeof(NInputManager), nameof(NInputManager.ResetToDefaults))]
    [HarmonyPostfix]
    private static void AfterInputResetToDefaults()
    {
        UndoInputBindings.EnsureRegistered();
    }

    [HarmonyPatch(typeof(NInputSettingsPanel), nameof(NInputSettingsPanel._Ready))]
    [HarmonyPrefix]
    private static void BeforeInputSettingsReady()
    {
        UndoInputBindings.EnsureRegistered();
    }

    [HarmonyPatch(typeof(NInputSettingsEntry), nameof(NInputSettingsEntry._Ready))]
    [HarmonyPrefix]
    private static void BeforeInputSettingsEntryReady()
    {
        UndoInputBindings.EnsureRegistered();
    }

    [HarmonyPatch(typeof(NInputSettingsEntry), nameof(NInputSettingsEntry._Ready))]
    [HarmonyPostfix]
    private static void AfterInputSettingsEntryReady(NInputSettingsEntry __instance)
    {
        string label = UndoInputBindings.LabelFor(__instance.InputName);
        if (string.IsNullOrEmpty(label))
        {
            return;
        }

        ReflectionUtil.GetField<MegaRichTextLabel>(__instance, "_inputLabel")!.Text = label;
        ReflectionUtil.Method(typeof(NInputSettingsEntry), "UpdateInput")?.Invoke(__instance, null);
    }

    [HarmonyPatch(typeof(ActionQueueSynchronizer), nameof(ActionQueueSynchronizer.SetCombatState))]
    [HarmonyPostfix]
    private static void AfterCombatSynchronizerStateChanged(ActionSynchronizerCombatState combatState)
    {
        if (combatState == ActionSynchronizerCombatState.PlayPhase)
        {
            UndoRedoManager.RequestPlayerControlReadyCapture("ActionQueueSynchronizer.PlayPhase");
        }
    }

    [HarmonyPatch(typeof(ActionExecutor), "AfterActionFinished")]
    [HarmonyPostfix]
    private static void AfterQueuedActionFinished(GameAction action)
    {
        UndoRedoManager.CaptureCompletedPlayerAction(action);
    }

    [HarmonyPatch(typeof(PlayCardAction), "ExecuteAction")]
    [HarmonyPrefix]
    private static void BeforePlayCard()
    {
        UndoRedoManager.CaptureBeforeAction("PlayCardAction");
    }

    [HarmonyPatch(typeof(PlayCardAction), "ExecuteAction")]
    [HarmonyPostfix]
    private static void AfterPlayCard(PlayCardAction __instance, ref Task __result)
    {
        __result = UndoRedoManager.CaptureAfterActionAsync(__result, "PlayCardAction", CreateCardEntry(__instance));
    }

    [HarmonyPatch(typeof(UsePotionAction), "ExecuteAction")]
    [HarmonyPrefix]
    private static void BeforeUsePotion(UsePotionAction __instance)
    {
        if (__instance.WasEnqueuedInCombat)
        {
            PotionEntries[__instance] = CreatePotionEntry(__instance);
            UndoRedoManager.CaptureBeforeAction("UsePotionAction");
        }
    }

    [HarmonyPatch(typeof(UsePotionAction), "ExecuteAction")]
    [HarmonyPostfix]
    private static void AfterUsePotion(UsePotionAction __instance, ref Task __result)
    {
        if (__instance.WasEnqueuedInCombat)
        {
            PotionEntries.TryGetValue(__instance, out ActionHistoryEntry? entry);
            PotionEntries.Remove(__instance);
            __result = UndoRedoManager.CaptureAfterActionAsync(__result, "UsePotionAction", entry);
        }
    }

    [HarmonyPatch(typeof(DiscardPotionGameAction), "ExecuteAction")]
    [HarmonyPrefix]
    private static void BeforeDiscardPotion(DiscardPotionGameAction __instance)
    {
        if (__instance.WasEnqueuedInCombat)
        {
            DiscardPotionEntries[__instance] = CreateDiscardPotionEntry(__instance);
            UndoRedoManager.CaptureBeforeAction("DiscardPotionGameAction");
        }
    }

    [HarmonyPatch(typeof(DiscardPotionGameAction), "ExecuteAction")]
    [HarmonyPostfix]
    private static void AfterDiscardPotion(DiscardPotionGameAction __instance, ref Task __result)
    {
        if (__instance.WasEnqueuedInCombat)
        {
            DiscardPotionEntries.TryGetValue(__instance, out ActionHistoryEntry? entry);
            DiscardPotionEntries.Remove(__instance);
            __result = UndoRedoManager.CaptureAfterActionAsync(__result, "DiscardPotionGameAction", entry);
        }
    }

    [HarmonyPatch(typeof(EndPlayerTurnAction), "ExecuteAction")]
    [HarmonyPrefix]
    private static void BeforeEndTurn()
    {
        UndoRedoManager.CaptureBeforeAction("EndPlayerTurnAction");
        UndoRedoManager.QueueTurnTransitionEntry();
    }

    [HarmonyPatch(typeof(EndPlayerTurnAction), "ExecuteAction")]
    [HarmonyPostfix]
    private static void AfterEndTurn()
    {
        // 이 액션은 플레이어의 턴 종료 준비만 표시함.
        // 여기서 캡처하면 다음 플레이어 조작 가능 스냅샷 전에 redo가 걸리는 비플레이 중간 지점이 생김.
    }

    private static ActionHistoryEntry? CreateCardEntry(PlayCardAction action)
    {
        try
        {
            CardModel? card = action.NetCombatCard.ToCardModelOrNull();
            if (card == null)
            {
                return null;
            }

            string target = action.Target?.LogName ?? UndoText.NoTarget;
            return new ActionHistoryEntry(ActionHistoryEntryKind.Card, card.Title, target, GetRoundNumber(), card: card);
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"Failed to create card stack entry: {ex.Message}");
            return null;
        }
    }

    private static ActionHistoryEntry? CreatePotionEntry(UsePotionAction action)
    {
        try
        {
            PotionModel? potion = action.Player.GetPotionAtSlotIndex((int)action.PotionIndex);
            if (potion == null)
            {
                return null;
            }

            string target = GetTargetName(action.TargetId);
            return new ActionHistoryEntry(ActionHistoryEntryKind.Potion, potion.Title.GetFormattedText() ?? potion.Id.Entry, target, GetRoundNumber(), potion: potion);
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"Failed to create potion stack entry: {ex.Message}");
            return null;
        }
    }

    private static ActionHistoryEntry? CreateDiscardPotionEntry(DiscardPotionGameAction action)
    {
        try
        {
            Player? player = ReflectionUtil.GetField<Player>(action, "_player");
            uint slotIndex = ReflectionUtil.GetField<uint>(action, "_potionSlotIndex");
            PotionModel? potion = player?.GetPotionAtSlotIndex((int)slotIndex);
            if (potion == null)
            {
                return null;
            }

            return new ActionHistoryEntry(ActionHistoryEntryKind.DiscardPotion, potion.Title.GetFormattedText() ?? potion.Id.Entry, UndoText.DiscardSlot(slotIndex), GetRoundNumber(), potion: potion);
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"Failed to create discard potion stack entry: {ex.Message}");
            return null;
        }
    }

    private static int GetRoundNumber()
    {
        return CombatManager.Instance.DebugOnlyGetState()?.RoundNumber ?? 1;
    }

    private static string GetTargetName(uint? targetId)
    {
        CombatState? state = CombatManager.Instance.DebugOnlyGetState();
        if (state == null || targetId == null)
        {
            return UndoText.NoTarget;
        }

        foreach (string fieldName in new[] { "_allies", "_enemies", "_escapedCreatures" })
        {
            List<Creature>? creatures = ReflectionUtil.GetField<List<Creature>>(state, fieldName);
            Creature? creature = creatures?.FirstOrDefault(creature => creature.CombatId == targetId.Value);
            if (creature != null)
            {
                return creature.LogName;
            }
        }

        return UndoText.NoTarget;
    }
}
