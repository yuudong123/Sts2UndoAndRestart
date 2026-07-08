using System.Threading;
using Godot;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Audio;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.MapDrawing;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace UndoAndRedoForkCode;

internal static class QuickRestartService
{
    private const string FadeTransitionPath = "res://materials/transitions/fade_transition_mat.tres";
    private static bool _isRestarting;

    public static void HandleQuickRestartKey()
    {
        RunManager runManager = RunManager.Instance;
        if (!runManager.IsInProgress)
        {
            return;
        }

        if (runManager.NetService.Type != NetGameType.Singleplayer)
        {
            MainFile.Logger.Info("Ignored F5 quick restart request during multiplayer.");
            return;
        }

        NGame? game = NGame.Instance;
        if (game == null || runManager.IsGameOver || game.Transition.InTransition)
        {
            return;
        }

        if (HasPendingGameAction(runManager))
        {
            MainFile.Logger.Info("Ignored F5 quick restart request while a game action is in progress.");
            return;
        }

        TaskHelper.RunSafely(DoQuickRestart());
    }

    private static async Task DoQuickRestart()
    {
        if (_isRestarting)
        {
            return;
        }

        RunManager runManager = RunManager.Instance;
        if (!runManager.IsInProgress)
        {
            return;
        }

        if (runManager.NetService.Type != NetGameType.Singleplayer)
        {
            MainFile.Logger.Info("Quick restart aborted because the run is not singleplayer.");
            return;
        }

        if (HasPendingGameAction(runManager))
        {
            MainFile.Logger.Info("Quick restart aborted because a game action is in progress.");
            return;
        }

        _isRestarting = true;
        MainFile.Logger.Info("F5 quick restart triggered.");
        try
        {
            NGame? game = NGame.Instance;
            if (game == null)
            {
                MainFile.Logger.Warn("Quick restart aborted because NGame is unavailable.");
                return;
            }

            if (SaveManager.Instance.CurrentRunSaveTask != null)
            {
                await SaveManager.Instance.CurrentRunSaveTask;
            }

            ReadSaveResult<SerializableRun> result = SaveManager.Instance.LoadRunSave();
            if (!result.Success || result.SaveData == null)
            {
                MainFile.Logger.Warn($"Unable to load current singleplayer run save. Status={result.Status}");
                return;
            }

            SerializableRun save = result.SaveData;
            SerializableRoom? roomToRestore = GetRoomToRestore(save);
            SerializableMapDrawings? savedDrawings = CaptureMapDrawings();
            List<(uint CombatId, Vector2 Position)> savedEnemyPositions = CaptureEnemyPositions();

            MainFile.Logger.Info("Cleaning up current run scene for F5 quick restart.");
            CombatRuntimeStateCleanup.ClearCombatTurnFlags();
            runManager.ActionQueueSet.Reset();
            NRunMusicController.Instance?.StopMusic();
            await game.Transition.FadeOut(0.8f, FadeTransitionPath);
            runManager.CleanUp(graceful: true);
            CombatRuntimeStateCleanup.ClearCombatTurnFlags();

            RunState runState = RunState.FromSerializable(save);
            await runManager.SetUpSavedSingleplayer(runState, save);
            game.ReactionContainer.InitializeNetworking(new NetSingleplayerGameService());

            await PreloadManager.LoadRunAssets(runState.Players.Select(static player => player.Character));
            await PreloadManager.LoadActAssets(runState.Act);
            runManager.Launch();

            NRun runNode = NRun.Create(runState);
            game.RootSceneContainer.SetCurrentScene(runNode);
            await runManager.GenerateMap();

            AbstractRoom? restoredRoom = TryDeserializeRoom(roomToRestore, runState);
            if (restoredRoom != null && runState.VisitedMapCoords.Count > 0)
            {
                TryEnsureRoomHistory(runState, restoredRoom);
            }

            if (restoredRoom is EventRoom eventRoom)
            {
                TryMarkVisitedEvent(runState, eventRoom);
            }

            await runManager.LoadIntoLatestMapCoord(restoredRoom);
            RestoreEnemyPositions(savedEnemyPositions);
            RestoreMapDrawings(savedDrawings);
            await game.Transition.FadeIn(0.8f, FadeTransitionPath);
            await RestoreMapMarker(game, runState);
            CombatRuntimeStateCleanup.ClearCombatTurnFlags();
            UndoRedoManager.Reset();
            MainFile.Logger.Info("F5 quick restart complete.");
        }
        catch (Exception ex)
        {
            MainFile.Logger.Error($"F5 quick restart failed: {ex}");
        }
        finally
        {
            _isRestarting = false;
        }
    }

    private static bool HasPendingGameAction(RunManager runManager)
    {
        return !runManager.ActionQueueSet.IsEmpty ||
               runManager.ActionExecutor.IsRunning ||
               runManager.ActionExecutor.CurrentlyRunningAction != null;
    }

    private static SerializableRoom? GetRoomToRestore(SerializableRun save)
    {
        if (CanDeserializeRoom(save.PreFinishedRoom))
        {
            return PrepareRoomForQuickRestart(save.PreFinishedRoom);
        }

        try
        {
            AbstractRoom? currentRoom = RunManager.Instance.DebugOnlyGetState()?.CurrentRoom;
            SerializableRoom? currentRoomSave = currentRoom?.ToSerializable();
            if (CanDeserializeRoom(currentRoomSave))
            {
                MainFile.Logger.Info($"Captured current room as quick restart fallback: {currentRoom!.RoomType} ({currentRoom.ModelId})");
                return PrepareRoomForQuickRestart(currentRoomSave);
            }
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"Failed to capture current room fallback: {ex.Message}");
        }

