using System;
using System.Threading.Tasks;
using BetterSongSearch.UI;

namespace BetterSongSearch.Util {
    static class BeatSaverRegionManager {
        public const string mapDownloadUrlFallback = "https://cdn.beatsaver.com";
        public const string detailsDownloadUrl = "https://api.beatsaver.com/maps/id";
        public static string mapDownloadUrl { get; private set; } = "https://r2cdn.beatsaver.com";
        public static string coverDownloadUrl { get; private set; } = "https://cdn.beatsaver.com";
        public static string previewDownloadUrl { get; private set; } = "https://cdn.beatsaver.com";
        static Task lookupTask;
        static bool resolved;
        public static void RegionLookup(bool force = false) {
            if(!force && (resolved || lookupTask != null && !lookupTask.IsCompleted)) return;
            lookupTask = LookupAsync(BSSFlowCoordinator.assetLoader);
        }
        static async Task LookupAsync(SongAssetAsyncLoader loader) {
            try {
                string address = await loader.GetPreviewURL("225eb",BSSFlowCoordinator.closeCancelSource.Token);
                if(!Uri.TryCreate(address,UriKind.Absolute,out var uri) || uri.Scheme != "https") throw new InvalidOperationException("Invalid BeatSaver region URL");
                coverDownloadUrl = previewDownloadUrl = uri.GetLeftPart(UriPartial.Authority); resolved = true;
            } catch(OperationCanceledException) { }
              catch(Exception ex) { Plugin.Log.Warn("BeatSaver region lookup; using configured CDN: " + ex); }
        }
    }
}
