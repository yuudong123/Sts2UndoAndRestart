using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Debug;
using MegaCrit.Sts2.Core.Nodes.Screens.Settings;
using MegaCrit.Sts2.Core.Nodes.Vfx;
using MegaCrit.Sts2.Core.Nodes.Vfx.Cards;
using MegaCrit.Sts2.addons.mega_text;

namespace UndoAndRestartCode;

[HarmonyPatch]
internal static class UndoRedoPatches
{
    [HarmonyPatch(typeof(NGame), nameof(NGame._Input))]
    [HarmonyPrefix]
    private static void OnGameInput(InputEvent inputEvent)
    {
        // NGame._Input receives every event, including each mouse motion. Reject
        // non-press events before walking the focus tree or touching input maps.
        if (!inputEvent.IsPressed() ||
            inputEvent.IsEcho() ||
            inputEvent is not (InputEventAction or InputEventMouseButton or InputEventKey) ||
            ShouldIgnoreHotkeys())
        {
            return;
        }

        UndoInputBindings.EnsureRegistered();
        switch (inputEvent)
        {
            case InputEventAction action:
                HandleInputAction(action);
                break;
            case InputEventMouseButton mouseButton:
                HandleMouseSideButton(mouseButton);
                break;
            case InputEventKey key:
                HandleDefaultKeyFallback(key);
                break;
        }
    }

    private static void HandleDefaultKeyFallback(InputEventKey key)
    {
        // Default keys apply only while the player has not bound the action
        // (see ShouldHandleDefaultKeyFallback for the keyboard-only rule).
        if (IsKey(key, Key.Left) &&
            UndoInputBindings.ShouldHandleDefaultKeyFallback(UndoInputBindings.UndoAction, Key.Left))
        {
            MarkInputHandledIf(UndoRedoManager.HandleUndoKey());
        }
        else if (IsKey(key, Key.Right) &&
                 UndoInputBindings.ShouldHandleDefaultKeyFallback(UndoInputBindings.RedoAction, Key.Right))
        {
            MarkInputHandledIf(UndoRedoManager.HandleRedoKey());
        }
        else if (IsKey(key, Key.F5) &&
                 UndoInputBindings.ShouldHandleDefaultKeyFallback(UndoInputBindings.RestartAction, Key.F5))
        {
            MarkInputHandledIf(FloorRestartService.HandleQuickRestartKey());
        }
    }

    private static bool IsKey(InputEventKey inputEvent, Key key)
    {
        return inputEvent.Keycode == key || inputEvent.PhysicalKeycode == key;
    }

    private static void HandleMouseSideButton(InputEventMouseButton inputEvent)
    {
        if (!UndoAndRestartConfig.EnableMouseSideButtons)
        {
            return;
        }

        switch (inputEvent.ButtonIndex)
        {
            case MouseButton.Xbutton1:
                MarkInputHandledIf(UndoRedoManager.HandleUndoKey());
                break;
            case MouseButton.Xbutton2:
                MarkInputHandledIf(UndoRedoManager.HandleRedoKey());
                break;
        }
    }

    private static void HandleInputAction(InputEventAction action)
    {
        if (UndoInputBindings.IsUndoAction(action))
        {
            MarkInputHandledIf(UndoRedoManager.HandleUndoKey());
        }
        else if (UndoInputBindings.IsRedoAction(action))
        {
            MarkInputHandledIf(UndoRedoManager.HandleRedoKey());
        }
        else if (UndoInputBindings.IsRestartAction(action))
        {
            MarkInputHandledIf(FloorRestartService.HandleQuickRestartKey());
        }
    }

