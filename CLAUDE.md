# CLAUDE.md

Monorepo of BepInEx/Harmony plugins for Valheim, authored by tlisonbee.
`README.md` is the long-form reference (design rationale, per-mod config,
lessons learned) - read the relevant section before changing a mod.

## Layout

- `OtBulkStation/`, `OtExtensionReach/`, `OtInventoryLayout/` - one project
  per mod, each with `Plugin.cs` (config + `[BepInPlugin]`) and `*Patches.cs`
  (Harmony patches).
- `libs/` - compile-time reference DLLs only (gitignored, copied from the
  game's `valheim_Data/Managed/` and BepInEx). Referenced via `<HintPath>` +
  `<Private>false</Private>`; never shipped.
- `<Mod>/package/` - tracked Thunderstore package sources: manifest.json
  (no `version_number`, which comes from `PluginVersion`), icon.png (256x256),
  mod-page README.md, CHANGELOG.md.
- `package.py` - builds each mod and zips `package/*` + DLL into
  `<Mod>/dist/<Mod>-<version>.zip`. `dist/` is gitignored and disposable.

## Build

```
cd <Mod> && dotnet build -c Release
# -> <Mod>/bin/Release/netstandard2.1/<Mod>.dll
```

Targets `netstandard2.1` (no .NET Framework targeting pack on this machine;
fine for BepInEx 5 on Valheim's Mono). The `MSB3277` System.Net.Http /
System.IO.Compression version-conflict warning is harmless.

## Test / deploy

- **Only** deploy to the Gale profile **Modding**:
  `~/.local/share/com.kesomannen.gale/valheim/profiles/Modding/`. Never read
  from, write to, or delete anything in any other profile (especially
  `OnePointOh`). Deploying to Modding needs no confirmation.
- While iterating: copy the built DLL flat into `Modding/BepInEx/plugins/`.
  If a Gale-installed `plugins/OtMods-<Mod>/` folder for that mod exists,
  have the user disable/uninstall it in Gale first. Don't edit that folder
  yourself. BepInEx loads recursively, so both would load the plugin twice.
- When the user asks for a Thunderstore zip to install via Gale: build it,
  then delete the flat-copied `plugins/<Mod>.dll` from Modding (standing
  authorization). The user installs the zip through Gale.
- Log: `<profile>/BepInEx/LogOutput.log`. Config:
  `<profile>/BepInEx/config/tlisonbee.valheim.<modname-lowercase>.cfg`. The
  live cfg may hold the user's test values, not shipped defaults - check
  `Plugin.cs` for defaults.
- Claude can't launch or play the game; in-game verification is the user's.
  Never claim a feature "works in-game" unless the log shows that exact
  build/version loaded and the user confirmed the behavior.

## Conventions

- New mods get the **`Ot`** prefix in folder/project name, `PluginName`, and
  the last segment of `PluginGuid` (`tlisonbee.valheim.ot<name>`).
- Don't default modifier keybinds to Ctrl (vanilla crouch); prefer Alt/Shift.
- Verify game APIs by decompiling rather than from memory - signatures shift
  between game updates:
  `ilspycmd -t <TypeName> /mnt/storage/Steam/steamapps/common/Valheim/valheim_Data/Managed/assembly_valheim.dll`.
  `ZInput`/`Utils` are in `assembly_utils.dll`, `Localization` in
  `assembly_guiutils.dll`.
- Prefab names: trust the mod's own runtime log (e.g. OtInventoryLayout's
  `prefab=` line) over localization strings or wiki "Internal ID"s. E.g. the
  Wardrobe is `piece_chest_warderobe` (game's typo).
- Prefer re-invoking/post-processing vanilla methods over reimplementing
  them; keep changes client-side and away from real inventory dimensions or
  save data.
- Code comments explain *why* (vanilla behavior being worked around), in
  the existing dense prose style.

## Releasing a version

1. Bump `PluginVersion` in `Plugin.cs` and add a `## <version>` section to
   `package/CHANGELOG.md` (the script refuses to package without it).
   Update `package/README.md` if the mod's behavior or config changed.
2. Commit, tag `<modname-lowercase>-v<version>` (e.g.
   `otinventorylayout-v1.0.0`).
3. `./package.py <Mod>`, after committing, so the git hash stamped into the
   DLL is the release commit. Then delete the flat-copied DLL from Modding
   (see above).
4. Push `main` and the new tag to `origin`
   (github.com/tblisonb/ot-valheim-mods) without asking - building the
   release zip is standing authorization. Outside a release, push only when
   the user asks. Never force-push.
5. The user uploads to Hexium through its web UI (there's no publish API).
   Package rules are in README's "Publishing to Hexium".
