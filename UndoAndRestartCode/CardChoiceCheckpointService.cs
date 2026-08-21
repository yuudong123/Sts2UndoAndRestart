using System.Collections;
using Godot;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Actions;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Nodes.Vfx;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.TestSupport;

namespace UndoAndRestartCode;

internal static class CardChoiceCheckpointService
{
    private sealed class HookReplaySession
    {
        public HookReplaySession(ReplayCardSelector selector)
        {
            Selector = selector;
        }

        public ReplayCardSelector Selector { get; }
        public Task? ExecutionTask { get; set; }

        public void RequestInterruption()
        {
            Selector.RequestInterruption();
        }
    }

    private static readonly Dictionary<GameAction, CardChoiceReplaySequence> OriginalSequences = new();
    private static HookChoiceRecording? _activeHookChoiceRecording;
    private static CardChoiceRequest? _preparedRequest;
    private static CardChoiceRecord? _activeOriginalChoice;
    private static GameAction? _activeOriginalAction;
    private static ReplayCardSelector? _activeReplay;
    private static IDisposable? _selectorScope;
    private static GameAction? _replayAction;
    private static HookReplaySession? _activeHookReplaySession;
    private static IOverlayScreen? _activeReplayOverlay;
    private static bool _isReplayingBeforeTargetChoice;
    private static int _replayVisualGeneration;
    private static int _timelineGeneration;

    public static bool HasActiveChoice => _activeReplay != null || _activeOriginalAction != null;
    public static bool IsActiveHookChoice =>
        _activeReplay?.ReplayPoint.Sequence.IsHookReplay == true ||
        (_activeHookChoiceRecording != null &&
         _activeOriginalAction is GenericHookGameAction);
    public static bool IsReplayingBeforeTargetChoice => _isReplayingBeforeTargetChoice;

    public static void Reset()
    {
        _selectorScope?.Dispose();
        _selectorScope = null;
        _activeReplay = null;
        _activeOriginalAction = null;
        _replayAction = null;
        _activeHookReplaySession = null;
        _activeReplayOverlay = null;
        _preparedRequest = null;
        _activeOriginalChoice = null;
        _activeHookChoiceRecording = null;
        _isReplayingBeforeTargetChoice = false;
        _replayVisualGeneration++;
        _timelineGeneration++;
        OriginalSequences.Clear();
    }

    public static void InvalidateTimelineAsyncWork()
    {
        _timelineGeneration++;
        _activeReplay?.RequestInterruption();
        _activeHookReplaySession?.RequestInterruption();
        _preparedRequest = null;
        _activeOriginalChoice = null;
        _activeOriginalAction = null;
        _activeHookChoiceRecording = null;
        _isReplayingBeforeTargetChoice = false;
        _replayVisualGeneration++;
        OriginalSequences.Clear();
    }

    public static int CurrentTimelineGeneration => _timelineGeneration;

    public static bool IsCurrentTimelineGeneration(int generation)
    {
        return generation == _timelineGeneration;
    }

    public static void PrepareChoiceRequest(CardChoiceRequest request)
    {
        if (_activeReplay == null &&
            !UndoAndRestartConfig.IncludeCardChoiceSnapshots)
        {
            _preparedRequest = null;
            return;
        }

        if (_activeReplay == null &&
            _activeHookChoiceRecording == null &&
            RunManager.Instance.ActionExecutor.CurrentlyRunningAction is not PlayCardAction &&
            RunManager.Instance.ActionExecutor.CurrentlyRunningAction is not UsePotionAction)
        {
            _preparedRequest = null;
            return;
        }

        _preparedRequest = request;
    }

    public static void OnChoiceBegun(
        GameActionPlayerChoiceContext choiceContext)
    {
        GameAction action = choiceContext.Action;
        if (_activeReplay != null ||
            !UndoRedoManager.IsActionFromCurrentTimeline(action) ||
            action is not PlayCardAction &&
            action is not UsePotionAction { WasEnqueuedInCombat: true })
        {
            return;
        }

        CardChoiceRequest? request = ConsumePreparedRequest();
        if (request == null)
        {
            MainFile.Logger.Debug("Skipped card choice checkpoint: choice request metadata was unavailable.");
            return;
        }

        if (!request.RequiresPlayerChoice)
        {
            MainFile.Logger.Debug(
                "Skipped card choice checkpoint: the game resolves this choice automatically.");
            return;
        }

        if (!OriginalSequences.TryGetValue(action, out CardChoiceReplaySequence? sequence))
        {
            sequence = CardChoiceReplaySequence.FromAction(action);
            OriginalSequences.Add(action, sequence);
        }

        CardChoiceRecord record = sequence.AddChoice(
            request,
            choiceContext.LastInvolvedModel);
        UndoCheckpoint? checkpoint = UndoRedoManager.RegisterCardChoiceCheckpoint(record.ReplayPoint);
        if (checkpoint == null)
        {
            sequence.RemoveLastChoice(record);
            return;
        }

        record.Checkpoint = checkpoint;
        _activeOriginalChoice = record;
        _activeOriginalAction = action;
    }

    public static HookChoiceRecording? BeginTurnStartChoiceRecording(
        CombatManager combatManager,
        Player player)
    {
        if (!UndoAndRestartConfig.IncludeCardChoiceSnapshots ||
            _activeReplay != null ||
            _activeHookChoiceRecording != null ||
            !ReferenceEquals(combatManager, CombatManager.Instance) ||
            combatManager.DebugOnlyGetState() is not { } state)
        {
            return null;
        }

        CombatSnapshot? replayBaseSnapshot =
            UndoRedoManager.CaptureCardChoiceReplayBaseSnapshot(
                "SetupPlayerTurn:choice-replay-base");
        if (replayBaseSnapshot == null)
        {
            return null;
        }

        HookChoiceRecording recording = new(
            CardChoiceReplaySequence.FromTurnStart(state, player),
            replayBaseSnapshot,
            _timelineGeneration);
        _activeHookChoiceRecording = recording;
        return recording;
    }

    public static HookChoiceRecording? BeginTurnEndChoiceRecording(
        CombatManager combatManager)
    {
        if (!UndoAndRestartConfig.IncludeCardChoiceSnapshots ||
            _activeReplay != null ||
            _activeHookChoiceRecording != null ||
            !ReferenceEquals(combatManager, CombatManager.Instance) ||
            CombatManager.Instance.DebugOnlyGetState() is not { } state ||
            LocalContext.GetMe(state) is not { } localPlayer)
        {
            return null;
        }

        CombatSnapshot? replayBaseSnapshot =
            UndoRedoManager.CaptureCardChoiceReplayBaseSnapshot(
                "EndPlayerTurnPhaseOne:choice-replay-base");
        if (replayBaseSnapshot == null)
        {
            return null;
        }

        HookChoiceRecording recording = new(
            CardChoiceReplaySequence.FromTurnEnd(state, localPlayer),
            replayBaseSnapshot,
            _timelineGeneration);
        _activeHookChoiceRecording = recording;
        return recording;
    }

    public static async Task TrackHookChoiceRecordingAsync(
        Task original,
        HookChoiceRecording recording)
    {
        int generation = recording.TimelineGeneration;
        try
        {
            await original;
        }
        finally
        {
            if (generation == _timelineGeneration &&
                ReferenceEquals(_activeHookChoiceRecording, recording))
            {
                _activeHookChoiceRecording = null;
                _preparedRequest = null;
                if (recording.Sequence.HasChoices)
                {
                    UndoRedoManager.RequestForcedPlayerControlReadyCapture(
                        "turn hook card choice completed");
                }
            }
        }
    }

