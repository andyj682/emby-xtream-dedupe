# ADR-F005: Make the Plugin Protect Its Own Configuration

*(Fork ADR. Numbered in the fork's own `F` sequence so it can never collide with an
upstream ADR — see [README.md](README.md).)*

**Date**: 2026-09-10
**Status**: ACCEPTED — mechanisms 1–3 implemented and verified; mechanisms 5–7 accepted,
not yet implemented (see the 2026-09-13 amendment)
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
saving produced a copy in the `xtream-rollback` folder, so Emby's configuration endpoint does call
`UpdateConfiguration` rather than assigning `Configuration` and saving directly. The
coverage claim above holds: a UI save is captured at the moment it happens, not at the
following sync.

The copy captured the **pre-save** state, which is the point and is worth knowing how to
confirm: `File.Copy` preserves the source's last-write time, so a rollback copy carries two
different timestamps. The **filename** is when the copy was taken; the **file's mtime** is
when the state inside it was written. If the hook ever fired too late, the mtime would
match the save rather than predate it.

## Amendment, 2026-09-13: the backup becomes self-service too

Everything above shipped and is verified on a rig and in production. This amendment closes
an inconsistency inside the original decision rather than adding a new direction.

### Why

**The premise of this ADR was that anything requiring Docker, a cron entry, and prior
knowledge protects the users who least need protecting.** That reasoning moved detection
and evidence into the plugin — and then left the *backup*, the thing being detected damage
to, as a script the user has to remember to run.

Two provider re-ingests five days apart have now tested that. Both recovered fully. Both
depended entirely on a pre-event catalog snapshot that existed only because someone ran it
by hand. The second one came 12 hours before the identity work would have absorbed it
automatically, so the snapshot was again the whole recovery. **Twice is a pattern, not a
run of bad luck, and both times the artifact that mattered was the one thing still outside
the plugin.**

Two further findings sharpen it:

- **The store-size line is not durable.** Mechanism 1 puts the four counts in the log every
  sync, which is the trend the ADR wanted — but the log rotates. "It is in the log" is not
  the same as having a history, and the whole value of those counts is the trend across
  weeks.
- **"Same volume" was a hardcoded path, not a constraint.** The rollback writes to a
  `xtream-rollback` folder derived from the configuration's own directory. The honest caveat in
  mechanism 3 — that it does nothing for a lost volume — describes a choice, not a limit.

### 5. The rollback gains a sibling: a scheduled backup, under one relocatable root

The rollback stays exactly as specified: pre-write, bounded, beside the configuration, on
by default. It answers "undo the last bad write."

A **backup** is added alongside it, with a different trigger and a different destination: a
copy taken on a schedule, with its own larger retention. It answers "the volume holding my
configuration is gone."

Both use the same copy routine and the same hash check. They are separate settings because
they are separate failures, and conflating them is what made mechanism 3 have to apologize
for itself.

**The destination is one root with a working default, not a setting that must be filled
in.** `<configuration directory>/xtream-backups/` unless the user points it elsewhere. The
setting *relocates* the root; it does not enable the feature.

That distinction is the whole point. An empty-by-default path would mean these mechanisms
protect only users who went looking for them — the identical failure to shipping a script,
reproduced inside the plugin. It matters most for mechanisms 6 and 7, which are worthless
unless they have been running all along: a snapshot or a counts history that starts the day
you need it is no history at all.

The honest caveat is then the rollback's, unchanged and stated in the same breath: **by
default this is on the same volume as the file it protects**, so out of the box it covers a
bad write and not a lost disk. Pointing the root at another volume is what upgrades it, and
that is the one action worth asking the user to take.

**Driven by an Emby scheduled task, not by the sync.** A user whose sync is disabled,
failing, or simply not scheduled still needs backups — and tying the backup to the sync
reproduces, inside the plugin, exactly the "only protects people who already set it up"
property this ADR exists to remove.

**The task can also be run on demand**, so a backup can be forced before something risky
rather than waiting for the schedule.

### 6. The catalog snapshot moves into the plugin — reversing alternative 3

