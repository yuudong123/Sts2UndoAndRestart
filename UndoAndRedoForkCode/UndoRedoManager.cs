using Godot;
using System.Collections;
using System.Diagnostics;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

namespace UndoAndRedoForkCode;

internal static class UndoRedoManager
{
    public const long LeftArrowKeyCode = 4194319;
    public const long RightArrowKeyCode = 4194321;
    public const long QuickRestartKeyCode = 4194336;

    private static readonly List<CombatSnapshot> Snapshots = new();
    private static readonly List<UndoStackEntry> ActionEntries = new();
    private static CombatState? _sessionState;
    private static int _cursor = -1;
    private static bool _isRestoring;
    private static int _readyCaptureRequestId;
    private static int? _pendingTurnTransitionTurnNumber;
    private static readonly List<UndoStackEntry> PendingEntries = new();

    public static void Reset()
    {
        ClearHistory();
        _sessionState = null;
        _isRestoring = false;
        _pendingTurnTransitionTurnNumber = null;
        PendingEntries.Clear();
        CombatRuntimeStateCleanup.ResetRuntimeBlockerObservation();
        CreatureLifecycle.Clear();
        _readyCaptureRequestId++;
        MainFile.Logger.Info("Combat history reset.");
        UndoStackOverlay.Refresh();
    }

    public static bool HandleUndoKey()
    {
        MainFile.Logger.Info("Left arrow pressed.");
        return TryMove(-1);
    }

    public static bool HandleRedoKey()
    {
        MainFile.Logger.Info("Right arrow pressed.");
        return TryMove(1);
    }

    public static void CaptureBeforeAction(string reason)
    {
        MainFile.Logger.Debug($"Registered action start: {reason}");
        CapturePendingTurnStartBeforeAction(reason);
    }

    public static void QueueTurnTransitionEntry()
    {
        // 실제 턴 번호는 OnPlayerTurnStarted에서 정해짐. 강제 턴 종료 경로에서도 표식 유지해야 함.
        _pendingTurnTransitionTurnNumber ??= -1;
    }

    public static void OnPlayerTurnStarted(CombatState state)
    {
        if (!ReferenceEquals(_sessionState, state) || Snapshots.Count == 0)
        {
            RequestPlayerControlReadyCapture("CombatManager.TurnStarted");
            return;
        }

        int turnNumber = GetPlayerTurnNumber(state);
        if (turnNumber > 1)
        {
            _pendingTurnTransitionTurnNumber = turnNumber;
            MainFile.Logger.Info($"Queued next-turn snapshot for turn {turnNumber}.");
        }

        RequestPlayerControlReadyCapture("CombatManager.TurnStarted");
    }

    public static async Task CaptureAfterActionAsync(Task original, string reason)
    {
        await CaptureAfterActionAsync(original, reason, null);
    }

    public static async Task CaptureAfterActionAsync(Task original, string reason, UndoStackEntry? entry)
    {
        await original;
        if (entry != null)
        {
            PendingEntries.Add(entry);
        }

        RequestPlayerControlReadyCapture($"{reason}:settled");
    }

    public static void CaptureCompletedPlayerAction(GameAction action)
    {
        if (action is not PlayCardAction &&
            action is not UsePotionAction &&
            action is not DiscardPotionGameAction)
        {
            return;
        }

        int snapshotIndex = Capture(
            $"{action.GetType().Name}:boundary",
            strictPlayPhase: true);
        if (snapshotIndex < 0)
        {
            return;
        }

        if (PendingEntries.Count > 0)
        {
            UndoStackEntry entry = PendingEntries[0];
            PendingEntries.RemoveAt(0);
            entry.SnapshotIndex = snapshotIndex;
            ActionEntries.Add(entry);
            TrimActionEntriesToSnapshots();
        }

        UndoStackOverlay.Refresh();
    }

    public static void CapturePlayerControlReady()
    {
        RequestPlayerControlReadyCapture("explicit player-control request");
    }

