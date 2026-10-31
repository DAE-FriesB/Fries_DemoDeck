# Fries' Demo Deck

A Windows desktop app for teachers who build programming exercises step by step and demo them in class.
Each exercise lives in git: one branch per week (`weekNN/start` → `weekNN/solution`) and optional snapshot branches
(`weekNN/step-N`) for the points you want to jump to during a lesson. No more copies of copies of projects.

Works with any project that can be versioned in git without Git LFS: Unity, MonoGame, .NET (ASP.NET Core, console),
web projects, and so on. The app only runs git; it doesn't need to know how to build the project. Keep large binary
assets small, because without LFS every version of them stays in the repository (the app warns about files over 5 MB),
and give the project a proper `.gitignore` (see [Get the .gitignore right first](#get-the-gitignore-right-first)).

- **[Walkthrough with screenshots](https://dae-friesb.github.io/Fries_DemoDeck/guide/)**: from an empty folder to a
  week with three demo steps, rebuilding a small MonoGame project commit by commit.
- **[Demo mode video](https://www.youtube.com/watch?v=ivMepVpuztI)**: jumping between those steps in class.

## Install

Download `FriesDemoDeck-win-Setup.exe` from the latest [release](../../releases) and run it. It installs per user
(no admin rights), adds a Start-menu shortcut and updates itself when a new release is published.
Requires [Git for Windows](https://git-scm.com/download/win) on the PATH.

## Using it

1. **Open course** and choose a course folder: a folder with an `exercises.yaml` file and a `Projects\` folder next to
   it. The app offers to create both. Recent folders are remembered in `%APPDATA%\ExercisesPipeline\settings.json`.
2. With no project yet, **Create project** makes a git repository in `Projects\` (README, `.gitignore`,
   `.gitattributes` on `main`). The type (Unity or .NET) only picks the starting `.gitignore`; for any other kind of
   project, pick either and replace the `.gitignore` with your own in the first week.
3. **+** in the weeks list creates a week (`weekNN/start` + `weekNN/solution`), starting from the previous week's
   solution or an empty project, and opens its solution.
4. Select a week to see its steps (Start, demo steps, Solution). **Open** checks out that point.
5. While working on a week, **Save** commits the changes; on the solution it can also mark the saved state as the
   next demo step. Click the *unsaved changes* pill to see the changed files and diffs, and to discard selected files.
6. A save made on an earlier step after later steps exist shows *N saves not in later steps*. **Propagate** merges
   it forward into every later step and solution; where both sides changed the same file you choose which version to
   keep, once.
7. **Demo mode** for class: the window turns teal, editing is hidden, clicking a step card jumps to it.
   Next / Previous (or PageDown / PageUp on a presenter clicker) walk through the steps; *Reset step* throws away what
   was done live. You work on throwaway `demo/...` branches; accidental commits are kept on `rescue/...`.
   *Leave demo* returns to where you were.

The app refreshes by itself when files or branches change (in Unity, an IDE or the command line).
*Git log* at the bottom shows every git command that changes something.

## Get the .gitignore right first

The `.gitignore` decides what the app treats as your work, so set it up before the first save:

- **Save stores every file that is not ignored.** Build output, caches and IDE files that are not in the `.gitignore`
  end up in the history and keep showing up as unsaved changes (for example `bin/` and `obj/`, Unity's `Library/`
  and `Temp/`, `.vs/`, `node_modules/`).
- **Demo mode keeps ignored files.** A jump throws away everything you changed live, but leaves ignored files alone.
  That is what keeps Unity's `Library/` or a project's `bin/` and `obj/` intact between steps, so nothing has to be
  imported or rebuilt from scratch.
- **Use a template for your tool.** The app writes one for Unity and one for .NET. For anything else, start from the
  matching template in [github/gitignore](https://github.com/github/gitignore) (MonoGame projects can use
  `VisualStudio.gitignore`, as in the walkthrough).
- **Put it in the first commit of the first week**, so every branch has it. To change it later, commit the change on
  the earliest branch and **Propagate** it forward like any other fix.

## Notes on behaviour

- A switch only happens from a clean working state. In Preparation a dirty state shows a dialog
  (save / put aside / throw away / cancel); in Demo mode changes are thrown away automatically.
- `state.mode` in `exercises.yaml` is written only after a mode switch succeeded in every project (atomic write).
  At startup the app compares the saved mode with the projects and offers to fix a mismatch.
- Fixes are merged forward, history is never rewritten.

## Build and run

```
dotnet build
dotnet run --project src/Ex.App
dotnet test
```

Requires the .NET 10 SDK and Git for Windows.

```
ExercisesPipeline.sln
  src/Ex.Core/          class library: git runner, repo scanner, YAML, actions, checks (no UI code)
  src/Ex.App/           the WPF client
  tests/Ex.Core.Tests/  xUnit tests; most run real git commands in a temp folder
  tools/IconGen/        renders the app icon into the .ico file
```

The app icon is drawn once in `src/Ex.App/Themes/AppIcon.xaml` (also used inside the app). After changing it,
regenerate the `.ico` used for the exe, taskbar and title bars:

```
dotnet run --project tools/IconGen -- src/Ex.App/Themes/AppIcon.xaml src/Ex.App/Assets/app.ico
```

## Releasing (installer and updates via Velopack)

Push a version tag; GitHub Actions (`.github/workflows/release.yml`) tests, builds the installer and publishes the release:

```
git tag v0.2.0
git push origin v0.2.0
```

The release contains `FriesDemoDeck-win-Setup.exe` and `FriesDemoDeck-win-Portable.zip`. Installed copies check the
GitHub releases at startup, download a newer version in the background and show "Version x ready: restart" in the
header.

To build the installer locally (output in `artifacts\releases`): `.\build-installer.ps1 0.2.0`.
`vpk` is pinned as a local dotnet tool (`dotnet-tools.json`), the same version as the Velopack package.