Alternative 3 rejected this as duplicating ADR-F004 stage 3, on the grounds that a snapshot
stores ~37,500 catalog rows to answer a question about ~66,000 decisions. **That was right
about the snapshot being a poor substitute for a stored identity, and wrong about it being
redundant.** They answer different questions, and stage 3 cannot reach three of them:

- **Series.** Stage 3 is movies-only because the series list payload carries no TMDB ID —
  measured 0 of 21,758 on the install this was re-measured against. No pairs exist, so no
  in-plugin identity can re-point a series decision.
- **Titles the provider ships with no TMDB ID.** Roughly 11% of a churn cohort. Nothing
  gets recorded for them, so nothing can be re-pointed.
- **ID recycling** — whether a provider reuses a retired ID, which would silently re-apply
  a stale exclusion to unrelated content. Answering it requires two dated catalog states
  and cannot be answered from decisions at all.

The cost objection also inverts once the data is already in hand: **the sync fetches the
whole catalog every run**, so writing it costs a file write and no provider traffic. The
snapshot is therefore written by the sync rather than by the backup task — it belongs where
the data already is, and a separate task would re-fetch a catalog the plugin just had.

### 7. A durable, append-only counts log

The four store sizes are also appended to a sidecar file that does not rotate, one line per
sync. Mechanism 2 already set the precedent by writing deletion evidence to a sidecar
deliberately outside the log-level system, for the same reason: the question is asked
afterwards.

**The format must match the existing external log byte for byte** — full field names, local
timestamp, no trailing path. This is not cosmetic. When the standalone canary was rewritten
it deliberately adopted the *log's* format rather than the reverse, because changing the
labels splits the history into two incomparable series precisely at the event anyone would
want to look back through. A plugin-written line in a tidier format does the same damage,
permanently, and nobody notices until the next incident.

Retention should be generous or absent. One line per sync is a few hundred bytes a year;
pruning it recreates the problem the file exists to solve.

### 8. One root, and the README names every path

All three artifacts live under the single root from mechanism 5:

```
<root>/config/      configuration copies        (mechanism 5, scheduled + on demand)
<root>/snapshots/   catalogue TSVs              (mechanism 6, written by the sync)
<root>/counts.log   append-only store sizes     (mechanism 7, one line per sync)
```

Splitting them is a mistake this project has already made and paid for: the external
tooling kept catalog snapshots and configuration backups in two different directories, and
that produced a wrong command during a live incident — at the one moment when nobody has
time to go looking. The plugin should not rebuild that split internally.

**This matters more while restore is manual.** Every recovery path currently runs through
the external scripts, and `repair-id-churn.py` takes an explicit `--snapshot` path. If the
user cannot say where the snapshot is, the artifact might as well not exist. So:

- The snapshot keeps `catalogue-snapshot.py`'s exact TSV format and filename convention, so
  the existing tooling reads a plugin-written file with no changes and no flags.
- The **README's "Protecting your configuration" section lists every location in one
  place** — including the rollback folder and the deletion sidecars, which stay where they
  are. Two artifacts outside the root is tolerable; two artifacts nobody can find is not.
- The sync and task logs name the path they wrote to, so the answer is also in the log the
  user already has.

The rollback deliberately stays beside the configuration rather than moving under the root,
as a **sibling** of it: `xtream-rollback/` next to `xtream-backups/`. It is copied on every
save, so it has to stay adjacent, on the same volume, and available unconditionally —
following a root the user can relocate would mean cross-volume I/O on every write and a
failure mode where the destination is unavailable at exactly the moment it is needed. Two
sibling directories, only one of them relocatable, makes the rollback/backup distinction
this ADR keeps insisting on visible in the filesystem.

**Both names are prefixed** because they share `plugins/configurations/` with every other
plugin's configuration file, where a bare `rollback/` says nothing about whose it is. The
rename from `rollback/` costs nothing only because mechanism 3 has never appeared in a
tagged release — after one it would need a migration, or it would strand users' most recent
good copies in a directory nothing prunes.

### What does not change

