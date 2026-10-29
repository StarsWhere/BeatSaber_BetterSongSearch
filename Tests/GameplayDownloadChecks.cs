using BetterSongSearch.UI;
using BetterSongSearch.Util;
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace BetterSongSearchSmoke {
    internal sealed class GameplayDownloadChecks {
        readonly DownloadHistoryView owner;
        readonly Func<DownloadHistoryView.DownloadHistoryEntry, CancellationToken, Action<float>, Task> previous;
        readonly TaskCompletionSource<bool> gameplay = new TaskCompletionSource<bool>();
        readonly string suffix = "BSSGameplay" + Guid.NewGuid().ToString("N");
        readonly string root = Path.GetFullPath(CustomLevelPathHelper.customLevelsDirectoryPath);
        internal DownloadHistoryView.DownloadHistoryEntry Entry { get; private set; }
        internal bool RemoteRequestInGameplay { get; private set; }
        internal GameplayDownloadChecks() {
            owner = BSSFlowCoordinator.downloadHistoryView;
            previous = SongDownloader.DownloadOverride;
        }
        internal void Start() {
            var song = BSSFlowCoordinator.songsList.Where(x => x.CheckIsDownloadable() && x.detailsSong.upvotes > 100 && x.detailsSong.songDurationSeconds >= 30 && x.detailsSong.songDurationSeconds < 150)
                .OrderByDescending(x => x.detailsSong.rating).First();
            SongDownloader.DownloadOverride = async (entry, token, progress) => {
                using(token.Register(() => gameplay.TrySetCanceled())) await gameplay.Task;
                token.ThrowIfCancellationRequested();
                RemoteRequestInGameplay = UnityEngine.SceneManagement.SceneManager.GetSceneByName("GameCore").isLoaded;
                SongDownloader.DownloadOverride = null;
                await SongDownloader.BeatmapDownload(entry, token, progress);
            };
            if(!owner.TryAddDownload(song)) throw new InvalidOperationException("Cannot enqueue gameplay fixture");
            Entry = owner.downloadList.Single(x => x.hash.Equals(song.hash, StringComparison.OrdinalIgnoreCase));
            typeof(DownloadHistoryView.DownloadHistoryEntry).GetField("songName", BindingFlags.Instance | BindingFlags.Public).SetValue(Entry, Entry.songName + " " + suffix);
        }
        internal void ContinueInGameplay() => gameplay.TrySetResult(true);
        internal async Task VerifyAsync(Action<string, bool, string> check) {
            check("native play retains download owner", ReferenceEquals(owner, DownloadHistoryView.Instance) && Entry.isDownloading, "application queue survived native Play and GameCore scene transition");
            ContinueInGameplay();
            await owner.DrainAsync();
            var folders = Directory.GetDirectories(root).Where(path => Path.GetFileName(path).Contains(suffix)).ToArray();
            check("real remote download during gameplay", RemoteRequestInGameplay && UnityEngine.SceneManagement.SceneManager.GetSceneByName("GameCore").isLoaded && Entry.status == DownloadHistoryView.DownloadHistoryEntry.DownloadStatus.Downloaded && folders.Length == 1 && File.Exists(Path.Combine(folders[0], "Info.dat")), Entry.hash + "; status=" + Entry.status + "; files=" + (folders.Length == 1 ? Directory.GetFiles(folders[0], "*", SearchOption.AllDirectories).Length : 0));
            check("SongCore refresh deferred during gameplay", !SongCore.Collections.songWithHashPresent(Entry.hash), "finished package waits for the menu before registration");
        }
        internal async Task CleanupAsync() {
            gameplay.TrySetCanceled();
            if(Entry != null && (Entry.isDownloading || Entry.isQueued)) owner.CancelPending();
            await owner.DrainAsync();
            SongDownloader.DownloadOverride = previous;
            if(Entry != null) owner.downloadList.Remove(Entry);
            if(Directory.Exists(root)) foreach(var path in Directory.GetDirectories(root).Where(path => Path.GetFileName(path).Contains(suffix))) {
                if(!String.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), root, StringComparison.OrdinalIgnoreCase)) throw new IOException("Unexpected fixture path: " + path);
                Directory.Delete(path, true);
            }
            if(DownloadHistoryView.IsMenu) await owner.RefreshSongsAsync(CancellationToken.None);
        }
    }
}
