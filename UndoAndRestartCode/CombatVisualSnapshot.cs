using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Bindings.MegaSpine;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Vfx.Forms;

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
                try
                {
                    state.Restore(creature);
                }
                catch (Exception ex)
                {
                    // 선택적 시각 상태 복원 실패가 핵심 전투 상태 복원을 중단시키면 안 됨.
                    MainFile.Logger.Warn(
                        $"Failed to restore creature visuals for {creature.LogName}: {ex.Message}");
                }
            }
        }
    }

    private sealed class CreatureVisualState
    {
        private readonly Vector2? _position;
        private readonly float? _tempScale;
        private readonly float? _defaultScale;
        private readonly float? _hue;
        private readonly List<AnimationTrackState> _tracks = new();
        private readonly FormVfxState _formVfx = FormVfxState.Empty;

        private CreatureVisualState(NCreature? node, Creature creature)
        {
            if (node == null || !GodotObject.IsInstanceValid(node))
            {
                return;
            }

            _position = node.Position;
            if (ReflectionUtil.Field(node.GetType(), "_tempScale")?.GetValue(node) is float tempScale)
            {
                _tempScale = tempScale;
            }
            _defaultScale = node.Visuals.DefaultScale;
            if (ReflectionUtil.Field(node.Visuals.GetType(), "_hue")?.GetValue(node.Visuals) is float hue)
            {
                // NCreatureVisuals uses 1 as its untouched sentinel, while
                // NCombatRoom.RandomizeEnemyScalesAndHues writes 0..0.05.
                // Reapplying the sentinel through SetScaleAndHue would create an
                // unintended full hue shift on players and unique enemies.
                if (hue is >= 0f and <= 0.1f)
                {
                    _hue = hue;
                }
            }
            _formVfx = FormVfxState.Capture(node, creature);

            if (!node.HasSpineAnimation)
            {
                return;
            }

            for (int trackId = 0; trackId < 8; trackId++)
            {
                try
                {
                    using MegaTrackEntry? track = node.SpineAnimation.GetCurrentTrack(trackId);
                    if (track == null)
                    {
                        continue;
                    }

                    AnimationTrackState? stableState =
                        CaptureStableAnimationTrack(track, trackId);
                    if (stableState != null)
                    {
                        _tracks.Add(stableState);
                    }
                }
                catch
                {
                    // 비어 있는 Spine 트랙은 일부 네이티브 바인딩에서 예외가 날 수 있음.
                }
            }
        }

        private static AnimationTrackState? CaptureStableAnimationTrack(
            MegaTrackEntry track,
            int trackId)
        {
            if (track.IsLoop())
            {
                return CreateTrackState(
                    trackId,
                    track.GetAnimationName(),
                    AnimationRestoreMode.Loop);
            }

            // Spine transitions often play a one-shot and queue the durable loop
            // behind it (Lagavulin Matriarch eyes_open -> eyes_open_loop, Vantom
            // charge_up -> charged). The one-shot's playback time is transient, but
            // its queued loop is already the semantic destination of this state.
            MegaTrackEntry? queued = GetNextTrack(track);
            MegaTrackEntry? lastQueued = null;
            try
            {
                while (queued != null)
                {
                    lastQueued?.Dispose();
                    lastQueued = queued;
                    queued = null;
                    queued = GetNextTrack(lastQueued);
                }

                if (lastQueued != null && lastQueued.IsLoop())
                {
                    return CreateTrackState(
                        trackId,
                        lastQueued.GetAnimationName(),
                        AnimationRestoreMode.Loop);
                }
            }
            finally
            {
                queued?.Dispose();
                lastQueued?.Dispose();
            }

            return trackId > 0 && track.IsComplete()
                ? CreateTrackState(
                    trackId,
                    track.GetAnimationName(),
                    AnimationRestoreMode.TerminalPose)
                : null;
        }

        private static MegaTrackEntry? GetNextTrack(MegaTrackEntry track)
        {
            return ReflectionUtil.Method(typeof(MegaTrackEntry), "GetNext")
                ?.Invoke(track, null) as MegaTrackEntry;
        }

        private static AnimationTrackState? CreateTrackState(
            int trackId,
            string name,
            AnimationRestoreMode mode)
        {
            return string.IsNullOrWhiteSpace(name)
                ? null
                : new AnimationTrackState(trackId, name, mode);
        }

        public static CreatureVisualState Capture(Creature creature)
        {
            return new CreatureVisualState(
                NCombatRoom.Instance?.GetCreatureNode(creature),
                creature);
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

            RestoreSemanticScaleAndHue(node, creature);
            NormalizeModelDrivenUi(node, creature);
            RestoreStableAnimations(node, creature);
            _formVfx.Restore(node, creature);
        }

        private void RestoreSemanticScaleAndHue(NCreature node, Creature creature)
        {
            if (_defaultScale.HasValue)
            {
                if (_hue.HasValue)
                {
                    node.SetScaleAndHue(_defaultScale.Value, _hue.Value);
                }
                else
                {
                    node.Visuals.DefaultScale = _defaultScale.Value;
                }
            }

            // Osty derives its presentation scale from its restored model size and
            // does not update NCreature._tempScale in OstyScaleToSize.
            if (creature.Monster is Osty)
            {
                node.OstyScaleToSize(creature.MaxHp, 0.0);
                return;
            }

            if (!_tempScale.HasValue)
            {
                return;
            }

            ReflectionUtil.SetField(node, "_tempScale", _tempScale.Value);
            Vector2 scale = Vector2.One * _tempScale.Value * node.Visuals.DefaultScale;
            MethodInfo? applyScale = ReflectionUtil.Method(
                node.GetType(),
                "DoScaleTween",
                typeof(Vector2));
            if (applyScale != null)
            {
                applyScale.Invoke(node, new object[] { scale });
            }
            else
            {
                node.Visuals.Scale = scale;
            }
        }

        private void RestoreStableAnimations(NCreature node, Creature creature)
        {
            foreach (AnimationTrackState trackState in _tracks)
            {
                try
                {
                    bool loop = trackState.Mode == AnimationRestoreMode.Loop;
                    node.SpineAnimation.SetAnimation(
                        trackState.Name,
                        loop,
                        trackState.TrackId);
                    if (loop)
                    {
                        continue;
                    }

                    // A completed overlay can encode a lasting pose (open eyes,
                    // charged layer, etc.).  Restore its semantic endpoint, never the
                    // captured playback time.
                    using MegaTrackEntry? restored =
                        node.SpineAnimation.GetCurrentTrack(trackState.TrackId);
                    if (restored != null)
                    {
                        restored.SetMixDuration(0f);
                        restored.SetTrackTime(Math.Max(0f, restored.GetAnimationEnd()));
                    }
                }
                catch (Exception ex)
                {
                    MainFile.Logger.Warn(
                        $"Failed to restore stable animation {trackState.TrackId} for {creature.LogName}: {ex.Message}");
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

                Node? healthBar = ReflectionUtil.GetField<Node>(stateDisplay, "_healthBar");
                if (healthBar != null)
                {
                    foreach (string fieldName in new[]
                             {
                                 "_blockTween",
                                 "_hpLabelFadeTween",
                                 "_middlegroundTween",
                             })
                    {
                        ReflectionUtil.GetField<Tween>(healthBar, fieldName)?.Kill();
                        ReflectionUtil.SetField(healthBar, fieldName, null);
                    }
                }
            }
        }

        private static void NormalizeModelDrivenUi(NCreature node, Creature creature)
        {
            node.Visuals.Position = Vector2.Zero;
            node.Visuals.Modulate = Opaque(node.Visuals.Modulate);
            node.Visuals.SelfModulate = Opaque(node.Visuals.SelfModulate);

            bool showStateDisplay = creature.IsAlive &&
                                    (!creature.IsMonster ||
                                     creature.Monster?.IsHealthBarVisible != false);
            Node? stateDisplay = ReflectionUtil.GetField<Node>(node, "_stateDisplay");
            if (stateDisplay is CanvasItem canvas)
            {
                canvas.Visible = showStateDisplay;
                canvas.Modulate = Opaque(canvas.Modulate);
                canvas.SelfModulate = Opaque(canvas.SelfModulate);
                if (stateDisplay is Control control &&
                    ReflectionUtil.Field(stateDisplay.GetType(), "_originalPosition")
                        ?.GetValue(stateDisplay) is Vector2 originalPosition)
                {
                    control.Position = originalPosition;
                }

                NormalizeHealthBar(stateDisplay);
                ReflectionUtil.Method(stateDisplay.GetType(), "RefreshValues")?.Invoke(stateDisplay, null);
                FinishHealthBarRefresh(stateDisplay);
            }

            if (creature.IsAlive)
            {
                ReflectionUtil.Method(node.GetType(), "ImmediatelySetIdle")?.Invoke(node, null);
                if (creature.IsEnemy)
                {
                    node.IntentContainer.Modulate = Opaque(node.IntentContainer.Modulate);
                    node.IntentContainer.SelfModulate = Opaque(node.IntentContainer.SelfModulate);
                }
            }
        }

        private static void NormalizeHealthBar(Node stateDisplay)
        {
            Node? healthBar = ReflectionUtil.GetField<Node>(stateDisplay, "_healthBar");
            if (healthBar == null)
            {
                return;
            }

            if (ReflectionUtil.GetField<Control>(healthBar, "_blockContainer") is { } block)
            {
                if (ReflectionUtil.Field(healthBar.GetType(), "_originalBlockPosition")
                        ?.GetValue(healthBar) is Vector2 originalBlockPosition)
                {
                    block.Position = originalBlockPosition;
                }
                block.Modulate = Opaque(block.Modulate);
                block.SelfModulate = Opaque(block.SelfModulate);
            }

            if (ReflectionUtil.GetField<Control>(healthBar, "_hpLabel") is { } hpLabel)
            {
                hpLabel.Modulate = Opaque(hpLabel.Modulate);
                hpLabel.SelfModulate = Opaque(hpLabel.SelfModulate);
            }
        }

        private static void FinishHealthBarRefresh(Node stateDisplay)
        {
            Node? healthBar = ReflectionUtil.GetField<Node>(stateDisplay, "_healthBar");
            Tween? middlegroundTween = healthBar != null
                ? ReflectionUtil.GetField<Tween>(healthBar, "_middlegroundTween")
                : null;
            if (middlegroundTween == null)
            {
                return;
            }

            // RefreshValues intentionally animates old HP toward new HP.  During a
            // restore there is no meaningful "old" presentation, so settle it now.
            if (middlegroundTween.CustomStep(10.0))
            {
                middlegroundTween.Kill();
            }
            ReflectionUtil.SetField(healthBar!, "_middlegroundTween", null);
        }

        private static Color Opaque(Color color)
        {
            return new Color(color.R, color.G, color.B, 1f);
        }
    }

    private sealed record AnimationTrackState(
        int TrackId,
        string Name,
        AnimationRestoreMode Mode);

    private enum AnimationRestoreMode
    {
        Loop,
        TerminalPose,
    }

    private sealed class FormVfxState
    {
        public static readonly FormVfxState Empty = new(null, null, false);

        private readonly Type? _vfxType;
        private readonly PowerModel? _power;
        private readonly bool _isActive;

        private FormVfxState(Type? vfxType, PowerModel? power, bool isActive)
        {
            _vfxType = vfxType;
            _power = power;
            _isActive = isActive;
        }

        public static FormVfxState Capture(NCreature node, Creature creature)
        {
            Control? holder = ReflectionUtil.GetField<Control>(
                node.Visuals,
                "_formVfxHolder");
            NFormVfx? vfx = holder?.GetChildren().OfType<NFormVfx>().FirstOrDefault();
            if (vfx == null || !GodotObject.IsInstanceValid(vfx))
            {
                return Empty;
            }

            PowerModel? power = creature.Powers.FirstOrDefault(
                candidate => ReferenceEquals(
                    ReflectionUtil.GetField<NFormVfx>(candidate, "_vfx"),
                    vfx));
            bool isActive = ReflectionUtil.GetField<bool>(vfx, "_isActive");
            return new FormVfxState(vfx.GetType(), power, isActive);
        }

        public void Restore(NCreature node, Creature creature)
        {
            Control? holder = ReflectionUtil.GetField<Control>(
                node.Visuals,
                "_formVfxHolder");
            if (holder == null || !GodotObject.IsInstanceValid(holder))
            {
                return;
            }

            if (_vfxType == null ||
                _power == null ||
                !creature.Powers.Contains(_power))
            {
                node.Visuals.RemoveFormVfx();
                return;
            }

            NFormVfx? vfx = holder.GetChildren().OfType<NFormVfx>().FirstOrDefault();
            if (vfx == null || !GodotObject.IsInstanceValid(vfx) || vfx.GetType() != _vfxType)
            {
                node.Visuals.RemoveFormVfx();
                MethodInfo? createMethod = _vfxType.GetMethod(
                    "Create",
                    BindingFlags.Public | BindingFlags.Static,
                    binder: null,
                    types: new[] { typeof(Creature) },
                    modifiers: null);
                vfx = createMethod?.Invoke(null, new object[] { creature }) as NFormVfx;
            }

            if (vfx == null || !GodotObject.IsInstanceValid(vfx))
            {
                MainFile.Logger.Warn(
                    $"Failed to restore form VFX {_vfxType.Name} for {creature.LogName}.");
                return;
            }

            ReflectionUtil.SetRequiredField(_power, "_vfx", vfx);
            vfx.SetActive(_isActive);
            ForceVisualState(vfx, _isActive);
        }

        private static void ForceVisualState(NFormVfx vfx, bool isActive)
        {
            object? valueRamp = ReflectionUtil.GetField<object>(vfx, "_valueRamp");
            ReflectionUtil.Method(valueRamp?.GetType() ?? typeof(object), "ForceValue", typeof(float))
                ?.Invoke(valueRamp, new object[] { isActive ? 1f : 0f });

            foreach (string methodName in new[]
                     {
                         "UpdateModulates",
                         "UpdateVfx",
                         "UpdateSnakesContainerModulate",
                     })
            {
                ReflectionUtil.Method(vfx.GetType(), methodName, typeof(float))
                    ?.Invoke(vfx, new object[] { isActive ? 1f : 0f });
            }
        }
    }

}
