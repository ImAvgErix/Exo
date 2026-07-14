# Steam Optimizer Pack

Bundled with OptiHub. The default UI action is a full no-compromise pass:

- quiet CEF launch flags and aggressive client/overlay settings;
- steamwebhelper working-set reclamation every 5 seconds;
- High client priority while idle and Below Normal priority while gaming;
- deep disposable web/client cache cleanup and orphaned shader-cache cleanup;
- Windows startup suppression and Start menu/taskbar launcher retargeting.

OptiHub aborts if a Steam game is active. Active/resumable downloads, installed
games, and installed-game shader pre-caches are preserved. `Repair Steam`
restores captured config/startup values, stock shortcut targets, and removes the
OptiHub launcher/helper. Recovery is written before mutation, merged without
overwriting the original values on reapply, and retained after any failed repair
step. Orphan shader cleanup is skipped unless every library manifest inventory
is readable and unambiguous.
