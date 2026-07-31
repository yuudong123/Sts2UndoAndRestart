using Godot;
using System.Collections;
using System.Diagnostics;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

namespace UndoAndRestartCode;

internal static class UndoRedoManager
{
    public const long LeftArrowKeyCode = 4194319;
    public const long RightArrowKeyCode = 4194321;
    public const long QuickRestartKeyCode = 4194336;

    private static readonly List<UndoCheckpoint> Checkpoints = new();
    private static readonly List<ActionHistoryEntry> ActionEntries = new();
    private static CombatState? _sessionState;
    private static int _cursor = -1;
    private static bool _isRestoring;
    private static int _readyCaptureRequestId;
    private static int? _pendingTurnStartTurnNumber;
    private static readonly List<ActionHistoryEntry> PendingEntries = new();
    private static bool _choiceNavigationInProgress;
    private static bool _forceNextPlayerControlReadySnapshot;

    public static void Reset()
    {
        ClearHistory();
        _sessionState = null;
        _isRestoring = false;
        _choiceNavigationInProgress = false;
        _forceNextPlayerControlReadySnapshot = false;
        _pendingTurnStartTurnNumber = null;
        PendingEntries.Clear();
        CardChoiceCheckpointService.Reset();
        CombatRuntimeStateCleanup.ResetRuntimeBlockerObservation();
        ParkedCreatureNodeRegistry.Clear();
        _readyCaptureRequestId++;
        MainFile.Logger.Info("Combat history reset.");
        ActionHistoryOverlay.Refresh();
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
        FinalizeForcedPlayerControlReadySnapshotBeforeAction(reason);
        CaptureInitialSnapshotBeforeFirstPlayerAction(reason);
        CapturePendingTurnStartBeforeAction(reason);
    }

    private static void FinalizeForcedPlayerControlReadySnapshotBeforeAction(string reason)
    {
        if (!_forceNextPlayerControlReadySnapshot)
        {
            return;
        }

        CapturePlayerControlReadySnapshot();
        if (!_forceNextPlayerControlReadySnapshot)
        {
            MainFile.Logger.Info(
                $"Finalized the post-choice player-control snapshot before {reason}.");
        }
    }

    public static void PrepareNextTurnStartEntry()
    {
        // 실제 턴 번호는 OnPlayerTurnStarted에서 정해짐. 강제 턴 종료 경로에서도 표식 유지해야 함.
        _pendingTurnStartTurnNumber ??= -1;
    }

    public static void OnPlayerTurnStarted(CombatState state)
    {
        StartSessionIfNeeded(state);
        QueueTurnStartEntry(state);
        RequestPlayerControlReadyCapture("CombatManager.TurnStarted");
    }

    public static void QueueCurrentTurnStartEntry()
    {
        CombatState? state = CombatManager.Instance.DebugOnlyGetState();
        if (state != null && CombatManager.Instance.IsInProgress)
        {
            QueueTurnStartEntry(state);
        }
    }

    private static void QueueTurnStartEntry(CombatState state)
    {
        int turnNumber = GetPlayerTurnNumber(state);
        bool turnStartAlreadyRecorded = ActionEntries.Any(entry =>
            entry.Kind == ActionHistoryEntryKind.TurnStart &&
            entry.TurnNumber == turnNumber);
        if (!turnStartAlreadyRecorded)
        {
            _pendingTurnStartTurnNumber = turnNumber;
            MainFile.Logger.Info($"Queued turn-start snapshot for turn {turnNumber}.");
        }
    }

    public static async Task CaptureAfterActionAsync(Task original, string reason)
    {
        await CaptureAfterActionAsync(original, reason, null);
    }

    public static async Task CaptureAfterActionAsync(Task original, string reason, ActionHistoryEntry? entry)
    {
        await original;
        if (_choiceNavigationInProgress)
        {
            MainFile.Logger.Debug(
                $"Skipped completed action recording during card choice navigation: {reason}");
            return;
        }

        if (entry != null)
        {
            PendingEntries.Add(entry);
        }

        RequestPlayerControlReadyCapture($"{reason}:settled");
    }

