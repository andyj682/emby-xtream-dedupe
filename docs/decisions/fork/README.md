# Fork decision records

This directory holds ADRs for decisions made in **this fork**
(`andyj682/emby-xtream-dedupe`) rather than upstream (`firestaerter3/emby-xtream`).

## Why the separate namespace

Both projects are actively adding ADRs, and both were allocating from the same
`001, 002, 003 …` sequence. That collides on every merge: upstream shipped
`016-dispatcharr-stats-codec-trust.md` while this fork already had an `016`, an `017`
and an `018`. Git never reports a conflict, because the filenames differ — the damage
is silent. A code comment that says `see ADR-016` stops naming one document.

So fork ADRs live here, in their own sequence, and are cited as **`ADR-F001`**,
`ADR-F002`, and so on. Upstream cannot collide with a directory it does not know
exists, and a bare `ADR-016` anywhere in the tree is unambiguously upstream's.

## Conventions

- Filenames are `NNN-kebab-case-title.md`, numbered from `001` in **this** directory.
- Cite them in code, docs and commit messages as `ADR-F001`, never as `ADR-001`.
- Upstream ADRs stay in `docs/decisions/` and keep their own numbers. Reference them
  by their plain number (`ADR-012`, `ADR-014`) exactly as upstream does.
- Never renumber an existing fork ADR. If one is superseded, add a new one and mark
  the old one `SUPERSEDED BY ADR-Fnnn`.

## Index

| ADR | Title | Status |
| --- | --- | --- |
| [ADR-F001](001-collapse-group-exclusion-propagation.md) | Propagate series exclusion across the collapse group | Accepted |
| [ADR-F002](002-require-review-before-sync.md) | Hold un-reviewed titles out of the sync | Accepted |
| [ADR-F003](003-retire-dispatcharr-episode-refresh.md) | Retire the sync-time Dispatcharr episode refresh | Accepted |
| [ADR-F004](004-survive-provider-id-churn.md) | Survive provider ID churn inside the plugin | Accepted (stages 1 and 3 implemented; stage 2 withdrawn) |
| [ADR-F005](005-self-protecting-configuration.md) | Make the plugin protect its own configuration | Accepted |
| [ADR-F006](006-fetch-movie-detail-on-sync.md) | Fetch movie detail for titles we sync | Proposed |

## History

These three were renumbered on 2026-09-07, during the merge that brought in upstream's
`4b81027..3482982`. They were previously ADR-016, ADR-017 and ADR-018. References
written before that date — in the CHANGELOG for releases 1.4.0 through 1.5.0, in commit
messages, and in git history generally — use the old numbers.
