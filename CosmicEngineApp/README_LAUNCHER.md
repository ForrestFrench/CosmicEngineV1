# Cosmic Engine — Desktop Launcher

A no-Terminal way to start Cosmic Engine and open the Scene Dashboard.

## What to double-click

**`Run Cosmic Engine.command`** — lives in the `CosmicEngine.App` folder. Double-clicking it:

1. Starts Cosmic Engine in show/dashboard mode at the **Safe** profile.
2. Opens the Scene Dashboard automatically in your browser at `http://localhost:8080`.
3. Uses the known-good show seed (**777**) for Stellar Nursery by default.
4. Lets you quit the engine anytime from the dashboard's **Quit Engine** button.
5. If something's wrong (missing `dotnet`, moved files, engine failed to start), it prints a clear error and **keeps the window open** so you can read it — it won't just flash and disappear.
6. If Cosmic Engine is already running, it opens the existing dashboard instead of starting a confusing second copy.

`run-show.sh` does the actual work; `Run Cosmic Engine.command` is the double-clickable wrapper around it (Finder runs `.command` files in Terminal automatically). You only ever need to double-click the `.command` file — never type anything into Terminal yourself.

## Putting it on your Desktop

Don't **copy** `Run Cosmic Engine.command` to your Desktop — it needs to stay next to `run-show.sh` inside `CosmicEngine.App` to find the project. Instead, make a **Finder alias**:

1. In Finder, open the `CosmicEngine.App` folder.
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
CosmicEngine.App/DiagnosticReports/launcher_last_run.log
```

which is overwritten each time you launch — useful to check (or share) if the dashboard doesn't come up.

## What this launcher does *not* do

- It doesn't change any visuals, shaders, or scene behavior — it only starts the existing `dotnet run -- --profile Safe` show-mode session, the same one you'd get from Terminal.
- It doesn't install .NET for you — if the `dotnet` command is missing, it'll tell you to install the .NET SDK from [dotnet.microsoft.com/download](https://dotnet.microsoft.com/download).
- It doesn't run any bounded diagnostics/smoke-tests — for those, use Terminal directly (see the main `CLAUDE.md` for commands), since they're a developer tool, not a show-mode launcher.
