using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BeatSaberMarkupLanguage.Attributes;
using BeatSaberMarkupLanguage.Parser;
using BeatSaberMarkupLanguage.Components.Settings;
using BetterSongSearch.HarmonyPatches;
using BetterSongSearch.Util;
using HMUI;
using SongDetailsCache.Structs;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using static SelectLevelCategoryViewController;

namespace BetterSongSearch.UI {
    class SelectedSongView : MonoBehaviour, INotifyPropertyChanged {
        internal SongSearchSong selectedSong;
        internal SongSearchSong.SongSearchDiff selectedDiff;
        [UIComponent("coverImage")] internal Image coverImage;
        [UIComponent("selectedSongAuthor")] TMP_Text selectedSongAuthor;
        [UIComponent("selectedSongName")] TMP_Text selectedSongName;
        [UIComponent("selectedMapperName")] TMP_Text selectedMapperName;
        [UIComponent("songDuration")] TMP_Text songDuration;
        [UIComponent("songBpm")] TMP_Text songBpm;
        [UIComponent("songRating")] TMP_Text songRating;
        [UIComponent("songVotes")] TMP_Text songVotes;
        [UIComponent("songDiffMetrics")] TMP_Text songDiffMetrics;
        [UIComponent("songDiffMotion")] TMP_Text songDiffMotion;
        [UIComponent("noteCount")] TMP_Text noteCount;
        [UIComponent("obstacleCount")] TMP_Text obstacleCount;
        [UIComponent("bombCount")] TMP_Text bombCount;
        [UIComponent("starValue")] TMP_Text starValue;
        string mapInfo, uploadInfo;
        [UIComponent("songActionStatus")] TMP_Text songActionStatus;
        [UIComponent("downloadButton")] NoTransitionsButton downloadButton;
        [UIComponent("playButton")] NoTransitionsButton playButton;
        [UIComponent("modeDropdown")] DropDownListSetting modeDropdown;
        [UIComponent("difficultyDropdown")] DropDownListSetting difficultyDropdown;
        internal static SongPreviewPlayer songPreviewPlayer { get; private set; }
        internal static CancellationTokenSource songAssetLoadCanceller { get; private set; }
        SongAssetAsyncLoader.CoverLease coverLease;
        Task mediaTask = Task.CompletedTask, playTask = Task.CompletedTask;
        int revision;
        bool busy;
        bool updatingDifficultyControls;
        string actionError;
        string mode, difficulty;
        [UIValue("modeChoices")] public List<object> modeChoices { get; private set; } = new List<object>();
        [UIValue("difficultyChoices")] public List<object> difficultyChoices { get; private set; } = new List<object>();
        [UIValue("selectedMode")] public string selectedMode { get => mode; set => mode = value; }
        [UIValue("selectedDifficulty")] public string selectedDifficulty { get => difficulty; set => difficulty = value; }
        public event PropertyChangedEventHandler PropertyChanged;
        void Notify(string name) => PropertyChanged?.Invoke(this,new PropertyChangedEventArgs(name));
        [UIAction("#post-parse")] internal void Parsed() {
            BssUi.Setting(modeDropdown,70,14,3,2.8f); BssUi.Setting(difficultyDropdown,70,14,3,2.8f);
            BssUi.Text(selectedSongName,116,13); BssUi.Text(selectedSongAuthor,116,4.5f); BssUi.Text(selectedMapperName,116,4.5f);
            foreach(var value in new[]{songDuration,songBpm,songRating,songVotes}) BssUi.Text(value,25.5f,4.8f);
            foreach(var value in new[]{songDiffMetrics,songDiffMotion,noteCount,obstacleCount,bombCount,starValue}) BssUi.Text(value,22.5f,5.5f);
            BssUi.Text(songActionStatus,0,6);
            BssTypography.Apply(selectedSongName,true);
        }
        internal void SetSelectedSong(SongSearchSong song,bool selectInTableIfPossible = false) {
            bool changed = !String.Equals(selectedSong?.hash,song?.hash,StringComparison.OrdinalIgnoreCase);
            actionError = null;
            string oldDiff = selectedDiff?.serializedDiff;
            selectedSong = song;
            selectedDiff = song == null ? null : !changed ? song.diffs.FirstOrDefault(x => x.serializedDiff == oldDiff && x.passesFilter) ?? song.GetFirstPassingDifficulty() : song.GetFirstPassingDifficulty();
            if(selectedDiff == null && song != null) selectedDiff = song.sortedDiffs.FirstOrDefault();
            if(song == null) { ReleaseAssets(); BSSFlowCoordinator.detailView?.Refresh(); return; }
            BSSFlowCoordinator.ShowDetails();
            modeChoices = song.diffs.Select(x => x.detailsDiff.characteristic).Distinct().Select(x => (object)SongPresentation.Mode(x)).ToList();
            selectedSongName.text = SongPresentation.Name(song.detailsSong.songName);
            BssTypography.Apply(selectedSongName,true);
            selectedSongAuthor.text = SongPresentation.Name(song.detailsSong.songAuthorName);
            selectedMapperName.text = BssText.T("Mapper") + " " + SongPresentation.Name(song.detailsSong.levelAuthorName);
            songDuration.text=SongPresentation.Duration(song.detailsSong.songDurationSeconds);
            songBpm.text=song.detailsSong.bpm > 0 ? song.detailsSong.bpm.ToString("0.#") : "—";
            songRating.text=SongPresentation.Rating(in song.detailsSong);
            songVotes.text=((ulong)song.detailsSong.upvotes + song.detailsSong.downvotes).ToString("N0");
            uploadInfo = BuildUploadInfo();
            Notify("modeChoices"); RefreshDifficulty();
            if(selectInTableIfPossible) BSSFlowCoordinator.songListView?.SelectByHash(song.hash,true);
            if(changed || coverLease == null) {
                ReleaseAssets();
                songAssetLoadCanceller = CancellationTokenSource.CreateLinkedTokenSource(BSSFlowCoordinator.closeCancelSource.Token);
                coverImage.sprite = SongCore.Loader.defaultCoverImage;
                mediaTask = LoadMediaAsync(BSSFlowCoordinator.assetLoader,song,++revision,songAssetLoadCanceller.Token);
            }
            BSSFlowCoordinator.detailView?.Refresh();
        }

