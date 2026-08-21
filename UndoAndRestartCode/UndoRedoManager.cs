using Godot;
using System.Collections;
using System.Diagnostics;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Actions;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Combat;
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
    private static readonly Dictionary<GameAction, ActionHistoryEntry> PendingEntriesByAction = new();
    private static readonly Dictionary<GameAction, int> ActionGenerations = new();
    private static bool _choiceNavigationInProgress;
    private static bool _navigationInProgress;
    private static int _navigationRequestId;
    private static int _timelineGeneration;
    private static CancellationTokenSource? _restoreCancellation;
    private static bool _forceNextPlayerControlReadySnapshot;
    private static int? _queuedNavigationDirection;
    private static UndoCheckpoint? _queuedNavigationCheckpoint;
    private static string? _queuedNavigationSource;

    public static void Reset()
    {
        ClearHistory();
        _sessionState = null;
        _isRestoring = false;
        _choiceNavigationInProgress = false;
        _navigationInProgress = false;
        _navigationRequestId++;
        _timelineGeneration++;
        _restoreCancellation?.Cancel();
        _restoreCancellation?.Dispose();
        _restoreCancellation = null;
        _forceNextPlayerControlReadySnapshot = false;
        ClearQueuedNavigation();
        _pendingTurnStartTurnNumber = null;
        PendingEntries.Clear();
        PendingEntriesByAction.Clear();
        ActionGenerations.Clear();
        CardChoiceCheckpointService.Reset();
        CombatRuntimeStateCleanup.ResetRuntimeBlockerObservation();
        _readyCaptureRequestId++;
        MainFile.Logger.Info("Combat history reset.");
        ActionHistoryOverlay.Refresh();
    }

    public static bool HandleUndoKey()
    {
        MainFile.Logger.Info("Left arrow pressed.");
        return RequestMove(-1);
    }

    public static bool HandleRedoKey()
    {
        MainFile.Logger.Info("Right arrow pressed.");
        return RequestMove(1);
    }

    public static void CaptureBeforeAction(GameAction action, string reason)
    {
        CombatState? state = CombatManager.Instance.DebugOnlyGetState();
        if (state != null)
        {
            StartSessionIfNeeded(state);
            ActionGenerations[action] = _timelineGeneration;
        }

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

        CapturePlayerControlReadySnapshot(allowTimelineBranch: true);
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

    public static async Task CaptureAfterActionAsync(Task original, GameAction action, string reason)
    {
        await CaptureAfterActionAsync(original, action, reason, null);
    }

    public static async Task CaptureAfterActionAsync(
        Task original,
        GameAction action,
        string reason,
        ActionHistoryEntry? entry)
    {
        int generation = ActionGenerations.GetValueOrDefault(action, -1);
        try
        {
            await original;
        }
        catch
        {
            DiscardPendingEntry(action);
            throw;
        }

        if (_choiceNavigationInProgress ||
            generation != _timelineGeneration ||
            !IsActionFromCurrentTimeline(action))
        {
            MainFile.Logger.Debug(
                $"Skipped stale completed action recording: {reason}");
            DiscardPendingEntry(action);
            return;
        }

        if (entry != null)
        {
            PendingEntries.Add(entry);
            PendingEntriesByAction[action] = entry;
        }

        RequestPlayerControlReadyCapture($"{reason}:settled");
    }

    public static void CaptureCompletedPlayerAction(GameAction action)
    {
        if (_choiceNavigationInProgress)
        {
            MainFile.Logger.Debug(
                $"Skipped interrupted choice action boundary: {action.GetType().Name}");
            DiscardPendingEntry(action);
            ActionGenerations.Remove(action);
            return;
        }

        if (!IsActionFromCurrentTimeline(action))
        {
            MainFile.Logger.Debug(
                $"Skipped stale action boundary: {action.GetType().Name}");
            DiscardPendingEntry(action);
            ActionGenerations.Remove(action);
            return;
        }

        if (action.State == GameActionState.Canceled || action.Exception != null)
        {
            MainFile.Logger.Warn(
                $"Skipped failed action boundary: {action.GetType().Name}.");
            DiscardPendingEntry(action);
            ActionGenerations.Remove(action);
            return;
        }

        if (action is not PlayCardAction &&
            action is not UsePotionAction &&
            action is not DiscardPotionGameAction)
        {
            ActionGenerations.Remove(action);
            return;
        }

        if (_navigationInProgress && !_isRestoring)
        {
            ActionGenerations.Remove(action);
            _forceNextPlayerControlReadySnapshot = true;
            RequestPlayerControlReadyCapture(
                $"{action.GetType().Name}:navigation-deferred");
            MainFile.Logger.Debug(
                $"Deferred action boundary until navigation completes: {action.GetType().Name}");
            return;
        }

        int snapshotIndex = Capture(
            $"{action.GetType().Name}:boundary",
            strictPlayPhase: true,
            allowTimelineBranch: true);
        if (snapshotIndex < 0)
        {
            DiscardPendingEntry(action);
            ActionGenerations.Remove(action);
            return;
        }

        if (PendingEntriesByAction.Remove(action, out ActionHistoryEntry? pendingEntry))
        {
            PendingEntries.Remove(pendingEntry);
            pendingEntry.SnapshotIndex = snapshotIndex;
            ActionEntries.Add(pendingEntry);
            TrimActionEntriesToSnapshots();
        }

        ActionGenerations.Remove(action);
        ActionHistoryOverlay.Refresh();
    }

    public static bool IsActionFromCurrentTimeline(GameAction action)
    {
        return ActionGenerations.TryGetValue(action, out int generation) &&
               generation == _timelineGeneration;
    }

    private static void DiscardPendingEntry(GameAction action)
    {
        if (PendingEntriesByAction.Remove(action, out ActionHistoryEntry? entry))
        {
            PendingEntries.Remove(entry);
        }
    }

    public static void ScheduleFailedCardChoiceReplayRecovery(
        CardChoiceReplayPoint replayPoint,
        Exception replayException)
    {
        MainFile.Logger.Error(
            $"Card choice replay failed; scheduling rollback to its base snapshot: {replayException}");
        TaskHelper.RunSafely(
            RecoverFailedCardChoiceReplayAsync(replayPoint));
    }

    private static async Task RecoverFailedCardChoiceReplayAsync(
        CardChoiceReplayPoint replayPoint)
    {
        for (int frame = 0; frame < 240 && _navigationInProgress; frame++)
        {
            await AwaitProcessFramesAsync(1, CancellationToken.None);
        }

        if (_navigationInProgress)
        {
            MainFile.Logger.Error(
                "Could not serialize failed card choice recovery behind timeline navigation.");
            return;
        }

        CombatState? state = CombatManager.Instance.DebugOnlyGetState();
        if (state == null ||
            !CombatManager.Instance.IsInProgress ||
            !ReferenceEquals(_sessionState, state))
        {
            MainFile.Logger.Warn(
                "Skipped failed card choice replay recovery because the combat changed.");
            return;
        }

        _navigationInProgress = true;
        int requestId = ++_navigationRequestId;
        _restoreCancellation?.Cancel();
        _restoreCancellation?.Dispose();
        _restoreCancellation = new CancellationTokenSource();
        CancellationToken cancellationToken = _restoreCancellation.Token;
        _readyCaptureRequestId++;
        _forceNextPlayerControlReadySnapshot = false;
        RestoreExecutionScope? restoreScope = null;
        try
        {
            restoreScope = RestoreExecutionScope.Acquire(state);
            if (!await WaitForRestorableRuntimeAsync(
                    state,
                    requestId,
                    "failed card choice recovery",
                    cancellationToken))
            {
                return;
            }

            int firstChoiceIndex = Checkpoints.FindIndex(checkpoint =>
                ReferenceEquals(
                    checkpoint.CardChoice?.Sequence,
                    replayPoint.Sequence));
            if (firstChoiceIndex < 0)
            {
                MainFile.Logger.Warn(
                    "Skipped failed card choice replay recovery because its timeline no longer exists.");
                return;
            }

            CombatSnapshot replayBaseSnapshot = Checkpoints[firstChoiceIndex].Snapshot;
            bool previousCheckpointUsesReplayBase =
                firstChoiceIndex > 0 &&
                ReferenceEquals(
                    Checkpoints[firstChoiceIndex - 1].Snapshot,
                    replayBaseSnapshot);

            _timelineGeneration++;
            ActionGenerations.Clear();
            ClearPendingActionMetadata();
            CardChoiceCheckpointService.InvalidateTimelineAsyncWork();
            _isRestoring = true;
            restoreScope.Reassert();
            await replayBaseSnapshot.RestoreAsync(
                validate: false,
                cancellationToken);
            SnapshotValidator.ValidatePlayableStateDuringRestore(state);

            Checkpoints.RemoveRange(
                firstChoiceIndex,
                Checkpoints.Count - firstChoiceIndex);
            ActionEntries.RemoveAll(entry =>
                entry.SnapshotIndex >= firstChoiceIndex);
            _pendingTurnStartTurnNumber = null;

            if (previousCheckpointUsesReplayBase)
            {
                _cursor = firstChoiceIndex - 1;
            }
            else
            {
                Checkpoints.Add(UndoCheckpoint.ForSnapshot(replayBaseSnapshot));
                _cursor = Checkpoints.Count - 1;
            }

            CardChoiceCheckpointService.Reset();
            TrimActionEntriesToSnapshots();
            ActionHistoryOverlay.Refresh();
            MainFile.Logger.Warn(
                "Recovered the state before the failed card choice replay and removed its invalid timeline.");
        }
        catch (Exception recoveryException)
        {
            MainFile.Logger.Error(
                $"Failed to recover the card choice replay base snapshot: {recoveryException}");
        }
        finally
        {
            _isRestoring = false;
            if (requestId == _navigationRequestId)
            {
                _navigationInProgress = false;
            }
            restoreScope?.Dispose();
        }
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
        if (_isRestoring ||
            _navigationInProgress ||
            !RunManager.Instance.IsSingleplayerOrFakeMultiplayer)
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
    public static bool IsNavigationInProgress => _navigationInProgress;

    public static UndoCheckpoint? RegisterCardChoiceCheckpoint(
        CardChoiceReplayPoint replayPoint,
        CombatSnapshot? replayBaseSnapshot = null)
    {
        if (_isRestoring ||
            _navigationInProgress ||
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
        if (_isRestoring || _navigationInProgress)
        {
            return;
        }

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
        if (_isRestoring || _navigationInProgress)
        {
            return;
        }

        TruncateTimelineAfterCursor();
    }

    public static void TryRestoreSnapshot(int target)
    {
        if (target < 0 || target >= Checkpoints.Count)
        {
            MainFile.Logger.Info(
                $"Snapshot restore blocked via history click: target={target}, count={Checkpoints.Count}");
            return;
        }

        RequestRestore(Checkpoints[target], "history click");
    }

    private static int Capture(
        string reason,
        bool strictPlayPhase,
        bool allowTimelineBranch = false)
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
            if (_cursor < Checkpoints.Count - 1 && !allowTimelineBranch)
            {
                MainFile.Logger.Debug(
                    $"Skipped automatic capture inside redo history: {reason}");
                return -1;
            }

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

    private static bool RequestMove(int direction)
    {
        if (_navigationInProgress)
        {
            QueueNavigationMove(direction);
            return true;
        }

        int target = FindManualRestoreTarget(direction);
        if (target < 0)
        {
            MainFile.Logger.Info(
                $"Undo/redo blocked: no {(direction < 0 ? "undo" : "redo")} snapshot. cursor={_cursor}, count={Checkpoints.Count}");
            return false;
        }

        return RequestRestore(
            Checkpoints[target],
            direction < 0 ? "undo" : "redo");
    }

    private static bool RequestRestore(UndoCheckpoint targetCheckpoint, string source)
    {
        if (_navigationInProgress)
        {
            QueueNavigationCheckpoint(targetCheckpoint, source);
            return true;
        }

        if (!targetCheckpoint.IsManualRestoreTarget)
        {
            MainFile.Logger.Info(
                $"Snapshot restore blocked via {source}: target is not manually restorable.");
            return false;
        }

        bool hasActiveChoice = CardChoiceCheckpointService.HasActiveChoice;
        bool canBegin = hasActiveChoice
            ? CanCapture(
                strictPlayPhase: false,
                out string blockReason,
                out CombatState? state)
            : CanRestore(out blockReason, out state);
        if (!canBegin || state == null)
        {
            MainFile.Logger.Info(
                $"Snapshot restore blocked via {source}: {blockReason}");
            return false;
        }

        StartSessionIfNeeded(state);
        if (!Checkpoints.Contains(targetCheckpoint))
        {
            MainFile.Logger.Info(
                $"Snapshot restore blocked via {source}: target is not on the current timeline.");
            return false;
        }

        _navigationInProgress = true;
        int requestId = ++_navigationRequestId;
        _restoreCancellation?.Cancel();
        _restoreCancellation?.Dispose();
        _restoreCancellation = new CancellationTokenSource();
        CancellationToken cancellationToken = _restoreCancellation.Token;
        // Input and UI signal callbacks can run while Godot is traversing the scene
        // tree. CallDeferred leaves that traversal without waiting for another
        // rendered frame, so the old and restored states are never drawn in between.
        Callable.From(() =>
        {
            TaskHelper.RunSafely(ProcessNavigationRequestAsync(
                state,
                targetCheckpoint,
                source,
                requestId,
                cancellationToken));
        })
            .CallDeferred();
        return true;
    }

    private static async Task ProcessNavigationRequestAsync(
        CombatState state,
        UndoCheckpoint targetCheckpoint,
        string source,
        int requestId,
        CancellationToken cancellationToken)
    {
        bool wasChoiceNavigation = CardChoiceCheckpointService.HasActiveChoice;
        bool wasHookChoiceNavigation = CardChoiceCheckpointService.IsActiveHookChoice;
        UndoCheckpoint? rollbackCheckpoint =
            wasChoiceNavigation && _cursor >= 0 && _cursor < Checkpoints.Count
                ? Checkpoints[_cursor]
                : null;
        int rollbackCursor = _cursor;
        RestoreExecutionScope? restoreScope = null;
        bool dispatchQueuedNavigation = false;
        try
        {
            ThrowIfNavigationBecameStale(state, requestId, cancellationToken);
            restoreScope = RestoreExecutionScope.Acquire(state);

            if (!wasChoiceNavigation &&
                (!RunManager.Instance.ActionQueueSet.IsEmpty ||
                 RunManager.Instance.ActionExecutor.IsRunning ||
                 RunManager.Instance.ActionExecutor.CurrentlyRunningAction != null))
            {
                MainFile.Logger.Info(
                    $"Snapshot restore canceled via {source}: action runtime changed before the navigation lock.");
                return;
            }

            if (wasChoiceNavigation)
            {
                _choiceNavigationInProgress = true;
                await CardChoiceCheckpointService.InterruptActiveChoiceAsync();
                restoreScope.Reassert();
                ThrowIfNavigationBecameStale(state, requestId, cancellationToken);
            }

            if (!await WaitForRestorableRuntimeAsync(
                    state,
                    requestId,
                    source,
                    cancellationToken,
                    allowInterruptedHookPhase: wasHookChoiceNavigation))
            {
                return;
            }

            int target = Checkpoints.IndexOf(targetCheckpoint);
            if (target < 0)
            {
                MainFile.Logger.Info(
                    $"Snapshot restore canceled via {source}: target left the current timeline.");
                return;
            }

            if (target == _cursor)
            {
                MainFile.Logger.Info(
                    $"Snapshot restore ignored via {source}: already at snapshot {target + 1}.");
                return;
            }

            await RestoreSnapshotAsync(
                state,
                targetCheckpoint,
                source,
                rollbackCheckpoint,
                rollbackCursor,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            MainFile.Logger.Info($"Canceled stale snapshot navigation via {source}.");
        }
        catch (Exception ex)
        {
            MainFile.Logger.Error($"Snapshot navigation failed via {source}: {ex}");
        }
        finally
        {
            if (requestId == _navigationRequestId)
            {
                _navigationInProgress = false;
                _choiceNavigationInProgress = false;
                dispatchQueuedNavigation =
                    _queuedNavigationDirection.HasValue ||
                    _queuedNavigationCheckpoint != null;
            }
            restoreScope?.Dispose();
            if (dispatchQueuedNavigation)
            {
                Callable.From(DispatchQueuedNavigation).CallDeferred();
            }
        }
    }

    private static void QueueNavigationMove(int direction)
    {
        _queuedNavigationDirection = direction < 0 ? -1 : 1;
        _queuedNavigationCheckpoint = null;
        _queuedNavigationSource = direction < 0 ? "queued undo" : "queued redo";
        MainFile.Logger.Info(
            $"Queued the latest {(direction < 0 ? "undo" : "redo")} input behind the active restore.");
    }

    private static void QueueNavigationCheckpoint(
        UndoCheckpoint checkpoint,
        string source)
    {
        _queuedNavigationDirection = null;
        _queuedNavigationCheckpoint = checkpoint;
        _queuedNavigationSource = source;
        MainFile.Logger.Info(
            $"Queued the latest {source} request behind the active restore.");
    }

    private static void DispatchQueuedNavigation()
    {
        if (_navigationInProgress)
        {
            return;
        }

        int? direction = _queuedNavigationDirection;
        UndoCheckpoint? checkpoint = _queuedNavigationCheckpoint;
        string source = _queuedNavigationSource ?? "queued navigation";
        ClearQueuedNavigation();

        if (direction.HasValue)
        {
            RequestMove(direction.Value);
        }
        else if (checkpoint != null)
        {
            RequestRestore(checkpoint, source);
        }
    }

    private static void ClearQueuedNavigation()
    {
        _queuedNavigationDirection = null;
        _queuedNavigationCheckpoint = null;
        _queuedNavigationSource = null;
    }

    private static async Task<bool> WaitForRestorableRuntimeAsync(
        CombatState state,
        int requestId,
        string source,
        CancellationToken cancellationToken,
        bool allowInterruptedHookPhase = false)
    {
        string blockReason = "runtime is not settled";
        for (int frame = 0; frame < 240; frame++)
        {
            ThrowIfNavigationBecameStale(state, requestId, cancellationToken);
            if (CanRestoreDuringNavigation(
                    state,
                    allowInterruptedHookPhase,
                    out blockReason))
            {
                return true;
            }

            if (frame == 0 || frame == 30 || frame == 120)
            {
                MainFile.Logger.Info(
                    $"Waiting to restore via {source}: {blockReason}");
            }

            await AwaitProcessFramesAsync(1, cancellationToken);
        }

        MainFile.Logger.Warn(
            $"Snapshot restore blocked via {source}: {blockReason}");
        return false;
    }

    private static async Task RestoreSnapshotAsync(
        CombatState state,
        UndoCheckpoint checkpoint,
        string source,
        UndoCheckpoint? rollbackCheckpoint,
        int rollbackCursor,
        CancellationToken cancellationToken)
    {
        CombatSnapshot snapshot = checkpoint.Snapshot;
        if (!snapshot.BelongsTo(state))
        {
            MainFile.Logger.Info("Undo/redo blocked: snapshot belongs to a different combat.");
            Reset();
            return;
        }

        CombatSnapshot rollbackSnapshot;
        if (rollbackCheckpoint == null)
        {
            try
            {
                rollbackSnapshot = CombatSnapshot.Capture(state, "restore-rollback");
                rollbackCursor = _cursor;
            }
            catch (Exception ex)
            {
                MainFile.Logger.Error(
                    $"Undo/redo blocked because rollback snapshot capture failed: {ex}");
                return;
            }
        }
        else
        {
            rollbackSnapshot = rollbackCheckpoint.Snapshot;
        }

        _isRestoring = true;
        _readyCaptureRequestId++;
        _forceNextPlayerControlReadySnapshot = false;
        _timelineGeneration++;
        ActionGenerations.Clear();
        ClearPendingActionMetadata();
        CardChoiceCheckpointService.InvalidateTimelineAsyncWork();

        bool restoreStarted = false;
        try
        {
            Stopwatch timer = Stopwatch.StartNew();
            restoreStarted = true;
            await snapshot.RestoreAsync(validate: false, cancellationToken);
            if (checkpoint.CardChoice == null)
            {
                SnapshotValidator.ValidatePlayableStateDuringRestore(state);
            }
            else
            {
                SnapshotValidator.ValidateCardChoiceReplayBaseStateDuringRestore(state);
            }

            timer.Stop();
            int target = Checkpoints.IndexOf(checkpoint);
            if (target < 0)
            {
                throw new InvalidOperationException(
                    "The restored checkpoint left the current timeline.");
            }

            _cursor = target;
            if (checkpoint.CardChoice != null)
            {
                CardChoiceCheckpointService.StartReplay(checkpoint.CardChoice);
            }

            MainFile.Logger.Info(
                $"Restored snapshot {_cursor + 1}/{Checkpoints.Count} via {source}: {checkpoint.Reason}");
            if (timer.ElapsedMilliseconds >= 8)
            {
                MainFile.Logger.Info(
                    $"Snapshot restore took {timer.ElapsedMilliseconds} ms: {snapshot.Reason}");
            }

            ActionHistoryOverlay.Refresh();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            MainFile.Logger.Error($"Failed to restore snapshot: {ex}");
            if (!restoreStarted)
            {
                return;
            }

            try
            {
                await rollbackSnapshot.RestoreAsync(validate: false, cancellationToken);
                _cursor = rollbackCursor;
                if (rollbackCheckpoint?.CardChoice != null)
                {
                    SnapshotValidator.ValidateCardChoiceReplayBaseStateDuringRestore(state);
                    CardChoiceCheckpointService.StartReplay(rollbackCheckpoint.CardChoice);
                }
                else
                {
                    SnapshotValidator.ValidatePlayableStateDuringRestore(state);
                }

                MainFile.Logger.Warn(
                    "Restored the pre-operation state after snapshot restore failure.");
            }
            catch (Exception rollbackEx)
            {
                MainFile.Logger.Error(
                    $"Failed to restore the pre-operation rollback snapshot: {rollbackEx}");
            }
        }
        finally
        {
            _isRestoring = false;
        }
    }

    private static void ThrowIfNavigationBecameStale(
        CombatState state,
        int requestId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (requestId != _navigationRequestId ||
            !CombatManager.Instance.IsInProgress ||
            !ReferenceEquals(CombatManager.Instance.DebugOnlyGetState(), state) ||
            !ReferenceEquals(_sessionState, state))
        {
            throw new OperationCanceledException(
                "The combat or timeline navigation request changed.",
                cancellationToken);
        }
    }

    private static async Task AwaitProcessFramesAsync(
        int count,
        CancellationToken cancellationToken)
    {
        for (int frame = 0; frame < count; frame++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Engine.GetMainLoop() is SceneTree sceneTree)
            {
                await sceneTree.ToSignal(sceneTree, SceneTree.SignalName.ProcessFrame);
            }
            else
            {
                await Task.Delay(16, cancellationToken);
            }
        }
    }

    private sealed class RestoreExecutionScope : IDisposable
    {
        private readonly CombatState _state;
        private readonly NPlayerHand? _hand;
        private bool _disposed;

        private RestoreExecutionScope(CombatState state, NPlayerHand? hand)
        {
            _state = state;
            _hand = hand;
        }

        public static RestoreExecutionScope Acquire(CombatState state)
        {
            if (!ReferenceEquals(CombatManager.Instance.DebugOnlyGetState(), state))
            {
                throw new InvalidOperationException(
                    "Cannot acquire the restore lock for a different combat.");
            }

            NPlayerHand? hand = NPlayerHand.Instance;
            bool managerLocked = false;
            bool executorPaused = false;
            bool queuesPaused = false;
            try
            {
                ReflectionUtil.SetRequiredField(
                    CombatManager.Instance,
                    "_playerActionsDisabled",
                    true);
                managerLocked = CombatManager.Instance.PlayerActionsDisabled;
                if (!managerLocked)
                {
                    throw new InvalidOperationException(
                        "CombatManager refused the restore input lock.");
                }

                RunManager.Instance.ActionExecutor.Pause();
                executorPaused = true;
                RunManager.Instance.ActionQueueSet.PauseAllPlayerQueues();
                queuesPaused = true;
                if (hand != null && GodotObject.IsInstanceValid(hand))
                {
                    ReflectionUtil.Method(typeof(NPlayerHand), "AnimDisable")
                        ?.Invoke(hand, null);
                }

                MainFile.Logger.Debug("Acquired restore execution lock.");
                return new RestoreExecutionScope(state, hand);
            }
            catch
            {
                if (queuesPaused)
                {
                    RunManager.Instance.ActionExecutor.Unpause();
                    RunManager.Instance.ActionQueueSet.UnpauseAllPlayerQueues();
                    executorPaused = false;
                }

                if (executorPaused)
                {
                    RunManager.Instance.ActionExecutor.Unpause();
                }

                if (managerLocked)
                {
                    ReflectionUtil.SetField(
                        CombatManager.Instance,
                        "_playerActionsDisabled",
                        false);
                }

                throw;
            }
        }

        public void Reassert()
        {
            if (_disposed ||
                !CombatManager.Instance.IsInProgress ||
                !ReferenceEquals(CombatManager.Instance.DebugOnlyGetState(), _state))
            {
                throw new InvalidOperationException(
                    "Cannot reassert a stale restore execution lock.");
            }

            ReflectionUtil.SetRequiredField(
                CombatManager.Instance,
                "_playerActionsDisabled",
                true);
            RunManager.Instance.ActionExecutor.Pause();
            RunManager.Instance.ActionQueueSet.PauseAllPlayerQueues();
            if (_hand != null && GodotObject.IsInstanceValid(_hand))
            {
                ReflectionUtil.Method(typeof(NPlayerHand), "AnimDisable")
                    ?.Invoke(_hand, null);
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (!CombatManager.Instance.IsInProgress ||
                !ReferenceEquals(CombatManager.Instance.DebugOnlyGetState(), _state))
            {
                return;
            }

            try
            {
                ReflectionUtil.SetRequiredField(
                    CombatManager.Instance,
                    "_playerActionsDisabled",
                    false);
            }
            catch (Exception ex)
            {
                MainFile.Logger.Error(
                    $"Failed to release the CombatManager restore lock: {ex}");
            }

            try
            {
                if (_hand != null && GodotObject.IsInstanceValid(_hand))
                {
                    ReflectionUtil.Method(typeof(NPlayerHand), "AnimEnable")
                        ?.Invoke(_hand, null);
                }
            }
            catch (Exception ex)
            {
                MainFile.Logger.Error($"Failed to re-enable the hand after restore: {ex}");
            }

            try
            {
                RunManager.Instance.ActionExecutor.Unpause();
            }
            catch (Exception ex)
            {
                MainFile.Logger.Error($"Failed to unpause the action executor: {ex}");
            }

            try
            {
                RunManager.Instance.ActionQueueSet.UnpauseAllPlayerQueues();
            }
            catch (Exception ex)
            {
                MainFile.Logger.Error($"Failed to unpause player action queues: {ex}");
            }

            MainFile.Logger.Debug("Released restore execution lock.");
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

    private static void CapturePlayerControlReadySnapshot(
        bool allowTimelineBranch = false)
    {
        bool needsSnapshot = Checkpoints.Count == 0 ||
                             PendingEntries.Count > 0 ||
                             _pendingTurnStartTurnNumber is > 0 ||
                             _forceNextPlayerControlReadySnapshot;
        if (!needsSnapshot)
        {
            return;
        }

        bool insideRedoHistory = _cursor >= 0 && _cursor < Checkpoints.Count - 1;
        int snapshotIndex = Capture(
            "PlayerControlReady",
            strictPlayPhase: true,
            allowTimelineBranch: allowTimelineBranch);
        if (snapshotIndex < 0)
        {
            if (insideRedoHistory && !allowTimelineBranch)
            {
                MainFile.Logger.Info(
                    "Discarded stale automatic capture metadata inside redo history.");
                ClearPendingActionMetadata();
                _pendingTurnStartTurnNumber = null;
                _forceNextPlayerControlReadySnapshot = false;
                return;
            }

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
        PendingEntriesByAction.Clear();

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
            strictPlayPhase: true,
            allowTimelineBranch: true);
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
            strictPlayPhase: true,
            allowTimelineBranch: true);
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

    private static bool IsRuntimeSettled(
        out string reason,
        bool allowRestoreInputLock = false)
    {
        try
        {
            return IsRuntimeSettledCore(out reason, allowRestoreInputLock);
        }
        catch (Exception ex)
        {
            MainFile.Logger.Error($"Failed to inspect undo/redo runtime state: {ex}");
            reason = "runtime state inspection failed";
            return false;
        }
    }

    private static bool IsRuntimeSettledCore(
        out string reason,
        bool allowRestoreInputLock)
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
                    effectDepth,
                    allowRestoreInputLock))
            {
                return IsRuntimeSettled(out reason, allowRestoreInputLock);
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
                    receivedChoices,
                    allowRestoreInputLock))
            {
                return IsRuntimeSettled(out reason, allowRestoreInputLock);
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
        _timelineGeneration++;
        _pendingTurnStartTurnNumber = null;
        _forceNextPlayerControlReadySnapshot = false;
        ClearPendingActionMetadata();
        ActionGenerations.Clear();
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

    private static void ClearPendingActionMetadata()
    {
        PendingEntries.Clear();
        PendingEntriesByAction.Clear();
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

    private static bool CanRestoreDuringNavigation(
        CombatState expectedState,
        bool allowInterruptedHookPhase,
        out string reason)
    {
        CombatState? state = CombatManager.Instance.DebugOnlyGetState();
        if (!RunManager.Instance.IsSingleplayerOrFakeMultiplayer)
        {
            reason = "multiplayer run";
            return false;
        }

        if (!CombatManager.Instance.IsInProgress ||
            state == null ||
            !ReferenceEquals(state, expectedState))
        {
            reason = "combat changed";
            return false;
        }

        if (state.CurrentSide != CombatSide.Player)
        {
            reason = "not player side";
            return false;
        }

        if (state.Players.Any(player => player.PlayerCombatState == null))
        {
            reason = "player combat state is unavailable";
            return false;
        }

        bool hasNonPlayPhase = state.Players.Any(player =>
            player.PlayerCombatState!.Phase != PlayerTurnPhase.Play);
        if (allowInterruptedHookPhase &&
            state.Players.Any(player =>
                player.PlayerCombatState!.Phase == PlayerTurnPhase.None))
        {
            reason = "interrupted turn hook has no active player phase";
            return false;
        }

        // A turn-hook choice (Toolbox/BeforeHandDraw and the equivalent end-turn
        // hooks) intentionally pauses outside Play. Once that choice has been
        // interrupted and its action runtime is empty, requiring Play creates a
        // deadlock: only the snapshot restore can advance us to the target phase.
        if (!allowInterruptedHookPhase && hasNonPlayPhase)
        {
            reason = "player turn phase is not Play";
            return false;
        }

        if (!IsRuntimeSettled(out reason, allowRestoreInputLock: true))
        {
            return false;
        }

        if (!allowInterruptedHookPhase &&
            (CombatManager.Instance.EndingPlayerTurnPhaseOne ||
             CombatManager.Instance.EndingPlayerTurnPhaseTwo) &&
            !CombatRuntimeStateCleanup.TryClearStaleEndingTurnFlagsIfPlayerControlAvailable(
                state,
                allowRestoreInputLock: true))
        {
            reason = "ending player turn";
            return false;
        }

        reason = "";
        return true;
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
