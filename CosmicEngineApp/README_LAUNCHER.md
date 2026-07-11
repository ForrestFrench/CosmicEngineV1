# Cosmic Engine — Desktop Launcher

A no-Terminal way to start Cosmic Engine and open the Scene Dashboard.

## What to double-click

**`Run Cosmic Engine.command`** — lives in the `CosmicEngineApp` folder. Double-clicking it:

1. Starts **only the Scene Dashboard** — no Stellar Nursery, no Lava Lamp, no visual window opens automatically.
2. Opens the Scene Dashboard automatically in your browser at `http://localhost:8080`, showing **"No visual running. Choose a scene below."**
3. Waits for you to click **Launch Safe** or **Launch High** on whichever scene you want (Stellar Nursery or Lava Lamp) — only then does a visual window open, using that scene/profile.
4. Uses the known-good show seed (**777**) for Stellar Nursery automatically whenever you launch it this way (no extra step needed).
5. Lets you quit anytime from the dashboard's **Quit Engine** button — this works whether or not a scene has been launched yet.
6. If something's wrong (missing `dotnet`, moved files, engine failed to start), it prints a clear error and **keeps the window open** so you can read it — it won't just flash and disappear.
7. If Cosmic Engine is already running, it opens the existing dashboard instead of starting a confusing second copy.

`run-show.sh` does the actual work; `Run Cosmic Engine.command` is the double-clickable wrapper around it (Finder runs `.command` files in Terminal automatically). You only ever need to double-click the `.command` file — never type anything into Terminal yourself.

> **Why the project folder is named `CosmicEngineApp` (no dot)** — it used to be `CosmicEngine.App`, but macOS Finder treats any folder name ending in `.app` (case-insensitive, so `.App` counts) as a broken application bundle and refuses to let you navigate into it normally, even though it's just a regular folder. Renaming it fixed that — you can now open the folder in Finder like any other.

## Putting it on your Desktop

Don't **copy** `Run Cosmic Engine.command` to your Desktop — it needs to stay next to `run-show.sh` inside `CosmicEngineApp` to find the project. Instead, make a **Finder alias**:

1. In Finder, open the `CosmicEngineApp` folder (inside the CosmicEngine project folder).
2. Right-click `Run Cosmic Engine.command` → **Make Alias**. This creates a file named `Run Cosmic Engine.command alias` (or similar) right there.
3. Drag that alias file to your Desktop (or your Dock, if you prefer).
4. (Optional) Rename the alias to whatever you like, e.g. "Cosmic Engine" — renaming an alias doesn't break it.

You can now double-click the Desktop alias any time — it always launches the real script wherever the project actually lives.

If you double-click the launcher and it can't find `run-show.sh`, that's the sign it was copied instead of aliased — the launcher will tell you this directly and explain how to fix it.

## If macOS blocks it ("unidentified developer")

The first time you run any downloaded/self-written script, macOS Gatekeeper may show a warning. If that happens:

1. **Right-click** (not double-click) `Run Cosmic Engine.command` (or its alias) and choose **Open**.
2. Click **Open** again in the dialog that appears.

You only need to do this once — after that, regular double-clicking works normally.

If macOS says the file "can't be opened because it is from an unidentified developer" and there's no Open option on right-click, go to **System Settings → Privacy & Security**, scroll down, and click **Open Anyway** next to the mention of this file, then try double-clicking it again.

## How to quit

- Click **Quit Engine** on the dashboard page (`http://localhost:8080`), **or**
- Close the Terminal window that opened when you launched it, **or**
- Click into that Terminal window and press `Ctrl+C`.

Any of these stop Cosmic Engine cleanly — no process is left running in the background.

## If something goes wrong

The launcher window itself will show what happened and stay open so you can read it. It also writes a full log to:

```
CosmicEngineApp/DiagnosticReports/launcher_last_run.log
```

which is overwritten each time you launch — useful to check (or share) if the dashboard doesn't come up.

## What this launcher does *not* do

- It doesn't change any visuals, shaders, or scene behavior — it only starts the dashboard (`dotnet run -- --dashboard-only`), the same one you'd get from Terminal.
- It doesn't open Stellar Nursery, Lava Lamp, or any visual window on its own — you always choose that from the dashboard first.
- It doesn't install .NET for you — if the `dotnet` command is missing, it'll tell you to install the .NET SDK from [dotnet.microsoft.com/download](https://dotnet.microsoft.com/download).
- It doesn't run any bounded diagnostics/smoke-tests — for those, use Terminal directly (see the main `CLAUDE.md` for commands), since they're a developer tool, not a show-mode launcher.

## Dashboard-only mode, for developers

The launcher runs `dotnet run -- --dashboard-only` under the hood — a new engine mode that starts just the dashboard/control server without constructing an OpenGL window. If you want a visual immediately without going through the dashboard (e.g. for a quick manual check), the old direct-launch commands still work exactly as before and are unaffected by this change:

```bash
dotnet run -- --world StellarNursery --profile Safe   # opens Stellar Nursery directly, no dashboard-only step
dotnet run -- --world LavaLamp --profile Safe
```
