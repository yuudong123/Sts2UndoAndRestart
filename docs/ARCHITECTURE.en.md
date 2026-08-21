# Undo And Restart Architecture Specification

## Goal

This mod stores and restores Slay the Spire 2 combat state directly. It does not replay a fight from the beginning like a quick restart system. Instead, it snapshots the live object graph, runtime model fields, and relevant UI state so the game can move back to a previous state immediately.

## Module Overview

```text
MainFile
  -> Registers Harmony patches
  -> Loads configuration
  -> Subscribes to CombatManager events

UndoRedoPatches
  -> Handles hotkeys
  -> Detects game action boundaries
  -> Injects input settings entries

UndoRedoManager
  -> Owns the snapshot stack
  -> Tracks the undo/redo cursor
  -> Serializes navigation and owns the restore execution lock
  -> Invalidates callbacks from abandoned timeline generations
  -> Captures snapshots at stable player-control and optional card-selection boundaries

CardChoiceCheckpointService
  -> Captures manual card-selection checkpoints
  -> Reconstructs selection screens by replaying their originating action

CombatSnapshot
  -> Captures and restores combat, run, card, player, creature, and UI state

ActionHistoryOverlay
  -> Renders the action history UI

FloorRestartService
  -> Handles F5 floor restart
```

## Snapshot Capture Flow

1. When a major action starts, such as playing a card, using a potion, discarding a potion, or ending the turn, `UndoRedoPatches` calls `UndoRedoManager.CaptureBeforeAction`.
2. When the action `Task` completes, `CaptureAfterActionAsync` schedules a capture for the next stable player-control boundary.
3. Stability is checked again from boundaries such as `ActionExecutor.AfterActionFinished`, `ActionQueueSynchronizer.PlayPhase`, and `CombatManager.PlayerActionsDisabledChanged`.
4. Once the game is actually capturable, `CombatSnapshot.Capture` stores the current state.
5. No partial state fingerprint is used. Actions that only change relic counters or mod-owned internal fields still receive independent snapshots.
6. Action completion metadata is keyed by the exact `GameAction` and accepted only from the current timeline generation.
7. Automatic ready callbacks cannot remove a redo branch. A redo branch is truncated only when the player performs a new action or makes a new replayed choice.

When card-selection checkpoints are enabled, manual selection requests create an
additional boundary. Automatic one-card resolutions are skipped. Restoring one of
these checkpoints first restores its pre-action snapshot, then replays only the
originating action until the requested selection is open. Replay-side captures are
suppressed so they cannot fork or truncate the existing timeline.

## Restore Flow

`UndoRedoManager` processes one navigation request at a time. The input callback uses `CallDeferred` to leave scene-tree traversal without waiting for another rendered frame, then locks player input and the hand and pauses the executor and all player queues. It interrupts an active card choice when necessary and waits only while the action runtime genuinely owns work. An additional direction input received during navigation replaces the single queued request instead of being discarded. It then captures a rollback snapshot immediately before calling `CombatSnapshot.RestoreAsync`. If the target restore or validation fails, it restores that rollback snapshot under the same lock.

`CombatSnapshot.RestoreAsync` restores state in this order:

1. Immediately hides and quarantines tracked old-timeline asynchronous card VFX owners and their card nodes. Their tasks finish against the isolated nodes without blocking restore.
2. Clears the remaining transient card VFX and synchronously detaches creature nodes whose old-timeline death tasks require the engine cancellation boundary.
3. Restores creature lifecycle state and combat participant lists. Any needed replacement creature nodes are created only after all model restoration is complete.
4. Restores run state and model field snapshots.
5. Restores combat fields, player state, card piles, potions, relics, and orbs.
6. Restores combat history and run history.
7. Sends card UI refresh notifications and clears transient relic activation display state.
8. Synchronously detaches stale play UI, rebuilds the hand before the same render, and snaps its layout and interaction state.
9. Awaits enemy intent refresh tasks, then runs snapshot validation.

The restore process intentionally reuses live objects instead of using the game's normal save/load path. Because of that, Godot nodes, Spine animations, card play nodes, potion holders, relic holders, and other UI state need explicit cleanup and refresh logic. Pooled nodes must be retired with `QueueFreeSafely`; direct `QueueFree` is forbidden in restore cleanup.

Creature animation playback time is not snapshot state. One-shot track-0 hit, attack, and death motions are discarded and normalized to a stable state. Looping tracks restore only their animation identity. When a one-shot transition already has a loop queued behind it, the final queued loop is captured as the semantic destination instead of preserving the transition frame. Completed persistent poses on higher tracks restore at their semantic endpoint. Position, form VFX, scale, and hue remain restorable presentation values; tween progress and exact Spine frames do not.

Assigning `null` to `NCreature.DeathAnimationTask` does not cancel a running `AnimDie`. Nodes with death work are therefore detached before model mutation. Their `_ExitTree` cancels `DeathAnimCancelToken` and unsubscribes from the live power and combat events. A fresh `NCreature` is projected from the same logical `Creature` after restore, preventing callbacks from the abandoned timeline from hiding its health bar or freeing it later.

## F5 Floor Restart Flow

`FloorRestartService` reloads the current room from the current singleplayer run save.

- It does not run in multiplayer.
- It does not run while a game action is pending.
- Finished extra rewards are preserved when restarting from a completed reward screen.
- Unfinished extra rewards created during an active combat are cleared before restarting.
- The current run scene is cleaned up, then the saved room is loaded again.

## Settings and Input

- Config path: `OS.GetUserDataDir()/mod_configs/UndoAndRestart.json`
- The settings screen also controls action-history visibility and optional card-selection checkpoints.
- Input actions:
  - `undo_and_restart_undo`
  - `undo_and_restart_redo`
  - `undo_and_restart_restart`
- Default fallback keys:
  - Undo: left arrow
  - Redo: right arrow
  - Restart floor: `F5`

If the player binds a key through the game's input settings, that binding takes priority over the fallback key. Undo/redo hotkeys are ignored while the player is typing into console-like controls, `LineEdit`, or `TextEdit`.

## Multiplayer Policy

The manifest uses `affects_gameplay=false` so players can still enter multiplayer lobbies with the mod installed. The gameplay-changing features are blocked in code instead.

- `UndoRedoManager.CanCapture` blocks snapshot capture in normal multiplayer runs.
- `FloorRestartService` blocks F5 restart unless `NetGameType.Singleplayer` is active.
- The action history overlay is shown only in singleplayer or fake-multiplayer-compatible contexts.

## Update-sensitive Areas

- STS2 internal fields: reflected field names and types should be rechecked after game updates.
- Action stabilization timing: card play, card generation, potion use, and end-turn boundaries should still capture only after player control is restored.
- Async VFX ownership: compare every fire-and-forget card VFX task and delayed cleanup against `TransientCardVfxCleanup` after an update.
- Restore locking: verify that synchronizer state changes do not unpause the executor or player queues inside restore.
- Timeline callbacks: action and card-choice callbacks must carry a generation and must not branch from abandoned history.
- Card cost and runtime state: cost-changing effects, this-turn-only costs, and card UI refresh should restore together.
- Relic stack display: restored model values and UI display values should refresh to the same state.
- Rewards and run history: F5 restart and undo should not leave accumulated changes in statistics, damage records, or reward calculations.
