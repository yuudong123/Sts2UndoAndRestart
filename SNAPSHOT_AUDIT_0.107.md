# Undo And Restart snapshot audit for STS2 0.107

## Scope

This audit is based on the current 0.107 `sts2.dll` decompilation at:

`D:\Study\sts2\.tools\sts2-0107-decompiled`

The following model groups were checked:

| Group | Files checked |
|---|---:|
| Cards | 578 |
| Relics | 298 |
| Potions | 64 |
| Powers | 260 |
| Orbs | 5 |
| Enchantments | 23 |
| Afflictions | 7 |
| Monsters | 121 |

The audit also covers `CombatState`, `CombatManager`, `RunState`, `CombatRoom`,
`Player`, `Creature`, action queues, player choices, combat history, run history,
encounters, modifiers, badges, monster move state machines, RNG objects, and
combat UI nodes.

The goal is not to serialize the whole Godot scene. A correct snapshot must
restore the complete gameplay model and then rebuild the view from that model.

## Snapshot boundary

A user-restorable snapshot must only be captured at a quiescent player-control
boundary:

- The combat is in the player play phase.
- The action queue is empty.
- The action executor is idle and has no current action.
- No card or potion effect is executing.
- No player choice is open or waiting for a response.
- Turn-start hooks, draw actions, powers, relics, and queued follow-up actions
  have all finished.
- The player can actually select and play a card.

Do not capture a playable snapshot inside `PlayCardAction.Execute`, immediately
after only the parent task completes, or while its child actions are still in
the queue. A card or potion use is one transaction. Its post-action snapshot is
the next quiescent boundary after all resulting actions and hooks settle.

This rule is required for forced turn-ending cards, turn transitions, chained
effects, generated cards, delayed powers, and cards whose values are calculated
from combat history.

## Required gameplay state

### Combat state

Capture and restore:

- `CombatState.RoundNumber`
- `CombatState.CurrentSide`
- `CombatState._nextCreatureId`
- Ordered `CombatState._allies`
- Ordered `CombatState._enemies`
- Ordered `CombatState._escapedCreatures`
- `CombatState._allCards`, including exact object identity and order
- Encounter model state and encounter RNG
- Every combat modifier model
- Every combat badge model
- Multiplayer scaling model state when present

Known modifier and badge state that must not be omitted:

- `Hoarder._cardsToSkip`, a readonly mutable `HashSet<CardModel>`
- `CharacterCards` model state
- `CccComboModel._cardsPlayedThisTurn`

Readonly does not mean immutable. Readonly collections must have their contents
captured and restored in place.

### Combat manager

At a playable boundary, transient execution state should normally be normalized
rather than copied blindly. The snapshot still needs enough semantic state to
restore the same turn:

- Players taking an extra turn
- Players ready to end their turn, normally empty at a playable boundary
- Players ready to begin the enemy turn, normally empty
- Player-actions-disabled state
- Enemy-turn-started and turn-ending phase flags
- Pause state if restoration while paused is supported
- Pending-loss state, which must be absent for a playable snapshot
- Card-or-potion effect depth, which must be zero
- Deferred end-turn transition, which must be absent
- Player-turn-setup flag, which must be false

Do not always clear `_playersTakingExtraTurn`. Cards check this list to decide
whether the current turn is an extra turn.

### Action and choice synchronization

Playable snapshots require empty pending work, but sequence counters still need
to be restored:

- `ActionQueueSet._nextId`
- `ActionQueueSynchronizer._nextHookId`
- `PlayerChoiceSynchronizer._choiceIds`
- Reward/selection synchronizer counters used by the active combat

The following collections must be empty before capture:

- Action queues
- Actions waiting for resumption
- Hook actions
- Requested actions waiting for the player turn
- Received or pending player choices

Never snapshot live `Task`, `CancellationTokenSource`, completion source,
delegate, or running `GameAction` objects as restorable state.

### Combat room

Capture:

- `CombatRoom._isPreFinished`
- `CombatRoom.GoldProportion`
- `CombatRoom._extraRewards` for every player
- Reward payloads as independent serialized values

