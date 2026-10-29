using BeatSaberMarkupLanguage.Attributes;
using BeatSaberMarkupLanguage.ViewControllers;
using BetterSongSearch.Util;
using TMPro;
using UnityEngine;
using System.Collections;

namespace BetterSongSearch.UI {
    [ViewDefinition("BetterSongSearch.UI.Views.SelectedSong.bsml")]
    class DetailController : BSMLAutomaticViewController {
        internal SelectedSongView selectedSongView;
        [UIComponent("detailArea")] Transform detailArea;
        [UIComponent("queueArea")] Transform queueArea;
        [UIComponent("queueSummary")] TMP_Text queueSummary;
        [UIComponent("emptyState")] Transform emptyState;
        internal bool QueueVisible { get; private set; }
        void Awake() { selectedSongView = gameObject.AddComponent<SelectedSongView>(); }
        [UIAction("#post-parse")] void Parsed() {
            BSSFlowCoordinator.downloadHistoryView.RenderInto(queueArea.gameObject);
            BSSFlowCoordinator.downloadHistoryView.Changed += Refresh;
            BssUi.Style(gameObject);
            StartCoroutine(FinalizeLayout());
            ShowDetails(); Refresh();
        }
        IEnumerator FinalizeLayout() { yield return null; selectedSongView.Parsed(); BssUi.Text(queueSummary,0,7); }
        [UIAction("ShowDetails")] internal void ShowDetails() {
            QueueVisible = false;
            if(queueArea != null) queueArea.gameObject.SetActive(false);
            Refresh();
        }
        [UIAction("ShowQueue")] internal void ShowQueue() {
            QueueVisible = true;
            if(detailArea != null) detailArea.gameObject.SetActive(false);
            if(emptyState != null) emptyState.gameObject.SetActive(false);
            if(queueArea != null) queueArea.gameObject.SetActive(true);
            BSSFlowCoordinator.downloadHistoryView.RefreshTable();
        }
        internal void Refresh() {
            if(queueSummary != null) queueSummary.text = BSSFlowCoordinator.downloadHistoryView.Summary;
            if(!QueueVisible) {
                bool hasSong = selectedSongView.selectedSong != null;
                if(detailArea != null) detailArea.gameObject.SetActive(hasSong);
                if(emptyState != null) emptyState.gameObject.SetActive(!hasSong);
            }
            selectedSongView.RefreshDownloadState();
            BSSFlowCoordinator.songListView?.RefreshRows();
        }
        protected override void OnDestroy() {
            if(BSSFlowCoordinator.downloadHistoryView != null) BSSFlowCoordinator.downloadHistoryView.Changed -= Refresh;
            base.OnDestroy();
        }
    }
}