    public static void OnHookChoiceBegun(HookPlayerChoiceContext choiceContext)
    {
        GenericHookGameAction? hookAction = choiceContext.GameAction;
        if (hookAction == null)
        {
            return;
        }

        if (_activeReplay != null)
        {
            AttachReplayAction(hookAction);
            return;
        }

        HookChoiceRecording? recording = _activeHookChoiceRecording;
        CardChoiceRequest? request = ConsumePreparedRequest();
        if (recording == null ||
            recording.TimelineGeneration != _timelineGeneration ||
            request == null)
        {
            return;
        }

        if (!request.RequiresPlayerChoice)
        {
            MainFile.Logger.Debug(
                "Skipped turn-hook choice checkpoint: the game resolves this choice automatically.");
            return;
        }

        CardChoiceRecord record = recording.Sequence.AddChoice(
            request,
            choiceContext.LastInvolvedModel ?? choiceContext.Source);
        UndoCheckpoint? checkpoint = UndoRedoManager.RegisterCardChoiceCheckpoint(
            record.ReplayPoint,
            recording.ReplayBaseSnapshot);
        if (checkpoint == null)
        {
            recording.Sequence.RemoveLastChoice(record);
            return;
        }

        record.Checkpoint = checkpoint;
        _activeOriginalChoice = record;
        _activeOriginalAction = hookAction;
    }

    public static void OnCardsChosen(IEnumerable<CardModel?> cards)
    {
        IReadOnlyList<CardModel> selectedCards = cards.OfType<CardModel>().ToList();
        if (_activeReplay != null)
        {
            _preparedRequest = null;
            return;
        }

        CardChoiceRecord? record = _activeOriginalChoice;
        _activeOriginalChoice = null;
        _activeOriginalAction = null;
        _preparedRequest = null;
        if (record?.Checkpoint == null)
        {
            return;
        }

        record.SetSelectedCards(selectedCards);
        UndoRedoManager.RecordCardChoice(
            record.Checkpoint,
            selectedCards,
            record.SourceModel);
    }

    public static void OnActionFinished(GameAction action)
    {
        OriginalSequences.Remove(action);

        if (ReferenceEquals(_activeOriginalAction, action))
        {
            _activeOriginalAction = null;
            _activeOriginalChoice = null;
        }
    }

    public static void StartReplay(CardChoiceReplayPoint replayPoint)
    {
        if (_activeReplay != null)
        {
            throw new InvalidOperationException("A card choice replay is already active.");
        }

        if (CardSelectCmd.Selector != null || CardSelectCmd.LocalSelector != null)
        {
            throw new InvalidOperationException("Another card selector is active.");
        }

        // RestoreExecutionScope deliberately disables the hand while the snapshot is
        // being installed. Card-choice replay starts before that scope is disposed,
        // so its Y=100 disable tween would otherwise be classified as an old tween
        // and survive replay fast-forward. The later enable tween reaches Y=0 first,
        // then the stale disable tween wins 0.2 seconds later and shifts the entire
        // hand selector (cards, prompt, and backstop) down. Canonicalize the hand root
        // before taking the replay tween baseline so no restore-owned tween can race it.
        StabilizeHandRootTransitionForReplay();

        ReplayCardSelector selector = new(replayPoint, _timelineGeneration);
        IDisposable selectorScope = CardSelectCmd.UseSelector(
            selector,
            localOnly: replayPoint.Sequence.IsHookReplay);

        _activeReplay = selector;
        _selectorScope = selectorScope;
        StartReplayVisualFastForward();
        if (replayPoint.Sequence.IsHookReplay)
        {
            HookReplaySession session = new(selector);
            _activeHookReplaySession = session;
            session.ExecutionTask = RunHookReplayAsync(replayPoint, session);
            TaskHelper.RunSafely(session.ExecutionTask);
            MainFile.Logger.Info(
                $"Started turn hook choice replay for choice {replayPoint.ChoiceOrdinal + 1}.");
            return;
        }

        GameAction replayAction = replayPoint.Sequence.CreateReplayAction();
        _replayAction = replayAction;
        replayAction.AfterFinished += OnReplayActionFinished;
        replayAction.BeforeCancelled += FinishReplay;

        try
        {
            RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(replayAction);
            MainFile.Logger.Info(
                $"Started card choice replay for choice {replayPoint.ChoiceOrdinal + 1}.");
        }
        catch
        {
            FinishReplay(replayAction);
            throw;
        }
    }

    private static void OnReplayActionFinished(GameAction action)
    {
        CardChoiceReplayPoint? replayPoint = _activeReplay?.ReplayPoint;
        bool wasInterrupted = _activeReplay?.IsInterruptionRequested == true;
        Exception? replayException = action.Exception;
        FinishReplay(action);

        if (!wasInterrupted && replayPoint != null && replayException != null)
        {
            UndoRedoManager.ScheduleFailedCardChoiceReplayRecovery(
                replayPoint,
                replayException);
        }
    }

    private static async Task RunHookReplayAsync(
        CardChoiceReplayPoint replayPoint,
        HookReplaySession session)
    {
        try
        {
            await replayPoint.Sequence.ReplayHookAsync(session.Selector);
            if (!session.Selector.IsInterruptionRequested &&
                ReferenceEquals(_activeHookReplaySession, session))
            {
                if (replayPoint.Sequence.IsTurnStartHookReplay)
                {
                    UndoRedoManager.QueueCurrentTurnStartEntry();
                }

                UndoRedoManager.RequestForcedPlayerControlReadyCapture(
                    "turn hook card choice replay completed");
            }
        }
        catch (Exception ex) when (
            ex is not OperationCanceledException &&
            !session.Selector.IsInterruptionRequested)
        {
            MainFile.Logger.Error($"Turn hook choice replay failed: {ex}");
            UndoRedoManager.ScheduleFailedCardChoiceReplayRecovery(
                replayPoint,
                ex);
        }
        finally
        {
            if (ReferenceEquals(_activeHookReplaySession, session))
            {
                _activeHookReplaySession = null;
                FinishReplay(_replayAction);
            }
        }
    }

    private static void AttachReplayAction(GameAction action)
    {
        if (_replayAction != null)
        {
            if (!ReferenceEquals(_replayAction, action))
            {
                throw new InvalidOperationException(
                    "Turn-start choice replay created more than one hook action.");
            }

            return;
        }

        _replayAction = action;
        action.AfterFinished += ReleaseHookReplayAction;
        action.BeforeCancelled += OnHookReplayActionCancelled;
    }

    private static void ReleaseHookReplayAction(GameAction action)
    {
        action.AfterFinished -= ReleaseHookReplayAction;
        action.BeforeCancelled -= OnHookReplayActionCancelled;
        if (ReferenceEquals(_replayAction, action))
        {
            _replayAction = null;
        }
    }

    private static void OnHookReplayActionCancelled(GameAction action)
    {
        if (ReferenceEquals(_replayAction, action))
        {
            _activeHookReplaySession?.RequestInterruption();
        }
    }

    public static CardChoiceRequest? ConsumePreparedRequest()
    {
        CardChoiceRequest? request = _preparedRequest;
        _preparedRequest = null;
        return request;
    }

