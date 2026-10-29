using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using SongDetailsCache.Structs;
using BetterSongSearch.UI;

namespace BetterSongSearch.Util {
    static class BssText {
        static readonly Dictionary<string,string> cache = new Dictionary<string,string>();
        static FieldInfo engineField;
        static MethodInfo translate;
        static object cachedEngine;

        static object Engine {
            get {
                if(engineField == null) {
                    var type = AccessTools.TypeByName("ModUiChinese.UiTranslator");
                    engineField = type?.GetField("Engine");
                }

                object current = engineField?.GetValue(null);
                if(!ReferenceEquals(current,cachedEngine)) {
                    cache.Clear();
                    translate = null;
                    cachedEngine = current;
                }
                return current;
            }
        }

        public static string T(string value) {
            if(value == null) return null;
            var engine = Engine;
            if(engine == null) return value;
            if(cache.TryGetValue(value, out var result)) return result;
            translate ??= engine.GetType().GetMethod("Translate", new[] {typeof(string),typeof(string)});
            if(translate == null) return value;
            try {
                result = (string)translate.Invoke(engine, new object[] {value,"BetterSongSearch"});
            } catch(Exception error) {
                BetterSongSearch.Plugin.Log?.Warn("BetterSongSearch translation failed for caption [" + value + "]: " + error);
                return value;
            }
            return cache[value] = result;
        }

        public static void OnLocalizationReloaded() {
            cache.Clear();
            translate = null;
            cachedEngine = null;
            try { BSSFlowCoordinator.OnLocalizationReloaded(); }
            catch(Exception exception) { BetterSongSearch.Plugin.Log?.Warn("BetterSongSearch localization refresh failed: " + exception); }
        }
    }
    static class SongPresentation {
        public static string Name(string value) => String.IsNullOrWhiteSpace(value) ? "—" : value.Replace('\n',' ').Replace('\r',' ');
        public static string Duration(uint seconds) => seconds == 0 ? "—" : TimeSpan.FromSeconds(seconds).ToString(seconds >= 3600 ? "h\\:mm\\:ss" : "m\\:ss", CultureInfo.InvariantCulture);
        public static string Rating(in Song song) => (ulong)song.upvotes + song.downvotes == 0 ? "—" : song.rating.ToString("0%", CultureInfo.InvariantCulture);
        public static string Nps(uint notes, uint seconds, bool withUnit = false) => seconds == 0 ? "—" : ((double)notes / seconds).ToString("0.0", CultureInfo.InvariantCulture) + (withUnit ? BssText.T("/s") : "");
        public static string Mode(MapCharacteristic mode) => BssText.T(mode.ToString());
        public static string Difficulty(MapDifficulty difficulty) => BssText.T(difficulty.ToString());
        static string DifficultyColor(MapDifficulty difficulty) => difficulty switch {
            MapDifficulty.Easy => "#7DC88E", MapDifficulty.Normal => "#70BEEF", MapDifficulty.Hard => "#F3B562", MapDifficulty.Expert => "#DF8EC8", _ => "#B995EF"
        };
        public static string Stars(SongSearchSong.SongSearchDiff diff) {
            if(diff == null) return "—";
            float value = diff.GetStars();
            if(value <= 0 || Single.IsNaN(value) || Single.IsInfinity(value)) return "—";
            string service = diff.GetTargetedRankLeaderboardService() == RankedStates.ScoresaberRanked ? "SS" : "BL";
            return service + " " + value.ToString("0.0", CultureInfo.InvariantCulture) + "★";
        }
        public static string DiffSummary(SongSearchSong song) {
            var diff = song.GetFirstPassingDifficulty();
            if(diff == null) return BssText.T("No matching difficulty");
            var d = diff.detailsDiff;
            var parts = new List<string> {Mode(d.characteristic) + " · <color="+DifficultyColor(d.difficulty)+">" + Difficulty(d.difficulty)+"</color>"};
            var stars = Stars(diff);
            if(stars != "—") parts.Add(stars);
            parts.Add(BssText.T("Density") + " " + Nps(d.notes,song.detailsSong.songDurationSeconds,true));
            int additional = song.diffs.Count(x => x.passesFilter) - 1;
            if(additional > 0) parts.Add("+" + additional);
            return String.Join(" · ", parts);
        }
        public static string State(SongSearchSong song) {
            var entry = BSSFlowCoordinator.downloadHistoryView?.downloadList.FirstOrDefault(x => x.hash.Equals(song.hash,StringComparison.OrdinalIgnoreCase));
            if(entry != null && entry.status != DownloadHistoryView.DownloadHistoryEntry.DownloadStatus.Loaded) {
                if(entry.status == DownloadHistoryView.DownloadHistoryEntry.DownloadStatus.Downloaded) return BssText.T("Waiting for song load");
                if(entry.status == DownloadHistoryView.DownloadHistoryEntry.DownloadStatus.Downloading) return (entry.downloadProgress*100).ToString("0") + "%";
                return BssText.T(entry.status.ToString());
            }
            return song.CheckIsDownloadedAndLoaded() ? BssText.T("Downloaded") : "";
        }
    }
}
