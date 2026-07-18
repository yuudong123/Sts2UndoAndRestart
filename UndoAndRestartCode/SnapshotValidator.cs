using System.Collections;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Runs;

namespace UndoAndRestartCode;

internal static class SnapshotValidator
{
    public static void ValidatePlayableState(CombatState state)
    {
        List<string> errors = new();
        HashSet<CardModel> runCards = ReflectionUtil
            .GetRequiredField<List<CardModel>>((RunState)state.RunState, "_allCards")
            .ToHashSet();
        HashSet<CardModel> combatCards =
            ReflectionUtil.GetRequiredField<List<CardModel>>(state, "_allCards").ToHashSet();

        foreach (Player player in state.Players)
        {
            HashSet<CardModel> seenCombatCards = new();
            foreach (CardPile pile in player.Piles)
            {
                foreach (CardModel card in pile.Cards)
                {
                    if (!ReferenceEquals(card.Owner, player))
                    {
                        errors.Add($"{card.Id.Entry} has the wrong owner in {pile.Type}.");
                    }

                    if (pile.IsCombatPile)
                    {
                        if (!combatCards.Contains(card))
                        {
                            errors.Add($"{card.Id.Entry} is in {pile.Type} but absent from CombatState._allCards.");
                        }

                        if (!seenCombatCards.Add(card))
                        {
                            errors.Add($"{card.Id.Entry} exists in multiple combat piles.");
                        }
                    }
                    else if (!runCards.Contains(card))
                    {
                        errors.Add($"{card.Id.Entry} is in the permanent deck but absent from RunState._allCards.");
                    }
                    else
                    {
                        if (combatCards.Contains(card))
                        {
                            errors.Add($"{card.Id.Entry} is in the permanent deck and CombatState._allCards.");
                        }

                        if (card.HasBeenRemovedFromState)
                        {
                            errors.Add($"{card.Id.Entry} is in the permanent deck with its removed-state flag set.");
                        }
                    }
                }
            }
        }

        uint[] creatureIds = state.Creatures
            .Where(creature => creature.CombatId.HasValue)
            .Select(creature => creature.CombatId!.Value)
            .ToArray();
        if (creatureIds.Distinct().Count() != creatureIds.Length)
        {
            errors.Add("Combat creature IDs are not unique.");
        }

        if (!RunManager.Instance.ActionQueueSet.IsEmpty)
        {
            errors.Add("Action queue is not empty after restore.");
        }

        Dictionary<Player, int> effectDepth = ReflectionUtil.GetRequiredField<Dictionary<Player, int>>(
            CombatManager.Instance,
            "_cardOrPotionEffectDepth");
        if (effectDepth.Values.Any(depth => depth != 0))
        {
            errors.Add("Card or potion effect depth is non-zero after restore.");
        }

        ValidatePlayerControlState(state, errors);

        if (errors.Count == 0)
        {
            return;
        }

        string message = string.Join(" ", errors);
        MainFile.Logger.Error($"Snapshot invariant failure: {message}");
        throw new InvalidOperationException(message);
    }

    private static void ValidatePlayerControlState(CombatState state, List<string> errors)
    {
        if (state.CurrentSide != CombatSide.Player)
        {
            return;
        }

        if (CombatManager.Instance.PlayerActionsDisabled)
        {
            errors.Add("Player actions remain disabled after restore.");
        }

        if (state.Players.Any(player => player.PlayerCombatState?.Phase != PlayerTurnPhase.Play))
        {
            errors.Add("A player is not in the Play phase after restore.");
        }

        NPlayerHand? hand = NPlayerHand.Instance;
        if (hand == null)
        {
            errors.Add("The local player hand is missing after restore.");
            return;
        }

        if (hand.InCardPlay)
        {
            errors.Add("A card play node remains active after restore.");
        }

        if (hand.CurrentMode != NPlayerHand.Mode.Play)
        {
            errors.Add($"The hand remains in {hand.CurrentMode} mode after restore.");
        }

        if (NTargetManager.Instance?.IsInSelection == true)
        {
            errors.Add("The target manager remains in selection mode after restore.");
        }

        if (hand.PeekButton.IsPeeking)
        {
            errors.Add("The hand remains in peek mode after restore.");
        }

        if (ReflectionUtil.GetRequiredField<bool>(hand, "_isDisabled"))
        {
            errors.Add("The hand remains disabled after restore.");
        }

        if (ReflectionUtil.GetField<int>(hand, "_draggedHolderIndex") >= 0)
        {
            errors.Add("The hand still has a dragged card index after restore.");
        }

        ICollection? awaitingHolders =
            ReflectionUtil.GetField<ICollection>(hand, "_holdersAwaitingQueue");
        if (awaitingHolders?.Count > 0)
        {
            errors.Add("The hand still has card holders awaiting play after restore.");
        }

        Player? localPlayer = state.Players.FirstOrDefault();
        CardPile? restoredHand = localPlayer?.PlayerCombatState?.Hand;
        if (restoredHand != null)
        {
            List<NHandCardHolder> holders = hand.CardHolderContainer
                .GetChildren()
                .OfType<NHandCardHolder>()
                .ToList();
            if (holders.Count != restoredHand.Cards.Count)
            {
                errors.Add(
                    $"Hand holder count {holders.Count} does not match card count {restoredHand.Cards.Count}.");
            }

            int sharedCount = Math.Min(holders.Count, restoredHand.Cards.Count);
            for (int index = 0; index < sharedCount; index++)
            {
                if (!ReferenceEquals(holders[index].CardNode?.Model, restoredHand.Cards[index]))
                {
                    errors.Add($"Hand holder {index} does not match the restored card order.");
                }
            }
        }

        Node? selectedContainer = ReflectionUtil.GetField<Node>(hand, "_selectedHandCardContainer");
        if (selectedContainer?.GetChildren().OfType<NCardHolder>().Any() == true)
        {
            errors.Add("The selected-card container still contains hand cards after restore.");
        }

        bool canPlayCards =
            (bool)(ReflectionUtil.Method(typeof(NPlayerHand), "CanPlayCards")!
                .Invoke(hand, null) ?? false);
        if (!canPlayCards)
        {
            errors.Add("NPlayerHand.CanPlayCards returned false after restore.");
        }
    }
}
