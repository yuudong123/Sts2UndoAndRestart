using Godot;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Vfx;

namespace UndoAndRestartCode;

internal static class SovereignBladeVfxSync
{
    public static void Refresh(IEnumerable<Player> players)
    {
        foreach (Player player in players)
        {
            try
            {
                SyncPlayer(player);
            }
            catch (Exception ex)
            {
                MainFile.Logger.Warn($"Failed to refresh Sovereign Blade VFX: {ex.Message}");
            }
        }
    }

    private static void SyncPlayer(Player player)
    {
        NCreature? creatureNode = NCombatRoom.Instance?.GetCreatureNode(player.Creature);
        PlayerCombatState? combatState = player.PlayerCombatState;
        if (creatureNode == null || combatState == null)
        {
            return;
        }

        List<SovereignBlade> desiredBlades = GetActiveBlades(combatState);
        Dictionary<CardModel, SovereignBlade> desiredByOriginal = desiredBlades
            .GroupBy(GetOriginalCard)
            .ToDictionary(group => group.Key, group => group.First());

        HashSet<CardModel> kept = new();
        foreach (NSovereignBladeVfx node in creatureNode.GetChildren().OfType<NSovereignBladeVfx>().ToList())
        {
            CardModel? card = node.Card;
            CardModel? original = card != null ? GetOriginalCard(card) : null;
            if (original == null || !desiredByOriginal.ContainsKey(original) || !kept.Add(original))
            {
                RemoveInstantly(node);
            }
        }

        foreach (SovereignBlade blade in desiredBlades)
        {
            CardModel original = GetOriginalCard(blade);
            if (kept.Contains(original))
            {
                continue;
            }

            NSovereignBladeVfx? node = NSovereignBladeVfx.Create(blade);
            if (node == null)
            {
                continue;
            }

            creatureNode.AddChild(node);
            node.Position = Vector2.Zero;
            node.OrbitProgress = desiredBlades.Count <= 1 ? 0.0 : (double)kept.Count / desiredBlades.Count;
            node.Forge(blade.DynamicVars.Damage.IntValue, showFlames: false);
            kept.Add(original);
        }

        NormalizeOrbitSpacing(creatureNode);
    }

    private static List<SovereignBlade> GetActiveBlades(PlayerCombatState combatState)
    {
        return combatState.AllCards
            .Where(card => !card.IsDupe)
            .OfType<SovereignBlade>()
            .Where(card => card.Pile == null || (card.Pile.IsCombatPile && card.Pile.Type != PileType.Exhaust))
            .ToList();
    }

    private static CardModel GetOriginalCard(CardModel card)
    {
        return card.DupeOf ?? card;
    }

    private static void NormalizeOrbitSpacing(NCreature creatureNode)
    {
        List<NSovereignBladeVfx> activeNodes = creatureNode.GetChildren().OfType<NSovereignBladeVfx>().ToList();
        for (int i = 0; i < activeNodes.Count; i++)
        {
            activeNodes[i].OrbitProgress = activeNodes.Count <= 1 ? 0.0 : (double)i / activeNodes.Count;
        }
    }

    private static void RemoveInstantly(NSovereignBladeVfx node)
    {
        if (!GodotObject.IsInstanceValid(node))
        {
            return;
        }

        ReflectionUtil.GetField<Tween>(node, "_attackTween")?.Kill();
        ReflectionUtil.GetField<Tween>(node, "_scaleTween")?.Kill();
        ReflectionUtil.GetField<Tween>(node, "_sparkDelay")?.Kill();
        ReflectionUtil.GetField<Tween>(node, "_glowTween")?.Kill();

        try
        {
            ReflectionUtil.Method(node.GetType(), "CleanupAttack")?.Invoke(node, null);
            ReflectionUtil.Method(node.GetType(), "CleanupForge")?.Invoke(node, null);
        }
        catch
        {
            // 복원 정리 중 노드가 이미 QueueFree 중일 수 있음.
        }

        node.Visible = false;
        node.GetParent()?.RemoveChild(node);
        node.QueueFree();
    }
}