        return null;
    }

    private static SerializableRoom? PrepareRoomForQuickRestart(SerializableRoom? room)
    {
        if (room == null)
        {
            return null;
        }

        if (!room.IsPreFinished &&
            room.RoomType is RoomType.Monster or RoomType.Elite or RoomType.Boss &&
            room.ExtraRewards.Count > 0)
        {
            MainFile.Logger.Info(
                $"Cleared {room.ExtraRewards.Sum(static entry => entry.Value.Count)} " +
                "unfinished-combat extra reward(s) before F5 quick restart.");
            room.ExtraRewards.Clear();
        }
        else if (room.IsPreFinished && room.ExtraRewards.Count > 0)
        {
            MainFile.Logger.Info(
                $"Preserved {room.ExtraRewards.Sum(static entry => entry.Value.Count)} " +
                "completed-combat extra reward(s) for F5 reward-screen restore.");
        }

        return room;
    }

    private static bool CanDeserializeRoom(SerializableRoom? room)
    {
        if (room == null)
        {
            return false;
        }

        return room.RoomType is RoomType.Monster or RoomType.Elite or RoomType.Boss or RoomType.Event;
    }

    private static AbstractRoom? TryDeserializeRoom(SerializableRoom? room, RunState runState)
    {
        if (!CanDeserializeRoom(room))
        {
            return null;
        }

        try
        {
            return AbstractRoom.FromSerializable(room, runState);
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"Failed to deserialize quick restart room; falling back to map point room. {ex.Message}");
            return null;
        }
    }

    private static SerializableMapDrawings? CaptureMapDrawings()
    {
        try
        {
            return NRun.Instance?.GlobalUi?.MapScreen?.Drawings?.GetSerializableMapDrawings();
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"Failed to capture map drawings: {ex.Message}");
            return null;
        }
    }

    private static void RestoreMapDrawings(SerializableMapDrawings? drawings)
    {
        if (drawings == null)
        {
            return;
        }

        try
        {
            NRun.Instance?.GlobalUi?.MapScreen?.Drawings?.LoadDrawings(drawings);
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"Failed to restore map drawings: {ex.Message}");
        }
    }

    private static List<(uint CombatId, Vector2 Position)> CaptureEnemyPositions()
    {
        List<(uint, Vector2)> positions = new();
        try
        {
            NCombatRoom? room = NCombatRoom.Instance;
            if (room == null)
            {
                return positions;
            }

            foreach (NCreature node in room.CreatureNodes)
            {
                if (node.Entity.Side == CombatSide.Enemy)
                {
                    positions.Add((node.Entity.CombatId.GetValueOrDefault(), node.Position));
                }
            }

            positions.Sort(static (left, right) => left.Item1.CompareTo(right.Item1));
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"Failed to capture enemy positions: {ex.Message}");
        }

        return positions;
    }

    private static void RestoreEnemyPositions(List<(uint CombatId, Vector2 Position)> savedEnemyPositions)
    {
        try
        {
            NCombatRoom? room = NCombatRoom.Instance;
            if (room == null || savedEnemyPositions.Count == 0)
            {
                return;
            }

            List<NCreature> enemies = room.CreatureNodes.Where(static node => node.Entity.Side == CombatSide.Enemy).ToList();
            if (enemies.Count != savedEnemyPositions.Count)
            {
                MainFile.Logger.Info($"Skipped enemy position restore because count changed ({enemies.Count} vs {savedEnemyPositions.Count}).");
                return;
            }

            for (int i = 0; i < enemies.Count; i++)
            {
                enemies[i].Position = savedEnemyPositions[i].Position;
            }
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"Failed to restore enemy positions: {ex.Message}");
        }
    }

    private static void TryEnsureRoomHistory(RunState runState, AbstractRoom restoredRoom)
    {
        try
        {
            IReadOnlyList<MapCoord> visitedMapCoords = runState.VisitedMapCoords;
            MapCoord coord = visitedMapCoords[visitedMapCoords.Count - 1];
            if (runState.GetHistoryEntryFor(runState.MapLocation) != null)
            {
                MainFile.Logger.Info("Quick restart reused existing room history entry.");
                return;
            }

            MapPoint? point = runState.Map.GetPoint(coord);
            if (point != null)
            {
                runState.AppendToMapPointHistory(point.PointType, restoredRoom.RoomType, restoredRoom.ModelId);
                MainFile.Logger.Info("Quick restart rebuilt missing room history entry.");
            }
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"Failed to rebuild quick restart room history: {ex.Message}");
        }
    }

    private static void TryMarkVisitedEvent(RunState runState, EventRoom eventRoom)
    {
        try
        {
            runState.AddVisitedEvent(eventRoom.CanonicalEvent);
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"Failed to mark quick restarted event as visited: {ex.Message}");
        }
    }

    private static async Task RestoreMapMarker(NGame game, RunState runState)
    {
        if (runState.VisitedMapCoords.Count == 0)
        {
            return;
        }

        IReadOnlyList<MapCoord> visitedMapCoords = runState.VisitedMapCoords;
        MapCoord lastCoord = visitedMapCoords[visitedMapCoords.Count - 1];
        await game.ToSignal(game.GetTree(), SceneTree.SignalName.ProcessFrame);
        NMapScreen.Instance?.InitMarker(lastCoord);
    }
}
