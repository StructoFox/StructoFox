# StructoFox 🦊

**Flow · Struct · Code**

Plan and *build* code from diagrams. Sketch a flowchart (PAP, DIN 66001), turn it
into a structogram (Nassi-Shneiderman, DIN 66261), arrange UML-style structure
cards — then generate real source in 10 languages. Optionally let an AI fill the
gaps that can't be derived deterministically.

> Not a code *reader* like Structorizer — StructoFox is about **planning and
> building**. The diagram is the source of truth; the code falls out of it.

## Status

🌱 Early extraction. The platform-neutral **core** has been carved out of its
origin app (ClaudetRelay) and stands on its own — no WPF, no localization, no AI
plumbing. Cross-platform UI (Windows + Linux via Avalonia) is the next layer.

## Layout

```
src/
  StructoFox.Core/        ← platform-neutral .NET library (net10.0)
    Models/               ← CodeEntity, FlowChart, Structogram, board primitives
    Services/             ← CodeExportService (10 langs), StructogramConverter (PAP→NS),
                            CodeBoardService (persistence)
```

The core has **zero UI dependencies** and compiles on any .NET 10 target, so the
same logic backs both the Windows and Linux front-ends. Theming uses the
OXSUIT colour-theme XML standard (plain colour definitions → trivially
cross-platform).

## Roadmap

- [x] Carve platform-neutral core out of ClaudetRelay
- [ ] Avalonia UI shell (board canvas, flowchart, structogram editors)
- [ ] OXSUIT theme loader for Avalonia
- [ ] Optional AI code generation (single provider, tucked in a menu)