`_extraRewards` is modified during combat by effects including The Hunt,
Heist, Royalties, Forbidden Grimoire, and Swipe. Omitting it causes duplicate
or retained rewards after undo/restart.

Do not rely on serializing the entire room object. Event-parent combat rooms can
have room serialization restrictions. Snapshot the fields and each reward
payload directly.

### Run state

Capture:

- `RunState._allCards`
- Run RNG set
- Run odds set if any current combat effect can mutate it
- Shared relic grab bag
- `RunState.ExtraFields`
- `ActFloor`
- `NextRoomId` if an in-combat effect or restart path can change it
- Visited map coordinates
- Map point history
- Visited event IDs
- Current act index only if restoration is allowed across an act mutation

`RunState._allCards` is a critical identity registry. Restoring a card to
`Player.Deck` without restoring this registry leaves a visible card that the
run does not consider to exist. Removing a generated or permanently removed
card only from a pile leaves a leaked registry entry.

`ExtraRunFields.TestSubjectKills` must be restored. It increments when a Test
Subject dies and affects run state independently of the visible monster.

### Player state

Capture for every player:

- Gold
- Current and maximum HP through the player creature
- `IsActiveForHooks`
- `ExtraPlayerFields`
- Max energy
- Base orb slot count
- Potion-slot count and ordered slot contents, including null slots
- `CanRemovePotions`
- Ordered relic inventory
- Player relic grab bag
- Player RNG set
- Player odds set if mutable during combat
- Player combat state
- Every combat and run pile
- Discovered lists if exact run-local state is required

`ExtraPlayerFields` currently contains:

- Card shop removals used
- Wongo points
- CCCombo badge unlocked
- Damage dealt
- Debuffs applied

If these are not restored, repeatedly undoing damage makes the final run record
report damage that did not remain in the timeline, and badge/progression checks
can also disagree with the restored combat.

`IsActiveForHooks` is required when undoing death, revive, escape, or phase
changes. A living player with deactivated hooks is not equivalent to the same
visible HP value.

Global profile discovery/save progress is outside the combat transaction unless
the mod deliberately delays those writes. Run-local discovered lists can be
restored, but already-flushed profile saves cannot safely be undone.

### Player combat state

Capture:

- Energy
- Stars and any other character combat resource
- Turn number
- Player turn phase
- Hand draw/discard/exhaust/deck combat piles
- Permanent deck pile
- Pets and pet ordering
- Orb queue and capacity
- Character-specific mutable fields

Every pile needs:

- Exact ordered card references
- Pile type and any pile-local state
- Correct owner
- Correct combat/run registry membership
- Correct state-tracker subscription

Replacing the private list directly is insufficient. `CardPile.AddInternal` and
`RemoveInternal` manage state-tracker subscriptions. Restored cards must end
with exactly one valid subscription and cards outside all piles must have none.

### Creature state

Capture for every player, pet, active monster, and escaped monster:

- Current HP
- Maximum HP
- Block
- `MonsterMaxHpBeforeModification`
- Combat ID
- Combat side
- Combat-state attachment
- Slot name
- HP display mode
- Pet owner
- Ordered power list
- Alive/dead state as represented by HP plus hook activation

Power owner references and creature relationships must retain identity. Do not
clone a second copy of a creature or card referenced by another model.

### Monster state

Capture:

- All mutable fields declared by each monster model
- Monster RNG and run RNG reference
- Spawned-this-turn state
- Performing-move state
- Next move
- Complete move state machine
- Current and initial move state
- Per-state performed flags
- State log
- Intents
- Must-perform-once flags
- Follow-up state
- Random/conditional branch state that can change after setup

The generic model capture covers direct primitive fields only if it is made
deep and readonly-aware. Important monster families include:

- Test Subject phase, respawn, and kill state
- Queen and amalgam phase relationships
- Tunneler burrow state
- Waterfall Giant phase and damage state
- Vantom transition state
- Cubex Construct and Soul Nexus death/listener state
- Slumbering Beetle sleep/death state
- Multi-part bosses and summoned creature references

Creature removal during restore must not call destructive vanilla room
lifecycle methods. `CombatManager.RemoveCreature` invokes monster cleanup and
resets state machines. Re-adding the same model later does not safely recreate
all event subscriptions, and calling `AfterAddedToRoom` again can duplicate
initial powers.