- **Nothing alarms automatically.** The plugin still cannot distinguish a deliberate bulk
  un-exclude from data loss. Every mechanism here makes a question answerable; none decides
  that something is wrong.
- **Off-box copies remain the user's job.** A plugin cannot put a file somewhere the plugin
  cannot reach, and a configurable directory on the same machine is not an off-box backup.
  The README section stays and says so.
- **`scripts/` stays.** The standalone canary in particular keeps one job the plugin can
  never do: **running when the plugin does not.** If Emby is down or the plugin fails to
  load, its own log line cannot report that — no sync, no line, and silence reads as
  "nothing changed." Durability, trend and format move in-plugin; independence cannot.

### Credentials: state it once, do not special-case it

Mechanism 3 argued that a rollback copy is not a new class of exposure because it inherits
the configuration's own location and permissions. A user-chosen destination does break that
specific premise — but not in a way that matters, and it is worth being accurate rather
than cautious here.

**Credentials are already spread across this system by design.** Every `.strm` file the
plugin writes embeds the provider username and password in its URL; a curated library is
tens of thousands of such files, on a volume deliberately shared to media clients. The XC
API carries the same credentials as query parameters. The configuration is not the
sensitive outlier — it is one file among many, and the backup is one more copy of a secret
that is already everywhere the plugin touches.

So the answer is not a restriction that implies a guarantee the rest of the design does not
make. It is to **say it once, plainly, where the user is choosing the path**, and let them
apply the same judgment they are already applying to their library. The README's existing
warning extends to cover backups; the setting carries the same note.

The default root avoids the question entirely, sitting inside the configuration's own
directory where mechanism 3's original argument holds unchanged.

The destination picker reuses the writable-paths browser that already chooses the STRM
library path — **for consistency and to catch typos, not as a security control.** Calling
it a safeguard would be theatre.

### Deferred: restore, and why a button is right here

Restoring from a backup is **not** in this amendment. It is the first thing that would let
the plugin overwrite its own configuration from user-supplied data, and taking backups is
both the urgent half and purely additive.

When it is built, one constraint is settled in advance: **the user initiates it, always. The
plugin never restores automatically.**

That is the opposite of ADR-F004's "not a button", and the distinction is worth stating so
the two do not read as inconsistent. F004 rejected a manual repair because the failure it
addresses is *silent* — a design that depends on the user knowing it happened has already
failed. A restore answers a problem the user already knows they have. Automatic repair is
right for an invisible failure; automatic restore would be a plugin deciding, on its own,
that the present state is wrong — exactly the judgment the "nothing alarms automatically"
rule says it cannot make.

What it would buy is real: it removes the two dangerous steps in the current procedure —
stopping Emby and hand-copying a file over the live configuration — because the write goes
through the plugin's own save path.

## Implementation references

- `Emby.Xtream.Plugin/Service/StrmSyncService.cs` (sync summary, `CleanupOrphans`,
  `RemoveExcludedContent`)
- `Emby.Xtream.Plugin/PluginConfiguration.cs` (retention settings)
- `scripts/config-counts-canary.py` (the counts logic the sync summary replaces for the
  common case; retained for ad-hoc runs against a rig or a repair candidate, and for the
  one job the plugin cannot do — running when the plugin does not)
- `scripts/audit-strm-links.py`, `scripts/repair-id-churn.py` (post-hoc repair, unchanged)

Added by the 2026-09-13 amendment:

- `Emby.Xtream.Plugin/PluginConfiguration.cs` (backup directory and its own retention,
  separate from the rollback's)
- `Emby.Xtream.Plugin/ScheduledTasks/` (the backup task — scheduled and on-demand)
- `Emby.Xtream.Plugin/Service/StrmSyncService.cs` (the catalog snapshot, written from the
  fetch the sync already performs; the append-only counts line)
- `scripts/catalogue-snapshot.py` (the TSV format the in-plugin snapshot must match, so the
  external repair tooling can read either)
- [ADR-F004](004-survive-provider-id-churn.md) (stage 3, which this amendment deliberately
  does *not* treat as making the snapshot redundant — see mechanism 6)