    public static void CaptureCompletedPlayerAction(GameAction action)
    {
        if (_choiceNavigationInProgress)
        {
            MainFile.Logger.Debug(
                $"Skipped action boundary during card choice navigation: {action.GetType().Name}");
            return;
        }

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
            ActionHistoryEntry pendingEntry = PendingEntries[0];
            PendingEntries.RemoveAt(0);
            pendingEntry.SnapshotIndex = snapshotIndex;
            ActionEntries.Add(pendingEntry);
            TrimActionEntriesToSnapshots();
        }

        ActionHistoryOverlay.Refresh();
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

    public static void RequestForcedPlayerControlReadyCapture(string source)
    {
        _forceNextPlayerControlReadySnapshot = true;
        RequestPlayerControlReadyCapture(source);
    }

    public static CombatSnapshot? CaptureCardChoiceReplayBaseSnapshot(string reason)
    {
        if (_isRestoring || !RunManager.Instance.IsSingleplayerOrFakeMultiplayer)
        {
            return null;
        }

        CombatState? state = CombatManager.Instance.DebugOnlyGetState();
        if (state == null || !CombatManager.Instance.IsInProgress)
        {
            return null;
        }

        try
        {
            StartSessionIfNeeded(state);
            return CombatSnapshot.Capture(state, reason);
        }
        catch (Exception ex)
        {
            MainFile.Logger.Error(
                $"Failed to capture card choice replay base snapshot {reason}: {ex}");
            return null;
        }
    }

    public static IReadOnlyList<ActionHistoryEntry> GetActionEntries()
    {
        return ActionEntries;
    }

    public static int CurrentSnapshotIndex => _cursor;
    public static int SnapshotCount => Checkpoints.Count;

    public static UndoCheckpoint? RegisterCardChoiceCheckpoint(
        CardChoiceReplayPoint replayPoint,
        CombatSnapshot? replayBaseSnapshot = null)
    {
        if (_isRestoring ||
            (replayBaseSnapshot == null &&
             (_cursor < 0 || _cursor >= Checkpoints.Count)))
        {
            return null;
        }

        CombatState? state = CombatManager.Instance.DebugOnlyGetState();
        if (state == null ||
            !ReferenceEquals(_sessionState, state) ||
            !RunManager.Instance.IsSingleplayerOrFakeMultiplayer)
        {
            return null;
        }

        CombatSnapshot checkpointSnapshot =
            replayBaseSnapshot ?? Checkpoints[_cursor].Snapshot;
        if (!checkpointSnapshot.BelongsTo(state))
        {
            return null;
        }

        TruncateTimelineAfterCursor();
        UndoCheckpoint checkpoint = UndoCheckpoint.ForCardChoice(
            checkpointSnapshot,
            replayPoint);
        Checkpoints.Add(checkpoint);
        TrimSnapshotsToLimit();
        _cursor = Checkpoints.IndexOf(checkpoint);
        if (_cursor < 0)
        {
            return null;
        }

        MainFile.Logger.Info(
            $"Registered card choice checkpoint {_cursor + 1}/{Checkpoints.Count}: choice {replayPoint.ChoiceOrdinal + 1}.");
        ActionHistoryOverlay.Refresh();
        return checkpoint;
    }

    public static void RecordCardChoice(
        UndoCheckpoint checkpoint,
        IReadOnlyList<CardModel> selectedCards,
        AbstractModel? sourceModel)
    {
        int checkpointIndex = Checkpoints.IndexOf(checkpoint);
        if (checkpointIndex < 0)
        {
            return;
        }

        ActionHistoryEntry? existing = ActionEntries.FirstOrDefault(entry =>
            entry.Kind == ActionHistoryEntryKind.CardChoice &&
            entry.SnapshotIndex == checkpointIndex);
        string title = selectedCards.Count == 0
            ? UndoText.NoCardSelected
            : string.Join(", ", selectedCards.Select(card => card.Title));
        CardModel? card = selectedCards.FirstOrDefault();
        if (existing == null)
        {
            ActionEntries.Add(new ActionHistoryEntry(
                ActionHistoryEntryKind.CardChoice,
                title,
                UndoText.CardChoice,
                GetPlayerTurnNumber(CombatManager.Instance.DebugOnlyGetState()!),
                card: card,
                sourceModel: sourceModel)
            {
                SnapshotIndex = checkpointIndex,
            });
        }
        else
        {
            existing.UpdateCardChoice(title, card, sourceModel);
        }

        ActionHistoryOverlay.Refresh();
    }

