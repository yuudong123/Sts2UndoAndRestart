using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Localization.Fonts;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace UndoAndRestartCode;

/// <summary>
/// Optional on-screen Undo / Redo buttons, for players without a keyboard (touch
/// screens, remote play). On by default. They start beside the energy orb and can
/// be dragged anywhere; a press that moves less than <see cref="DragThreshold"/>
/// still counts as a click. Refreshed with the action history overlay whenever
/// the undo history changes.
/// </summary>
internal static class ScreenButtonsOverlay
{
    private const float ButtonWidth = 88f;
    private const float ButtonHeight = 54f;
    private const int IconSize = 46;
    private const float ArrowSize = 40f;
    private const string SlabTexturePath = "res://images/packed/combat_ui/end_turn_button.png";
    private const string ArrowTexturePath = "res://images/atlases/compressed_atlas.sprites/back_button_arrow.tres";
    /// <summary>Opaque area of the end-turn slab texture and the width of its pointed ends.</summary>
    private static readonly Rect2 SlabRegion = new(46f, 43f, 420f, 170f);
    private const int SlabPointMargin = 72;
    private const float Gap = 6f;
    private const float Margin = 4f;
    private const float DragThreshold = 12f;

    private static CanvasLayer? _layer;
    private static Button? _undoButton;
    private static Button? _redoButton;
    private static bool _loggedLayout;
    private static bool _watchingFrames;

    private static readonly Dictionary<Button, VanillaSkin> Skins = new();

    private sealed class VanillaSkin(Control visuals)
    {
        public Control Visuals { get; } = visuals;
        public bool Hovered;
        public bool Down;
    }

    private sealed class DragState
    {
        public bool Tracking;
        public bool Dragging;
        public bool SuppressPress;
        public Vector2 PointerStart;
        public Vector2 ButtonStart;
    }

