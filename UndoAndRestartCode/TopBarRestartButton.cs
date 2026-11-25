using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Runs;

namespace UndoAndRestartCode;

/// <summary>
/// Restart Floor button in the top bar, shown with the on-screen Undo / Redo
/// buttons option: the same end-turn stone slab, with the action written on it.
/// It starts left of the leftmost top-right icon and follows that group; holding
/// it for <see cref="HoldSeconds"/> lets it slide anywhere along the top bar. A
/// click asks for confirmation before restarting.
/// </summary>
internal static class TopBarRestartButton
{
    public const string NodeName = "UndoAndRestartTopBarRestart";
    private const double HoldSeconds = 0.4;
    /// <summary>Moving this far before the hold completes cancels the press.</summary>
    private const float CancelDistance = 16f;
    private const float ButtonHeight = 50f;
    private const float MinWidth = 120f;
    private const float TextPadding = 10f;
    private const float Gap = 8f;
    private const int FontSize = 20;
    private static readonly Color TextColor = new(0.96f, 0.92f, 0.82f);
    private static readonly Color DragTint = new(1.25f, 1.15f, 0.8f);

    private sealed class PressState
    {
        public int PressId;
        public bool Down;
        public bool Dragging;
        public bool Cancelled;
        public bool Hovered;
        public Vector2 PointerStart;
        public float ButtonStartX;
    }

