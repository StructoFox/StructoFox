# Writing StructoFox Plugins 🦊🔌

A **plugin** is an optional `.dll` you drop next to StructoFox. On startup the app
discovers it and adds its commands to the **Extensions** ("Erweiterungen") menu.
Plugins are purely additive: leave the `.dll` out and the feature simply isn't there
(e.g. a school deployment without the AI code-generation plugin).

A plugin only needs to reference **`StructoFox.Core`** — the platform-neutral, UI-free
library. It reads everything the user planned (classes, functions, diagrams) through the
normal Core services, and talks to the user through a small host context. No UI framework
is required for a basic plugin.

---

## How plugins are loaded

- StructoFox looks in a **`Plugins/`** folder next to the executable.
- Put your plugin in its **own subfolder** — `Plugins/MyPlugin/` — containing its `.dll`,
  any unique dependencies, and an optional `Languages/` folder. A single loose `.dll`
  directly in `Plugins/` also works for a dependency-free plugin.
- At startup the host scans every `*.dll` and instantiates **every public, non-abstract
  type that implements `IStructoFoxPlugin` and has a public parameterless constructor**.
- Shared assemblies (`StructoFox.Core`, Avalonia, the BCL) are resolved from the **host**,
  so your `IStructoFoxPlugin` is the *same type* the app knows. Your own unique
  dependencies ship alongside your `.dll` and load in an isolated context.
- A broken or incompatible plugin is **skipped, never fatal** — so make sure your command
  list builds even when no project is open.

---

## Quick start — a minimal plugin

### 1. A class library targeting `net10.0`

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <!-- Loaded dynamically; don't drag your own copy of shared deps. -->
    <EnableDynamicLoading>true</EnableDynamicLoading>
  </PropertyGroup>

  <ItemGroup>
    <!-- Reference the SHIPPED StructoFox.Core.dll (it sits next to StructoFox.exe).
         Private=false + ExcludeAssets=runtime → do NOT copy Core next to your plugin;
         at runtime you bind to the host's already-loaded Core (same types). -->
    <Reference Include="StructoFox.Core">
      <HintPath>C:\Path\To\StructoFox\StructoFox.Core.dll</HintPath>
      <Private>false</Private>
      <ExcludeAssets>runtime</ExcludeAssets>
    </Reference>
  </ItemGroup>
</Project>
```

> Building **inside** the StructoFox solution instead? Use a `ProjectReference` to
> `StructoFox.Core` with the same `Private=false` / `ExcludeAssets=runtime` — see
> [`src/StructoFox.Plugin.Sample`](src/StructoFox.Plugin.Sample).

### 2. Implement `IStructoFoxPlugin`

```csharp
using StructoFox.Core;

public sealed class MyPlugin : IStructoFoxPlugin
{
    public string Name    => "My Plugin";
    public string Version => "1.0";

