using BeatSaberMarkupLanguage;
using BetterSongSearch.Util;
using HMUI;
using SongDetailsCache;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace BetterSongSearch.UI {
    class BSSFlowCoordinator : FlowCoordinator {
        internal static FilterView filterView;
        internal static SongListController songListView;
        internal static DetailController detailView;
        internal static DownloadHistoryView downloadHistoryView;
        internal static SongAssetAsyncLoader assetLoader;
        internal static SongDetails songDetails;
        static BSSFlowCoordinator instance;
        internal static CancellationTokenSource closeCancelSource;
        internal static SongSearchSong[] songsList { get; private set; }
        internal static PlayerDataModel playerDataModel;
        internal static Dictionary<string,Dictionary<string,float>> _songsWithScores;
        internal static bool songsWithScoresShouldProbablyUpdate = true;
        internal static Dictionary<string,Dictionary<string,float>> songsWithScores => _songsWithScores ??= new Dictionary<string,Dictionary<string,float>>(StringComparer.OrdinalIgnoreCase);
        internal static int lastVisibleTableRowIdx { get; private set; }
        int datasetRevision, filterRevision;
        string datasetStatusKey;
        int datasetSongCount = -1;
        Coroutine datasetRoutine, filterRoutine;
        Task activationTask = Task.CompletedTask, cleanupTask = Task.CompletedTask;
        Vector2 leftScreenSize,rightScreenSize,mainScreenSize;
        Vector2 mainScreenPosition;
        bool screensResized;
        public override void DidActivate(bool firstActivation,bool addedToHierarchy,bool screenSystemEnabling) {
            activationTask = ActivateAsync(firstActivation);
        }
        async Task ActivateAsync(bool firstActivation) {
            try {
                await cleanupTask;
                instance = this;
                closeCancelSource?.Dispose(); closeCancelSource = new CancellationTokenSource();
                assetLoader = new SongAssetAsyncLoader();
                if(firstActivation) {
                    SetTitle("Better Song Search"); showBackButton = true;
                    downloadHistoryView = DownloadHistoryView.GetOrCreate();
                    detailView = BeatSaberUI.CreateViewController<DetailController>();
                    filterView = BeatSaberUI.CreateViewController<FilterView>();
                    songListView = BeatSaberUI.CreateViewController<SongListController>();
                    ProvideInitialViewControllers(detailView,filterView,songListView);
                    SongCore.Loader.SongsLoadedEvent += SongcoreSongsLoaded;
                    SongDetailsContainer.dataAvailableOrUpdated += DatasetUpdated;
                    BeatSaverRegionManager.RegionLookup();
                }
                playerDataModel = Resources.FindObjectsOfTypeAll<PlayerDataModel>().FirstOrDefault();
                StartCoroutine(ResizeScreens());
                ReadLocalScores();
                SongcoreSongsLoaded(null,null);
                var init = SongDetails.Init(1);
                var delay = Task.Delay(45000,closeCancelSource.Token);
                if(await Task.WhenAny(init,delay) != init) {
                    closeCancelSource.Token.ThrowIfCancellationRequested();
                    _ = init.ContinueWith(t => Plugin.Log.Warn("Dataset initialization: " + t.Exception),TaskContinuationOptions.OnlyOnFaulted);
                    throw new TimeoutException("Dataset initialization exceeded 45 seconds");
                }
                songDetails = await init;
                if(closeCancelSource.IsCancellationRequested) return;
                DatasetUpdated(); downloadHistoryView.RefreshTable();
            } catch(OperationCanceledException) { }
              catch(Exception ex) { Plugin.Log.Error("Dataset initialization: " + ex); datasetStatusKey = "Dataset unavailable"; if(filterView?.datasetInfoLabel != null) {filterView.datasetInfoLabel.text = BssText.T(datasetStatusKey);filterView.datasetInfoLabel.gameObject.SetActive(true);} }
        }
        void ReadLocalScores() {
            _songsWithScores = new Dictionary<string,Dictionary<string,float>>(StringComparer.OrdinalIgnoreCase);
            if(playerDataModel == null) return;
            foreach(var pair in playerDataModel.playerData.levelsStatsData) {
                var id = pair.Key.levelId;
                if(!pair.Value.validScore || pair.Value.highScore == 0 || id == null || !id.StartsWith(CustomLevelLoader.kCustomLevelPrefixId,StringComparison.Ordinal) || id.Length < CustomLevelLoader.kCustomLevelPrefixId.Length+40) continue;
                string hash = id.Substring(CustomLevelLoader.kCustomLevelPrefixId.Length,40);
                if(!_songsWithScores.TryGetValue(hash,out var values)) _songsWithScores[hash] = values = new Dictionary<string,float>();
                string mode = pair.Key.characteristic.SerializedName().Replace("360Degree","ThreeSixtyDegree").Replace("90Degree","NinetyDegree");
                values[mode + "_" + pair.Key.difficulty] = 0;
            }
            songsWithScoresShouldProbablyUpdate = false;
        }
        IEnumerator ResizeScreens() {
            while(isActivated && (filterView.screen==null || songListView.screen==null)) yield return null;
            if(!isActivated) yield break;
            var left=(RectTransform)filterView.screen.transform; var right=(RectTransform)songListView.screen.transform;var main=(RectTransform)detailView.screen.transform;
            if(!screensResized) {leftScreenSize=left.sizeDelta;rightScreenSize=right.sizeDelta;mainScreenSize=main.sizeDelta;mainScreenPosition=main.anchoredPosition;screensResized=true;}
            left.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical,96); right.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical,96);main.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical,88);
            main.anchoredPosition=mainScreenPosition+new Vector2(0,-4);
        }
        void DatasetUpdated() {
            if(instance != this || songDetails == null || closeCancelSource?.IsCancellationRequested != false) return;
            if(datasetRoutine != null) StopCoroutine(datasetRoutine);
            datasetRoutine = StartCoroutine(BuildDataset(++datasetRevision));
        }
        IEnumerator BuildDataset(int generation) {
            var details = songDetails;
            var difficulties = details.difficulties.ToArray();
            var values = new SongSearchSong[details.songs.Length];
            for(int i=0;i<values.Length;i++) {
                if(generation != datasetRevision || closeCancelSource.IsCancellationRequested) yield break;
                values[i] = new SongSearchSong(in details.songs[i],difficulties);
                if(i % 2048 == 2047) yield return null;
            }
            songsList = values;
            datasetSongCount = values.Length;
            datasetStatusKey = null;
            if(filterView.datasetInfoLabel != null) filterView.datasetInfoLabel.text = values.Length.ToString("N0") + " " + BssText.T("Songs");
            if(filterView.datasetInfoLabel != null) filterView.datasetInfoLabel.gameObject.SetActive(false);
            FilterSongs();
        }
        internal static void FilterSongs() {
            if(instance == null || songsList == null || closeCancelSource.IsCancellationRequested) return;
            filterView.RefreshGroupLabels();
            if(instance.filterRoutine != null) instance.StopCoroutine(instance.filterRoutine);
            instance.filterRoutine = instance.StartCoroutine(instance.FilterDataset(++instance.filterRevision));
        }

        // Called by ModUiChinese after it swaps TranslationEngine.  Refresh only
        // BSS-owned caption producers; song/user data remains in its current
        // fields and is never passed back through the translation engine.
        internal static void OnLocalizationReloaded() {
            var current = instance;
            if(current == null || closeCancelSource?.IsCancellationRequested != false) return;
            try {
                if(filterView?.datasetInfoLabel != null) {
                    if(!String.IsNullOrEmpty(current.datasetStatusKey))
                        filterView.datasetInfoLabel.text = BssText.T(current.datasetStatusKey);
                    else if(current.datasetSongCount >= 0)
                        filterView.datasetInfoLabel.text = current.datasetSongCount.ToString("N0") + " " + BssText.T("Songs");
                }
                filterView?.RefreshGroupLabels();
                detailView?.selectedSongView?.RefreshLocalization();
                songListView?.RefreshLocalization();
                downloadHistoryView?.RefreshTable(false);
                detailView?.Refresh();
                BetterSongSearch.UI.SplitViews.PlaylistCreation.instance.RefreshLocalization();
            } catch(Exception exception) {
                Plugin.Log?.Warn("BetterSongSearch localization refresh failed: " + exception);
            }
        }

        IEnumerator FilterDataset(int generation) {
            var options = FilterView.currentFilter.Clone(); options.CalculateTagBitfields();
            var source = songsList; var matches = new List<SongSearchSong>();
            for(int i=0;i<source.Length;i++) {
                if(generation != filterRevision || closeCancelSource.IsCancellationRequested) yield break;
                var song = source[i];
                if(filterView.SongCheck(in song.detailsSong,options) && filterView.SearchSongCheck(song,options)) {
                    var copy = new SongSearchSong(song);
                    foreach(var diff in copy.diffs) diff._passesFilter = filterView.DifficultyCheck(in diff.detailsDiff,options) && filterView.SearchDifficultyCheck(diff,options);
                    if(copy.diffs.Any(x => x.passesFilter)) matches.Add(copy);
                }
                if(i % 2048 == 2047) yield return null;
            }
            SongListController.filteredSongsList = matches.ToArray();
            songListView.UpdateSearchedSongsList();
        }
        void SongcoreSongsLoaded(object sender,object args) {
            if(instance != this || closeCancelSource?.IsCancellationRequested != false || downloadHistoryView == null) return;
            foreach(var entry in downloadHistoryView.downloadList)
                if(entry.status == DownloadHistoryView.DownloadHistoryEntry.DownloadStatus.Downloaded && SongCore.Collections.songWithHashPresent(entry.hash)) entry.status = DownloadHistoryView.DownloadHistoryEntry.DownloadStatus.Loaded;
            ReadLocalScores(); downloadHistoryView.RefreshTable(false); detailView?.Refresh();
        }
        internal static void ShowDetails() => detailView?.ShowDetails();
        internal static bool IsOpen(CancellationToken token) => instance != null && closeCancelSource != null && !closeCancelSource.IsCancellationRequested && closeCancelSource.Token == token;
        internal static bool IsUiAvailable => instance != null && instance.isActivated && closeCancelSource?.IsCancellationRequested == false && DownloadHistoryView.IsMenu;
        internal static void Close(bool immediately = false,Action afterClosed = null) {
            if(instance == null || !instance.isActivated) return;
            var flow = instance; instance = null;
            if(flow.screensResized) {((RectTransform)filterView.screen.transform).sizeDelta=flow.leftScreenSize;((RectTransform)songListView.screen.transform).sizeDelta=flow.rightScreenSize;var main=(RectTransform)detailView.screen.transform;main.sizeDelta=flow.mainScreenSize;main.anchoredPosition=flow.mainScreenPosition;flow.screensResized=false;}
            lastVisibleTableRowIdx = songListView.songList?.GetVisibleCellsIdRange().Item1 ?? 0;
            closeCancelSource.Cancel();
            downloadHistoryView.SuspendRendering();
            detailView.selectedSongView.ReleaseAssets(); songListView.StopSearch();
            songsList = null; SongListController.filteredSongsList = null; SongListController.searchedSongsList = null;
            detailView.selectedSongView.SetSelectedSong(null); songListView.songList?.ReloadData();
            flow.StopAllCoroutines(); flow.datasetRevision++; flow.filterRevision++;
            foreach(var modal in flow.GetComponentsInChildren<ModalView>(true)) modal.Hide(false);
            foreach(var modal in detailView.GetComponentsInChildren<ModalView>(true)) modal.Hide(false);
            foreach(var modal in songListView.GetComponentsInChildren<ModalView>(true)) modal.Hide(false);
            downloadHistoryView.RequestSongRefresh();
            flow.cleanupTask = flow.CleanupAsync();
            Manager._parentFlow.DismissFlowCoordinator(flow,afterClosed,ViewController.AnimationDirection.Horizontal,immediately);
        }
        async Task CleanupAsync() {
            try { await Task.WhenAll(songListView.DrainAsync(),detailView.selectedSongView.DrainMediaAsync(),detailView.selectedSongView.DrainPlayAsync()); }
            catch(Exception ex) { Plugin.Log.Warn("Flow cleanup: " + ex); }
            assetLoader?.Dispose(); assetLoader = null;
        }
        internal static async Task RefreshSongsAsync(CancellationToken token) {
            await DownloadHistoryView.GetOrCreate().RefreshSongsAsync(token);
        }
        internal static async Task<bool> WaitForStableMenuAsync(CancellationToken token) {
            var deadline = DateTime.UtcNow.AddSeconds(20);
            DateTime? stableSince = null;
            while(DateTime.UtcNow < deadline) {
                token.ThrowIfCancellationRequested();
                if(!DownloadHistoryView.IsMenu) return false;
                FlowCoordinator current = BeatSaberUI.MainFlowCoordinator;
                bool stable = current != null;
                int depth = 0;
                while(current != null && depth++ < 16) {
                    if(current.isInTransition) { stable = false; break; }
                    current = current.childFlowCoordinator;
                }
                if(current != null) stable = false;
                if(stable) {
                    stableSince ??= DateTime.UtcNow;
                    if((DateTime.UtcNow - stableSince.Value).TotalSeconds >= 1) return true;
                } else stableSince = null;
                await Task.Delay(100,token);
            }
            Plugin.Log?.Warn("Downloaded maps remain pending: menu did not remain stable for one second within 20 seconds");
            return false;
        }
        internal static async Task<bool> RefreshSongCoreAsync(CancellationToken token) {
            if(!await WaitForStableMenuAsync(token)) return false;
            var done = new TaskCompletionSource<bool>();
            void Completed(object sender,object args) => done.TrySetResult(true);
            SongCore.Loader.SongsLoadedEvent += Completed;
            try {
                // RefreshSongs ignores requests while another mod is scanning.
                // Wait for that scan, then issue ours so packages published after
                // its directory snapshot are included as well.
                for(int scans=0;SongCore.Loader.AreSongsLoading;scans++) {
                    if(scans >= 3) throw new TimeoutException("SongCore remained busy after three completed scans");
                    await WaitForSongCoreAsync(done.Task,token);
                    done = new TaskCompletionSource<bool>();
                    if(!await WaitForStableMenuAsync(token)) return false;
                }
                token.ThrowIfCancellationRequested();
                if(!DownloadHistoryView.IsMenu) return false;
                if(SongCore.Loader.Instance == null) throw new InvalidOperationException("SongCore menu loader is not ready");
                done = new TaskCompletionSource<bool>();
                SongCore.Loader.Instance.RefreshSongs(false);
                await WaitForSongCoreAsync(done.Task,token);
                return true;
            } finally { SongCore.Loader.SongsLoadedEvent -= Completed; }
        }
        static async Task WaitForSongCoreAsync(Task done,CancellationToken token) {
            using(var timeout = CancellationTokenSource.CreateLinkedTokenSource(token)) {
                var wait = Task.Delay(20000,timeout.Token);
                if(await Task.WhenAny(done,wait) != done) { token.ThrowIfCancellationRequested(); throw new TimeoutException("SongCore refresh exceeded 20 seconds"); }
                timeout.Cancel();
                await done;
            }
        }
        public override void BackButtonWasPressed(ViewController topViewController) => Close();
        void OnDestroy() {
            SongCore.Loader.SongsLoadedEvent -= SongcoreSongsLoaded;
            SongDetailsContainer.dataAvailableOrUpdated -= DatasetUpdated;
            if(instance == this) { instance = null; closeCancelSource?.Cancel(); assetLoader?.Dispose(); }
        }
    }
}
