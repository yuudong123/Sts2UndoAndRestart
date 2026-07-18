# Undo And Restart snapshot audit for STS2 0.109

## Scope

This audit uses the public-beta `v0.109.0` game assembly (`c12f634d`) and the
official 0.109 patch notes. The current mod source targets this game version
only. Older game versions are supported by their matching historical releases,
not by compatibility branches in the current binary.

The 0.109 assembly contains the following gameplay model files:

| Group | Files checked |
|---|---:|
| Cards | 596 |
| Relics | 300 |
| Potions | 65 |
| Powers | 268 |
| Orbs | 5 |
| Enchantments | 23 |
| Afflictions | 7 |
| Monsters | 121 |

The audit also covers snapshot boundaries, action and choice synchronization,
combat and run history, floor restart serialization, transient combat UI, and
every Harmony patch or reflection member used by the mod.

## 0.109 implementation

### RNG state

The complete 0.109 RNG state consists of a counter and four `ulong` state
values exposed through `SerializableRng`.

`ObjectGraphSnapshot` uses `ToSerializable()` and the `SerializableRng`
constructor directly. There is no legacy constructor or object-graph fallback
in the current binary.

This preserves exact future random results instead of merely restoring the
number of calls. A focused test consumed one value, cloned the RNG, and verified
that the next values from the original and clone were identical.

### Turn transition and action queue guards

0.109 added `CombatManager._playerToEnemyTransitionFired` to reject duplicate
turn transitions and `ActionQueueSet._wasReset` to tolerate orphaned actions
after queue reset.

At a restored player-control boundary both values must be false. Snapshot
restore now normalizes both fields. Floor restart also clears the transition
guard, but intentionally leaves `_wasReset` under vanilla reset/startup control.

### Potion interaction state

The player potion lock is captured and restored through the 0.109
`Player.CanUseOrRemovePotions` property so its matching change event is raised
by vanilla code.

### Card VFX cleanup

0.109 introduced `NCardExhaustVfx`, `NCardExhaustQuickVfx`, and
`NCardRemoveVfx`. These nodes can outlive the action briefly and retain their
own `NCard`, so restoring during that delay can leave a stale card on screen.

Restore now removes these nodes from combat UI and global VFX containers using
direct 0.109 type references.

## New gameplay state review

The official 0.109 notes were mapped to the implementation classes below. This
review follows complete feature flows rather than checking each model name in
isolation.

### Dowsing Rod quest flow

- `DowsingRod.AfterObtained` creates a run-scoped `Dowsing` card and adds it to
  the permanent deck.
- `Dowsing.AfterRoomEntered` increments the saved `_roomsEntered` field when an
  unknown room is entered. At five rooms it completes the quest and calls
  `CardCmd.TransformTo<Abundance>` on the permanent card.
- A combat deck can already have been cloned before the room-entry transform
  finishes. The valid fifth-room combat state is therefore permanent
  `Abundance` plus a separate combat `Dowsing` clone. The run registry, permanent
  deck, combat registry, and combat piles must not be merged.
- The snapshot captures both card identities independently. Snapshot capture
  repairs a missing permanent-deck registration in `RunState._allCards`, removes
  that permanent card from `CombatState._allCards`, and clears an invalid
  removed-state flag. This was required after a real 0.109 run exposed
  `Abundance` in the deck but registered through the combat scope.
- `Abundance` uses combat-card-generation RNG, a player choice, a generated
  Power card, and `SetToFreeThisTurn`. The run/combat RNG sets, choice IDs,
  generated card identity, complete card object graph, cost state, and ordered
  piles are captured.

### Neow's Sacrifice quest flow

- `NeowsSacrifice.AfterObtained` procures `Ambergris` and creates a run-scoped
  `Guilty` card in the permanent deck.
- `Guilty` stores `_combatsSeen` as a saved property, updates its dynamic
  variable, and removes itself after the fifth completed combat. Permanent deck
  cards and their dynamic variables are included in model capture. Combat undo
  cannot cross the completed-combat hook; floor restart reloads the vanilla
  room-entry save.
- `Ambergris` heals the selected player and applies hidden `AmbergrisPower`.
  Creature HP, potion slots, potion ownership, power amount, and power identity
  are captured.
- `AmbergrisPower` schedules an extra turn through
  `ShouldTakeExtraTurn` and decrements after that turn. Both the power and
  `CombatManager._playersTakingExtraTurn` are captured, covering snapshots on
  either side of the extra-turn transition.

### Reworked singleplayer-capable cards and relics

- `DiamondDiadem` grants normal block and `BlurPower` on the first turn. The
  creature block, power model, turn number, and relic state are captured.
- `HistoryCourse` now selects only the previous turn's completed non-dupe
  Attack from `CombatHistory`. The exact history entries and referenced card
  identity are restored.
- `Mirage` applies `EnergyNextTurnPower`; power amount and ownership are covered
  by creature power/model capture.
- `WellLaidPlansPower.ShouldFlush` prevents hand flushing. Power presence and
  the exact ordered hand are restored together.
- `Expertise` applies the card field used by single-turn Retain to each drawn
  card. Full card model capture includes that flag, while pile capture preserves
  the draw result and hand order.