    private static void MarkInputHandledIf(bool accepted)
    {
        if (accepted)
        {
            NGame.Instance?.GetViewport()?.SetInputAsHandled();
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
            if (node is NDevConsole)
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

    [HarmonyPatch(typeof(NPlayerHand), "OnSelectModeConfirmButtonPressed")]
    [HarmonyPrefix]
    private static bool BeforeHandSelectionConfirmed(NPlayerHand __instance)
    {
        TaskCompletionSource<IEnumerable<CardModel>>? completionSource =
            ReflectionUtil.GetField<TaskCompletionSource<IEnumerable<CardModel>>>(
                __instance,
                "_selectionCompletionSource");
        bool isCurrentSelection = __instance.IsInCardSelection &&
                                  completionSource?.Task.IsCompleted == false;
        if (!isCurrentSelection)
        {
            MainFile.Logger.Warn(
                "Ignored a stale hand-selection confirmation after timeline restoration.");
        }

        return isCurrentSelection;
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

    [HarmonyPatch(typeof(NInputSettingsPanel), nameof(NInputSettingsPanel._Ready))]
    [HarmonyPostfix]
    private static void AfterInputSettingsReady(NInputSettingsPanel __instance)
    {
        UndoInputBindings.EnsureSettingsPanelEntries(__instance);
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

        MegaLabel? inputLabel = ReflectionUtil.GetField<MegaLabel>(__instance, "_inputLabel");
        if (inputLabel == null)
        {
            MainFile.Logger.Warn(
                $"Failed to localize input settings label for {__instance.InputName}.");
            return;
        }

        inputLabel.SetTextAutoSize(label);
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
        CardChoiceCheckpointService.OnActionFinished(action);
        UndoRedoManager.CaptureCompletedPlayerAction(action);
    }

    [HarmonyPatch(
        typeof(NCardPlayQueue),
        nameof(NCardPlayQueue.OnLocalCardPlayed))]
    [HarmonyPostfix]
    private static void AfterLocalCardAddedToPlayQueue(
        NCardPlayQueue __instance,
        PlayCardAction action)
    {
        CardChoiceCheckpointService.CompleteReplayCardQueueAnimation(
            __instance,
            action);
    }

    [HarmonyPatch(
        typeof(CardPileCmd),
        nameof(CardPileCmd.AddDuringManualCardPlay))]
    [HarmonyPostfix]
    private static void AfterReplayCardMovedToPlayPile(
        CardModel card,
        ref Task __result)
    {
        if (CardChoiceCheckpointService.IsReplayingCard(card))
        {
            __result =
                CardChoiceCheckpointService.CompleteReplayCardPlayPileAnimationAsync(
                    __result,
                    card);
        }
    }

    [HarmonyPatch(
        typeof(Cmd),
        nameof(Cmd.Wait),
        new[] { typeof(float), typeof(bool) })]
    [HarmonyPrefix]
    private static bool BeforeReplayVisualWait(ref Task __result)
    {
        if (!CardChoiceCheckpointService.IsReplayingBeforeTargetChoice)
        {
            return true;
        }

        __result = Task.CompletedTask;
        return false;
    }

    [HarmonyPatch(
        typeof(Cmd),
        nameof(Cmd.Wait),
        new[] { typeof(float), typeof(CancellationToken), typeof(bool) })]
    [HarmonyPrefix]
    private static bool BeforeReplayVisualCancelableWait(ref Task __result)
    {
        if (!CardChoiceCheckpointService.IsReplayingBeforeTargetChoice)
        {
            return true;
        }

        __result = Task.CompletedTask;
        return false;
    }

    [HarmonyPatch(
        typeof(Cmd),
        nameof(Cmd.CustomScaledWait),
        new[]
        {
            typeof(float),
            typeof(float),
            typeof(bool),
            typeof(CancellationToken),
        })]
    [HarmonyPrefix]
    private static bool BeforeReplayVisualScaledWait(ref Task __result)
    {
        if (!CardChoiceCheckpointService.IsReplayingBeforeTargetChoice)
        {
            return true;
        }

        __result = Task.CompletedTask;
        return false;
    }

    [HarmonyPatch(typeof(NItemThrowVfx), nameof(NItemThrowVfx._Ready))]
    [HarmonyPrefix]
    private static bool BeforeReplayItemThrowReady(NItemThrowVfx __instance)
    {
        if (!CardChoiceCheckpointService.IsReplayingBeforeTargetChoice)
        {
            return true;
        }

        __instance.Visible = false;
        __instance.QueueFreeSafely();
        return false;
    }

    [HarmonyPatch(typeof(NCardExhaustQuickVfx), nameof(NCardExhaustQuickVfx.PlayAnimation))]
    [HarmonyPostfix]
    private static void TrackQuickExhaustAnimation(
        NCardExhaustQuickVfx __instance,
        ref Task __result)
    {
        __result = TransientCardVfxCleanup.TrackAsync(__result, __instance);
    }

    [HarmonyPatch(typeof(NCardExhaustVfx), nameof(NCardExhaustVfx.PlayAnimation))]
    [HarmonyPostfix]
    private static void TrackExhaustAnimation(
        NCardExhaustVfx __instance,
        ref Task __result)
    {
        __result = TransientCardVfxCleanup.TrackAsync(__result, __instance);
    }

    [HarmonyPatch(typeof(NCardExhaustVfx), "DelayedFree")]
    [HarmonyPostfix]
    private static void TrackExhaustDelayedFree(
        NCardExhaustVfx __instance,
        ref Task __result)
    {
        __result = TransientCardVfxCleanup.TrackAsync(__result, __instance);
    }

    [HarmonyPatch(typeof(NCardRemoveVfx), "PlayAnimation")]
    [HarmonyPostfix]
    private static void TrackRemoveAnimation(
        NCardRemoveVfx __instance,
        ref Task __result)
    {
        __result = TransientCardVfxCleanup.TrackAsync(__result, __instance);
    }

    [HarmonyPatch(typeof(NCardTransformShineVfx), nameof(NCardTransformShineVfx.PlayUntilCardUpdate))]
    [HarmonyPostfix]
    private static void TrackTransformUntilUpdate(
        NCardTransformShineVfx __instance,
        ref Task __result)
    {
        __result = TransientCardVfxCleanup.TrackAsync(__result, __instance);
    }

    [HarmonyPatch(typeof(NCardTransformShineVfx), nameof(NCardTransformShineVfx.PlayShineAndReveal))]
    [HarmonyPostfix]
    private static void TrackTransformReveal(
        NCardTransformShineVfx __instance,
        ref Task __result)
    {
        __result = TransientCardVfxCleanup.TrackAsync(__result, __instance);
    }

    [HarmonyPatch(typeof(NCardTransformShineVfx), "AnimatingCardScale")]
    [HarmonyPostfix]
    private static void TrackTransformScale(
        NCardTransformShineVfx __instance,
        ref Task __result)
    {
        __result = TransientCardVfxCleanup.TrackAsync(__result, __instance);
    }

    [HarmonyPatch(typeof(NCardFlyVfx), "PlayAnim")]
    [HarmonyPostfix]
    private static void TrackCardFlyAnimation(
        NCardFlyVfx __instance,
        ref Task __result)
    {
        __result = TransientCardVfxCleanup.TrackAsync(__result, __instance);
    }

    [HarmonyPatch(typeof(NCardFlyShuffleVfx), "PlayAnim")]
    [HarmonyPostfix]
    private static void TrackCardShuffleAnimation(
        NCardFlyShuffleVfx __instance,
        ref Task __result)
    {
        __result = TransientCardVfxCleanup.TrackAsync(__result, __instance);
    }

    [HarmonyPatch(typeof(NCardTrailVfx), nameof(NCardTrailVfx.FadeOut))]
    [HarmonyPostfix]
    private static void TrackCardTrailFade(
        NCardTrailVfx __instance,
        ref Task __result)
    {
        __result = TransientCardVfxCleanup.TrackAsync(__result, __instance);
    }

    [HarmonyPatch(typeof(NCardFlyPowerVfx), nameof(NCardFlyPowerVfx.PlayAnim))]
    [HarmonyPostfix]
    private static void TrackCardFlyPowerAnimation(
        NCardFlyPowerVfx __instance,
        ref Task __result)
    {
        __result = TransientCardVfxCleanup.TrackAsync(__result, __instance);
    }

    [HarmonyPatch(typeof(NCardEnchantVfx), "PlayAnimation")]
    [HarmonyPostfix]
    private static void TrackCardEnchantAnimation(
        NCardEnchantVfx __instance,
        ref Task __result)
    {
        __result = TransientCardVfxCleanup.TrackAsync(__result, __instance);
    }

    [HarmonyPatch(typeof(NCardTransformVfx), "PlayAnimation")]
    [HarmonyPostfix]
    private static void TrackCardTransformAnimation(
        NCardTransformVfx __instance,
        ref Task __result)
    {
        __result = TransientCardVfxCleanup.TrackAsync(__result, __instance);
    }

    [HarmonyPatch(typeof(NCardUpgradeVfx), "PlayAnimation")]
    [HarmonyPostfix]
    private static void TrackCardUpgradeAnimation(
        NCardUpgradeVfx __instance,
        ref Task __result)
    {
        __result = TransientCardVfxCleanup.TrackAsync(__result, __instance);
    }

    [HarmonyPatch(typeof(NCardSmithVfx), "PlayAnimation", new Type[] { })]
    [HarmonyPostfix]
    private static void TrackCardSmithAnimation(
        NCardSmithVfx __instance,
        ref Task __result)
    {
        __result = TransientCardVfxCleanup.TrackAsync(__result, __instance);
    }

    [HarmonyPatch(
        typeof(NCardSmithVfx),
        "PlayAnimation",
        new[] { typeof(IEnumerable<CardModel>) })]
    [HarmonyPostfix]
    private static void TrackCardSmithCollectionAnimation(
        NCardSmithVfx __instance,
        ref Task __result)
    {
        __result = TransientCardVfxCleanup.TrackAsync(__result, __instance);
    }

    [HarmonyPatch(typeof(CardSelectCmd), nameof(CardSelectCmd.FromChooseACardScreen),
        new[]
        {
            typeof(PlayerChoiceContext),
            typeof(IReadOnlyList<CardModel>),
            typeof(Player),
            typeof(bool),
        })]
    [HarmonyPrefix]
    private static void BeforeChooseACardScreen(
        IReadOnlyList<CardModel> cards,
        bool canSkip)
    {
        CardChoiceCheckpointService.PrepareChoiceRequest(
            CardChoiceRequest.ForChooseCard(cards, canSkip));
    }

    [HarmonyPatch(typeof(CardSelectCmd), nameof(CardSelectCmd.FromSimpleGrid),
        new[]
        {
            typeof(PlayerChoiceContext),
            typeof(IReadOnlyList<CardModel>),
            typeof(Player),
            typeof(CardSelectorPrefs),
        })]
    [HarmonyPrefix]
    private static void BeforeSimpleGrid(
        IReadOnlyList<CardModel> cardsIn,
        CardSelectorPrefs prefs)
    {
        CardChoiceCheckpointService.PrepareChoiceRequest(
            CardChoiceRequest.ForSimpleGrid(prefs, cardsIn));
    }

    [HarmonyPatch(typeof(CardSelectCmd), nameof(CardSelectCmd.FromSimpleGridForRewards),
        new[]
        {
            typeof(PlayerChoiceContext),
            typeof(List<CardCreationResult>),
            typeof(Player),
            typeof(CardSelectorPrefs),
        })]
    [HarmonyPrefix]
    private static void BeforeRewardGrid(
        List<CardCreationResult> cards,
        CardSelectorPrefs prefs)
    {
        CardChoiceCheckpointService.PrepareChoiceRequest(
            CardChoiceRequest.ForSimpleGrid(
                prefs,
                cards.Select(card => card.Card).ToList()));
    }

    [HarmonyPatch(typeof(CardSelectCmd), nameof(CardSelectCmd.FromCombatPile),
        new[]
        {
            typeof(PlayerChoiceContext),
            typeof(CardPile),
            typeof(Player),
            typeof(CardSelectorPrefs),
            typeof(Func<CardModel, bool>),
        })]
    [HarmonyPrefix]
    private static void BeforeCombatPile(
        CardPile pile,
        CardSelectorPrefs prefs,
        Func<CardModel, bool>? filter)
    {
        CardChoiceCheckpointService.PrepareChoiceRequest(
            CardChoiceRequest.ForCombatPile(pile, prefs, filter));
    }

    [HarmonyPatch(typeof(CardSelectCmd), nameof(CardSelectCmd.FromHand),
        new[]
        {
            typeof(PlayerChoiceContext),
            typeof(Player),
            typeof(CardSelectorPrefs),
            typeof(Func<CardModel, bool>),
            typeof(AbstractModel),
        })]
    [HarmonyPrefix]
    private static void BeforeHandChoice(
        Player player,
        CardSelectorPrefs prefs,
        Func<CardModel, bool>? filter,
        AbstractModel source)
    {
        IReadOnlyList<CardModel> availableCards = PileType.Hand
            .GetPile(player)
            .Cards
            .Where(filter ?? (_ => true))
            .ToList();
        CardChoiceCheckpointService.PrepareChoiceRequest(
            CardChoiceRequest.ForHand(
                prefs,
                filter,
                source,
                availableCards));
    }

    [HarmonyPatch(typeof(CardSelectCmd), nameof(CardSelectCmd.FromHandForUpgrade),
        new[]
        {
            typeof(PlayerChoiceContext),
            typeof(Player),
            typeof(AbstractModel),
        })]
    [HarmonyPrefix]
    private static void BeforeHandUpgrade(
        Player player,
        AbstractModel source)
    {
        IReadOnlyList<CardModel> availableCards = PileType.Hand
            .GetPile(player)
            .Cards
            .Where(card => card.IsUpgradable)
            .ToList();
        CardChoiceCheckpointService.PrepareChoiceRequest(
            CardChoiceRequest.ForHandUpgrade(source, availableCards));
    }

    [HarmonyPatch(typeof(GameActionPlayerChoiceContext), nameof(GameActionPlayerChoiceContext.SignalPlayerChoiceBegun))]
    [HarmonyPostfix]
    private static void AfterPlayerChoiceBegun(GameActionPlayerChoiceContext __instance)
    {
        CardChoiceCheckpointService.OnChoiceBegun(__instance);
    }

    [HarmonyPatch(
        typeof(HookPlayerChoiceContext),
        nameof(HookPlayerChoiceContext.SignalPlayerChoiceBegun))]
    [HarmonyPostfix]
    private static void AfterHookPlayerChoiceBegun(
        HookPlayerChoiceContext __instance)
    {
        CardChoiceCheckpointService.OnHookChoiceBegun(__instance);
    }

    [HarmonyPatch(typeof(CombatManager), "SetupPlayerTurn")]
    [HarmonyPrefix]
    private static void BeforeSetupPlayerTurn(
        CombatManager __instance,
        Player player,
        out HookChoiceRecording? __state)
    {
        __state =
            CardChoiceCheckpointService.BeginTurnStartChoiceRecording(
                __instance,
                player);
    }

    [HarmonyPatch(typeof(CombatManager), "SetupPlayerTurn")]
    [HarmonyPostfix]
    private static void AfterSetupPlayerTurn(
        HookChoiceRecording? __state,
        ref Task __result)
    {
        if (__state != null)
        {
            __result =
                CardChoiceCheckpointService.TrackHookChoiceRecordingAsync(
                    __result,
                    __state);
        }
    }

    [HarmonyPatch(
        typeof(CombatManager),
        "EndPlayerTurnPhaseOneInternal",
        new Type[] { })]
    [HarmonyPrefix]
    private static void BeforeEndPlayerTurnPhaseOne(
        CombatManager __instance,
        out HookChoiceRecording? __state)
    {
        __state =
            CardChoiceCheckpointService.BeginTurnEndChoiceRecording(
                __instance);
    }

    [HarmonyPatch(
        typeof(CombatManager),
        "EndPlayerTurnPhaseOneInternal",
        new Type[] { })]
    [HarmonyPostfix]
    private static void AfterEndPlayerTurnPhaseOne(
        HookChoiceRecording? __state,
        ref Task __result)
    {
        if (__state != null)
        {
            __result =
                CardChoiceCheckpointService.TrackHookChoiceRecordingAsync(
                    __result,
                    __state);
        }
    }

    [HarmonyPatch(typeof(CardSelectCmd), "LogChoice")]
    [HarmonyPostfix]
    private static void AfterCardsChosen(IEnumerable<CardModel?> cards)
    {
        CardChoiceCheckpointService.OnCardsChosen(cards);
    }

    [HarmonyPatch(typeof(PlayCardAction), "ExecuteAction")]
    [HarmonyPrefix]
    private static void BeforePlayCard(PlayCardAction __instance)
    {
        UndoRedoManager.CaptureBeforeAction(__instance, "PlayCardAction");
    }

    [HarmonyPatch(typeof(PlayCardAction), "ExecuteAction")]
    [HarmonyPostfix]
    private static void AfterPlayCard(PlayCardAction __instance, ref Task __result)
    {
        __result = UndoRedoManager.CaptureAfterActionAsync(
            __result,
            __instance,
            "PlayCardAction",
            CreateCardEntry(__instance));
    }

    [HarmonyPatch(typeof(UsePotionAction), "ExecuteAction")]
    [HarmonyPrefix]
    private static void BeforeUsePotion(
        UsePotionAction __instance,
        out ActionHistoryEntry? __state)
    {
        // The slot is emptied while the action runs, so the history entry must be
        // built before it. Harmony's __state carries it to the postfix of the
        // same call without a shared dictionary that could leak on exceptions.
        __state = null;
        if (__instance.WasEnqueuedInCombat)
        {
            __state = CreatePotionEntry(__instance);
            UndoRedoManager.CaptureBeforeAction(__instance, "UsePotionAction");
        }
    }

    [HarmonyPatch(typeof(UsePotionAction), "ExecuteAction")]
    [HarmonyPostfix]
    private static void AfterUsePotion(
        UsePotionAction __instance,
        ActionHistoryEntry? __state,
        ref Task __result)
    {
        if (__instance.WasEnqueuedInCombat)
        {
            __result = UndoRedoManager.CaptureAfterActionAsync(
                __result,
                __instance,
                "UsePotionAction",
                __state);
        }
    }

    [HarmonyPatch(typeof(DiscardPotionGameAction), "ExecuteAction")]
    [HarmonyPrefix]
    private static void BeforeDiscardPotion(
        DiscardPotionGameAction __instance,
        out ActionHistoryEntry? __state)
    {
        __state = null;
        if (__instance.WasEnqueuedInCombat)
        {
            __state = CreateDiscardPotionEntry(__instance);
            UndoRedoManager.CaptureBeforeAction(__instance, "DiscardPotionGameAction");
        }
    }

    [HarmonyPatch(typeof(DiscardPotionGameAction), "ExecuteAction")]
    [HarmonyPostfix]
    private static void AfterDiscardPotion(
        DiscardPotionGameAction __instance,
        ActionHistoryEntry? __state,
        ref Task __result)
    {
        if (__instance.WasEnqueuedInCombat)
        {
            __result = UndoRedoManager.CaptureAfterActionAsync(
                __result,
                __instance,
                "DiscardPotionGameAction",
                __state);
        }
    }

    [HarmonyPatch(typeof(EndPlayerTurnAction), "ExecuteAction")]
    [HarmonyPrefix]
    private static void BeforeEndTurn(EndPlayerTurnAction __instance)
    {
        UndoRedoManager.CaptureBeforeAction(__instance, "EndPlayerTurnAction");
        // Ending the turn is a new action. Without this, ending the turn after an
        // undo kept the old redo branch: the next turn-start capture was discarded
        // as "inside redo history" and redo jumped back to the abandoned timeline.
        // Choice replay never runs EndPlayerTurnAction (it calls the phase directly).
        if (!CardChoiceCheckpointService.HasActiveChoice)
        {
            UndoRedoManager.BeginBranchFromCurrentCheckpoint();
        }

        // No postfix capture: EndPlayerTurnAction only marks the player ready. A
        // snapshot here would create a non-playable redo point before the next
        // player-control snapshot.
        UndoRedoManager.PrepareNextTurnStartEntry();
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
            return new ActionHistoryEntry(ActionHistoryEntryKind.Card, card.Title, target, UndoRedoManager.CurrentPlayerTurnNumber(), card: card);
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
            return new ActionHistoryEntry(ActionHistoryEntryKind.Potion, potion.Title.GetFormattedText() ?? potion.Id.Entry, target, UndoRedoManager.CurrentPlayerTurnNumber(), potion: potion);
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

            return new ActionHistoryEntry(ActionHistoryEntryKind.DiscardPotion, potion.Title.GetFormattedText() ?? potion.Id.Entry, UndoText.DiscardSlot(slotIndex), UndoRedoManager.CurrentPlayerTurnNumber(), potion: potion);
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"Failed to create discard potion stack entry: {ex.Message}");
            return null;
        }
    }

    private static string GetTargetName(uint? targetId)
    {
        return CombatManager.Instance.DebugOnlyGetState()?.GetCreature(targetId)?.LogName ??
               UndoText.NoTarget;
    }
}
