# Changelog

All notable changes to this project will be documented in this file.

## 1.0.0 - 24-Nov-26

Initial public release of **ModdedBlueprintUtils** (renamed from BlueprintInfo).

### Added

- Attributes loaded blueprints to the `.modcomponent` pack or Melon that added them.
- Writes `UserData\BlueprintSources.txt` with source labels and resolved in-game display names.
- Public API for other mods:
  - `TryGetModSourceInfo(object blueprintData, out string sourceLabel, out string friendlyModName)` (preferred; identifies copies that share a vanilla id).
  - `TryGetModSourceInfo(string blueprintIdOrName, out string sourceLabel, out string friendlyModName)` (kept for uniquely named new blueprints; raw vanilla ids return false).
- Display names are resolved through `BlueprintData.GetDisplayedNameWithCount()` and `GetCraftedResult().GetCraftingDisplayName()`, with pack localization and `Localization.Get` as fallbacks.
- Hardened `Localization.Get` lookup for Il2Cpp string types; failed lookups are not cached permanently so they can succeed after language tables load.

### Changed

- Harmony patching is limited to four `Il2CppTLD.Gear.BlueprintManager` load/add methods: `InitializeBlueprints`, `LoadAddressableBlueprints`, `LoadAllUserBlueprints`, and `LoadUserBlueprint(string)`. Query methods such as `IsBlueprintUnlocked` are no longer patched. This removes the crafting-panel lag caused by earlier broad, keyword-based hooking.
- Attribution is per `BlueprintData` instance. Vanilla recipes are snapshotted after vanilla init; only instances added later are recorded. The same object appearing in both `m_AllBlueprints` and `m_UserBlueprints` is not treated as a copy.
- Only brand-new mod blueprints and true copies of vanilla blueprints (a second, distinct instance) are listed. Copies are tagged `[Copied Vanilla Blueprint]`.
- Project and Melon name is now `ModdedBlueprintUtils` (namespace `ModdedBlueprintUtils`; class remains `BlueprintInfoMod`). The project file is `ModdedBlueprintUtils.csproj` (the earlier `ModdedBlueprintIUtils.csproj` typo is corrected).

### Removed

- `[Modified Vanilla Blueprint]` labeling. Vanilla blueprints that were only modified in place are omitted from `BlueprintSources.txt` and return no source from `TryGetModSourceInfo`. This also stops vanilla recipes (for example, an extra cannery arrow that was only tweaked in place) from being mis-tagged as pack-added.

### Fixed

- Two recipes in one pack that share the same blueprint `Name` (for example, Cannery Manufacturing's `SkilletFromScrapMetal`, used by both the skillet and recycled-can recipes) are both listed. The second entry uses a ` (Copy)` suffix on the id and keeps its own display name, instead of hiding one recipe.
