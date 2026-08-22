# STS2 0.111 Restore Pipeline Audit

## Scope

This audit covers the local STS2 `0.111.0` runtime and the Undo/Redo combat
restore path rebuilt from tag `v0.110.0.2`.

- Game release: `v0.111.0`
- Game commit: `41cef1ea`
- `sts2.dll` SHA-256:
  `0861BFA1DF347538D932F22D580E75420F08082792EB914E53B4882764ACDBE9`
- Mod development version: `0.111.0.3`
- Audit date: `2026-08-22`

The previous `0.111.0.1` quick-exhaust cleanup removed live nodes directly.
That approach is intentionally not reused. The current implementation treats
restore as a serialized transaction and treats asynchronous visual tasks as
temporary owners of their nodes.

Version `0.111.0.3` additionally treats the hand UI as a projection with an
explicit lifetime invariant. Godot nodes queued for deletion are rejected even
while `GodotObject.IsInstanceValid` still returns true, disposed card visuals
are detached without calling back through their dead `CardNode`, and missing
holders are recreated from the restored hand pile before card-choice replay.
The vanilla selection completion source is no longer nulled during restore;
stale confirm-button releases are ignored instead.

## Verified Engine Sequence

### Action execution

`ActionExecutor.ExecuteActions` owns `CurrentlyRunningAction` while a
`GameAction.Execute()` task is incomplete. It raises the action-finished hooks,
clears `CurrentlyRunningAction`, and then obtains the next ready action.

Consequences:

1. A restore must not mutate combat models while `CurrentlyRunningAction` is
   non-null.
2. An empty logical queue is insufficient while `ActionExecutor.IsRunning` is
   true.
3. Pausing the executor does not cancel the current action. It prevents the next
   action from beginning.
4. All player queues must also be paused because queue changes can start the
   executor independently.

### Quick exhaust

`CardPileCmd.GetTweenForCardsChangingPiles` starts
`NCardExhaustQuickVfx.PlayAnimation()` from a tween callback when a card moves
from hand to exhaust. The VFX task is passed to `TaskHelper.RunSafely` and is not
awaited by the card action.

`NCardExhaustQuickVfx.PlayAnimation` then:

1. waits through the anticipation period;
2. sets `_isFinishing`;
3. reparents the VFX;
4. returns the card node through `QueueFreeSafely()`;
5. starts a separate delayed VFX cleanup.

Therefore an action and queue can be settled while the old VFX still owns and
will later free its card node. Rebuilding the hand during that interval permits
the old continuation to free or mutate a newly reused pooled `NCard`.

### Full exhaust

`NCardExhaustVfx.DelayedFree` retains the child card for another two seconds and
then returns both the card and VFX. Unlike quick exhaust's delayed task, this
delayed task still owns the card and must be included in the restore barrier.

### Node pooling

`GodotTreeExtensions.QueueFreeSafely()` checks `IPoolable`, removes the node
safely, and defers `NodePool.Free`. Direct `QueueFree()` bypasses that contract.
All restore-owned retirement of combat cards, holders, potions, relics, orbs,
powers, and Sovereign Blade VFX now uses `QueueFreeSafely()`.

### Creature presentation cancellation

`NCreature.AnimDie` continues after the combat model has changed. It can await
the UI fade, spawn death VFX, and finally call `QueueFreeSafely()` on its owner.
Clearing `DeathAnimationTask` only loses the task reference; it does not stop
that continuation. `NCreature._ExitTree` is the engine cancellation boundary:
it cancels `DeathAnimCancelToken` and removes the creature, power, animator, and
combat-event subscriptions.

Before model mutation, restore now detaches every active or removing creature
node that still owns a death task. After the logical creature and model graphs
are restored, `SyncCreatureNodes` creates a fresh `NCreature` if the target
timeline needs one. Creatures that leave the logical combat are retired instead
of being hidden and retained with live subscriptions.

