# Repository Instructions

## Scope

These instructions apply to the entire repository.

## Language

- Write all documentation and code comments in English.
- This includes Markdown, XML documentation, docstrings, inline and block comments, `TODO`/`FIXME` notes, test descriptions, and developer-facing diagnostics.
- Do not introduce non-English prose in new or modified files.
- Preserve exact external identifiers, protocol tokens, Codex UI labels, and quoted output when matching them is required; explain them in English.
- Preserve license notices, legal text, and attribution verbatim.
- When third-party source is imported into the maintained firmware copy, translate non-English code comments to English without changing behavior or required notices.

## README ownership

- `README.md` is the source of truth for the goal, verified findings, architecture, protocol draft, roadmap, project status, safety gates, and known limitations.
- Update the README whenever implementation or hardware testing changes any of those facts.
- Keep verified facts distinct from drafts, proposals, and untested assumptions.
- Update the date and phase line after a material project-state change.
- Do not create a second design document that duplicates README content.

## Project invariants

- Firmware owns debounced physical input state and actual LED rendering.
- The companion owns action mapping, desired LED scenes, USB coordination, and serialized Codex operations.
- The visible Codex UI is the source of truth for current task state and effort.
- Do not flash hardware before roadmap gates R0 and R1 are complete.
- Do not add a Windows Service, custom driver, administrator requirement, private Codex IPC, second app server, coordinate-based UI control, or automatic approval handling.
- Fail closed when device identity, Codex target, UI selector, ACK, or postcondition is ambiguous.

## Working rules

- Follow the roadmap order and exit gates in `README.md`; do not mark a phase complete without its stated evidence.
- Prefer the smallest working implementation and native platform APIs before dependencies.
- Preserve upstream license and attribution when importing firmware code.
- Do not claim a physical measurement or hardware test unless the user performed it or supplied direct evidence.
- Add one runnable check for non-trivial parsing, protocol, state, or timing logic.
- Run `git diff --check` before handing work back.
- Review changed documentation and comments for English-only prose before completion.
