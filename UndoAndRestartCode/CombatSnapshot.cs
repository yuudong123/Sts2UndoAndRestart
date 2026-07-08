using System.Collections;
using System.Reflection;
using Godot;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Combat.History;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Orbs;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Orbs;
using MegaCrit.Sts2.Core.Nodes.Potions;
using MegaCrit.Sts2.Core.Nodes.Relics;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace UndoAndRestartCode;

internal sealed class CombatSnapshot
{
    private readonly CombatState _state;
    private readonly int _roundNumber;
    private readonly CombatSide _currentSide;
    private readonly bool _playerActionsDisabled;
    private readonly bool _endingPlayerTurnPhaseOne;
    private readonly bool _endingPlayerTurnPhaseTwo;
    private readonly List<Player> _playersTakingExtraTurn;
    private readonly uint _nextActionId;
    private readonly uint _nextHookId;
    private readonly List<uint> _choiceIds;
    private readonly uint _nextCreatureId;
    private readonly List<Creature> _allies;
    private readonly List<Creature> _enemies;
    private readonly List<Creature> _escapedCreatures;
    private readonly List<CardModel> _allCards;
    private readonly List<CardModel> _snapshotCards;
    private readonly Dictionary<Creature, CreatureState> _creatures;
    private readonly Dictionary<Player, PlayerState> _players;
    private readonly Dictionary<AbstractModel, ModelFieldSnapshot> _models = new();
    private readonly Dictionary<CardModel, CardRuntimeSnapshot> _cardRuntimeStates;
    private readonly List<CombatHistoryEntry> _historyEntries;
    private readonly RunStateSnapshot _runState;
    private readonly RunHistorySnapshot _runHistory;
    private readonly CombatVisualSnapshot _visuals;

    public string Reason { get; }
    public string Fingerprint { get; }
    public bool IsManualRestoreTarget => _currentSide == CombatSide.Player &&
                                         !_playerActionsDisabled &&
                                         !_endingPlayerTurnPhaseOne &&
                                         !_endingPlayerTurnPhaseTwo &&
                                         !Reason.Equals("EndPlayerTurnAction:after", StringComparison.Ordinal);

    private CombatSnapshot(CombatState state, string reason)
    {
        _state = state;
        Reason = reason;
        _roundNumber = state.RoundNumber;
        _currentSide = state.CurrentSide;
        _playerActionsDisabled = CombatManager.Instance.PlayerActionsDisabled;
        _endingPlayerTurnPhaseOne = CombatManager.Instance.EndingPlayerTurnPhaseOne;
        _endingPlayerTurnPhaseTwo = CombatManager.Instance.EndingPlayerTurnPhaseTwo;
        _playersTakingExtraTurn =
            ReflectionUtil.GetField<List<Player>>(CombatManager.Instance, "_playersTakingExtraTurn")?.ToList()
            ?? new List<Player>();
        _nextActionId = RunManager.Instance.ActionQueueSet.NextActionId;
        _nextHookId = RunManager.Instance.ActionQueueSynchronizer.NextHookId;
        _choiceIds = RunManager.Instance.PlayerChoiceSynchronizer.ChoiceIds.ToList();
        _nextCreatureId = ReflectionUtil.GetField<uint>(state, "_nextCreatureId");
        _allies = GetCreatureList(state, "_allies");
        _enemies = GetCreatureList(state, "_enemies");
        _escapedCreatures = GetCreatureList(state, "_escapedCreatures");
        List<Player> players = GetPlayersFromCreatures(_allies.Concat(_enemies));
        _allCards = ReflectionUtil.GetField<List<CardModel>>(state, "_allCards")?.ToList() ?? players.SelectMany(p => p.PlayerCombatState?.AllCards ?? Array.Empty<CardModel>()).ToList();
        _creatures = _allies.Concat(_enemies).Concat(_escapedCreatures).Distinct().ToDictionary(creature => creature, CreatureState.Capture);
        _players = players.ToDictionary(player => player, PlayerState.Capture);
        _snapshotCards = _allCards.Concat(_players.Values.SelectMany(player => player.AllPileCards)).Distinct().ToList();
        _cardRuntimeStates = _snapshotCards.ToDictionary(card => card, CardRuntimeSnapshot.Capture);
        // 플레이어 조작 가능 경계에서는 히스토리가 더 이상 변경되지 않음. 매 카드마다 전체 히스토리 복제할 필요 없음.
        _historyEntries = CombatManager.Instance.History.Entries.ToList();
        _runState = RunStateSnapshot.Capture((RunState)state.RunState);
        _runHistory = RunHistorySnapshot.Capture((RunState)state.RunState);
        _visuals = CombatVisualSnapshot.Capture(_allies.Concat(_enemies));

        foreach (Creature creature in _creatures.Keys)
        {
            CaptureModel(creature.Monster);
            foreach (PowerModel power in creature.Powers)
            {
                CaptureModel(power);
            }
        }

        CaptureModel(state.Encounter);
        foreach (ModifierModel modifier in state.Modifiers)
        {
            CaptureModel(modifier);
        }

        foreach (BadgeModel badge in state.BadgeModels)
        {
            CaptureModel(badge);
        }

        foreach (CardModel card in _snapshotCards)
        {
            CaptureModel(card);
            CaptureModel(card.Enchantment);
            CaptureModel(card.Affliction);
        }

        foreach (PlayerState playerState in _players.Values)
        {
            foreach (PotionModel? potion in playerState.Potions)
            {
                CaptureModel(potion);
            }

            foreach (RelicModel relic in playerState.Relics)
            {
                CaptureModel(relic);
            }

            foreach (OrbModel orb in playerState.Orbs)
            {
                CaptureModel(orb);
            }
        }

        Fingerprint = BuildFingerprint();
    }

    public static CombatSnapshot Capture(CombatState state, string reason)
    {
        return new CombatSnapshot(state, reason);
    }

    public bool BelongsTo(CombatState state)
    {
        return ReferenceEquals(_state, state);
    }

    private static List<Creature> GetCreatureList(CombatState state, string fieldName)
    {
        return ReflectionUtil.GetField<List<Creature>>(state, fieldName)?.ToList() ?? new List<Creature>();
    }

    private static List<Player> GetPlayersFromCreatures(IEnumerable<Creature> creatures)
    {
        return creatures
            .Select(creature => creature.Player)
            .Where(player => player != null)
            .Cast<Player>()
            .Distinct()
            .ToList();
    }

    private IEnumerable<Creature> CurrentCreatures()
    {
        return (ReflectionUtil.GetField<List<Creature>>(_state, "_allies") ?? new List<Creature>())
            .Concat(ReflectionUtil.GetField<List<Creature>>(_state, "_enemies") ?? new List<Creature>());
    }

    private Player? LocalPlayer()
    {
        return _players.Keys.FirstOrDefault();
    }

    public void Restore()
    {
        TransientCardVfxCleanup.Clear();
        RestoreCreatures();
        _runState.Restore();
        RestoreModels();
        RestoreCombatFields();
        RestorePlayers();
        RestoreCards();
        RestoreHistory();
        RestoreRunHistory();
        RestoreCardRuntimeStates();
        ClearTransientRelicActivationStates();
        RestoreUi();
        SnapshotValidator.ValidatePlayableState(_state);
    }

