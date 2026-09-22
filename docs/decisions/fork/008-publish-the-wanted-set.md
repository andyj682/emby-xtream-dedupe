# ADR-F008: Publish the Wanted Set as a File

*(Fork ADR. Numbered in the fork's own `F` sequence so it can never collide with an
upstream ADR — see [README.md](README.md).)*

**Date**: 2026-09-16
**Status**: ACCEPTED — contract agreed with the consuming plugin's author, implemented.
**Affects**: `StrmSyncService.SyncMoviesAsync` (one file write at the end), `PluginConfiguration`
(one path field), `README.md` (a setup section — this feature requires a Docker change)
**Supersedes**: the demand-signal half of [ADR-F006](006-fetch-movie-detail-on-sync.md), which
was withdrawn. **Read this rather than that** — F006's record is kept for its measurements, but
this is the part that survived and the only part being built.

---

## Context

Providers describe series well and movies poorly. Resolution, audio codec and similar attributes
usually arrive with a series and usually do not with a movie, so tooling that ranks streams can
do it for episodes and not for films. Closing that gap needs a pass that goes and gets the
missing data — and that pass needs to know **which movies are worth the effort**, because
probing a whole catalog to improve the fraction someone actually watches is not a tractable job.

**This plugin is the only component that knows which titles a person wants.** The review gate
already computes it: reviewed, not excluded, actually written. Nothing on the proxy side records
demand at all.

ADR-F006 tried to convey that by calling `get_vod_info` per wanted movie, which stamps a
"a client asked for this" marker on the proxy's own records. **That was withdrawn**, and the
reason matters here: under the cadence that made the call affordable, the marker never expires,
so a stable title keeps a timestamp forever. The signal degenerates to a boolean — it can say
"was wanted once" and never "no longer wanted" — and it is erased outright by the relation churn
that provider re-ingests cause. For scoping a pass where each probe costs a real provider
connection slot, that is lossy on exactly the axis that matters: the population would accumulate
titles the user has since dropped.

**A file has none of those problems, costs one write per sync, and is the one thing the
consuming side genuinely cannot reconstruct later.** Everything else it wanted — what metadata
is missing, which relations exist, how many candidates a movie has — it can already query.

## Decision

**Write the wanted set to a JSON file at the end of each movie sync, to a configurable path.**

```json
{
  "schema": 1,
  "generated_at": "2026-09-16T04:12:33Z",
  "generator": "emby-strm",
  "count": 2412,
  "tmdb_ids": [603, 27205],
  "unidentified": [{"stream_id": 419883}]
}
```

- **An object, not a bare array.** A bare array has the same defect that disqualified the proxy's
  refresh timestamp: **if the generator stops writing, a stale file is indistinguishable from a
  current one, forever.** `generated_at` lets the consumer refuse to act on a set nobody has
  confirmed in months rather than spend connection slots on it.
- **`count` guards a partial write**, and it earns its place on our side for a second reason: a
  sync can abort part-way for reasons unrelated to this file, and a count disagreeing with the
  array length is the cheapest possible signal that it did. **It covers `tmdb_ids` only** — stated
  here and in the README so nobody has to guess.
- **`schema`** because these always change.
- **Written atomically** — temp file, then rename within the same directory. The consumer is an
  unattended nightly pass, and a truncated JSON read at 3am is miserable to diagnose.
- **Blank path means do not write.** The feature requires a Docker change (below), so it cannot
  be on by default.

### `unidentified` is closer to the core of the probe population than its margin

Wanted titles with no TMDB ID, given as `stream_id` only — we have no account concept, so the
consumer resolves accounts itself.

🔑 **This looks like a ~5% tail and is not.** TMDB coverage on the movie list payload measured
~95%, so it is roughly 120 titles out of 2,400. But a provider that ships no ID also ships no
*metadata* — no artwork, no plot, no useful detail payload — so **the free API route can never
populate them and a self-probe is the only thing that ever will.** They are disproportionately
the titles the probe pass exists for.

⚠️ **Do not drop this array as a simplification.** The 5% figure makes it look optional; the
correlation is what makes it not.

⚠️ Entries will sometimes fail to resolve, because `stream_id` is the proxy's `Movie.id` and
those rows churn — one provider's entire relation set was recreated twice in a week. That is
expected, not corruption. [ADR-F004](004-survive-provider-id-churn.md) stage 3 keeps the
*decision* attached across that churn; only this file's pointer goes stale, and the next sync
rewrites it.

