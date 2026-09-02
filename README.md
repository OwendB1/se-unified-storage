# Space Engineers Plugin Template

[Client only version of this template](https://github.com/CometWorks/client-plugin-template)

## Prerequisites

- [Space Engineers](https://store.steampowered.com/app/244850/Space_Engineers/)
- [Python 3.12](https://python.org) (requires 3.12 or newer)
- [Pulsar](https://github.com/SpaceGT/Pulsar) — plugin loader for Space Engineers (game client)
- [Magnetar](https://magnetar.se) — the Space Engineers server with plugin support
- [.NET Framework 4.8.1 Developer Pack](https://dotnet.microsoft.com/en-us/download/dotnet-framework/net481) and
  [.NET 10 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)

## Create your plugin project

1. Click on **Use this template** (top right corner on GitHub) and follow the wizard to create your repository
2. Clone your repository to have a local working copy
3. Run `setup.py`, enter the name of your plugin project in `CapitalizedWords` format
4. Let `setup.py` auto-detect your install locations or fill them in manually
5. Open the solution in Visual Studio or Rider
6. Make a test build, it should deploy the resulting files to their respective target folders (see them in the build log)
7. Test that the empty plugin can be enabled in Pulsar (client) and Magnetar (server)
8. Replace the contents of this file with the description of your plugin
9. Follow the TODO comments in the source code
10. Look into the source code of other plugins for examples on how to patch the game

You may find the source code of these plugins inspirational:
- [Performance Improvements](https://github.com/viktor-ferenczi/se-performance-improvements)
- [Multigrid Projector](https://github.com/viktor-ferenczi/se-multigrid-projector)
- [Toolbar Manager](https://github.com/viktor-ferenczi/se-toolbar-manager)

In case of questions please feel free to ask the SE plugin developer community on the
[Pulsar](https://discord.gg/z8ZczP2YZY) Discord server in their relevant text channels. They also have dedicated
channels for plugin ideas, should you look for a new one.

_Good luck!_

## Remarks

### Plugin version

The plugin version lives in `Version.Build.props`, which **is** committed and imported by
`Directory.Build.props`. Keeping the version separate from the local path overrides means it
is shared by all contributors and stays under version control. Bump the version there.

### Folder path overrides

`Directory.Build.props` **is** committed and declares the overridable folder paths with empty
defaults:

- `Bin64` — the folder containing `SpaceEngineers.exe`
- `Dedicated64` — the folder containing `SpaceEngineersDedicated.exe`
- `Pulsar` — the Pulsar folder the client plugin is deployed into after each build
- `Magnetar` — the Magnetar installation folder, the one holding the launcher executables
  and their `Libraries`, which is where `PluginSdk.dll` is referenced from
- `MagnetarData` — the Magnetar config folder the server plugin is deployed into, the one
  holding `Local`, `Sources` and `Profiles`

It optionally imports `Directory.Build.props.user` from the repository root, which is **not
committed** (matched by `*.user` in `.gitignore`), so each contributor keeps their own local
paths there.

To override a path manually, copy the first `PropertyGroup` of `Directory.Build.props` into
`Directory.Build.props.user`, wrapped into a top-level `<Project>` element, and fill in your
paths. `setup.py` writes that file for you with the auto-detected install locations, creating
it if needed and keeping any other overrides already in it.

Leaving a path empty (or having no `Directory.Build.props.user` at all) falls back to the
auto-detection in `Directory.Build.props`, which reads the Steam registry keys on Windows and
the usual Steam locations on Linux, then resolves the game and the Dedicated Server through
Steam's `libraryfolders.vdf`, so installs on a secondary Steam library are found as well.

| Loader folder  | Windows                                        | Linux                                             |
|----------------|------------------------------------------------|---------------------------------------------------|
| `Pulsar`       | `%AppData%\Pulsar`                             | `$XDG_CONFIG_HOME/Pulsar` (`~/.config/Pulsar`)    |
| `Magnetar`     | the `Magnetar\` tree next to the server install | `$XDG_DATA_HOME/Magnetar` (`~/.local/share/Magnetar`) |
| `MagnetarData` | `<Magnetar>\MagnetarLegacy` or `\MagnetarInterim`, named after the launcher | `$XDG_CONFIG_HOME/Magnetar` (`~/.config/Magnetar`) |

The build fails with a clear message if `Bin64`, `Dedicated64` or Magnetar's `PluginSdk.dll`
cannot be resolved, and warns instead of failing if a loader folder is missing, in which case
that plugin is only built, not deployed.

### Deployment

Each successful build copies itself into its loader's `Local` plugin folder, so there is
nothing to run by hand:

| Project        | Build     | Deployed to                                       |
|----------------|-----------|---------------------------------------------------|
| `ClientPlugin` | `net48`   | `<Pulsar>/Legacy/Local/<PluginName>/`             |
| `ClientPlugin` | `net10.0` | `<Pulsar>/Interim/Local/<PluginName>/`            |
| `ServerPlugin` | `net48`   | `<Magnetar>/MagnetarLegacy/Local/` (Windows only) |
| `ServerPlugin` | `net10.0` | `<MagnetarData>/Local/`                           |

Pulsar identifies a plugin by its folder, so the client DLL is copied as `plugin.dll`, its
symbols as `plugin.pdb` and the PluginHub registration XML from the repository root as
`plugin.xml`. Magnetar identifies a plugin by its DLL file name, so the server plugin is
copied flat as `<PluginName>.dll`, with the MagnetarHub registration XML next to it as
`<PluginName>.dll.xml`. Either way the loader shows the plugin under its friendly name and
honours the runtime and platform restrictions declared in the XML.

`Interim` is the Pulsar executable running Space Engineers 1 on .NET 10. It falls back to the
`Legacy` data folder when `<Pulsar>/Interim` does not exist, and so does the deployment.
(`<Pulsar>/Modern` belongs to Space Engineers 2 and is never a deployment target here.)
`MagnetarInterim` is its dedicated server counterpart and falls back the same way. On Linux
only the Interim launchers exist, so only the `net10.0` build is made.

### Plugin configuration

You can have a nice configuration dialog with little effort in the game client.
Customize the `Config` class in the `ClientPlugin` project, just follow the examples.
It supports many different data types, including key binding. Once you have more
options than can fit on the screen the dialog will have a vertical scrollbar.

![Example config dialog](Docs/ConfigDialogExample.png "Example config dialog")

The server plugin configuration works differently, please see the `Config` folder
of the `Shared` project for that. The client side `Config` class is not integrated
with the server side configuration, currently.

### Shared project

- Put any code you can share between the plugin projects into the Shared project.
  Try to keep the redundancy at the minimum.

- The DLLs required by your Shared code need to be added as a dependency to all the projects,
  even if some of the code is not used by one of the projects.

- You can delete the projects you don't need. If you want only a single project,
  then move over what is in the Shared one, then you can delete Shared.

### How to prevent the potential crash after game updates

Please use the `EnsureCode` attribute on patch methods to safely skip loading the plugin
with an error logged should the code in any of the methods patched would change as part of
a game update. It is a good way to prevent blaming crashes on your plugin after game updates,
so your plugin can remain safely enabled (but effectively disabled) until you have a chance
to release an update for compatibility with the new game version. Please see the examples in
the `Shared/Patches` folder on how to use this attribute.

The hexadecimal hash code is logged in case of a mismatch, so you can read them from the logs
for any new method you patch, just leave the string initially empty in the `EnsureCode`
attribute, then replace with the value from the error log line after you run your plugin
with the patch for the first time.

On Proton (Linux) this check tends to cause issues, therefore it is automatically skipped
when the plugin detects it is running under Wine/Proton.

### Debugging

- Always use a debug build if you want to set breakpoints and see variable values.
- A debug build defines `DEBUG`, so you can add conditional code in `#if DEBUG` blocks.
- While debugging a specific target unload the other one. It prevents the IDE to be confused.
- If breakpoints do not "stick" or do not work, then make sure that:
  - Other projects are unloaded, only the debugged one and Shared are loaded.
  - Debugger is attached to the running process.
  - You are debugging the code which is running (no code changes made since the build).
- Transpiler patches will write a `harmony.log.txt` file to your `Desktop` while running `Debug`
  builds. Never release a debug build to your users, because that would litter their desktop
  as well.
- To debug transpiler changes to the IL code it is most practical to generate the files
  of the method's IL code before and after the change made, so you can just diff them.
  Please see the transpiler example under the `Shared/Patches` folder for the details.

### Accessing internal, protected and private members in game code

Enable the Krafs publicizer to significantly reduce the amount of reflections you need to write.

This can be done by systematically uncommenting the code sections marked with "Uncomment to enable publicizer support".
Make sure not to miss any of those. List the game assemblies you need to publicize in `GameAssembliesToPublicize.cs`. 
In case of problems read about the [Krafs Publicizer](https://github.com/krafs/Publicizer) or reach out on the [Pulsar](https://discord.gg/z8ZczP2YZY) Discord server.

### AI assisted plugin development

Please consider using [se-dev-skills](https://github.com/viktor-ferenczi/se-dev-skills/) for better outcomes.

### Troubleshooting

- If the IDE looks confused, then restarting it and the debugged game usually works.
- If the restart did not work, then try to delete caches used by your IDE and restart.
- If your build fails to copy the plugin into the `Local` folder, then something locks the DLL file.
- Look for running game or server processes (maybe stuck running in the background) and kill them.

### Release

- Always make your final release from a RELEASE build. (More optimized, removes debug code.)
- Always test your RELEASE build before publishing. Sometimes it behaves differently.
- In case of client plugins the Pulsar compiles your code, watch out for differences.

### Publishing your plugin

- Register **client** plugins into [PluginHub](https://github.com/StarCpt/PluginHub/),
  so they become available in Pulsar.
- Register **server** plugins into [MagnetarHub](https://github.com/viktor-ferenczi/MagnetarHub),
  so they become available in Magnetar.

### Communication

- In your documentation always include how players or server admins should report bugs.
- Try to be reachable and respond on a timely manner over your communication channels.
- Be open for constructive critics.

### Abandoning your project

- Always consider finding a new maintainer, ask around at least once.
- If you ever abandon the project, then make it clear on its GitHub page.
- Abandoned projects should be made hidden on PluginHub and MagnetarHub.
- Keep the code available on GitHub, so it can be forked and continued by others.
