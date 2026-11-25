using Godot;
using MegaCrit.Sts2.addons.mega_text;
using HarmonyLib;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.Capstones;
using MegaCrit.Sts2.Core.Nodes.Screens.PauseMenu;
using MegaCrit.Sts2.Core.Runs;

namespace UndoAndRestartCode;

/// <summary>
/// "Restart Floor" in the pause menu, above Abandon Run. Always there in
/// singleplayer runs, so restarting stays reachable when another mod covers the
/// top bar button or the on-screen buttons are turned off.
/// </summary>
internal static class PauseMenuRestartEntry
{
    public const string NodeName = "UndoAndRestartRestartFloor";

    public static void Attach(NPauseMenu menu)
    {
        Control container = menu.GetNode<Control>("%ButtonContainer");
        if (container.GetNodeOrNull(NodeName) != null)
        {
            return;
        }

        NPauseMenuButton giveUp = container.GetNode<NPauseMenuButton>("GiveUp");
        // No signal flag: Abandon Run's click handler must not come along.
        NPauseMenuButton entry = (NPauseMenuButton)giveUp.Duplicate(
            (int)(Node.DuplicateFlags.Groups | Node.DuplicateFlags.Scripts | Node.DuplicateFlags.UseInstantiation));
        entry.Name = NodeName;
        // The hover shader material is a shared resource; without a copy, hovering
        // this entry would also light up Abandon Run.
        TextureRect image = entry.GetNode<TextureRect>("ButtonImage");
        if (image.Material != null)
        {
            image.Material = (Material)image.Material.Duplicate();
        }

        container.AddChild(entry);
        container.MoveChild(entry, giveUp.GetIndex());
        entry.GetNode<MegaLabel>("Label").SetTextAutoSize(UndoText.InputRestart);
        entry.Connect(NClickableControl.SignalName.Released, Callable.From<NButton>(_ => OnPressed()));
    }

    /// <summary>Shown only in singleplayer runs and wired into the menu's controller focus loop.</summary>
    public static void Refresh(NPauseMenu menu)
    {
        Control container = menu.GetNode<Control>("%ButtonContainer");
        if (container.GetNodeOrNull<NPauseMenuButton>(NodeName) is not { } entry)
        {
            return;
        }

        entry.Visible = RunManager.Instance.NetService.Type == NetGameType.Singleplayer;
        if (RunManager.Instance.IsInProgress && !RunManager.Instance.IsGameOver)
        {
            entry.Enable();
        }
        else
        {
            entry.Disable();
        }

        List<NPauseMenuButton> buttons = container.GetChildren().OfType<NPauseMenuButton>().Where(button => button.Visible).ToList();
        for (int i = 0; i < buttons.Count; i++)
        {
            buttons[i].FocusNeighborTop = buttons[(i + buttons.Count - 1) % buttons.Count].GetPath();
            buttons[i].FocusNeighborBottom = buttons[(i + 1) % buttons.Count].GetPath();
        }
    }

    private static void OnPressed()
    {
        TaskHelper.RunSafely(ConfirmFromMenuAsync());
    }

    private static async Task ConfirmFromMenuAsync()
    {
        bool closed = false;
        await FloorRestartService.ConfirmAndRestartAsync(beforeRestart: () =>
        {
            // Close the menu the way Resume does before the screen fades out.
            NCapstoneContainer.Instance?.Close();
            NRun.Instance?.GlobalUi?.TopBar?.Pause?.ToggleAnimState();
            closed = true;
        });
        if (!closed)
        {
            MainFile.Logger.Info("Pause menu restart canceled.");
        }
    }
}

[HarmonyPatch(typeof(NPauseMenu), nameof(NPauseMenu._Ready))]
internal static class PauseMenuRestartReadyPatch
{
    private static void Postfix(NPauseMenu __instance)
    {
        try
        {
            PauseMenuRestartEntry.Attach(__instance);
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"Could not add Restart Floor to the pause menu: {ex.Message}");
        }
    }
}

[HarmonyPatch(typeof(NPauseMenu), nameof(NPauseMenu.Initialize))]
internal static class PauseMenuRestartInitializePatch
{
    private static void Postfix(NPauseMenu __instance)
    {
        try
        {
            PauseMenuRestartEntry.Refresh(__instance);
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"Could not update Restart Floor in the pause menu: {ex.Message}");
        }
    }
}
