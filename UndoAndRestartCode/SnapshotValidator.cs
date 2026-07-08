using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

namespace UndoAndRestartCode;

internal static class SnapshotValidator
{
    public static void ValidatePlayableState(CombatState state)
    {
        List<string> errors = new();
        RunState runState = (RunState)state.RunState;
        HashSet<CardModel> runCards =
            (ReflectionUtil.GetField<List<CardModel>>(runState, "_allCards")
                ?? new List<CardModel>()).ToHashSet();
        HashSet<CardModel> combatCards =
            (ReflectionUtil.GetField<List<CardModel>>(state, "_allCards")
                ?? new List<CardModel>()).ToHashSet();

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

        Dictionary<Player, int>? effectDepth =
            ReflectionUtil.GetField<Dictionary<Player, int>>(CombatManager.Instance, "_cardOrPotionEffectDepth");
        if (effectDepth?.Values.Any(depth => depth != 0) == true)
        {
            errors.Add("Card or potion effect depth is non-zero after restore.");
        }

        if (errors.Count == 0)
        {
            return;
        }

        string message = string.Join(" ", errors);
        MainFile.Logger.Error($"Snapshot invariant failure: {message}");
        throw new InvalidOperationException(message);
    }
}
