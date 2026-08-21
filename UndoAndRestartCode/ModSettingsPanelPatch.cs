using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Localization.Fonts;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;

namespace UndoAndRestartCode;

[HarmonyPatch(typeof(NModInfoContainer), nameof(NModInfoContainer.Fill))]
internal static class ModSettingsPanelPatch
{
    private const string PanelName = "UndoAndRestartSettingsPanel";

    private static void Postfix(NModInfoContainer __instance, Mod mod)
    {
        RemoveExistingPanel(__instance);
        if (!string.Equals(mod.manifest?.id, MainFile.ModId, StringComparison.Ordinal))
        {
            return;
        }

        __instance.AddChild(CreatePanel());
    }

    private static void RemoveExistingPanel(NModInfoContainer container)
    {
        Node? old = container.GetNodeOrNull(new NodePath(PanelName));
        if (old != null && GodotObject.IsInstanceValid(old))
        {
            container.RemoveChild(old);
            old.QueueFreeSafely();
        }
    }

    private static Control CreatePanel()
    {
        int savedSnapshotLimit = UndoAndRestartConfig.SnapshotLimit;
        bool savedShowHistory = UndoAndRestartConfig.ShowActionHistoryOverlay;
        bool savedIncludeCardChoices = UndoAndRestartConfig.IncludeCardChoiceSnapshots;

        PanelContainer panel = new()
        {
            Name = PanelName,
            Position = new Vector2(0f, 318f),
            CustomMinimumSize = new Vector2(560f, 252f),
        };
        StyleBoxFlat style = new()
        {
            BgColor = new Color(0.045f, 0.052f, 0.062f, 0.92f),
            BorderColor = new Color(0.32f, 0.34f, 0.38f, 0.9f),
            BorderWidthLeft = 1,
            BorderWidthTop = 1,
            BorderWidthRight = 1,
            BorderWidthBottom = 1,
        };
        panel.AddThemeStyleboxOverride("panel", style);

        MarginContainer margin = new();
        margin.AddThemeConstantOverride("margin_left", 12);
        margin.AddThemeConstantOverride("margin_top", 10);
        margin.AddThemeConstantOverride("margin_right", 12);
        margin.AddThemeConstantOverride("margin_bottom", 10);

        VBoxContainer box = new();
        box.AddThemeConstantOverride("separation", 6);

        Label hint = CreateLabel(UndoText.SnapshotLimitHint, 11, new Color(0.74f, 0.76f, 0.8f), FontType.Regular);
        Label warning = CreateLabel(UndoText.SnapshotLimitWarning, 11, new Color(1f, 0.65f, 0.42f), FontType.Bold);
        Button historyToggle = CreateToggleButton(
            UndoText.ShowHistoryTab,
            savedShowHistory);
        Button cardChoiceToggle = CreateToggleButton(
            UndoText.IncludeCardChoiceSnapshots,
            savedIncludeCardChoices);

        HBoxContainer snapshotRow = new();
        snapshotRow.AddThemeConstantOverride("separation", 10);
        Label snapshotLabel = CreateLabel(
            UndoText.SnapshotLimitTitle,
            13,
            new Color(0.9f, 0.88f, 0.8f),
            FontType.Bold);
        snapshotLabel.CustomMinimumSize = new Vector2(210f, 34f);
        snapshotLabel.VerticalAlignment = VerticalAlignment.Center;

        LineEdit input = new()
        {
            Text = savedSnapshotLimit.ToString(),
            CustomMinimumSize = new Vector2(120f, 34f),
            SelectAllOnFocus = true,
        };
        ApplyGameFont(input, FontType.Regular);

        Label status = CreateLabel("", 12, new Color(0.74f, 0.76f, 0.8f), FontType.Regular);
        status.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        status.VerticalAlignment = VerticalAlignment.Center;

        Label savedIndicator = CreateLabel(
            "",
            20,
            new Color(0.42f, 1f, 0.56f),
            FontType.Bold);
        savedIndicator.CustomMinimumSize = new Vector2(28f, 34f);
        savedIndicator.HorizontalAlignment = HorizontalAlignment.Center;
        savedIndicator.VerticalAlignment = VerticalAlignment.Center;

        Button saveButton = new()
        {
            Text = UndoText.Save,
            CustomMinimumSize = new Vector2(88f, 34f),
            Disabled = true,
        };
        ApplyGameFont(saveButton, FontType.Bold);
        HBoxContainer footer = new();
        footer.AddThemeConstantOverride("separation", 8);

        input.TextChanged += _ => RefreshSaveButton();
        historyToggle.Toggled += _ => RefreshSaveButton();
        cardChoiceToggle.Toggled += _ => RefreshSaveButton();
        input.TextSubmitted += _ =>
        {
            if (!saveButton.Disabled)
            {
                SaveDraft();
            }
        };
        saveButton.Pressed += SaveDraft;

        snapshotRow.AddChild(snapshotLabel);
        snapshotRow.AddChild(input);
        footer.AddChild(status);
        footer.AddChild(savedIndicator);
        footer.AddChild(saveButton);
        box.AddChild(historyToggle);
        box.AddChild(cardChoiceToggle);
        box.AddChild(snapshotRow);
        box.AddChild(hint);
        box.AddChild(warning);
        box.AddChild(footer);
        margin.AddChild(box);
        panel.AddChild(margin);
        return panel;

        void RefreshSaveButton()
        {
            bool changed =
                !string.Equals(
                    input.Text.Trim(),
                    savedSnapshotLimit.ToString(),
                    StringComparison.Ordinal) ||
                historyToggle.ButtonPressed != savedShowHistory ||
                cardChoiceToggle.ButtonPressed != savedIncludeCardChoices;
            saveButton.Disabled = !changed;
            if (changed)
            {
                status.Text = "";
                savedIndicator.Text = "";
            }
        }

        void SaveDraft()
        {
            if (!int.TryParse(input.Text.Trim(), out int snapshotLimit))
            {
                status.Text = UndoText.NumberOnly;
                status.AddThemeColorOverride("font_color", new Color(1f, 0.58f, 0.42f));
                return;
            }

            int normalizedSnapshotLimit = Math.Max(0, snapshotLimit);
            if (!UndoAndRestartConfig.SaveSettings(
                    normalizedSnapshotLimit,
                    historyToggle.ButtonPressed,
                    cardChoiceToggle.ButtonPressed))
            {
                status.Text = UndoText.SaveFailed;
                status.AddThemeColorOverride("font_color", new Color(1f, 0.58f, 0.42f));
                return;
            }

            savedSnapshotLimit = normalizedSnapshotLimit;
            savedShowHistory = historyToggle.ButtonPressed;
            savedIncludeCardChoices = cardChoiceToggle.ButtonPressed;
            input.Text = normalizedSnapshotLimit.ToString();
            status.Text = "";
            savedIndicator.Text = "V";
            RefreshSaveButton();
            ActionHistoryOverlay.Refresh();
            HideSavedIndicatorAfterDelay(savedIndicator);
        }
    }