Use a snapshot-specific detach/attach path that:

- Updates combat collections and UI without resetting the model.
- Preserves existing model event subscriptions.
- Reconciles StateTracker membership.
- Creates or removes only the presentation node.

### Card state

Capture the complete base `CardModel` state:

- Owner and registry identity
- Energy cost and every temporary cost modifier
- Star cost and modifiers
- Upgrade level and downgrade state
- Enchantments
- Afflictions
- Exhaust, retain, sly, innate, ethereal, and other runtime flags
- Targeting state and target type
- Play index and replay state
- Clone/duplicate provenance
- Deck version
- Tags and keywords when mutable
- Dynamic variables
- Any per-combat or per-turn value

`CardEnergyCost` must be deep-cloned. In particular, clone the local temporary
modifier list and preserve duration semantics such as this turn, until played,
and this combat. This covers Madness-style potion effects and generated
zero-cost cards.

Card classes with explicit mutable gameplay fields found in 0.107:

- Claw: extra damage from plays
- Discovery: mocked selected card
- Genetic Algorithm: current and increased block
- Guilty: combats seen
- Kingly Punch: extra damage
- Mad Science: Tinker Time type/rider and mocked chaos card
- Maul: extra damage from plays
- Rampage: extra damage from plays
- Regret: cards in hand
- Sovereign Blade: current damage, repeats, and forged origin
- Splash: mocked generated card
- Spoils Map: spoils act index
- The Scythe: current and increased damage
- Thrash: extra damage
- Up My Sleeve: times played this combat
- Wither: fake upgrade level

This list is not permission to hard-code only these cards. Future cards and
inherited fields must be captured by a general model-graph snapshot.

Many cards derive their current value from `CombatHistory`, not a card field.
Examples include Death's Door, Evil Eye, Fetch, Flatten, Forgotten Ritual,
Normality, Finisher, FTL, Helix Drill, Lunar Blast, Make It So, Pinpoint, and
Stomp. Correct card restoration therefore requires correct history restoration.

### Power state

Capture the complete `PowerModel` base state:

- Owner
- Amount and type
- Stack/removal state
- Dynamic variables
- Any turn/combat flags
- Internal data object

At least 33 powers store behavior in a private nested `Data` object. A
memberwise clone is not enough when that object owns collections.

Collection-bearing internal power state found in 0.107 includes:

- Afterimage: card-to-count dictionary
- Calamity: dictionary state
- Dampen: creature set and card dictionary
- Gravity: dictionary state
- Hellraiser: card set, counters, and flags
- Intercept: creature list
- Monologue: dictionary state
- Oblivion: dictionary state
- Possess Speed: dictionaries
- Possess Strength: dictionaries
- Rupture: dictionary state
- Serpent Form: dictionary state
- Storm: dictionary state
- Strangle: dictionary state
- Subroutine: dictionary state

Other internal data holders with primitive or identity-reference state include
Adaptable, Automation, Chains of Binding, Curl Up, Dark Embrace, Feral,
Gigantification, Hardened Shell, Illusion, Juggling, Nightmare, Orbit,
Outbreak, Panache, Reattach, Skittish, Vigor, and Void Form.

All internal data must be recursively copied while card/creature/model
references remain references to the original registered objects.

### Relic state

Capture:

- Owner
- Stack count
- Melted/wax/status state
- Removed state
- Dynamic variables
- Floor obtained and other base metadata
- Every derived counter, flag, remembered target, and collection

Examples whose values visibly depend on mutable counters include Brilliant
Scarf, Diamond Diadem, Kunai, Shuriken, Pen Nib, Pocketwatch, Rainbow Ring,
Velvet Choker, and many other per-turn/per-combat relics.

Relic collections requiring real deep container copies include:

- Bing Bong: card set
- Pael's Tooth: serializable-card list
- Unsettling Lamp: power list
- Archaic Tooth and Touch of Orobas: hover-tip lists, presentation only

Directly replacing `Player._relics` is unsafe. `Player.AddRelicInternal` and
`RemoveRelicInternal` maintain owner state, removal state, flash event
subscriptions, and inventory events. Restoration needs a silent reconciliation
path that produces exactly one flash subscription per equipped relic without
re-running obtain/remove gameplay effects.