Spine playback time and tween progress are deliberately excluded from snapshot
state. Track-0 one-shots normalize to idle. Looping tracks restore from their
animation name. If a one-shot transition already has a durable loop queued
behind it, restore captures the final queued loop as the semantic destination;
this covers transitions such as Matriarch `eyes_open -> eyes_open_loop` and
Vantom charge stages. Completed higher-track poses restore at the animation
endpoint. Semantic form VFX, position, scale, hue, health-bar visibility, and
intent visibility are reconstructed without reviving an old animation owner.

### Synchronizer state

`ActionQueueSynchronizer.SetCombatState(PlayPhase)` unpauses player queues.
The snapshot UI reset needs to call it to restore the synchronizer, but doing so
inside restore would otherwise open the transaction. The snapshot now
immediately re-pauses the executor and all player queues and keeps
`CombatManager._playerActionsDisabled` true. Only the outer restore scope
releases these locks.

## Restore Transaction

Every undo, redo, and action-history click follows the same path:

1. Resolve the requested checkpoint by object identity.
2. If another request is active, retain the latest direction/checkpoint request
   in a single serialized slot.
3. Use `CallDeferred` to leave `NGame._Input` SceneTree traversal without
   waiting for another rendered frame.
4. Disable player actions, disable the hand, pause `ActionExecutor`, and pause
   every player queue.
5. If a card choice is active, request selector interruption, cancel its UI and
   action, release a hook action that was canceled before execution began,
   remove its queue/waiting-resumption records, and reassert the lock.
6. Wait up to 240 process frames for the queue, executor, current action,
   resumption list, synchronized action lists, effect depth, and received choice
   state to settle.
7. Capture a rollback snapshot when the live state is not already represented by
   a card-choice checkpoint.
8. Increment the timeline generation and invalidate old async callbacks.
9. Hide and quarantine tracked old-timeline transient card VFX owners and their
   card nodes without waiting for the animation tasks to finish.
10. Clear the remaining transient UI and synchronously detach non-rewindable
    creature presentations at their engine cancellation boundary.
11. Restore combat/run/card state and project any missing creature nodes only
    after model restoration.
12. Synchronously detach obsolete play/holder/card UI and rebuild/snap the hand
    before the same render.
13. Await enemy intent refreshes created by restore.
14. Validate the restored state.
15. Start card-choice replay if the target is a choice checkpoint.
16. Enable the hand and player actions, then unpause executor and queues.

No other restore path is allowed to run in parallel. Failed card-choice replay
recovery uses the same navigation gate and execution lock.

## Timeline Invariants

### Generation ownership

Each player `GameAction` is registered with the current timeline generation at
its execution prefix. Completion callbacks are accepted only if their action is
still registered in that generation.

Every successful restore attempt invalidates the generation before model
mutation. This prevents completion callbacks from the abandoned future from
creating snapshots or history entries after undo.

Card-choice replay has an independent generation. Its selector checks the
generation before and after every awaited user selection. An intentional
navigation interruption is not treated as replay failure.

### Exact pending action association

Pending history entries are keyed by `GameAction`. A completed action consumes
only its own entry. The previous positional rule (`PendingEntries[0]`) could
attach a delayed callback's card metadata to a different action.

### Branching

Automatic `PlayerControlReady` callbacks cannot truncate redo history. While the
cursor is inside redo history, their stale metadata is discarded.

Timeline truncation is allowed only at a real user branch boundary:

- the forced ready snapshot immediately before an actual action;
- the initial or pending turn-start snapshot immediately before an action;
- the completed player-action boundary;
- a new card selection made after replay reaches its target checkpoint.

Action-history clicks retain checkpoint identity instead of relying on an index
that can change when snapshot limits trim the list.

## Transient VFX Ownership

The following asynchronous visual writers are tracked:

- `NCardExhaustQuickVfx.PlayAnimation`
- `NCardExhaustVfx.PlayAnimation`
- `NCardExhaustVfx.DelayedFree`
- `NCardRemoveVfx.PlayAnimation`
- `NCardTransformShineVfx.PlayUntilCardUpdate`
- `NCardTransformShineVfx.PlayShineAndReveal`
- `NCardTransformShineVfx.AnimatingCardScale`
- `NCardFlyVfx.PlayAnim`
- `NCardFlyPowerVfx.PlayAnim`
- `NCardFlyShuffleVfx.PlayAnim`
- `NCardTrailVfx.FadeOut`
- `NCardEnchantVfx.PlayAnimation`
- `NCardTransformVfx.PlayAnimation`
- `NCardUpgradeVfx.PlayAnimation`
- both `NCardSmithVfx.PlayAnimation` overloads

