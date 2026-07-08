using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Localization.Fonts;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;

namespace UndoAndRedoForkCode;

[HarmonyPatch(typeof(NModInfoContainer), nameof(NModInfoContainer.Fill))]
internal static class ModSettingsPanelPatch
{
    private const string PanelName = "UndoAndRedoForkSettingsPanel";

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
            old.QueueFree();
        }
    }

    private static Control CreatePanel()
    {
        PanelContainer panel = new()
        {
            Name = PanelName,
            Position = new Vector2(0f, 318f),
            CustomMinimumSize = new Vector2(560f, 174f),
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

        Label title = CreateLabel(UndoText.SnapshotLimitTitle, 17, new Color(1f, 0.91f, 0.66f), FontType.Bold);
        Label hint = CreateLabel(UndoText.SnapshotLimitHint, 11, new Color(0.74f, 0.76f, 0.8f), FontType.Regular);
        Label warning = CreateLabel(UndoText.SnapshotLimitWarning, 11, new Color(1f, 0.65f, 0.42f), FontType.Bold);
        CheckBox historyToggle = new()
        {
            Text = UndoText.ShowHistoryTab,
            ButtonPressed = UndoAndRedoConfig.ShowActionHistoryOverlay,
            CustomMinimumSize = new Vector2(260f, 30f),
            FocusMode = Control.FocusModeEnum.None,
        };
        historyToggle.AddThemeFontSizeOverride("font_size", 13);
        historyToggle.AddThemeColorOverride("font_color", new Color(0.84f, 0.86f, 0.9f));
        historyToggle.AddThemeColorOverride("font_pressed_color", new Color(1f, 0.91f, 0.66f));
        ApplyGameFont(historyToggle, FontType.Bold);
        historyToggle.Toggled += SetHistoryOverlayVisible;

        HBoxContainer row = new();
        row.AddThemeConstantOverride("separation", 8);

        LineEdit input = new()
        {
            Text = UndoAndRedoConfig.SnapshotLimit.ToString(),
            CustomMinimumSize = new Vector2(120f, 34f),
            SelectAllOnFocus = true,
        };
        ApplyGameFont(input, FontType.Regular);

        Label status = CreateLabel("", 12, new Color(0.74f, 0.76f, 0.8f), FontType.Regular);
        status.CustomMinimumSize = new Vector2(220f, 34f);
        status.VerticalAlignment = VerticalAlignment.Center;

        Button saveButton = new()
        {
            Text = UndoText.Save,
            CustomMinimumSize = new Vector2(88f, 34f),
        };
        ApplyGameFont(saveButton, FontType.Bold);
        saveButton.Pressed += () => SaveSnapshotLimit(input, status);
        input.TextSubmitted += _ => SaveSnapshotLimit(input, status);

        row.AddChild(input);
        row.AddChild(saveButton);
        row.AddChild(status);
        box.AddChild(title);
        box.AddChild(historyToggle);
        box.AddChild(hint);
        box.AddChild(warning);
        box.AddChild(row);
        margin.AddChild(box);
        panel.AddChild(margin);
        return panel;
    }

    private static void SetHistoryOverlayVisible(bool visible)
    {
        UndoAndRedoConfig.SetShowActionHistoryOverlay(visible, save: true);
        UndoStackOverlay.Refresh();
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

    private static void SaveSnapshotLimit(LineEdit input, Label status)
    {
        if (UndoAndRedoConfig.TrySetSnapshotLimit(input.Text, out int value))
        {
            input.Text = value.ToString();
            status.Text = UndoText.Saved(value);
            status.AddThemeColorOverride("font_color", new Color(0.55f, 1f, 0.68f));
            return;
        }

        input.Text = value.ToString();
        status.Text = UndoText.NumberOnly;
        status.AddThemeColorOverride("font_color", new Color(1f, 0.58f, 0.42f));
    }

    private static void ApplyGameFont(Control control, FontType fontType)
    {
        try
        {
            control.ApplyLocaleFontSubstitution(fontType, "font");
        }
        catch
        {
            // 폰트 교체가 불가능해도 설정 화면은 기본 폰트로 동작해야 함.
        }
    }
}
