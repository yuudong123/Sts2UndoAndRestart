using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Runs.History;

namespace UndoAndRestartCode;

internal sealed class RunHistorySnapshot
{
    private readonly RunState _runState;
    private readonly int _actFloor;
    private readonly List<MapCoord> _visitedMapCoords;
    private readonly List<List<MapPointHistoryEntry>> _mapPointHistory;
    private readonly HashSet<ModelId> _visitedEventIds;

    private RunHistorySnapshot(RunState runState)
    {
        _runState = runState;
        _actFloor = runState.ActFloor;
        _visitedMapCoords =
            (List<MapCoord>)ObjectGraphSnapshot.CloneValue(runState.VisitedMapCoords.ToList())!;
        _mapPointHistory = CloneHistory(runState.MapPointHistory);
        _visitedEventIds =
            (HashSet<ModelId>)ObjectGraphSnapshot.CloneValue(runState.VisitedEventIds.ToHashSet())!;
    }

    public static RunHistorySnapshot Capture(RunState runState)
    {
        return new RunHistorySnapshot(runState);
    }

    public void Restore()
    {
        _runState.ActFloor = _actFloor;
        ReplacePrivateList(
            "_visitedMapCoords",
            (List<MapCoord>)ObjectGraphSnapshot.CloneValue(_visitedMapCoords)!);
        ReplacePrivateList(
            "_mapPointHistory",
            CloneHistory(_mapPointHistory));
        ReplacePrivateSet(
            "_visitedEventIds",
            (HashSet<ModelId>)ObjectGraphSnapshot.CloneValue(_visitedEventIds)!);
    }

    private void ReplacePrivateList<T>(string fieldName, IEnumerable<T> values)
    {
        List<T>? destination = ReflectionUtil.GetField<List<T>>(_runState, fieldName);
        if (destination == null)
        {
            throw new MissingFieldException(typeof(RunState).FullName, fieldName);
        }

        ReflectionUtil.ReplaceList(destination, values);
    }

    private void ReplacePrivateSet<T>(string fieldName, IEnumerable<T> values)
    {
        HashSet<T>? destination = ReflectionUtil.GetField<HashSet<T>>(_runState, fieldName);
        if (destination == null)
        {
            throw new MissingFieldException(typeof(RunState).FullName, fieldName);
        }

        destination.Clear();
        destination.UnionWith(values);
    }

    private static List<List<MapPointHistoryEntry>> CloneHistory(
        IReadOnlyList<IReadOnlyList<MapPointHistoryEntry>> history)
    {
        List<List<MapPointHistoryEntry>> result = new(history.Count);
        for (int actIndex = 0; actIndex < history.Count; actIndex++)
        {
            IReadOnlyList<MapPointHistoryEntry> actEntries = history[actIndex];
            List<MapPointHistoryEntry> actCopy = actEntries.ToList();
            if (actCopy.Count > 0 && actIndex == history.Count - 1)
            {
                int currentIndex = actCopy.Count - 1;
                actCopy[currentIndex] =
                    (MapPointHistoryEntry)ObjectGraphSnapshot.CloneValue(actCopy[currentIndex])!;
            }

            result.Add(actCopy);
        }

        return result;
    }
}
