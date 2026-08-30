# C# File Specification

## Entry Points and Patches

| File | Responsibility |
| --- | --- |
| `MainFile.cs` | Mod initialization entry point. Registers Harmony patches, loads config, and subscribes to combat events. |
| `UndoRedoPatches.cs` | Central Harmony patch collection. Handles keyboard and optional M4/M5 input, action boundaries, input settings, history entries, and registration of asynchronous card VFX ownership. |
| `ModSettingsPanelPatch.cs` | Adds snapshot count, action-history visibility, card-selection checkpoint, and M4/M5 control settings to the mod info screen. |
| `FeatureAnnouncement.cs` | Shows the one-time feature announcement after the main menu becomes ready and persists its acknowledgement. |
| `NecrobinderVfxSafetyPatches.cs` | Patches `NNecrobinderVfx` head visibility and scythe flame callbacks so disposed Godot nodes do not throw during restore cleanup. |

## Snapshot Engine

| File | Responsibility |
| --- | --- |
| `UndoRedoManager.cs` | Owns the snapshot stack, cursor, timeline generations, serialized navigation gate, restore execution lock, turn-transition snapshots, and exact action-history linking. |
| `CardChoiceCheckpointService.cs` | Captures manual card-selection checkpoints, invalidates stale selector generations, interrupts an active selection for timeline navigation, and replays the originating action to reconstruct a restored selection screen. |
| `ObjectGraphSnapshot.cs` | Deep-clones and restores an arbitrary object's field graph through reflection. It also discovers nested mutable `AbstractModel` instances and restores `Rng`, `CardEnergyCost`, `DynamicVarSet`, and collections. |
| `CombatSnapshot.cs` | Core combat snapshot implementation. Quarantines the abandoned presentation generation and restores creatures, players, models, cards, piles, potions, relics, orbs, history, and UI before the next render while preserving the outer execution lock. |
| `RunStateSnapshot.cs` | Stores and restores run-state fields that can be affected during combat. |
| `RunHistorySnapshot.cs` | Stores and restores run history so undo/restart does not accumulate incorrect damage or statistic records. |
| `CombatVisualSnapshot.cs` | Restores creature position and semantic form/loop/terminal-pose state without restoring animation playback time. |
| `SnapshotValidator.cs` | Validates hand holders, piles, combat card lists, and targeting after restore, triggering rollback when an invariant fails. |

## UI and Input

| File | Responsibility |
| --- | --- |
| `ActionHistoryOverlay.cs` | Builds and renders the top-right action history tab. Handles card/potion images, turn separators, current snapshot display, hover effects, and click-to-restore behavior. |
| `UndoInputBindings.cs` | Registers undo/redo/restart actions in the game's input settings and connects user-defined bindings with fallback default keys. |
| `UndoText.cs` | Loads localized UI strings and selects the matching language with English fallback. |
| `UndoAndRestartConfig.cs` | Loads and saves snapshot count, overlay visibility, card-selection checkpoint, M4/M5 control, and announcement acknowledgement settings. |
| `language/*.json` | Contains English, Korean, Japanese, Simplified Chinese, and Traditional Chinese user-facing strings. |

## Restore Safety Helpers

| File | Responsibility |
| --- | --- |
| `CombatRuntimeStateCleanup.cs` | Clears leftover runtime flags, action blockers, and turn-ending state after restore or F5 restart. |
| `TransientCardVfxCleanup.cs` | Tracks async card-VFX owners, immediately hides and quarantines old-timeline nodes, and safely retires the remaining temporary card nodes without violating `NodePool` ownership. |
| `SovereignBladeVfxSync.cs` | Keeps Sovereign Blade-style floating VFX synchronized with the restored card count. |
| `CreaturePresentationLifecycle.cs` | Retires non-rewindable death-animation nodes at the engine cancellation boundary so clean presentation nodes can be created after restore. |
| `ReflectionUtil.cs` | Centralizes private field and method access so reflection failures are easier to audit after game updates. |

## F5 Restart

| File | Responsibility |
| --- | --- |
| `FloorRestartService.cs` | Reloads the current room from saved run data. Handles combat, event, and reward-screen restart behavior, including reward preservation and cleanup rules. |

## Data Types

| Type | Responsibility |
| --- | --- |
| `ActionHistoryEntry` in `UndoRedoManager.cs` | Action-history item shown in the overlay. Stores the action type, target snapshot index, and turn number. |
| `ActionHistoryEntryKind` in `UndoRedoManager.cs` | Distinguishes card, potion, potion discard, card-choice, and turn-transition entries. |
| `RuntimeBlockerKind` in `CombatRuntimeStateCleanup.cs` | Categorizes runtime blockers for logging and recovery decisions. |

## Maintenance Rules

- When adding a new snapshot field, keep its capture and restore logic close together when possible.
- Prefer `ReflectionUtil` instead of scattered direct reflection calls.
- Put new UI text in `UndoText`.
- Put new persisted settings in `UndoAndRestartConfig` with load, save, and default-value handling together.
- Any feature that mutates combat or run state must check multiplayer blocking first.