- `PillarOfCreationPower` determines its first generated card by querying
  `CardGeneratedEntry` history for the current turn. Combat history is restored
  before card UI refresh.
- `Eidolon` awaits each Ethereal auto-play in sequence. Snapshots are deferred
  until the action queue, choice synchronizer, and card/potion effect depth are
  settled, so a partial auto-play chain is not accepted as a manual restore
  target.
- Card transform, transform-shine, enchant, smith, and upgrade VFX are treated
  as transient restore-time nodes so a delayed or accelerated animation cannot
  leave a stale card above the combat UI.

### Multiplayer-only changes

- `Tutor`, `TheBall`, `ImitationLearning`, `OneForAll`, `Unmovable`,
  `Underworld`, `Hibernate`, `HuddleUp`, and the other changed multiplayer
  cards can contain target-player references, generated clones, internal
  dictionaries, and cross-player piles. The generic object graph preserves
  those identities, but undo, redo, and floor restart are intentionally blocked
  whenever the run is not singleplayer.
- 0.109 allowing most potions to target another player therefore does not add a
  supported singleplayer snapshot path.

### Balance-only changes

The remaining official changes alter canonical numbers, rarity, pools, text,
art, or enemy move selection. They add no mutable snapshot field: Aeonglass,
Torchhead Amalgam, Demon Form, Expect a Fight, Primal Force, Taunt,
Bloodletting, Cruelty, Dominate, Outbreak, Accelerant, Collision Course,
Hyperbeam, Sunder, Trash to Treasure, Midnight, Blade Symphony, Sand Castle,
Meat Cleaver, Toybox, Fiddle, Sere Talon, and Distinguished Cape.

The Soulbound shuffle correction changes the destination pile only. Exact pile
membership and order were already captured.

The Expose/Hand Drill correction changes hook dispatch only and introduces no
new persistent combat field.

### Seed and serialization changes

- The 12-character/larger internal seed change does not affect combat snapshot
  storage because the mod captures the instantiated RNG states rather than
  parsing or reconstructing the display seed.
- Floor restart loads `SerializableRun` through the current vanilla save API,
  so it receives the current seed representation without a mod-owned conversion.
- The `SavedPropertySerializationCache` merge into
  `ModelIdSerializationCache` affects model hashing and serialization metadata,
  not the in-memory model fields captured by undo.

## API and reflection review

All direct Harmony targets compile against 0.109, including input settings,
combat reset, action completion, card and potion actions, end turn, mod
settings, and Necrobinder VFX hooks.

The private members used for gameplay restoration still exist in 0.109:

- Combat creatures, escaped creatures, card registry, and next creature ID.
- Action ID, hook ID, choice IDs, waiting actions, hook actions, and received
  choices.
- Player energy, stars, turn phase, pets, potions, relics, and orb queue.
- Combat room extra rewards, pre-finished state, and gold proportion.
- Creature powers, monster move state, combat history, and run history.
- Card play queue, hand holders, pile counters, relic holders, potion holders,
  creature nodes, and navigation refresh methods.

The `SavedPropertySerializationCache` merge into
`ModelIdSerializationCache` does not affect the mod's in-memory object graph
snapshot. Floor restart delegates serialization to the current game version.
Because the mod defines no gameplay models and has `affects_gameplay=false`,
its code does not add model IDs or saved properties to multiplayer hashes.

## Remaining runtime QA

Static analysis cannot validate animation timing or every asynchronous hook.
The following scenarios are the highest-value in-game checks for 0.109:

1. Enter the fifth unknown room as a combat with Dowsing: confirm the permanent
   deck contains Abundance while that combat still contains Dowsing, then undo
   and redo without changing either identity.
2. Undo and redo after Abundance creates a free Power card, then play that card.
3. Use Ambergris, undo and redo its heal/potion/power state, then cross its extra
   turn in both directions and end the turn.
4. Complete the fifth Guilty combat, restart the room, and confirm the permanent
   quest counter/removal follows the vanilla room-entry save exactly once.
5. Undo and redo after Eidolon auto-plays multiple Ethereal exhaust cards.
6. Trigger History Course and Pillar of Creation, restore, and trigger them
   again without duplicate history effects.
7. Undo Mirage and Expertise and confirm next-turn Energy and single-turn Retain
   return to their exact prior values.
8. Undo immediately after normal and quick exhaust animations begin; no card or
   exhaust VFX should remain floating.
9. Use undo repeatedly around a multi-enemy death and confirm targeting,
   rewards, and the next turn transition still work.
10. Undo after a card play and confirm the hand is in `Play` mode, peek mode is
   closed, no holder is awaiting play, and mouse and shortcut input both work.

## Result

The renewed feature-flow audit found one uncovered invariant: during the
Dowsing-to-Abundance transition, `CardModel.CardScope` falls back to the owner's
active combat state even though Dowsing is in the permanent deck. The new
Abundance can therefore be registered in `CombatState._allCards` while being
placed in the permanent deck. Snapshot capture now moves permanent-deck card
registration to the run scope generically rather than special-casing Abundance.

No other official 0.109 gameplay change requires a per-card or per-relic restore
path. All remaining mutable state maps to the card model graph, creature powers,
ordered piles, combat history, RNG sets, potion slots, or turn-transition fields
already captured by the current implementation.
