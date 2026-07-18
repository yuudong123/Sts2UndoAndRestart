# C# File Specification

## Entry Points and Patches

| File | Responsibility |
| --- | --- |
| `MainFile.cs` | Mod initialization entry point. Registers Harmony patches, loads config, and subscribes to combat events. |
| `UndoRedoPatches.cs` | Central Harmony patch collection. Handles input, action boundary detection, input settings injection, and action-history entry creation. |
| `ModSettingsPanelPatch.cs` | Adds the snapshot limit and action-history visibility settings UI to the mod info screen. |
| `NecrobinderVfxSafetyPatches.cs` | Patches `NNecrobinderVfx` head visibility and scythe flame callbacks so disposed Godot nodes do not throw during restore cleanup. |

## Snapshot Engine

| File | Responsibility |
| --- | --- |
| `UndoRedoManager.cs` | Owns the snapshot stack and cursor. Handles capture eligibility, undo/redo movement, turn-transition snapshots, and action-history linking. |
| `ObjectGraphSnapshot.cs` | Deep-clones and restores an arbitrary object's field graph through reflection. It also discovers nested mutable `AbstractModel` instances and restores `Rng`, `CardEnergyCost`, `DynamicVarSet`, and collections. |
| `CombatSnapshot.cs` | Core combat snapshot implementation. Captures and restores creatures, players, models, cards, piles, potions, relics, orbs, combat history, and UI state, then sends card UI refresh notifications. |
| `RunStateSnapshot.cs` | Stores and restores run-state fields that can be affected during combat. |
| `RunHistorySnapshot.cs` | Stores and restores run history so undo/restart does not accumulate incorrect damage or statistic records. |
| `CombatVisualSnapshot.cs` | Stores and restores creature positions, visibility, and visual animation state. |
| `SnapshotValidator.cs` | Validates hand holders, piles, combat card lists, and targeting after restore, triggering rollback when an invariant fails. |

## UI and Input

| File | Responsibility |
| --- | --- |
| `ActionHistoryOverlay.cs` | Builds and renders the top-right action history tab. Handles card/potion images, turn separators, current snapshot display, hover effects, and click-to-restore behavior. |
| `UndoInputBindings.cs` | Registers undo/redo/restart actions in the game's input settings and connects user-defined bindings with fallback default keys. |
| `UndoText.cs` | Provides Korean, English, and Chinese UI strings based on the current game language. |
| `UndoAndRestartConfig.cs` | Loads and saves the snapshot limit and action-history overlay visibility settings. |

## Restore Safety Helpers

| File | Responsibility |
| --- | --- |
| `CombatRuntimeStateCleanup.cs` | Clears leftover runtime flags, action blockers, and turn-ending state after restore or F5 restart. |
| `TransientCardVfxCleanup.cs` | Removes temporary card nodes and selection tweens left by card play or card generation animations. |
| `SovereignBladeVfxSync.cs` | Keeps Sovereign Blade-style floating VFX synchronized with the restored card count. |
| `ParkedCreatureNodeRegistry.cs` | Temporarily hides and removes `NCreature` nodes from the combat room lists, then restores them when the creature becomes active again. |
| `ReflectionUtil.cs` | Centralizes private field and method access so reflection failures are easier to audit after game updates. |

## F5 Restart

| File | Responsibility |
| --- | --- |
| `FloorRestartService.cs` | Reloads the current room from saved run data. Handles combat, event, and reward-screen restart behavior, including reward preservation and cleanup rules. |

## Data Types

| Type | Responsibility |
| --- | --- |
| `ActionHistoryEntry` in `UndoRedoManager.cs` | Action-history item shown in the overlay. Stores the action type, target snapshot index, and turn number. |
| `ActionHistoryEntryKind` in `UndoRedoManager.cs` | Distinguishes card, potion, potion discard, and turn-transition entries. |
| `RuntimeBlockerKind` in `CombatRuntimeStateCleanup.cs` | Categorizes runtime blockers for logging and recovery decisions. |

## Maintenance Rules

- When adding a new snapshot field, keep its capture and restore logic close together when possible.
- Prefer `ReflectionUtil` instead of scattered direct reflection calls.
- Put new UI text in `UndoText`.
- Put new persisted settings in `UndoAndRestartConfig` with load, save, and default-value handling together.
- Any feature that mutates combat or run state must check multiplayer blocking first.
