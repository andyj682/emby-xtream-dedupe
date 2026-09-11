# ADR-F005: Make the Plugin Protect Its Own Configuration

*(Fork ADR. Numbered in the fork's own `F` sequence so it can never collide with an
upstream ADR — see [README.md](README.md).)*

**Date**: 2026-09-10
**Status**: ACCEPTED (not yet implemented)
**Affects**: `StrmSyncService` (sync summary logging, `CleanupOrphans`,
`RemoveExcludedContent`), `Plugin` / configuration save path, `PluginConfiguration` (new
opt-out and retention fields), `README.md`, `scripts/` (one script retired from the
critical path)

---

## Context

This fork has accumulated a set of standalone diagnostic and recovery tools in `scripts/`,
and they work: on 2026-09-09 a provider reissued every stream ID in its catalog, and
`repair-id-churn.py` re-pointed 8,560 exclusions and 118 reviewed marks against a
pre-event `catalogue-snapshot.py` snapshot. The library came out intact.

**It worked because a snapshot from 48 hours earlier happened to exist.** It had been run
by hand. The same directory listing showed a six-day gap immediately before it. Recovery
rested on luck.

Everything in `scripts/` shares that shape: it requires Docker, a cron entry, and a user
who knows the tools exist before the day they are needed. The person who wrote them had
not automated them. Nobody else is going to.

## Problem

Four things need to be true for a user's configuration to be recoverable. Today all four
depend on setup the plugin does not ask for and most installs will never do.

1. **You can tell when a store shrinks.** Exclusions and reviewed marks are the expensive,
   irreplaceable part of this plugin's state — tens of thousands of individual decisions.
   Nothing surfaces their size, so a drop is invisible until someone notices the review
   queue looks wrong, which can be weeks.
2. **You can tell what a deletion removed.** ADR-F004 stage 1 logs a sample of up to 15
   deleted paths. That is enough for a routine sweep and useless for the events that
   matter: a 2026-09-06 run removed 360 files under `Shows`, a 2026-09-08 run removed 126
   under `Movies`, and neither is explainable now.
3. **You can map a dead provider ID back to a title.** Handled by ADR-F004 stage 3; not
   re-decided here.
4. **You can get back the configuration you had before a bad write.** The failure ADR-F001
   through ADR-F004 keep circling is the config being damaged *through the plugin's own
   save path*. An external backup runs on a schedule; the damage happens whenever.

## Alternatives considered

1. **Ship a combined "back up everything" script.** The obvious move, and the one this ADR
   was opened to write. Rejected: it inherits every property that made the existing tools
   fail to be set up. A script that must be discovered, installed, and scheduled protects
   the users who least need protecting.
2. **Do nothing; document better.** The README already documents the scripts. That was true
   on 2026-09-09 and the snapshot still nearly did not exist.
3. **Move everything into the plugin, including the catalog snapshot.** Rejected as
   duplication: ADR-F004 stage 3 already persists the identity that matters, targeted at
   the decisions rather than the whole catalog. A plugin-written snapshot would store
   ~37,500 rows to answer a question about ~66,000 decisions.
4. **Selective: move what is cheap and universal, leave the rest.** Chosen.

## Decision

**Move the detection and evidence into the plugin, where they cost nothing per user and
require no setup. Leave genuine off-box backup to the user, and say so plainly.**

### 1. Report the store sizes in every sync summary

One line per sync naming all four counts. The sync already reads every store, so this is
free, and it lands in a log the user already has — giving every install a trend rather
than a single reading. A drop becomes visible at the next sync instead of at the next
triage session.

This deliberately does **not** try to alarm on a drop. The plugin cannot distinguish a
user bulk-unexcluding several thousand titles from a store being eaten, and a false alarm
on a legitimate action is worse than a number in a log.

### 2. Record every deleted path, not a sample

When a cleanup run deletes more than the logged sample, write the full list to a sidecar
file beside Emby's logs, with a bounded number of files retained. The Info summary keeps
its 15-path sample and gains a pointer to the file.

Written unconditionally rather than at Debug level: the question is always asked
*afterwards*, and a diagnostic that requires having enabled it beforehand does not answer
retrospective questions.

**This retires the daily folder-listing idea.** A `find` listing diffed across days is a
way of inferring what disappeared; recording what was deleted answers it directly, needs
no baseline, and catches episode-level loss inside an existing show, which a title-level
listing cannot.

