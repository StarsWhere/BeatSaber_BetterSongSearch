using System;
using System.Threading;
using System.Threading.Tasks;
using BeatSaberMarkupLanguage.Attributes;
using BetterSongSearch.Util;
using HMUI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace BetterSongSearch.UI.SplitViews {
    class UploadDetails {
        internal static readonly UploadDetails instance = new UploadDetails();
        [UIComponent("songName")] TMP_Text songName;
        [UIComponent("selectedSongDescription")] CurvedTextMeshPro selectedSongDescription;
        [UIComponent("songDetailsLoading")] ImageView songDetailsLoading;
        [UIComponent("descriptionScroll")] Transform descriptionScroll;
        SongSearchSong song;
        string summary;
        string description;
        bool descriptionLoading;
        bool descriptionUnavailable;
        CancellationTokenSource cancel;
        Task request;
        int revision;
        [UIAction("#post-parse")] void Parsed() {
            BSMLStuff.InitializeScrollViews(descriptionScroll.gameObject);
            BssUi.Text(selectedSongDescription,75,-1);
            var fitter=selectedSongDescription.GetComponent<ContentSizeFitter>() ?? selectedSongDescription.gameObject.AddComponent<ContentSizeFitter>();
            fitter.verticalFit=ContentSizeFitter.FitMode.PreferredSize;
            selectedSongDescription.rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal,75);
            var column=(RectTransform)selectedSongDescription.transform.parent;
            var layout=column.GetComponent<LayoutElement>() ?? column.gameObject.AddComponent<LayoutElement>();
            layout.layoutPriority=10;layout.minWidth=layout.preferredWidth=87;layout.flexibleWidth=0;
            column.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal,87);
        }
        internal void Populate(SongSearchSong value,string uploadInfo,string mapInfo) {
            song = value; summary=uploadInfo+"\n"+mapInfo;
            description = null; descriptionLoading = true; descriptionUnavailable = false;
            Reload();
        }
        internal void RefreshLocalization(string uploadInfo,string mapInfo) {
            if(song == null || selectedSongDescription == null) return;
            summary = uploadInfo + "\n" + mapInfo;
            RenderDescription();
        }
        void Reload() {
            if(song == null) return;
            cancel?.Cancel(); cancel?.Dispose();
            cancel = CancellationTokenSource.CreateLinkedTokenSource(UI.BSSFlowCoordinator.closeCancelSource.Token);
            songName.text = SongPresentation.Name(song.detailsSong.songName); BssTypography.Apply(songName,true);
            description = null; descriptionLoading = true; descriptionUnavailable = false;
            RenderDescription(); songDetailsLoading.gameObject.SetActive(true);
            request = LoadAsync(song,++revision,cancel.Token);
        }
        async Task LoadAsync(SongSearchSong value,int generation,CancellationToken token) {
            try {
                string text = await UI.BSSFlowCoordinator.assetLoader.GetSongDescription(value.detailsSong.key,token);
                if(token.IsCancellationRequested || generation != revision) return;
                description = text; descriptionLoading = false; descriptionUnavailable = false;
                RenderDescription();
            } catch(OperationCanceledException) { }
              catch(Exception ex) {
                  if(generation == revision && !token.IsCancellationRequested) {
                      description = null; descriptionLoading = false; descriptionUnavailable = true;
                      RenderDescription();
                  }
                  Plugin.Log.Warn("Description [" + value.hash + "]: " + ex);
              }
            finally { if(generation == revision && songDetailsLoading != null) songDetailsLoading.gameObject.SetActive(false); }
        }
        void RenderDescription() {
            if(selectedSongDescription == null) return;
            string body = descriptionLoading ? BssText.T("Loading...") :
                descriptionUnavailable ? BssText.T("Description unavailable") :
                String.IsNullOrWhiteSpace(description) ? BssText.T("No description") : description;
            selectedSongDescription.text = summary + "\n\n" + body;
            BssTypography.Apply(selectedSongDescription);
        }
        [UIAction("RetryDescription")] void Retry() => Reload();
    }
}
