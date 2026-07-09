<div align="center">

<img src="Assets/Structofox_256.png" width="140" alt="StructoFox logo"/>

# StructoFox 🦊

**Flow · Struct · Code**

*Plan and build code from diagrams.*

</div>

---

StructoFox is a **code-planning and generation tool**. You design software visually —
classes and their relationships, then the logic of each function as a **flowchart**
(Programmablaufplan, DIN 66001) or a **structogram** (Nassi-Shneiderman, DIN 66261) —
and StructoFox turns it into real, buildable source code across many languages.

> Not a code *reader* like Structorizer — StructoFox is about **planning and building**.
> The diagram is the source of truth; the code falls out of it. The deterministic parts
> (structure, signatures, structograms) generate code with **no AI**; an optional AI
> plugin fills only the gaps that can't be derived.

**Status:** `0.9.0-beta` — feature-complete for a first release; hardening and polish.

---

## Screenshots

<!-- Drop images into Assets/ and reference them here, e.g.:
<p align="center"><img src="Assets/screenshot-flowchart.png" width="820" alt="Flowchart editor"/></p>
-->

*Screenshots coming soon.*

---

## What you can do

- **Model structure** — Namespaces, Classes, Structs, Interfaces, Enums, Functions and
  Objects, each with fields, methods (with typed parameters), enum values and data ports.
  Arrange them as UML-style cards on free **structure boards**; wire typed data ports;
  set inheritance and interfaces.
- **Draw the logic** — per function/method:
  - a **flowchart** (DIN 66001): start/end, process, decision, **multi-branch** (switch/case
    comb), I/O, subroutine, note; labelled arrows; multi-page via off-page connectors.
  - a **structogram** (DIN 66261): nested statement / if-else / while / do-while / case blocks.
- **Deterministic workflow** — *(optional flowchart) → structogram → code*. A flowchart
  converts to a structogram with one button; that doubles as a **clean-structure filter**
  (unstructurable jumps are flagged, not silently miscompiled). The structogram then
  generates the method body **deterministically — no AI**.
- **Node-editor autocomplete** — an **AI-free, Visual-Studio-style** completion dropdown
  while writing node text: it suggests the project's own types, objects, namespaces and
  functions (methods shown *with* parameters) plus variables introduced earlier in the
  same flow, rendered in the project's chosen language syntax.
- **Generate buildable projects** — deterministic skeletons in **13 languages**, with
  **multi-file** projects (one file per type + build/wiring files) for 12 of them:
  C#, C, C++, Java, Go, PHP, TypeScript, JavaScript, Python, Rust, Kotlin, Swift
  (Verse is single-file). Optionally let an AI plugin fill the method bodies.

---

## AI is optional

The whole deterministic pipeline — structure, structograms, and code export — runs
**without any AI**. The **AI code generation is a removable plugin** (`Plugins/AiCodegen`):
leave it out and the feature simply isn't there (e.g. a school deployment). When present,
it drives local or cloud models to fill method bodies into the deterministic skeleton, with
per-language guidance and deterministic post-passes so even weak models produce compiling
code. API keys are stored only in the OS's native secret store, never in a file.

---

## Extending StructoFox (plugins)

StructoFox loads optional plugins — `.dll` drop-ins in the `Plugins/` folder — that add
commands to the **Extensions** menu (the AI code generation ships as one). A plugin only
references the UI-free `StructoFox.Core` and reads the project through the normal Core
services.

See **[PLUGINS.md](PLUGINS.md)** for the guide: the contract, a minimal template, the csproj
setup, reading project data, custom themed UI, and localization — with the bundled `Sample`
(minimal) and `AiCodegen` (advanced) plugins as references.

---

## Architecture

```
src/
  StructoFox.Core/            ← platform-neutral .NET library (net10.0), zero UI deps
    Models/                   ← CodeEntity, FlowChart, Structogram, board primitives
    Services/                 ← CodeExportService + ProjectExporter (13-language codegen),
                                StructogramConverter (PAP→NS), CodeCompletionService,
                                project / diagram / board persistence
  StructoFox.App/             ← Avalonia 12 desktop front-end + OXSUIT theming + plugin host
  StructoFox.AI/              ← provider layer (cloud + local), bundled by the AI plugin
  StructoFox.Plugin.AiCodegen ← optional AI code-generation plugin
  StructoFox.Plugin.Sample    ← minimal reference plugin
```

The **core has zero UI dependencies** and compiles on any .NET 10 target, so the same logic
can back multiple front-ends. Theming uses the **OXSUIT** colour-theme XML standard (plain
colour definitions → trivially cross-platform); drop `.oxsuit` files into `Themes/`.

---

## Requirements

- Windows 10 / 11 (built on **.NET 10** and **Avalonia 12**; the core is cross-platform,
  a Linux/macOS front-end is planned).
- Optional: an AI provider (local server or cloud API key) only if you use the AI plugin —
  everything else works without any AI.

---

## Roadmap

- [x] Platform-neutral core carved out of ClaudetRelay
- [x] Avalonia UI shell — structure boards, flowchart & structogram editors
- [x] OXSUIT theme loader for Avalonia
- [x] Deterministic multi-file code generation (13 languages)
- [x] AI code generation as a removable plugin
- [x] AI-free node-editor autocomplete
- [x] Plugin system + authoring guide
- [ ] Linux / macOS front-end
- [ ] Per-language help pages
- [ ] AI command/syntax lookup plugin

---

## License

MIT — see [LICENSE](LICENSE). © 2026 Hans-Robert Matthes.
