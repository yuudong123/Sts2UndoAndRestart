using Godot;
using MegaCrit.Sts2.Core.Bindings.MegaSpine;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;

namespace UndoAndRestartCode;

internal sealed class CombatVisualSnapshot
{
    private readonly Dictionary<uint, CreatureVisualState> _creatures = new();

    public static CombatVisualSnapshot Capture(IEnumerable<Creature> creatures)
    {
        CombatVisualSnapshot snapshot = new();
        foreach (Creature creature in creatures)
        {
            if (creature.CombatId is uint id)
            {
                snapshot._creatures[id] = CreatureVisualState.Capture(creature);
            }
        }

        return snapshot;
    }

    public void Restore(IEnumerable<Creature> creatures)
    {
        foreach (Creature creature in creatures)
        {
            if (creature.CombatId is uint id &&
                _creatures.TryGetValue(id, out CreatureVisualState? state))
            {
                state.Restore(creature);
            }
        }
    }

    private sealed class CreatureVisualState
    {
        private readonly Vector2? _position;
        private readonly TransformState? _body;
        private readonly float? _tempScale;
        private readonly List<AnimationTrackState> _tracks = new();

        private CreatureVisualState(NCreature? node)
        {
            if (node == null || !GodotObject.IsInstanceValid(node))
            {
                return;
            }

            _position = node.Position;
            _body = TransformState.Capture(node.Body);
            _tempScale = ReflectionUtil.GetField<float>(node, "_tempScale");

            if (!node.HasSpineAnimation)
            {
                return;
            }

            for (int trackId = 0; trackId < 8; trackId++)
            {
                try
                {
                    MegaTrackEntry? track = node.SpineAnimation.GetCurrentTrack(trackId);
                    if (track != null)
                    {
                        _tracks.Add(new AnimationTrackState(
                            trackId,
                            track.GetAnimationName(),
                            track.GetTrackTime()));
                    }
                }
                catch
                {
                    // 비어 있는 Spine 트랙은 일부 네이티브 바인딩에서 예외가 날 수 있음.
                }
            }
        }

        public static CreatureVisualState Capture(Creature creature)
        {
            return new CreatureVisualState(NCombatRoom.Instance?.GetCreatureNode(creature));
        }

        public void Restore(Creature creature)
        {
            NCreature? node = NCombatRoom.Instance?.GetCreatureNode(creature);
            if (node == null || !GodotObject.IsInstanceValid(node))
            {
                return;
            }

            KillTransientTweens(node);
            if (_position.HasValue)
            {
                node.Position = _position.Value;
            }

            _body?.Restore(node.Body);
            if (_tempScale.HasValue)
            {
                ReflectionUtil.SetField(node, "_tempScale", _tempScale.Value);
            }

            NormalizeModelDrivenUi(node, creature);
            RestoreTrackTimesOnly(node, creature);
        }

        private void RestoreTrackTimesOnly(NCreature node, Creature creature)
        {
            foreach (AnimationTrackState trackState in _tracks)
            {
                try
                {
                    MegaTrackEntry? track = node.SpineAnimation.GetCurrentTrack(trackState.TrackId);
                    if (track == null || track.GetAnimationName() != trackState.Name)
                    {
                        continue;
                    }

                    float end = Math.Max(0f, track.GetAnimationEnd());
                    float time = end > 0f ? Math.Clamp(trackState.Time, 0f, end) : trackState.Time;
                    track.SetTrackTime(time);
                }
                catch (Exception ex)
                {
                    MainFile.Logger.Warn(
                        $"Failed to restore animation time {trackState.TrackId} for {creature.LogName}: {ex.Message}");
                }
            }
        }

        private static void KillTransientTweens(NCreature node)
        {
            foreach (string fieldName in new[] { "_intentFadeTween", "_shakeTween", "_scaleTween" })
            {
                ReflectionUtil.GetField<Tween>(node, fieldName)?.Kill();
                ReflectionUtil.SetField(node, fieldName, null);
            }

            Node? stateDisplay = ReflectionUtil.GetField<Node>(node, "_stateDisplay");
            if (stateDisplay != null)
            {
                foreach (string fieldName in new[] { "_showHideTween", "_hoverTween" })
                {
                    ReflectionUtil.GetField<Tween>(stateDisplay, fieldName)?.Kill();
                    ReflectionUtil.SetField(stateDisplay, fieldName, null);
                }
            }
        }

        private static void NormalizeModelDrivenUi(NCreature node, Creature creature)
        {
            Node? stateDisplay = ReflectionUtil.GetField<Node>(node, "_stateDisplay");
            if (stateDisplay is CanvasItem canvas && (creature.IsPlayer || creature.IsAlive))
            {
                canvas.Visible = true;
                canvas.Modulate = Opaque(canvas.Modulate);
                canvas.SelfModulate = Opaque(canvas.SelfModulate);
                ReflectionUtil.Method(stateDisplay.GetType(), "RefreshValues")?.Invoke(stateDisplay, null);
            }

            if (creature.IsAlive)
            {
                node.DeathAnimationTask = null;
                ReflectionUtil.Method(node.GetType(), "ImmediatelySetIdle")?.Invoke(node, null);
            }
        }

        private static Color Opaque(Color color)
        {
            return new Color(color.R, color.G, color.B, 1f);
        }
    }

    private sealed record AnimationTrackState(int TrackId, string Name, float Time);

    private sealed class TransformState
    {
        private readonly Vector2 _position;
        private readonly Vector2 _scale;
        private readonly float _rotation;

        private TransformState(Node2D node)
        {
            _position = node.Position;
            _scale = node.Scale;
            _rotation = node.Rotation;
        }

        public static TransformState? Capture(Node? node)
        {
            return node is Node2D node2D && GodotObject.IsInstanceValid(node2D)
                ? new TransformState(node2D)
                : null;
        }

        public void Restore(Node? node)
        {
            if (node is not Node2D node2D || !GodotObject.IsInstanceValid(node2D))
            {
                return;
            }

            node2D.Position = _position;
            node2D.Scale = _scale;
            node2D.Rotation = _rotation;
            node2D.Modulate = Colors.White;
            node2D.SelfModulate = Colors.White;
        }
    }
}
