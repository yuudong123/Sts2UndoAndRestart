using Godot;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;

namespace UndoAndRedoForkCode;

internal static class CreatureLifecycle
{
    private static readonly Dictionary<Creature, NCreature> ParkedNodes = new();

    public static void Park(Creature creature)
    {
        NCombatRoom? room = NCombatRoom.Instance;
        NCreature? node = room?.GetCreatureNode(creature);
        if (room == null || node == null || !GodotObject.IsInstanceValid(node))
        {
            return;
        }

        ReflectionUtil.GetField<List<NCreature>>(room, "_creatureNodes")?.Remove(node);
        ReflectionUtil.GetField<List<NCreature>>(room, "_removingCreatureNodes")?.Remove(node);
        node.Visible = false;
        node.ProcessMode = Node.ProcessModeEnum.Disabled;
        node.Hitbox.MouseFilter = Control.MouseFilterEnum.Ignore;
        ParkedNodes[creature] = node;
    }

    public static bool Unpark(Creature creature)
    {
        NCombatRoom? room = NCombatRoom.Instance;
        if (room == null ||
            !ParkedNodes.Remove(creature, out NCreature? node) ||
            !GodotObject.IsInstanceValid(node))
        {
            return false;
        }

        List<NCreature>? nodes = ReflectionUtil.GetField<List<NCreature>>(room, "_creatureNodes");
        if (nodes != null && !nodes.Contains(node))
        {
            nodes.Add(node);
        }
        ReflectionUtil.GetField<List<NCreature>>(room, "_removingCreatureNodes")?.Remove(node);

        node.ProcessMode = Node.ProcessModeEnum.Inherit;
        node.Visible = true;
        node.Hitbox.MouseFilter = Control.MouseFilterEnum.Stop;
        return true;
    }

    public static void Clear()
    {
        foreach (NCreature node in ParkedNodes.Values)
        {
            if (!GodotObject.IsInstanceValid(node) || node.IsQueuedForDeletion())
            {
                continue;
            }

            // 전투방이 아직 노드를 소유 중이면 Godot이 방 트리와 함께 정리하게 둠.
            // 전투 정리 중 이미 소유된 Spine 크리처를 QueueFree하면 네이티브 시그널이 중복 해제될 수 있음.
            if (node.GetParent() == null)
            {
                node.QueueFree();
            }
        }

        ParkedNodes.Clear();
    }
}
