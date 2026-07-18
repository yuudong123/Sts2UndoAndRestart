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
  -> Captures snapshots only at stable player-control boundaries

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
6. If a new action is taken while a redo branch exists, snapshots and action-history entries after the current cursor are removed.

## Restore Flow

`UndoRedoManager` captures the current state as a rollback snapshot immediately before calling `CombatSnapshot.Restore`. If the target restore or validation fails, it restores that rollback snapshot.

`CombatSnapshot.Restore` restores state in this order:

1. Clears transient card VFX, such as cards floating in the center of the screen.
2. Restores creature lifecycle state and combat participant lists.
3. Restores run state and model field snapshots.
4. Restores combat fields, player state, card piles, potions, relics, and orbs.
5. Restores combat history and run history.
6. Sends card UI refresh notifications and clears transient relic activation display state.
7. Refreshes UI and runs snapshot validation.

The restore process intentionally reuses live objects instead of using the game's normal save/load path. Because of that, Godot nodes, Spine animations, card play nodes, potion holders, relic holders, and other UI state need explicit cleanup and refresh logic.

## F5 Floor Restart Flow

`FloorRestartService` reloads the current room from the current singleplayer run save.

- It does not run in multiplayer.
- It does not run while a game action is pending.
- Finished extra rewards are preserved when restarting from a completed reward screen.
- Unfinished extra rewards created during an active combat are cleared before restarting.
- The current run scene is cleaned up, then the saved room is loaded again.

## Settings and Input

- Config path: `OS.GetUserDataDir()/mod_configs/UndoAndRestart.json`
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
- Card cost and runtime state: cost-changing effects, this-turn-only costs, and card UI refresh should restore together.
- Relic stack display: restored model values and UI display values should refresh to the same state.
- Rewards and run history: F5 restart and undo should not leave accumulated changes in statistics, damage records, or reward calculations.