    public static void RequestPlayerControlReadyCapture(string source)
    {
        int requestId = ++_readyCaptureRequestId;
        _ = CapturePlayerControlReadyWhenStable(requestId, source);
    }

    public static IReadOnlyList<UndoStackEntry> GetActionEntries()
    {
        return ActionEntries;
    }

    public static int CurrentSnapshotIndex => _cursor;

    public static void TryRestoreSnapshot(int target)
    {
        TryRestoreTarget(target, "history click");
    }

    private static int Capture(string reason, bool strictPlayPhase)
    {
        if (_isRestoring)
        {
            return -1;
        }

        if (!CanCapture(strictPlayPhase, out string blockReason, out CombatState? state))
        {
            MainFile.Logger.Debug($"Skipped capture {reason}: {blockReason}");
            return -1;
        }

        try
        {
            StartSessionIfNeeded(state!);
            Stopwatch timer = Stopwatch.StartNew();
            CombatSnapshot snapshot = CombatSnapshot.Capture(state!, reason);
            timer.Stop();
            if (timer.ElapsedMilliseconds >= 8)
            {
                MainFile.Logger.Info(
                    $"Snapshot capture took {timer.ElapsedMilliseconds} ms: {reason}");
            }
            if (_cursor >= 0 && _cursor < Snapshots.Count && Snapshots[_cursor].Fingerprint == snapshot.Fingerprint)
            {
                MainFile.Logger.Debug($"Skipped duplicate snapshot {reason}.");
                return -1;
            }

            if (_cursor < Snapshots.Count - 1)
            {
                Snapshots.RemoveRange(_cursor + 1, Snapshots.Count - _cursor - 1);
                ActionEntries.RemoveAll(entry => entry.SnapshotIndex > _cursor);
            }

            Snapshots.Add(snapshot);
            TrimSnapshotsToLimit();
            _cursor = Snapshots.Count - 1;
            MainFile.Logger.Info($"Captured snapshot {_cursor + 1}/{Snapshots.Count}: {reason}");
            UndoStackOverlay.Refresh();
            return _cursor;
        }
        catch (Exception ex)
        {
            MainFile.Logger.Error($"Failed to capture snapshot {reason}: {ex}");
            return -1;
        }
    }

    private static bool TryMove(int direction)
    {
        if (!CanRestore(out string blockReason, out CombatState? state))
        {
            MainFile.Logger.Info($"Undo/redo blocked: {blockReason}");
            return false;
        }

        StartSessionIfNeeded(state!);
        int target = FindManualRestoreTarget(direction);
        if (target < 0)
        {
            MainFile.Logger.Info($"Undo/redo blocked: no {(direction < 0 ? "undo" : "redo")} snapshot. cursor={_cursor}, count={Snapshots.Count}");
            return false;
        }

        return RestoreSnapshot(state!, target, direction < 0 ? "undo" : "redo");
    }

    private static void TryRestoreTarget(int target, string source)
    {
        if (!CanRestore(out string blockReason, out CombatState? state))
        {
            MainFile.Logger.Info($"Snapshot restore blocked via {source}: {blockReason}");
            return;
        }

        StartSessionIfNeeded(state!);
        if (Snapshots.Count == 0)
        {
            Capture("manual-initial", strictPlayPhase: true);
        }

        if (target == _cursor)
        {
            MainFile.Logger.Info($"Snapshot restore ignored via {source}: already at snapshot {target + 1}.");
            return;
        }

        if (target < 0 || target >= Snapshots.Count)
        {
            MainFile.Logger.Info($"Snapshot restore blocked via {source}: target={target}, count={Snapshots.Count}");
            return;
        }

        if (!Snapshots[target].IsManualRestoreTarget)
        {
            MainFile.Logger.Info($"Snapshot restore blocked via {source}: target snapshot is not manually restorable.");
            return;
        }

        RestoreSnapshot(state!, target, source);
    }

