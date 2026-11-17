# EarGuard documentation

Start with the current context and specification, not archived review notes.

| Document | Purpose and status |
|---|---|
| [Product README](../README.md) | Public setup, product limitations and user-facing release notes. |
| [Project context](../CONTEXT.md) | Current project state, implementation boundaries and working conventions. |
| [Architecture specification](SPEC.md) | Current requirements, safety invariants, modules and build/test procedures. |
| [Gumroad review draft](marketing/GUMROAD.md) | Active, unpublished seller draft awaiting Amr's review; not an approved listing or release. |
| [Marketing cover art](../assets/cover%20photo/README.md) | Source, masters and platform exports for the product cover and Gumroad thumbnail. |
| [Build script](../build.ps1) and [compiler response file](../EarGuard.rsp) | Root build entry points; keep their source, output and asset paths stable. |

## Build and test

Run commands from the repository root, including commands shown in the specification:

```powershell
powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File build.ps1 -BuildOnly -Compiler Roslyn
powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File build.ps1 -BuildOnly -Compiler Framework
```

These run the isolated regression suite and compile into fresh `archive/builds/<run>/` directories without replacing the root or installed executable. `-Deploy` and `-HardwareTest` are separate opt-ins, not cleanup checks. The response-file quick build writes the root executable; do not use it to verify an organization-only change against a working copy.

## Layout and retained history

- `src/` and `tests/` contain application source and regression tests; `build.ps1`, `EarGuard.rsp`, `.github/`, `.gitignore` and `LICENSE` stay at the root.
- `EarGuard.ico` is referenced by the compiler/build script. `EarGuard-transparent.png` and `EarGuard.png` are runtime logo candidates; the product README also uses `EarGuard.png` and `screenshot.png`. Keep these paths stable.
- The working root `EarGuard.exe` stays in place. A new isolated build does not replace it.
- The existing, Git-ignored `archive/` holds retained build/test artifacts, prior patches and historical documents, including the dated startup plan and `archive/docs/` review/website notes. It is local evidence, may be absent in a fresh clone, and is excluded from the semantic corpus. Historical paths, version claims and safety language there are not current requirements; use the specification and current source instead. Do not delete or silently rewrite that evidence.
- Keep active requirements and review drafts in this docs tree. Archive only genuinely superseded material after checking consumers and recording a hash-preserved move.

## Retrieval and index

- Exact filenames, symbols and errors: grep/file search first.
- Concepts or unknown implementation names:

  ```text
  semsearch "question" -c earguard -k 5 --json
  ```

  Alternatively, use egindex MCP `search` with `index="earguard"`. Open the returned files before changing anything; current source wins if an index hit is stale.
- Reuse the existing central index `E:/AI/eg2-index/indexes/earguard.sqlite`, not a new project-local store. Corpus ownership is declared in `E:/AI/Harness/tools/semantic_corpora.py`; `archive/` is excluded.
- After source/document moves, the authorized shared-index owner can refresh only this corpus:

  ```text
  E:/AI/eg2-index/.venv/Scripts/python.exe E:/AI/Harness/tools/build_corpora.py earguard
  ```

- The embedding endpoint at `127.0.0.1:8099` starts on demand. Searching does not automatically refresh changed files; do not add a duplicate endpoint or boot task. Shared retrieval policy: `E:/AI/Harness/docs/knowledge-discovery.md`.
