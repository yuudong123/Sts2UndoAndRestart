# STS2 0.110 Snapshot Audit

## Scope

This audit targets the STS2 `0.110.0` runtime. It compares the current game
assembly with the previous supported build and records the compatibility work
that directly affects Undo And Restart.

## Compatibility Changes

- Updated combat turn-state access to the `0.110.0` runtime model.
- Updated the input-settings injection path for the current mouse/keyboard and
  keyboard-only input maps.
- Rechecked reflected combat fields, action queues, card piles, player choices,
  relic state, potion state, orb state, creature lifecycle state, and run
  history against the current assembly.
- Rechecked temporary card and creature VFX cleanup against the current node
  types.

## Card-Selection Checkpoints

- Manual card-selection screens can be captured as optional checkpoints.
- Automatic selections that do not ask the player for confirmation are skipped.
- A restored selection screen is reconstructed from its pre-action snapshot by
  replaying only the action that created it.
- Captures are suppressed during replay so the existing undo/redo branch cannot
  be truncated by synthetic action boundaries.
- Turn-start hook choices are replayed in their original order, followed by a
  separate stable turn-start snapshot.

## Static Verification

- The project builds against the installed `0.110.0` runtime with zero warnings
  and zero errors.
- All user-facing mod strings are present in English, Korean, Japanese,
  Simplified Chinese, and Traditional Chinese language files.
- Production identity, config path, assembly name, and manifest ID remain
  `UndoAndRestart`, preserving existing settings and Workshop subscriptions.

## Runtime QA

- Play, undo, and redo targeted and untargeted cards.
- Use and undo card-selection cards and card-selection potions.
- Verify sequential start-of-turn choices such as multiple relic or power
  prompts.
- Verify one-card automatic selections do not block navigation.
- Verify rapid undo/redo across adjacent selection checkpoints preserves the
  screen dim and card ownership.
- Verify F5 restart from combat and reward screens.
- Verify multiplayer blocks undo, redo, and restart while allowing the mod to
  remain installed.