    private void CaptureModel(AbstractModel? model)
    {
        if (model != null && !_models.ContainsKey(model))
        {
            _models[model] = ModelFieldSnapshot.Capture(model);
        }
    }

    private void RestoreModels()
    {
        foreach ((AbstractModel model, ModelFieldSnapshot snapshot) in _models)
        {
            snapshot.Restore(model);
        }
    }

    private void RestoreCombatFields()
    {
        _state.RoundNumber = _roundNumber;
        _state.CurrentSide = _currentSide;
        ReflectionUtil.SetField(_state, "_nextCreatureId", _nextCreatureId);
        ReplaceReadOnlyList(ReflectionUtil.GetField<List<Creature>>(_state, "_allies"), _allies);
        ReplaceReadOnlyList(ReflectionUtil.GetField<List<Creature>>(_state, "_enemies"), _enemies);
        ReplaceReadOnlyList(ReflectionUtil.GetField<List<Creature>>(_state, "_escapedCreatures"), _escapedCreatures);
        ReplaceReadOnlyList(ReflectionUtil.GetField<List<CardModel>>(_state, "_allCards"), _allCards);

        foreach (Creature creature in _allies.Concat(_enemies).Concat(_escapedCreatures))
        {
            creature.CombatState = _state;
        }
    }

    private void RestoreCreatures()
    {
        HashSet<Creature> targetCreatures = _allies.Concat(_enemies).ToHashSet();
        foreach (Creature liveCreature in CurrentCreatures().ToList())
        {
            if (!targetCreatures.Contains(liveCreature))
            {
                RemoveCreatureFromCombat(liveCreature);
            }
        }

        foreach (Creature creature in targetCreatures)
        {
            if (!_state.ContainsCreature(creature))
            {
                creature.CombatState = _state;
                AddCreatureToList(creature);
                try
                {
                    CombatManager.Instance.StateTracker.Subscribe(creature);
                }
                catch (Exception ex)
                {
                    MainFile.Logger.Warn($"Failed to subscribe creature {creature.LogName}: {ex.Message}");
                }

                if (NCombatRoom.Instance?.GetCreatureNode(creature) == null &&
                    !ParkedCreatureNodeRegistry.Unpark(creature))
                {
                    try
                    {
                        NCombatRoom.Instance?.AddCreature(creature);
                    }
                    catch (Exception ex)
                    {
                        MainFile.Logger.Warn($"Failed to re-add creature node {creature.LogName}: {ex.Message}");
                    }
                }
            }
        }

        foreach ((Creature creature, CreatureState state) in _creatures)
        {
            state.Restore(creature);
        }
    }

    private void RestorePlayers()
    {
        foreach ((Player player, PlayerState state) in _players)
        {
            state.Restore(player);
        }
    }

    private void RestoreCards()
    {
        HashSet<CardModel> savedCards = _snapshotCards.ToHashSet();
        HashSet<CardModel> liveCards = GetLiveAllCards().ToHashSet();

        foreach (PlayerState playerState in _players.Values)
        {
            playerState.RestorePiles();
        }

        foreach (CardModel card in liveCards.Where(card => !savedCards.Contains(card)))
        {
            card.HasBeenRemovedFromState = true;
            ReflectionUtil.SetField(card, "_owner", null);
        }
    }

    private void RestoreCardRuntimeStates()
    {
        foreach ((CardModel card, CardRuntimeSnapshot state) in _cardRuntimeStates)
        {
            state.Restore(card);
        }
    }

    private void RestoreHistory()
    {
        CombatHistory history = CombatManager.Instance.History;
        List<CombatHistoryEntry>? entries = ReflectionUtil.GetField<List<CombatHistoryEntry>>(history, "_entries");
        if (entries == null)
        {
            MainFile.Logger.Warn("Failed to restore combat history: backing entry list not found.");
            return;
        }

        ReflectionUtil.ReplaceList(entries, _historyEntries);
        ReflectionUtil.GetField<Action>(history, "Changed")?.Invoke();
    }

    private void RestoreRunHistory()
    {
        _runHistory.Restore();
    }

    private void RestoreUi()
    {
        ResetCombatManagerFlags();
        ResetTargetingState();
        SyncCreatureNodes();
        _visuals.Restore(CurrentCreatures());
        RefreshPotionUi();
        RefreshRelicUi();
        RefreshOrbUi();
        ClearTransientCardPlayUi();
        RefreshHandUi();
        RefreshPlayContainer();
        SovereignBladeVfxSync.Refresh(_players.Keys);
        RefreshCreatureUi();
        RefreshPileEvents();
        RefreshPileCounters();
        NotifyCombatStateChanged();
    }

    private void ResetCombatManagerFlags()
    {
        CombatManager manager = CombatManager.Instance;
        ReflectionUtil.SetField(manager, "_pendingLoss", null);
        ReflectionUtil.SetField(manager, "_playerActionsDisabled", false);
        ReflectionUtil.SetField(manager, "_inPlayerTurnSetup", false);
        ReflectionUtil.SetField(manager, "_deferredEndTurnTransition", null);
        ReflectionUtil.SetField(manager, "<IsPaused>k__BackingField", false);
        ReflectionUtil.SetField(manager, "<IsEnemyTurnStarted>k__BackingField", _currentSide == CombatSide.Enemy);
        ReflectionUtil.SetField(manager, "<EndingPlayerTurnPhaseOne>k__BackingField", false);
        ReflectionUtil.SetField(manager, "<EndingPlayerTurnPhaseTwo>k__BackingField", false);

        ReflectionUtil.GetField<HashSet<Player>>(manager, "_playersReadyToEndTurn")?.Clear();
        ReflectionUtil.GetField<HashSet<Player>>(manager, "_playersReadyToBeginEnemyTurn")?.Clear();
        ReflectionUtil.GetField<Dictionary<Player, int>>(manager, "_cardOrPotionEffectDepth")?.Clear();
        ReplaceReadOnlyList(
            ReflectionUtil.GetField<List<Player>>(manager, "_playersTakingExtraTurn"),
            _playersTakingExtraTurn);

        ReflectionUtil.SetField(RunManager.Instance.ActionQueueSet, "_nextId", _nextActionId);
        ReflectionUtil.SetField(RunManager.Instance.ActionQueueSynchronizer, "_nextHookId", _nextHookId);
        ReplaceReadOnlyList(
            ReflectionUtil.GetField<List<uint>>(RunManager.Instance.PlayerChoiceSynchronizer, "_choiceIds"),
            _choiceIds);

        RunManager.Instance.ActionExecutor.Unpause();
        RunManager.Instance.ActionQueueSet.UnpauseAllPlayerQueues();
        RunManager.Instance.ActionQueueSynchronizer.SetCombatState(
            _currentSide == CombatSide.Player ? ActionSynchronizerCombatState.PlayPhase : ActionSynchronizerCombatState.NotPlayPhase);
    }

