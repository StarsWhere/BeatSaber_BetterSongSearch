using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using BeatSaberMarkupLanguage;
using BeatSaberMarkupLanguage.Attributes;
using BetterSongSearch.Util;
using HMUI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace BetterSongSearch.UI.CustomLists {
    static class SongListTableData {
        const string ReuseIdentifier = "BSS.SongSummary";
        public static CustomSongListTableCell GetCell(TableView tableView) {
            var cell = tableView.DequeueReusableCellForIdentifier(ReuseIdentifier);
            if(cell == null) {
                cell = new GameObject("CustomSongListTableCell",typeof(Touchable)).AddComponent<CustomSongListTableCell>();
                cell.transform.SetParent(tableView.transform,false);
                cell.interactable = true;
                cell.reuseIdentifier = ReuseIdentifier;
                BSMLParser.Instance.Parse(Utilities.GetResourceContent(Assembly.GetExecutingAssembly(),"BetterSongSearch.UI.CustomLists.SongListCell.bsml"),cell.gameObject,cell);
                BssTypography.Apply(cell.gameObject);
            }
            return (CustomSongListTableCell)cell;
        }
    }
    class CustomSongListTableCell : TableCell {
        [UIComponent("songName")] TMP_Text songName;
        [UIComponent("songAuthorName")] TMP_Text songAuthorName;
        [UIComponent("songSummary")] TMP_Text songSummary;
        [UIComponent("songStats")] TMP_Text songStats;
        [UIComponent("songState")] TMP_Text songState;
        [UIComponent("cover")] Image cover;
        [UIComponent("bgContainer")] ImageView bg;
        SongSearchSong song;
        CancellationTokenSource coverCancel;
        SongAssetAsyncLoader.CoverLease coverLease;
        Task coverTask;
        int revision;
        [UIAction("#post-parse")] void Parsed() {
            BssUi.Text(songName,73,4.5f); BssUi.Text(songState,18,4.5f);
            BssUi.Text(songAuthorName,71,3.5f); BssUi.Text(songStats,20,3.5f); BssUi.Text(songSummary,93,3.5f);
        }
        public CustomSongListTableCell PopulateWithSongData(SongSearchSong value) {
            bool changed = song == null || song.hash != value.hash;
            song = value;
            songName.text = SongPresentation.Name(song.detailsSong.songName);
            songAuthorName.text = SongPresentation.Name(song.detailsSong.songAuthorName) + " · " + BssText.T("Mapper") + " " + SongPresentation.Name(song.detailsSong.levelAuthorName);
            songStats.text = SongPresentation.Duration(song.detailsSong.songDurationSeconds) + " · " + SongPresentation.Rating(in song.detailsSong);
            songSummary.text = SongPresentation.DiffSummary(song);
            songState.text = SongPresentation.State(song);
            songName.color = new Color(.94f,.96f,.98f);
            BssTypography.Apply(songName,true);
            if(changed || coverLease == null) BeginCover();
            RefreshBgState();
            return this;
        }
        void BeginCover() {
            ReleaseCover();
            cover.sprite = SongCore.Loader.defaultCoverImage;
            if(song == null || BSSFlowCoordinator.assetLoader == null || !isActiveAndEnabled) return;
            coverCancel = CancellationTokenSource.CreateLinkedTokenSource(BSSFlowCoordinator.closeCancelSource.Token);
            coverTask = LoadCoverAsync(BSSFlowCoordinator.assetLoader,song,++revision,coverCancel.Token);
        }
        async Task LoadCoverAsync(SongAssetAsyncLoader loader,SongSearchSong value,int generation,CancellationToken token) {
            SongAssetAsyncLoader.CoverLease lease = null;
            try {
                lease = await loader.AcquireCoverAsync(value.detailsSong,token);
                if(token.IsCancellationRequested || !this || generation != revision || song?.hash != value.hash) return;
                coverLease = lease; lease = null;
                cover.sprite = coverLease.Sprite;
            } catch(OperationCanceledException) { }
              catch(Exception ex) { Plugin.Log.Warn("Cover [" + value.hash + "]: " + ex); }
            finally { lease?.Dispose(); }
        }
        void ReleaseCover() {
            revision++;
            coverCancel?.Cancel(); coverCancel?.Dispose(); coverCancel = null;
            coverLease?.Dispose(); coverLease = null;
        }
        protected override void OnEnable() { base.OnEnable(); if(song != null && cover != null) BeginCover(); }
        public override void OnDisable() { ReleaseCover(); base.OnDisable(); }
        protected override void OnDestroy() { ReleaseCover(); base.OnDestroy(); }
        public override void WasPreparedForReuse() { ReleaseCover(); song = null; }
        public override void SelectionDidChange(TransitionType transitionType) => RefreshBgState();
        public override void HighlightDidChange(TransitionType transitionType) => RefreshBgState();
        void RefreshBgState() {
            if(bg != null) bg.color = selected ? new Color(.05f,.30f,.40f,.96f) : highlighted ? new Color(.12f,.20f,.27f,.94f) : new Color(.065f,.10f,.145f,.85f);
        }
    }
}
