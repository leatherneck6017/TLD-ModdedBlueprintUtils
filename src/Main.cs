using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using HarmonyLib;
using MelonLoader;
using MelonLoader.Utils;

[assembly: MelonInfo(typeof(ModdedBlueprintUtils.BlueprintInfoMod), "ModdedBlueprintUtils", "1.0.0", "leatherneck6017")]
[assembly: MelonGame("Hinterland", "TheLongDark")]

namespace ModdedBlueprintUtils
{
    public class BlueprintInfoMod : MelonMod
    {
        private sealed class PackEntry
        {
            public string FileName;
            public string FriendlyName;
            public string PackLabel;
        }

        internal static readonly Dictionary<string, string> ModdedBlueprintToSource =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        internal static readonly Dictionary<string, string> BlueprintDisplayNames =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Maps normalized blueprint tokens/identifiers -> pack info
        private static readonly Dictionary<string, PackEntry> ModComponentIndex =
            new Dictionary<string, PackEntry>(StringComparer.OrdinalIgnoreCase);

        // Maps normalized blueprint/gear/loc tokens -> resolved in-game display names from .modcomponent files
        private static readonly Dictionary<string, string> PackResolvedDisplayNames =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Caches successful Localization.Get(key) lookups only (failures are retried later)
        private static readonly Dictionary<string, string> LocalizationResultCache =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Tracks .modcomponent pack filenames that actually contain a 'blueprints/' folder
        private static readonly HashSet<string> BlueprintBearingPacks =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Tracks which blueprint-bearing .modcomponent packs were matched to at least one loaded blueprint
        private static readonly HashSet<string> MatchedBlueprintPacks =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Authoritative map: Il2Cpp BlueprintData instance key -> source label
        private static readonly Dictionary<string, string> ModdedInstanceToSource =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Entries written to BlueprintSources.txt, including disambiguated copy keys
        private static readonly Dictionary<string, string> ReportEntries =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private static readonly Dictionary<string, string> ReportDisplayNames =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private static readonly Dictionary<string, int> CopyReportCounts =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        private static readonly Dictionary<string, string> InstanceKeyToReportKey =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Vanilla BlueprintData instances / ids captured after vanilla init (not tagged)
        private static readonly HashSet<string> VanillaInstanceKeys =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static readonly HashSet<string> VanillaBlueprintIds =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static readonly HashSet<string> KnownInstanceKeys =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static Type _cachedBlueprintManagerType;
        private static bool _searchedBlueprintManagerType;

        private static MethodInfo _cachedLocalizationGetMethod;
        private static bool _searchedLocalizationGetMethod;

        private static string _lastZeroMatchSummary = string.Empty;
        private static bool _initialReportWritten;

        [ThreadStatic]
        private static bool _isInsidePatch;

        [ThreadStatic]
        private static Stack<HashSet<string>> _preCallInstanceKeys;

        private static readonly HashSet<string> IgnoredPrefixes = new HashSet<string>(StringComparer.Ordinal)
        {
            "System",
            "Microsoft",
            "mscorlib",
            "netstandard",
            "UnityEngine",
            "Unity",
            "Il2Cpp",
            "Unhollower",
            "MelonLoader",
            "0Harmony",
            "HarmonyLib",
            "Assembly-CSharp",
            "ModdedBlueprintUtils"
        };

        private static string ModsDirectoryPath =>
            Path.Combine(MelonEnvironment.GameRootDirectory, "Mods");

        private static string OutputFilePath =>
            Path.Combine(MelonEnvironment.UserDataDirectory, "BlueprintSources.txt");

        public override void OnInitializeMelon()
        {
            LoggerInstance.Msg("Initializing ModdedBlueprintUtils for TLD 2.60...");
            ScanModComponentPacks();
            TryPatchBlueprintManagerMethods();
        }

        // Thread-safe lookup for CraftingSearch (can be called directly or via reflection).
        // This string overload is backward compatible. It only returns a source for uniquely
        // named, newly added blueprints (for example BP_PillowDZ01). Raw vanilla ids such as
        // BLUEPRINT_GEAR_* return false — including when a copy of that vanilla blueprint
        // exists — so in-place-modified originals are never tagged. When you have a
        // BlueprintData instance, prefer the object overload below so copies that share a
        // vanilla id can be identified.
        public static bool TryGetModSourceInfo(string blueprintIdOrName, out string sourceLabel, out string friendlyModName)
        {
            sourceLabel = null;
            friendlyModName = null;

            if (string.IsNullOrWhiteSpace(blueprintIdOrName))
                return false;

            string trimmed = blueprintIdOrName.Trim();

            lock (ModdedBlueprintToSource)
            {
                if (IsVanillaBlueprintId(trimmed))
                    return false;

                if (ModdedBlueprintToSource.TryGetValue(trimmed, out string exactSource) &&
                    !string.IsNullOrWhiteSpace(exactSource))
                {
                    sourceLabel = exactSource;
                    friendlyModName = ExtractFriendlyNameForSearch(exactSource);
                    return true;
                }

                string normTarget = NormalizeKey(trimmed);
                if (string.IsNullOrWhiteSpace(normTarget))
                    return false;

                foreach (var kvp in ModdedBlueprintToSource)
                {
                    if (NormalizeKey(kvp.Key) == normTarget)
                    {
                        sourceLabel = kvp.Value;
                        friendlyModName = ExtractFriendlyNameForSearch(kvp.Value);
                        return true;
                    }
                }

                foreach (var kvp in BlueprintDisplayNames)
                {
                    if (!string.IsNullOrWhiteSpace(kvp.Value) &&
                        NormalizeKey(kvp.Value) == normTarget &&
                        ModdedBlueprintToSource.TryGetValue(kvp.Key, out string srcByName))
                    {
                        sourceLabel = srcByName;
                        friendlyModName = ExtractFriendlyNameForSearch(srcByName);
                        return true;
                    }
                }
            }

            return false;
        }

        // Preferred CraftingSearch lookup. Pass the BlueprintData instance (Il2CppTLD.Gear.BlueprintData)
        // shown in the crafting list. Returns source info for that specific instance when it is a
        // brand-new blueprint or a new copy of a vanilla blueprint. Returns false for the original
        // vanilla instance, even if it was later modified in place.
        public static bool TryGetModSourceInfo(object blueprintData, out string sourceLabel, out string friendlyModName)
        {
            sourceLabel = null;
            friendlyModName = null;

            if (blueprintData == null)
                return false;

            string instanceKey = GetInstanceKey(blueprintData);
            if (string.IsNullOrEmpty(instanceKey))
                return false;

            lock (ModdedBlueprintToSource)
            {
                if (VanillaInstanceKeys.Contains(instanceKey))
                    return false;

                if (ModdedInstanceToSource.TryGetValue(instanceKey, out string source) &&
                    !string.IsNullOrWhiteSpace(source))
                {
                    sourceLabel = source;
                    friendlyModName = ExtractFriendlyNameForSearch(source);
                    return true;
                }
            }

            return false;
        }