### It is a projection, never a store

🚨 **The file carries nothing incremental. It is recomputed in full from the current wanted set
on every run, and holds no information that exists nowhere else.**

That single property is what makes every other decision here safe: a deletion costs at most one
sync interval of staleness, a partial write is repaired by the next run, and no upgrade,
migration or wipe on either side can lose anything.

⚠️ **The tempting future change is to make it incremental for efficiency — do not.** It would
convert a disposable projection into a state store that any of the above can destroy, and it
would buy nothing: the set is a few thousand integers and writing it costs one file operation.

### Transport: a file, not an HTTP endpoint

The plugin already has an authenticated API and both containers share a Docker network, so
serving this would have been a small addition. Rejected, on the consuming author's argument
rather than ours:

- **It would mean storing an Emby API key in their plugin's settings** — a credential lifecycle
  introduced to solve a file-placement problem, in a plugin that otherwise holds nothing
  sensitive. A read-only file adds no secret.
- Their consumer is an **unattended nightly pass**, where a missing file fails louder than a 401
  nobody sees.
- A file can be inspected with `cat` at 3am.

Our own argument — that a file survives Emby being down or mid-restart and an endpoint does not
— pointed the same way but is the weaker one.

### Location: one mount, and the plugin directory is the wrong target

**This feature requires a Docker Compose change and cannot work without one.** The two
containers share no mount today.

**Target: a directory that is a sibling of the proxy's `plugins/`, inside the `/data` it already
mounts** — so the consumer needs no compose change and no restart, and only this plugin's
container gains a new bind mount. One change, one restart.

🚨 **Do NOT mount into the consuming plugin's own folder**, which was the first instinct because
the feature exists for that plugin and it avoids touching anything shared. Two reasons:

1. **The proxy imports Python from `/data/plugins`, which is on its `sys.path`.** Write access
   there is not a storage concern, it is **code execution in the consumer's process**. This is
   the reason that would not be noticed.
2. Plugin folders are **replaced wholesale on every plugin upload**, so the file would vanish
   without warning on the consumer's next release. Survivable, given the projection property
   above — but needless.

   A sibling directory is verified inert on the consumer's side: its loader lists only
   `/data/plugins` and appends only that to `sys.path`, so a sibling is never scanned, imported
   or touched by an upgrade.

⚠️ **If the proxy's `/data` turns out to be a named volume rather than a host bind mount, this
shape does not work** and the fallback is a dedicated exchange directory mounted rw here and
**ro** there — cleaner on permissions, at the cost of a second compose change and restart.

### Permissions: the file mode is not the hazard

**File `0644`, directory `0755`.**

🔑 **The directory mode matters as much as the file mode and is the likelier mistake** — a `0700`
directory created by this container makes the file unreadable however permissive the file itself
is.

🚨 **And a permissions failure is indistinguishable from an absent file**, which is defined below
as "do nothing" — so a misconfiguration would silently disable the entire pass with no symptom.
The consumer therefore **distinguishes them in its logging**: absent is a normal state (before
the first sync, after a wipe) and logs at INFO, while present-but-unreadable or
present-but-malformed is a misconfiguration and logs at WARNING with the reason. Same principle
as this fork's own identity line: **a report that says zero beats an absence that could mean
either.**

### The consumer's contract: fail closed on scope

Agreed with the consuming author and recorded here because it is a shared contract, not their
private business. **Any untrustworthy input means do nothing** — absent, unparseable,
unrecognised `schema`, `count` disagreeing with the array length, or `generated_at` older than
the staleness threshold. Never "refresh everything", never treat an empty set as "probe it all".

🔑 **The principle, in their words: fail open on enrichment, fail closed on scope.** A fault in
the enrichment costs some missing metadata, and nothing there is destructive, so a user waiting
on data should not lose it to an over-eager guard. But a fault in the thing that decides **how
much work to do** must collapse to zero, because the failure mode is thousands of provider
connection slots spent on a set nobody asked for.

## Consequences

- **Setup is required and the feature is inert without it.** The README must say so plainly: a
  configured path that writes into a directory nobody reads looks exactly like it is working.
- **One file write per movie sync**, over a set the sync already enumerates. No provider traffic,
  no measurable cost.
- **The consumer's probe population self-expires.** A title the user stops wanting leaves the
  file on the next sync, which is the property the withdrawn marker approach could not provide.