        string BuildUploadInfo() {
            return selectedSong.detailsSong.uploadTime.ToString("yyyy-MM-dd") + " · " + selectedSong.detailsSong.key +
                (selectedSong.detailsSong.uploadFlags.HasFlag(UploadFlags.Curated) ? " · " + BssText.T("Curated") : "") +
                (selectedSong.detailsSong.uploadFlags.HasFlag(UploadFlags.VerifiedUploader) ? " · " + BssText.T("Verified Mapper") : "");
        }

        internal void RefreshLocalization() {
            if(selectedSong == null || selectedDiff == null) return;
            bool wasUpdating = updatingDifficultyControls;
            updatingDifficultyControls = true;
            try {
                modeChoices = selectedSong.diffs.Select(x => x.detailsDiff.characteristic)
                    .Distinct().Select(x => (object)SongPresentation.Mode(x)).ToList();
                selectedMapperName.text = BssText.T("Mapper") + " " + SongPresentation.Name(selectedSong.detailsSong.levelAuthorName);
                uploadInfo = BuildUploadInfo();
                Notify("modeChoices");
                RefreshDifficulty();
                BetterSongSearch.UI.SplitViews.UploadDetails.instance.RefreshLocalization(uploadInfo, mapInfo);
            } finally { updatingDifficultyControls = wasUpdating; }
        }

