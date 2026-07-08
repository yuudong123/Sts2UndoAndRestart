using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Localization.Fonts;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;

namespace UndoAndRestartCode;

internal static class ActionHistoryOverlay
{
    private const float PanelWidth = 500f;
    private const float PanelHeight = 620f;
    private const float TileWidth = 78f;
    private const float TileHeight = 116f;
    private const float TileCellWidth = 88f;
    private const float TileCellHeight = 124f;
    private const int GridColumns = 5;

    private static CanvasLayer? _layer;
    private static Button? _tabButton;
    private static PanelContainer? _panel;
    private static ScrollContainer? _scroll;
    private static VBoxContainer? _content;
    private static bool _isOpen;

    public static void Refresh()
    {
        try
        {
            EnsureCreated();
            Render();
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"Failed to refresh undo stack overlay: {ex.Message}");
        }
    }

    private static void EnsureCreated()
    {
        if (NGame.Instance == null)
        {
            return;
        }

        if (_layer != null && GodotObject.IsInstanceValid(_layer))
        {
            UpdateLayout();
            return;
        }

        _layer = new CanvasLayer
        {
            Name = "ActionHistoryOverlay",
            Layer = 80,
        };

        _tabButton = new Button
        {
            Name = "UndoAndRestartActionHistoryTab",
            Text = UndoText.ActionHistory,
            CustomMinimumSize = new Vector2(104f, 34f),
            FocusMode = Control.FocusModeEnum.None,
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        ApplyTabStyle(_tabButton);
        ApplyGameFont(_tabButton, FontType.Bold);
        _tabButton.Pressed += Toggle;

        _panel = new PanelContainer
        {
            Name = "UndoAndRestartActionHistoryPanel",
            CustomMinimumSize = new Vector2(PanelWidth, PanelHeight),
            Visible = _isOpen,
        };
        StyleBoxFlat panelStyle = new()
        {
            BgColor = new Color(0.045f, 0.052f, 0.062f, 0.94f),
            BorderWidthLeft = 0,
            BorderWidthTop = 0,
            BorderWidthRight = 0,
            BorderWidthBottom = 0,
            CornerRadiusTopLeft = 0,
            CornerRadiusTopRight = 0,
            CornerRadiusBottomLeft = 0,
            CornerRadiusBottomRight = 0,
        };
        _panel.AddThemeStyleboxOverride("panel", panelStyle);

        MarginContainer margin = new()
        {
            Name = "UndoAndRestartActionHistoryMargin",
        };
        margin.AddThemeConstantOverride("margin_left", 12);
        margin.AddThemeConstantOverride("margin_top", 10);
        margin.AddThemeConstantOverride("margin_right", 12);
        margin.AddThemeConstantOverride("margin_bottom", 10);

        _scroll = new ScrollContainer
        {
            Name = "UndoAndRestartActionHistoryScroll",
            CustomMinimumSize = new Vector2(PanelWidth - 24f, PanelHeight - 20f),
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto,
        };

        _content = new VBoxContainer
        {
            Name = "UndoAndRestartActionHistoryContent",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        _content.AddThemeConstantOverride("separation", 8);

        _scroll.AddChild(_content);
        margin.AddChild(_scroll);
        _panel.AddChild(margin);
        _layer.AddChild(_tabButton);
        _layer.AddChild(_panel);
        NGame.Instance.AddChild(_layer);
        UpdateLayout();
    }

    private static void Toggle()
    {
        _isOpen = !_isOpen;
        _tabButton?.ReleaseFocus();
        Render();
    }

    private static void UpdateLayout()
    {
        if (_tabButton == null || _panel == null || NGame.Instance == null)
        {
            return;
        }

        Vector2 size = NGame.Instance.GetViewportRect().Size;
        float tabY = Mathf.Clamp(size.Y * 0.16f, 72f, Math.Max(72f, size.Y - 240f));
        _tabButton.Position = new Vector2(Math.Max(16f, size.X - 124f), tabY);
        _panel.Position = new Vector2(Math.Max(16f, size.X - PanelWidth - 24f), Math.Min(tabY + 40f, Math.Max(16f, size.Y - PanelHeight - 24f)));
        _panel.Size = new Vector2(PanelWidth, Math.Min(PanelHeight, Math.Max(260f, size.Y - _panel.Position.Y - 24f)));
    }

    private static void Render()
    {
        if (_content == null || _panel == null || _tabButton == null)
        {
            return;
        }

        UpdateLayout();
        if (!ShouldShowOverlay())
        {
            _panel.Visible = false;
            _tabButton.Visible = false;
            _isOpen = false;
            return;
        }

        _tabButton.Visible = true;
        _panel.Visible = _isOpen;
        _tabButton.Text = _isOpen ? UndoText.Close : UndoText.ActionHistory;

        foreach (Node child in _content.GetChildren().ToList())
        {
            _content.RemoveChild(child);
            child.QueueFree();
        }

        IReadOnlyList<ActionHistoryEntry> entries = UndoRedoManager.GetActionEntries();
        int cursor = UndoRedoManager.CurrentSnapshotIndex;
        _content.AddChild(CreateHeader(entries, cursor));
        _content.AddChild(CreateInitialStateRow(cursor));

        if (entries.Count == 0)
        {
            return;
        }

        int? previousTurn = null;
        GridContainer? currentGrid = null;
        foreach (ActionHistoryEntry entry in entries)
        {
            if (previousTurn != entry.TurnNumber || currentGrid == null)
            {
                previousTurn = entry.TurnNumber;
                _content.AddChild(CreateTurnSeparator(entry.TurnNumber));
                currentGrid = CreateGrid();
                _content.AddChild(currentGrid);
            }

            currentGrid.AddChild(CreateEntryTile(entry, cursor));
        }

        if (entries.Count == 0 || cursor >= entries[^1].SnapshotIndex)
        {
            ScrollToBottomDeferred();
        }
    }

    private static async void ScrollToBottomDeferred()
    {
        try
        {
            if (_scroll == null || !GodotObject.IsInstanceValid(_scroll))
            {
                return;
            }

            if (Engine.GetMainLoop() is SceneTree sceneTree)
            {
                await sceneTree.ToSignal(sceneTree, SceneTree.SignalName.ProcessFrame);
            }

            VScrollBar? bar = _scroll.GetVScrollBar();
            if (bar != null && GodotObject.IsInstanceValid(bar))
            {
                _scroll.ScrollVertical = (int)bar.MaxValue;
            }
        }
        catch (Exception ex)
        {
            MainFile.Logger.Debug($"Failed to scroll history to bottom: {ex.Message}");
        }
    }

    private static bool ShouldShowOverlay()
    {
        try
        {
            return CombatManager.Instance.IsInProgress &&
                   CombatManager.Instance.DebugOnlyGetState() != null &&
                   RunManager.Instance.IsSingleplayerOrFakeMultiplayer &&
                   UndoAndRestartConfig.ShowActionHistoryOverlay;
        }
        catch
        {
            return false;
        }
    }

    private static void ApplyTabStyle(Button button)
    {
        StyleBoxFlat normal = CreateTabStyle(new Color(0.07f, 0.085f, 0.105f, 0.88f));
        StyleBoxFlat hover = CreateTabStyle(new Color(0.12f, 0.14f, 0.17f, 0.94f));
        StyleBoxFlat pressed = CreateTabStyle(new Color(0.18f, 0.14f, 0.07f, 0.96f));
        button.AddThemeStyleboxOverride("normal", normal);
        button.AddThemeStyleboxOverride("hover", hover);
        button.AddThemeStyleboxOverride("pressed", pressed);
        button.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
        button.FocusMode = Control.FocusModeEnum.None;
        button.AddThemeColorOverride("font_color", new Color(1f, 0.9f, 0.62f));
        button.AddThemeColorOverride("font_hover_color", new Color(1f, 0.96f, 0.75f));
        button.AddThemeColorOverride("font_pressed_color", new Color(1f, 0.86f, 0.4f));
        button.AddThemeFontSizeOverride("font_size", 14);
        ApplyGameFont(button, FontType.Bold);
    }

    private static StyleBoxFlat CreateTabStyle(Color color)
    {
        return new StyleBoxFlat
        {
            BgColor = color,
            BorderWidthLeft = 0,
            BorderWidthTop = 0,
            BorderWidthRight = 0,
            BorderWidthBottom = 0,
            CornerRadiusTopLeft = 0,
            CornerRadiusTopRight = 0,
            CornerRadiusBottomLeft = 0,
            CornerRadiusBottomRight = 0,
            ContentMarginLeft = 10,
            ContentMarginRight = 10,
            ContentMarginTop = 5,
            ContentMarginBottom = 5,
        };
    }

    private static Control CreateHeader(IReadOnlyList<ActionHistoryEntry> entries, int cursor)
    {
        int currentAction = entries.Count(entry => entry.SnapshotIndex <= cursor);
        int totalActions = entries.Count;
        VBoxContainer box = new()
        {
            CustomMinimumSize = new Vector2(PanelWidth - 24f, 54f),
        };

        Label title = new()
        {
            Text = UndoText.ActionHistory,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        title.AddThemeFontSizeOverride("font_size", 20);
        title.AddThemeColorOverride("font_color", new Color(1f, 0.91f, 0.66f));
        ApplyGameFont(title, FontType.Bold);

        Label subtitle = new()
        {
            Text = $"{currentAction}/{totalActions}",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        subtitle.AddThemeFontSizeOverride("font_size", 12);
        subtitle.AddThemeColorOverride("font_color", new Color(0.72f, 0.74f, 0.78f));
        ApplyGameFont(subtitle, FontType.Regular);

        box.AddChild(title);
        box.AddChild(subtitle);
        return box;
    }

    private static Control CreateInitialStateRow(int cursor)
    {
        bool current = cursor == 0;
        HBoxContainer row = new()
        {
            CustomMinimumSize = new Vector2(PanelWidth - 36f, 44f),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        row.AddThemeConstantOverride("separation", 8);

        row.AddChild(CreateHistoryActionPanel(
            UndoText.RestartCombat,
            UndoText.RestartCombatTooltip,
            OnQuickRestartInput,
            current: false));
        row.AddChild(CreateHistoryActionPanel(
            UndoText.InitialState(current),
            UndoText.InitialStateTooltip,
            inputEvent => OnTileInput(inputEvent, 0),
            current));
        return row;
    }

    private static Control CreateHistoryActionPanel(string text, string tooltip, Action<InputEvent> onInput, bool current)
    {
        PanelContainer panel = new()
        {
            CustomMinimumSize = new Vector2((PanelWidth - 44f) * 0.5f, 44f),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            MouseFilter = Control.MouseFilterEnum.Stop,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
            PivotOffset = new Vector2((PanelWidth - 44f) * 0.25f, 22f),
            TooltipText = tooltip,
        };
        panel.GuiInput += inputEvent => onInput(inputEvent);
        panel.MouseEntered += () => SetInitialRowHover(panel, true);
        panel.MouseExited += () => SetInitialRowHover(panel, false);

        StyleBoxFlat style = new()
        {
            BgColor = current ? new Color(0.2f, 0.17f, 0.08f, 0.95f) : new Color(0.07f, 0.078f, 0.09f, 0.88f),
            BorderColor = current ? new Color(1f, 0.86f, 0.33f) : new Color(0.32f, 0.34f, 0.38f, 0.9f),
            BorderWidthLeft = 1,
            BorderWidthTop = 1,
            BorderWidthRight = 1,
            BorderWidthBottom = 1,
            CornerRadiusTopLeft = 0,
            CornerRadiusTopRight = 0,
            CornerRadiusBottomLeft = 0,
            CornerRadiusBottomRight = 0,
        };
        panel.AddThemeStyleboxOverride("panel", style);

        Label label = new()
        {
            Text = text,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            CustomMinimumSize = new Vector2((PanelWidth - 44f) * 0.5f, 44f),
            ClipText = true,
        };
        label.AddThemeFontSizeOverride("font_size", 12);
        label.AddThemeColorOverride("font_color", current ? new Color(1f, 0.9f, 0.45f) : new Color(0.78f, 0.8f, 0.84f));
        ApplyGameFont(label, FontType.Bold);
        panel.AddChild(label);
        return panel;
    }

    private static Control CreateTurnSeparator(int turnNumber)
    {
        HBoxContainer row = new()
        {
            CustomMinimumSize = new Vector2(PanelWidth - 24f, 24f),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };

        HSeparator left = new()
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        Label label = new()
        {
            Text = UndoText.Turn(turnNumber),
            CustomMinimumSize = new Vector2(72f, 22f),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        label.AddThemeFontSizeOverride("font_size", 13);
        label.AddThemeColorOverride("font_color", new Color(0.93f, 0.82f, 0.5f));
        ApplyGameFont(label, FontType.Bold);
        HSeparator right = new()
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };

        row.AddChild(left);
        row.AddChild(label);
        row.AddChild(right);
        return row;
    }

    private static GridContainer CreateGrid()
    {
        GridContainer grid = new()
        {
            Columns = GridColumns,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        grid.AddThemeConstantOverride("h_separation", 14);
        grid.AddThemeConstantOverride("v_separation", 8);
        return grid;
    }

    private static Control CreateEntryTile(ActionHistoryEntry entry, int cursor)
    {
        bool applied = entry.SnapshotIndex <= cursor;
        bool current = entry.SnapshotIndex == cursor;

        Control wrapper = new()
        {
            CustomMinimumSize = new Vector2(TileCellWidth, TileCellHeight),
            ClipContents = false,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };

        PanelContainer panel = new()
        {
            CustomMinimumSize = new Vector2(TileWidth, TileHeight),
            Size = new Vector2(TileWidth, TileHeight),
            Position = new Vector2((TileCellWidth - TileWidth) * 0.5f, (TileCellHeight - TileHeight) * 0.5f),
            Modulate = applied ? Colors.White : new Color(1f, 1f, 1f, 0.38f),
            MouseFilter = Control.MouseFilterEnum.Stop,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
            PivotOffset = new Vector2(TileWidth * 0.5f, TileHeight * 0.5f),
            TooltipText = $"{entry.Title}\n{entry.Detail}\n{UndoText.Snapshot(entry.SnapshotIndex)}",
        };
        panel.GuiInput += inputEvent => OnTileInput(inputEvent, entry.SnapshotIndex);
        panel.MouseEntered += () => SetTileHover(panel, true);
        panel.MouseExited += () => SetTileHover(panel, false);

        StyleBoxFlat tileStyle = new()
        {
            BgColor = current ? new Color(0.24f, 0.19f, 0.08f, 0.96f) : new Color(0.085f, 0.094f, 0.108f, 0.9f),
            BorderColor = current ? new Color(1f, 0.86f, 0.33f) : GetKindColor(entry.Kind),
            BorderWidthLeft = 1,
            BorderWidthTop = 1,
            BorderWidthRight = 1,
            BorderWidthBottom = 1,
            CornerRadiusTopLeft = 0,
            CornerRadiusTopRight = 0,
            CornerRadiusBottomLeft = 0,
            CornerRadiusBottomRight = 0,
        };
        panel.AddThemeStyleboxOverride("panel", tileStyle);

        VBoxContainer box = new()
        {
            CustomMinimumSize = new Vector2(TileWidth, TileHeight),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        box.AddThemeConstantOverride("separation", 2);

        Control image = CreateImage(entry);
        Label title = new()
        {
            Text = Shorten(entry.Title, 9),
            HorizontalAlignment = HorizontalAlignment.Center,
            ClipText = true,
            CustomMinimumSize = new Vector2(TileWidth - 8f, 18f),
        };
        title.AddThemeFontSizeOverride("font_size", 10);
        title.AddThemeColorOverride("font_color", Colors.White);
        ApplyGameFont(title, FontType.Regular);

        Label marker = new()
        {
            Text = current ? $"> {GetKindText(entry.Kind)} <" : (applied ? GetKindText(entry.Kind) : UndoText.Redo),
            HorizontalAlignment = HorizontalAlignment.Center,
            CustomMinimumSize = new Vector2(TileWidth - 8f, 16f),
        };
        marker.AddThemeFontSizeOverride("font_size", current ? 13 : 10);
        marker.AddThemeColorOverride("font_color", current ? new Color(1f, 0.9f, 0.45f) : GetKindColor(entry.Kind));
        ApplyGameFont(marker, FontType.Bold);

        box.AddChild(image);
        box.AddChild(title);
        box.AddChild(marker);
        panel.AddChild(box);
        wrapper.AddChild(panel);
        return wrapper;
    }

    private static void OnTileInput(InputEvent inputEvent, int snapshotIndex)
    {
        if (inputEvent is not InputEventMouseButton mouseButton ||
            !mouseButton.Pressed ||
            mouseButton.ButtonIndex != MouseButton.Left)
        {
            return;
        }

        UndoRedoManager.TryRestoreSnapshot(snapshotIndex);
        NGame.Instance?.GetViewport()?.SetInputAsHandled();
    }

    private static void OnQuickRestartInput(InputEvent inputEvent)
    {
        if (inputEvent is not InputEventMouseButton mouseButton ||
            !mouseButton.Pressed ||
            mouseButton.ButtonIndex != MouseButton.Left)
        {
            return;
        }

        FloorRestartService.HandleQuickRestartKey();
        NGame.Instance?.GetViewport()?.SetInputAsHandled();
    }

    private static void SetTileHover(Control tile, bool isHovered)
    {
        if (!GodotObject.IsInstanceValid(tile))
        {
            return;
        }

        tile.Scale = isHovered ? new Vector2(1.03f, 1.03f) : Vector2.One;
        tile.ZIndex = isHovered ? 20 : 0;
    }

    private static void SetInitialRowHover(Control row, bool isHovered)
    {
        if (!GodotObject.IsInstanceValid(row))
        {
            return;
        }

        row.Scale = isHovered ? new Vector2(1.025f, 1.025f) : Vector2.One;
        row.ZIndex = isHovered ? 20 : 0;
    }

    private static Control CreateImage(ActionHistoryEntry entry)
    {
        Texture2D? texture = GetTexture(entry);
        if (texture == null)
        {
            Label fallback = new()
            {
                Text = GetKindText(entry.Kind),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                CustomMinimumSize = new Vector2(TileWidth - 10f, 78f),
            };
            fallback.AddThemeFontSizeOverride("font_size", 12);
            fallback.AddThemeColorOverride("font_color", GetKindColor(entry.Kind));
            ApplyGameFont(fallback, FontType.Bold);
            return fallback;
        }

        TextureRect image = new()
        {
            Texture = texture,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            CustomMinimumSize = new Vector2(TileWidth - 10f, 78f),
        };
        return image;
    }

    private static Texture2D? GetTexture(ActionHistoryEntry entry)
    {
        try
        {
            if (entry.Kind == ActionHistoryEntryKind.Card)
            {
                return entry.Card?.Portrait;
            }

            return entry.Potion?.Image;
        }
        catch (Exception ex)
        {
            MainFile.Logger.Debug($"Failed to load stack entry texture: {ex.Message}");
            return null;
        }
    }

    private static string Shorten(string text, int maxLength)
    {
        if (text.Length <= maxLength)
        {
            return text;
        }

        return text[..Math.Max(1, maxLength - 1)] + ".";
    }

    private static void ApplyGameFont(Control control, FontType fontType)
    {
        try
        {
            control.ApplyLocaleFontSubstitution(fontType, "font");
        }
        catch (Exception ex)
        {
            MainFile.Logger.Debug($"Failed to apply game font: {ex.Message}");
        }
    }

    private static string GetKindText(ActionHistoryEntryKind kind)
    {
        return kind switch
        {
            ActionHistoryEntryKind.Card => UndoText.Kind(kind),
            ActionHistoryEntryKind.Potion => UndoText.Kind(kind),
            ActionHistoryEntryKind.DiscardPotion => UndoText.Kind(kind),
            ActionHistoryEntryKind.TurnTransition => UndoText.Kind(kind),
            _ => UndoText.Kind(kind),
        };
    }

    private static Color GetKindColor(ActionHistoryEntryKind kind)
    {
        return kind switch
        {
            ActionHistoryEntryKind.Card => new Color(0.45f, 0.72f, 1f),
            ActionHistoryEntryKind.Potion => new Color(0.45f, 1f, 0.68f),
            ActionHistoryEntryKind.DiscardPotion => new Color(1f, 0.58f, 0.35f),
            ActionHistoryEntryKind.TurnTransition => new Color(1f, 0.84f, 0.42f),
            _ => Colors.White,
        };
    }
}
