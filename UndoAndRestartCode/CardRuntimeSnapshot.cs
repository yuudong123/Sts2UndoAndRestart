using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;

namespace UndoAndRestartCode;

internal sealed class CardRuntimeSnapshot
{
    public static CardRuntimeSnapshot Capture(CardModel card)
    {
        return new CardRuntimeSnapshot();
    }

    public static string BuildFingerprint(CardModel card)
    {
        int localEnergyCost = SafeGetEnergyCost(card, CostModifiers.Local);
        int combatEnergyCost = SafeGetEnergyCost(card, CostModifiers.All);
        int localStarCost = card.CurrentStarCost;
        int combatStarCost = SafeGetStarCost(card);
        return $"{card.GetHashCode()}:{localEnergyCost}:{combatEnergyCost}:{localStarCost}:{combatStarCost}";
    }

    public void Restore(CardModel card)
    {
        // 실제 상태는 ModelFieldSnapshot이 담당함. 여기서는 카드 UI 갱신 알림만 보냄.
        card.InvokeEnergyCostChanged();
        ReflectionUtil.GetField<Action>(card, "StarCostChanged")?.Invoke();
        ReflectionUtil.GetField<Action>(card, "KeywordsChanged")?.Invoke();
    }

    private static int SafeGetEnergyCost(CardModel card, CostModifiers modifiers)
    {
        try
        {
            return card.EnergyCost.GetWithModifiers(modifiers);
        }
        catch
        {
            return card.EnergyCost.GetWithModifiers(CostModifiers.Local);
        }
    }

    private static int SafeGetStarCost(CardModel card)
    {
        try
        {
            return card.GetStarCostWithModifiers();
        }
        catch
        {
            return card.CurrentStarCost;
        }
    }
}
