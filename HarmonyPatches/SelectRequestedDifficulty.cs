using System;
using System.Collections.Generic;
using HarmonyLib;

namespace BetterSongSearch.HarmonyPatches {
    [HarmonyPatch(typeof(StandardLevelDetailView),nameof(StandardLevelDetailView.SetContent),new[] {typeof(BeatmapLevel),typeof(BeatmapDifficultyMask),typeof(HashSet<BeatmapCharacteristic>),typeof(BeatmapDifficulty),typeof(BeatmapCharacteristic?),typeof(PlayerData)})]
    static class SelectRequestedDifficulty {
        static BeatmapKey? pending;
        static DateTime expires;
        internal static void Prepare(BeatmapKey key) { pending=key; expires=DateTime.UtcNow.AddSeconds(15); }
        internal static void Clear() => pending=null;
        static void Prefix(BeatmapLevel __0,ref BeatmapDifficulty __3,ref BeatmapCharacteristic? __4) {
            if(!pending.HasValue) return;
            if(DateTime.UtcNow>expires) {Clear();return;}
            var key=pending.Value;
            if(__0==null || !__0.levelID.Equals(key.levelId,StringComparison.OrdinalIgnoreCase)) return;
            __3=key.difficulty; __4=key.characteristic; Clear();
        }
    }
}
