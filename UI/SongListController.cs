using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BeatSaberMarkupLanguage.Attributes;
using BeatSaberMarkupLanguage.Components;
using BeatSaberMarkupLanguage.Components.Settings;
using BeatSaberMarkupLanguage.Parser;
using BeatSaberMarkupLanguage.ViewControllers;
using BetterSongSearch.UI.CustomLists;
using BetterSongSearch.Util;
using HMUI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace BetterSongSearch.UI {
    [ViewDefinition("BetterSongSearch.UI.Views.SongList.bsml")]
    class SongListController : BSMLAutomaticViewController,TableView.IDataSource {
        internal static IList<SongSearchSong> filteredSongsList, searchedSongsList;
        internal SelectedSongView selectedSongView => BSSFlowCoordinator.detailView.selectedSongView;
        [UIComponent("searchInProgress")] ImageView searchInProgress;
        [UIComponent("resultCount")] TMP_Text resultCount;
        [UIComponent("songList")] public CustomListTableData songListData;
        [UIComponent("searchBoxContainer")] VerticalLayoutGroup searchBoxContainer;
        [UIComponent("scrollBarContainer")] VerticalLayoutGroup scrollBarContainer;
        [UIComponent("sortDropdown")] DropDownListSetting sortDropdown;
        internal InputFieldView songSearchInput { get; private set; }
        TMP_Text searchPlaceholder;
        string resultStatusKey;
        public TableView songList => songListData?.TableView;
        internal int FirstVisibleRow => Math.Max(0,songList?.GetVisibleCellsIdRange().Item1 ?? 0);
        CancellationTokenSource searchCancel;
        Coroutine debounce;
        readonly List<Task> searches = new List<Task>();
        int revision;
        bool selecting;
        [UIAction("#post-parse")] void Parsed() {
            var source = Resources.FindObjectsOfTypeAll<InputFieldView>().FirstOrDefault(x => x.gameObject.name == "SearchInputField");
            if(source != null) {
                var box = Instantiate(source.gameObject,searchBoxContainer.transform,false);
                var boxLayout=box.GetComponent<LayoutElement>() ?? box.AddComponent<LayoutElement>();
                boxLayout.layoutPriority=10; boxLayout.ignoreLayout=false; boxLayout.minWidth=boxLayout.preferredWidth=114;
                boxLayout.flexibleWidth=0; boxLayout.minHeight=boxLayout.preferredHeight=6; boxLayout.flexibleHeight=0;
                var boxFitter=box.GetComponent<ContentSizeFitter>(); if(boxFitter!=null) {boxFitter.horizontalFit=ContentSizeFitter.FitMode.Unconstrained;boxFitter.verticalFit=ContentSizeFitter.FitMode.Unconstrained;}
                songSearchInput = box.GetComponent<InputFieldView>(); songSearchInput.text = "";
                searchPlaceholder = box.transform.Find("PlaceholderText")?.GetComponent<TMP_Text>();
                if(searchPlaceholder != null) searchPlaceholder.text = BssText.T("Search songs, artists, mappers or key");
                foreach(var text in box.GetComponentsInChildren<TMP_Text>(true)) text.fontSize=2.6f;
                songSearchInput._keyboardPositionOffset = new Vector3(-60,-36);
                songSearchInput.onValueChanged.AddListener(_ => UpdateSearchedSongsList());
            }
            songList.SetDataSource(this,false);
            BSMLStuff.GetScrollbarForTable(songListData.gameObject,scrollBarContainer.transform);
            BSMLStuff.InitializeScrollViews(gameObject); BssUi.Style(gameObject); BssUi.Setting(sortDropdown,65,0);
            BssUi.Text(resultCount,22,8);
            StartCoroutine(FinalizeLayout());
        }
        IEnumerator FinalizeLayout() { yield return null; BssUi.Setting(sortDropdown,65,0); }
        public void UpdateSearchedSongsList() {
            searchCancel?.Cancel();
            if(debounce != null) StopCoroutine(debounce);
            debounce = StartCoroutine(SearchRoutine(++revision));
        }
        IEnumerator SearchRoutine(int generation) {
            yield return new WaitForSecondsRealtime(.1f);
            if(filteredSongsList == null || songListData == null || BSSFlowCoordinator.closeCancelSource.IsCancellationRequested) yield break;
            var source = filteredSongsList;
            string query = songSearchInput?.text ?? "";
            string sort = selectedSortMode;
            var cancel = CancellationTokenSource.CreateLinkedTokenSource(BSSFlowCoordinator.closeCancelSource.Token);
            searchCancel = cancel;
            searchInProgress.gameObject.SetActive(true);
            Task<SongSearchSong[]> task = Task.Run(() => {
                cancel.Token.ThrowIfCancellationRequested();
                var values = query.Length == 0 ? source.OrderByDescending(sortModes[sort]) : WeightedSongSearch.Search(source,query,sortModes[sort],cancel.Token);
                return values.ToArray();
            },cancel.Token);
            searches.RemoveAll(x => x.IsCompleted); searches.Add(task);
            _ = task.ContinueWith(t => { if(searchCancel == cancel) searchCancel = null; cancel.Dispose(); },CancellationToken.None,TaskContinuationOptions.None,TaskScheduler.FromCurrentSynchronizationContext());
            _ = task.ContinueWith(t => Plugin.Log.Warn("Search: " + t.Exception),TaskContinuationOptions.OnlyOnFaulted);
            while(!task.IsCompleted && generation == revision && !cancel.IsCancellationRequested) yield return null;
            if(generation != revision || cancel.IsCancellationRequested) yield break;
            SongSearchSong[] results = null;
            try { results = task.GetAwaiter().GetResult(); }
            catch(OperationCanceledException) { }
            catch(Exception ex) { resultStatusKey = "Search failed"; resultCount.text = BssText.T(resultStatusKey); Plugin.Log.Warn("Search result: " + ex); }
            searchInProgress.gameObject.SetActive(false);
            if(results == null) yield break;
            string previous = selectedSongView.selectedSong?.hash;
            searchedSongsList = results;
            resultStatusKey = null;
            songList.ReloadData();
            resultCount.text = results.Length.ToString("N0") + " " + BssText.T("Results");
            var selected = results.FirstOrDefault(x => x.hash.Equals(previous,StringComparison.OrdinalIgnoreCase)) ?? results.FirstOrDefault();
            selectedSongView.SetSelectedSong(selected);
            if(selected != null) SelectByHash(selected.hash,false); else songList.ClearSelection();
        }
        internal void SelectByHash(string hash,bool scroll) {
            if(searchedSongsList == null || songList == null) return;
            int index = -1;
            for(int i=0;i<searchedSongsList.Count;i++) if(searchedSongsList[i].hash.Equals(hash,StringComparison.OrdinalIgnoreCase)) { index = i; break; }
            selecting = true;
            try {
                if(index < 0) songList.ClearSelection();
                else { if(scroll) songList.ScrollToCellWithIdx(index,TableView.ScrollPositionType.Center,false); songList.SelectCellWithIdx(index); }
            } finally { selecting = false; }
        }
        [UIAction("SelectSong")] void SelectSong(TableView table,int index) {
            if(selecting || searchedSongsList==null || index<0 || index>=searchedSongsList.Count) return;
            var song=searchedSongsList[index];
            if(String.Equals(song.hash,selectedSongView.selectedSong?.hash,StringComparison.OrdinalIgnoreCase)) return;
            selectedSongView.SetSelectedSong(song);
        }
        [UIAction("UpdateDataAndFilters")] void UpdateDataAndFilters(object value) => UpdateSearchedSongsList();
        [UIAction("SelectRandom")] void SelectRandom() { if(searchedSongsList?.Count > 0) selectedSongView.SetSelectedSong(searchedSongsList[UnityEngine.Random.Range(0,searchedSongsList.Count)],true); }
        public float CellSize(int index) => 12.5f;
        public int NumberOfCells() => searchedSongsList?.Count ?? 0;
        public TableCell CellForIdx(TableView table,int index) => SongListTableData.GetCell(table).PopulateWithSongData(searchedSongsList[index]);
        internal void RefreshRows() { if(songList != null && searchedSongsList != null) songList.RefreshCells(false,true); }
        internal void RefreshLocalization() {
            if(searchPlaceholder != null)
                searchPlaceholder.text = BssText.T("Search songs, artists, mappers or key");
            if(resultCount != null) {
                if(!String.IsNullOrEmpty(resultStatusKey))
                    resultCount.text = BssText.T(resultStatusKey);
                else if(searchedSongsList != null)
                    resultCount.text = searchedSongsList.Count.ToString("N0") + " " + BssText.T("Results");
            }
            RefreshRows();
        }
        internal void StopSearch() { revision++; searchCancel?.Cancel(); StopAllCoroutines(); if(searchInProgress != null) searchInProgress.gameObject.SetActive(false); }
        internal async Task DrainAsync() { try { await Task.WhenAll(searches.ToArray()); } catch(OperationCanceledException) { } finally { searchCancel?.Dispose(); searches.Clear(); } }
        BSMLParserParams multiDlParams,createPlaylistParams,settingsParams;
        [UIAction("ShowMultiDlModal")] void ShowMultiDlModal() => BSMLStuff.InitSplitView(ref multiDlParams,gameObject,SplitViews.MultiDl.instance).EmitEvent("ShowModal");
        [UIAction("ShowPlaylistCreation")] void ShowPlaylistCreation() { BSMLStuff.InitSplitView(ref createPlaylistParams,gameObject,SplitViews.PlaylistCreation.instance); SplitViews.PlaylistCreation.instance.Open(); }
        [UIAction("ShowSettings")] void ShowSettings() => BSMLStuff.InitSplitView(ref settingsParams,gameObject,SplitViews.Settings.instance).EmitEvent("ShowModal");
        internal static readonly IReadOnlyDictionary<string,Func<SongSearchSong,float>> sortModes = new Dictionary<string,Func<SongSearchSong,float>> {
            {"Newest Upload",x => x.detailsSong.uploadTimeUnix}, {"Oldest Upload",x => uint.MaxValue-x.detailsSong.uploadTimeUnix},
            {"Ranked/Qualified time",x => x.isRanked() ? x.detailsSong.rankedChangeUnix : 0},
            {"Most Stars",x => x.diffs.Where(d => d.passesFilter).Select(d => d.GetStars()).DefaultIfEmpty(0).Max()},
            {"Least Stars",x => -x.diffs.Where(d => d.passesFilter && d.GetStars()>0).Select(d => d.GetStars()).DefaultIfEmpty(float.MaxValue).Min()},
            {"Best rated",x => x.detailsSong.rating},{"Worst rated",x => x.detailsSong.upvotes+x.detailsSong.downvotes == 0 ? float.MinValue : -x.detailsSong.rating}
        };
        internal static readonly IReadOnlyDictionary<string,Func<SongSearchSong.SongSearchDiff,float>> sortModesDiffSort = new Dictionary<string,Func<SongSearchSong.SongSearchDiff,float>> {
            {"Most Stars",x => -x.GetStars()}, {"Least Stars",x => x.GetStars()>0 ? x.GetStars() : float.MaxValue}
        };
        [UIValue("sortModeSelections")] static readonly IReadOnlyList<object> sortModeSelections = sortModes.Keys.Cast<object>().ToArray();
        [UIValue("selectedSortMode")] internal static string selectedSortMode { get; private set; } = "Newest Upload";
    }
}