    public static void SetActiveReplayOverlay(IOverlayScreen overlay)
    {
        _activeReplayOverlay = overlay;
    }

    public static void ClearActiveReplayOverlay(IOverlayScreen overlay)
    {
        if (ReferenceEquals(_activeReplayOverlay, overlay))
        {
            _activeReplayOverlay = null;
        }
    }

    public static void CompleteReplayCardQueueAnimation(
        NCardPlayQueue playQueue,
        PlayCardAction action)
    {
        if (!ReferenceEquals(_replayAction, action))
        {
            return;
        }

        IList queueItems = ReflectionUtil.GetRequiredField<IList>(playQueue, "_playQueue");
        for (int index = 0; index < queueItems.Count; index++)
        {
            object queueItem = queueItems[index]!;
            if (!ReferenceEquals(
                    ReflectionUtil.GetRequiredField<GameAction>(queueItem, "action"),
                    action))
            {
                continue;
            }

            ReflectionUtil.GetField<Tween>(queueItem, "currentTween")?.Kill();
            NCard card = ReflectionUtil.GetRequiredField<NCard>(queueItem, "card");
            card.Position = (Vector2)(ReflectionUtil.Method(
                    typeof(NCardPlayQueue),
                    "GetPositionForQueueIndex",
                    typeof(NCard),
                    typeof(int))
                ?.Invoke(playQueue, new object[] { card, index }) ??
                throw new MissingMethodException(
                    typeof(NCardPlayQueue).FullName,
                    "GetPositionForQueueIndex"));
            card.Scale = (Vector2)(ReflectionUtil.Method(
                    typeof(NCardPlayQueue),
                    "GetScaleForQueueIndex",
                    typeof(int))
                ?.Invoke(playQueue, new object[] { index }) ??
                throw new MissingMethodException(
                    typeof(NCardPlayQueue).FullName,
                    "GetScaleForQueueIndex"));
            Color modulate = card.Modulate;
            modulate.A = 1f;
            card.Modulate = modulate;
            MainFile.Logger.Debug("Completed replay card queue animation immediately.");
            return;
        }
    }

    public static bool IsReplayingCard(CardModel card)
    {
        return _replayAction is PlayCardAction replayAction &&
               ReferenceEquals(
                   replayAction.NetCombatCard.ToCardModelOrNull(),
                   card);
    }

    public static async Task CompleteReplayCardPlayPileAnimationAsync(
        Task original,
        CardModel card)
    {
        await original;
        if (!IsReplayingCard(card))
        {
            return;
        }

        NCard? cardNode = NCard.FindOnTable(card);
        cardNode?.PlayPileTween?.FastForwardToCompletion();
        MainFile.Logger.Debug("Completed replay card play-pile animation immediately.");
    }

    public static async Task InterruptActiveChoiceAsync()
    {
        HookReplaySession? interruptedHookReplaySession = _activeHookReplaySession;
        _activeReplay?.RequestInterruption();
        GameAction activeAction = await WaitForActiveChoiceActionAsync();
        Task? actionExecutionTask = ReflectionUtil.GetField<Task>(activeAction, "_executionTask");
        bool hookActionWasWaitingToStart =
            interruptedHookReplaySession != null &&
            activeAction is GenericHookGameAction &&
            activeAction.State == GameActionState.WaitingForExecution;

        interruptedHookReplaySession?.RequestInterruption();
        CancelActiveSelectionUi();
        await WaitForChoiceTaskToObserveInterruption(actionExecutionTask);
        if (activeAction.State is not GameActionState.Finished and
            not GameActionState.Canceled)
        {
            activeAction.Cancel();
        }

        if (hookActionWasWaitingToStart &&
            activeAction is GenericHookGameAction pendingHookAction)
        {
            CancelPendingHookActionStart(pendingHookAction);
        }

        RemoveChoiceActionFromQueue(activeAction);
        if (interruptedHookReplaySession == null && HasActiveChoice)
        {
            FinishReplay(activeAction);
        }

        await WaitForInterruptedChoiceToSettle(activeAction, actionExecutionTask);
        await WaitForHookReplayToSettle(interruptedHookReplaySession);
        MainFile.Logger.Info("Interrupted active card choice for timeline navigation.");
    }

    private static void CancelPendingHookActionStart(
        GenericHookGameAction action)
    {
        // A rapid navigation input can acquire the restore lock after the hook
        // action is enqueued but before ActionExecutor starts it. The replay task
        // is then waiting on ExecutionStartedTask while the restore lock keeps the
        // queue paused, so merely canceling/removing the action cannot wake it.
        // Cancel that pre-execution signal as part of abandoning this timeline.
        TaskCompletionSource executionStartedSource =
            ReflectionUtil.GetRequiredField<TaskCompletionSource>(
                action,
                "_executionStartedSource");
        if (executionStartedSource.TrySetCanceled())
        {
            MainFile.Logger.Info(
                "Canceled a turn-hook action before execution so its replay could settle.");
        }
    }

    private static async Task WaitForChoiceTaskToObserveInterruption(Task? actionExecutionTask)
    {
        if (actionExecutionTask == null)
        {
            return;
        }

        for (int frame = 0; frame < 60; frame++)
        {
            if (actionExecutionTask.IsCompleted)
            {
                return;
            }

            await AwaitProcessFrame();
        }

        MainFile.Logger.Warn(
            "The interrupted card choice task did not stop before its action was canceled.");
    }

    private static async Task WaitForHookReplayToSettle(
        HookReplaySession? session)
    {
        Task? executionTask = session?.ExecutionTask;
        if (executionTask == null)
        {
            return;
        }

        for (int frame = 0; frame < 60; frame++)
        {
            if (executionTask.IsCompleted)
            {
                return;
            }

            await AwaitProcessFrame();
        }

        throw new TimeoutException(
            "Timed out waiting for the interrupted turn hook replay to settle.");
    }

    private static async Task<GameAction> WaitForActiveChoiceActionAsync()
    {
        for (int frame = 0; frame < 60; frame++)
        {
            GameAction? activeAction = _replayAction ?? _activeOriginalAction;
            if (activeAction != null)
            {
                return activeAction;
            }

            if (_activeReplay == null)
            {
                throw new InvalidOperationException("The card choice ended before it could be interrupted.");
            }

            NGame game = NGame.Instance ??
                         throw new InvalidOperationException(
                             "The game scene ended while waiting for the card choice action.");
            await game.ToSignal(
                game.GetTree(),
                SceneTree.SignalName.ProcessFrame);
        }

        throw new TimeoutException("Timed out waiting for the active card choice action.");
    }

    public static HashSet<Tween> CaptureProcessedTweens()
    {
        SceneTree? tree = NGame.Instance?.GetTree();
        return tree == null
            ? new HashSet<Tween>()
            : tree.GetProcessedTweens().ToHashSet();
    }

    public static async Task CompleteNewReplayUiAnimationsAsync(
        HashSet<Tween> existingTweens)
    {
        CompleteNewReplayUiAnimations(existingTweens);
        if (NGame.Instance?.GetTree() is not { } tree)
        {
            return;
        }

        await NGame.Instance.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        CompleteNewReplayUiAnimations(existingTweens);
    }