    private static Button CreateToggleButton(
        string label,
        bool enabled)
    {
        Button button = new()
        {
            ToggleMode = true,
            ButtonPressed = enabled,
            CustomMinimumSize = new Vector2(360f, 30f),
            FocusMode = Control.FocusModeEnum.None,
            Alignment = HorizontalAlignment.Left,
        };
        button.AddThemeFontSizeOverride("font_size", 13);
        button.AddThemeColorOverride("font_color", new Color(0.86f, 0.88f, 0.92f));
        button.AddThemeColorOverride("font_hover_color", Colors.White);
        button.AddThemeColorOverride("font_pressed_color", new Color(1f, 0.91f, 0.66f));
        button.AddThemeStyleboxOverride(
            "normal",
            CreateToggleStyle(
                new Color(0.10f, 0.12f, 0.15f, 0.96f),
                new Color(0.42f, 0.46f, 0.52f, 1f)));
        button.AddThemeStyleboxOverride(
            "hover",
            CreateToggleStyle(
                new Color(0.14f, 0.17f, 0.21f, 0.98f),
                new Color(0.66f, 0.7f, 0.78f, 1f)));
        button.AddThemeStyleboxOverride(
            "pressed",
            CreateToggleStyle(
                new Color(0.18f, 0.17f, 0.11f, 0.98f),
                new Color(0.92f, 0.75f, 0.34f, 1f)));
        button.AddThemeStyleboxOverride(
            "disabled",
            CreateToggleStyle(
                new Color(0.08f, 0.09f, 0.11f, 0.9f),
                new Color(0.3f, 0.32f, 0.36f, 0.9f)));
        button.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
        ApplyGameFont(button, FontType.Bold);
        UpdateToggleText(button, label, enabled);
        button.Toggled += value =>
        {
            UpdateToggleText(button, label, value);
        };
        return button;
    }

    private static StyleBoxFlat CreateToggleStyle(Color background, Color border)
    {
        return new StyleBoxFlat
        {
            BgColor = background,
            BorderColor = border,
            BorderWidthLeft = 1,
            BorderWidthTop = 1,
            BorderWidthRight = 1,
            BorderWidthBottom = 1,
            ContentMarginLeft = 9f,
            ContentMarginRight = 9f,
        };
    }

    private static void UpdateToggleText(Button button, string label, bool enabled)
    {
        button.Text = $"{(enabled ? "[X]" : "[ ]")} {label}";
    }

    private static Label CreateLabel(string text, int fontSize, Color color, FontType fontType)
    {
        Label label = new()
        {
            Text = text,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        label.AddThemeFontSizeOverride("font_size", fontSize);
        label.AddThemeColorOverride("font_color", color);
        ApplyGameFont(label, fontType);
        return label;
    }

    private static void HideSavedIndicatorAfterDelay(Label savedIndicator)
    {
        if (!GodotObject.IsInstanceValid(savedIndicator))
        {
            return;
        }

        SceneTreeTimer timer = savedIndicator.GetTree().CreateTimer(1.4);
        timer.Timeout += () =>
        {
            if (GodotObject.IsInstanceValid(savedIndicator))
            {
                savedIndicator.Text = "";
            }
        };
    }

    private static void ApplyGameFont(Control control, FontType fontType)
    {
        try
        {
            control.RemoveThemeFontOverride("font");
            control.ApplyLocaleFontSubstitution(fontType, "font");
        }
        catch
        {
            // 폰트 교체가 불가능해도 설정 화면은 기본 폰트로 동작해야 함.
        }
    }
}
