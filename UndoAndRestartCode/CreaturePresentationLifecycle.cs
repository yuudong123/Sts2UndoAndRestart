using Godot;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;

namespace UndoAndRestartCode;

internal static class CreaturePresentationLifecycle
{
    /// <summary>
    /// Cancels presentation work that cannot safely be rewound.  In particular,
    /// assigning null to NCreature.DeathAnimationTask does not cancel AnimDie; only
    /// leaving the scene tree cancels NCreature.DeathAnimCancelToken.
    /// </summary>
    public static void PrepareForRestore()
    {
        NCombatRoom? room = NCombatRoom.Instance;
        if (room == null)
        {
            return;
        }

        IEnumerable<NCreature> active =
            ReflectionUtil.GetField<List<NCreature>>(room, "_creatureNodes") ??
            Enumerable.Empty<NCreature>();
        IEnumerable<NCreature> removing =
            ReflectionUtil.GetField<List<NCreature>>(room, "_removingCreatureNodes") ??
            Enumerable.Empty<NCreature>();

        foreach (NCreature node in active.Concat(removing).Distinct().ToList())
        {
            if (GodotObject.IsInstanceValid(node) &&
                (node.DeathAnimationTask != null || node.IsQueuedForDeletion()))
            {
                MainFile.Logger.Info(
                    $"Retiring non-rewindable creature presentation before restore: {node.Entity.LogName}.");
                Retire(room, node);
            }
        }
    }

    public static void Retire(Creature creature)
    {
        NCombatRoom? room = NCombatRoom.Instance;
        NCreature? node = room?.GetCreatureNode(creature);
        if (room == null || node == null || !GodotObject.IsInstanceValid(node))
        {
            return;
        }

        // A node that has left the logical combat should not survive as a hidden,
        // event-subscribed animation owner.  The creature model is the durable state;
        // a later undo/redo creates a fresh node from it.
        Retire(room, node);
    }

    private static void Retire(NCombatRoom room, NCreature node)
    {
        ReflectionUtil.GetField<List<NCreature>>(room, "_creatureNodes")?.Remove(node);
        ReflectionUtil.GetField<List<NCreature>>(room, "_removingCreatureNodes")?.Remove(node);

        if (!GodotObject.IsInstanceValid(node))
        {
            return;
        }

        node.Visible = false;
        node.ProcessMode = Node.ProcessModeEnum.Disabled;
        if (GodotObject.IsInstanceValid(node.Hitbox))
        {
            node.Hitbox.MouseFilter = Control.MouseFilterEnum.Ignore;
        }

        // RemoveChild synchronously invokes _ExitTree, which is the engine-owned
        // cancellation boundary for death animation work and creature subscriptions.
        node.GetParent()?.RemoveChild(node);
        if (!node.IsQueuedForDeletion())
        {
            node.QueueFreeSafely();
        }
    }
}
