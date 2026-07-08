using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;

namespace UndoAndRestartCode;

[ModInitializer(nameof(Initialize))]
public static class MainFile
{
    public const string ModId = "UndoAndRestart";

    private static Harmony? _harmony;
    private static bool _eventsSubscribed;

    public static Logger Logger { get; } = new(ModId, LogType.Generic);

    public static void Initialize()
    {
        if (_harmony != null)
        {
            return;
        }

        _harmony = new Harmony(ModId);
        _harmony.PatchAll(Assembly.GetExecutingAssembly());
        UndoAndRestartConfig.Load();
        SubscribeCombatEvents();
        Logger.Info("Undo snapshot engine initialized.");
    }

    private static void SubscribeCombatEvents()
    {
        if (_eventsSubscribed)
        {
            return;
        }

        CombatManager.Instance.TurnStarted += state =>
        {
            if (state.CurrentSide == CombatSide.Player)
            {
                UndoRedoManager.OnPlayerTurnStarted(state);
            }
        };

        CombatManager.Instance.PlayerActionsDisabledChanged += state =>
        {
            if (state?.CurrentSide == CombatSide.Player && !CombatManager.Instance.PlayerActionsDisabled)
            {
                UndoRedoManager.RequestPlayerControlReadyCapture("PlayerActionsDisabledChanged");
            }
        };

        _eventsSubscribed = true;
    }
}
