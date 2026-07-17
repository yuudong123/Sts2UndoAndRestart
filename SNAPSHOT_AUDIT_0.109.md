# Undo And Restart snapshot audit for STS2 0.109

## Scope

This audit uses the public-beta `v0.109.0` game assembly (`c12f634d`) and the
official 0.109 patch notes. The retained 0.107 decompilation is used as the
structural comparison baseline because a complete 0.108 assembly is not stored
in this repository.

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

## Compatibility fixes

### RNG state

0.109 replaced the old `Rng.Seed` and `Rng.Counter` reconstruction API with a
stateful `MegaRandom` implementation. The complete RNG state now consists of a
counter and four `ulong` state values exposed through `SerializableRng`.

`ObjectGraphSnapshot.CloneRng` now selects the available API at runtime:

- 0.109 and later: `ToSerializable()` plus the serializable constructor.
- Legacy versions: `Seed`, `Counter`, and the `(ulong, int)` constructor.

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

The player potion lock was renamed from `_canRemovePotions` to
`_canUseOrRemovePotions`. Capture and restore probe both field names and notify
the matching old or new change event, preserving compatibility across game
versions.

### Card VFX cleanup

0.109 introduced `NCardExhaustVfx`, `NCardExhaustQuickVfx`, and
`NCardRemoveVfx`. These nodes can outlive the action briefly and retain their
own `NCard`, so restoring during that delay can leave a stale card on screen.

Restore now removes these nodes from combat UI and global VFX containers. The
new types are recognized by full type name rather than a direct assembly
reference, so older game versions can still load the same mod binary.

## New gameplay state review

### New and reworked cards

- `Abundance` uses combat-card-generation RNG, a player choice, a generated
  card, and a temporary free-this-turn cost. The RNG set, choice counters,
  card registry, card fields, dynamic variables, energy cost, and piles are all
  captured.
- `Eidolon` awaits every auto-play inside its parent `PlayCardAction`. It does
  not enqueue separate manual play actions, so the whole chain remains one
  undo transaction.
- `TheBall` stores cumulative damage in `_extraDamageFromPlays`; generic card
  model capture includes it.
- `PillarOfCreationPower` queries `CardGeneratedEntry` history. Combat history
  is restored before card values and UI are refreshed.
- `WellLaidPlansPower` changes hand flushing through the power model. The power
  and exact ordered hand are both restored.
- New multiplayer-only cards and powers may contain player references and
  readonly dictionaries. Generic model capture preserves model identity and
  restores readonly containers in place. Undo and floor restart remain blocked
  in real multiplayer runs.

### New relic and potion state

- `HistoryCourse` reads the previous turn's completed attack from combat
  history. Restoring history preserves its deterministic replay target.
- `AmbergrisPower` grants an extra turn. Power amount and
  `_playersTakingExtraTurn` are both captured, so undoing across the extra-turn
  boundary restores the semantic turn state.
- `Dowsing` stores `_roomsEntered` as a saved property and changes only when an
  unknown room is entered. Combat undo does not modify this out-of-combat quest
  counter. Floor restart uses the game's room-entry save rather than a combat
  snapshot, so it remains on the vanilla serialization path.
- Diamond Diadem's new block and Blur behavior uses ordinary creature block and
  power state, both already covered.

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

1. Undo and redo after Abundance creates a free power card, then play that card.
2. Undo and redo after Eidolon auto-plays multiple Ethereal exhaust cards.
3. Cross an Ambergris extra turn in both directions and then end the turn.
4. Trigger History Course and Pillar of Creation, restore, and trigger them
   again without duplicate history effects.
5. Undo immediately after normal and quick exhaust animations begin; no card or
   exhaust VFX should remain floating.
6. Restart a combat and an unknown room while Dowsing is in the deck; room
   progress should neither duplicate nor disappear.
7. Use undo repeatedly around a multi-enemy death and confirm targeting,
   rewards, and the next turn transition still work.

## Result

No uncovered 0.109 gameplay field requires a new per-card or per-relic special
case. The compatibility-sensitive changes are centralized in RNG cloning,
runtime guard normalization, potion lock probing, and transient VFX cleanup.