    public static void StabilizeHandRootTransitionForReplay()
    {
        NPlayerHand? hand = NPlayerHand.Instance;
        if (hand == null || !GodotObject.IsInstanceValid(hand))
        {
            return;
        }

        foreach (string fieldName in new[]
                 {
                     "_animEnableTween",
                     "_animInTween",
                     "_animOutTween",
                 })
        {
            ReflectionUtil.GetField<Tween>(hand, fieldName)?.Kill();
            ReflectionUtil.SetField(hand, fieldName, null);
        }

        bool isDisabled = ReflectionUtil.GetRequiredField<bool>(hand, "_isDisabled");
        if (isDisabled)
        {
            hand.Position = ReflectionUtil.GetStaticField<Vector2>(
                                typeof(NPlayerHand),
                                "_disablePosition");
            hand.Modulate = ReflectionUtil.GetStaticField<Color>(
                                typeof(NPlayerHand),
                                "_disableModulate");
        }
        else
        {
            hand.Position = Vector2.Zero;
            hand.Modulate = Colors.White;
        }
    }

    public static void OnTargetChoiceReached()
    {
        if (!_isReplayingBeforeTargetChoice)
        {
            return;
        }

        RemoveReplayItemThrowVfx();
        _isReplayingBeforeTargetChoice = false;
    }

    private static void StartReplayVisualFastForward()
    {
        _isReplayingBeforeTargetChoice = true;
        int generation = ++_replayVisualGeneration;
        HashSet<Tween> existingTweens = CaptureProcessedTweens();
        TaskHelper.RunSafely(
            FastForwardReplayVisualsUntilTargetChoiceAsync(
                generation,
                existingTweens));
    }

    private static async Task FastForwardReplayVisualsUntilTargetChoiceAsync(
        int generation,
        HashSet<Tween> existingTweens)
    {
        while (_isReplayingBeforeTargetChoice &&
               generation == _replayVisualGeneration &&
               _activeReplay != null)
        {
            CompleteNewReplayUiAnimations(existingTweens);
            RemoveReplayItemThrowVfx();
            await AwaitProcessFrame();
        }
    }

    private static void RemoveReplayItemThrowVfx()
    {
        Node? vfxContainer = NCombatRoom.Instance?.CombatVfxContainer;
        if (vfxContainer == null)
        {
            return;
        }

        foreach (NItemThrowVfx itemThrowVfx in
                 vfxContainer.GetChildren().OfType<NItemThrowVfx>())
        {
            itemThrowVfx.Visible = false;
            itemThrowVfx.QueueFreeSafely();
        }
    }

    private static void CompleteNewReplayUiAnimations(HashSet<Tween> existingTweens)
    {
        SceneTree? tree = NGame.Instance?.GetTree();
        if (tree == null)
        {
            return;
        }

        foreach (Tween tween in tree.GetProcessedTweens())
        {
            if (!existingTweens.Contains(tween) && tween.IsValid())
            {
                tween.CustomStep(60.0);
            }
        }
    }

    private static async Task WaitForInterruptedChoiceToSettle(
        GameAction activeAction,
        Task? actionExecutionTask)
    {
        ActionExecutor actionExecutor = RunManager.Instance.ActionExecutor;
        for (int frame = 0; frame < 60; frame++)
        {
            bool actionExecutionFinished = actionExecutionTask?.IsCompleted != false;
            if (actionExecutionFinished && !actionExecutor.IsRunning)
            {
                await AwaitProcessFrame();
                FinalizeInterruptedChoiceCleanup(actionExecutor, activeAction);
                return;
            }

            await AwaitProcessFrame();
        }

        actionExecutor.Cancel();
        if (Engine.GetMainLoop() is SceneTree fallbackSceneTree)
        {
            await fallbackSceneTree.ToSignal(
                fallbackSceneTree,
                SceneTree.SignalName.ProcessFrame);
        }

        FinalizeInterruptedChoiceCleanup(actionExecutor, activeAction);
        MainFile.Logger.Warn(
            "Forced cleanup after an interrupted card choice did not settle within 60 frames.");
    }

    private static void FinalizeInterruptedChoiceCleanup(
        ActionExecutor actionExecutor,
        GameAction activeAction)
    {
        ClearInterruptedActionFromExecutor(actionExecutor, activeAction);

        // 0.110의 GetReadyAction은 취소된 액션을 제거해도 빈 큐 완료 신호를 갱신하지 않음.
        // 실행기 참조를 먼저 정리한 뒤 다시 검사해 실제 큐와 IsEmpty 상태를 일치시킴.
        RemoveChoiceActionFromQueue(activeAction);
        if (!RunManager.Instance.ActionQueueSet.IsEmpty)
        {
            throw new InvalidOperationException(
                "The action queue did not become empty after interrupting the card choice.");
        }
    }

    private static async Task AwaitProcessFrame()
    {
        if (Engine.GetMainLoop() is SceneTree sceneTree)
        {
            await sceneTree.ToSignal(sceneTree, SceneTree.SignalName.ProcessFrame);
        }
        else
        {
            await Task.Delay(16);
        }
    }

    private static void ClearInterruptedActionFromExecutor(
        ActionExecutor actionExecutor,
        GameAction activeAction)
    {
        if (!ReferenceEquals(actionExecutor.CurrentlyRunningAction, activeAction))
        {
            return;
        }

        ReflectionUtil.SetRequiredField(
            actionExecutor,
            "<CurrentlyRunningAction>k__BackingField",
            null);
        MainFile.Logger.Info("Cleared the interrupted card choice from the action executor.");
    }

    private static void CancelActiveSelectionUi()
    {
        HashSet<Tween> existingTweens = CaptureProcessedTweens();
        NPlayerHand? hand = NPlayerHand.Instance;
        if (hand?.IsInCardSelection == true)
        {
            ReflectionUtil.Method(typeof(NPlayerHand), "CancelHandSelectionIfNecessary")
                ?.Invoke(hand, null);
        }

        IOverlayScreen? overlay = _activeReplayOverlay;
        if (overlay == null)
        {
            IOverlayScreen? topOverlay = NOverlayStack.Instance?.Peek();
            if (topOverlay is NCombatPileCardSelectScreen or
                NSimpleCardSelectScreen or
                NChooseACardSelectionScreen)
            {
                overlay = topOverlay;
            }
        }

        if (overlay != null && NOverlayStack.Instance != null)
        {
            NOverlayStack.Instance.Remove(overlay);
        }

        _activeReplayOverlay = null;
        CompleteNewReplayUiAnimations(existingTweens);
    }

    private static void RemoveChoiceActionFromQueue(GameAction activeAction)
    {
        object actionQueueSet = RunManager.Instance.ActionQueueSet;
        IList queues = ReflectionUtil.GetRequiredField<IList>(actionQueueSet, "_actionQueues");
        bool actionExecutorOwnsAction = ReferenceEquals(
            RunManager.Instance.ActionExecutor.CurrentlyRunningAction,
            activeAction);
        foreach (object queue in queues)
        {
            IList actions = ReflectionUtil.GetRequiredField<IList>(queue, "actions");
            if (!actionExecutorOwnsAction && actions.Contains(activeAction))
            {
                actions.Remove(activeAction);
            }

            ClearChoiceCancellationMarker(queue);
        }

        IList waitingResumptions =
            ReflectionUtil.GetRequiredField<IList>(actionQueueSet, "_actionsWaitingForResumption");
        for (int index = waitingResumptions.Count - 1; index >= 0; index--)
        {
            object waiting = waitingResumptions[index]!;
            uint oldId = ReflectionUtil.GetRequiredField<uint>(waiting, "oldId");
            if (oldId == activeAction.Id)
            {
                waitingResumptions.RemoveAt(index);
            }
        }

        if (UndoRedoManager.IsNavigationInProgress)
        {
            RunManager.Instance.ActionQueueSet.PauseAllPlayerQueues();
        }
        else
        {
            RunManager.Instance.ActionQueueSet.UnpauseAllPlayerQueues();
        }
        ReflectionUtil.Method(actionQueueSet.GetType(), "CheckIfQueuesEmpty")?.Invoke(actionQueueSet, null);
    }