Capture both the player relic grab bag and shared run relic grab bag. Obtaining
a non-stackable relic can remove it from both.

### Potion state

Capture:

- Exact slot ordering and empty slots
- Owner
- Removed/used state
- Dynamic variables
- Every derived mutable field

Snecko Oil currently has a direct test/override field; future potion fields
must be covered generically.

Potion UI can be rebuilt after slot restoration. Do not replay potion
procurement or use effects merely to rebuild the slots.

### Orb state

Capture:

- Ordered orb instances
- Orb capacity
- Owner
- Dynamic variables
- Base passive/evoke state
- Derived orb fields

Known derived mutable fields:

- Dark Orb: evoke value
- Glass Orb: passive value

Rebuild orb nodes from the restored queue rather than preserving active tween
objects.

### Enchantments and afflictions

Capture all base and derived model fields, owner/card references, dynamic
variables, and stack/usage state.

Known mutable enchantment fields:

- Glam: used-this-combat flag
- Momentum: extra damage
- Slither: override state

No additional direct affliction fields were found in 0.107, but afflictions
still participate in card identity and model state and must remain in the
generic graph policy.

### Encounter state

Capture:

- Encounter RNG
- Generated monster lists and encounter-owned mutable collections
- Every derived encounter field

Known derived examples:

- Battleworn Dummy: setting and ran-out-of-time state
- Gremlin Merc Normal: gold-was-stolen state

Encounter state is currently easy to miss because it is not a card, relic,
power, or creature model, but it can affect subsequent combat behavior and
rewards.

## History and derived values

### Combat history

Capture every history entry as an independent immutable DTO:

- Entry type
- Player turn numbers
- Card play and resource-spend data
- Energy spent
- Damage and block results
- Creature targets
- Monster move entries
- Power/relic/potion hook entries
- Turn boundary entries

Do not copy only the list of existing entry objects. `DamageResult` remains
mutable after construction, and some entries hold mutable result collections.
`MonsterPerformedMoveEntry.Targets` should be materialized rather than retaining
a potentially deferred enumerable.

Preserve identity references to cards, creatures, powers, relics, and players,
but copy all value/result containers.

Helix Drill calculates damage from `EnergySpentEntry` values in the current
turn. A missing, shared, or incorrectly truncated history explains why it can
deal no damage after undo.

### Run history

Deep-copy the entire current map-point history, including:

- Room and monster entries
- Turns taken
- HP, healing, max-HP, damage, gold, and current-gold values
- Cards gained/removed/transformed/upgraded/downgraded/enchanted
- Relic and potion choices/removals/uses
- Event, rest-site, shop, ancient, and quest choices
- Stolen loot

The current clone omits the 0.107 `PlayerMapPointHistoryEntry.StolenLoot` field.
This must be added. Prefer a serializer-backed deep clone so newly added fields
cannot silently disappear after future game patches.

## Deep-copy policy

The snapshot graph needs a visited-object map so shared references stay shared
and cycles do not recurse forever.

Copy by value:

- Primitive values, enums, strings, IDs, vectors, colors
- RNG seed/counter state
- Mutable structs and result DTOs
- Lists, dictionaries, sets, queues, and arrays
- Nested helper/data objects
- Dynamic variable sets
- Card energy/star cost objects and modifier entries
- Serializable cards, rewards, history entries, and move logs

Preserve identity and snapshot separately:

- `Player`
- `Creature`
- `CardModel`
- `PowerModel`
- `RelicModel`
- `PotionModel`
- `OrbModel`
- `MonsterModel`
- Encounter/modifier/badge models

Never clone as gameplay state:

- Godot `Node`, `GodotObject`, `Tween`, and scene resources
- Delegates and events
- Tasks, completion sources, cancellation tokens
- Logger and service singletons
- Action executor instances
- Audio playback objects

For readonly mutable fields, restore the existing collection contents rather
than assigning a new field value.

The current `ReflectionUtil.CloneValue` is structurally insufficient because:

- Dictionaries copy keys and values shallowly.
- Lists only memberwise-clone their immediate items.
- Nested core helper objects are only memberwise-cloned.
- Readonly fields are skipped entirely.
- Whole namespaces are treated as identity references, including mutable helper
  objects that are not model identities.

## Restore order

Use one guarded transaction:

1. Block player input and new snapshot capture.
2. Verify the current combat and target snapshot identity.
3. Verify or cancel transient UI tweens and selections.
4. Non-destructively detach creatures/cards/relic nodes that do not exist in
   the target.
5. Restore run RNG, odds, registries, grab bags, extra fields, and run history.
6. Restore combat room rewards and encounter/modifier/badge state.
7. Restore combat creature/card registries and ordered side lists.
8. Restore player core, inventory, piles, pets, resources, and orb queues.
9. Restore creature, monster move, card, power, relic, potion, orb,
   enchantment, and affliction graph state.
10. Reconcile owners, combat attachments, pile membership, hook activation,
    StateTracker subscriptions, and relic flash subscriptions.
11. Restore combat history and action/choice sequence counters.
12. Normalize CombatManager to an idle playable boundary while preserving
    semantic extra-turn state.
13. Rebuild combat UI from the restored model.
14. Run invariants. If any invariant fails, log the exact object and refuse to
    resume input.

## UI and animation policy

Do not treat arbitrary node transforms and alpha values as authoritative
gameplay state. Capturing a health bar during a fade or tween is how an
invisible health UI becomes a permanent snapshot value.

Rebuild from model:

- Creature HP and block UI
- Power icons and counters
- Intent UI
- Hand, draw pile, discard pile, exhaust pile, and deck counts
- Potion slots
- Relic inventory
- Orb nodes
- Card hitboxes and mouse interaction
- Targeting state

Preserve only semantic visual state that cannot be derived from the model:

- Every active Spine animation track, not just track zero
- Animation name, time, loop, and applicable speed/mix state
- Monster body/default scale and semantic position
- Skin, attachment, palette, or phase presentation
- Special per-monster visual state

Known special visual adapters are needed for Sovereign Blade orbiting blades,
Test Subject phase visuals, Tunneler burrow presentation, Soul Nexus secondary
animation track, Queen/multipart bosses, sleeping enemies, Flyconid spores,
Vantom transition scale, and Crusher/Rocket boss backgrounds.

On restore:

- Kill transient attack/card/tween VFX.
- Stop stale attack animation loops.
- Apply model state.
- Recreate the final semantic animation directly, without replaying the action.
- Re-enable hitboxes only after the view is coherent.

`Rng.Chaotic` is used for cosmetic variation such as dialogue, shakes, and some
skins. It should not be part of deterministic gameplay snapshots.

## Current implementation: P0 defects

These can directly produce incorrect gameplay:

1. `Player.Gold` is not restored.
2. `Player.ExtraFields` is not restored.
3. `RunState.ExtraFields`, especially Test Subject kills, is not restored.
4. `Player.IsActiveForHooks` is not restored.
5. `RunState._allCards` is not restored.
6. `CombatRoom._extraRewards` is not restored.
7. Encounter, modifier, and badge model state is not captured.
8. `Creature.HpDisplay`, pet owner, and monster pre-modification max HP are not
   fully covered.
9. Readonly mutable model fields are skipped.
10. Nested model data and history entries are shallow-copied.
11. Pile replacement bypasses StateTracker subscription management.
12. Relic-list replacement bypasses flash subscription management.
13. Creature removal invokes destructive monster lifecycle cleanup.
14. Re-added monsters do not safely regain custom listeners/setup.
15. Post-card snapshots can be captured before all child actions settle.
16. Extra-turn membership is cleared instead of restored.
17. Run history omits `StolenLoot`.
18. Action, hook, and choice sequence counters are not part of the snapshot.

## Current implementation: P1 defects

These commonly produce visual or delayed behavioral corruption:

- UI state is copied from transient nodes rather than rebuilt.
- Only one Spine animation track is preserved.
- Inventory UI reconciliation assumes existing holders.
- Card hitbox/selection state can survive a restore.
- Potion/relic/card ownership and removed flags are repaired without a unified
  lifecycle reconciler.