    public static void Refresh()
    {
        TopBarRestartButton.Refresh();
        try
        {
            if (!ShouldShow())
            {
                if (_layer != null && GodotObject.IsInstanceValid(_layer))
                {
                    _layer.Visible = false;
                }

                return;
            }

            EnsureCreated();
            if (_layer == null || _undoButton == null || _redoButton == null)
            {
                return;
            }

            _layer.Visible = true;
            _undoButton.TooltipText = UndoText.InputUndo;
            _redoButton.TooltipText = UndoText.InputRedo;
            int cursor = UndoRedoManager.CurrentSnapshotIndex;
            _undoButton.Disabled = cursor <= 0;
            _redoButton.Disabled = cursor >= UndoRedoManager.SnapshotCount - 1;
            UpdateSkin(_undoButton);
            UpdateSkin(_redoButton);
            UpdateLayout();
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"Failed to refresh on-screen undo buttons: {ex.Message}");
        }
    }

    private static bool ShouldShow()
    {
        try
        {
            return UndoAndRestartConfig.EnableScreenButtons &&
                   NGame.Instance != null &&
                   !CombatManager.Instance.IsOverOrEnding &&
                   CombatManager.Instance.DebugOnlyGetState() != null &&
                   RunManager.Instance.IsSingleplayerOrFakeMultiplayer;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// The undo history only refreshes the buttons when it changes, so the end of
    /// combat (last enemy down, death, escape) is caught here, on the frame it happens.
    /// </summary>
    private static void HideWhenCombatEnds()
    {
        if (_layer != null && GodotObject.IsInstanceValid(_layer) && _layer.Visible && !ShouldShow())
        {
            _layer.Visible = false;
        }
    }

    private static void EnsureCreated()
    {
        if (_layer != null && GodotObject.IsInstanceValid(_layer))
        {
            return;
        }

        Skins.Clear();
        _layer = new CanvasLayer
        {
            Name = "UndoAndRestartScreenButtons",
            Layer = 79,
        };
        _undoButton = CreateButton("UndoAndRestartScreenUndo", "undo_icon.png", UndoText.InputUndo, () => UndoRedoManager.HandleUndoKey(), isUndo: true);
        _redoButton = CreateButton("UndoAndRestartScreenRedo", "redo_icon.png", UndoText.InputRedo, () => UndoRedoManager.HandleRedoKey(), isUndo: false);
        _layer.AddChild(_undoButton);
        _layer.AddChild(_redoButton);
        NGame.Instance!.AddChild(_layer);
        if (!_watchingFrames)
        {
            _watchingFrames = true;
            NGame.Instance.GetTree().ProcessFrame += HideWhenCombatEnds;
        }
    }

    /// <summary>
    /// Saved positions are fractions of the viewport, so they survive resolution
    /// and window size changes. Without one, Undo sits left of the energy orb and
    /// Redo right of it.
    /// </summary>
    private static void UpdateLayout()
    {
        if (_undoButton == null || _redoButton == null || NGame.Instance == null)
        {
            return;
        }

        Vector2 viewport = Viewport();
        Rect2 orb = EnergyOrbRect() ?? new Rect2(100f, 806f, 128f, 128f);
        float y = orb.GetCenter().Y - ButtonHeight / 2f;
        Vector2 undoDefault = new(orb.Position.X - ButtonWidth - Gap, y);
        Vector2 redoDefault = new(orb.End.X + Gap, y);
        _undoButton.Size = new Vector2(ButtonWidth, ButtonHeight);
        _redoButton.Size = new Vector2(ButtonWidth, ButtonHeight);
        _undoButton.Position = Clamp(UndoAndRestartConfig.ScreenUndoButtonPosition is { } undo ? undo * viewport : undoDefault, viewport);
        _redoButton.Position = Clamp(UndoAndRestartConfig.ScreenRedoButtonPosition is { } redo ? redo * viewport : redoDefault, viewport);
        if (!_loggedLayout)
        {
            _loggedLayout = true;
            MainFile.Logger.Info($"On-screen undo buttons: viewport={viewport} orb={orb} undo={_undoButton.Position} redo={_redoButton.Position}");
        }
    }

    private static Vector2 Viewport() => NGame.Instance?.GetViewportRect().Size ?? new Vector2(1920f, 1080f);

    private static Vector2 Clamp(Vector2 position, Vector2 viewport)
    {
        return new Vector2(
            Mathf.Clamp(position.X, Margin, Math.Max(Margin, viewport.X - ButtonWidth - Margin)),
            Mathf.Clamp(position.Y, Margin, Math.Max(Margin, viewport.Y - ButtonHeight - Margin)));
    }

    private static Rect2? EnergyOrbRect()
    {
        Control? container = NCombatRoom.Instance?.Ui?.EnergyCounterContainer;
        if (container == null || !GodotObject.IsInstanceValid(container) || !container.IsVisibleInTree())
        {
            return null;
        }

        // The counter slides in from off-screen at combat start (NEnergyCounter.AnimIn
        // tweens its position to zero), so use where it ends up, not where it is now.
        NEnergyCounter? counter = container.GetChildren().OfType<NEnergyCounter>().FirstOrDefault();
        Vector2 size = counter != null ? counter.Size * counter.Scale : container.Size;
        Rect2 rect = new(container.GlobalPosition, size * container.GetGlobalTransform().Scale);
        return rect.Size.X > 1f && rect.Size.Y > 1f ? rect : null;
    }

    private static Button CreateButton(string name, string iconFile, string fallbackText, Action onPressed, bool isUndo)
    {
        Button button = new()
        {
            Name = name,
            CustomMinimumSize = new Vector2(ButtonWidth, ButtonHeight),
            FocusMode = Control.FocusModeEnum.None,
            MouseFilter = Control.MouseFilterEnum.Stop,
            IconAlignment = HorizontalAlignment.Center,
            VerticalIconAlignment = VerticalAlignment.Center,
        };
        if (!TryApplyVanillaSkin(button, mirrorIcon: !isUndo))
        {
            ApplyFallbackSkin(button, iconFile, fallbackText);
        }

        DragState drag = new();
        // gui_input is emitted before the button's own handling, so a drag can
        // cancel the press that the release would otherwise trigger.
        button.GuiInput += inputEvent => OnButtonInput(button, drag, inputEvent, isUndo);
        button.Pressed += () =>
        {
            if (drag.SuppressPress)
            {
                drag.SuppressPress = false;
                return;
            }

            onPressed();
            NGame.Instance?.GetViewport()?.SetInputAsHandled();
        };
        return button;
    }

    private static void OnButtonInput(Button button, DragState drag, InputEvent inputEvent, bool isUndo)
    {
        if (inputEvent is InputEventMouseButton { ButtonIndex: MouseButton.Left } mouseButton)
        {
            if (mouseButton.Pressed)
            {
                drag.Tracking = true;
                drag.Dragging = false;
                drag.SuppressPress = false;
                drag.PointerStart = mouseButton.GlobalPosition;
                drag.ButtonStart = button.Position;
            }
            else if (drag.Tracking)
            {
                drag.Tracking = false;
                if (drag.Dragging)
                {
                    drag.Dragging = false;
                    drag.SuppressPress = true;
                    SavePosition(button, isUndo);
                }
            }

            return;
        }

        if (inputEvent is InputEventMouseMotion motion && drag.Tracking)
        {
            Vector2 delta = motion.GlobalPosition - drag.PointerStart;
            if (!drag.Dragging && delta.Length() < DragThreshold)
            {
                return;
            }

            drag.Dragging = true;
            button.Position = Clamp(drag.ButtonStart + delta, Viewport());
            button.AcceptEvent();
        }
    }

    private static void SavePosition(Button button, bool isUndo)
    {
        Vector2 fraction = button.Position / Viewport();
        bool saved = isUndo
            ? UndoAndRestartConfig.SaveScreenButtonPositions(fraction, UndoAndRestartConfig.ScreenRedoButtonPosition)
            : UndoAndRestartConfig.SaveScreenButtonPositions(UndoAndRestartConfig.ScreenUndoButtonPosition, fraction);
        if (!saved)
        {
            MainFile.Logger.Warn("Failed to save the on-screen button position.");
        }
    }

    /// <summary>
    /// The game's own end-turn stone slab, shrunk with a nine-patch so the pointed
    /// ends keep their shape, and the back button's cream arrow (mirrored for
    /// Redo). Both are loaded from the game at runtime, nothing is redistributed.
    /// </summary>
    private static bool TryApplyVanillaSkin(Button button, bool mirrorIcon)
    {
        try
        {
            NinePatchRect? background = CreateSlab(new Vector2(ButtonWidth, ButtonHeight));
            Texture2D? arrow = ResourceLoader.Load<Texture2D>(ArrowTexturePath);
            if (background == null || arrow == null)
            {
                MainFile.Logger.Warn("Vanilla button textures not found; using the plain button style.");
                return false;
            }

            StyleBoxEmpty empty = new();
            foreach (string style in new[] { "normal", "hover", "pressed", "disabled", "focus" })
            {
                button.AddThemeStyleboxOverride(style, empty);
            }

            Control visuals = new()
            {
                Name = "Visuals",
                MouseFilter = Control.MouseFilterEnum.Ignore,
                Size = new Vector2(ButtonWidth, ButtonHeight),
                PivotOffset = new Vector2(ButtonWidth, ButtonHeight) / 2f,
            };
            TextureRect icon = new()
            {
                Name = "Icon",
                Texture = arrow,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                FlipH = mirrorIcon,
                MouseFilter = Control.MouseFilterEnum.Ignore,
                Size = new Vector2(ArrowSize, ArrowSize),
                Position = new Vector2((ButtonWidth - ArrowSize) / 2f, (ButtonHeight - ArrowSize) / 2f),
            };
            visuals.AddChild(background);
            visuals.AddChild(icon);
            button.AddChild(visuals);

            VanillaSkin skin = new(visuals);
            Skins[button] = skin;
            button.MouseEntered += () => { skin.Hovered = true; UpdateSkin(button); };
            button.MouseExited += () => { skin.Hovered = false; skin.Down = false; UpdateSkin(button); };
            button.ButtonDown += () => { skin.Down = true; UpdateSkin(button); };
            button.ButtonUp += () => { skin.Down = false; UpdateSkin(button); };
            UpdateSkin(button);
            return true;
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"Could not build the vanilla button style: {ex.Message}");
            return false;
        }
    }

    /// <summary>The end-turn slab as a nine-patch of the given on-screen size, or null if the game lacks it.</summary>
    internal static NinePatchRect? CreateSlab(Vector2 size)
    {
        Texture2D? slab = ResourceLoader.Load<Texture2D>(SlabTexturePath);
        if (slab == null)
        {
            return null;
        }

        NinePatchRect background = new()
        {
            Name = "Background",
            Texture = slab,
            RegionRect = SlabRegion,
            PatchMarginLeft = SlabPointMargin,
            PatchMarginRight = SlabPointMargin,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        ResizeSlab(background, size);
        return background;
    }

    internal static void ResizeSlab(NinePatchRect background, Vector2 size)
    {
        float scale = size.Y / SlabRegion.Size.Y;
        background.Size = new Vector2(size.X / scale, SlabRegion.Size.Y);
        background.Scale = new Vector2(scale, scale);
    }

    /// <summary>On-screen width of one pointed end of the slab at the given height.</summary>
    internal static float SlabEndWidth(float height) => SlabPointMargin * height / SlabRegion.Size.Y;

    /// <summary>Hover brightens like the end-turn button, a press sinks it, disabled greys it out.</summary>
    private static void UpdateSkin(Button button)
    {
        if (!Skins.TryGetValue(button, out VanillaSkin? skin) || !GodotObject.IsInstanceValid(skin.Visuals))
        {
            return;
        }

        skin.Visuals.Modulate = button.Disabled
            ? new Color(0.5f, 0.5f, 0.5f, 0.85f)
            : skin.Hovered ? new Color(1.3f, 1.3f, 1.3f) : Colors.White;
        skin.Visuals.Scale = !button.Disabled && skin.Down ? new Vector2(0.94f, 0.94f) : Vector2.One;
    }

    private static void ApplyFallbackSkin(Button button, string iconFile, string fallbackText)
    {
        Texture2D? icon = LoadIcon(iconFile);
        if (icon != null)
        {
            button.Icon = icon;
            button.AddThemeConstantOverride("icon_max_width", IconSize);
            button.AddThemeColorOverride("icon_disabled_color", new Color(1f, 1f, 1f, 0.35f));
        }
        else
        {
            button.Text = fallbackText;
            ApplyGameFont(button);
        }

        button.AddThemeFontSizeOverride("font_size", 16);
        button.AddThemeColorOverride("font_color", new Color(0.92f, 0.9f, 0.84f));
        button.AddThemeColorOverride("font_disabled_color", new Color(0.5f, 0.5f, 0.52f));
        button.AddThemeStyleboxOverride("normal", CreateStyle(new Color(0.07f, 0.085f, 0.105f, 0.88f), new Color(0.55f, 0.5f, 0.38f, 1f)));
        button.AddThemeStyleboxOverride("hover", CreateStyle(new Color(0.12f, 0.14f, 0.17f, 0.94f), new Color(0.92f, 0.75f, 0.34f, 1f)));
        button.AddThemeStyleboxOverride("pressed", CreateStyle(new Color(0.18f, 0.14f, 0.07f, 0.96f), new Color(0.92f, 0.75f, 0.34f, 1f)));
        button.AddThemeStyleboxOverride("disabled", CreateStyle(new Color(0.05f, 0.06f, 0.07f, 0.6f), new Color(0.3f, 0.32f, 0.36f, 0.7f)));
        button.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
    }

    /// <summary>Icons are embedded in the DLL, so the release zip layout stays the same.</summary>
    internal static Texture2D? LoadIcon(string file)
    {
        try
        {
            using Stream? stream = typeof(ScreenButtonsOverlay).Assembly.GetManifestResourceStream("UndoAndRestart." + file);
            if (stream == null)
            {
                MainFile.Logger.Warn($"Missing embedded icon {file}; using text.");
                return null;
            }

            using MemoryStream buffer = new();
            stream.CopyTo(buffer);
            Image image = new();
            if (image.LoadPngFromBuffer(buffer.ToArray()) != Error.Ok)
            {
                MainFile.Logger.Warn($"Could not decode icon {file}; using text.");
                return null;
            }

            return ImageTexture.CreateFromImage(image);
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"Could not load icon {file}: {ex.Message}");
            return null;
        }
    }

    internal static StyleBoxFlat CreateStyle(Color background, Color border)
    {
        return new StyleBoxFlat
        {
            BgColor = background,
            BorderColor = border,
            BorderWidthLeft = 2,
            BorderWidthTop = 2,
            BorderWidthRight = 2,
            BorderWidthBottom = 2,
            CornerRadiusTopLeft = 14,
            CornerRadiusTopRight = 14,
            CornerRadiusBottomLeft = 14,
            CornerRadiusBottomRight = 14,
        };
    }

    internal static void ApplyGameFont(Control control)
    {
        try
        {
            control.RemoveThemeFontOverride("font");
            control.ApplyLocaleFontSubstitution(FontType.Bold, "font");
        }
        catch
        {
            // The buttons still work with the default font.
        }
    }
}
