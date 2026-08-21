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
    private static readonly object ActiveVisualLock = new();
    private static readonly Dictionary<GodotObject, int> ActiveVisualTasks =
        new(System.Collections.Generic.ReferenceEqualityComparer.Instance);

    public static async Task TrackAsync(Task original, GodotObject owner)
    {
        lock (ActiveVisualLock)
        {
            ActiveVisualTasks[owner] = ActiveVisualTasks.GetValueOrDefault(owner) + 1;
        }

        try
        {
            await original;
        }
        finally
        {
            lock (ActiveVisualLock)
            {
                if (ActiveVisualTasks.TryGetValue(owner, out int count) && count > 1)
                {
                    ActiveVisualTasks[owner] = count - 1;
                }
                else
                {
                    ActiveVisualTasks.Remove(owner);
                }
            }
        }
    }

    public static int QuarantineForRestore()
    {
        GodotObject[] activeOwners = SnapshotActiveOwners();
        if (activeOwners.Length > 0)
        {
            QuarantineActiveVisuals(activeOwners);
            MainFile.Logger.Info(
                $"Quarantined {activeOwners.Length} old-timeline transient card VFX owner(s) for restore.");
        }

        return activeOwners.Length;
    }

    public static int ActiveVisualCount => SnapshotActiveOwners().Length;

    private static GodotObject[] SnapshotActiveOwners()
    {
        lock (ActiveVisualLock)
        {
            return ActiveVisualTasks.Keys.ToArray();
        }
    }

    private static bool IsActiveVisual(GodotObject owner)
    {
        lock (ActiveVisualLock)
        {
            return ActiveVisualTasks.ContainsKey(owner);
        }
    }

    private static bool ContainsActiveVisual(Node root)
    {
        foreach (GodotObject owner in SnapshotActiveOwners())
        {
            if (owner is not Node activeNode ||
                !GodotObject.IsInstanceValid(activeNode))
            {
                continue;
            }

            if (ReferenceEquals(root, activeNode) || root.IsAncestorOf(activeNode))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsReferencedByActiveVisual(Node candidate)
    {
        foreach (GodotObject owner in SnapshotActiveOwners())
        {
            if (!GodotObject.IsInstanceValid(owner))
            {
                continue;
            }

            // Card fly/shuffle effects own their detached trail through _vfx.
            // The trail is a sibling in the scene tree, not a child, so ancestry
            // checks alone cannot see that its still-running owner will use it.
            if (ReferenceEquals(
                    ReflectionUtil.GetField<Node>(owner, "_vfx"),
                    candidate))
            {
                return true;
            }
        }

        return false;
    }

    private static void QuarantineActiveVisuals(IEnumerable<GodotObject> owners)
    {
        foreach (GodotObject owner in owners)
        {
            if (!GodotObject.IsInstanceValid(owner))
            {
                continue;
            }

            if (owner is CanvasItem canvasItem)
            {
                canvasItem.Visible = false;
            }

            if (TryGetOwnedCard(owner) is { } card &&
                GodotObject.IsInstanceValid(card))
            {
                card.Visible = false;
            }
        }
    }

    public static bool IsCardReferencedByActiveVisual(NCard card)
    {
        foreach (GodotObject owner in SnapshotActiveOwners())
        {
            if (GodotObject.IsInstanceValid(owner) &&
                ReferenceEquals(TryGetOwnedCard(owner), card))
            {
                return true;
            }
        }

        return false;
    }

    private static NCard? TryGetOwnedCard(GodotObject owner)
    {
        if (owner is NCardFlyPowerVfx flyPower)
        {
            return flyPower.CardNode;
        }

        return ReflectionUtil.GetField<NCard>(owner, "_cardNode") ??
               ReflectionUtil.GetField<NCard>(owner, "_card");
    }

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
                if (IsActiveVisual(child))
                {
                    QuarantineActiveVisuals(new[] { child });
                    continue;
                }

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

        if (node is NCard card && IsCardReferencedByActiveVisual(card))
        {
            card.Visible = false;
            return;
        }

        if (IsActiveVisual(node) ||
            ContainsActiveVisual(node) ||
            IsReferencedByActiveVisual(node))
        {
            if (node is CanvasItem canvasItem)
            {
                canvasItem.Visible = false;
            }

            QuarantineActiveVisuals(SnapshotActiveOwners());
            return;
        }

        node.GetParent()?.RemoveChild(node);
        node.QueueFreeSafely();
    }

    private static void RemoveCardImmediately(NCard card)
    {
        if (!GodotObject.IsInstanceValid(card) || card.IsQueuedForDeletion())
        {
            return;
        }

        if (IsCardReferencedByActiveVisual(card))
        {
            card.Visible = false;
            return;
        }

        card.PlayPileTween?.Kill();
        card.PlayPileTween = null;
        card.QueueFreeSafely();
    }
}