    public static void BeginBranchFromCurrentCheckpoint()
    {
        TruncateTimelineAfterCursor();
    }

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
            TruncateTimelineAfterCursor();

            UndoCheckpoint checkpoint = UndoCheckpoint.ForSnapshot(snapshot);
            Checkpoints.Add(checkpoint);
            TrimSnapshotsToLimit();
            _cursor = Checkpoints.IndexOf(checkpoint);
            if (_cursor < 0)
            {
                _cursor = Checkpoints.Count - 1;
                MainFile.Logger.Info($"Discarded snapshot {reason} because the configured limit is full.");
                return -1;
            }

            MainFile.Logger.Info($"Captured snapshot {_cursor + 1}/{Checkpoints.Count}: {reason}");
            ActionHistoryOverlay.Refresh();
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
        if (_choiceNavigationInProgress)
        {
            MainFile.Logger.Info("Undo/redo input ignored while card choice navigation is settling.");
            return true;
        }

        if (CardChoiceCheckpointService.HasActiveChoice)
        {
            return TryMoveDuringCardChoice(direction);
        }

        if (!CanRestore(out string blockReason, out CombatState? state))
        {
            MainFile.Logger.Info($"Undo/redo blocked: {blockReason}");
            return false;
        }

        StartSessionIfNeeded(state!);
        int target = FindManualRestoreTarget(direction);
        if (target < 0)
        {
            MainFile.Logger.Info($"Undo/redo blocked: no {(direction < 0 ? "undo" : "redo")} snapshot. cursor={_cursor}, count={Checkpoints.Count}");
            return false;
        }