    public IReadOnlyList<PluginCommand> Commands { get; } = new[]
    {
        new PluginCommand
        {
            Title = "List functions",
            Run = ctx =>
            {
                if (ctx.ProjectFolder is null) { ctx.Notify("No project open."); return; }
                var fns = CodeEntityService.LoadAll(ctx.ProjectFolder, "Function");
                ctx.ShowText("Functions",
                    fns.Count == 0 ? "(none)" : string.Join("\n", fns.Select(f => "• " + f.Name)));
            },
        },
    };
}
```

### 3. Build, deploy, run

- Build the library.
- Copy the output `.dll` into `…/StructoFox/Plugins/MyPlugin/`.
- Restart StructoFox → your command appears under **Extensions**.

That's the whole loop. (`StructoFox.Plugin.Sample` is exactly this, ready to copy.)

---

## The contract (`StructoFox.Core/Services/Plugins.cs`)

### `IStructoFoxPlugin`
| Member | Purpose |
|---|---|
| `string Name` | Display name in the Extensions menu. |
| `string Version` | Free-form version string (e.g. `"1.0"`). |
| `IReadOnlyList<PluginCommand> Commands` | The actions your plugin contributes. |

### `PluginCommand`
| Member | Purpose |
|---|---|
| `string Title` | Menu label. |
| `Action<IPluginContext> Run` | Invoked when the user picks the command. |

### `IPluginContext` — what the host gives a running command
| Member | Purpose |
|---|---|
| `string? ProjectFolder` | The open project's folder, or `null` if none is open. |
| `string Language` | Host UI language code (`"de"`, `"en"`, …) — for localizing your own UI. |
| `object? OwnerWindow` | The host's main window as an **Avalonia `Window`** (opaque so Core stays UI-free). Cast it to parent your dialogs. |
| `void ApplyTheme(object window)` | Applies the host theme to a plugin-created Avalonia `Window`, so your dialogs match the app. |
| `void ShowText(string title, string content)` | Shows a read-only text panel (generated code, a lookup result…). |
| `void Notify(string message)` | Brief, non-blocking status/confirmation. |

---

## Reading project data

Everything the user planned lives on disk under `ProjectFolder`; read it through the
Core services (all UI-free):

- **`CodeEntityService.LoadAll(projFolder, type)`** → `List<CodeEntity>`, where `type` is
  one of `"Namespace"`, `"Class"`, `"Struct"`, `"Interface"`, `"Enum"`, `"Function"`,
  `"Object"`. A `CodeEntity` exposes `Name`, `Fields`, `Methods` (with `Parameters`),
  `EnumValues`, `Namespace`, `InstanceOfId`, data `Ports`, and more.
- **`FlowChartService` / `StructogramService`** — load a function's or method's diagram.
  The key is the entity id, or `"entityId#methodId"` for a method body.
- **`CodeExportService` / `ProjectExporter`** — turn entities into source / a buildable
  project (the same engines the built-in export uses).
- **`ProjectService`** — project metadata (`ProjectInfo`: authoring `Language`, …).

---

## Showing UI

- **Simple output** — `ctx.ShowText(title, content)` for a read-only panel, or
  `ctx.Notify(message)` for a brief status. No UI framework needed.
- **Custom dialogs** — reference **Avalonia** in your csproj (again `Private=false`, so you
  bind to the host's copy), build your own `Window`, then:
  - parent / position it with `ctx.OwnerWindow` (cast to `Avalonia.Controls.Window`);
  - theme it with `ctx.ApplyTheme(yourWindow)` so it matches the app's OXSUIT theme.

  See [`src/StructoFox.Plugin.AiCodegen`](src/StructoFox.Plugin.AiCodegen) for a full
  example — configuration windows, themed dialogs, and shared UI helpers (`PluginUi`).

---

## Localizing your plugin (optional)

- `ctx.Language` gives the host's current language code.
- Ship a `Languages/<code>.json` (flat `{ "Key": "Text" }`) next to your `.dll` and load
  overrides yourself. The AI-codegen plugin's `PluginLoc` shows the pattern: an English
  built-in dictionary as the baseline + a JSON overlay per language; ship `en.json` as the
  ready-to-copy template for translators. Missing keys fall back to English.

---

## Distribution & compatibility

- Ship your `Plugins/<name>/` folder: the `.dll`, your **unique** dependencies, and any
  `Languages/`. Users drop it into StructoFox's `Plugins/` folder and restart.
- **Do NOT ship** `StructoFox.Core.dll` or Avalonia — the host provides them (that's what
  `Private=false` / `ExcludeAssets=runtime` are for). Shipping your own copy risks a
  type-identity mismatch.
- Build against the **public `StructoFox.Core` API** (the contract + services above); it is
  kept backward-compatible.

---

## Examples in this repo

| Plugin | What it shows |
|---|---|
| [`src/StructoFox.Plugin.Sample`](src/StructoFox.Plugin.Sample) | The minimal template above — one command reading the project and showing text. |
| [`src/StructoFox.Plugin.AiCodegen`](src/StructoFox.Plugin.AiCodegen) | Advanced: bundles a whole AI provider layer (`StructoFox.AI`), its own localization (`PluginLoc`), themed UI helpers (`PluginUi`), and multiple commands. |