- Reward payloads, serializable cards, and history elements are not guaranteed
  to be independent snapshot values.
- Discovered run-local lists, max energy, base orb slots, potion removal state,
  and relic grab bags are not comprehensively handled.

## Recommended code structure

Split the current monolithic snapshot into these responsibilities:

- `SnapshotBoundary`: decides when a snapshot is legal.
- `CombatSnapshot`: immutable aggregate and restore coordinator only.
- `RunStateSnapshot`: run registries, RNG, odds, history, grab bags, extras.
- `RoomSnapshot`: combat room, encounter, rewards, modifiers, badges.
- `PlayerSnapshot`: inventory, piles, combat resources, extras, hook state.
- `CreatureSnapshot`: creature and monster move state.
- `ModelGraphSnapshot`: recursive, identity-aware model field graph.
- `CombatHistorySnapshot`: independent history DTOs.
- `LifecycleReconciler`: owners, registries, StateTracker, events, attachments.
- `CombatViewRebuilder`: model-driven UI and semantic animation restoration.
- `SnapshotValidator`: pre-capture and post-restore invariants.

Card-, relic-, or monster-specific adapters should be reserved for semantic
view reconstruction or genuinely opaque native state. Gameplay fields should
normally be handled by the general graph snapshot, not an ever-growing list of
hard-coded exceptions.

## Required invariants

Validate after every restore:

- Every card belongs to exactly the expected registries and piles.
- Every card in a run pile exists in `RunState._allCards`.
- Every card in a combat pile exists in `CombatState._allCards`.
- No card exists twice in one pile or unexpectedly in multiple exclusive piles.
- Card owner matches the owning player.
- Every active card has exactly one StateTracker subscription.
- Every equipped relic has the correct owner and exactly one flash subscription.
- Potion slot count and null positions match the snapshot.
- Every power owner matches the containing creature.
- Every active creature appears on exactly one combat side.
- Escaped creatures are not also active.
- Creature combat IDs are unique and below `_nextCreatureId`.
- Pet owner and pet-list relationships agree.
- Monster next move belongs to its restored state machine.
- Combat history references only registered snapshot objects.
- Action queue and player-choice state are empty at a playable boundary.
- Combat manager is not in card/potion effect execution.
- The local player is active for hooks when alive.
- UI pile counts equal model pile counts.
- Every visible card hitbox is enabled only when the card is actually selectable.

## Regression matrix

At minimum, automate or manually repeat these cases in both undo and redo
directions:

- Strike then Defend: undo removes only Defend.
- End turn: redo reaches the next playable turn after all turn-start effects.
- Void Form: forced turn end cannot be undone to a card-playable midpoint.
- Madness potion: potion slot and temporary zero cost both restore.
- Skill/Attack potion: generated card disappears and potion returns.
- Generated zero-cost card: cost duration remains correct.
- Pinpoint/Precision-style dynamic costs: history-derived cost moves both ways.
- Helix Drill: spent-energy history restores after crossing a turn boundary.
- Brilliant Scarf and other use counters: counter moves both ways.
- Sovereign Blade: exactly the correct number of blade models and VFX exist.
- Dark and Glass orbs: values, order, and capacity restore.
- The Hunt: extra rewards do not duplicate.
- Swipe/Thievery: stolen loot and permanent deck changes restore.
- Test Subject phase death: targeting, listeners, kill count, and phase restore.
- Tunneler: burrow model and animation agree.
- Queen/Soul Nexus: secondary animation tracks and phase state restore.
- Player death/revive: hook activation matches HP.
- Gold-changing combat effect: gold and run history restore.
- Relic obtained/removed during combat: inventory, grab bags, and subscriptions
  restore without re-running obtain effects.
- Repeated undo/redo 100 times: no deck loss, duplicate subscriptions, reward
  growth, history growth, or UI disappearance.

## Conclusion

The reliable solution is not more per-card exception code. The core must become
an identity-aware deep model snapshot with lifecycle reconciliation and
model-driven UI reconstruction. Once those three foundations are correct, the
direct mutable fields of all current cards, relics, powers, monsters, and other
models are covered without duplicating their gameplay logic inside the mod.