        [UIAction("ModeChanged")] void ModeChanged(object value) {
            if(selectedSong == null || updatingDifficultyControls) return;
            var candidates = selectedSong.sortedDiffs.Where(x => SongPresentation.Mode(x.detailsDiff.characteristic) == value?.ToString()).ToArray();
            if(candidates.Length == 0) { RefreshDifficulty(); return; }
            actionError=null;
            selectedDiff = candidates.FirstOrDefault(x => x.detailsDiff.difficulty == selectedDiff?.detailsDiff.difficulty) ?? candidates.FirstOrDefault(x => x.passesFilter) ?? candidates[0];
            RefreshDifficulty();
        }
        [UIAction("DifficultyChanged")] void DifficultyChanged(object value) {
            if(selectedSong == null || selectedDiff == null || updatingDifficultyControls) return;
            actionError=null;
            selectedDiff = selectedSong.diffs.FirstOrDefault(x => x.detailsDiff.characteristic == selectedDiff.detailsDiff.characteristic && SongPresentation.Difficulty(x.detailsDiff.difficulty) == value?.ToString()) ?? selectedDiff;
            RefreshDifficulty();
        }
        void RefreshDifficulty() {
            if(selectedDiff == null) return;
            mode = SongPresentation.Mode(selectedDiff.detailsDiff.characteristic);
            difficulty = SongPresentation.Difficulty(selectedDiff.detailsDiff.difficulty);
            difficultyChoices = selectedSong.diffs.Where(x => x.detailsDiff.characteristic == selectedDiff.detailsDiff.characteristic).OrderBy(x => x.detailsDiff.difficulty).Select(x => (object)SongPresentation.Difficulty(x.detailsDiff.difficulty)).Distinct().ToList();
            updatingDifficultyControls=true;
            try {
                Notify("difficultyChoices"); Notify("selectedMode"); Notify("selectedDifficulty");
                modeDropdown.Values=modeChoices; modeDropdown.UpdateChoices(); modeDropdown.Value=mode;
                difficultyDropdown.Values=difficultyChoices; difficultyDropdown.UpdateChoices(); difficultyDropdown.Value=difficulty;
            } finally { updatingDifficultyControls=false; }
            var d = selectedDiff.detailsDiff;
            songDiffMetrics.text = SongPresentation.Nps(d.notes,selectedSong.detailsSong.songDurationSeconds,true);
            songDiffMotion.text=d.njs.ToString("0.#");noteCount.text=d.notes.ToString("N0");obstacleCount.text=d.obstacles.ToString("N0");bombCount.text=d.bombs.ToString("N0");starValue.text=SongPresentation.Stars(selectedDiff);
            var tags = BSSFlowCoordinator.songDetails.tags.Where(x => (selectedSong.detailsSong.tags & x.Value) != 0).Select(x => {var translated=BssText.T(x.Key);return translated==x.Key ? Configuration.FilterOptions.FormatBeatSaverTag(x.Key) : translated;}).ToArray();
            mapInfo = FilterView.FormatMods(d.mods) + (tags.Length == 0 ? "" : "\n" + String.Join(" · ",tags));
            SetActionStatus(selectedDiff.passesFilter ? "" : BssText.T("Outside current filters"));
            RefreshDownloadState();
        }
        async Task LoadMediaAsync(SongAssetAsyncLoader loader,SongSearchSong song,int generation,CancellationToken token) {
            SongAssetAsyncLoader.CoverLease lease = null;
            try {
                songPreviewPlayer = songPreviewPlayer ? songPreviewPlayer : Resources.FindObjectsOfTypeAll<SongPreviewPlayer>().FirstOrDefault();
                songPreviewPlayer?.CrossfadeToDefault();
                lease = await loader.AcquireCoverAsync(song.detailsSong,token);
                if(token.IsCancellationRequested || !this || generation != revision || selectedSong?.hash != song.hash) return;
                coverLease = lease; lease = null; coverImage.sprite = coverLease.Sprite;
                if(PluginConfig.Instance.loadSongPreviews) {
                    var preview = await loader.LoadPreviewAsync(song.detailsSong,token);
                    if(!token.IsCancellationRequested && generation == revision && selectedSong?.hash == song.hash && preview != null) songPreviewPlayer?.CrossfadeTo(preview,-5f,0,preview.length,null);
                }
            } catch(OperationCanceledException) { }
              catch(Exception ex) { Plugin.Log.Warn("Song media [" + song.hash + "]: " + ex); }
            finally { lease?.Dispose(); }
        }
        internal void ReleaseAssets() {
            revision++; songAssetLoadCanceller?.Cancel(); songAssetLoadCanceller?.Dispose(); songAssetLoadCanceller = null;
            coverLease?.Dispose(); coverLease = null;
            if(songPreviewPlayer) songPreviewPlayer.CrossfadeToDefault();
        }
        internal Task DrainMediaAsync() => mediaTask;
        internal Task DrainPlayAsync() => playTask;
        internal void RefreshDownloadState() {
            if(downloadButton == null || selectedSong == null) return;
            bool downloaded = selectedSong.CheckIsDownloaded();
            downloadButton.gameObject.SetActive(!downloaded);
            playButton.gameObject.SetActive(downloaded);
            downloadButton.interactable = !busy && selectedSong.CheckIsDownloadable();
            playButton.interactable = !busy && Manager.goToSongSelect != null && selectedDiff != null;
            if(!busy && selectedDiff?.passesFilter == true) SetActionStatus(actionError ?? SongPresentation.State(selectedSong));
        }
        void SetActionStatus(string message) { songActionStatus.text=message; songActionStatus.gameObject.SetActive(!String.IsNullOrEmpty(message)); }
        internal void SetIsDownloaded(bool isDownloaded,bool downloadable = true) => RefreshDownloadState();
        [UIAction("Download")] void DownloadButton() { if(selectedSong != null) BSSFlowCoordinator.downloadHistoryView.TryAddDownload(selectedSong); RefreshDownloadState(); }
        BSMLParserParams detailsParams;
        [UIAction("ShowSongDetails")] void ShowSongDetails() {
            if(selectedSong == null) return;
            BSMLStuff.InitSplitView(ref detailsParams,gameObject,SplitViews.UploadDetails.instance).EmitEvent("ShowModal");
            SplitViews.UploadDetails.instance.Populate(selectedSong,uploadInfo,mapInfo);
        }
        [UIAction("FilterByUploader")] void FilterByUploader() {
            if(selectedSong == null) return;
            FilterView.currentFilter.uploadersString = selectedSong.detailsSong.uploaderName.ToLowerInvariant();
            FilterView.currentFilter.NotifyPropertyChanged("uploadersString"); BSSFlowCoordinator.FilterSongs();
        }
        [UIAction("Play")] void _Play() => PlaySong();
        internal void PlaySong(SongSearchSong songToPlay = null) {
            if(busy) return;
            var song = songToPlay ?? selectedSong;
            var diff = song == selectedSong ? selectedDiff : song?.GetFirstPassingDifficulty();
            if(song == null || diff == null) return;
            StartPlay(song,diff);
        }
        void StartPlay(SongSearchSong song,SongSearchSong.SongSearchDiff diff) { if(!busy) playTask = PlayCoreAsync(song,diff); }
        async Task PlayCoreAsync(SongSearchSong song,SongSearchSong.SongSearchDiff diff) {
            busy = true; RefreshDownloadState();
            var token = BSSFlowCoordinator.closeCancelSource.Token;
            try {
                if(!song.CheckIsDownloadedAndLoaded()) {
                    SetActionStatus(BssText.T("Loading song"));
                    await BSSFlowCoordinator.RefreshSongsAsync(token);
                }
                token.ThrowIfCancellationRequested();
                if(!BSSFlowCoordinator.IsOpen(token)) throw new OperationCanceledException(token);
                var level = song.GetLoadedLevel();
                if(level == null) throw new InvalidOperationException("Downloaded map is not registered: " + song.hash);
                string target = diff.detailsDiff.characteristic.ToString().Replace("ThreeSixty","360").Replace("Ninety","90");
                var key = level.GetBeatmapKeys().FirstOrDefault(x => x.difficulty == (BeatmapDifficulty)diff.detailsDiff.difficulty && x.characteristic.SerializedName() == target);
                if(String.IsNullOrEmpty(key.levelId)) throw new InvalidOperationException("Selected difficulty absent: " + song.hash + "/" + diff.serializedDiff);
                var data = SongCore.Collections.GetCustomLevelSongDifficultyData(key);
                if(data == null) throw new InvalidOperationException("SongCore difficulty requirements unavailable: " + key);
                var missing = data.additionalDifficultyData._requirements.Where(x => !SongCore.Collections.capabilities.Contains(x)).ToArray();
                if(missing.Length > 0) { actionError = BssText.T("Missing mods") + ": " + String.Join(", ",missing); return; }
                var state = new LevelSelectionFlowCoordinator.State(LevelCategory.CustomSongs,SongCore.Loader.CustomLevelsPack,in key,level);
                var solo = Resources.FindObjectsOfTypeAll<SoloFreePlayFlowCoordinator>().FirstOrDefault();
                var multiplayer = Resources.FindObjectsOfTypeAll<MultiplayerLevelSelectionFlowCoordinator>().FirstOrDefault();
                solo?.Setup(state); multiplayer?.Setup(state);
                SelectRequestedDifficulty.Prepare(key);
                ReturnToBSS.returnTobss = PluginConfig.Instance.returnToBssFromSolo;
                BSSFlowCoordinator.Close(true,() => Manager.goToSongSelect?.Invoke());
            } catch(OperationCanceledException) { }
              catch(Exception ex) { SelectRequestedDifficulty.Clear(); actionError = BssText.T("Cannot open selected difficulty"); SetActionStatus(actionError); Plugin.Log.Warn("Play [" + song.hash + "]: " + ex); }
            finally { busy = false; RefreshDownloadState(); }
        }
        void OnDestroy() { ReleaseAssets(); }
    }
}