    private static bool RestoreSnapshot(CombatState state, int target, string source)
    {
        CombatSnapshot snapshot = Snapshots[target];
        if (!snapshot.BelongsTo(state!))
        {
            MainFile.Logger.Info("Undo/redo blocked: snapshot belongs to a different combat.");
            Reset();
            return false;
        }

        _isRestoring = true;
        _readyCaptureRequestId++;
        try
        {
            Stopwatch timer = Stopwatch.StartNew();
            snapshot.Restore();
            timer.Stop();
            _cursor = target;
            MainFile.Logger.Info($"Restored snapshot {_cursor + 1}/{Snapshots.Count} via {source}: {snapshot.Reason}");
            if (timer.ElapsedMilliseconds >= 8)
            {
                MainFile.Logger.Info(
                    $"Snapshot restore took {timer.ElapsedMilliseconds} ms: {snapshot.Reason}");
            }
            UndoStackOverlay.Refresh();
            return true;
        }
        catch (Exception ex)
        {
            MainFile.Logger.Error($"Failed to restore snapshot: {ex}");
            return false;
        }
        finally
        {
            _isRestoring = false;
        }
    }

    private static int FindManualRestoreTarget(int direction)
    {
        int target = _cursor + direction;
        int skipped = 0;
        while (target >= 0 && target < Snapshots.Count)
        {
            if (Snapshots[target].IsManualRestoreTarget)
            {
                if (skipped > 0)
                {
                    MainFile.Logger.Info($"Skipped {skipped} non-playable snapshot(s) while moving {(direction < 0 ? "back" : "forward")}.");
                }

                return target;
            }

            skipped++;
            target += direction;
        }

        return -1;
    }