        private static string ExtractFriendlyNameForSearch(string sourceMod)
        {
            if (string.IsNullOrWhiteSpace(sourceMod))
                return "Mod";

            Match friendlyWithFile = Regex.Match(
                sourceMod,
                @"ModComponent Pack \((.+?)\s+\[[^\]]+\.modcomponent\]\)",
                RegexOptions.IgnoreCase);
            if (friendlyWithFile.Success && friendlyWithFile.Groups.Count > 1)
                return friendlyWithFile.Groups[1].Value.Trim();

            Match packMatch = Regex.Match(sourceMod, @"ModComponent Pack \(([^)]+)\)", RegexOptions.IgnoreCase);
            if (packMatch.Success && packMatch.Groups.Count > 1)
                return Path.GetFileNameWithoutExtension(packMatch.Groups[1].Value.Trim());

            int parenIdx = sourceMod.IndexOf('(');
            return parenIdx > 0 ? sourceMod.Substring(0, parenIdx).Trim() : sourceMod.Trim();
        }

        private void ScanModComponentPacks()
        {
            ModComponentIndex.Clear();
            PackResolvedDisplayNames.Clear();
            LocalizationResultCache.Clear();
            BlueprintBearingPacks.Clear();
            MatchedBlueprintPacks.Clear();
            ModdedInstanceToSource.Clear();
            ReportEntries.Clear();
            ReportDisplayNames.Clear();
            CopyReportCounts.Clear();
            InstanceKeyToReportKey.Clear();
            VanillaInstanceKeys.Clear();
            VanillaBlueprintIds.Clear();
            KnownInstanceKeys.Clear();
            _lastZeroMatchSummary = string.Empty;
            _initialReportWritten = false;
            _searchedLocalizationGetMethod = false;
            _cachedLocalizationGetMethod = null;

            if (!Directory.Exists(ModsDirectoryPath))
                return;

            string[] packFiles;
            try
            {
                packFiles = Directory.GetFiles(ModsDirectoryPath, "*.modcomponent", SearchOption.TopDirectoryOnly);
            }
            catch (Exception ex)
            {
                LoggerInstance.Warning($"Failed enumerating .modcomponent packs: {ex.Message}");
                return;
            }

            int itemOnlySkippedCount = 0;

            foreach (string packPath in packFiles.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                string fileName = Path.GetFileName(packPath);
                string fallbackName = Path.GetFileNameWithoutExtension(fileName);

                try
                {
                    using (FileStream fs = File.OpenRead(packPath))
                    using (ZipArchive archive = new ZipArchive(fs, ZipArchiveMode.Read, leaveOpen: false))
                    {
                        var blueprintEntries = new List<ZipArchiveEntry>();
                        string friendlyName = null;

                        foreach (ZipArchiveEntry entry in archive.Entries)
                        {
                            if (string.IsNullOrEmpty(entry.Name))
                                continue;

                            if (IsInsideBlueprintsFolder(entry.FullName))
                            {
                                blueprintEntries.Add(entry);
                            }
                            else if (IsBuildInfoEntry(entry) && entry.Length > 0 && entry.Length < 64 * 1024)
                            {
                                friendlyName = ReadFriendlyNameFromBuildInfo(entry);
                            }
                        }

                        if (blueprintEntries.Count == 0)
                        {
                            itemOnlySkippedCount++;
                            continue;
                        }

                        string resolvedFriendlyName = !string.IsNullOrWhiteSpace(friendlyName)
                            ? friendlyName.Trim()
                            : fallbackName;

                        var packEntry = new PackEntry
                        {
                            FileName = fileName,
                            FriendlyName = resolvedFriendlyName,
                            PackLabel = string.Equals(resolvedFriendlyName, fallbackName, StringComparison.OrdinalIgnoreCase)
                                ? $"ModComponent Pack ({fileName})"
                                : $"ModComponent Pack ({resolvedFriendlyName} [{fileName}])"
                        };

                        BlueprintBearingPacks.Add(fileName);

                        var locIdToText = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        var gearNameToLocId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        var blueprintTextEntries = new List<string>();

                        foreach (ZipArchiveEntry entry in archive.Entries)
                        {
                            if (string.IsNullOrEmpty(entry.Name) || IsBuildInfoEntry(entry))
                                continue;

                            if (!IsTextLikeEntry(entry.Name) || entry.Length <= 0 || entry.Length >= 512 * 1024)
                                continue;

                            string content = null;
                            try
                            {
                                using (var reader = new StreamReader(entry.Open()))
                                {
                                    content = reader.ReadToEnd();
                                }
                            }
                            catch
                            {
                                continue;
                            }

                            if (string.IsNullOrWhiteSpace(content))
                                continue;

                            if (IsInsideBlueprintsFolder(entry.FullName))
                            {
                                string entryNameWithoutExt = Path.GetFileNameWithoutExtension(entry.Name);
                                IndexToken(entryNameWithoutExt, packEntry);
                                ExtractBlueprintIdentifiers(content, packEntry);
                                blueprintTextEntries.Add(content);
                            }
                            else if (entry.Name.IndexOf("localization", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                ParseLocalizationJson(content, locIdToText);
                            }
                            else
                            {
                                ParseGearLocalizationMapping(content, gearNameToLocId, locIdToText);
                            }
                        }

                        for (int i = 0; i < blueprintTextEntries.Count; i++)
                        {
                            ResolveBlueprintDisplayNamesFromJson(
                                blueprintTextEntries[i],
                                gearNameToLocId,
                                locIdToText);
                        }
                    }
                }
                catch (Exception ex)
                {
                    LoggerInstance.Warning($"Could not read '{fileName}' as zip: {ex.Message}");
                }
            }

            LoggerInstance.Msg(
                $"Indexed {ModComponentIndex.Count} blueprint tokens and {PackResolvedDisplayNames.Count} display names across {BlueprintBearingPacks.Count} blueprint-bearing .modcomponent pack(s) (skipped {itemOnlySkippedCount} item-only pack(s)).");
        }

        private static bool IsBuildInfoEntry(ZipArchiveEntry entry)
        {
            if (entry == null || string.IsNullOrEmpty(entry.Name))
                return false;

            return string.Equals(entry.Name, "buildinfo.json", StringComparison.OrdinalIgnoreCase);
        }

        private static string ReadFriendlyNameFromBuildInfo(ZipArchiveEntry entry)
        {
            try
            {
                using (var reader = new StreamReader(entry.Open()))
                {
                    string json = reader.ReadToEnd();
                    string name = ExtractJsonStringField(json, "Name");
                    return !string.IsNullOrWhiteSpace(name) ? name.Trim() : null;
                }
            }
            catch
            {
                return null;
            }
        }

        private static void ParseLocalizationJson(string jsonText, Dictionary<string, string> locIdToText)
        {
            var blockMatches = Regex.Matches(
                jsonText,
                @"""([^""\r\n]+)""\s*:\s*\{([^{}]*)\}",
                RegexOptions.Singleline);

            foreach (Match block in blockMatches)
            {
                if (block.Groups.Count < 3)
                    continue;

                string key = block.Groups[1].Value.Trim();
                string body = block.Groups[2].Value;
                if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(body))
                    continue;

                string englishMatch = ExtractJsonStringField(body, "English");
                string chosen = !string.IsNullOrWhiteSpace(englishMatch)
                    ? englishMatch
                    : ExtractFirstJsonStringValue(body);

                if (!string.IsNullOrWhiteSpace(chosen))
                {
                    locIdToText[key] = chosen.Trim();
                    string normKey = NormalizeKey(key);
                    if (!string.IsNullOrWhiteSpace(normKey) && !PackResolvedDisplayNames.ContainsKey(normKey))
                    {
                        PackResolvedDisplayNames[normKey] = chosen.Trim();
                    }
                }
            }
        }

        private static void ParseGearLocalizationMapping(
            string jsonText,
            Dictionary<string, string> gearNameToLocId,
            Dictionary<string, string> locIdToText)
        {
            string gearName = ExtractJsonStringField(jsonText, "GearName") ??
                              ExtractJsonStringField(jsonText, "Name");
            string locId = ExtractJsonStringField(jsonText, "DisplayNameLocalizationId") ??
                           ExtractJsonStringField(jsonText, "DisplayName") ??
                           ExtractJsonStringField(jsonText, "LocalizedDisplayName");

            if (!string.IsNullOrWhiteSpace(gearName) && !string.IsNullOrWhiteSpace(locId))
            {
                gearNameToLocId[gearName.Trim()] = locId.Trim();

                if (locIdToText.TryGetValue(locId.Trim(), out string displayName))
                {
                    RegisterDisplayNameForKeys(displayName, gearName.Trim(), locId.Trim());
                }
            }
        }

        private static void ResolveBlueprintDisplayNamesFromJson(
            string blueprintJson,
            Dictionary<string, string> gearNameToLocId,
            Dictionary<string, string> locIdToText)
        {
            string bpName = ExtractJsonStringField(blueprintJson, "Name") ??
                            ExtractJsonStringField(blueprintJson, "BlueprintName");
            string craftedResult = ExtractJsonStringField(blueprintJson, "CraftedResult") ??
                                   ExtractJsonStringField(blueprintJson, "m_CraftedResult");

            string resolvedName = null;
            string locId = null;

            if (!string.IsNullOrWhiteSpace(craftedResult))
            {
                if (gearNameToLocId.TryGetValue(craftedResult.Trim(), out string mappedLoc))
                {
                    locId = mappedLoc;
                }
                else if (craftedResult.StartsWith("GEAR_", StringComparison.OrdinalIgnoreCase))
                {
                    string gameplayKey = "GAMEPLAY_" + craftedResult.Substring(5);
                    if (locIdToText.ContainsKey(gameplayKey))
                    {
                        locId = gameplayKey;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(locId) && !string.IsNullOrWhiteSpace(bpName))
            {
                if (gearNameToLocId.TryGetValue(bpName.Trim(), out string bpMappedLoc))
                {
                    locId = bpMappedLoc;
                }
                else
                {
                    string gameplayKey = "GAMEPLAY_" + bpName.Trim();
                    if (locIdToText.ContainsKey(gameplayKey))
                    {
                        locId = gameplayKey;
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(locId) && locIdToText.TryGetValue(locId, out string locValue))
            {
                resolvedName = locValue;
            }
            else if (!string.IsNullOrWhiteSpace(craftedResult) &&
                     locIdToText.TryGetValue(craftedResult.Trim(), out string directCraftedLoc))
            {
                resolvedName = directCraftedLoc;
            }
            else if (!string.IsNullOrWhiteSpace(bpName) &&
                     locIdToText.TryGetValue(bpName.Trim(), out string directBpLoc))
            {
                resolvedName = directBpLoc;
            }

            if (!string.IsNullOrWhiteSpace(resolvedName))
            {
                RegisterDisplayNameForKeys(resolvedName, bpName, craftedResult, locId);
            }
        }

        private static void RegisterDisplayNameForKeys(string displayName, params string[] keys)
        {
            if (string.IsNullOrWhiteSpace(displayName) || keys == null)
                return;

            string trimmedName = displayName.Trim();

            for (int i = 0; i < keys.Length; i++)
            {
                string k = keys[i];
                if (string.IsNullOrWhiteSpace(k))
                    continue;

                string norm = NormalizeKey(k);
                if (!string.IsNullOrWhiteSpace(norm) && !PackResolvedDisplayNames.ContainsKey(norm))
                {
                    PackResolvedDisplayNames[norm] = trimmedName;
                }

                if (!k.StartsWith("BP_", StringComparison.OrdinalIgnoreCase) &&
                    !k.StartsWith("BLUEPRINT_", StringComparison.OrdinalIgnoreCase))
                {
                    string bpNorm = NormalizeKey("BP_" + k);
                    if (!string.IsNullOrWhiteSpace(bpNorm) && !PackResolvedDisplayNames.ContainsKey(bpNorm))
                    {
                        PackResolvedDisplayNames[bpNorm] = trimmedName;
                    }
                }
            }
        }

        private static string ExtractJsonStringField(string text, string fieldName)
        {
            if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(fieldName))
                return null;

            Match match = Regex.Match(
                text,
                @"""" + Regex.Escape(fieldName) + @"""\s*:\s*""([^""\r\n]*)""",
                RegexOptions.IgnoreCase);

            return match.Success && match.Groups.Count > 1 ? match.Groups[1].Value.Trim() : null;
        }

        private static string ExtractFirstJsonStringValue(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;

            Match match = Regex.Match(text, @":\s*""([^""\r\n]+)""");
            return match.Success && match.Groups.Count > 1 ? match.Groups[1].Value.Trim() : null;
        }

        private static bool IsInsideBlueprintsFolder(string fullName)
        {
            if (string.IsNullOrWhiteSpace(fullName))
                return false;

            string normalized = fullName.Replace('\\', '/').TrimStart('/');
            return normalized.StartsWith("blueprints/", StringComparison.OrdinalIgnoreCase) ||
                   normalized.IndexOf("/blueprints/", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsTextLikeEntry(string name)
        {
            string ext = Path.GetExtension(name).ToLowerInvariant();
            return ext == ".json" || ext == ".xml" || ext == ".txt" || ext == ".yaml" || ext == ".yml" || ext == ".csv";
        }

        private static void ExtractBlueprintIdentifiers(string text, PackEntry packEntry)
        {
            if (string.IsNullOrWhiteSpace(text))
                return;

            var matches = Regex.Matches(
                text,
                @"(?:""?(?:Name|BlueprintName|m_BlueprintName|CraftedResult|m_CraftedResult)""?\s*[:=]\s*""?([A-Za-z0-9_\-\. ]+)""?)",
                RegexOptions.IgnoreCase);

            foreach (Match match in matches)
            {
                if (match.Groups.Count > 1)
                {
                    string token = match.Groups[1].Value.Trim();
                    if (token.Length >= 3)
                    {
                        IndexToken(token, packEntry);
                    }
                }
            }
        }

        private static void IndexToken(string token, PackEntry packEntry)
        {
            if (string.IsNullOrWhiteSpace(token) || packEntry == null)
                return;

            string normalized = NormalizeKey(token);
            if (string.IsNullOrWhiteSpace(normalized))
                return;

            if (!ModComponentIndex.ContainsKey(normalized))
            {
                ModComponentIndex[normalized] = packEntry;
            }
        }

        private static string NormalizeKey(string value)
        {
            return Regex.Replace(value.Trim(), @"[^A-Za-z0-9]+", string.Empty).ToLowerInvariant();
        }

        private static PackEntry MatchModComponentPack(string candidateId)
        {
            if (string.IsNullOrWhiteSpace(candidateId))
                return null;

            string normId = NormalizeKey(candidateId);
            if (ModComponentIndex.TryGetValue(normId, out PackEntry exactMatch))
                return exactMatch;

            foreach (var kvp in ModComponentIndex)
            {
                if (kvp.Key.Length >= 5 && normId.Contains(kvp.Key))
                    return kvp.Value;
            }

            return null;
        }

        private void TryPatchBlueprintManagerMethods()
        {
            Type bpManagerType = GetCachedBlueprintManagerType();
            if (bpManagerType == null)
            {
                LoggerInstance.Warning("Could not find Il2CppTLD.Gear.BlueprintManager; blueprint hooks were not applied.");
                return;
            }

            int patchedCount = 0;
            patchedCount += PatchBlueprintManagerMethod(
                bpManagerType,
                "InitializeBlueprints",
                expectedParamCount: 0,
                includePrefix: false,
                includeVanillaSnapshotPostfix: true,
                includeAttributePostfix: true);
            patchedCount += PatchBlueprintManagerMethod(
                bpManagerType,
                "LoadAddressableBlueprints",
                expectedParamCount: 0,
                includePrefix: false,
                includeVanillaSnapshotPostfix: true,
                includeAttributePostfix: true);
            patchedCount += PatchBlueprintManagerMethod(
                bpManagerType,
                "LoadAllUserBlueprints",
                expectedParamCount: 0,
                includePrefix: true,
                includeVanillaSnapshotPostfix: false,
                includeAttributePostfix: true);
            patchedCount += PatchBlueprintManagerMethod(
                bpManagerType,
                "LoadUserBlueprint",
                expectedParamCount: 1,
                includePrefix: true,
                includeVanillaSnapshotPostfix: false,
                includeAttributePostfix: true);

            LoggerInstance.Msg($"Hooked {patchedCount} BlueprintManager load/add method(s).");
        }

        private int PatchBlueprintManagerMethod(
            Type bpManagerType,
            string methodName,
            int expectedParamCount,
            bool includePrefix,
            bool includeVanillaSnapshotPostfix,
            bool includeAttributePostfix)
        {
            MethodInfo target = FindDeclaredMethod(bpManagerType, methodName, expectedParamCount);
            if (target == null)
            {
                LoggerInstance.Warning($"BlueprintManager.{methodName} (arity {expectedParamCount}) not found; skipped.");
                return 0;
            }

            try
            {
                HarmonyMethod prefix = includePrefix
                    ? new HarmonyMethod(typeof(BlueprintInfoMod), nameof(CaptureExistingInstancesPrefix))
                    : null;

                if (includeVanillaSnapshotPostfix)
                {
                    var snapshotPostfix = new HarmonyMethod(typeof(BlueprintInfoMod), nameof(SnapshotVanillaInstancesPostfix));
                    snapshotPostfix.priority = 10000;
                    HarmonyInstance.Patch(target, prefix: prefix, postfix: snapshotPostfix);
                    prefix = null;
                }

                if (includeAttributePostfix)
                {
                    if (includePrefix)
                    {
                        // Finalizer always pops the prefix snapshot, even if the original method throws
                        var finalizer = new HarmonyMethod(typeof(BlueprintInfoMod), nameof(AttributeUserLoadedBlueprintsFinalizer));
                        HarmonyInstance.Patch(target, prefix: prefix, finalizer: finalizer);
                    }
                    else
                    {
                        var attributePostfix = new HarmonyMethod(typeof(BlueprintInfoMod), nameof(AttributePostVanillaInitPostfix));
                        attributePostfix.priority = -10000;
                        HarmonyInstance.Patch(target, prefix: prefix, postfix: attributePostfix);
                    }
                }
                else if (prefix != null)
                {
                    HarmonyInstance.Patch(target, prefix: prefix);
                }

                return 1;
            }
            catch (Exception ex)
            {
                LoggerInstance.Warning($"Failed patching BlueprintManager.{methodName}: {ex.Message}");
                return 0;
            }
        }

        private static MethodInfo FindDeclaredMethod(Type type, string methodName, int expectedParamCount)
        {
            if (type == null || string.IsNullOrEmpty(methodName))
                return null;

            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Static |
                                       BindingFlags.Public | BindingFlags.NonPublic |
                                       BindingFlags.DeclaredOnly;

            MethodInfo[] methods;
            try
            {
                methods = type.GetMethods(flags);
            }
            catch
            {
                return null;
            }

            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo method = methods[i];
                if (method == null || method.Name != methodName)
                    continue;

                try
                {
                    if (method.GetParameters().Length == expectedParamCount)
                        return method;
                }
                catch
                {
                    // Ignore methods whose parameters cannot be inspected
                }
            }

            return null;
        }

        private static void CaptureExistingInstancesPrefix()
        {
            if (_preCallInstanceKeys == null)
                _preCallInstanceKeys = new Stack<HashSet<string>>();

            try
            {
                _preCallInstanceKeys.Push(CollectCurrentInstanceKeys());
            }
            catch
            {
                _preCallInstanceKeys.Push(new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            }
        }

        private static void SnapshotVanillaInstancesPostfix()
        {
            if (_isInsidePatch)
                return;

            _isInsidePatch = true;
            try
            {
                SnapshotCurrentInstancesAsVanilla();
            }
            catch
            {
                // Lists may not be ready yet
            }
            finally
            {
                _isInsidePatch = false;
            }
        }

        private static void AttributePostVanillaInitPostfix(MethodBase __originalMethod)
        {
            if (_isInsidePatch)
                return;

            _isInsidePatch = true;
            try
            {
                // Only attribute late additions if a vanilla snapshot already exists.
                // An empty snapshot means addressable/vanilla lists were not ready; do not
                // treat every later-visible vanilla instance as a mod addition.
                if (VanillaInstanceKeys.Count == 0)
                    return;

                AttributeNewInstances(
                    DetectCallingMod(),
                    beforeKeys: null);
            }
            catch
            {
                // Prevent postfix exceptions from breaking game code
            }
            finally
            {
                _isInsidePatch = false;
            }
        }

        private static Exception AttributeUserLoadedBlueprintsFinalizer(Exception __exception)
        {
            HashSet<string> beforeKeys = null;
            if (_preCallInstanceKeys != null && _preCallInstanceKeys.Count > 0)
                beforeKeys = _preCallInstanceKeys.Pop();

            if (_isInsidePatch)
                return __exception;

            _isInsidePatch = true;
            try
            {
                AttributeNewInstances(
                    DetectCallingMod(),
                    beforeKeys);
            }
            catch
            {
                // Prevent patch exceptions from breaking game code
            }
            finally
            {
                _isInsidePatch = false;
            }

            return __exception;
        }

        private static Type GetCachedBlueprintManagerType()
        {
            if (_searchedBlueprintManagerType)
                return _cachedBlueprintManagerType;

            _searchedBlueprintManagerType = true;

            Assembly[] loadedAssemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < loadedAssemblies.Length; i++)
            {
                Assembly asm = loadedAssemblies[i];
                if (asm == null)
                    continue;

                Type type;
                try
                {
                    type = asm.GetType("Il2CppTLD.Gear.BlueprintManager", throwOnError: false, ignoreCase: false);
                }
                catch
                {
                    type = null;
                }

                if (type != null)
                {
                    _cachedBlueprintManagerType = type;
                    return _cachedBlueprintManagerType;
                }
            }

            return null;
        }

        private static HashSet<string> CollectCurrentInstanceKeys()
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (BlueprintEntry entry in EnumerateBlueprintEntries())
            {
                if (!string.IsNullOrEmpty(entry.Key))
                    keys.Add(entry.Key);
            }

            return keys;
        }

        private static void SnapshotCurrentInstancesAsVanilla()
        {
            lock (ModdedBlueprintToSource)
            {
                foreach (BlueprintEntry entry in EnumerateBlueprintEntries())
                {
                    if (string.IsNullOrEmpty(entry.Key))
                        continue;

                    // Already classified as a mod-added instance (e.g. LoadAllUserBlueprints
                    // ran inside InitializeBlueprints before this snapshot).
                    if (ModdedInstanceToSource.ContainsKey(entry.Key))
                        continue;

                    VanillaInstanceKeys.Add(entry.Key);
                    KnownInstanceKeys.Add(entry.Key);
                    if (!string.IsNullOrWhiteSpace(entry.Name))
                        VanillaBlueprintIds.Add(entry.Name);
                }
            }
        }

        private static void AttributeNewInstances(string stackSourceMod, HashSet<string> beforeKeys)
        {
            lock (ModdedBlueprintToSource)
            {
                bool anyRecorded = false;
                var seenInstanceKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (BlueprintEntry entry in EnumerateBlueprintEntries())
                {
                    if (string.IsNullOrEmpty(entry.Key) || !seenInstanceKeys.Add(entry.Key))
                        continue;

                    // Original vanilla instance (in-place modifications stay untagged)
                    if (VanillaInstanceKeys.Contains(entry.Key))
                        continue;

                    if (ModdedInstanceToSource.ContainsKey(entry.Key))
                        continue;

                    // Present before this LoadUser* call but never snapshotted as vanilla.
                    // Leave untagged rather than guessing (could be late addressable content).
                    if (beforeKeys != null && beforeKeys.Contains(entry.Key))
                    {
                        KnownInstanceKeys.Add(entry.Key);
                        continue;
                    }

                    bool isTrueCopy = !string.IsNullOrWhiteSpace(entry.Name) &&
                                      VanillaBlueprintIds.Contains(entry.Name);

                    string resolvedSource = ResolveNonVanillaSource(
                        stackSourceMod,
                        entry.Name,
                        isTrueCopy,
                        out string matchedPackFile);
                    if (string.IsNullOrEmpty(resolvedSource))
                        continue;

                    string displayName = ResolveInGameDisplayName(entry.Instance, entry.Name);
                    if (RecordModdedBlueprint(
                        entry.Key,
                        entry.Name,
                        isTrueCopy,
                        resolvedSource,
                        matchedPackFile,
                        displayName,
                        deferFileWrite: true))
                    {
                        anyRecorded = true;
                    }

                    KnownInstanceKeys.Add(entry.Key);
                }

                if (!_initialReportWritten)
                {
                    // Skip an empty pre-load pass (lists not ready yet). Once vanilla
                    // has been snapshotted or any blueprint is visible, write once even
                    // if nothing was recorded so a stale BlueprintSources.txt is cleared,
                    // and log the zero-match summary.
                    if (anyRecorded || VanillaInstanceKeys.Count > 0 || seenInstanceKeys.Count > 0)
                    {
                        WriteReportLocked(forceZeroMatchLog: true);
                        _initialReportWritten = true;
                        Melon<BlueprintInfoMod>.Logger.Msg($"Wrote modded blueprint source map to {OutputFilePath}");
                    }
                }
                else if (anyRecorded)
                {
                    WriteReportLocked(forceZeroMatchLog: false);
                }
            }
        }

        private readonly struct BlueprintEntry
        {
            public readonly object Instance;
            public readonly string Key;
            public readonly string Name;

            public BlueprintEntry(object instance, string key, string name)
            {
                Instance = instance;
                Key = key;
                Name = name;
            }
        }

        private static List<BlueprintEntry> EnumerateBlueprintEntries()
        {
            var results = new List<BlueprintEntry>();

            Type bpManagerType = GetCachedBlueprintManagerType();
            if (bpManagerType == null)
                return results;

            object instance = TryReadMemberValue(bpManagerType, null, "s_Instance") ??
                              TryReadMemberValue(bpManagerType, null, "Instance");
            if (instance == null)
                return results;

            foreach (string listMember in new[] { "m_AllBlueprints", "m_UserBlueprints" })
            {
                object listObj = TryReadMemberValue(bpManagerType, instance, listMember);
                if (listObj == null)
                    continue;

                int count = Convert.ToInt32(TryReadMemberValue(listObj.GetType(), listObj, "Count") ?? 0);
                var indexer = listObj.GetType().GetProperty("Item");
                if (indexer == null)
                    continue;

                for (int i = 0; i < count; i++)
                {
                    object entry = null;
                    try
                    {
                        entry = indexer.GetValue(listObj, new object[] { i });
                    }
                    catch
                    {
                        continue;
                    }

                    if (entry == null)
                        continue;

                    results.Add(new BlueprintEntry(entry, GetInstanceKey(entry), ExtractNameFromObject(entry)));
                }
            }

            return results;
        }

        private static string GetInstanceKey(object obj)
        {
            if (obj == null)
                return null;

            for (Type type = obj.GetType(); type != null; type = type.BaseType)
            {
                const BindingFlags declared = BindingFlags.Instance | BindingFlags.Public |
                                              BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

                foreach (string memberName in new[] { "Pointer", "m_CachedPtr", "cachedPtr" })
                {
                    object val = null;
                    try
                    {
                        PropertyInfo prop = type.GetProperty(memberName, declared);
                        if (prop != null && prop.GetIndexParameters().Length == 0)
                            val = prop.GetValue(obj);
                    }
                    catch
                    {
                        val = null;
                    }

                    if (val == null)
                    {
                        try
                        {
                            FieldInfo field = type.GetField(memberName, declared);
                            if (field != null)
                                val = field.GetValue(obj);
                        }
                        catch
                        {
                            val = null;
                        }
                    }

                    string formatted = FormatPointerValue(val);
                    if (!string.IsNullOrEmpty(formatted))
                        return formatted;
                }
            }

            try
            {
                MethodInfo getInstanceId = obj.GetType().GetMethod(
                    "GetInstanceID",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null,
                    Type.EmptyTypes,
                    null);
                if (getInstanceId != null)
                {
                    object id = getInstanceId.Invoke(obj, null);
                    if (id is int intId && intId != 0)
                        return "uid:" + intId.ToString();
                }
            }
            catch
            {
                // Fall through
            }

            return null;
        }

        private static string FormatPointerValue(object val)
        {
            if (val is IntPtr ptr && ptr != IntPtr.Zero)
                return "ptr:" + ptr.ToInt64().ToString("X");

            if (val is UIntPtr uptr && uptr != UIntPtr.Zero)
                return "ptr:" + uptr.ToUInt64().ToString("X");

            if (val is long longPtr && longPtr != 0)
                return "ptr:" + longPtr.ToString("X");

            if (val is ulong ulongPtr && ulongPtr != 0)
                return "ptr:" + ulongPtr.ToString("X");

            return null;
        }

        private static string ResolveInGameDisplayName(object blueprintEntry, string blueprintId)
        {
            if (blueprintEntry != null)
            {
                string displayed = ToManagedString(TryInvokeInstanceMethod(blueprintEntry, "GetDisplayedNameWithCount"));
                if (IsResolvedDisplayName(displayed, blueprintId))
                    return displayed.Trim();

                object craftedResult = TryInvokeInstanceMethod(blueprintEntry, "GetCraftedResult");
                if (craftedResult != null)
                {
                    string craftedName = ToManagedString(TryInvokeInstanceMethod(craftedResult, "GetCraftingDisplayName"));
                    if (IsResolvedDisplayName(craftedName, blueprintId))
                        return craftedName.Trim();
                }
            }

            var candidateKeys = new List<string>();
            if (!string.IsNullOrWhiteSpace(blueprintId))
            {
                candidateKeys.Add(blueprintId);

                if (blueprintId.StartsWith("BP_", StringComparison.OrdinalIgnoreCase))
                {
                    string suffix = blueprintId.Substring(3);
                    candidateKeys.Add("GEAR_" + suffix);
                    candidateKeys.Add("DECO_" + suffix);
                    candidateKeys.Add("GAMEPLAY_" + suffix);
                }
                else if (blueprintId.StartsWith("BLUEPRINT_GEAR_", StringComparison.OrdinalIgnoreCase))
                {
                    string suffix = blueprintId.Substring("BLUEPRINT_GEAR_".Length);
                    candidateKeys.Add("GEAR_" + suffix);
                    candidateKeys.Add("GAMEPLAY_" + suffix);
                }
                else if (blueprintId.StartsWith("BLUEPRINT_DECO_", StringComparison.OrdinalIgnoreCase))
                {
                    string suffix = blueprintId.Substring("BLUEPRINT_DECO_".Length);
                    candidateKeys.Add("DECO_" + suffix);
                    candidateKeys.Add("GAMEPLAY_" + suffix);
                }
            }

            for (int i = 0; i < candidateKeys.Count; i++)
            {
                string key = candidateKeys[i];
                if (string.IsNullOrWhiteSpace(key))
                    continue;

                string localized = TryLocalizeStringCached(key);
                if (!string.IsNullOrWhiteSpace(localized) && IsResolvedDisplayName(localized, key))
                    return localized.Trim();
            }

            for (int i = 0; i < candidateKeys.Count; i++)
            {
                string key = candidateKeys[i];
                if (string.IsNullOrWhiteSpace(key))
                    continue;

                string norm = NormalizeKey(key);
                if (!string.IsNullOrWhiteSpace(norm) &&
                    PackResolvedDisplayNames.TryGetValue(norm, out string packName) &&
                    IsResolvedDisplayName(packName, key))
                {
                    return packName.Trim();
                }
            }

            return null;
        }

        private static object TryInvokeInstanceMethod(object obj, string methodName)
        {
            if (obj == null || string.IsNullOrEmpty(methodName))
                return null;

            try
            {
                MethodInfo method = obj.GetType().GetMethod(
                    methodName,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null,
                    Type.EmptyTypes,
                    null);
                if (method == null)
                    return null;

                return method.Invoke(obj, null);
            }
            catch
            {
                return null;
            }
        }

        private static string ToManagedString(object value)
        {
            if (value == null)
                return null;

            if (value is string managed)
                return managed;

            try
            {
                string converted = value.ToString();
                if (string.IsNullOrWhiteSpace(converted))
                    return null;

                string typeName = value.GetType().FullName ?? value.GetType().Name;
                if (string.Equals(converted, typeName, StringComparison.Ordinal) ||
                    string.Equals(converted, value.GetType().Name, StringComparison.Ordinal))
                {
                    return null;
                }

                return converted;
            }
            catch
            {
                return null;
            }
        }

        private static bool IsResolvedDisplayName(string candidate, string originalKey)
        {
            if (string.IsNullOrWhiteSpace(candidate))
                return false;

            string trimmed = candidate.Trim();
            if (string.Equals(trimmed, originalKey?.Trim(), StringComparison.OrdinalIgnoreCase))
                return false;

            if (trimmed.StartsWith("GAMEPLAY_", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("BLUEPRINT_", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("GEAR_", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("DECO_", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("BP_", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return true;
        }

        private static MethodInfo GetCachedLocalizationGetMethod()
        {
            if (_searchedLocalizationGetMethod)
                return _cachedLocalizationGetMethod;

            Assembly[] loadedAssemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < loadedAssemblies.Length; i++)
            {
                Assembly asm = loadedAssemblies[i];
                if (asm == null)
                    continue;

                string asmName = asm.GetName().Name ?? string.Empty;
                if (!asmName.StartsWith("Assembly-CSharp", StringComparison.Ordinal) &&
                    !asmName.Contains("Il2Cpp", StringComparison.Ordinal))
                {
                    continue;
                }

                Type[] types;
                try
                {
                    types = asm.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    types = ex.Types;
                }
                catch
                {
                    continue;
                }

                if (types == null)
                    continue;

                for (int j = 0; j < types.Length; j++)
                {
                    Type t = types[j];
                    if (t == null || t.Name != "Localization")
                        continue;

                    MethodInfo getMethod = FindStaticStringGetMethod(t);
                    if (getMethod != null)
                    {
                        _cachedLocalizationGetMethod = getMethod;
                        _searchedLocalizationGetMethod = true;
                        return _cachedLocalizationGetMethod;
                    }
                }
            }

            if (loadedAssemblies.Any(a => (a?.GetName().Name ?? string.Empty).Contains("Il2Cpp")))
            {
                _searchedLocalizationGetMethod = true;
            }

            return null;
        }

        private static MethodInfo FindStaticStringGetMethod(Type type)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            MethodInfo[] methods;
            try
            {
                methods = type.GetMethods(flags);
            }
            catch
            {
                return null;
            }

            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo method = methods[i];
                if (method == null || method.Name != "Get" || !method.IsStatic)
                    continue;

                ParameterInfo[] parameters;
                try
                {
                    parameters = method.GetParameters();
                }
                catch
                {
                    continue;
                }

                if (parameters.Length == 1 && IsStringLikeType(parameters[0].ParameterType))
                    return method;
            }

            return null;
        }

        private static bool IsStringLikeType(Type type)
        {
            if (type == null)
                return false;

            if (type == typeof(string))
                return true;

            string fullName = type.FullName ?? type.Name;
            return string.Equals(fullName, "Il2CppSystem.String", StringComparison.Ordinal) ||
                   string.Equals(type.Name, "String", StringComparison.Ordinal);
        }

        private static string TryLocalizeStringCached(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
                return null;

            string trimmedKey = key.Trim();
            if (LocalizationResultCache.TryGetValue(trimmedKey, out string cached))
                return cached;

            MethodInfo getMethod = GetCachedLocalizationGetMethod();
            if (getMethod == null)
                return null;

            try
            {
                object result = getMethod.Invoke(null, new object[] { trimmedKey });
                string resolved = ToManagedString(result);
                if (!string.IsNullOrWhiteSpace(resolved) &&
                    !string.Equals(resolved, trimmedKey, StringComparison.Ordinal))
                {
                    string trimmedResolved = resolved.Trim();
                    LocalizationResultCache[trimmedKey] = trimmedResolved;
                    return trimmedResolved;
                }
            }
            catch
            {
                // Ignore invocation errors on early frames; do not cache the failure
            }

            // Do not cache null/unresolved results — localization tables may load later
            return null;
        }

        private static bool IsVanillaBlueprintId(string identifier)
        {
            if (string.IsNullOrWhiteSpace(identifier))
                return false;

            return VanillaBlueprintIds.Contains(identifier);
        }

        private static string ResolveNonVanillaSource(
            string stackSourceMod,
            string identifier,
            bool isTrueCopy,
            out string matchedPackFile)
        {
            matchedPackFile = null;

            PackEntry packMatch = MatchModComponentPack(identifier);
            if (packMatch != null)
            {
                matchedPackFile = packMatch.FileName;

                string baseLabel = (!string.IsNullOrEmpty(stackSourceMod) &&
                                    stackSourceMod.StartsWith("ModComponent", StringComparison.OrdinalIgnoreCase))
                    ? $"{stackSourceMod} -> {packMatch.PackLabel}"
                    : packMatch.PackLabel;

                return isTrueCopy
                    ? $"{baseLabel} [Copied Vanilla Blueprint]"
                    : baseLabel;
            }

            bool hasModCaller = !string.IsNullOrEmpty(stackSourceMod) &&
                                !string.Equals(stackSourceMod, "Unknown / Vanilla or Data-Only", StringComparison.OrdinalIgnoreCase);

            if (isTrueCopy)
            {
                return hasModCaller
                    ? $"{stackSourceMod} [Copied Vanilla Blueprint]"
                    : "Modded / Runtime Copy of Vanilla Blueprint";
            }

            return hasModCaller
                ? stackSourceMod
                : "Custom / Data-Only (Unmatched Pack)";
        }

        private static bool RecordModdedBlueprint(
            string instanceKey,
            string blueprintId,
            bool isTrueCopy,
            string sourceMod,
            string matchedPackFile,
            string displayName,
            bool deferFileWrite)
        {
            if (string.IsNullOrWhiteSpace(instanceKey) || string.IsNullOrWhiteSpace(sourceMod))
                return false;

            lock (ModdedBlueprintToSource)
            {
                if (!string.IsNullOrWhiteSpace(matchedPackFile))
                    MatchedBlueprintPacks.Add(matchedPackFile);

                bool shouldWrite = false;
                if (!ModdedInstanceToSource.ContainsKey(instanceKey))
                {
                    ModdedInstanceToSource[instanceKey] = sourceMod;
                    shouldWrite = true;
                }
                else if (ModdedInstanceToSource[instanceKey] == "Custom / Data-Only (Unmatched Pack)" &&
                         sourceMod != "Custom / Data-Only (Unmatched Pack)")
                {
                    ModdedInstanceToSource[instanceKey] = sourceMod;
                    shouldWrite = true;
                }

                if (!InstanceKeyToReportKey.TryGetValue(instanceKey, out string reportKey))
                {
                    reportKey = BuildReportKey(blueprintId, isTrueCopy, instanceKey);
                    InstanceKeyToReportKey[instanceKey] = reportKey;
                }

                if (!ReportEntries.TryGetValue(reportKey, out string existingReport) ||
                    (existingReport == "Custom / Data-Only (Unmatched Pack)" &&
                     sourceMod != "Custom / Data-Only (Unmatched Pack)"))
                {
                    ReportEntries[reportKey] = sourceMod;
                    shouldWrite = true;
                }

                // String lookup only for uniquely named new blueprints — never for raw vanilla
                // ids, and never once a second distinct instance shares the same Name.
                bool canIndexById = !isTrueCopy &&
                                    !string.IsNullOrWhiteSpace(blueprintId) &&
                                    !VanillaBlueprintIds.Contains(blueprintId);
                if (canIndexById)
                {
                    string trimmedId = blueprintId.Trim();
                    if (!string.Equals(reportKey, trimmedId, StringComparison.OrdinalIgnoreCase))
                    {
                        if (ModdedBlueprintToSource.Remove(blueprintId))
                            shouldWrite = true;
                        if (BlueprintDisplayNames.Remove(blueprintId))
                            shouldWrite = true;
                    }
                    else
                    {
                        if (!ModdedBlueprintToSource.TryGetValue(blueprintId, out string existing) ||
                            (existing == "Custom / Data-Only (Unmatched Pack)" &&
                             sourceMod != "Custom / Data-Only (Unmatched Pack)"))
                        {
                            ModdedBlueprintToSource[blueprintId] = sourceMod;
                            shouldWrite = true;
                        }

                        if (!string.IsNullOrWhiteSpace(displayName))
                        {
                            if (!BlueprintDisplayNames.TryGetValue(blueprintId, out string existingDisplay) ||
                                string.IsNullOrWhiteSpace(existingDisplay))
                            {
                                BlueprintDisplayNames[blueprintId] = displayName;
                                shouldWrite = true;
                            }
                        }
                    }
                }

                if (!string.IsNullOrWhiteSpace(displayName))
                {
                    if (!ReportDisplayNames.TryGetValue(reportKey, out string existingReportDisplay) ||
                        string.IsNullOrWhiteSpace(existingReportDisplay))
                    {
                        ReportDisplayNames[reportKey] = displayName;
                        shouldWrite = true;
                    }
                }

                if (shouldWrite && !deferFileWrite)
                    WriteReportLocked(forceZeroMatchLog: false);

                return shouldWrite;
            }
        }

        private static string BuildReportKey(string blueprintId, bool isTrueCopy, string instanceKey)
        {
            string baseId = !string.IsNullOrWhiteSpace(blueprintId) ? blueprintId.Trim() : instanceKey;

            // Vanilla copies always use a suffixed key so the raw vanilla id stays unlisted.
            // A later non-vanilla instance that reuses an already-listed Name also gets a
            // suffix; [Copied Vanilla Blueprint] is applied only when isTrueCopy is set.
            bool needsCopySuffix = isTrueCopy ||
                                   (!string.IsNullOrWhiteSpace(baseId) && ReportEntries.ContainsKey(baseId));
            if (!needsCopySuffix)
                return baseId;

            if (!CopyReportCounts.TryGetValue(baseId, out int count))
                count = 0;
            count++;
            CopyReportCounts[baseId] = count;
            return count == 1 ? $"{baseId} (Copy)" : $"{baseId} (Copy {count})";
        }

        private static string ExtractNameFromObject(object obj)
        {
            if (obj == null)
                return null;

            string managed = ToManagedString(obj);
            if (obj is string && !string.IsNullOrWhiteSpace(managed))
                return managed;

            var type = obj.GetType();
            foreach (string propName in new[] { "name", "Name" })
            {
                string strVal = ToManagedString(TryReadMemberValue(type, obj, propName));
                if (!string.IsNullOrWhiteSpace(strVal))
                    return strVal;
            }

            return null;
        }

        private static object TryReadMemberValue(Type type, object target, string memberName)
        {
            if (type == null || string.IsNullOrEmpty(memberName))
                return null;

            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Static |
                                       BindingFlags.Public | BindingFlags.NonPublic;

            try
            {
                var prop = type.GetProperty(memberName, flags);
                if (prop != null && prop.GetIndexParameters().Length == 0)
                    return prop.GetValue(target);
            }
            catch
            {
                // Ignore property read failures
            }

            try
            {
                var field = type.GetField(memberName, flags);
                if (field != null)
                    return field.GetValue(target);
            }
            catch
            {
                // Ignore field read failures
            }

            return null;
        }

        private static string DetectCallingMod()
        {
            try
            {
                var stack = new StackTrace(2, false);
                for (int i = 0; i < stack.FrameCount; i++)
                {
                    var frame = stack.GetFrame(i);
                    var method = frame?.GetMethod();
                    var asmName = method?.DeclaringType?.Assembly.GetName().Name;

                    if (string.IsNullOrEmpty(asmName))
                        continue;

                    if (IgnoredPrefixes.Any(prefix => asmName.StartsWith(prefix, StringComparison.Ordinal)))
                        continue;

                    var matchingMelon = MelonBase.RegisteredMelons
                        .FirstOrDefault(m => m.MelonAssembly?.Assembly?.GetName().Name == asmName);

                    if (matchingMelon != null)
                        return $"{matchingMelon.Info.Name} ({asmName})";

                    return asmName;
                }
            }
            catch
            {
                // Ignore stack walk errors in restricted frames
            }

            return "Unknown / Vanilla or Data-Only";
        }

        private static void LogZeroMatchPacksLocked(bool force)
        {
            if (BlueprintBearingPacks.Count == 0)
                return;

            var unmatchedPacks = BlueprintBearingPacks
                .Where(pack => !MatchedBlueprintPacks.Contains(pack))
                .OrderBy(pack => pack, StringComparer.OrdinalIgnoreCase)
                .ToList();

            string summary = unmatchedPacks.Count == 0
                ? $"All {BlueprintBearingPacks.Count} blueprint-bearing .modcomponent pack(s) matched at least one blueprint."
                : $"{unmatchedPacks.Count} of {BlueprintBearingPacks.Count} blueprint-bearing .modcomponent pack(s) had zero matched blueprints: {string.Join(", ", unmatchedPacks)}";

            if (force || !string.Equals(summary, _lastZeroMatchSummary, StringComparison.Ordinal))
            {
                _lastZeroMatchSummary = summary;
                Melon<BlueprintInfoMod>.Logger.Msg(summary);
            }
        }

        private static void WriteReportLocked(bool forceZeroMatchLog)
        {
            try
            {
                Directory.CreateDirectory(MelonEnvironment.UserDataDirectory);

                var lines = ReportEntries
                    .OrderBy(kvp => kvp.Key)
                    .Select(kvp =>
                    {
                        if (ReportDisplayNames.TryGetValue(kvp.Key, out string displayName) &&
                            !string.IsNullOrWhiteSpace(displayName))
                        {
                            return $"{kvp.Key} = {kvp.Value} | Name: {displayName}";
                        }

                        return $"{kvp.Key} = {kvp.Value}";
                    });

                File.WriteAllLines(OutputFilePath, lines);

                LogZeroMatchPacksLocked(forceZeroMatchLog);
            }
            catch (Exception ex)
            {
                Melon<BlueprintInfoMod>.Logger.Error($"Failed writing blueprint report: {ex.Message}");
            }
        }
    }
}