    private static void ClearChoiceCancellationMarker(object queue)
    {
        // 0.110 stored this state as a bool. 0.111 keeps the action which started
        // cancellation instead, so timeline interruption must support both layouts.
        bool cleared = false;
        if (ReflectionUtil.Field(queue.GetType(), "actionCancellingPlayCardActions") != null)
        {
            ReflectionUtil.SetField(queue, "actionCancellingPlayCardActions", null);
            cleared = true;
        }

        if (ReflectionUtil.Field(queue.GetType(), "isCancellingPlayCardActions") != null)
        {
            ReflectionUtil.SetField(queue, "isCancellingPlayCardActions", false);
            cleared = true;
        }

        if (!cleared)
        {
            MainFile.Logger.Warn(
                "Could not find the action-queue card-choice cancellation marker.");
        }
    }

    private static void FinishReplay(GameAction? action)
    {
        if (_replayAction != null &&
            action != null &&
            !ReferenceEquals(_replayAction, action))
        {
            return;
        }

        if (_activeReplay == null &&
            _selectorScope == null &&
            _activeOriginalAction == null)
        {
            return;
        }

        _selectorScope?.Dispose();
        _selectorScope = null;
        _activeReplay = null;
        _replayAction = null;
        _activeHookReplaySession = null;
        _activeOriginalAction = null;
        _activeReplayOverlay = null;
        _preparedRequest = null;
        _activeOriginalChoice = null;
        _isReplayingBeforeTargetChoice = false;
        _replayVisualGeneration++;
        MainFile.Logger.Info("Finished card choice replay.");
    }
}

internal sealed class ReplayCardSelector : MegaCrit.Sts2.Core.TestSupport.ICardSelector
{
    private readonly CardChoiceReplayPoint _target;
    private readonly int _timelineGeneration;
    private readonly TaskCompletionSource _interruptionCompletionSource =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _choiceOrdinal;
    private bool _branched;

    public ReplayCardSelector(CardChoiceReplayPoint target, int timelineGeneration)
    {
        _target = target;
        _timelineGeneration = timelineGeneration;
    }

    public bool IsInterruptionRequested => _interruptionCompletionSource.Task.IsCompleted;
    public CardChoiceReplayPoint ReplayPoint => _target;

    public void RequestInterruption()
    {
        _interruptionCompletionSource.TrySetResult();
    }

    public async Task<IEnumerable<CardModel>> GetSelectedCards(
        IEnumerable<CardModel> options,
        int minSelect,
        int maxSelect)
    {
        int currentOrdinal = _choiceOrdinal++;
        CardChoiceRequest request = CardChoiceCheckpointService.ConsumePreparedRequest() ??
                                    CardChoiceRequest.ForGenericGrid(minSelect, maxSelect);
        IReadOnlyList<CardModel> availableCards = options.ToList();
        ThrowIfInterruptionRequested();

        if (currentOrdinal < _target.ChoiceOrdinal)
        {
            CardChoiceRecord priorChoice = _target.Sequence.GetChoice(currentOrdinal);
            IReadOnlyList<CardModel> replayedCards = priorChoice.ResolveSelectedCards(availableCards);
            MainFile.Logger.Info(
                $"Replayed prior card choice {currentOrdinal + 1}/{_target.ChoiceOrdinal + 1}.");
            return replayedCards;
        }

        if (currentOrdinal > _target.ChoiceOrdinal && !_branched)
        {
            throw new InvalidOperationException("Card choice replay passed the target before branching.");
        }

        CardChoiceRecord record;
        if (currentOrdinal == _target.ChoiceOrdinal)
        {
            record = _target.Sequence.GetChoice(currentOrdinal);
        }
        else
        {
            record = _target.Sequence.AddChoice(request);
            UndoCheckpoint? checkpoint = UndoRedoManager.RegisterCardChoiceCheckpoint(record.ReplayPoint);
            if (checkpoint == null)
            {
                throw new InvalidOperationException("Failed to register a replayed card choice checkpoint.");
            }

            record.Checkpoint = checkpoint;
        }

        CardChoiceCheckpointService.OnTargetChoiceReached();
        Task<IReadOnlyList<CardModel>> selectionTask =
            request.ShowSelectionAsync(availableCards, minSelect, maxSelect);
        Task completedTask = await Task.WhenAny(
            selectionTask,
            _interruptionCompletionSource.Task);
        ThrowIfInterruptionRequested();
        if (!ReferenceEquals(completedTask, selectionTask))
        {
            ThrowIfInterruptionRequested();
        }

        IReadOnlyList<CardModel> selectedCards = await selectionTask;
        ThrowIfInterruptionRequested();
        if (!_branched)
        {
            UndoRedoManager.BeginBranchFromCurrentCheckpoint();
            _target.Sequence.TruncateAfter(currentOrdinal);
            _branched = true;
        }

        record.SetSelectedCards(selectedCards, availableCards);
        if (record.Checkpoint != null)
        {
            UndoRedoManager.RecordCardChoice(
                record.Checkpoint,
                selectedCards,
                record.SourceModel);
        }

        return selectedCards;
    }

    private void ThrowIfInterruptionRequested()
    {
        if (IsInterruptionRequested ||
            !CardChoiceCheckpointService.IsCurrentTimelineGeneration(
                _timelineGeneration))
        {
            throw new OperationCanceledException(
                "Card choice replay was interrupted for timeline navigation.");
        }
    }

    public CardRewardSelection GetSelectedCardReward(
        IReadOnlyList<CardCreationResult> options,
        IReadOnlyList<CardRewardAlternative> alternatives)
    {
        return new CardRewardSelection
        {
            card = options.FirstOrDefault()?.Card,
        };
    }
}

internal sealed class CardChoiceReplaySequence
{
    private readonly List<CardChoiceRecord> _choices = new();
    private readonly Player _player;
    private readonly ReplayableChoiceActionKind _actionKind;
    private readonly NetCombatCard _card;
    private readonly ModelId? _cardModelId;
    private readonly uint? _targetId;
    private readonly uint _potionIndex;
    private readonly CombatState? _hookReplayState;
    private readonly HookChoiceReplayKind _hookReplayKind;
    private readonly AbstractModel? _fallbackSourceModel;

    private CardChoiceReplaySequence(PlayCardAction action)
    {
        _actionKind = ReplayableChoiceActionKind.PlayCard;
        _player = action.Player;
        _card = action.NetCombatCard;
        _cardModelId = action.CardModelId;
        _targetId = action.TargetId;
        _fallbackSourceModel = action.NetCombatCard.ToCardModelOrNull();
    }

    private CardChoiceReplaySequence(UsePotionAction action)
    {
        _actionKind = ReplayableChoiceActionKind.UsePotion;
        _player = action.Player;
        _potionIndex = action.PotionIndex;
        _targetId = action.TargetId;
        _fallbackSourceModel =
            action.Player.GetPotionAtSlotIndex((int)action.PotionIndex);
    }

