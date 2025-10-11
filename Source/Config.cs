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

        // ---- New (数字・記号だらけの行は既定で除外) ----
        public int MinLength = 2;
        // 空白/URL/数字のみ/記号のみ/GUID を既定で弾く
        public string ExcludePatterns =
            @"^\s*$|^https?://|^[0-9]+$|^[-–—…\.\(\)\[\]\{\}/:+*,%<>→=~\s]+$|^[A-F0-9]{8}(-[A-F0-9]{4}){3}-[A-F0-9]{12}$";

        // 画面名除外/許可（Type名カンマ区切り）
        public string IncludedWindows = "";
        // ★ 追加: 設定系ダイアログも既定で除外（自己参照ループ防止）
        public string ExcludedWindows = "EditWindow_Log,Page_ModsConfig,Dialog_DebugTables,Dialog_ModSettings,Dialog_Options";

        // 追記デバウンス
        public bool PauseAggregate = false;
        public int AggregateDebounceMs = 250;

        // 辞書監視とUI
        public bool WatchDict = true;
        public bool ShowDebugHUD = false;
        public bool LogExcludedScreens = true;

        public static Config Load(string iniPath, string defaultDict, string defaultLog, string defaultExportRoot)
        {
            var c = new Config();
            c._iniPath = iniPath;
            c.DictTsv = defaultDict;
            c.LogPath = defaultLog;
            c.ExportRoot = defaultExportRoot;

            try
            {
                if (File.Exists(iniPath))
                {
                    foreach (var raw in File.ReadAllLines(iniPath, new UTF8Encoding(false)))
                    {
                        var line = raw.Trim();
                        if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
                        int eq = line.IndexOf('=');
                        if (eq <= 0) continue;

                        string k = line.Substring(0, eq).Trim();
                        string v = line.Substring(eq + 1).Trim();

                        switch (k)
                        {
                            case "ApplyDictAtRuntime": c.ApplyDictAtRuntime = ParseBool(v, c.ApplyDictAtRuntime); break;
                            case "ExportPerMod": c.ExportPerMod = ParseBool(v, c.ExportPerMod); break;
                            case "EmitAggregate": c.EmitAggregate = ParseBool(v, c.EmitAggregate); break;
                            case "ExportMode": c.ExportMode = string.IsNullOrWhiteSpace(v) ? c.ExportMode : v; break;
                            case "PerModSubdir": c.PerModSubdir = string.IsNullOrWhiteSpace(v) ? c.PerModSubdir : v; break;

                            case "DictPath": c.DictTsv = Resolve(v, c.DictTsv); break;
                            case "LogPath": c.LogPath = Resolve(v, c.LogPath); break;
                            case "ExportRoot": c.ExportRoot = Resolve(v, c.ExportRoot); break;

                            // New
                            case "MinLength": if (int.TryParse(v, out var ml)) c.MinLength = Math.Max(0, ml); break;
                            case "ExcludePatterns": c.ExcludePatterns = v ?? c.ExcludePatterns; break;
                            case "IncludedWindows": c.IncludedWindows = v ?? ""; break;
                            case "ExcludedWindows": c.ExcludedWindows = v ?? ""; break;
                            case "PauseAggregate": c.PauseAggregate = ParseBool(v, c.PauseAggregate); break;
                            case "AggregateDebounceMs": if (int.TryParse(v, out var ms)) c.AggregateDebounceMs = Math.Max(0, ms); break;
                            case "WatchDict": c.WatchDict = ParseBool(v, c.WatchDict); break;
                            case "ShowDebugHUD": c.ShowDebugHUD = ParseBool(v, c.ShowDebugHUD); break;
                            case "LogExcludedScreens": c.LogExcludedScreens = ParseBool(v, c.LogExcludedScreens); break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Verse.Log.Warning("[RimLex] ini load failed: " + ex.Message);
            }

            return c;
        }

        public void Save()
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("# RimLex settings");
                sb.AppendLine("ApplyDictAtRuntime=" + (ApplyDictAtRuntime ? "true" : "false"));
                sb.AppendLine("ExportPerMod=" + (ExportPerMod ? "true" : "false"));
                sb.AppendLine("EmitAggregate=" + (EmitAggregate ? "true" : "false"));
                sb.AppendLine("ExportMode=" + ExportMode);
                sb.AppendLine("DictPath=" + ToIni(DictTsv));
                sb.AppendLine("LogPath=" + ToIni(LogPath));
                sb.AppendLine("ExportRoot=" + ToIni(ExportRoot));
                sb.AppendLine("PerModSubdir=" + (string.IsNullOrWhiteSpace(PerModSubdir) ? "PerMod" : PerModSubdir));

                // New
                sb.AppendLine("MinLength=" + MinLength);
                sb.AppendLine("ExcludePatterns=" + (ExcludePatterns ?? ""));
                sb.AppendLine("IncludedWindows=" + (IncludedWindows ?? ""));
                sb.AppendLine("ExcludedWindows=" + (ExcludedWindows ?? ""));
                sb.AppendLine("PauseAggregate=" + (PauseAggregate ? "true" : "false"));
                sb.AppendLine("AggregateDebounceMs=" + AggregateDebounceMs);
                sb.AppendLine("WatchDict=" + (WatchDict ? "true" : "false"));
                sb.AppendLine("ShowDebugHUD=" + (ShowDebugHUD ? "true" : "false"));
                sb.AppendLine("LogExcludedScreens=" + (LogExcludedScreens ? "true" : "false"));

                File.WriteAllText(_iniPath, sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                Verse.Log.Warning("[RimLex] ini save failed: " + ex.Message);
            }
        }

        // ---- helpers ----
        private static bool ParseBool(string v, bool def)
        {
            if (string.IsNullOrWhiteSpace(v)) return def;

            if (v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1") return true;
            if (v.Equals("false", StringComparison.OrdinalIgnoreCase) || v == "0") return false;

            return def;
        }

        private static string Resolve(string v, string def)
        {
            if (string.IsNullOrWhiteSpace(v)) return def;
            return v.Replace("%ModDir%", ModInitializer.ModDir ?? "").Replace('\\', '/');
        }

        private static string ToIni(string abs)
        {
            try
            {
                var md = (ModInitializer.ModDir ?? "").Replace('\\', '/');
                var a = (abs ?? "").Replace('\\', '/');
                if (!string.IsNullOrEmpty(md) && a.StartsWith(md, StringComparison.OrdinalIgnoreCase))
                    return "%ModDir%" + a.Substring(md.Length);
            }
            catch { }
            return abs ?? "";
        }
    }
}
