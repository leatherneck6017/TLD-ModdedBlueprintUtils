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

        // Caches Localization.Get(key) lookups (null value means not yet resolved / unavailable)
        private static readonly Dictionary<string, string> LocalizationResultCache =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Tracks .modcomponent pack filenames that actually contain a 'blueprints/' folder
        private static readonly HashSet<string> BlueprintBearingPacks =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Tracks which blueprint-bearing .modcomponent packs were matched to at least one loaded blueprint
        private static readonly HashSet<string> MatchedBlueprintPacks =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static Type _cachedBlueprintManagerType;
        private static bool _searchedBlueprintManagerType;

        private static MethodInfo _cachedLocalizationGetMethod;
        private static bool _searchedLocalizationGetMethod;

        private static string _lastZeroMatchSummary = string.Empty;

        [ThreadStatic]
        private static bool _isInsidePostfix;

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
            TryPatchCandidateBlueprintMethods();
        }

        // Thread-safe lookup for CraftingSearch (can be called directly or via reflection)
        public static bool TryGetModSourceInfo(string blueprintIdOrName, out string sourceLabel, out string friendlyModName)
        {
            sourceLabel = null;
            friendlyModName = null;

            if (string.IsNullOrWhiteSpace(blueprintIdOrName))
                return false;

            string trimmed = blueprintIdOrName.Trim();

            lock (ModdedBlueprintToSource)
            {
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
            _lastZeroMatchSummary = string.Empty;
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

        private void TryPatchCandidateBlueprintMethods()
        {
            var assemblies = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a =>
                {
                    var name = a.GetName().Name ?? string.Empty;
                    return name.StartsWith("Assembly-CSharp", StringComparison.Ordinal) ||
                           name.Contains("Il2Cpp", StringComparison.Ordinal);
                });

            int patchedCount = 0;

            foreach (var asm in assemblies)
            {
                Type[] types;
                try
                {
                    types = asm.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    types = ex.Types.Where(t => t != null).ToArray();
                }
                catch
                {
                    continue;
                }

                foreach (var type in types)
                {
                    if (type == null || type.Name.IndexOf("Blueprint", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    MethodInfo[] methods;
                    try
                    {
                        methods = type.GetMethods(
                            BindingFlags.Instance | BindingFlags.Static |
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                    }
                    catch
                    {
                        continue;
                    }

                    foreach (var method in methods)
                    {
                        if (method == null || method.IsAbstract || method.ContainsGenericParameters)
                            continue;

                        if (!IsLikelyBlueprintHookTarget(method.Name))
                            continue;

                        try
                        {
                            var postfix = new HarmonyMethod(typeof(BlueprintInfoMod), nameof(GenericBlueprintPostfix));
                            HarmonyInstance.Patch(method, postfix: postfix);
                            patchedCount++;
                        }
                        catch
                        {
                            // Skip methods that HarmonyX cannot patch in IL2CPP
                        }
                    }
                }
            }

            LoggerInstance.Msg($"Hooked {patchedCount} candidate Blueprint methods.");
        }

        private static bool IsLikelyBlueprintHookTarget(string methodName)
        {
            if (methodName.StartsWith("get_", StringComparison.Ordinal) ||
                methodName.StartsWith("set_", StringComparison.Ordinal) ||
                methodName.StartsWith("add_", StringComparison.Ordinal) ||
                methodName.StartsWith("remove_", StringComparison.Ordinal))
            {
                return false;
            }

            if (methodName.IndexOf("Unregister", StringComparison.OrdinalIgnoreCase) >= 0 ||
                methodName.IndexOf("Unium", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return false;
            }

            string[] keywords = { "Add", "Register", "Unlock", "Load", "Init" };
            return keywords.Any(k => methodName.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static void GenericBlueprintPostfix(MethodBase __originalMethod, object __instance, object[] __args)
        {
            if (_isInsidePostfix)
                return;

            _isInsidePostfix = true;
            try
            {
                string sourceMod = DetectCallingMod();

                if (__originalMethod?.DeclaringType?.Name == "BlueprintManager")
                {
                    HarvestBlueprintManagerLists(sourceMod, __originalMethod.Name);
                }

                string blueprintId = ExtractBlueprintIdentifier(__instance, __args, __originalMethod);
                if (string.IsNullOrWhiteSpace(blueprintId))
                    return;

                string resolvedSource = ResolveNonVanillaSource(sourceMod, blueprintId, isTrueCopy: false, out string matchedPackFile);
                if (!string.IsNullOrEmpty(resolvedSource))
                {
                    object sourceObj = FindBlueprintObject(__instance, __args, __originalMethod, blueprintId);
                    string displayName = ResolveInGameDisplayName(sourceObj, blueprintId);

                    RecordModdedBlueprint(
                        blueprintId,
                        resolvedSource,
                        matchedPackFile,
                        displayName,
                        $"{__originalMethod?.DeclaringType?.Name}.{__originalMethod?.Name}",
                        deferFileWrite: false);
                }
            }
            catch
            {
                // Prevent postfix exceptions from breaking game code
            }
            finally
            {
                _isInsidePostfix = false;
            }
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
                    if (t != null && t.FullName == "Il2CppTLD.Gear.BlueprintManager")
                    {
                        _cachedBlueprintManagerType = t;
                        return _cachedBlueprintManagerType;
                    }
                }
            }

            return null;
        }

        private static void HarvestBlueprintManagerLists(string stackSourceMod, string viaMethod)
        {
            try
            {
                Type bpManagerType = GetCachedBlueprintManagerType();
                if (bpManagerType == null)
                    return;

                object instance = TryReadMemberValue(bpManagerType, null, "s_Instance");
                if (instance == null)
                    return;

                bool anyRecorded = false;
                var seenIdsInSweep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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

                        string entryName = ExtractNameFromObject(entry);
                        if (string.IsNullOrWhiteSpace(entryName))
                            continue;

                        // A duplicate BLUEPRINT_* entry in the list represents a true copied vanilla blueprint
                        bool isTrueCopy = LooksLikeVanillaBlueprintId(entryName) && !seenIdsInSweep.Add(entryName);

                        string resolved = ResolveNonVanillaSource(stackSourceMod, entryName, isTrueCopy, out string matchedPackFile);
                        if (!string.IsNullOrEmpty(resolved))
                        {
                            string keyToRecord = isTrueCopy ? $"{entryName} (Copy)" : entryName;
                            string displayName = ResolveInGameDisplayName(entry, entryName);

                            if (RecordModdedBlueprint(
                                keyToRecord,
                                resolved,
                                matchedPackFile,
                                displayName,
                                $"BlueprintManager.{listMember} (via {viaMethod})",
                                deferFileWrite: true))
                            {
                                anyRecorded = true;
                            }
                        }
                    }
                }

                if (anyRecorded)
                {
                    lock (ModdedBlueprintToSource)
                    {
                        WriteReportLocked(forceZeroMatchLog: false);
                    }
                }
            }
            catch
            {
                // Ignore if BlueprintManager lists are not yet populated
            }
        }

        private static object FindBlueprintObject(object instance, object[] args, MethodBase method, string blueprintId)
        {
            if (args != null)
            {
                foreach (var arg in args)
                {
                    if (arg == null || arg is string)
                        continue;

                    string candidateName = ExtractNameFromObject(arg);
                    if (!string.IsNullOrWhiteSpace(candidateName) &&
                        string.Equals(candidateName, blueprintId, StringComparison.OrdinalIgnoreCase))
                    {
                        return arg;
                    }
                }
            }

            if (instance != null && method?.DeclaringType?.Name != "BlueprintManager")
            {
                return instance;
            }

            return null;
        }

        private static string ResolveInGameDisplayName(object blueprintEntry, string blueprintId)
        {
            var candidateKeys = new List<string>();

            if (blueprintEntry != null)
            {
                CollectDisplayCandidatesFromObject(blueprintEntry, candidateKeys, depth: 0);
            }

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
                {
                    return localized.Trim();
                }
            }

            for (int i = 0; i < candidateKeys.Count; i++)
            {
                string key = candidateKeys[i];
                if (string.IsNullOrWhiteSpace(key))
                    continue;

                string norm = NormalizeKey(key);
                if (!string.IsNullOrWhiteSpace(norm) && PackResolvedDisplayNames.TryGetValue(norm, out string packName))
                {
                    if (!string.IsNullOrWhiteSpace(packName) && IsResolvedDisplayName(packName, key))
                    {
                        return packName.Trim();
                    }
                }
            }

            return null;
        }

        private static void CollectDisplayCandidatesFromObject(object obj, List<string> candidates, int depth)
        {
            if (obj == null || obj is string || depth > 2)
                return;

            Type type = obj.GetType();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            PropertyInfo[] props;
            try
            {
                props = type.GetProperties(flags);
            }
            catch
            {
                props = Array.Empty<PropertyInfo>();
            }

            for (int i = 0; i < props.Length; i++)
            {
                PropertyInfo prop = props[i];
                if (prop == null || prop.GetIndexParameters().Length > 0)
                    continue;

                if (!IsDisplayOrResultMemberName(prop.Name))
                    continue;

                object val = null;
                try
                {
                    val = prop.GetValue(obj);
                }
                catch
                {
                    continue;
                }

                AddCandidateValue(val, candidates, depth);
            }

            FieldInfo[] fields;
            try
            {
                fields = type.GetFields(flags);
            }
            catch
            {
                fields = Array.Empty<FieldInfo>();
            }

            for (int i = 0; i < fields.Length; i++)
            {
                FieldInfo field = fields[i];
                if (field == null || !IsDisplayOrResultMemberName(field.Name))
                    continue;

                object val = null;
                try
                {
                    val = field.GetValue(obj);
                }
                catch
                {
                    continue;
                }

                AddCandidateValue(val, candidates, depth);
            }
        }

        private static bool IsDisplayOrResultMemberName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return false;

            return name.IndexOf("Crafted", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.IndexOf("Result", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.IndexOf("Display", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.IndexOf("Localized", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.IndexOf("Title", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   string.Equals(name, "name", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "m_Name", StringComparison.OrdinalIgnoreCase);
        }

        private static void AddCandidateValue(object val, List<string> candidates, int depth)
        {
            if (val == null)
                return;

            if (val is string str)
            {
                if (!string.IsNullOrWhiteSpace(str) && !candidates.Contains(str, StringComparer.OrdinalIgnoreCase))
                {
                    candidates.Add(str.Trim());
                }
                return;
            }

            if (depth < 2)
            {
                CollectDisplayCandidatesFromObject(val, candidates, depth + 1);
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
                trimmed.StartsWith("DECO_", StringComparison.OrdinalIgnoreCase))
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

                    MethodInfo getMethod = t.GetMethod(
                        "Get",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                        null,
                        new[] { typeof(string) },
                        null);

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
                if (result is string s && !string.IsNullOrWhiteSpace(s))
                {
                    string resolved = s.Trim();
                    LocalizationResultCache[trimmedKey] = resolved;
                    return resolved;
                }
            }
            catch
            {
                // Ignore invocation errors on early frames
            }

            LocalizationResultCache[trimmedKey] = null;
            return null;
        }

        private static bool LooksLikeVanillaBlueprintId(string identifier)
        {
            if (string.IsNullOrWhiteSpace(identifier))
                return false;

            return identifier.StartsWith("BLUEPRINT_", StringComparison.OrdinalIgnoreCase);
        }

        private static string ResolveNonVanillaSource(
            string stackSourceMod,
            string identifier,
            bool isTrueCopy,
            out string matchedPackFile)
        {
            matchedPackFile = null;
            bool isVanillaId = LooksLikeVanillaBlueprintId(identifier);

            PackEntry packMatch = MatchModComponentPack(identifier);
            if (packMatch != null)
            {
                matchedPackFile = packMatch.FileName;

                string baseLabel = (!string.IsNullOrEmpty(stackSourceMod) &&
                                    stackSourceMod.StartsWith("ModComponent", StringComparison.OrdinalIgnoreCase))
                    ? $"{stackSourceMod} -> {packMatch.PackLabel}"
                    : packMatch.PackLabel;

                if (!isVanillaId)
                    return baseLabel;

                return isTrueCopy
                    ? $"{baseLabel} [Copied Vanilla Blueprint]"
                    : $"{baseLabel} [Modified Vanilla Blueprint]";
            }

            bool hasModCaller = !string.IsNullOrEmpty(stackSourceMod) &&
                                !string.Equals(stackSourceMod, "Unknown / Vanilla or Data-Only", StringComparison.OrdinalIgnoreCase);

            bool isModComponentCaller = hasModCaller &&
                                        stackSourceMod.StartsWith("ModComponent", StringComparison.OrdinalIgnoreCase);

            if (isVanillaId)
            {
                if (isTrueCopy)
                {
                    return hasModCaller
                        ? $"{stackSourceMod} [Copied Vanilla Blueprint]"
                        : "Modded / Runtime Copy of Vanilla Blueprint";
                }

                if (isModComponentCaller)
                {
                    return "ModComponent [Modified Vanilla Blueprint]";
                }

                if (hasModCaller)
                {
                    return $"{stackSourceMod} [Modified Vanilla Blueprint]";
                }

                return null;
            }

            return hasModCaller
                ? stackSourceMod
                : "Custom / Data-Only (Unmatched Pack)";
        }

        private static bool RecordModdedBlueprint(
            string blueprintId,
            string sourceMod,
            string matchedPackFile,
            string displayName,
            string viaDetail,
            bool deferFileWrite)
        {
            if (string.IsNullOrWhiteSpace(blueprintId) || string.IsNullOrWhiteSpace(sourceMod))
                return false;

            lock (ModdedBlueprintToSource)
            {
                if (!string.IsNullOrWhiteSpace(matchedPackFile))
                {
                    MatchedBlueprintPacks.Add(matchedPackFile);
                }

                bool shouldWrite = false;
                if (!ModdedBlueprintToSource.TryGetValue(blueprintId, out string existing))
                {
                    ModdedBlueprintToSource[blueprintId] = sourceMod;
                    shouldWrite = true;
                }
                else if (existing == "Custom / Data-Only (Unmatched Pack)" &&
                         sourceMod != "Custom / Data-Only (Unmatched Pack)")
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

                if (shouldWrite)
                {
                    string nameSuffix = BlueprintDisplayNames.TryGetValue(blueprintId, out string dn) &&
                                        !string.IsNullOrWhiteSpace(dn)
                        ? $" | Name: {dn}"
                        : string.Empty;

                    //Melon<BlueprintInfoMod>.Logger.Msg(
                    //    $"[Blueprint: {blueprintId}]{nameSuffix} -> Source: {sourceMod} (via {viaDetail})");

                    if (!deferFileWrite)
                    {
                        WriteReportLocked(forceZeroMatchLog: false);
                    }
                }

                return shouldWrite;
            }
        }

        private static string ExtractBlueprintIdentifier(object instance, object[] args, MethodBase method)
        {
            if (args != null)
            {
                foreach (var arg in args)
                {
                    if (arg == null)
                        continue;

                    if (arg is string s && !string.IsNullOrWhiteSpace(s))
                        return s;

                    string objName = ExtractNameFromObject(arg);
                    if (!string.IsNullOrWhiteSpace(objName))
                        return objName;
                }
            }

            if (instance != null && method?.DeclaringType?.Name != "BlueprintManager")
            {
                string instName = ExtractNameFromObject(instance);
                if (!string.IsNullOrWhiteSpace(instName))
                    return instName;
            }

            return null;
        }

        private static string ExtractNameFromObject(object obj)
        {
            if (obj == null)
                return null;

            if (obj is string s && !string.IsNullOrWhiteSpace(s))
                return s;

            var type = obj.GetType();
            foreach (string propName in new[]
                     {
                         "name", "Name", "m_Name", "m_BlueprintName", "BlueprintName",
                         "CraftedResult", "m_CraftedResult", "Id", "ID"
                     })
            {
                object val = TryReadMemberValue(type, obj, propName);
                if (val is string strVal && !string.IsNullOrWhiteSpace(strVal))
                    return strVal;

                if (val != null && val is not string)
                {
                    object nestedName = TryReadMemberValue(val.GetType(), val, "name") ??
                                      TryReadMemberValue(val.GetType(), val, "Name");
                    if (nestedName is string nestedStr && !string.IsNullOrWhiteSpace(nestedStr))
                        return nestedStr;
                }
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

                var lines = ModdedBlueprintToSource
                    .OrderBy(kvp => kvp.Key)
                    .Select(kvp =>
                    {
                        if (BlueprintDisplayNames.TryGetValue(kvp.Key, out string displayName) &&
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

        public override void OnDeinitializeMelon()
        {
            lock (ModdedBlueprintToSource)
            {
                WriteReportLocked(forceZeroMatchLog: true);
            }
            LoggerInstance.Msg($"Wrote modded blueprint source map to {OutputFilePath}");
        }
    }
}