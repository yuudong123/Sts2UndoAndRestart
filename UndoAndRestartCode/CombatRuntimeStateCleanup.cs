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
    private const int StaleRuntimeBlockerMinObservations = 5;
    private const ulong StaleRuntimeBlockerMinMillis = 1500;

    private static string? _observedRuntimeBlockerReason;
    private static int _observedRuntimeBlockerCount;
    private static ulong _observedRuntimeBlockerFirstTick;

    public static void ClearCombatTurnFlags()
    {
        ReflectionUtil.SetRequiredField(CombatManager.Instance, "_playerToEnemyTransitionFired", false);
        ReflectionUtil.SetRequiredField(CombatManager.Instance, "_inPlayerTurnSetup", false);
        ReflectionUtil.SetRequiredField(CombatManager.Instance, "_deferredEndTurnTransition", null);
        ReflectionUtil.SetRequiredField(CombatManager.Instance, "<EndingPlayerTurnPhaseOne>k__BackingField", false);
        ReflectionUtil.SetRequiredField(CombatManager.Instance, "<EndingPlayerTurnPhaseTwo>k__BackingField", false);
    }

    public static bool TryClearStaleEndingTurnFlagsIfPlayerControlAvailable(CombatState state)
    {
        if (!CanTreatCurrentStateAsPlayable(state) || HasPendingRuntimeWork())
        {
            return false;
        }

        ReflectionUtil.SetRequiredField(CombatManager.Instance, "_playerToEnemyTransitionFired", false);
        ReflectionUtil.SetRequiredField(CombatManager.Instance, "<EndingPlayerTurnPhaseOne>k__BackingField", false);
        ReflectionUtil.SetRequiredField(CombatManager.Instance, "<EndingPlayerTurnPhaseTwo>k__BackingField", false);
        MainFile.Logger.Info("Cleared stale ending-turn flags while player control was available.");
        return true;
    }

    public static bool TryRecoverStaleRuntimeBlocker(
        string reason,
        RuntimeBlockerKind kind,
        object blocker)
    {
        if (!CanConsiderRuntimeBlockerStale())
        {
            ResetRuntimeBlockerObservation();
            return false;
        }

        ulong now = Time.GetTicksMsec();
        if (_observedRuntimeBlockerReason != reason)
        {
            _observedRuntimeBlockerReason = reason;
            _observedRuntimeBlockerCount = 1;
            _observedRuntimeBlockerFirstTick = now;
            return false;
        }

        _observedRuntimeBlockerCount++;
        if (_observedRuntimeBlockerCount < StaleRuntimeBlockerMinObservations ||
            now - _observedRuntimeBlockerFirstTick < StaleRuntimeBlockerMinMillis)
        {
            return false;
        }

        return kind switch
        {
            RuntimeBlockerKind.EffectDepth => ClearStaleEffectDepth(blocker),
            RuntimeBlockerKind.ReceivedChoices => ClearStaleSingleplayerReceivedChoices(blocker),
            _ => false,
        };
    }

    public static void ResetRuntimeBlockerObservation()
    {
        _observedRuntimeBlockerReason = null;
        _observedRuntimeBlockerCount = 0;
        _observedRuntimeBlockerFirstTick = 0;
    }

    private static bool ClearStaleEffectDepth(object blocker)
    {
        ((Dictionary<Player, int>)blocker).Clear();
        MainFile.Logger.Warn("Cleared stale card/potion effect depth after player control was available.");
        ResetRuntimeBlockerObservation();
        return true;
    }

    private static bool ClearStaleSingleplayerReceivedChoices(object blocker)
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

    private static bool CanConsiderRuntimeBlockerStale()
    {
        CombatState? state = CombatManager.Instance.DebugOnlyGetState();
        if (state == null ||
            !CanTreatCurrentStateAsPlayable(state) || HasImmediateRuntimeWork())
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

    private static bool CanTreatCurrentStateAsPlayable(CombatState state)
    {
        return CombatManager.Instance.IsInProgress &&
               state.CurrentSide == CombatSide.Player &&
               !CombatManager.Instance.PlayerActionsDisabled &&
               !CombatManager.Instance.EndingPlayerTurnPhaseOne &&
               !CombatManager.Instance.EndingPlayerTurnPhaseTwo &&
               state.Players.All(player => player.PlayerCombatState?.Phase == PlayerTurnPhase.Play);
    }

    private static bool HasImmediateRuntimeWork()
    {
        return !RunManager.Instance.ActionQueueSet.IsEmpty ||
               RunManager.Instance.ActionExecutor.IsRunning ||
               RunManager.Instance.ActionExecutor.CurrentlyRunningAction != null;
    }

    internal static bool HasPendingRuntimeWork()
    {
        try
        {
            return HasPendingRuntimeWorkCore();
        }
        catch (Exception ex)
        {
            MainFile.Logger.Error($"Failed to inspect pending combat runtime work: {ex}");
            return true;
        }
    }

    private static bool HasPendingRuntimeWorkCore()
    {
        if (HasImmediateRuntimeWork())
        {
            return true;
        }

        IList waitingActions = ReflectionUtil.GetRequiredField<IList>(
            RunManager.Instance.ActionQueueSet,
            "_actionsWaitingForResumption");
        if (waitingActions.Count > 0)
        {
            return true;
        }

        IList hookActions = ReflectionUtil.GetRequiredField<IList>(
            RunManager.Instance.ActionQueueSynchronizer,
            "_hookActions");
        IList requestedActions = ReflectionUtil.GetRequiredField<IList>(
            RunManager.Instance.ActionQueueSynchronizer,
            "_requestedActionsWaitingForPlayerTurn");
        if (hookActions.Count > 0 || requestedActions.Count > 0)
        {
            return true;
        }

        Dictionary<Player, int> effectDepth = ReflectionUtil.GetRequiredField<Dictionary<Player, int>>(
            CombatManager.Instance,
            "_cardOrPotionEffectDepth");
        if (effectDepth.Values.Any(depth => depth != 0))
        {
            return true;
        }

        IList receivedChoices = ReflectionUtil.GetRequiredField<IList>(
            RunManager.Instance.PlayerChoiceSynchronizer,
            "_receivedChoices");
        return receivedChoices.Count > 0;
    }
}

internal enum RuntimeBlockerKind
{
    EffectDepth,
    ReceivedChoices,
}