    private CardChoiceReplaySequence(
        CombatState hookReplayState,
        Player player,
        HookChoiceReplayKind hookReplayKind)
    {
        _hookReplayState = hookReplayState;
        _player = player;
        _hookReplayKind = hookReplayKind;
    }

    public bool IsHookReplay => _hookReplayKind != HookChoiceReplayKind.None;
    public bool IsTurnStartHookReplay => _hookReplayKind == HookChoiceReplayKind.TurnStart;
    public bool HasChoices => _choices.Count > 0;
    public AbstractModel? FallbackSourceModel => _fallbackSourceModel;

    public static CardChoiceReplaySequence FromAction(GameAction action)
    {
        return action switch
        {
            PlayCardAction playCardAction =>
                new CardChoiceReplaySequence(playCardAction),
            UsePotionAction { WasEnqueuedInCombat: true } usePotionAction =>
                new CardChoiceReplaySequence(usePotionAction),
            _ => throw new InvalidOperationException(
                $"Unsupported card choice action: {action.GetType().FullName}"),
        };
    }

    public static CardChoiceReplaySequence FromTurnStart(
        CombatState state,
        Player player)
    {
        return new CardChoiceReplaySequence(
            state,
            player,
            HookChoiceReplayKind.TurnStart);
    }

    public static CardChoiceReplaySequence FromTurnEnd(
        CombatState state,
        Player player)
    {
        return new CardChoiceReplaySequence(
            state,
            player,
            HookChoiceReplayKind.TurnEnd);
    }

    public CardChoiceRecord AddChoice(
        CardChoiceRequest request,
        AbstractModel? contextSource = null)
    {
        CardChoiceRecord record = new(
            this,
            _choices.Count,
            request,
            contextSource);
        _choices.Add(record);
        return record;
    }

    public CardChoiceRecord GetChoice(int ordinal)
    {
        if (ordinal < 0 || ordinal >= _choices.Count)
        {
            throw new InvalidOperationException(
                $"Card choice {ordinal + 1} is missing from the recorded replay sequence.");
        }

        return _choices[ordinal];
    }

    public void RemoveLastChoice(CardChoiceRecord record)
    {
        if (_choices.Count > 0 && ReferenceEquals(_choices[^1], record))
        {
            _choices.RemoveAt(_choices.Count - 1);
        }
    }

    public void TruncateAfter(int ordinal)
    {
        int firstRemovedOrdinal = ordinal + 1;
        if (firstRemovedOrdinal < _choices.Count)
        {
            _choices.RemoveRange(firstRemovedOrdinal, _choices.Count - firstRemovedOrdinal);
        }
    }

    public GameAction CreateReplayAction()
    {
        if (IsHookReplay)
        {
            throw new InvalidOperationException(
                "A hook replay sequence cannot create a direct game action.");
        }

        return _actionKind switch
        {
            ReplayableChoiceActionKind.PlayCard =>
                new PlayCardAction(
                    _player,
                    _card,
                    _cardModelId ??
                    throw new InvalidOperationException(
                        "A card replay sequence is missing its card model ID."),
                    _targetId),
            ReplayableChoiceActionKind.UsePotion =>
                new UsePotionAction(
                    _player,
                    _potionIndex,
                    _targetId,
                    targetPlayerId: null,
                    isCombatInProgress: true),
            _ => throw new InvalidOperationException(
                "This choice sequence does not contain a replayable game action."),
        };
    }

    public async Task ReplayHookAsync(ReplayCardSelector selector)
    {
        try
        {
            switch (_hookReplayKind)
            {
                case HookChoiceReplayKind.TurnStart:
                    await ReplayTurnStartAsync();
                    break;
                case HookChoiceReplayKind.TurnEnd:
                    await ReplayTurnEndAsync();
                    break;
                default:
                    throw new InvalidOperationException(
                        "This choice sequence is not a hook replay.");
            }
        }
        catch (OperationCanceledException) when (selector.IsInterruptionRequested)
        {
            MainFile.Logger.Debug(
                "Stopped turn hook replay after timeline navigation interrupted it.");
        }
    }

    private async Task ReplayTurnStartAsync()
    {
        CombatState state = _hookReplayState ??
                            throw new InvalidOperationException(
                                "This choice sequence is not a turn-start replay.");
        ulong localPlayerId = LocalContext.NetId ??
                              throw new InvalidOperationException(
                                  "A local player is required for turn-start choice replay.");
        HookPlayerChoiceContext choiceContext = new(
            _player,
            localPlayerId,
            GameActionType.CombatPlayPhaseOnly);
        Task replayTask = ReplayTurnStartToPlayerControlAsync(
            state,
            choiceContext);
        await choiceContext.AssignTaskAndWaitForPauseOrCompletion(replayTask);
        await choiceContext.WaitForCompletion();
        RunManager.Instance.ActionExecutor.Unpause();
        RunManager.Instance.ActionQueueSynchronizer.SetCombatState(
            ActionSynchronizerCombatState.PlayPhase);
    }

    private async Task ReplayTurnEndAsync()
    {
        CombatState state = _hookReplayState ??
                            throw new InvalidOperationException(
                                "This choice sequence is not a turn-end replay.");
        CombatRuntimeStateCleanup.SetEndingPlayerTurnPhaseOne(true);
        try
        {
            RunManager.Instance.ActionQueueSynchronizer.SetCombatState(
                ActionSynchronizerCombatState.EndTurnPhaseOne);
            Task endTurnTask = (Task)(ReflectionUtil.Method(
                    typeof(CombatManager),
                    "EndPlayerTurnPhaseOneInternal",
                    Type.EmptyTypes)
                ?.Invoke(CombatManager.Instance, null) ??
                throw new MissingMethodException(
                    typeof(CombatManager).FullName,
                    "EndPlayerTurnPhaseOneInternal"));
            await endTurnTask;
            if (!CombatManager.Instance.IsInProgress ||
                !ReferenceEquals(CombatManager.Instance.DebugOnlyGetState(), state))
            {
                return;
            }

            Player localPlayer = LocalContext.GetMe(state) ??
                                 throw new InvalidOperationException(
                                     "A local player is required to finish turn-end choice replay.");
            RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(
                new ReadyToBeginEnemyTurnAction(localPlayer));
        }
        finally
        {
            CombatRuntimeStateCleanup.SetEndingPlayerTurnPhaseOne(false);
        }
    }

    private async Task ReplayTurnStartToPlayerControlAsync(
        CombatState state,
        HookPlayerChoiceContext choiceContext)
    {
        object turnState = CombatRuntimeStateCleanup.GetCurrentTurnState(state) ??
                           throw new InvalidOperationException(
                               "Current combat turn state is unavailable for turn-start replay.");
        Type turnStateType = turnState.GetType();
        Task setupPlayerTurnTask =
            (Task)(ReflectionUtil.Method(
                       typeof(CombatManager),
                       "SetupPlayerTurn",
                       turnStateType,
                       typeof(Player),
                       typeof(HookPlayerChoiceContext))
                   ?.Invoke(
                       CombatManager.Instance,
                       new[] { turnState, _player, choiceContext }) ??
                   throw new MissingMethodException(
                       typeof(CombatManager).FullName,
                       "SetupPlayerTurn"));
        await choiceContext.WaitForPauseOrCompletionWithoutAssigningTask(
            setupPlayerTurnTask);
        if (!CombatManager.Instance.IsInProgress ||
            !ReferenceEquals(CombatManager.Instance.DebugOnlyGetState(), state) ||
            _player.PlayerCombatState == null)
        {
            return;
        }

        await Hook.AfterSideTurnStart(
            state,
            state.CurrentSide,
            GetCreaturesStartingTurnForReplay(state));

        await _player.PlayerCombatState.OrbQueue.AfterTurnStart(choiceContext);

        Task runAutoPrePlayTask =
            (Task)(ReflectionUtil.Method(
                       typeof(CombatManager),
                       "RunAutoPrePlayPhase",
                       turnStateType,
                       typeof(HookPlayerChoiceContext),
                       typeof(Task),
                       typeof(Player))
                   ?.Invoke(
                       CombatManager.Instance,
                       new object[]
                       {
                           turnState,
                           choiceContext,
                           setupPlayerTurnTask,
                           _player,
                       }) ??
                   throw new MissingMethodException(
                       typeof(CombatManager).FullName,
                       "RunAutoPrePlayPhase"));
        await runAutoPrePlayTask;
    }

