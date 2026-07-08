using Godot;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Vfx;

namespace UndoAndRedoForkCode;

internal static class TransientCardVfxCleanup
{
    public static void Clear()
    {
        NCombatRoom? room = NCombatRoom.Instance;
        ClearContainer(room?.Ui?.CardPreviewContainer);
        ClearContainer(room?.Ui?.MessyCardPreviewContainer);
        ClearCardVfx(room?.CombatVfxContainer);

        if (NRun.Instance?.GlobalUi != null)
        {
            ClearContainer(NRun.Instance.GlobalUi.CardPreviewContainer);
            ClearContainer(NRun.Instance.GlobalUi.MessyCardPreviewContainer);
            ClearCardVfx(NRun.Instance.GlobalUi.TopBar?.TrailContainer);
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
                RemoveImmediately(child);
                continue;
            }

            ClearCardVfx(child);
        }
    }

    private static bool IsTransientCardVfx(Node node)
    {
        return node is NCardFlyVfx ||
               node is NCardFlyShuffleVfx ||
               node is NCardFlyPowerVfx ||
               node is NCardTrailVfx;
    }

    private static void RemoveImmediately(Node node)
    {
        if (!GodotObject.IsInstanceValid(node))
        {
            return;
        }

        node.GetParent()?.RemoveChild(node);
        node.QueueFree();
    }
}
