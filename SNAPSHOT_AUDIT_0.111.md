# STS2 0.111 Snapshot Audit

## Scope

This audit targets the STS2 `0.111.0` public-beta runtime, Steam build
`24724944`, game commit `41cef1ea`. It records compatibility work that directly
affects Undo And Restart.

## Compatibility Changes

- Updated card-choice action-queue cleanup for the `0.111.0` queue layout.
  `ActionQueue.isCancellingPlayCardActions` was replaced by
  `ActionQueue.actionCancellingPlayCardActions`.
- Kept the cleanup path compatible with both the `0.110.0` boolean marker and
  the `0.111.0` action-reference marker.
- Removed compile-time access to the now-internal parameterless
  `CombatManager.EndPlayerTurnPhaseOneInternal` method while preserving the
  existing Harmony patch and turn-end choice replay behavior.

## Card-Selection Interruption Fix

- Rapid undo while a card-selection action is paused no longer aborts cleanup
  with a missing-field exception.
- The interrupted action is canceled, removed from its player queue and action
  executor, and its player-choice resumption state is cleared before restoring
  a snapshot.
- The fix is implemented in the shared card-choice interruption path. It covers
  hand selection, hand upgrade, combat-pile selection, simple-grid selection,
  generated-card selection, and card-selection potions rather than special
  casing Brand.
- Snapshot VFX cleanup now queues the quick-exhaust card parent before its
  child VFX. This avoids re-entering Godot ancestor removal from
  `NCardExhaustQuickVfx._ExitTree` when undoing near the exhaust animation.

## Static Verification

- The project builds against the installed `0.111.0` runtime with zero warnings
  and zero errors.
- The release keeps the production identity, config path, assembly name, and
  manifest ID `UndoAndRestart`.
- The release manifest version is `0.111.0.1`.

## Runtime QA

- Reproduce the old failure with Brand by pressing undo immediately after its
  exhaust selection and before the selected card finishes moving.
- Verify the fixed build restores the prior checkpoint without leaving Brand in
  the play area, disabling card play, or crashing during quick-exhaust cleanup.
- Repeat with hand discard/exhaust, hand upgrade, combat-pile, generated-card,
  and potion selection actions.
- Verify rapid undo/redo across adjacent selection checkpoints.
- Verify a normal card play, targeted card play, potion use, end-turn choice,
  and floor restart still work.