### 3. Keep a rollback copy of the configuration before writing it

Before the plugin writes a configuration whose stores differ from what is on disk, copy
the existing file aside, keeping a small bounded number.

**Call it a rollback, not a backup, and be honest about what it does not do.** It lives
beside the configuration it protects, so it does nothing for a lost volume or a failed
disk. What it does cover is the failure this fork has spent four ADRs on: a bad write
through the plugin's own save path, where the plugin is the only component that knows a
write is about to happen. An external backup runs at 3am; the wipe happens whenever.

**On by default.** The reflex is that this touches provider credentials and must therefore
be opt-in. That reasoning does not hold up: the file is already on disk in that directory,
in plaintext, and the copy inherits the same location and permissions. It is not a new
class of exposure, and making it opt-in would mean it protects only users who already
understood the risk — the same failure as shipping a script. Retention is configurable and
can be set to zero to disable it.

Skip the copy when the stores are unchanged, so routine saves do not churn several
megabytes.

### 4. Do not ship a combined backup script

The README gains a short **Protecting your configuration** section: what is irreplaceable,
what the plugin now does for you, and the one-line `cp` for an off-box copy. That last part
is genuinely the user's job — a plugin cannot put a file somewhere the plugin cannot reach.

## Consequences

- Every install gets store-size trends, full deletion evidence, and a pre-write rollback
  with no setup, no Docker, and no cron. That is the entire point: the recovery tooling
  only helped because one user happened to run it by hand.
- `scripts/` stays, and stays useful — for repairing damage after the fact, for series
  (which ADR-F004 stage 3 does not cover), and for anything needing the provider's live
  catalog. It leaves the *critical path*: no user has to have set it up in advance.
- The daily folder listing is retired before being built.
- Disk cost is bounded and small: a handful of configuration copies and a handful of
  deletion logs. The configuration is ~1.8 MB on a heavily curated install, so the
  retention default should be single digits rather than the 30 an external backup keeps.
- The rollback copies contain provider credentials, exactly as the configuration itself
  does. This must be stated in the README next to the retention setting, not left implicit.
- Nothing here alarms automatically. Every mechanism makes a question *answerable*; none
  tries to decide that something is wrong. That is deliberate — the plugin cannot tell a
  deliberate bulk action from data loss, and this project's own history is full of
  diagnostics that were correct and silent rather than loud and wrong.

## Implementation note, 2026-09-10: both hooks exist

The rollback is taken from two places, because neither alone is sufficient.

**At the start of a sync**, before `CheckAndUpgradeNamingVersion` and before the review
gate's write-back — both of which save. This covers everything the sync itself writes.

**In an override of `BasePlugin.UpdateConfiguration`**, which covers a save made from the
config page. That path arrives through Emby's own API and touches no plugin code anywhere
else, so without this hook a UI save was only captured at the *following* sync — the last
good state survived, but only for as many syncs as the retention count allowed. Whether
the base method was `virtual` could not be determined from the reference assemblies and was
settled by compiling: it is.

Both funnel into the same snapshot routine, and the hash comparison means the two hooks
firing in quick succession produce one copy rather than two.

✅ **Verified on an Emby 4.10 rig, 2026-09-11.** Changing a setting in the config page and
saving produced a copy in the `rollback` folder, so Emby's configuration endpoint does call
`UpdateConfiguration` rather than assigning `Configuration` and saving directly. The
coverage claim above holds: a UI save is captured at the moment it happens, not at the
following sync.

The copy captured the **pre-save** state, which is the point and is worth knowing how to
confirm: `File.Copy` preserves the source's last-write time, so a rollback copy carries two
different timestamps. The **filename** is when the copy was taken; the **file's mtime** is
when the state inside it was written. If the hook ever fired too late, the mtime would
match the save rather than predate it.

## Implementation references

- `Emby.Xtream.Plugin/Service/StrmSyncService.cs` (sync summary, `CleanupOrphans`,
  `RemoveExcludedContent`)
- `Emby.Xtream.Plugin/PluginConfiguration.cs` (retention settings)
- `scripts/config-counts-canary.py` (the counts logic the sync summary replaces for the
  common case; retained for ad-hoc runs against a rig or a repair candidate)
- `scripts/audit-strm-links.py`, `scripts/repair-id-churn.py` (post-hoc repair, unchanged)
