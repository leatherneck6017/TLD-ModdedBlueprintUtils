# ModdedBlueprintUtils

A MelonLoader utility mod for The Long Dark that figures out which other mod added a crafting blueprint. It was written to support [CraftingSearch](https://github.com/leatherneck6017/TLD-CraftingSearch) and any other mod that needs a reliable source for "who added this recipe."

## Features

- **Modded blueprint attribution**: Matches loaded blueprints to the `.modcomponent` pack (or Melon) that added them.
- **Source report**: Writes `UserData\BlueprintSources.txt` after blueprints are loaded, including in-game display names when they can be resolved.
- **New recipes and true copies only**: Lists brand-new mod blueprints and new copies of vanilla recipes (a second, distinct `BlueprintData` instance). Copies are labeled `[Copied Vanilla Blueprint]`.
- **In-place vanilla edits are ignored**: Vanilla blueprints that were only modified in place (for example, a pack adding alternate tools to an existing recipe) are not listed and are not reported through the lookup API.
- **API for other mods**: Exposes `TryGetModSourceInfo` so another Melon can ask whether a given `BlueprintData` instance is a new or copied recipe and, if so, which pack it came from.

## Requirements

- The Long Dark (v2.60)
- MelonLoader v0.7.2

[ModComponent](https://github.com/ds5678/ModComponent) is not required to load this mod. If you use `.modcomponent` packs, keep them in your `Mods` folder so they can be matched by pack name. Blueprints added by other Melon mods can still be attributed from the call stack when that information is available.

## Installation

1. Install MelonLoader v0.7.2 for The Long Dark if you have not already.
2. Place `ModdedBlueprintUtils.dll` into your game's `Mods` folder (for example, `E:\SteamLibrary\steamapps\common\TheLongDark\Mods`).
3. Launch the game and load a save. The report is written under MelonLoader's `UserData` folder once blueprints have been loaded (and again when the mod shuts down).

To use pack-name filtering in CraftingSearch, install both `ModdedBlueprintUtils.dll` and `CraftingSearch.dll` in `Mods`.

## Output File

The report is written to:

`TheLongDark\UserData\BlueprintSources.txt`

Each line has the form `id = source` and, when a display name is available, `| Name: <display name>`.

Pack labels usually look like `Friendly Name [File.modcomponent]`. If the pack's display name matches the filename (ignoring case), the label may show only the `.modcomponent` filename.

True copies of a vanilla blueprint are tagged `[Copied Vanilla Blueprint]` and use a disambiguated key such as `BLUEPRINT_… (Copy)`. Separately, if two recipes from one pack share the same `Name`, the second and later entries get a ` (Copy)` or ` (Copy 2)` suffix on the id; those are not tagged `[Copied Vanilla Blueprint]`.

```
BP_CookingPot = ModComponent Pack (Forge Blueprints [ForgeBlueprints.modcomponent]) | Name: Cooking Pot
BP_BedrollFromBlankets = ModComponent Pack (CozyBlankets.modcomponent) | Name: Patchwork Bedroll
BP_blueprint_wolfCap = ModComponent Pack (NorthfolkClothingPack.modcomponent) | Name: Wolfskin Cap
```

Vanilla recipes that were only tweaked in place do not appear. The MelonLoader log also notes any `.modcomponent` packs that contain a `blueprints/` folder but could not be matched to a loaded blueprint.

## API for Other Mods

The public type is `ModdedBlueprintUtils.BlueprintInfoMod`. Other Melons can call it directly or via reflection (CraftingSearch uses reflection so it does not need a compile-time project reference).

**Preferred** — pass the `BlueprintData` instance from the crafting list. This is the only way to identify a copy that still uses a vanilla id:

```csharp
if (BlueprintInfoMod.TryGetModSourceInfo(blueprintData, out string sourceLabel, out string friendlyModName))
{
    // brand-new mod blueprint, or a true copy of a vanilla recipe
}
```

The original vanilla instance returns `false`, even if a copy of that recipe exists.

**String overload** (kept for compatibility) — looks up a uniquely named new blueprint such as `BP_PillowDZ01` by id or display name:

```csharp
BlueprintInfoMod.TryGetModSourceInfo(blueprintIdOrName, out string sourceLabel, out string friendlyModName);
```

A raw vanilla id such as `BLUEPRINT_GEAR_ArrowShaft_A` always returns `false` from the string overload, including when a copy of that blueprint exists. Prefer the instance overload whenever you have a `BlueprintData` object.

`sourceLabel` is the full attribution string (pack label, and `[Copied Vanilla Blueprint]` when applicable). `friendlyModName` is a shorter pack or Melon name suitable for search and UI text.

## CraftingSearch

[CraftingSearch](https://github.com/leatherneck6017/TLD-CraftingSearch) is the intended companion mod. With both installed, typing a pack name in the crafting search bar (for example, `cannery`) can include that pack's new and copied blueprints in the default name/ingredient search. CraftingSearch should call the instance overload above so copies that share a vanilla id are distinguished from the original recipe.

## Issues

If you encounter a bug or have a feature request, please open a report on the GitHub Issues tracker or reach out in the TLD Modding Discord server @leatherneck6017.

## Credits

- Hinterland Studio for creating this amazing game
- Other TLD modders for inspiration and reference materials
- The TLD Modding Discord server for guidance, testing, and feedback
- Modding Framework and Libraries: MelonLoader, Harmony (HarmonyLib), and Il2CppInterop

## Project Structure

- `src/Main.cs` — Melon entry point, `.modcomponent` pack scanning, Harmony hooks on `BlueprintManager` load methods, attribution, report writing, and the public `TryGetModSourceInfo` API.

## Building from Source

1. Clone or open the project in Visual Studio / .NET SDK targeting `net6.0`.
2. Update `TLDPath` in `ModdedBlueprintUtils.csproj` to point to your The Long Dark installation directory (currently set to `E:\SteamLibrary\steamapps\common\TheLongDark`).
3. Build the project so references from `$(TLDPath)\MelonLoader\net6\*.dll` and `$(TLDPath)\MelonLoader\Il2CppAssemblies\*.dll` resolve.

## AI Disclosure

AI tools were consulted during the development of this mod as part of my learning process for exploring The Long Dark's IL2CPP APIs, Harmony patching, and troubleshooting. All code was tested and verified in-game.

## License

This project is open source and available under the [MIT License](LICENSE.txt).