    private static IReadOnlyList<Creature> GetCreaturesStartingTurnForReplay(
        CombatState state)
    {
        IReadOnlyList<Player> extraTurnPlayers =
            CombatManager.Instance.PlayersTakingExtraTurn;
        if (extraTurnPlayers.Count > 0)
        {
            return extraTurnPlayers
                .Select(player => player.Creature)
                .ToList();
        }

        return state.CreaturesOnCurrentSide.ToList();
    }
}

internal enum ReplayableChoiceActionKind
{
    None,
    PlayCard,
    UsePotion,
}

internal enum HookChoiceReplayKind
{
    None,
    TurnStart,
    TurnEnd,
}

internal sealed record HookChoiceRecording(
    CardChoiceReplaySequence Sequence,
    CombatSnapshot ReplayBaseSnapshot,
    int TimelineGeneration);

internal sealed class CardChoiceRecord
{
    private IReadOnlyList<CardChoiceSelectionIdentity> _selectedCards =
        Array.Empty<CardChoiceSelectionIdentity>();

    public CardChoiceRecord(
        CardChoiceReplaySequence sequence,
        int choiceOrdinal,
        CardChoiceRequest request,
        AbstractModel? contextSource)
    {
        Request = request;
        ContextSource = contextSource;
        ReplayPoint = new CardChoiceReplayPoint(sequence, choiceOrdinal);
    }

    public CardChoiceRequest Request { get; }
    public AbstractModel? ContextSource { get; }
    public CardChoiceReplayPoint ReplayPoint { get; }
    public UndoCheckpoint? Checkpoint { get; set; }
    public AbstractModel? SourceModel =>
        Request.Source ??
        ContextSource ??
        ReplayPoint.Sequence.FallbackSourceModel;

    public void SetSelectedCards(
        IReadOnlyList<CardModel> selectedCards,
        IReadOnlyList<CardModel>? currentAvailableCards = null)
    {
        IReadOnlyList<CardModel> originalAvailableCards =
            currentAvailableCards ??
            Request.AvailableCards ??
            Array.Empty<CardModel>();
        HashSet<int> usedIndices = new();
        _selectedCards = selectedCards
            .Select(card => new CardChoiceSelectionIdentity(
                card,
                FindCardIndexByReference(
                    originalAvailableCards,
                    card,
                    usedIndices),
                card.Id.Entry,
                card.IsUpgraded))
            .ToList();
        Request.ReleaseAvailableCards();
    }

    public IReadOnlyList<CardModel> ResolveSelectedCards(IReadOnlyList<CardModel> availableCards)
    {
        List<CardModel> resolved = new();
        HashSet<int> usedIndices = new();
        foreach (CardChoiceSelectionIdentity selectedCard in _selectedCards)
        {
            int matchIndex = FindCardIndexByReference(
                availableCards,
                selectedCard.OriginalCard,
                usedIndices);
            if (matchIndex < 0 &&
                selectedCard.OriginalOptionIndex >= 0 &&
                selectedCard.OriginalOptionIndex < availableCards.Count &&
                !usedIndices.Contains(selectedCard.OriginalOptionIndex) &&
                selectedCard.Matches(availableCards[selectedCard.OriginalOptionIndex]))
            {
                matchIndex = selectedCard.OriginalOptionIndex;
                usedIndices.Add(matchIndex);
            }

            if (matchIndex < 0)
            {
                matchIndex = FindCardIndexByIdentity(
                    availableCards,
                    selectedCard,
                    usedIndices);
            }

            if (matchIndex < 0)
            {
                string availableCardIds = string.Join(
                    ", ",
                    availableCards.Select(card =>
                        $"{card.Id.Entry}{(card.IsUpgraded ? "+" : string.Empty)}"));
                throw new InvalidOperationException(
                    $"Previously selected card {selectedCard.DisplayName} is unavailable during replay. " +
                    $"Available cards: [{availableCardIds}].");
            }

            resolved.Add(availableCards[matchIndex]);
        }

        return resolved;
    }

    private static int FindCardIndexByReference(
        IReadOnlyList<CardModel> cards,
        CardModel selectedCard,
        ISet<int> usedIndices)
    {
        for (int index = 0; index < cards.Count; index++)
        {
            if (!usedIndices.Contains(index) &&
                ReferenceEquals(cards[index], selectedCard))
            {
                usedIndices.Add(index);
                return index;
            }
        }

        return -1;
    }

    private static int FindCardIndexByIdentity(
        IReadOnlyList<CardModel> cards,
        CardChoiceSelectionIdentity selectedCard,
        ISet<int> usedIndices)
    {
        for (int index = 0; index < cards.Count; index++)
        {
            if (!usedIndices.Contains(index) &&
                selectedCard.Matches(cards[index]))
            {
                usedIndices.Add(index);
                return index;
            }
        }

        return -1;
    }
}

internal sealed record CardChoiceSelectionIdentity(
    CardModel OriginalCard,
    int OriginalOptionIndex,
    string CardId,
    bool IsUpgraded)
{
    public string DisplayName => $"{CardId}{(IsUpgraded ? "+" : string.Empty)}";

    public bool Matches(CardModel card)
    {
        return card.Id.Entry == CardId &&
               card.IsUpgraded == IsUpgraded;
    }
}

internal sealed record CardChoiceReplayPoint(
    CardChoiceReplaySequence Sequence,
    int ChoiceOrdinal);

internal enum CardChoicePresentation
{
    GenericGrid,
    Hand,
    HandUpgrade,
    CombatPile,
    ChooseCard
}

internal sealed class CardChoiceRequest
{
    private CardChoiceRequest(
        CardChoicePresentation presentation,
        CardSelectorPrefs prefs,
        Func<CardModel, bool>? filter = null,
        AbstractModel? source = null,
        CardPile? pile = null,
        bool canSkip = false,
        IReadOnlyList<CardModel>? availableCards = null,
        bool alwaysRequiresPlayerChoice = false)
    {
        Presentation = presentation;
        Prefs = prefs;
        Filter = filter;
        Source = source;
        Pile = pile;
        CanSkip = canSkip;
        AvailableCards = availableCards?.ToList();
        AvailableCardCount = availableCards?.Count;
        AlwaysRequiresPlayerChoice = alwaysRequiresPlayerChoice;
    }

