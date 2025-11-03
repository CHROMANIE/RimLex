using System;
using System.IO;
using System.Text;

namespace RimLex
{
    public class Config
    {
        public bool ApplyDictAtRuntime = true;
        public string DictTsv;
        public string ExportRoot;
        public bool ExportPerMod = true;
        public bool EmitAggregate = true;
        public string ExportMode = "Both";
        public string PerModSubdir = "PerMod";

        public string LogPath;
        private string _iniPath;

        // ---- Noise / screen filters ----
        public int MinLength = 2;
        public string ExcludePatterns =
            @"^\s*$|^https?://|^[0-9]+$|^[-–—…\.\(\)\[\]\{\}/:+*,%<>→=~\s]+$|^[A-F0-9]{8}(-[A-F0-9]{4}){3}-[A-F0-9]{12}$";

        public string IncludedWindows = string.Empty;
        public string ExcludedWindows = "EditWindow_Log,Page_ModsConfig,Dialog_DebugTables,Dialog_ModSettings,Dialog_Options";

        // ---- Aggregate control ----
        public bool PauseAggregate = false;
        public int AggregateDebounceMs = 250;

        // ---- Misc runtime flags ----
        public bool WatchDict = true;
        public bool ShowDebugHUD = false;
        public bool LogExcludedScreens = true;

        public static Config Load(string iniPath, string defaultDict, string defaultLog, string defaultExportRoot)
        {
            var config = new Config
            {
                _iniPath = iniPath,
                DictTsv = defaultDict,
                LogPath = defaultLog,
                ExportRoot = defaultExportRoot
            };

            try
            {
                if (!File.Exists(iniPath)) return config;

                foreach (var raw in File.ReadAllLines(iniPath, new UTF8Encoding(false)))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;

                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;

                    string key = line.Substring(0, eq).Trim();
                    string value = line.Substring(eq + 1).Trim();

                    switch (key)
                    {
                        case "ApplyDictAtRuntime":
                            config.ApplyDictAtRuntime = ParseBool(value, config.ApplyDictAtRuntime);
                            break;
                        case "ExportPerMod":
                            config.ExportPerMod = ParseBool(value, config.ExportPerMod);
                            break;
                        case "EmitAggregate":
                            config.EmitAggregate = ParseBool(value, config.EmitAggregate);
                            break;
                        case "ExportMode":
                            if (!string.IsNullOrWhiteSpace(value)) config.ExportMode = value;
                            break;
                        case "PerModSubdir":
                            if (!string.IsNullOrWhiteSpace(value)) config.PerModSubdir = value;
                            break;

                        case "DictPath":
                            config.DictTsv = Resolve(value, config.DictTsv);
                            break;
                        case "LogPath":
                            config.LogPath = Resolve(value, config.LogPath);
                            break;
                        case "ExportRoot":
                            config.ExportRoot = Resolve(value, config.ExportRoot);
                            break;

                        case "MinLength":
                            if (int.TryParse(value, out var minLen)) config.MinLength = Math.Max(0, minLen);
                            break;
                        case "ExcludePatterns":
                            config.ExcludePatterns = value ?? config.ExcludePatterns;
                            break;
                        case "IncludedWindows":
                            config.IncludedWindows = value ?? string.Empty;
                            break;
                        case "ExcludedWindows":
                            config.ExcludedWindows = value ?? string.Empty;
                            break;
                        case "PauseAggregate":
                            config.PauseAggregate = ParseBool(value, config.PauseAggregate);
                            break;
                        case "AggregateDebounceMs":
                            if (int.TryParse(value, out var debounce)) config.AggregateDebounceMs = Math.Max(0, debounce);
                            break;
                        case "WatchDict":
                            config.WatchDict = ParseBool(value, config.WatchDict);
                            break;
                        case "ShowDebugHUD":
                            config.ShowDebugHUD = ParseBool(value, config.ShowDebugHUD);
                            break;
                        case "LogExcludedScreens":
                            config.LogExcludedScreens = ParseBool(value, config.LogExcludedScreens);
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                Verse.Log.Warning("[RimLex] ini load failed: " + ex.Message);
            }

            return config;
        }

        public void Save()
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("# RimLex settings");
                AppendKeyValue(sb, "ApplyDictAtRuntime", BoolToIni(ApplyDictAtRuntime));
                AppendKeyValue(sb, "ExportPerMod", BoolToIni(ExportPerMod));
                AppendKeyValue(sb, "EmitAggregate", BoolToIni(EmitAggregate));
                AppendKeyValue(sb, "ExportMode", ExportMode ?? "Both");
                AppendKeyValue(sb, "DictPath", ToIni(DictTsv));
                AppendKeyValue(sb, "LogPath", ToIni(LogPath));
                AppendKeyValue(sb, "ExportRoot", ToIni(ExportRoot));
                AppendKeyValue(sb, "PerModSubdir", string.IsNullOrWhiteSpace(PerModSubdir) ? "PerMod" : PerModSubdir);

                AppendKeyValue(sb, "MinLength", MinLength.ToString());
                AppendKeyValue(sb, "ExcludePatterns", ExcludePatterns ?? string.Empty);
                AppendKeyValue(sb, "IncludedWindows", IncludedWindows ?? string.Empty);
                AppendKeyValue(sb, "ExcludedWindows", ExcludedWindows ?? string.Empty);
                AppendKeyValue(sb, "PauseAggregate", BoolToIni(PauseAggregate));
                AppendKeyValue(sb, "AggregateDebounceMs", AggregateDebounceMs.ToString());
                AppendKeyValue(sb, "WatchDict", BoolToIni(WatchDict));
                AppendKeyValue(sb, "ShowDebugHUD", BoolToIni(ShowDebugHUD));
                AppendKeyValue(sb, "LogExcludedScreens", BoolToIni(LogExcludedScreens));

                File.WriteAllText(_iniPath, sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                Verse.Log.Warning("[RimLex] ini save failed: " + ex.Message);
            }
        }

        private static bool ParseBool(string value, bool fallback)
        {
            if (string.IsNullOrWhiteSpace(value)) return fallback;
            if (value.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
            if (value.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
            if (value == "1") return true;
            if (value == "0") return false;
            return fallback;
        }

        private static string Resolve(string value, string fallback)
        {
            if (string.IsNullOrWhiteSpace(value)) return fallback;
            return value.Replace("%ModDir%", ModInitializer.ModDir ?? string.Empty).Replace('\\', '/');
        }

        private static string ToIni(string absolute)
        {
            try
            {
                var modDir = (ModInitializer.ModDir ?? string.Empty).Replace('\\', '/');
                var normalized = (absolute ?? string.Empty).Replace('\\', '/');
                if (!string.IsNullOrEmpty(modDir) && normalized.StartsWith(modDir, StringComparison.OrdinalIgnoreCase))
                    return "%ModDir%" + normalized.Substring(modDir.Length);
            }
            catch
            {
            }
            return absolute ?? string.Empty;
        }

        private static void AppendKeyValue(StringBuilder sb, string key, string value)
            => sb.AppendLine(key + "=" + (value ?? string.Empty));

        private static string BoolToIni(bool value) => value ? "true" : "false";
    }
}