While any tracked task is active, its owner and owned card are hidden but never
destroyed by restore cleanup. Ownership lookup covers `_card`, `_cardNode`, and
`NCardFlyPowerVfx.CardNode`; it does not assume the VFX is an ancestor of the
card. Ancestor containers are also protected from cleanup. Detached trail VFX
referenced through a live fly/shuffle owner's `_vfx` field are protected even
though they are siblings in the scene tree.

Quick exhaust's delayed task is not tracked after `_isFinishing`: at that point
it only owns the VFX, and its later `QueueFreeSafely()` is valid even if restore
has already retired the VFX. Full exhaust's delayed task remains tracked because
it still owns the card node.

## 0.111 Compatibility Changes

- Card-choice queue cancellation accepts both the old boolean
  `isCancellingPlayCardActions` and the 0.111 action reference
  `actionCancellingPlayCardActions`.
- Turn-end hook replay invokes the now-private
  `CombatManager.EndPlayerTurnPhaseOneInternal()` through an exact reflection
  lookup.
- Harmony patches use the private method name string rather than `nameof`.
- Input settings use the 0.111 field name `commandToLocTitle`.
- Turn-start and turn-end hook choices may be restored from their native
  non-`Play` phase after interruption once the action runtime is empty. A hook
  action canceled while still waiting to execute also cancels its pending
  execution-start signal so rapid navigation cannot strand replay.

## Verification Matrix

Pre-release combat cases were exercised through a local `uar_qa` console
harness. The production assembly excludes that harness; subsequent automated
case setup belongs in the separate development-only QA mod.

Automated/static verification completed:

- `dotnet build -c Release` against the installed 0.111 runtime: zero warnings,
  zero errors.
- `git diff --check`: clean.
- Steam `-applaunch 2868840` startup: mod discovered and initialized.
- Harmony patch startup: no undefined target, missing method, or patch exception.
- Input localization startup: no `KeyNotFoundException`.

Manual combat verification still required after each engine update:

1. Brand/quick-exhaust card: press undo during anticipation, then repeat
   undo/redo rapidly.
2. Full exhaust from hand, play pile, draw pile, and discard pile.
3. First action of turn, then repeated undo/redo (Poke/repeatable-card case).
4. Generated-card choice, chained choices, and branch at the target choice.
5. Turn-start and turn-end hook choices; interrupt each with both directions.
6. Potion use/discard and a potion that opens card selection.
7. Transform, upgrade, enchant, smith, card-fly-power, and shuffle VFX during
   navigation.
8. Undo from the middle of redo history, wait several seconds, verify that no
   automatic callback deletes redo history.
9. Snapshot-limit trimming followed by action-history click.
10. End turn, extra turn, enemy intent refresh, summon/death, orb, relic, and
    Sovereign Blade state.
11. Kill an enemy, undo while its death animation is active, wait past the
    original death duration, then repeat undo/redo and verify the restored node
    and health bar remain present.
12. Test a multi-form enemy and a creature with looping or completed overlay
    tracks; verify the form/pose returns without restoring a mid-hit frame.

For every case, inspect `godot.log` for `ObjectDisposedException`, parent-busy
errors, invalid signal disconnects, Harmony failures, restore rollback, and
old-timeline VFX quarantine failures.

## Update Procedure

For a future STS2 build:

1. Hash and archive the new `sts2.dll` analysis under `.analysis/sts2`.
2. Diff the decompiled methods named in this document before changing the mod.
3. Verify every reflected field and exact Harmony target.
4. Build against the new runtime.
5. Run the startup smoke test before combat testing.
6. Execute the manual matrix above, prioritizing quick/full exhaust and card
   choices.
7. Update the manifest, README target version, and this audit.
8. Do not publish until the local DLL and packaged DLL hashes match.
