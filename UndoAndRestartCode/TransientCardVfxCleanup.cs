using Godot;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Vfx;
using MegaCrit.Sts2.Core.Nodes.Vfx.Cards;

namespace UndoAndRestartCode;

internal static class TransientCardVfxCleanup
{
    public static void Clear()
    {
        NCombatRoom? room = NCombatRoom.Instance;
        ClearContainer(room?.Ui?.CardPreviewContainer);
        ClearContainer(room?.Ui?.MessyCardPreviewContainer);
        ClearCardVfx(room?.Ui);
        ClearTransientCardVfxContainer(room?.CombatVfxContainer);
        ClearTransientCardVfxContainer(room?.BackCombatVfxContainer);

        if (NRun.Instance?.GlobalUi != null)
        {
            ClearContainer(NRun.Instance.GlobalUi.CardPreviewContainer);
            ClearContainer(NRun.Instance.GlobalUi.MessyCardPreviewContainer);
            ClearTransientCardVfxContainer(NRun.Instance.GlobalUi.AboveTopBarVfxContainer);
            ClearTransientCardVfxContainer(NRun.Instance.GlobalUi.TopBar?.TrailContainer);
        }
    }

    private static void ClearTransientCardVfxContainer(Node? container)
    {
        if (container == null || !GodotObject.IsInstanceValid(container))
        {
            return;
        }

        List<NCard> transientCards = new();
        CollectCardNodes(container, transientCards);
        ClearCardVfx(container);

        foreach (NCard card in transientCards)
        {
            RemoveCardImmediately(card);
        }
    }

    private static void CollectCardNodes(Node root, ICollection<NCard> cards)
    {
        foreach (Node child in root.GetChildren())
        {
            if (child is NCard card)
            {
                cards.Add(card);
            }

            CollectCardNodes(child, cards);
        }
    }

    private static void ClearContainer(Node? container)
    {
        if (container == null || !GodotObject.IsInstanceValid(container))
        {
            return;
        }

        foreach (Node child in container.GetChildren())
        {
            RemoveImmediately(child);
        }
    }

    private static void ClearCardVfx(Node? root)
    {
        if (root == null || !GodotObject.IsInstanceValid(root))
        {
            return;
        }

        foreach (Node child in root.GetChildren())
        {
            if (child is NCardExhaustQuickVfx quickExhaustVfx)
            {
                QueueQuickExhaustTreeForDeletion(quickExhaustVfx);
                continue;
            }

            if (IsTransientCardVfx(child))
            {
                RemoveImmediately(child);
                continue;
            }

            ClearCardVfx(child);
        }
    }

    private static bool IsTransientCardVfx(Node node)
    {
        if (node is NCardFlyVfx ||
            node is NCardFlyShuffleVfx ||
            node is NCardFlyPowerVfx ||
            node is NCardTrailVfx ||
            node is NCardTransformVfx ||
            node is NCardTransformShineVfx ||
            node is NCardEnchantVfx ||
            node is NCardSmithVfx ||
            node is NCardUpgradeVfx ||
            node is NCardExhaustVfx ||
            node is NCardExhaustQuickVfx ||
            node is NCardRemoveVfx)
        {
            return true;
        }

        return false;
    }

    private static void QueueQuickExhaustTreeForDeletion(
        NCardExhaustQuickVfx quickExhaustVfx)
    {
        // The quick exhaust VFX is a child of the card it owns. Its _ExitTree
        // callback queues that parent card for deletion. Removing the child
        // immediately while traversing the card tree therefore re-enters
        // RemoveChild on an ancestor and can crash Godot. Queue the parent
        // first so the callback observes an already-deleting card.
        NCard? card = ReflectionUtil.GetField<NCard>(quickExhaustVfx, "_cardNode");
        if (card != null &&
            GodotObject.IsInstanceValid(card) &&
            !card.IsQueuedForDeletion())
        {
            card.QueueFree();
        }

        if (GodotObject.IsInstanceValid(quickExhaustVfx) &&
            !quickExhaustVfx.IsQueuedForDeletion())
        {
            quickExhaustVfx.QueueFree();
        }
    }

    private static void RemoveImmediately(Node node)
    {
        if (!GodotObject.IsInstanceValid(node) || node.IsQueuedForDeletion())
        {
            return;
        }

        node.GetParent()?.RemoveChild(node);
        node.QueueFree();
    }

    private static void RemoveCardImmediately(NCard card)
    {
        if (!GodotObject.IsInstanceValid(card) || card.IsQueuedForDeletion())
        {
            return;
        }

        card.PlayPileTween?.Kill();
        card.PlayPileTween = null;
        card.QueueFreeSafely();
    }
}
