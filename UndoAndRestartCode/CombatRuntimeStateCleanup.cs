using System.Collections;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Runs;

namespace UndoAndRestartCode;

internal static class CombatRuntimeStateCleanup
{
    private const int staleRuntimeBlockerMinObservations = 5;
    private const ulong staleRuntimeBlockerMinMillis = 1500;

    private static string? observedRuntimeBlockerReason;
    private static int observedRuntimeBlockerCount;
    private static ulong observedRuntimeBlockerFirstTick;

    public static void ClearCombatTurnFlags()
    {
        ReflectionUtil.SetField(CombatManager.Instance, "_playerToEnemyTransitionFired", false);
        ReflectionUtil.SetField(CombatManager.Instance, "_inPlayerTurnSetup", false);
        ReflectionUtil.SetField(CombatManager.Instance, "_deferredEndTurnTransition", null);
        ReflectionUtil.SetField(CombatManager.Instance, "<EndingPlayerTurnPhaseOne>k__BackingField", false);
        ReflectionUtil.SetField(CombatManager.Instance, "<EndingPlayerTurnPhaseTwo>k__BackingField", false);
    }

    public static bool TryClearStaleEndingTurnFlagsIfPlayerControlAvailable(CombatState state)
    {
        if (!canTreatCurrentStateAsPlayable(state) ||
            hasRuntimeWorkThatShouldNotBeCleared())
        {
            return false;
        }

        ReflectionUtil.SetField(CombatManager.Instance, "_playerToEnemyTransitionFired", false);
        ReflectionUtil.SetField(CombatManager.Instance, "<EndingPlayerTurnPhaseOne>k__BackingField", false);
        ReflectionUtil.SetField(CombatManager.Instance, "<EndingPlayerTurnPhaseTwo>k__BackingField", false);
        MainFile.Logger.Info("Cleared stale ending-turn flags while player control was available.");
        return true;
    }

    public static bool TryRecoverStaleRuntimeBlocker(
        string reason,
        RuntimeBlockerKind kind,
        object blocker)
    {
        if (!canConsiderRuntimeBlockerStale())
        {
            ResetRuntimeBlockerObservation();
            return false;
        }

        ulong now = Time.GetTicksMsec();
        if (observedRuntimeBlockerReason != reason)
        {
            observedRuntimeBlockerReason = reason;
            observedRuntimeBlockerCount = 1;
            observedRuntimeBlockerFirstTick = now;
            return false;
        }

        observedRuntimeBlockerCount++;
        if (observedRuntimeBlockerCount < staleRuntimeBlockerMinObservations ||
            now - observedRuntimeBlockerFirstTick < staleRuntimeBlockerMinMillis)
        {
            return false;
        }

        return kind switch
        {
            RuntimeBlockerKind.EffectDepth => clearStaleEffectDepth(blocker),
            RuntimeBlockerKind.ReceivedChoices => clearStaleSingleplayerReceivedChoices(blocker),
            _ => false,
        };
    }

    public static void ResetRuntimeBlockerObservation()
    {
        observedRuntimeBlockerReason = null;
        observedRuntimeBlockerCount = 0;
        observedRuntimeBlockerFirstTick = 0;
    }

    private static bool clearStaleEffectDepth(object blocker)
    {
        ((Dictionary<Player, int>)blocker).Clear();
        MainFile.Logger.Warn("Cleared stale card/potion effect depth after player control was available.");
        ResetRuntimeBlockerObservation();
        return true;
    }

    private static bool clearStaleSingleplayerReceivedChoices(object blocker)
    {
        if (RunManager.Instance.NetService.Type != NetGameType.Singleplayer)
        {
            return false;
        }

        ((IList)blocker).Clear();
        MainFile.Logger.Warn("Cleared stale singleplayer received-choice state after player control was available.");
        ResetRuntimeBlockerObservation();
        return true;
    }

    private static bool canConsiderRuntimeBlockerStale()
    {
        CombatState? state = CombatManager.Instance.DebugOnlyGetState();
        if (state == null ||
            !canTreatCurrentStateAsPlayable(state) ||
            hasImmediateRuntimeWork())
        {
            return false;
        }

        try
        {
            NTargetManager? targetManager = NTargetManager.Instance;
            return targetManager?.IsInSelection != true;
        }
        catch
        {
            return false;
        }
    }

    private static bool canTreatCurrentStateAsPlayable(CombatState state)
    {
        return CombatManager.Instance.IsInProgress &&
               state.CurrentSide == CombatSide.Player &&
               !CombatManager.Instance.PlayerActionsDisabled &&
               !CombatManager.Instance.EndingPlayerTurnPhaseOne &&
               !CombatManager.Instance.EndingPlayerTurnPhaseTwo &&
               state.Players.All(player => player.PlayerCombatState?.Phase == PlayerTurnPhase.Play);
    }

    private static bool hasImmediateRuntimeWork()
    {
        return !RunManager.Instance.ActionQueueSet.IsEmpty ||
               RunManager.Instance.ActionExecutor.IsRunning ||
               RunManager.Instance.ActionExecutor.CurrentlyRunningAction != null;
    }

    private static bool hasRuntimeWorkThatShouldNotBeCleared()
    {
        if (hasImmediateRuntimeWork())
        {
            return true;
        }

        IList? waitingActions =
            ReflectionUtil.GetField<IList>(RunManager.Instance.ActionQueueSet, "_actionsWaitingForResumption");
        if (waitingActions?.Count > 0)
        {
            return true;
        }

        IList? hookActions =
            ReflectionUtil.GetField<IList>(RunManager.Instance.ActionQueueSynchronizer, "_hookActions");
        IList? requestedActions =
            ReflectionUtil.GetField<IList>(
                RunManager.Instance.ActionQueueSynchronizer,
                "_requestedActionsWaitingForPlayerTurn");
        if (hookActions?.Count > 0 || requestedActions?.Count > 0)
        {
            return true;
        }

        Dictionary<Player, int>? effectDepth =
            ReflectionUtil.GetField<Dictionary<Player, int>>(CombatManager.Instance, "_cardOrPotionEffectDepth");
        if (effectDepth?.Values.Any(depth => depth != 0) == true)
        {
            return true;
        }

        IList? receivedChoices =
            ReflectionUtil.GetField<IList>(RunManager.Instance.PlayerChoiceSynchronizer, "_receivedChoices");
        return receivedChoices?.Count > 0;
    }
}

internal enum RuntimeBlockerKind
{
    EffectDepth,
    ReceivedChoices,
}