        return RestoreSnapshot(state!, target, direction < 0 ? "undo" : "redo");
    }

    private static bool TryMoveDuringCardChoice(int direction)
    {
        if (!CanCapture(strictPlayPhase: false, out string blockReason, out CombatState? state))
        {
            MainFile.Logger.Info($"Undo/redo blocked during card choice: {blockReason}");
            return false;
        }

        int target = FindManualRestoreTarget(direction);
        if (target < 0)
        {
            MainFile.Logger.Info(
                $"Undo/redo blocked during card choice: no {(direction < 0 ? "undo" : "redo")} snapshot.");
            return false;
        }

        _choiceNavigationInProgress = true;
        TaskHelper.RunSafely(MoveDuringCardChoiceAsync(
            state!,
            target,
            direction,
            Checkpoints[_cursor],
            _cursor));
        return true;
    }

    private static async Task MoveDuringCardChoiceAsync(
        CombatState state,
        int target,
        int direction,
        UndoCheckpoint rollbackCheckpoint,
        int rollbackCursor)
    {
        try
        {
            await CardChoiceCheckpointService.InterruptActiveChoiceAsync();
            if (!ReferenceEquals(CombatManager.Instance.DebugOnlyGetState(), state) ||
                !CombatManager.Instance.IsInProgress)
            {
                MainFile.Logger.Info(
                    "Canceled card choice navigation because the combat changed while settling.");
                return;
            }

            RestoreSnapshot(
                state,
                target,
                direction < 0 ? "undo from card choice" : "redo from card choice",
                rollbackCheckpoint,
                rollbackCursor);
        }
        catch (Exception ex)
        {
            MainFile.Logger.Error($"Failed to interrupt card choice: {ex}");
        }
        finally
        {
            _choiceNavigationInProgress = false;
        }
    }

    private static void TryRestoreTarget(int target, string source)
    {
        if (!CanRestore(out string blockReason, out CombatState? state))
        {
            MainFile.Logger.Info($"Snapshot restore blocked via {source}: {blockReason}");
            return;
        }

        StartSessionIfNeeded(state!);
        if (Checkpoints.Count == 0)
        {
            Capture("manual-initial", strictPlayPhase: true);
        }

        if (target == _cursor)
        {
            MainFile.Logger.Info($"Snapshot restore ignored via {source}: already at snapshot {target + 1}.");
            return;
        }

        if (target < 0 || target >= Checkpoints.Count)
        {
            MainFile.Logger.Info($"Snapshot restore blocked via {source}: target={target}, count={Checkpoints.Count}");
            return;
        }

        if (!Checkpoints[target].IsManualRestoreTarget)
        {
            MainFile.Logger.Info($"Snapshot restore blocked via {source}: target snapshot is not manually restorable.");
            return;
        }

        RestoreSnapshot(state!, target, source);
    }

    private static bool RestoreSnapshot(
        CombatState state,
        int target,
        string source,
        UndoCheckpoint? rollbackCheckpoint = null,
        int rollbackCursor = -1)
    {
        UndoCheckpoint checkpoint = Checkpoints[target];
        CombatSnapshot snapshot = checkpoint.Snapshot;
        if (!snapshot.BelongsTo(state!))
        {
            MainFile.Logger.Info("Undo/redo blocked: snapshot belongs to a different combat.");
            Reset();
            return false;
        }

        CombatSnapshot rollbackSnapshot = rollbackCheckpoint?.Snapshot!;
        if (rollbackCheckpoint == null)
        {
            try
            {
                rollbackSnapshot = CombatSnapshot.Capture(state, "restore-rollback");
                rollbackCursor = _cursor;
            }
            catch (Exception ex)
            {
                MainFile.Logger.Error($"Undo/redo blocked because rollback snapshot capture failed: {ex}");
                return false;
            }
        }

        _isRestoring = true;
        _readyCaptureRequestId++;
        _forceNextPlayerControlReadySnapshot = false;
        try
        {
            Stopwatch timer = Stopwatch.StartNew();
            snapshot.Restore(validate: false);
            if (checkpoint.CardChoice == null)
            {
                SnapshotValidator.ValidatePlayableState(state);
            }
            else
            {
                SnapshotValidator.ValidateCardChoiceReplayBaseState(state);
            }
            timer.Stop();
            _cursor = target;
            if (checkpoint.CardChoice != null)
            {
                CardChoiceCheckpointService.StartReplay(checkpoint.CardChoice);
            }

            MainFile.Logger.Info($"Restored snapshot {_cursor + 1}/{Checkpoints.Count} via {source}: {checkpoint.Reason}");
            if (timer.ElapsedMilliseconds >= 8)
            {
                MainFile.Logger.Info(
                    $"Snapshot restore took {timer.ElapsedMilliseconds} ms: {snapshot.Reason}");
            }
            ActionHistoryOverlay.Refresh();
            return true;
        }
        catch (Exception ex)
        {
            MainFile.Logger.Error($"Failed to restore snapshot: {ex}");
            try
            {
                rollbackSnapshot.Restore(validate: false);
                _cursor = rollbackCursor;
                if (rollbackCheckpoint?.CardChoice != null)
                {
                    CardChoiceCheckpointService.StartReplay(rollbackCheckpoint.CardChoice);
                }

                MainFile.Logger.Warn("Restored the pre-operation state after snapshot restore failure.");
            }
            catch (Exception rollbackEx)
            {
                MainFile.Logger.Error($"Failed to restore the pre-operation rollback snapshot: {rollbackEx}");
            }

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
        while (target >= 0 && target < Checkpoints.Count)
        {
            if (Checkpoints[target].IsManualRestoreTarget)
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
        bool needsSnapshot = Checkpoints.Count == 0 ||
                             PendingEntries.Count > 0 ||
                             _pendingTurnStartTurnNumber is > 0 ||
                             _forceNextPlayerControlReadySnapshot;
        if (!needsSnapshot)
        {
            return;
        }

        int snapshotIndex = Capture("PlayerControlReady", strictPlayPhase: true);
        if (snapshotIndex < 0)
        {
            snapshotIndex = _cursor;
        }

        if (snapshotIndex < 0)
        {
            return;
        }

        _forceNextPlayerControlReadySnapshot = false;
        foreach (ActionHistoryEntry pending in PendingEntries)
        {
            pending.SnapshotIndex = snapshotIndex;
            ActionEntries.Add(pending);
        }
        PendingEntries.Clear();

        AddPendingTurnStartEntry(snapshotIndex);

        TrimActionEntriesToSnapshots();
        ActionHistoryOverlay.Refresh();
    }

    private static void CapturePendingTurnStartBeforeAction(string reason)
    {
        if (_pendingTurnStartTurnNumber is not > 0)
        {
            return;
        }

        int snapshotIndex = Capture(
            $"PlayerControlReady:before-{reason}",
            strictPlayPhase: true);
        if (snapshotIndex < 0)
        {
            MainFile.Logger.Info(
                $"Could not finalize pending turn {_pendingTurnStartTurnNumber} before {reason}; keeping it queued.");
            return;
        }

        AddPendingTurnStartEntry(snapshotIndex);
        TrimActionEntriesToSnapshots();
        ActionHistoryOverlay.Refresh();
        MainFile.Logger.Info($"Finalized next-turn snapshot before {reason}.");
    }

    private static void CaptureInitialSnapshotBeforeFirstPlayerAction(string reason)
    {
        CombatState? state = CombatManager.Instance.DebugOnlyGetState();
        if (state == null)
        {
            return;
        }

        if (ReferenceEquals(_sessionState, state) && Checkpoints.Count > 0)
        {
            return;
        }

        int snapshotIndex = Capture(
            $"PlayerControlReady:before-first-{reason}",
            strictPlayPhase: true);
        if (snapshotIndex >= 0)
        {
            MainFile.Logger.Info($"Captured initial snapshot before first player action: {reason}.");
        }
    }

    private static void AddPendingTurnStartEntry(int snapshotIndex)
    {
        if (_pendingTurnStartTurnNumber is not > 0)
        {
            return;
        }

        int turnNumber = _pendingTurnStartTurnNumber.Value;
        if (!ActionEntries.Any(entry =>
                entry.Kind == ActionHistoryEntryKind.TurnStart &&
                entry.TurnNumber == turnNumber &&
                entry.SnapshotIndex == snapshotIndex))
        {
            ActionHistoryEntry entry = new(
                ActionHistoryEntryKind.TurnStart,
                UndoText.TurnStart,
                UndoText.Turn(turnNumber),
                turnNumber)
            {
                SnapshotIndex = snapshotIndex,
            };
            ActionEntries.Add(entry);
        }

        _pendingTurnStartTurnNumber = null;
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
        try
        {
            return IsRuntimeSettledCore(out reason);
        }
        catch (Exception ex)
        {
            MainFile.Logger.Error($"Failed to inspect undo/redo runtime state: {ex}");
            reason = "runtime state inspection failed";
            return false;
        }
    }

    private static bool IsRuntimeSettledCore(out string reason)
    {
        if (!RunManager.Instance.ActionQueueSet.IsEmpty)
        {
            CombatRuntimeStateCleanup.ResetRuntimeBlockerObservation();
            reason = "action queue is not empty";
            return false;
        }

        if (RunManager.Instance.ActionExecutor.IsRunning)
        {
            CombatRuntimeStateCleanup.ResetRuntimeBlockerObservation();
            reason = "action executor task is running";
            return false;
        }

        if (RunManager.Instance.ActionExecutor.CurrentlyRunningAction != null)
        {
            CombatRuntimeStateCleanup.ResetRuntimeBlockerObservation();
            reason = "action executor retained an action";
            return false;
        }

        IList waitingActions = ReflectionUtil.GetRequiredField<IList>(
            RunManager.Instance.ActionQueueSet,
            "_actionsWaitingForResumption");
        if (waitingActions.Count > 0)
        {
            CombatRuntimeStateCleanup.ResetRuntimeBlockerObservation();
            reason = "an action is waiting for a player choice";
            return false;
        }

        IList hookActions = ReflectionUtil.GetRequiredField<IList>(
            RunManager.Instance.ActionQueueSynchronizer,
            "_hookActions");
        IList requestedActions = ReflectionUtil.GetRequiredField<IList>(
            RunManager.Instance.ActionQueueSynchronizer,
            "_requestedActionsWaitingForPlayerTurn");
        if (hookActions.Count > 0 || requestedActions.Count > 0)
        {
            CombatRuntimeStateCleanup.ResetRuntimeBlockerObservation();
            reason = "synchronized actions are still pending";
            return false;
        }

        Dictionary<Player, int> effectDepth = ReflectionUtil.GetRequiredField<Dictionary<Player, int>>(
            CombatManager.Instance,
            "_cardOrPotionEffectDepth");
        if (effectDepth.Values.Any(depth => depth != 0))
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

        IList receivedChoices = ReflectionUtil.GetRequiredField<IList>(
            RunManager.Instance.PlayerChoiceSynchronizer,
            "_receivedChoices");
        if (receivedChoices.Count > 0)
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
        CardChoiceCheckpointService.Reset();
        _pendingTurnStartTurnNumber = null;
        _forceNextPlayerControlReadySnapshot = false;
        PendingEntries.Clear();
        _sessionState = state;
        MainFile.Logger.Info("Started undo/redo session for current combat.");
        ActionHistoryOverlay.Refresh();
    }

    private static void ClearHistory()
    {
        Checkpoints.Clear();
        ActionEntries.Clear();
        _cursor = -1;
    }

    private static void TruncateTimelineAfterCursor()
    {
        if (_cursor >= Checkpoints.Count - 1)
        {
            return;
        }

        Checkpoints.RemoveRange(_cursor + 1, Checkpoints.Count - _cursor - 1);
        ActionEntries.RemoveAll(entry => entry.SnapshotIndex > _cursor);
    }

    private static void TrimSnapshotsToLimit()
    {
        int maxSnapshots = Math.Max(0, UndoAndRestartConfig.SnapshotLimit);
        while (Checkpoints.Count > maxSnapshots)
        {
            int removedSnapshotIndex = Checkpoints.Count > 1 ? 1 : 0;
            Checkpoints.RemoveAt(removedSnapshotIndex);
            if (_cursor >= removedSnapshotIndex)
            {
                _cursor--;
            }

            foreach (ActionHistoryEntry entry in ActionEntries)
            {
                if (entry.SnapshotIndex == removedSnapshotIndex)
                {
                    entry.SnapshotIndex = -1;
                }
                else if (entry.SnapshotIndex > removedSnapshotIndex)
                {
                    entry.SnapshotIndex--;
                }
            }

            ActionEntries.RemoveAll(entry => entry.SnapshotIndex < 0);
        }
    }

    private static void TrimActionEntriesToSnapshots()
    {
        ActionEntries.RemoveAll(entry => entry.SnapshotIndex < 0 || entry.SnapshotIndex >= Checkpoints.Count);
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

internal sealed class ActionHistoryEntry
{
    public ActionHistoryEntry(
        ActionHistoryEntryKind kind,
        string title,
        string detail,
        int turnNumber,
        CardModel? card = null,
        PotionModel? potion = null,
        AbstractModel? sourceModel = null)
    {
        Kind = kind;
        Title = title;
        Detail = detail;
        TurnNumber = turnNumber;
        Card = card;
        Potion = potion;
        SourceModel = sourceModel;
    }

    public ActionHistoryEntryKind Kind { get; }
    public string Title { get; private set; }
    public string Detail { get; }
    public int TurnNumber { get; }
    public CardModel? Card { get; private set; }
    public PotionModel? Potion { get; }
    public AbstractModel? SourceModel { get; private set; }
    public int SnapshotIndex { get; set; } = -1;

    public void UpdateCardChoice(
        string title,
        CardModel? card,
        AbstractModel? sourceModel)
    {
        Title = title;
        Card = card;
        SourceModel = sourceModel;
    }
}

internal enum ActionHistoryEntryKind
{
    Card,
    CardChoice,
    Potion,
    DiscardPotion,
    TurnStart
}

internal sealed class UndoCheckpoint
{
    private UndoCheckpoint(CombatSnapshot snapshot, CardChoiceReplayPoint? cardChoice)
    {
        Snapshot = snapshot;
        CardChoice = cardChoice;
    }

    public CombatSnapshot Snapshot { get; }
    public CardChoiceReplayPoint? CardChoice { get; }
    public string Reason => CardChoice == null
        ? Snapshot.Reason
        : $"CardChoice:{CardChoice.ChoiceOrdinal + 1}";
    public bool IsManualRestoreTarget =>
        CardChoice != null || Snapshot.IsManualRestoreTarget;

    public static UndoCheckpoint ForSnapshot(CombatSnapshot snapshot)
    {
        return new UndoCheckpoint(snapshot, null);
    }

    public static UndoCheckpoint ForCardChoice(
        CombatSnapshot snapshot,
        CardChoiceReplayPoint cardChoice)
    {
        return new UndoCheckpoint(snapshot, cardChoice);
    }
}
