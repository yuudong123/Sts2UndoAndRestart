using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace UndoAndRestartCode;

internal sealed class RunStateSnapshot
{
    private readonly RunState _runState;
    private readonly List<CardModel> _allCards;
    private readonly ObjectGraphSnapshot _extraFields;
    private readonly ObjectGraphSnapshot _rng;
    private readonly ObjectGraphSnapshot _odds;
    private readonly ObjectGraphSnapshot _sharedRelicGrabBag;
    private readonly int _nextRoomId;
    private readonly CombatRoomSnapshot? _combatRoom;

    private RunStateSnapshot(RunState runState)
    {
        _runState = runState;
        _allCards = ReflectionUtil.GetRequiredField<List<CardModel>>(runState, "_allCards").ToList();
        _extraFields = ObjectGraphSnapshot.Capture(runState.ExtraFields);
        _rng = ObjectGraphSnapshot.Capture(runState.Rng);
        _odds = ObjectGraphSnapshot.Capture(runState.Odds);
        _sharedRelicGrabBag = ObjectGraphSnapshot.Capture(runState.SharedRelicGrabBag);
        _nextRoomId = runState.NextRoomId;
        _combatRoom = runState.CurrentRoom is CombatRoom room
            ? CombatRoomSnapshot.Capture(room)
            : null;
    }

    public static RunStateSnapshot Capture(RunState runState)
    {
        return new RunStateSnapshot(runState);
    }

    public void Restore()
    {
        ReflectionUtil.ReplaceList(
            ReflectionUtil.GetRequiredField<List<CardModel>>(_runState, "_allCards"),
            _allCards);
        _extraFields.Restore(_runState.ExtraFields);
        _rng.Restore(_runState.Rng);
        _odds.Restore(_runState.Odds);
        _sharedRelicGrabBag.Restore(_runState.SharedRelicGrabBag);
        ReflectionUtil.SetField(_runState, "<NextRoomId>k__BackingField", _nextRoomId);
        _combatRoom?.Restore();
    }

    private sealed class CombatRoomSnapshot
    {
        private readonly CombatRoom _room;
        private readonly bool _isPreFinished;
        private readonly float _goldProportion;
        private readonly Dictionary<Player, List<Reward>> _extraRewards;

        private CombatRoomSnapshot(CombatRoom room)
        {
            _room = room;
            _isPreFinished = room.IsPreFinished;
            _goldProportion = room.GoldProportion;
            _extraRewards =
                (Dictionary<Player, List<Reward>>)ObjectGraphSnapshot.CloneValue(
                    ReflectionUtil.GetRequiredField<Dictionary<Player, List<Reward>>>(room, "_extraRewards"))!;
        }

        public static CombatRoomSnapshot Capture(CombatRoom room)
        {
            return new CombatRoomSnapshot(room);
        }

        public void Restore()
        {
            ReflectionUtil.SetField(_room, "_isPreFinished", _isPreFinished);
            ReflectionUtil.SetField(_room, "<GoldProportion>k__BackingField", _goldProportion);

            Dictionary<Player, List<Reward>> destination =
                ReflectionUtil.GetRequiredField<Dictionary<Player, List<Reward>>>(_room, "_extraRewards");

            destination.Clear();
            foreach ((Player player, List<Reward> rewards) in _extraRewards)
            {
                destination[player] =
                    (List<Reward>)ObjectGraphSnapshot.CloneValue(rewards)!;
            }
        }
    }
}