    private static void ResetTargetingState()
    {
        try
        {
            NTargetManager? targetManager = NTargetManager.Instance;
            targetManager?.CancelTargeting();
            if (targetManager != null)
            {
                ReflectionUtil.SetField(targetManager, "HoveredNode", null);
            }
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"Failed to reset targeting state: {ex.Message}");
        }
    }

    private void SyncCreatureNodes()
    {
        NCombatRoom? room = NCombatRoom.Instance;
        if (room == null)
        {
            return;
        }

        HashSet<Creature> wanted = _allies.Concat(_enemies).ToHashSet();
        List<NCreature>? nodes = ReflectionUtil.GetField<List<NCreature>>(room, "_creatureNodes");
        if (nodes == null)
        {
            return;
        }

        foreach (NCreature node in nodes.ToList())
        {
            if (!wanted.Contains(node.Entity))
            {
                ParkedCreatureNodeRegistry.Park(node.Entity);
            }
        }

        foreach (Creature creature in wanted)
        {
            if (room.GetCreatureNode(creature) == null)
            {
                if (ParkedCreatureNodeRegistry.Unpark(creature))
                {
                    continue;
                }

                try
                {
                    room.AddCreature(creature);
                }
                catch (Exception ex)
                {
                    MainFile.Logger.Warn($"Failed to sync creature node {creature.LogName}: {ex.Message}");
                }
            }
        }

        ReflectionUtil.Method(room.GetType(), "UpdateCreatureNavigation")?.Invoke(room, null);
    }

    private void RefreshPotionUi()
    {
        Player? localPlayer = LocalPlayer();
        foreach ((Player player, PlayerState state) in _players)
        {
            if (!ReferenceEquals(player, localPlayer))
            {
                continue;
            }

            NPotionContainer? container = NRun.Instance?.GlobalUi?.TopBar?.PotionContainer;
            List<NPotionHolder>? holders = container != null ? ReflectionUtil.GetField<List<NPotionHolder>>(container, "_holders") : null;
            if (container == null || holders == null)
            {
                return;
            }

            ReflectionUtil.Method(container.GetType(), "GrowPotionHolders", typeof(int))?.Invoke(container, new object?[] { player.MaxPotionCount });
            holders = ReflectionUtil.GetField<List<NPotionHolder>>(container, "_holders") ?? holders;

            for (int i = 0; i < holders.Count; i++)
            {
                PotionModel? desired = i < state.Potions.Count ? state.Potions[i] : null;
                SyncPotionHolder(holders[i], desired);
            }

            ReflectionUtil.Method(container.GetType(), "UpdateNavigation")?.Invoke(container, null);
            return;
        }
    }

    private static void SyncPotionHolder(NPotionHolder holder, PotionModel? desired)
    {
        NPotion? current = holder.Potion;
        if (current?.Model == desired)
        {
            holder.CancelPotionUseOrDiscard();
            return;
        }

        RemovePotionNodeInstantly(holder);
        if (desired == null)
        {
            SetPotionHolderEmptyVisual(holder);
            return;
        }

        NPotion? node = NPotion.Create(desired);
        if (node == null)
        {
            MainFile.Logger.Warn($"Failed to create potion node while restoring: {desired.Id.Entry}");
            return;
        }

        node.Position = new Vector2(-30f, -30f);
        holder.AddPotion(node);
        holder.CancelPotionUseOrDiscard();
    }

    private static void RemovePotionNodeInstantly(NPotionHolder holder)
    {
        ReflectionUtil.GetField<Tween>(holder, "_emptyPotionTween")?.Kill();
        ReflectionUtil.GetField<Tween>(holder, "_hoverTween")?.Kill();
        ReflectionUtil.SetField(holder, "_disabledUntilPotionRemoved", false);
        holder.Modulate = Colors.White;

        NPotion? potion = holder.Potion;
        if (potion != null && GodotObject.IsInstanceValid(potion))
        {
            potion.GetParent()?.RemoveChild(potion);
            potion.QueueFree();
        }

        ReflectionUtil.SetField(holder, "<Potion>k__BackingField", null);
    }

    private static void SetPotionHolderEmptyVisual(NPotionHolder holder)
    {
        TextureRect? emptyIcon = ReflectionUtil.GetField<TextureRect>(holder, "_emptyIcon");
        if (emptyIcon != null && GodotObject.IsInstanceValid(emptyIcon))
        {
            emptyIcon.Modulate = Colors.White;
        }
    }

    private void RefreshRelicUi()
    {
        Player? localPlayer = LocalPlayer();
        NRelicInventory? inventory = NRun.Instance?.GlobalUi?.RelicInventory;
        if (localPlayer == null || inventory == null)
        {
            return;
        }

        List<NRelicInventoryHolder>? holders =
            ReflectionUtil.GetField<List<NRelicInventoryHolder>>(inventory, "_relicNodes");
        if (holders == null)
        {
            return;
        }

        bool inventoryMatches =
            holders.Count == localPlayer.Relics.Count &&
            holders.Select(holder => holder.Relic?.Model)
                .SequenceEqual(localPlayer.Relics.Cast<RelicModel?>());
        if (inventoryMatches)
        {
            foreach (NRelicInventoryHolder holder in holders)
            {
                RefreshRelicHolder(holder);
            }

            return;
        }

        foreach (NRelicInventoryHolder holder in holders.ToList())
        {
            if (GodotObject.IsInstanceValid(holder))
            {
                inventory.RemoveChild(holder);
                holder.QueueFree();
            }
        }
        holders.Clear();

        MethodInfo? add = ReflectionUtil.Method(
            inventory.GetType(),
            "Add",
            typeof(RelicModel),
            typeof(bool),
            typeof(int));
        for (int index = 0; index < localPlayer.Relics.Count; index++)
        {
            add?.Invoke(inventory, new object?[] { localPlayer.Relics[index], true, index });
        }
        ReflectionUtil.Method(inventory.GetType(), "UpdateNavigation")?.Invoke(inventory, null);
        NTopBar? topBar = NRun.Instance?.GlobalUi?.TopBar;
        if (topBar != null)
        {
            ReflectionUtil.Method(topBar.GetType(), "UpdateNavigation")?.Invoke(topBar, null);
        }
    }

    private static void RefreshRelicHolder(NRelicInventoryHolder holder)
    {
        try
        {
            ReflectionUtil.GetField<Tween>(holder, "_hoverTween")?.Kill();
            ReflectionUtil.GetField<Tween>(holder, "_obtainedTween")?.Kill();
            holder.Relic.Icon.Scale = Vector2.One;
            holder.Relic.Icon.Modulate = Colors.White;

            ReflectionUtil.Method(holder.GetType(), "RefreshAmount")?.Invoke(holder, null);
            ReflectionUtil.Method(holder.GetType(), "RefreshStatus")?.Invoke(holder, null);
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"Failed to refresh relic UI while restoring: {ex.Message}");
        }
    }

    private void ClearTransientRelicActivationStates()
    {
        foreach (RelicModel relic in _players.Values.SelectMany(player => player.Relics))
        {
            ClearTransientRelicActivationState(relic);
        }
    }

    private static void ClearTransientRelicActivationState(RelicModel relic)
    {
        try
        {
            FieldInfo? activationField = ReflectionUtil.Field(relic.GetType(), "_isActivating");
            if (activationField?.FieldType != typeof(bool) ||
                activationField.GetValue(relic) is not true)
            {
                return;
            }

            activationField.SetValue(relic, false);

            MethodInfo? updateDisplay = ReflectionUtil.Method(relic.GetType(), "UpdateDisplay");
            if (updateDisplay != null)
            {
                updateDisplay.Invoke(relic, null);
                return;
            }

            ReflectionUtil.Method(relic.GetType(), "InvokeDisplayAmountChanged")?.Invoke(relic, null);
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"Failed to clear transient relic activation for {relic.Id}: {ex.Message}");
        }
    }

    private void RefreshOrbUi()
    {
        Player? localPlayer = LocalPlayer();
        foreach ((Player player, PlayerState state) in _players)
        {
            if (!ReferenceEquals(player, localPlayer))
            {
                continue;
            }

            NCreature? creatureNode = NCombatRoom.Instance?.GetCreatureNode(player.Creature);
            NOrbManager? manager = creatureNode?.OrbManager;
            if (creatureNode == null || manager == null)
            {
                return;
            }

            SyncOrbManager(manager, state.Orbs, state.OrbCapacity);
            ReflectionUtil.Method(creatureNode.GetType(), "SetOrbManagerPosition")?.Invoke(creatureNode, null);
            return;
        }
    }

    private static void SyncOrbManager(NOrbManager manager, IReadOnlyList<OrbModel> desiredOrbs, int capacity)
    {
        try
        {
            List<NOrb>? orbNodes = ReflectionUtil.GetField<List<NOrb>>(manager, "_orbs");
            Control? orbContainer = ReflectionUtil.GetField<Control>(manager, "_orbContainer");
            if (orbNodes == null || orbContainer == null)
            {
                return;
            }

            ReflectionUtil.GetField<Tween>(manager, "_curTween")?.Kill();
            bool queueMatches =
                orbNodes.Count == Math.Max(0, capacity) &&
                orbNodes.Select(node => node.Model)
                    .SequenceEqual(
                        Enumerable.Range(0, Math.Max(0, capacity))
                            .Select(index => index < desiredOrbs.Count ? desiredOrbs[index] : null));
            if (queueMatches)
            {
                foreach (NOrb orbNode in orbNodes)
                {
                    orbNode.Modulate = Colors.White;
                    orbNode.UpdateVisuals(isEvoking: false);
                }

                ReflectionUtil.Method(manager.GetType(), "UpdateControllerNavigation")?.Invoke(manager, null);
                return;
            }

            foreach (NOrb oldOrb in orbNodes.ToList())
            {
                if (GodotObject.IsInstanceValid(oldOrb))
                {
                    oldOrb.GetParent()?.RemoveChild(oldOrb);
                    oldOrb.QueueFree();
                }
            }

            foreach (NOrb strayOrb in orbContainer.GetChildren().OfType<NOrb>().ToList())
            {
                if (!orbNodes.Contains(strayOrb) && GodotObject.IsInstanceValid(strayOrb))
                {
                    orbContainer.RemoveChild(strayOrb);
                    strayOrb.QueueFree();
                }
            }

            orbNodes.Clear();
            int slotCount = Math.Max(0, capacity);
            for (int i = 0; i < slotCount; i++)
            {
                OrbModel? model = i < desiredOrbs.Count ? desiredOrbs[i] : null;
                NOrb node = NOrb.Create(manager.IsLocal, model);
                orbContainer.AddChild(node);
                node.Position = GetOrbSlotPosition(i, slotCount, manager.IsLocal);
                node.Modulate = Colors.White;
                node.UpdateVisuals(isEvoking: false);
                orbNodes.Add(node);
            }

            ReflectionUtil.Method(manager.GetType(), "UpdateControllerNavigation")?.Invoke(manager, null);
            ReflectionUtil.Method(manager.GetType(), "UpdateVisuals", typeof(OrbEvokeType))?.Invoke(manager, new object?[] { OrbEvokeType.None });
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"Failed to refresh orb UI while restoring: {ex.Message}");
        }
    }

    private static Vector2 GetOrbSlotPosition(int index, int capacity, bool isLocal)
    {
        if (capacity <= 0)
        {
            return Vector2.Zero;
        }

        float spread = 125f;
        float step = capacity > 1 ? spread / (capacity - 1) : 0f;
        float radius = Mathf.Lerp(225f, 300f, ((float)capacity - 3f) / 7f);
        if (!isLocal)
        {
            radius *= 0.75f;
        }

        float degrees = -25f - (spread - step * index);
        float radians = Mathf.DegToRad(degrees);
        return new Vector2(0f - Mathf.Cos(radians), Mathf.Sin(radians)) * radius;
    }

    private void RefreshHandUi()
    {
        NPlayerHand? hand = NPlayerHand.Instance;
        Player? localPlayer = LocalPlayer();
        CardPile? handPile = localPlayer?.PlayerCombatState?.Hand;
        if (hand == null || handPile == null)
        {
            return;
        }

        localPlayer?.PlayerCombatState?.RecalculateCardValues();

        try
        {
            hand.CancelAllCardPlay();
        }
        catch
        {
            // 이미 정리된 드래그/플레이 노드 때문에 복원이 막히면 안 됨.
        }

        foreach (NHandCardHolder holder in hand.ActiveHolders.ToList())
        {
            CardModel? model = holder.CardNode?.Model;
            if (model == null || !handPile.Cards.Contains(model))
            {
                hand.RemoveCardHolder(holder);
            }
        }

        for (int i = 0; i < handPile.Cards.Count; i++)
        {
            CardModel card = handPile.Cards[i];
            NCardHolder? holder = hand.GetCardHolder(card);
            if (holder == null)
            {
                NCard? node = NCard.Create(card);
                if (node != null)
                {
                    NHandCardHolder createdHolder = hand.Add(node, i);
                    createdHolder.UpdateCard();
                }
            }
            else if (holder is NHandCardHolder handHolder && handHolder.GetIndex() != i)
            {
                hand.CardHolderContainer.MoveChild(handHolder, i);
                handHolder.UpdateCard();
            }
            else if (holder is NHandCardHolder existingHandHolder)
            {
                existingHandHolder.UpdateCard();
            }
        }

        hand.ForceRefreshCardIndices();
        RefreshHandCardVisuals(hand);
        SnapHandLayoutInstantly(hand);
    }

    private static void RefreshHandCardVisuals(NPlayerHand hand)
    {
        foreach (NHandCardHolder holder in hand.ActiveHolders)
        {
            holder.UpdateCard();
        }
    }

    private static void SnapHandLayoutInstantly(NPlayerHand hand)
    {
        foreach (NHandCardHolder holder in hand.ActiveHolders)
        {
            try
            {
                holder.CancelDrag();
                ReflectionUtil.GetField<CancellationTokenSource>(holder, "_positionCancelToken")?.Cancel();
                ReflectionUtil.GetField<CancellationTokenSource>(holder, "_angleCancelToken")?.Cancel();
                ReflectionUtil.GetField<CancellationTokenSource>(holder, "_scaleCancelToken")?.Cancel();
                ReflectionUtil.GetField<Tween>(holder, "_hoverTween")?.Kill();
                ReflectionUtil.SetField(holder, "_currentPressedAction", null);
                ReflectionUtil.SetField(holder, "_isHovered", false);
                ReflectionUtil.SetField(holder, "_isFocused", false);
                holder.Position = holder.TargetPosition;
                holder.SetAngleInstantly(holder.TargetAngle);
                holder.SetScaleInstantly(ReflectionUtil.GetField<Vector2>(holder, "_targetScale"));
                holder.SetClickable(true);
                if (holder.Hitbox != null && GodotObject.IsInstanceValid(holder.Hitbox))
                {
                    holder.Hitbox.SetEnabled(enabled: true);
                }
            }
            catch (Exception ex)
            {
                MainFile.Logger.Warn($"Failed to snap hand card holder instantly: {ex.Message}");
            }
        }
    }

    private void ClearTransientCardPlayUi()
    {
        try
        {
            ClearCurrentCardPlay();
            ClearPlayQueue();
            ClearLooseCombatUiCards();
            ClearCombatUiPlayContainerCache();
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"Failed to clear transient card play UI: {ex.Message}");
        }
    }

    private static void ClearCurrentCardPlay()
    {
        NPlayerHand? hand = NPlayerHand.Instance;
        if (hand == null)
        {
            return;
        }

        try
        {
            hand.CancelAllCardPlay();
        }
        catch
        {
            // 되돌리기 복원 중에는 카드 플레이 노드가 이미 반쯤 정리된 상태일 수 있음.
        }

        Node? currentCardPlay = ReflectionUtil.GetField<Node>(hand, "_currentCardPlay");
        if (currentCardPlay != null && GodotObject.IsInstanceValid(currentCardPlay))
        {
            currentCardPlay.GetParent()?.RemoveChild(currentCardPlay);
            currentCardPlay.QueueFree();
        }

        ReflectionUtil.SetField(hand, "_currentCardPlay", null);
    }

    private static void ClearPlayQueue()
    {
        NCardPlayQueue? playQueue = NCardPlayQueue.Instance;
        if (playQueue == null || !GodotObject.IsInstanceValid(playQueue))
        {
            return;
        }

        IList? queue = ReflectionUtil.GetField<IList>(playQueue, "_playQueue");
        if (queue != null)
        {
            foreach (object item in queue.Cast<object>().ToList())
            {
                ReflectionUtil.GetField<Tween>(item, "currentTween")?.Kill();
                if (ReflectionUtil.GetField<NCard>(item, "card") is { } queuedCard)
                {
                    RemoveCardNodeImmediately(queuedCard);
                }
            }

            queue.Clear();
        }

        foreach (NCard card in playQueue.GetChildren().OfType<NCard>().ToList())
        {
            RemoveCardNodeImmediately(card);
        }
    }

    private static void ClearLooseCombatUiCards()
    {
        Node? ui = NCombatRoom.Instance?.Ui;
        if (ui == null || !GodotObject.IsInstanceValid(ui))
        {
            return;
        }

        foreach (NCard card in ui.GetChildren().OfType<NCard>().ToList())
        {
            RemoveCardNodeImmediately(card);
        }
    }

    private static void ClearCombatUiPlayContainerCache()
    {
        Node? ui = NCombatRoom.Instance?.Ui;
        if (ui == null)
        {
            return;
        }

        ReflectionUtil.GetField<IDictionary>(ui, "_originalPlayContainerCardPositions")?.Clear();
        ReflectionUtil.GetField<IDictionary>(ui, "_originalPlayContainerCardScales")?.Clear();
    }

    private void RefreshPlayContainer()
    {
        Control? playContainer = NCombatRoom.Instance?.Ui?.PlayContainer;
        if (playContainer == null)
        {
            return;
        }

        HashSet<CardModel> playCards = _players.Values.SelectMany(p => p.PlayPile).ToHashSet();
        foreach (NCard node in playContainer.GetChildren().OfType<NCard>().ToList())
        {
            if (node.Model == null || !playCards.Contains(node.Model))
            {
                RemoveCardNodeImmediately(node);
            }
        }
    }

    private static void RemoveCardNodeImmediately(NCard node)
    {
        if (!GodotObject.IsInstanceValid(node))
        {
            return;
        }

        try
        {
            node.PlayPileTween?.Kill();
            node.PlayPileTween = null;
            ReflectionUtil.GetField<Tween>(node, "<RandomizeCostTween>k__BackingField")?.Kill();
            ReflectionUtil.SetField(node, "<RandomizeCostTween>k__BackingField", null);
        }
        catch
        {
            // 선택 트윈을 완벽히 정리하는 것보다 카드 노드를 제거하는 게 더 중요함.
        }

        node.GetParent()?.RemoveChild(node);
        node.QueueFree();
    }

    private void RefreshCreatureUi()
    {
        foreach (Creature creature in _allies.Concat(_enemies))
        {
            NCreature? node = NCombatRoom.Instance?.GetCreatureNode(creature);
            if (node == null)
            {
                continue;
            }

            RefreshPowerContainer(node, creature);
            NormalizeCreatureInteraction(node, creature);
            ReflectionUtil.Method(node.GetType(), "UpdateBounds", typeof(Node))?.Invoke(node, new object?[] { node.Visuals });
            Node? stateDisplay = ReflectionUtil.GetField<Node>(node, "_stateDisplay");
            ReflectionUtil.Method(stateDisplay?.GetType() ?? typeof(Node), "RefreshValues")?.Invoke(stateDisplay!, null);
            if (creature.IsEnemy && creature.IsAlive)
            {
                _ = node.RefreshIntents();
            }
        }
    }

    private static void NormalizeCreatureInteraction(NCreature node, Creature creature)
    {
        bool shouldInteract = creature.IsAlive && (!creature.IsMonster || creature.Monster?.IsHealthBarVisible != false);
        try
        {
            if (creature.IsAlive)
            {
                node.DeathAnimationTask = null;
                ReflectionUtil.Method(node.GetType(), "ImmediatelySetIdle")?.Invoke(node, null);
                node.Hitbox.FocusMode = Control.FocusModeEnum.All;
            }

            ReflectionUtil.Method(node.GetType(), "ToggleIsInteractable", typeof(bool))?.Invoke(node, new object?[] { shouldInteract });
            if (shouldInteract)
            {
                node.Hitbox.MouseFilter = Control.MouseFilterEnum.Stop;
                node.Hitbox.Visible = true;
            }
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"Failed to normalize creature interaction for {creature.LogName}: {ex.Message}");
        }
    }

    private static void RefreshPowerContainer(NCreature node, Creature creature)
    {
        Node? stateDisplay = ReflectionUtil.GetField<Node>(node, "_stateDisplay");
        if (stateDisplay == null)
        {
            return;
        }

        Node? powerContainer = ReflectionUtil.GetField<Node>(stateDisplay, "_powerContainer");
        if (powerContainer == null)
        {
            return;
        }

        IList? powerNodes = ReflectionUtil.GetField<IList>(powerContainer, "_powerNodes");
        if (powerNodes == null)
        {
            return;
        }

        List<PowerModel> visiblePowers = creature.Powers.Where(power => power.IsVisible).ToList();
        List<NPower> existingNodes = powerNodes.Cast<NPower>().ToList();
        if (existingNodes.Count == visiblePowers.Count &&
            existingNodes.Select(node => node.Model).SequenceEqual(visiblePowers))
        {
            foreach (NPower powerNode in existingNodes)
            {
                ReflectionUtil.GetField<Tween>(powerNode, "_animInTween")?.Kill();
                powerNode.Modulate = Colors.White;
                ReflectionUtil.Method(powerNode.GetType(), "RefreshAmount")?.Invoke(powerNode, null);
            }

            ReflectionUtil.Method(powerContainer.GetType(), "UpdatePositions")?.Invoke(powerContainer, null);
            return;
        }

        foreach (Node powerNode in existingNodes)
        {
            powerNode.QueueFree();
        }

        powerNodes.Clear();
        MethodInfo? add = ReflectionUtil.Method(powerContainer.GetType(), "Add", typeof(PowerModel));
        foreach (PowerModel power in visiblePowers)
        {
            add?.Invoke(powerContainer, new object?[] { power });
        }

        ReflectionUtil.Method(powerContainer.GetType(), "UpdatePositions")?.Invoke(powerContainer, null);
    }

    private void RefreshPileEvents()
    {
        foreach (Player player in _players.Keys)
        {
            if (player.PlayerCombatState == null)
            {
                continue;
            }

            foreach (CardPile pile in player.Piles)
            {
                pile.InvokeContentsChanged();
            }
        }
    }

    private void RefreshPileCounters()
    {
        NCombatUi? ui = NCombatRoom.Instance?.Ui;
        PlayerCombatState? combatState = LocalPlayer()?.PlayerCombatState;
        if (ui == null || combatState == null)
        {
            return;
        }

        RefreshPileCounter(ui.DrawPile, combatState.DrawPile);
        RefreshPileCounter(ui.DiscardPile, combatState.DiscardPile);
        RefreshPileCounter(ui.ExhaustPile, combatState.ExhaustPile);
    }

    private static void RefreshPileCounter(NCombatCardPile? button, CardPile? pile)
    {
        if (button == null || pile == null)
        {
            return;
        }

        try
        {
            int count = pile.Cards.Count;
            ReflectionUtil.SetField(button, "_currentCount", count);
            ReflectionUtil.GetField<Tween>(button, "_bumpTween")?.Kill();

            MegaLabel? countLabel = ReflectionUtil.GetField<MegaLabel>(button, "_countLabel");
            if (countLabel != null && GodotObject.IsInstanceValid(countLabel))
            {
                countLabel.SetTextAutoSize(count.ToString());
                countLabel.PivotOffset = countLabel.Size * 0.5f;
                countLabel.Scale = Vector2.One;
            }

            Control? icon = ReflectionUtil.GetField<Control>(button, "_icon");
            if (icon != null && GodotObject.IsInstanceValid(icon))
            {
                icon.Scale = Vector2.One;
            }
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"Failed to refresh {pile.Type} pile counter: {ex.Message}");
        }
    }

    private static void NotifyCombatStateChanged()
    {
        try
        {
            ReflectionUtil.Method(CombatManager.Instance.StateTracker.GetType(), "NotifyCombatStateChanged", typeof(string))
                ?.Invoke(CombatManager.Instance.StateTracker, new object?[] { "UndoAndRedo restore" });
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"Failed to notify combat state changed: {ex.Message}");
        }
    }

    private IEnumerable<CardModel> GetLiveAllCards()
    {
        return _players.Keys
            .SelectMany(player => player.PlayerCombatState?.AllPiles ?? Array.Empty<CardPile>())
            .SelectMany(pile => pile.Cards)
            .Concat(ReflectionUtil.GetField<List<CardModel>>(_state, "_allCards") ?? new List<CardModel>())
            .Distinct();
    }

    private void RemoveCreatureFromCombat(Creature creature)
    {
        try
        {
            CombatManager.Instance.StateTracker.Unsubscribe(creature);
        }
        catch
        {
            // 일부만 생성된 크리처는 구독된 적이 없을 수 있음.
        }

        ReflectionUtil.GetField<List<Creature>>(_state, "_allies")?.Remove(creature);
        ReflectionUtil.GetField<List<Creature>>(_state, "_enemies")?.Remove(creature);
        creature.CombatState = null;

        NCreature? node = NCombatRoom.Instance?.GetCreatureNode(creature);
        if (node != null)
        {
            ParkedCreatureNodeRegistry.Park(creature);
        }
    }

    private void AddCreatureToList(Creature creature)
    {
        List<Creature>? list = creature.Side == CombatSide.Player
            ? ReflectionUtil.GetField<List<Creature>>(_state, "_allies")
            : ReflectionUtil.GetField<List<Creature>>(_state, "_enemies");
        if (list != null && !list.Contains(creature))
        {
            list.Add(creature);
        }
    }

    private static void ReplaceReadOnlyList<T>(List<T>? destination, IEnumerable<T> source)
    {
        if (destination != null)
        {
            ReflectionUtil.ReplaceList(destination, source);
        }
    }

    private string BuildFingerprint()
    {
        List<string> parts = new()
        {
            _roundNumber.ToString(),
            _currentSide.ToString(),
            $"history={_historyEntries.Count}",
            $"action={_nextActionId}",
            $"hook={_nextHookId}",
            string.Join(",", _snapshotCards.Select(CardRuntimeSnapshot.BuildFingerprint)),
            string.Join(",", _allies.Concat(_enemies).Select(c => $"{c.CombatId}:{c.CurrentHp}:{c.MaxHp}:{c.Block}:{string.Join('|', c.Powers.Select(p => $"{p.Id.Entry}:{p.Amount}"))}"))
        };

        foreach ((Player player, PlayerState state) in _players)
        {
            string orbs = string.Join(',', state.Orbs.Select(orb => $"{orb.Id.Entry}:{orb.GetHashCode()}"));
            parts.Add($"{player.NetId}:{player.Gold}:{state.Energy}:{state.Stars}:{string.Join('/', state.PileFingerprints)}:{string.Join(',', state.Potions.Select(p => p?.Id.Entry ?? "null"))}:orbs({state.OrbCapacity})={orbs}");
        }

        return string.Join(";", parts);
    }

    private sealed class CreatureState
    {
        private readonly uint? _combatId;
        private readonly int _currentHp;
        private readonly int _maxHp;
        private readonly int _block;
        private readonly int? _monsterMaxHpBeforeModification;
        private readonly HpDisplay _hpDisplay;
        private readonly Player? _petOwner;
        private readonly string? _slotName;
        private readonly CombatSide _side;
        private readonly List<PowerModel> _powers;
        private readonly MonsterMoveSnapshot? _monsterMove;

        private CreatureState(Creature creature)
        {
            _combatId = creature.CombatId;
            _currentHp = creature.CurrentHp;
            _maxHp = creature.MaxHp;
            _block = creature.Block;
            _monsterMaxHpBeforeModification = creature.MonsterMaxHpBeforeModification;
            _hpDisplay = creature.HpDisplay;
            _petOwner = creature.PetOwner;
            _slotName = creature.SlotName;
            _side = creature.Side;
            _powers = creature.Powers.ToList();
            _monsterMove = creature.Monster != null ? MonsterMoveSnapshot.Capture(creature.Monster) : null;
        }

        public static CreatureState Capture(Creature creature)
        {
            return new CreatureState(creature);
        }

        public void Restore(Creature creature)
        {
            creature.CombatId = _combatId;
            creature.SlotName = _slotName;
            ReflectionUtil.SetField(creature, "_currentHp", Math.Max(0, _currentHp));
            ReflectionUtil.SetField(creature, "_maxHp", Math.Max(0, _maxHp));
            ReflectionUtil.SetField(creature, "_block", Math.Max(0, _block));
            ReflectionUtil.SetField(
                creature,
                "<MonsterMaxHpBeforeModification>k__BackingField",
                _monsterMaxHpBeforeModification);
            creature.HpDisplay = _hpDisplay;
            ReflectionUtil.SetField(creature, "_petOwner", _petOwner);

            List<PowerModel>? powers = ReflectionUtil.GetField<List<PowerModel>>(creature, "_powers");
            if (powers != null)
            {
                ReflectionUtil.ReplaceList(powers, _powers);
                foreach (PowerModel power in powers)
                {
                    ReflectionUtil.SetField(power, "_owner", creature);
                }
            }

            if (creature.Monster != null)
            {
                _monsterMove?.Restore(creature.Monster);
            }
        }
    }

    private sealed class MonsterMoveSnapshot
    {
        private readonly MonsterMoveStateMachine? _machine;
        private readonly MonsterState? _currentState;
        private readonly bool _performedFirstMove;
        private readonly List<MonsterState> _stateLog;
        private readonly MoveState? _nextMove;
        private readonly bool _spawnedThisTurn;
        private readonly Dictionary<MoveState, bool> _movePerformedFlags = new();

        private MonsterMoveSnapshot(MonsterModel monster)
        {
            _machine = monster.MoveStateMachine;
            _nextMove = monster.NextMove;
            _spawnedThisTurn = monster.SpawnedThisTurn;
            if (_machine == null)
            {
                _stateLog = new List<MonsterState>();
                return;
            }

            _currentState = ReflectionUtil.GetField<MonsterState>(_machine, "_currentState");
            _performedFirstMove = ReflectionUtil.GetField<bool>(_machine, "_performedFirstMove");
            _stateLog = _machine.StateLog.ToList();
            foreach (MoveState move in _machine.States.Values.OfType<MoveState>())
            {
                _movePerformedFlags[move] = ReflectionUtil.GetField<bool>(move, "_performedAtLeastOnce");
            }
        }

        public static MonsterMoveSnapshot Capture(MonsterModel monster)
        {
            return new MonsterMoveSnapshot(monster);
        }

        public void Restore(MonsterModel monster)
        {
            ReflectionUtil.SetField(monster, "_moveStateMachine", _machine);
            ReflectionUtil.SetField(monster, "<NextMove>k__BackingField", _nextMove ?? new MoveState());
            ReflectionUtil.SetField(monster, "_spawnedThisTurn", _spawnedThisTurn);
            if (_machine == null)
            {
                return;
            }

            if (_currentState != null)
            {
                ReflectionUtil.SetField(_machine, "_currentState", _currentState);
            }

            ReflectionUtil.SetField(_machine, "_performedFirstMove", _performedFirstMove);
            ReflectionUtil.ReplaceList(_machine.StateLog, _stateLog);
            foreach ((MoveState move, bool performed) in _movePerformedFlags)
            {
                ReflectionUtil.SetField(move, "_performedAtLeastOnce", performed);
            }
        }
    }

    private sealed class PlayerState
    {
        private readonly Player _player;
        private readonly SerializablePlayerRngSet _rng;
        private readonly List<PotionModel?> _potions;
        private readonly List<RelicModel> _relics;
        private readonly List<OrbModel> _orbs;
        private readonly int _orbCapacity;
        private readonly List<Creature> _pets;
        private readonly Dictionary<PileType, List<CardModel>> _piles;
        private readonly object? _phase;
        private readonly int _gold;
        private readonly bool _isActiveForHooks;
        private readonly int _maxEnergy;
        private readonly int _baseOrbSlotCount;
        private readonly bool _canUseOrRemovePotions;
        private readonly ObjectGraphSnapshot _extraFields;
        private readonly ObjectGraphSnapshot _playerOdds;
        private readonly ObjectGraphSnapshot _relicGrabBag;
        private readonly List<ModelId> _discoveredCards;
        private readonly List<ModelId> _discoveredRelics;
        private readonly List<ModelId> _discoveredPotions;
        private readonly List<ModelId> _discoveredEnemies;
        private readonly List<string> _discoveredEpochs;

        public int Energy { get; }
        public int Stars { get; }
        public int TurnNumber { get; }
        public IReadOnlyList<PotionModel?> Potions => _potions;
        public IReadOnlyList<RelicModel> Relics => _relics;
        public IReadOnlyList<OrbModel> Orbs => _orbs;
        public int OrbCapacity => _orbCapacity;
        public IEnumerable<CardModel> AllPileCards => _piles.Values.SelectMany(cards => cards);
        public IReadOnlyList<CardModel> PlayPile => _piles.TryGetValue(PileType.Play, out List<CardModel>? cards) ? cards : Array.Empty<CardModel>();
        public IEnumerable<string> PileFingerprints => _piles.OrderBy(kvp => kvp.Key).Select(kvp => $"{kvp.Key}:{string.Join(',', kvp.Value.Select(c => c.GetHashCode()))}");

        private PlayerState(Player player)
        {
            _player = player;
            _rng = player.PlayerRng.ToSerializable();
            _potions = player.PotionSlots.ToList();
            _relics = player.Relics.ToList();
            Energy = player.PlayerCombatState?.Energy ?? 0;
            Stars = player.PlayerCombatState?.Stars ?? 0;
            TurnNumber = player.PlayerCombatState != null ? ReflectionUtil.GetField<int>(player.PlayerCombatState, "<TurnNumber>k__BackingField") : 1;
            _phase = player.PlayerCombatState != null ? ReflectionUtil.GetField<object>(player.PlayerCombatState, "_phase") : null;
            _gold = player.Gold;
            _isActiveForHooks = player.IsActiveForHooks;
            _maxEnergy = player.MaxEnergy;
            _baseOrbSlotCount = player.BaseOrbSlotCount;
            _canUseOrRemovePotions = GetCanUseOrRemovePotions(player);
            _extraFields = ObjectGraphSnapshot.Capture(player.ExtraFields);
            _playerOdds = ObjectGraphSnapshot.Capture(player.PlayerOdds);
            _relicGrabBag = ObjectGraphSnapshot.Capture(player.RelicGrabBag);
            _discoveredCards = player.DiscoveredCards.ToList();
            _discoveredRelics = player.DiscoveredRelics.ToList();
            _discoveredPotions = player.DiscoveredPotions.ToList();
            _discoveredEnemies = player.DiscoveredEnemies.ToList();
            _discoveredEpochs = player.DiscoveredEpochs.ToList();
            _orbs = player.PlayerCombatState?.OrbQueue.Orbs.ToList() ?? new List<OrbModel>();
            _orbCapacity = player.PlayerCombatState?.OrbQueue.Capacity ?? 0;
            _pets = player.PlayerCombatState?.Pets.ToList() ?? new List<Creature>();
            _piles = player.Piles.ToDictionary(pile => pile.Type, pile => pile.Cards.ToList());
        }

        public static PlayerState Capture(Player player)
        {
            return new PlayerState(player);
        }

        private static bool GetCanUseOrRemovePotions(Player player)
        {
            foreach (string fieldName in PotionUseOrRemoveFieldNames)
            {
                FieldInfo? field = ReflectionUtil.Field(player.GetType(), fieldName);
                if (field?.FieldType == typeof(bool) && field.GetValue(player) is bool value)
                {
                    return value;
                }
            }

            return true;
        }

        private static void SetCanUseOrRemovePotions(Player player, bool value)
        {
            foreach (string fieldName in PotionUseOrRemoveFieldNames)
            {
                FieldInfo? field = ReflectionUtil.Field(player.GetType(), fieldName);
                if (field?.FieldType != typeof(bool))
                {
                    continue;
                }

                field.SetValue(player, value);
                ReflectionUtil.GetField<Action>(player, "CanUseOrRemovePotionsChanged")?.Invoke();
                return;
            }
        }

        private static readonly string[] PotionUseOrRemoveFieldNames =
        {
            "_canUseOrRemovePotions",
            "<CanUseOrRemovePotions>k__BackingField",
            "_canRemovePotions",
            "<CanRemovePotions>k__BackingField",
        };

        public void Restore(Player player)
        {
            player.PlayerRng.LoadFromSerializable(_rng);
            player.Gold = _gold;
            ReflectionUtil.SetField(player, "<IsActiveForHooks>k__BackingField", _isActiveForHooks);
            player.MaxEnergy = _maxEnergy;
            player.BaseOrbSlotCount = _baseOrbSlotCount;
            SetCanUseOrRemovePotions(player, _canUseOrRemovePotions);
            _extraFields.Restore(player.ExtraFields);
            _playerOdds.Restore(player.PlayerOdds);
            _relicGrabBag.Restore(player.RelicGrabBag);
            ReflectionUtil.ReplaceList(player.DiscoveredCards, _discoveredCards);
            ReflectionUtil.ReplaceList(player.DiscoveredRelics, _discoveredRelics);
            ReflectionUtil.ReplaceList(player.DiscoveredPotions, _discoveredPotions);
            ReflectionUtil.ReplaceList(player.DiscoveredEnemies, _discoveredEnemies);
            ReflectionUtil.ReplaceList(player.DiscoveredEpochs, _discoveredEpochs);

            if (player.PlayerCombatState != null)
            {
                ReflectionUtil.SetField(player.PlayerCombatState, "_energy", Energy);
                ReflectionUtil.SetField(player.PlayerCombatState, "_stars", Stars);
                ReflectionUtil.SetField(player.PlayerCombatState, "<TurnNumber>k__BackingField", TurnNumber);
                if (_phase != null)
                {
                    ReflectionUtil.SetField(player.PlayerCombatState, "_phase", _phase);
                }
                ReflectionUtil.ReplaceList(ReflectionUtil.GetField<List<Creature>>(player.PlayerCombatState, "_pets")!, _pets);
                RestoreOrbs(player.PlayerCombatState);
            }

            RestorePotions(player);
            RestoreRelics(player);
        }

        public void RestorePiles()
        {
            List<CardPile> piles = _player.Piles.ToList();
            foreach (CardPile pile in piles)
            {
                foreach (CardModel card in pile.Cards.ToList())
                {
                    pile.RemoveInternal(card, silent: true);
                }
            }

            foreach (CardPile pile in piles)
            {
                if (!_piles.TryGetValue(pile.Type, out List<CardModel>? cards))
                {
                    continue;
                }

                for (int index = 0; index < cards.Count; index++)
                {
                    CardModel card = cards[index];
                    ReflectionUtil.SetField(card, "_owner", _player);
                    card.HasBeenRemovedFromState = false;
                    pile.AddInternal(card, index, silent: true);
                }

                pile.InvokeContentsChanged();
            }
        }

        private void RestoreOrbs(PlayerCombatState combatState)
        {
            OrbQueue queue = combatState.OrbQueue;
            List<OrbModel>? orbs = ReflectionUtil.GetField<List<OrbModel>>(queue, "_orbs");
            if (orbs != null)
            {
                ReflectionUtil.ReplaceList(orbs, _orbs);
            }

            ReflectionUtil.SetField(queue, "<Capacity>k__BackingField", _orbCapacity);
            foreach (OrbModel orb in _orbs)
            {
                ReflectionUtil.SetField(orb, "_owner", _player);
                ReflectionUtil.SetField(orb, "<HasBeenRemovedFromState>k__BackingField", false);
            }
        }

        private void RestorePotions(Player player)
        {
            List<PotionModel?>? slots = ReflectionUtil.GetField<List<PotionModel?>>(player, "_potionSlots");
            if (slots == null)
            {
                return;
            }

            ReflectionUtil.ReplaceList(slots, _potions);
            foreach (PotionModel? potion in _potions)
            {
                if (potion == null)
                {
                    continue;
                }

                ReflectionUtil.SetField(potion, "_owner", player);
                ReflectionUtil.SetField(potion, "<HasBeenRemovedFromState>k__BackingField", false);
                ReflectionUtil.SetField(potion, "<IsQueued>k__BackingField", false);
            }
        }

        private void RestoreRelics(Player player)
        {
            List<RelicModel>? relics = ReflectionUtil.GetField<List<RelicModel>>(player, "_relics");
            if (relics == null)
            {
                return;
            }

            HashSet<RelicModel> allRelics = relics.Concat(_relics).ToHashSet();
            foreach (RelicModel relic in allRelics)
            {
                RemovePlayerFlashHandlers(player, relic);
            }

            ReflectionUtil.ReplaceList(relics, _relics);
            foreach (RelicModel relic in relics)
            {
                ReflectionUtil.SetField(relic, "_owner", player);
                ReflectionUtil.SetField(relic, "<HasBeenRemovedFromState>k__BackingField", false);
                AddPlayerFlashHandler(player, relic);
            }

            foreach (RelicModel relic in allRelics.Except(_relics))
            {
                ReflectionUtil.SetField(relic, "_owner", null);
                ReflectionUtil.SetField(relic, "<HasBeenRemovedFromState>k__BackingField", true);
            }
        }

        private static void RemovePlayerFlashHandlers(Player player, RelicModel relic)
        {
            FieldInfo? field = ReflectionUtil.Field(relic.GetType(), "Flashed");
            if (field?.GetValue(relic) is not Delegate handlers)
            {
                return;
            }

            Delegate? filtered = null;
            foreach (Delegate handler in handlers.GetInvocationList())
            {
                if (ReferenceEquals(handler.Target, player) &&
                    handler.Method.Name == "OnRelicFlashed")
                {
                    continue;
                }

                filtered = Delegate.Combine(filtered, handler);
            }

            field.SetValue(relic, filtered);
        }

        private static void AddPlayerFlashHandler(Player player, RelicModel relic)
        {
            if (relic.IsMelted || !relic.ShouldFlashOnPlayer)
            {
                return;
            }

            MethodInfo? method = ReflectionUtil.Method(player.GetType(), "OnRelicFlashed");
            EventInfo? flashed = relic.GetType().GetEvent(
                "Flashed",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (method == null || flashed?.EventHandlerType == null)
            {
                return;
            }

            Delegate handler = Delegate.CreateDelegate(flashed.EventHandlerType, player, method);
            flashed.AddEventHandler(relic, handler);
        }
    }
}
