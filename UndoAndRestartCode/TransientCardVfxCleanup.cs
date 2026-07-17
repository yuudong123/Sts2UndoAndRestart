using Godot;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Vfx;

namespace UndoAndRestartCode;

internal static class TransientCardVfxCleanup
{
    public static void Clear()
    {
        NCombatRoom? room = NCombatRoom.Instance;
        ClearContainer(room?.Ui?.CardPreviewContainer);
        ClearContainer(room?.Ui?.MessyCardPreviewContainer);
        ClearCardVfx(room?.Ui);
        ClearCardVfx(room?.CombatVfxContainer);

        if (NRun.Instance?.GlobalUi != null)
        {
            ClearContainer(NRun.Instance.GlobalUi.CardPreviewContainer);
            ClearContainer(NRun.Instance.GlobalUi.MessyCardPreviewContainer);
            ClearCardVfx(NRun.Instance.GlobalUi.AboveTopBarVfxContainer);
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
        if (node is NCardFlyVfx ||
            node is NCardFlyShuffleVfx ||
            node is NCardFlyPowerVfx ||
            node is NCardTrailVfx)
        {
            return true;
        }

        // 0.109에서 추가된 타입을 직접 참조하면 이전 게임 버전에서 로드할 수 없음.
        return node.GetType().FullName is
            "MegaCrit.Sts2.Core.Nodes.Vfx.Cards.NCardExhaustVfx" or
            "MegaCrit.Sts2.Core.Nodes.Vfx.Cards.NCardExhaustQuickVfx" or
            "MegaCrit.Sts2.Core.Nodes.Vfx.Cards.NCardRemoveVfx";
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