    public static void Attach(NTopBar topBar)
    {
        if (topBar.GetNodeOrNull<Control>(NodeName) != null)
        {
            return;
        }

        Control button = new()
        {
            Name = NodeName,
            MouseFilter = Control.MouseFilterEnum.Stop,
            FocusMode = Control.FocusModeEnum.None,
            TooltipText = UndoText.RestartButtonTooltip,
            Visible = false,
        };
        Control visuals = new() { Name = "Visuals", MouseFilter = Control.MouseFilterEnum.Ignore };
        Control background = (Control?)ScreenButtonsOverlay.CreateSlab(new Vector2(MinWidth, ButtonHeight)) ?? new Panel
        {
            Name = "Background",
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        if (background is Panel panel)
        {
            panel.AddThemeStyleboxOverride("panel", ScreenButtonsOverlay.CreateStyle(new Color(0.07f, 0.085f, 0.105f, 0.88f), new Color(0.55f, 0.5f, 0.38f, 1f)));
        }

        Label label = new()
        {
            Name = "Label",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        ScreenButtonsOverlay.ApplyGameFont(label);
        label.AddThemeFontSizeOverride("font_size", FontSize);
        label.AddThemeColorOverride("font_color", TextColor);
        label.AddThemeColorOverride("font_outline_color", new Color(0.12f, 0.1f, 0.08f));
        label.AddThemeConstantOverride("outline_size", 6);
        visuals.AddChild(background);
        visuals.AddChild(label);
        button.AddChild(visuals);
        topBar.AddChild(button);

        PressState state = new();
        button.GuiInput += inputEvent => OnInput(topBar, button, state, inputEvent);
        button.MouseEntered += () => { state.Hovered = true; UpdateLook(button, state); };
        button.MouseExited += () => { state.Hovered = false; UpdateLook(button, state); };
        topBar.RightContainer.ItemRectChanged += () => Layout(topBar, button);
        topBar.Resized += () => Layout(topBar, button);
        Callable.From(() => Refresh(topBar)).CallDeferred();
    }

    /// <summary>Applies the option, the language and the saved position to the current run's top bar.</summary>
    public static void Refresh()
    {
        try
        {
            NTopBar? topBar = NRun.Instance?.GlobalUi?.TopBar;
            if (topBar != null && GodotObject.IsInstanceValid(topBar))
            {
                Refresh(topBar);
            }
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"Failed to refresh the top bar restart button: {ex.Message}");
        }
    }

    private static void Refresh(NTopBar topBar)
    {
        if (!GodotObject.IsInstanceValid(topBar) || topBar.GetNodeOrNull<Control>(NodeName) is not { } button)
        {
            return;
        }

        button.Visible = UndoAndRestartConfig.EnableScreenButtons &&
                         RunManager.Instance.NetService.Type == NetGameType.Singleplayer;
        button.TooltipText = UndoText.RestartButtonTooltip;
        if (button.GetNodeOrNull<Label>("Visuals/Label") is { } label)
        {
            label.Text = UndoText.InputRestart;
        }

        Layout(topBar, button);
    }

    /// <summary>
    /// Wide enough for the text, centered on the icon row. X is the saved fraction
    /// of the top bar width, or just left of the leftmost top-right icon.
    /// </summary>
    private static void Layout(NTopBar topBar, Control button)
    {
        if (!GodotObject.IsInstanceValid(topBar) || !GodotObject.IsInstanceValid(button) || !button.Visible)
        {
            return;
        }

        Control? leftmost = LeftmostIcon(topBar);
        if (leftmost == null)
        {
            return;
        }

        Label? label = button.GetNodeOrNull<Label>("Visuals/Label");
        float textWidth = label?.GetCombinedMinimumSize().X ?? 0f;
        Vector2 size = new(Math.Max(MinWidth, textWidth + 2f * (ScreenButtonsOverlay.SlabEndWidth(ButtonHeight) + TextPadding)), ButtonHeight);
        button.Size = size;
        Control visuals = button.GetNode<Control>("Visuals");
        visuals.Size = size;
        visuals.PivotOffset = size / 2f;
        switch (visuals.GetNodeOrNull("Background"))
        {
            case NinePatchRect slab:
                ScreenButtonsOverlay.ResizeSlab(slab, size);
                break;
            case Control plain:
                plain.Size = size;
                break;
        }

        if (label != null)
        {
            label.Size = size;
        }

        Transform2D bar = topBar.GetGlobalTransform();
        Vector2 barScale = bar.Scale;
        Vector2 iconLocal = (leftmost.GetGlobalRect().Position - bar.Origin) / barScale;
        Vector2 iconSize = leftmost.GetGlobalRect().Size / barScale;
        float x = UndoAndRestartConfig.ScreenRestartButtonX is { } fraction
            ? fraction * topBar.Size.X
            : iconLocal.X - size.X - Gap;
        button.Position = new Vector2(ClampX(topBar, button, x), iconLocal.Y + (iconSize.Y - size.Y) / 2f);
    }

    /// <summary>
    /// The leftmost clickable icon of the top-right group. The group also holds
    /// non-button areas (the save indicator) that are mostly empty on screen.
    /// </summary>
    internal static Control? LeftmostIcon(NTopBar topBar) =>
        topBar.RightContainer.GetChildren().OfType<Control>()
            .Where(control => control.Visible && control.Size.X > 0f && control is NClickableControl or BaseButton)
            .MinBy(control => control.GlobalPosition.X);

    private static float ClampX(NTopBar topBar, Control button, float x) =>
        Mathf.Clamp(x, 0f, Math.Max(0f, topBar.Size.X - button.Size.X));

    private static void OnInput(NTopBar topBar, Control button, PressState state, InputEvent inputEvent)
    {
        if (inputEvent is InputEventMouseButton { ButtonIndex: MouseButton.Left } mouseButton)
        {
            button.AcceptEvent();
            if (mouseButton.Pressed)
            {
                state.PressId++;
                state.Down = true;
                state.Dragging = false;
                state.Cancelled = false;
                state.PointerStart = mouseButton.GlobalPosition;
                state.ButtonStartX = button.Position.X;
                int pressId = state.PressId;
                button.GetTree().CreateTimer(HoldSeconds).Timeout += () => BeginDragIfHeld(button, state, pressId);
                UpdateLook(button, state);
                return;
            }

            if (!state.Down)
            {
                return;
            }

            state.Down = false;
            if (state.Dragging)
            {
                state.Dragging = false;
                float width = topBar.Size.X;
                if (width > 0f && !UndoAndRestartConfig.SaveScreenRestartButtonX(button.Position.X / width))
                {
                    MainFile.Logger.Warn("Failed to save the top bar restart button position.");
                }
            }
            else if (!state.Cancelled && button.GetGlobalRect().HasPoint(mouseButton.GlobalPosition))
            {
                OnClick();
            }

            UpdateLook(button, state);
            return;
        }

        if (inputEvent is InputEventMouseMotion motion && state.Down)
        {
            if (state.Dragging)
            {
                float scale = topBar.GetGlobalTransform().Scale.X;
                float delta = (motion.GlobalPosition.X - state.PointerStart.X) / (scale == 0f ? 1f : scale);
                button.Position = new Vector2(ClampX(topBar, button, state.ButtonStartX + delta), button.Position.Y);
                button.AcceptEvent();
            }
            else if (motion.GlobalPosition.DistanceTo(state.PointerStart) > CancelDistance)
            {
                state.Cancelled = true;
            }
        }
    }

    private static void BeginDragIfHeld(Control button, PressState state, int pressId)
    {
        if (!GodotObject.IsInstanceValid(button) || pressId != state.PressId || !state.Down || state.Cancelled)
        {
            return;
        }

        state.Dragging = true;
        UpdateLook(button, state);
    }

    /// <summary>Hover brightens and a press sinks, like the Undo / Redo slabs; a held button lifts and warms.</summary>
    private static void UpdateLook(Control button, PressState state)
    {
        if (!GodotObject.IsInstanceValid(button) || button.GetNodeOrNull<Control>("Visuals") is not { } visuals)
        {
            return;
        }

        visuals.Scale = state.Dragging ? new Vector2(1.08f, 1.08f)
            : state.Down && !state.Cancelled ? new Vector2(0.94f, 0.94f)
            : Vector2.One;
        visuals.Modulate = state.Dragging ? DragTint
            : state.Hovered ? new Color(1.3f, 1.3f, 1.3f)
            : Colors.White;
    }

    private static void OnClick()
    {
        if (!FloorRestartService.CanRestart(out _))
        {
            MainFile.Logger.Info("Top bar restart ignored: a restart cannot start right now.");
            return;
        }

        TaskHelper.RunSafely(FloorRestartService.ConfirmAndRestartAsync());
    }
}

[HarmonyPatch(typeof(NTopBar), nameof(NTopBar._Ready))]
internal static class TopBarRestartButtonPatch
{
    private static void Postfix(NTopBar __instance)
    {
        try
        {
            TopBarRestartButton.Attach(__instance);
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"Could not add the top bar restart button: {ex.Message}");
        }
    }
}
