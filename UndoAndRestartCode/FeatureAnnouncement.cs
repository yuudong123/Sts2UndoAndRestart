using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;

namespace UndoAndRestartCode;

internal static class FeatureAnnouncement
{
    private const string LocalizationTable = "main_menu_ui";
    private const string TitleKey = "UNDO_AND_RESTART_FEATURE.title";
    private const string BodyKey = "UNDO_AND_RESTART_FEATURE.body";
    private const string ConfirmKey = "UNDO_AND_RESTART_FEATURE.confirm";

    public static async Task ShowAfterMainMenuReady(NMainMenu mainMenu)
    {
        if (UndoAndRestartConfig.HasAcknowledgedFeatureAnnouncement)
        {
            return;
        }

        for (int frame = 0; frame < 36000; frame++)
        {
            if (!GodotObject.IsInstanceValid(mainMenu) ||
                UndoAndRestartConfig.HasAcknowledgedFeatureAnnouncement)
            {
                return;
            }

            SceneTree tree = mainMenu.GetTree();
            await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            NModalContainer? modalContainer = NModalContainer.Instance;
            if (modalContainer == null || modalContainer.OpenModal != null)
            {
                continue;
            }

            NGenericPopup? popup = NGenericPopup.Create();
            if (popup == null)
            {
                return;
            }

            RegisterLocalizedPopupText();
            modalContainer.Add(popup);
            bool acknowledged = await popup.WaitForConfirmation(
                new LocString(LocalizationTable, BodyKey),
                new LocString(LocalizationTable, TitleKey),
                null,
                new LocString(LocalizationTable, ConfirmKey));
            modalContainer.Clear();
            if (acknowledged)
            {
                UndoAndRestartConfig.AcknowledgeFeatureAnnouncement();
            }

            return;
        }

        MainFile.Logger.Warn(
            "Feature announcement was not shown because the modal container stayed occupied.");
    }

    private static void RegisterLocalizedPopupText()
    {
        LocManager.Instance.GetTable(LocalizationTable).MergeWith(
            new Dictionary<string, string>
            {
                [TitleKey] = UndoText.FeatureAnnouncementTitle,
                [BodyKey] = UndoText.FeatureAnnouncementBody,
                [ConfirmKey] = UndoText.FeatureAnnouncementConfirm,
            });
    }
}

[HarmonyPatch(typeof(NMainMenu), nameof(NMainMenu._Ready))]
internal static class FeatureAnnouncementPatch
{
    private static void Postfix(NMainMenu __instance)
    {
        if (!UndoAndRestartConfig.HasAcknowledgedFeatureAnnouncement)
        {
            TaskHelper.RunSafely(
                FeatureAnnouncement.ShowAfterMainMenuReady(__instance));
        }
    }
}
