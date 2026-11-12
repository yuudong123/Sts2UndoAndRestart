using Godot;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Combat;
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
            if (GodotObject.IsInstanceValid(owner))
            {
                QuarantineVisual(owner);
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
            RetireCardNode(card);
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
            if (IsTransientCardVfx(child))
            {
                if (IsActiveVisual(child))
                {
                    QuarantineActiveVisuals(new[] { child });
                    continue;
                }

                if (child is NCardExhaustQuickVfx quickExhaustVfx &&
                    !ReflectionUtil.GetField<bool>(quickExhaustVfx, "_isFinishing"))
                {
                    // Before _isFinishing the VFX is a child of its card, and its
                    // _ExitTree returns that card to the pool. Removing either node
                    // here would either re-enter RemoveChild on the card's ancestor
                    // or hand the pool a node that is also being destroyed. Without
                    // a running task nothing will finish it, so only hide it.
                    MainFile.Logger.Warn(
                        "Left an idle quick-exhaust VFX hidden because removing it would free its card twice.");
                    QuarantineVisual(quickExhaustVfx);
                    continue;
                }

                // Once finished, a quick-exhaust VFX owns only itself: vanilla has
                // already returned its card to the pool.
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

    private static void RemoveImmediately(Node node)
    {
        if (!GodotObject.IsInstanceValid(node) || node.IsQueuedForDeletion())
        {
            return;
        }

        if (node is NCard card)
        {
            RetireCardNode(card);
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

    /// <summary>
    /// The single way restore retires a card node it no longer projects.
    /// <para>
    /// NCard is pooled. A node must reach <c>NodePool</c> exactly once and only
    /// through <c>QueueFreeSafely</c>; a second return lets the pool hand one node
    /// to two owners, and destroying a pooled node puts a disposed object back
    /// into circulation (ObjectDisposedException in NCard.OnReturnedFromPool).
    /// </para>
    /// <para>
    /// A node still used by an old-timeline visual task belongs to that task,
    /// which returns it when it finishes. Restore only hides it and, when it sits
    /// where <c>NCard.FindOnTable</c> looks (play container/queue) or has lost its
    /// parent, parks it on the combat UI so vanilla cannot reuse it.
    /// </para>
    /// </summary>
    public static void RetireCardNode(NCard card)
    {
        if (!GodotObject.IsInstanceValid(card) || card.IsQueuedForDeletion())
        {
            return;
        }

        if (IsCardReferencedByActiveVisual(card))
        {
            card.Visible = false;
            ParkOutsideCardLookups(card);
            return;
        }

        // The play-pile tween is bound to the combat room, not to the card, so
        // leaving the tree does not stop it.
        card.PlayPileTween?.Kill();
        card.PlayPileTween = null;
        card.QueueFreeSafely();
    }

    private static void ParkOutsideCardLookups(NCard card)
    {
        NCombatUi? ui = NCombatRoom.Instance?.Ui;
        if (ui == null || !GodotObject.IsInstanceValid(ui))
        {
            return;
        }

        Node? parent = card.GetParent();
        if (parent == null)
        {
            ui.AddChild(card);
        }
        else if (ReferenceEquals(parent, ui.PlayContainer) ||
                 ReferenceEquals(parent, ui.PlayQueue))
        {
            card.Reparent(ui);
        }
    }

    private static void QuarantineVisual(GodotObject owner)
    {
        if (owner is CanvasItem canvasItem && GodotObject.IsInstanceValid(canvasItem))
        {
            canvasItem.Visible = false;
        }

        if (TryGetOwnedCard(owner) is { } card && GodotObject.IsInstanceValid(card))
        {
            card.Visible = false;
        }
    }
}
