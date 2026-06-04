# GraphToolkitDS

GraphToolkitDS is a Unity dialog system package with Graph Toolkit authoring, runtime dialog playback, localization support, and Timeline integration.

## Installation

Install from Git URL in Unity Package Manager:

```text
https://github.com/LocalT0aster/GraphToolkitDS.git?path=/NodeBasedDialog/Packages/zone.toaster.graphtoolkitds#v1.0.0
```

Or add it directly to `Packages/manifest.json`:

```json
{
  "dependencies": {
    "zone.toaster.graphtoolkitds": "https://github.com/LocalT0aster/GraphToolkitDS.git?path=/NodeBasedDialog/Packages/zone.toaster.graphtoolkitds#v1.0.0"
  }
}
```

Use a branch, commit SHA, or tag after `#` depending on how strictly you want to pin the dependency.

## Requirements

- Unity 6000.3 or newer.
- Unity Graph Toolkit `0.4.0-exp.2`.
- Unity Localization `1.5.4`.
- TextMesh Pro `3.0.9`.
- Timeline `1.7.7`.
- Unity UI `1.0.0`.

Package dependencies are declared in `package.json` and are installed by UPM.

## Usage

1. Create an authoring graph from `Assets > Create > Dialog Node Based System > Dialog Graph`.
2. Add a `Start` node and connect it to dialog nodes.
3. Compile selected `.dialoggtk` assets from `Tools > Dialog System > Compile Selected Graph Toolkit Dialog Graphs`.
4. Assign the generated runtime `DialogNodeGraph` asset to `DialogBehaviour.StartDialog`.

Legacy `DialogNodeGraph` assets can still be opened with `Window > Dialog Node Based Editor (Legacy)`. Use `Tools > Dialog System > Migrate Legacy Dialog Graphs` to create a migration manifest and empty Graph Toolkit authoring graph.

## Samples

Import `Demo` from the Package Manager samples panel. The sample includes a scene, dialog graph, localization assets, settings, and starter scripts.

If Unity does not automatically select the imported sample localization settings, assign the sample's `Localization Settings` asset in Project Settings before running the localized demo scene.

## Documentation

The original PDF guide is included under `Documentation~`.