- **We never write the proxy's own bookkeeping fields**, unchanged from ADR-F006 and worth
  keeping: those fields are the only record anywhere that a *client* asked for a movie's detail,
  and that meaning stays uncontaminated only if we stay out of them.

## Amendment — what building it settled

Four things the decision above left unstated, recorded here because each is a real choice and
none is visible from the diff.

### A partial catalogue fetch publishes nothing

**Not covered above, and it is the one case where writing is worse than not writing.** When some
VOD categories fail to answer, the sync still runs over the ones that did — so the wanted set
computed from that run lists a subset, and **a subset is indistinguishable from a smaller set the
user chose.** `count` cannot catch it, because it agrees with the array.

Under "fail closed on scope" the consumer would then do *less* work than asked, with no symptom on
either side. Leaving the previous file untouched instead lets `generated_at` age, which is a signal
the consumer already handles. Same reasoning as the catalogue snapshot's own partial-fetch skip.

### A resolved ID goes beside the stream ID, never into `tmdb_ids`

The plugin can resolve a missing ID through Emby's metadata providers. **`tmdb_ids` carries only
the provider's own ID**, from the list payload. Promoting a resolved one was rejected for two
reasons, and the consuming author independently supplied a third that is the decisive one:

1. The fallback only runs under certain flag combinations, so using it would make that list's
   contents depend on settings that have nothing to do with it.
2. It would move titles out of `unidentified` **without changing the fact that the provider has no
   stream metadata for them** — which is the whole reason they are the center of the consumer's
   job. A resolved ID would make the population look healthier while removing exactly the titles
   the probe is for.
3. 🔑 **It is not a usable lookup key on their side.** They resolve `tmdb_ids` against their own
   rows, and a row is unidentified there for the same reason it is here — the provider supplied no
   ID, so their row carries none. A promoted ID **would match nothing and silently drop the title
   out of scope**, which is worse than omitting it. Our resolved value is knowledge we derived;
   their database has never seen it.

**It is published, though, as a separate field on the unidentified entry** — added at their
request, on a churn argument. `stream_id` is their `Movie.id`, and those rows get recreated
wholesale: one provider's entire relation set was recreated twice inside a week. When that happens
the stream ID here is dead and the title is skipped with nothing to say it should not have been. A
second key costs nothing to try and starts working the moment their own enrichment tags the
recreated row with that same ID. **Dead handle, live fallback** — it can only add reach, never
redirect a lookup that would otherwise have succeeded.

### The durable key is actually the provider's raw name

```json
"unidentified": [{"stream_id": 419883, "name": "XX - Example Film (2019)", "resolved_tmdb_id": 12345}]
```

🔑 **Reached by working out what `resolved_tmdb_id` costs, and finding a free key sitting in the
same object.** The resolved ID needs TMDB folder naming **and** fallback lookup on, and that
lookup has no cache of any kind — it re-searches every non-provider-identified title on **every
sync, forever**, with no negative cache, so the queries that keep failing are the ones that keep
being retried. It covers only the fraction TMDB can match. The install this was built against runs
folder naming on and fallback **off**, deliberately, so the field would have been permanently
absent there.

The name has none of that: **free, always present, independent of every setting, and covers 100%
of unidentified titles.** And it is the better match key on its merits — the reader's rows are
built from the same provider feed, so the untouched string has a counterpart there.

⚠️ **Published RAW, never cleaned.** `EnableContentNameCleaning` exists to produce a tidy folder
on this side; applying it here would move the string *away* from the feed the reader's rows came
from, which is the only thing this key is for. Cleaning is also a setting, and the point of this
field is to be the one key no setting can take away.

🔑 **AND THE CONSUMING AUTHOR SUPPLIED THE SHARPER REASON, which neither of us had said out loud:
the string is not a foreign provider's title, it is a snapshot of THEIR OWN `Movie.name`.** We
consume the proxy's XC output, so the verbatim value we carry is the exact field they would match
against. **That makes this an exact-string comparison against a remembered value of their own
column — not cross-provider name normalisation**, which is a technique that has produced three
wrong answers on this project and which they would have refused outright. So cleaning would not
merely be untidy: it would convert a snapshot of their field into a *derived* string, and the
match would stop being exact. Record this as the reason, not the tidiness argument.

⚠️ **A name match is still fuzzy in the sense that a title can appear more than once, so it is a
fallback after the stream ID is already dead** — not a primary key, and not a competitor to
`tmdb_ids`.

