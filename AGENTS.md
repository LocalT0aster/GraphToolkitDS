# Agent Guidelines

## Migration From CherryDev's Node Based Dialog System

This repository is the `origin` fork, `LocalT0aster/GraphToolkitDS`. Treat CherryDev's original Node Based Dialog System as the upstream asset lineage, not as the current installation model.

The fork has two major differences from the upstream asset layout:

- It is installed primarily through Unity Package Manager from a Git URL.
- It modernizes dialog authoring around Unity Graph Toolkit while keeping legacy runtime graph assets readable.

Use this Git URL as the primary install target:

```text
https://github.com/LocalT0aster/GraphToolkitDS.git?path=/NodeBasedDialog/Packages/zone.toaster.graphtoolkitds#v1.0.0
```

The package name is `zone.toaster.graphtoolkitds`. Do not suggest `zone.toaster.GraphToolkitDS` as the UPM identifier; Unity package names must be lowercase. `GraphToolkitDS` is only the display name.

## What To Ask First

Before guiding a migration, identify:

- Unity version. This fork targets Unity `6000.3` or newer and depends on `com.unity.graphtoolkit`.
- Current install type: Asset Store import, copied `Assets/Plugins/DialogNodeBasedSystem`, embedded package, or Git UPM package.
- Whether the user needs to keep old `DialogNodeGraph` assets as-is or rebuild them as Graph Toolkit `.dialoggtk` authoring graphs.
- Whether the project has localization, Timeline tracks, custom prefabs, or scripts that hard-code old asset paths.

If any of those are unknown, state the uncertainty. Do not promise a fully automatic migration.

## Migration Path

1. Back up or commit the Unity project before changing package installation.
2. Remove the old upstream asset folder, usually `Assets/Plugins/DialogNodeBasedSystem`, before installing this fork. Duplicate asmdefs or duplicate `cherrydev` scripts can create misleading compile errors.
3. Install the fork through Unity Package Manager with the Git URL above.
4. Let UPM resolve package dependencies: Graph Toolkit, Localization, TextMesh Pro, Timeline, and Unity UI.
5. Import the `Demo` sample only if the project needs the sample scene, sample localization assets, or starter scripts.
6. Open Unity and wait for a clean import before editing dialogs.

## Legacy Graphs

Legacy `.asset` `DialogNodeGraph` assets are still readable through `Window > Dialog Node Based Editor (Legacy)`.

For Graph Toolkit migration, select an old `DialogNodeGraph` asset and run:

```text
Tools > Dialog System > Migrate Legacy Dialog Graphs
```

Be precise with expectations: Unity `6000.3` Graph Toolkit does not currently expose enough public API for this fork to fully recreate all nodes and wires automatically. The migration command creates an empty `.dialoggtk` graph plus a JSON manifest describing the old nodes, data, positions, and port mappings. The graph then needs manual rebuilding in Graph Toolkit.

After rebuilding a Graph Toolkit authoring graph, compile it with:

```text
Tools > Dialog System > Compile Selected Graph Toolkit Dialog Graphs
```

Assign the generated `*_Runtime.asset` to the existing `DialogBehaviour.StartDialog(DialogNodeGraph)` workflow.

## API And Asset Path Notes

The runtime namespace remains `cherrydev`, and the assembly names remain `DialogNodeBasedSystem.Runtime` and `DialogNodeBasedSystem.Editor`. Existing scripts may compile without namespace changes.

Do not rely on old paths such as:

```text
Assets/Plugins/DialogNodeBasedSystem
```

Runtime/editor/package assets now live under:

```text
NodeBasedDialog/Packages/zone.toaster.graphtoolkitds
```

Generated project assets should live under `Assets/...`, not inside `Packages/...`. The compiler, variable config creation, and localization setup are designed to write generated assets into writable project asset folders.

## Localization And Samples

If a migrated project uses Localization:

- Confirm `com.unity.localization` is installed by UPM.
- Check Project Settings for the active Localization Settings asset.
- Re-run localization setup or key update if old string tables are missing entries.
- If using the package demo, import the `Demo` sample and assign the sample `Localization Settings` asset if Unity does not do it automatically.

Addressables and Localization may retain old addresses even after assets move. Treat addresses as labels, but verify they do not point users to obsolete `Assets/Plugins` paths.

## Verification Checklist

Before saying migration is complete, verify as much of this as the environment allows:

- Unity imports with no duplicate asmdef or duplicate type errors.
- Package Manager shows `zone.toaster.graphtoolkitds`.
- The project no longer contains old upstream source under `Assets/Plugins/DialogNodeBasedSystem`.
- Existing scenes reference package prefabs or migrated project prefabs correctly.
- A legacy graph can still open in the legacy editor, if legacy support is needed.
- A Graph Toolkit `.dialoggtk` graph can be created, compiled, and assigned to `DialogBehaviour`.
- Localization tables resolve in play mode if localization is used.

If Unity is not available in the environment, run static checks only and explicitly say Unity import/compile was not verified.
