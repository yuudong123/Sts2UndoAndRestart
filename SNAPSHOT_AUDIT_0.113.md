# STS2 0.113 Snapshot Audit

## Scope

This audit targets the STS2 `0.113.0` public-beta runtime, Steam build
`26625880`, game commit `16ac0fe3`, `sts2.dll` SHA-256
`a6cc282c52b96ad2e01d5692b1144b33aa8e3ba4e969582f648ecb13b2f751d7`. The previous
audited runtime was `0.111.0`; `0.112` was never published on the beta branch.

The 0.111 build of the mod loads on 0.113 but every restore fails with
`MissingMethodException: NCreature.ToggleIsInteractable` and rolls back, so
undo and redo do nothing until this release.

## Compatibility Changes

- Card selectors moved from static `CardSelectCmd` state into the run:
  `RunManager.Instance.CardSelectorStack` keeps one stack per player plus a
  single local selector. Choice replay now uses `UseSelector(player, ...)` or
  `UseLocalSelector(...)` for turn-hook replay.
- `MonsterModel.IsHealthBarVisible` was renamed `IsInteractable`.
- `NCreature.ToggleIsInteractable` was replaced by
  `SetIsInteractable(on, changeVisibilityImmediately)`. Interactability is now
  node state made of `_interactionAllowed` (gameplay: flee, reattach) and
  `_interactionAllowedBecauseAlive` (cleared by the death animation, set again
  only by a revive animation). Restores capture the first flag with the creature
  visuals and reset the second from the model, so an enemy restored from before
  its death stays targetable.
- `NCreature.OstyScaleToSize` was renamed `ScaleAndPositionOsty`.
- `NPotionContainer.UpdateNavigation` was removed; potion and relic focus
  navigation is rebuilt by the now public `NTopBar.UpdateNavigation`. The old
  reflective call had become a silent no-op.
- `NPlayerHand.Add` now refuses a second holder for the same card model, also
  counting holders that await the play queue, and frees the new node. Hand
  rebuilds retire awaiting holders for restored hand cards first and treat a
  refused node as a restore failure instead of touching a pooled node again.
- `CombatTurnState.PlayersThatCannotUnEndTurn` is new and is cleared with the
  other end-turn sets.
- Keyboard-only navigation follows the player's keyboard-only direction
  bindings (`NInputManager.IsKbOnlyDirectionalKey`) instead of fixed arrow keys;
  the default Left/Right fallback uses the same test.
- `CardModel.ReplayCountChanged` is raised after restore.

## New Gameplay State

- Fool arrows. `PlayerCombatState._arrows` holds `ArrowSlot` objects whose
  status (Retrieved, Away, Destroyed) changes as arrows are shot, impaled and
  dislodged, and `ImpaledPower` stores the same slot objects. The object-graph
  copy cloned the slots, so after any restore the power and the player pointed
  at different slots. `ArrowSlot` is now an identity type; the player's slot
  list, status and `DestroyAfterImpaled` are captured and restored after the
  piles (adding a Shoot card to a combat pile can grant a slot). The arrow bar
  is refreshed through the slot and player events, and arrows stuck in
  creatures are dropped without their retrieve animation and recreated from
  each `ImpaledPower`, the way `ArrowCmd` creates them.

## Timeline Fix

- Ending the turn after an undo kept the old redo branch. The next turn-start
  capture was discarded as being inside redo history, so the new turn had no
  checkpoint and redo jumped back to the abandoned timeline after the enemy
  turn. `EndPlayerTurnAction` now starts a branch like other player actions.

## Reviewed Without Changes

- Every Harmony target resolves with the same signature on 0.113.
- No new fire-and-forget card VFX type holds an `NCard`; the tracked VFX types
  are unchanged. `NCardTrailVfx.FadeOut` is no longer async but still returns a
  task.
- `TweenHelper.AwaitFinished` now resumes with `Killed` when a tween is killed
  instead of waiting forever. Restores kill presentation tweens, so game
  coroutines waiting on them now continue; covered by the headless scenarios.
- `NPlayerHand.UpdateHandDisabledState` runs on every combat state change.
  Covered by the hand-selection replay scenarios.
- New run models (`RunModels`, `Dominion`) hold no combat state.

## Verification

- `dotnet build -c Release` against the 0.113 runtime: zero warnings, zero errors.
- All 204 Harmony targets and reflected member names were checked against the
  0.113 decompile; only `NPotionContainer.UpdateNavigation` had disappeared.
- 19 headless combat scenarios (undo/redo round trips, exhaust and power VFX in
  flight, hand and upgrade choices with replay, extra turns, enemy death
  animation, end turn and RNG replay, potions, Fool arrows, three seeded random
  sessions) pass on 0.113 with no restore errors in the log. The 0.111 build
  fails all restore scenarios on 0.113.

In-game checks are still required for visuals, rebound hotkeys, M4/M5, F5 floor
restart and multiplayer blocking.