### The reader resolves in a strict order, and only `stream_id` is a key

Recorded because it is a shared contract, and because it is what makes an unverified
`resolved_tmdb_id` safe to publish at all:

1. **`stream_id`** — authoritative while the row exists.
2. **`name`, only if it matches EXACTLY ONE current row.** Ambiguity means skip, never guess —
   multiple matches *are* the duplicate problem by definition.
3. **`resolved_tmdb_id`** — never on its own. Where it and `name` both resolve they must agree; if
   they disagree the reader trusts neither.

🔑 **That third rule is the answer to the unverified-first-result problem.** A wrong ID is worse
than a missing one because it is invisible — but it stops being dangerous once it can only ever
*corroborate* a match something else already made. **These are corroborating signals, not
alternatives.** Anyone extending this later must not promote `name` or `resolved_tmdb_id` to a
standalone key.

### The honest limit: it recovers row churn, not catalogue re-identification

⚠️ **The name fallback fails exactly when a provider changes its naming convention** — and that
has happened on the install this was built against: a re-ingest produced dotted filenames, mangled
whitespace and doubled prefixes, so **the IDs and the names moved at the same time.** Neither key
resolves and the title is skipped.

Accepted, and deliberately not solved here. **Stated so the field is not over-trusted: these two
events look identical from our side and are different problems.** Row churn is recoverable; a
re-identified catalogue is not, by any key in this file.

⚠️ **Consequence: the file now contains titles**, which puts it in the same class as the catalogue
snapshots. No credentials, but not something to publish beyond the host.

🚨 **The publish still logs when no resolved ID is present** — the reader is told to treat absence
as normal, so an install that never resolves would otherwise look identical to one where TMDB
genuinely cannot name these films. **But the log states the fact and stops; it does not suggest
turning the fallback lookup on.** That lookup takes the *first* search result unverified and, with
folder naming on, bakes it into the folder name — so recommending it would trade a missing key for
a confidently wrong identification, on exactly the obscure titles where a first-result name search
is least reliable. Note that the population argument cuts both ways here, and this is the direction
it cuts against.

### File mode is the umask's, not ours

The 0644/0755 above stands as the target, but **netstandard2.0 has no API to set a Unix file
mode**, so the plugin cannot enforce it. What it relies on is the default: `File.WriteAllText`
creates 0666 and `Directory.CreateDirectory` 0777, masked by the usual 022, which lands exactly on
the contract. **Recorded because it is an assumption about the environment rather than a guarantee
in the code** — a container running with a different umask produces different modes and nothing
here will notice.

### The review gate being off warns, and does not refuse

ADR-F006 left open whether to require the review gate. **Answered: no.** With the gate off, "the
titles this sync keeps" genuinely is the whole included catalog — the file is honest, just much
larger. Refusing would mean the plugin deciding a legitimate configuration is wrong, which is the
judgement it declines to make elsewhere (see mechanism 9's "the user always initiates this"). It
logs the situation plainly at each publish instead, and the consumer's own scope guard is the
backstop.

### 🔒 The file's contents are PII, and both sides hold the same rule

**Once it carries `name` it holds real library titles, which makes it PII under this project's
standing rules** — provider names and specific titles are treated the same as credentials for
anything that leaves the machine. Agreed explicitly with the consuming author and written down on
both records, because it is the kind of rule that only fails when one side assumes the other has it.

Neither side quotes its contents into a released artifact: no upstream bug report, no release
notes, no public README, no test fixture. **Genericised placeholders at the point of writing,
never scrubbed afterwards** — this ADR's own examples are synthetic for that reason, and the
README's advice not to move the file off the host is the same rule seen from the producer's end.

## Implementation references

- `Emby.Xtream.Plugin/Service/StrmSyncService.cs` (`SyncMoviesAsync`, one write at the end, after
  the wanted set is known)
- `Emby.Xtream.Plugin/PluginConfiguration.cs` (one path field, blank = off)
- [ADR-F006](006-fetch-movie-detail-on-sync.md) (withdrawn; kept for the measurements that led
  here, including why the refresh timestamp is not a usable demand signal)
- [ADR-F002](002-require-review-before-sync.md) (the review gate, which defines "wanted")
- [ADR-F004](004-survive-provider-id-churn.md) (stage 3, which keeps decisions attached across
  the churn that makes `stream_id` pointers go stale)