    private static async Task CapturePlayerControlReadyWhenStable(int requestId, string source)
    {
        for (int attempt = 0; attempt < 240; attempt++)
        {
            if (requestId != _readyCaptureRequestId || _isRestoring)
            {
                return;
            }

            if (CanCaptureReadySnapshot(out string blockReason))
            {
                MainFile.Logger.Info($"Player control ready detected via {source} after {attempt} frame(s).");
                CapturePlayerControlReadySnapshot();
                return;
            }

            if (attempt == 0 || attempt == 30 || attempt == 120)
            {
                MainFile.Logger.Info($"Waiting to capture PlayerControlReady via {source}: {blockReason}");
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

        MainFile.Logger.Info($"Gave up capturing PlayerControlReady via {source}: state did not become stable.");
    }

    private static void CapturePlayerControlReadySnapshot()
    {
        int snapshotIndex = Capture("PlayerControlReady", strictPlayPhase: true);
        if (snapshotIndex < 0)
        {
            snapshotIndex = _cursor;
        }

        if (snapshotIndex < 0)
        {
            return;
        }

        foreach (UndoStackEntry pending in PendingEntries)
        {
            pending.SnapshotIndex = snapshotIndex;
            ActionEntries.Add(pending);
        }
        PendingEntries.Clear();

        AddPendingTurnTransitionEntry(snapshotIndex);

        TrimActionEntriesToSnapshots();
        UndoStackOverlay.Refresh();
    }

    private static void CapturePendingTurnStartBeforeAction(string reason)
    {
        if (_pendingTurnTransitionTurnNumber is not > 1)
        {
            return;
        }

        int snapshotIndex = Capture(
            $"PlayerControlReady:before-{reason}",
            strictPlayPhase: true);
        if (snapshotIndex < 0)
        {
            MainFile.Logger.Info(
                $"Could not finalize pending turn {_pendingTurnTransitionTurnNumber} before {reason}; keeping it queued.");
            return;
        }

        AddPendingTurnTransitionEntry(snapshotIndex);
        TrimActionEntriesToSnapshots();
        UndoStackOverlay.Refresh();
        MainFile.Logger.Info($"Finalized next-turn snapshot before {reason}.");
    }

    private static void AddPendingTurnTransitionEntry(int snapshotIndex)
    {
        if (_pendingTurnTransitionTurnNumber is not > 1)
        {
            return;
        }

        int turnNumber = _pendingTurnTransitionTurnNumber.Value;
        if (!ActionEntries.Any(entry =>
                entry.Kind == UndoStackEntryKind.TurnTransition &&
                entry.TurnNumber == turnNumber &&
                entry.SnapshotIndex == snapshotIndex))
        {
            UndoStackEntry entry = new(
                UndoStackEntryKind.TurnTransition,
                UndoText.NextTurnStart,
                UndoText.Turn(turnNumber),
                turnNumber)
            {
                SnapshotIndex = snapshotIndex,
            };
            ActionEntries.Add(entry);
        }

        _pendingTurnTransitionTurnNumber = null;
    }

    private static int GetPlayerTurnNumber(CombatState state)
    {
        return state.Players
            .Select(player => player.PlayerCombatState?.TurnNumber ?? 1)
            .DefaultIfEmpty(1)
            .Max();
    }

    private static bool CanCaptureReadySnapshot(out string reason)
    {
        if (!CanCapture(strictPlayPhase: true, out reason, out _))
        {
            return false;
        }

        return IsRuntimeSettled(out reason);
    }

    private static bool IsRuntimeSettled(out string reason)
    {
        if (!RunManager.Instance.ActionQueueSet.IsEmpty)
        {
            CombatRuntimeStateCleanup.ResetRuntimeBlockerObservation();
            reason = "action queue is not empty";
            return false;
        }

        if (RunManager.Instance.ActionExecutor.IsRunning || RunManager.Instance.ActionExecutor.CurrentlyRunningAction != null)
        {
            CombatRuntimeStateCleanup.ResetRuntimeBlockerObservation();
            reason = "action executor is running";
            return false;
        }

        IList? waitingActions =
            ReflectionUtil.GetField<IList>(RunManager.Instance.ActionQueueSet, "_actionsWaitingForResumption");
        if (waitingActions?.Count > 0)
        {
            CombatRuntimeStateCleanup.ResetRuntimeBlockerObservation();
            reason = "an action is waiting for a player choice";
            return false;
        }

        IList? hookActions =
            ReflectionUtil.GetField<IList>(RunManager.Instance.ActionQueueSynchronizer, "_hookActions");
        IList? requestedActions =
            ReflectionUtil.GetField<IList>(
                RunManager.Instance.ActionQueueSynchronizer,
                "_requestedActionsWaitingForPlayerTurn");
        if (hookActions?.Count > 0 || requestedActions?.Count > 0)
        {
            CombatRuntimeStateCleanup.ResetRuntimeBlockerObservation();
            reason = "synchronized actions are still pending";
            return false;
        }

        Dictionary<Player, int>? effectDepth =
            ReflectionUtil.GetField<Dictionary<Player, int>>(CombatManager.Instance, "_cardOrPotionEffectDepth");
        if (effectDepth?.Values.Any(depth => depth != 0) == true)
        {
            reason = "card or potion effect is still executing";
            if (CombatRuntimeStateCleanup.TryRecoverStaleRuntimeBlocker(
                    reason,
                    RuntimeBlockerKind.EffectDepth,
                    effectDepth))
            {
                return IsRuntimeSettled(out reason);
            }

            return false;
        }

        IList? receivedChoices =
            ReflectionUtil.GetField<IList>(RunManager.Instance.PlayerChoiceSynchronizer, "_receivedChoices");
        if (receivedChoices?.Count > 0)
        {
            reason = "player choice is pending";
            if (CombatRuntimeStateCleanup.TryRecoverStaleRuntimeBlocker(
                    reason,
                    RuntimeBlockerKind.ReceivedChoices,
                    receivedChoices))
            {
                return IsRuntimeSettled(out reason);
            }

            return false;
        }

        CombatRuntimeStateCleanup.ResetRuntimeBlockerObservation();
        reason = "";
        return true;
    }

    private static void StartSessionIfNeeded(CombatState state)
    {
        if (ReferenceEquals(_sessionState, state))
        {
            return;
        }

        ClearHistory();
        _pendingTurnTransitionTurnNumber = null;
        PendingEntries.Clear();
        _sessionState = state;
        MainFile.Logger.Info("Started undo/redo session for current combat.");
        UndoStackOverlay.Refresh();
    }

    private static void ClearHistory()
    {
        Snapshots.Clear();
        ActionEntries.Clear();
        _cursor = -1;
    }

    private static void TrimSnapshotsToLimit()
    {
        int maxSnapshots = UndoAndRedoConfig.SnapshotLimit;
        while (Snapshots.Count > maxSnapshots)
        {
            Snapshots.RemoveAt(0);
            foreach (UndoStackEntry entry in ActionEntries)
            {
                entry.SnapshotIndex--;
            }

            ActionEntries.RemoveAll(entry => entry.SnapshotIndex < 0);
        }
    }

    private static void TrimActionEntriesToSnapshots()
    {
        ActionEntries.RemoveAll(entry => entry.SnapshotIndex < 0 || entry.SnapshotIndex >= Snapshots.Count);
    }

    private static bool CanCapture(bool strictPlayPhase, out string reason, out CombatState? state)
    {
        state = CombatManager.Instance.DebugOnlyGetState();
        if (_isRestoring)
        {
            reason = "restore in progress";
            return false;
        }

        if (!RunManager.Instance.IsSingleplayerOrFakeMultiplayer)
        {
            reason = "multiplayer run";
            return false;
        }

        if (!CombatManager.Instance.IsInProgress || state == null)
        {
            reason = "combat is not in progress";
            return false;
        }

        if (strictPlayPhase && !IsSafePlayPhase(state, out reason))
        {
            return false;
        }

        reason = "";
        return true;
    }

    private static bool CanRestore(out string reason, out CombatState? state)
    {
        if (!CanCapture(strictPlayPhase: true, out reason, out state))
        {
            return false;
        }

        return IsRuntimeSettled(out reason);
    }

    private static bool IsSafePlayPhase(CombatState state, out string reason)
    {
        if (state.CurrentSide != CombatSide.Player)
        {
            reason = "not player side";
            return false;
        }

        if (CombatManager.Instance.PlayerActionsDisabled)
        {
            reason = "player actions disabled";
            return false;
        }

        if (CombatManager.Instance.EndingPlayerTurnPhaseOne || CombatManager.Instance.EndingPlayerTurnPhaseTwo)
        {
            if (CombatRuntimeStateCleanup.TryClearStaleEndingTurnFlagsIfPlayerControlAvailable(state))
            {
                reason = "";
                return true;
            }

            reason = "ending player turn";
            return false;
        }

        if (state.Players.Any(player => player.PlayerCombatState?.Phase != PlayerTurnPhase.Play))
        {
            reason = "player turn phase is not Play";
            return false;
        }

        reason = "";
        return true;
    }

}

internal sealed class UndoStackEntry
{
    public UndoStackEntry(UndoStackEntryKind kind, string title, string detail, int turnNumber, CardModel? card = null, PotionModel? potion = null)
    {
        Kind = kind;
        Title = title;
        Detail = detail;
        TurnNumber = turnNumber;
        Card = card;
        Potion = potion;
    }

    public UndoStackEntryKind Kind { get; }
    public string Title { get; }
    public string Detail { get; }
    public int TurnNumber { get; }
    public CardModel? Card { get; }
    public PotionModel? Potion { get; }
    public int SnapshotIndex { get; set; } = -1;
}

internal enum UndoStackEntryKind
{
    Card,
    Potion,
    DiscardPotion,
    TurnTransition
}