    public CardChoicePresentation Presentation { get; }
    public CardSelectorPrefs Prefs { get; }
    public Func<CardModel, bool>? Filter { get; }
    public AbstractModel? Source { get; }
    public CardPile? Pile { get; }
    public bool CanSkip { get; }
    public IReadOnlyList<CardModel>? AvailableCards { get; private set; }
    public int? AvailableCardCount { get; }
    public bool AlwaysRequiresPlayerChoice { get; }
    public bool RequiresPlayerChoice =>
        AlwaysRequiresPlayerChoice ||
        AvailableCardCount is not int count ||
        count > 0 &&
        (Prefs.RequireManualConfirmation || count > Prefs.MinSelect);

    public void ReleaseAvailableCards()
    {
        AvailableCards = null;
    }

    public static CardChoiceRequest ForGenericGrid(int minSelect, int maxSelect)
    {
        return new CardChoiceRequest(
            CardChoicePresentation.GenericGrid,
            new CardSelectorPrefs(CardSelectorPrefs.TransformSelectionPrompt, minSelect, maxSelect));
    }

    public static CardChoiceRequest ForSimpleGrid(
        CardSelectorPrefs prefs,
        IReadOnlyList<CardModel> availableCards)
    {
        return new CardChoiceRequest(
            CardChoicePresentation.GenericGrid,
            prefs,
            availableCards: availableCards);
    }

    public static CardChoiceRequest ForHand(
        CardSelectorPrefs prefs,
        Func<CardModel, bool>? filter,
        AbstractModel source,
        IReadOnlyList<CardModel> availableCards)
    {
        return new CardChoiceRequest(
            CardChoicePresentation.Hand,
            prefs,
            filter,
            source,
            availableCards: availableCards);
    }

    public static CardChoiceRequest ForHandUpgrade(
        AbstractModel source,
        IReadOnlyList<CardModel> availableCards)
    {
        return new CardChoiceRequest(
            CardChoicePresentation.HandUpgrade,
            new CardSelectorPrefs(CardSelectorPrefs.UpgradeSelectionPrompt, 1),
            card => card.IsUpgradable,
            source,
            availableCards: availableCards);
    }

    public static CardChoiceRequest ForCombatPile(
        CardPile pile,
        CardSelectorPrefs prefs,
        Func<CardModel, bool>? filter)
    {
        IReadOnlyList<CardModel> availableCards = filter == null
            ? pile.Cards.ToList()
            : pile.Cards.Where(filter).ToList();
        return new CardChoiceRequest(
            CardChoicePresentation.CombatPile,
            prefs,
            filter,
            pile: pile,
            availableCards: availableCards);
    }

    public static CardChoiceRequest ForChooseCard(
        IReadOnlyList<CardModel> availableCards,
        bool canSkip)
    {
        return new CardChoiceRequest(
            CardChoicePresentation.ChooseCard,
            new CardSelectorPrefs(CardSelectorPrefs.TransformSelectionPrompt, canSkip ? 0 : 1, 1),
            canSkip: canSkip,
            availableCards: availableCards,
            alwaysRequiresPlayerChoice: true);
    }

    public async Task<IReadOnlyList<CardModel>> ShowSelectionAsync(
        IReadOnlyList<CardModel> availableCards,
        int minSelect,
        int maxSelect)
    {
        NCombatRoom combatRoom = NCombatRoom.Instance ??
                                 throw new InvalidOperationException("Combat room UI is unavailable.");
        NOverlayStack overlayStack = NOverlayStack.Instance ??
                                     throw new InvalidOperationException("Overlay stack is unavailable.");

        switch (Presentation)
        {
            case CardChoicePresentation.Hand:
                {
                    CardChoiceCheckpointService.StabilizeHandRootTransitionForReplay();
                    HashSet<Tween> existingTweens =
                        CardChoiceCheckpointService.CaptureProcessedTweens();
                    Task<IEnumerable<CardModel>> selectionTask = combatRoom.Ui.Hand.SelectCards(
                        Prefs,
                        Filter,
                        Source);
                    await CardChoiceCheckpointService.CompleteNewReplayUiAnimationsAsync(
                        existingTweens);
                    return (await selectionTask).ToList();
                }
            case CardChoicePresentation.HandUpgrade:
                {
                    CardChoiceCheckpointService.StabilizeHandRootTransitionForReplay();
                    HashSet<Tween> existingTweens =
                        CardChoiceCheckpointService.CaptureProcessedTweens();
                    Task<IEnumerable<CardModel>> selectionTask = combatRoom.Ui.Hand.SelectCards(
                        Prefs,
                        Filter,
                        Source,
                        NPlayerHand.Mode.UpgradeSelect);
                    await CardChoiceCheckpointService.CompleteNewReplayUiAnimationsAsync(
                        existingTweens);
                    return (await selectionTask).ToList();
                }
            case CardChoicePresentation.CombatPile:
                {
                    if (Pile == null)
                    {
                        throw new InvalidOperationException("Combat pile choice is missing its pile.");
                    }

                    HashSet<Tween> existingTweens =
                        CardChoiceCheckpointService.CaptureProcessedTweens();
                    NCombatPileCardSelectScreen screen = NCombatPileCardSelectScreen.Create(
                        Pile,
                        Prefs,
                        Filter ?? (_ => true));
                    overlayStack.Push(screen);
                    CardChoiceCheckpointService.SetActiveReplayOverlay(screen);
                    try
                    {
                        await CardChoiceCheckpointService.CompleteNewReplayUiAnimationsAsync(
                            existingTweens);
                        return (await screen.CardsSelected()).ToList();
                    }
                    finally
                    {
                        CardChoiceCheckpointService.ClearActiveReplayOverlay(screen);
                    }
                }
            case CardChoicePresentation.ChooseCard:
                {
                    HashSet<Tween> existingTweens =
                        CardChoiceCheckpointService.CaptureProcessedTweens();
                    NChooseACardSelectionScreen screen =
                        NChooseACardSelectionScreen.ShowScreen(availableCards, CanSkip) ??
                        throw new InvalidOperationException("Choose-a-card screen could not be created.");
                    CardChoiceCheckpointService.SetActiveReplayOverlay(screen);
                    try
                    {
                        await CardChoiceCheckpointService.CompleteNewReplayUiAnimationsAsync(
                            existingTweens);
                        return (await screen.CardsSelected()).OfType<CardModel>().ToList();
                    }
                    finally
                    {
                        CardChoiceCheckpointService.ClearActiveReplayOverlay(screen);
                    }
                }
            default:
                {
                    CardSelectorPrefs prefs = Prefs;
                    if (prefs.MinSelect != minSelect || prefs.MaxSelect != maxSelect)
                    {
                        prefs = new CardSelectorPrefs(
                            CardSelectorPrefs.TransformSelectionPrompt,
                            minSelect,
                            maxSelect);
                    }

                    HashSet<Tween> existingTweens =
                        CardChoiceCheckpointService.CaptureProcessedTweens();
                    NSimpleCardSelectScreen screen =
                        NSimpleCardSelectScreen.Create(availableCards, prefs);
                    overlayStack.Push(screen);
                    CardChoiceCheckpointService.SetActiveReplayOverlay(screen);
                    try
                    {
                        await CardChoiceCheckpointService.CompleteNewReplayUiAnimationsAsync(
                            existingTweens);
                        return (await screen.CardsSelected()).ToList();
                    }
                    finally
                    {
                        CardChoiceCheckpointService.ClearActiveReplayOverlay(screen);
                    }
                }
        }
    }

}
