using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BetterSongSearch.UI;
using BetterSongSearch.Util;
using HarmonyLib;
using HMUI;
using IPA;
using Newtonsoft.Json;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace BetterSongSearchSmoke {
    [Plugin(RuntimeOptions.SingleStartInit)]
    public sealed class Plugin {
        GameObject owner;
        [OnStart] public void Start() {
            if(String.IsNullOrEmpty(Environment.GetEnvironmentVariable("BSS_UI_SMOKE_DIR"))) return;
            string playlist=Environment.GetEnvironmentVariable("BSS_UI_PLAYLIST_DLL");
            if(!String.IsNullOrEmpty(playlist)) System.Reflection.Assembly.LoadFrom(playlist);
            owner = new GameObject("BetterSongSearchSmoke"); UnityEngine.Object.DontDestroyOnLoad(owner); owner.AddComponent<Runner>();
        }
        [OnExit] public void Exit() { if(owner != null) UnityEngine.Object.Destroy(owner); }
    }
    public sealed class Runner : MonoBehaviour {
        readonly List<object> checks = new List<object>();
        readonly List<string> errors = new List<string>();
        readonly Stopwatch timer = Stopwatch.StartNew();
        string directory,phase;
        Task run;
        bool finished;
        double lastProgress;
        void Start() {
            directory = Environment.GetEnvironmentVariable("BSS_UI_SMOKE_DIR"); Directory.CreateDirectory(directory);
            Application.logMessageReceived += OnLog; run = RunAsync();
        }
        void OnDestroy() { Application.logMessageReceived -= OnLog; }
        void Update() {
            if(!finished && timer.Elapsed.TotalSeconds-lastProgress > 5) { lastProgress=timer.Elapsed.TotalSeconds; File.WriteAllText(Path.Combine(directory,"progress.json"),JsonConvert.SerializeObject(new {phase,elapsed=lastProgress,scene=UnityEngine.SceneManagement.SceneManager.GetActiveScene().name,results=SongListController.searchedSongsList?.Count,views=Resources.FindObjectsOfTypeAll<ViewController>().Where(x=>x.gameObject.activeInHierarchy).Select(x=>new {x.name,x.isActivated}).ToArray(),buttons=Resources.FindObjectsOfTypeAll<Button>().Where(x=>x.gameObject.activeInHierarchy).Take(30).Select(x=>new {x.name,text=String.Join("/",x.GetComponentsInChildren<TMP_Text>().Select(t=>t.text))}).ToArray(),errors=errors.Take(5).ToArray()})); }
            if(!finished && timer.Elapsed.TotalSeconds > 390) Finish("Timeout: " + phase);
        }
        void OnLog(string message,string stack,LogType type) {
            if((type == LogType.Exception || type == LogType.Error) && (message.Contains("BetterSongSearch") || stack.Contains("BetterSongSearch"))) errors.Add(message + "\n" + stack);
        }
        void Check(string name,bool passed,string detail) { checks.Add(new {name,passed,detail}); if(!passed) errors.Add(name + ": " + detail); }
        async Task Wait(Func<bool> predicate,int seconds) {
            var end = DateTime.UtcNow.AddSeconds(seconds);
            while(!predicate() && DateTime.UtcNow < end && !finished) await Task.Delay(100);
            if(!predicate()) throw new TimeoutException(phase);
        }
        async Task RunAsync() {
            GameplayDownloadChecks gameplayDownload = null;
            try {
                phase = "main menu";
                await Wait(() => UnityEngine.SceneManagement.SceneManager.GetActiveScene().name == "MainMenu",60);
                await Task.Delay(1500);
                Capture("startup.png");
                foreach(var button in Resources.FindObjectsOfTypeAll<Button>().Where(x => x.gameObject.activeInHierarchy && x.GetComponentsInChildren<TMP_Text>(true).Any(t => t.text == "继续" || t.text.Equals("Continue",StringComparison.OrdinalIgnoreCase)))) button.onClick.Invoke();
                await Wait(() => Resources.FindObjectsOfTypeAll<MainMenuViewController>().Any(x => x.isActivated),70);
                await Task.Delay(1500);
                var entry = Resources.FindObjectsOfTypeAll<Button>().FirstOrDefault(x => x.gameObject.activeInHierarchy && x.GetComponentsInChildren<TMP_Text>(true).Any(t => t.text == "搜歌与下载" || t.text == "Better Song Search"));
                Check("menu entry",entry != null,"native BSML menu button");
                if(entry == null) throw new InvalidOperationException("Online song menu entry absent");
                entry.onClick.Invoke();
                phase = "online song results";
                await Wait(() => SongListController.searchedSongsList?.Count > 0 && BSSFlowCoordinator.detailView?.selectedSongView.selectedSong != null,80);
                await Task.Delay(1500);
                Check("left-center-right",BSSFlowCoordinator.filterView.screen.name.Contains("Left") && BSSFlowCoordinator.detailView.screen.name.Contains("Main") && BSSFlowCoordinator.songListView.screen.name.Contains("Right"),BSSFlowCoordinator.filterView.screen.name + "," + BSSFlowCoordinator.detailView.screen.name + "," + BSSFlowCoordinator.songListView.screen.name);
                var cells = BSSFlowCoordinator.songListView.GetComponentsInChildren<BetterSongSearch.UI.CustomLists.CustomSongListTableCell>().Where(x => x.gameObject.activeInHierarchy).ToArray();
                Check("six-row list",cells.Length >= 6 && cells.Length <= 7,"visible=" + cells.Length);
                var view = BSSFlowCoordinator.detailView.selectedSongView;
                Check("matching representative",view.selectedDiff?.passesFilter == true && view.selectedDiff.serializedDiff == view.selectedSong.GetFirstPassingDifficulty()?.serializedDiff,view.selectedDiff?.serializedDiff);
                var title = (TMP_Text)AccessTools.Field(typeof(SelectedSongView),"selectedSongName").GetValue(view);
                Check("real bold title",title.font.name == "BetterSongSearch-YaHei-Bold" && Math.Abs(title.fontSize-4.6f)<.01f && title.fontStyle == FontStyles.Normal,title.font.name + "/" + title.fontSize);
                var regular = BSSFlowCoordinator.filterView.GetComponentsInChildren<TMP_Text>().First(x => x.text.Any(c => c >= 0x4e00 && c <= 0x9fff));
                regular.ForceMeshUpdate();
                var glyphs = regular.textInfo.characterInfo.Take(regular.textInfo.characterCount).Where(x => x.isVisible && x.character >= 0x4e00 && x.character <= 0x9fff).ToArray();
                Check("real regular Chinese glyphs",glyphs.Length > 0 && glyphs.All(x => x.fontAsset.name == "BetterSongSearch-YaHei-Regular"),String.Join(",",glyphs.Select(x => x.fontAsset.name).Distinct()));
                Check("unknown values",SongPresentation.Nps(42,0) == "—" && SongPresentation.Duration(0) == "—","zero duration never becomes infinity or 0:00");
                var votes = new SongDetailsCache.Structs.Song(); Check("zero votes",SongPresentation.Rating(in votes) == "—","no fictitious zero rating");
                Check("filter fields have height",BSSFlowCoordinator.filterView.GetComponentsInChildren<TMP_Text>().Where(x=>x.name=="Label").All(x=>x.rectTransform.rect.height>=7),"native setting labels occupy an 8-unit row");
                Check("filter font size",BSSFlowCoordinator.filterView.GetComponentsInChildren<TMP_Text>().Where(x=>x.name=="Label").All(x=>Math.Abs(x.fontSize-2.6f)<.01f),"native templates must not reset label size");
                var filterRect=BSSFlowCoordinator.filterView.rectTransform;
                foreach(var label in BSSFlowCoordinator.filterView.GetComponentsInChildren<TMP_Text>().Where(x=>x.name=="Label" || x.name=="Title")) {
                    var corners=new Vector3[4];label.rectTransform.GetWorldCorners(corners);var positions=corners.Select(filterRect.InverseTransformPoint).ToArray();
                    Check("filter label within panel",positions.Min(p=>p.x)>=filterRect.rect.xMin && positions.Max(p=>p.x)<=filterRect.rect.xMax,label.text+" x="+positions.Min(p=>p.x)+".."+positions.Max(p=>p.x));
                }
                Check("search field has width",BSSFlowCoordinator.songListView.songSearchInput.GetComponentsInChildren<TMP_Text>().All(x=>x.rectTransform.rect.width>20),"no collapsed native search field");
                var choices=BSSFlowCoordinator.detailView.GetComponentsInChildren<BeatSaberMarkupLanguage.Components.Settings.DropDownListSetting>();
                Check("selected difficulty controls",choices.All(x=>x.Value!=null && !String.IsNullOrEmpty(x.Value.ToString())),String.Join("/",choices.Select(x=>x.Value)));
                Check("fixed result title width",cells.All(x=>((TMP_Text)AccessTools.Field(x.GetType(),"songName").GetValue(x)).rectTransform.rect.width>=70),"separate title and status columns");
                foreach(var cell in cells) {var text=(TMP_Text)AccessTools.Field(cell.GetType(),"songName").GetValue(cell);text.ForceMeshUpdate(); Check("visible result title",text.textInfo.characterInfo.Take(text.textInfo.characterCount).Any(x=>x.isVisible),text.text);}
                Check("detail title width",title.rectTransform.rect.width>=89,"two-line title beside enlarged cover");
                var coverRect=(RectTransform)view.coverImage.transform;
                Check("compact cover",Math.Abs(coverRect.rect.width-32)<.1f && Math.Abs(coverRect.rect.height-32)<.1f,coverRect.rect.ToString());
                var metadata=new[]{"songDuration","songBpm","songRating","songVotes"}.Select(name=>(TMP_Text)AccessTools.Field(typeof(SelectedSongView),name).GetValue(view)).ToArray();
                Check("separate readable song metadata",metadata.All(x=>x.fontSize>=3.6f && x.rectTransform.rect.width>=25 && !x.text.Contains("·")),String.Join("/",metadata.Select(x=>x.text)));
                var metricText=(TMP_Text)AccessTools.Field(typeof(SelectedSongView),"songDiffMetrics").GetValue(view);
                var motionText=(TMP_Text)AccessTools.Field(typeof(SelectedSongView),"songDiffMotion").GetValue(view);
                var selectedChart=view.selectedDiff.detailsDiff;
                var detailTexts=view.GetComponentsInChildren<TMP_Text>().Select(x=>x.text).ToArray();
                Check("Chinese chart data",new[]{"音符密度","音符总量","音符流速","障碍数","炸弹数","星级"}.All(detailTexts.Contains),String.Join("/",detailTexts));
                var countText=(TMP_Text)AccessTools.Field(typeof(SelectedSongView),"noteCount").GetValue(view);
                var obstacleText=(TMP_Text)AccessTools.Field(typeof(SelectedSongView),"obstacleCount").GetValue(view);
                var bombText=(TMP_Text)AccessTools.Field(typeof(SelectedSongView),"bombCount").GetValue(view);
                Check("selected chart counts",countText.text==selectedChart.notes.ToString("N0") && obstacleText.text==selectedChart.obstacles.ToString("N0") && bombText.text==selectedChart.bombs.ToString("N0"),view.selectedDiff.serializedDiff+"/"+countText.text+"/"+obstacleText.text+"/"+bombText.text);
                var expectedNps = view.selectedSong.detailsSong.songDurationSeconds == 0 ? "—" : ((double)selectedChart.notes / view.selectedSong.detailsSong.songDurationSeconds).ToString("0.0",System.Globalization.CultureInfo.InvariantCulture);
                bool npsMatches = expectedNps == "—" ? metricText.text == expectedNps : metricText.text == expectedNps + "/s" || metricText.text == expectedNps + "/秒";
                Check("selected chart speed",motionText.text==selectedChart.njs.ToString("0.#") && npsMatches,metricText.text+"/"+motionText.text+"; expected NPS="+expectedNps);
                var actionButton=(UnityEngine.UI.Button)AccessTools.Field(typeof(SelectedSongView),"downloadButton").GetValue(view);
                var detailRect=(RectTransform)((Transform)AccessTools.Field(typeof(DetailController),"detailArea").GetValue(BSSFlowCoordinator.detailView));
                var listRect=(RectTransform)BSSFlowCoordinator.songListView.songList.transform;
                float Bottom(RectTransform rect) { var corners=new Vector3[4];rect.GetWorldCorners(corners);return corners.Min(point=>filterRect.InverseTransformPoint(point).y); }
                Check("detail bottom aligned with results",Math.Abs(Bottom(detailRect)-Bottom(listRect))<=1.5f,"detail bottom="+Bottom(detailRect)+", result bottom="+Bottom(listRect));
                foreach(var text in new[]{metricText,motionText,countText,obstacleText,bombText,(TMP_Text)AccessTools.Field(typeof(SelectedSongView),"starValue").GetValue(view)}) {
                    var corners=new Vector3[4];text.rectTransform.GetWorldCorners(corners);var positions=corners.Select(detailRect.InverseTransformPoint).ToArray();text.ForceMeshUpdate();
                    Check("chart metric fully visible",positions.Min(p=>p.y)>=detailRect.rect.yMin && positions.Max(p=>p.y)<=detailRect.rect.yMax && text.textInfo.characterInfo.Take(text.textInfo.characterCount).Any(x=>x.isVisible) && !text.isTextOverflowing,text.text+" y="+positions.Min(p=>p.y)+".."+positions.Max(p=>p.y));
                }
                Check("action button inside visible frame",Bottom((RectTransform)actionButton.transform)>=Bottom(detailRect),"action bottom="+Bottom((RectTransform)actionButton.transform));
                var originalSong=view.selectedSong;
                var fixture=BSSFlowCoordinator.songsList.FirstOrDefault(song=>song.diffs.Select(diff=>diff.detailsDiff.characteristic).Distinct().Count()>1 && song.diffs.GroupBy(diff=>diff.detailsDiff.characteristic).Any(group=>group.Count()>1));
                if(fixture==null) throw new InvalidOperationException("No multi-mode chart fixture for selection verification");
                view.SetSelectedSong(fixture);await Task.Delay(100);
                var modeSetting=(BeatSaberMarkupLanguage.Components.Settings.DropDownListSetting)AccessTools.Field(typeof(SelectedSongView),"modeDropdown").GetValue(view);
                var difficultySetting=(BeatSaberMarkupLanguage.Components.Settings.DropDownListSetting)AccessTools.Field(typeof(SelectedSongView),"difficultyDropdown").GetValue(view);
                var selectIndex=AccessTools.Method(typeof(BeatSaberMarkupLanguage.Components.Settings.DropDownListSetting),"OnSelectIndex");
                var ownedModes=fixture.diffs.Select(diff=>SongPresentation.Mode(diff.detailsDiff.characteristic)).Distinct().ToArray();
                Check("modes restricted to selected song",modeSetting.Values.Cast<object>().Select(value=>value.ToString()).ToHashSet().SetEquals(ownedModes),String.Join("/",ownedModes));
                foreach(var wantedMode in ownedModes) {
                    selectIndex.Invoke(modeSetting,new object[]{modeSetting.Dropdown,modeSetting.Values.IndexOf(wantedMode)});await Task.Delay(30);
                    Check("native mode selection refresh",SongPresentation.Mode(view.selectedDiff.detailsDiff.characteristic)==wantedMode && view.selectedMode==wantedMode,wantedMode+"/"+view.selectedDiff.serializedDiff);
                    var ownedDifficulties=fixture.diffs.Where(diff=>diff.detailsDiff.characteristic==view.selectedDiff.detailsDiff.characteristic).OrderBy(diff=>diff.detailsDiff.difficulty).Select(diff=>SongPresentation.Difficulty(diff.detailsDiff.difficulty)).Distinct().ToArray();
                    Check("difficulties restricted to selected mode",difficultySetting.Values.Cast<object>().Select(value=>value.ToString()).SequenceEqual(ownedDifficulties),wantedMode+"/"+String.Join("/",ownedDifficulties));
                    foreach(var wantedDifficulty in ownedDifficulties) {
                        selectIndex.Invoke(difficultySetting,new object[]{difficultySetting.Dropdown,difficultySetting.Values.IndexOf(wantedDifficulty)});await Task.Delay(30);
                        var chart=view.selectedDiff.detailsDiff;
                        Check("native difficulty refreshes all metrics",SongPresentation.Mode(chart.characteristic)==wantedMode && SongPresentation.Difficulty(chart.difficulty)==wantedDifficulty && countText.text==chart.notes.ToString("N0") && obstacleText.text==chart.obstacles.ToString("N0") && bombText.text==chart.bombs.ToString("N0") && motionText.text==chart.njs.ToString("0.#") && metricText.text==SongPresentation.Nps(chart.notes,fixture.detailsSong.songDurationSeconds,true) && ((TMP_Text)AccessTools.Field(typeof(SelectedSongView),"starValue").GetValue(view)).text==SongPresentation.Stars(view.selectedDiff),view.selectedDiff.serializedDiff+"/"+countText.text+"/"+motionText.text);
                    }
                }
                view.SetSelectedSong(originalSong);await Task.Delay(100);
                Check("new song replaces available choices",modeSetting.Values.Cast<object>().Select(value=>value.ToString()).ToHashSet().SetEquals(originalSong.diffs.Select(diff=>SongPresentation.Mode(diff.detailsDiff.characteristic)).Distinct()),originalSong.hash);
                PresentationChecks.Run(Check);
                PlaylistChecks.Run(Check);
                await AssetChecks.RunAsync(Check);
                Capture("overview.png"); Capture("details-close.png",false); SaveGeometry();
                AccessTools.Method(typeof(SelectedSongView),"ShowSongDetails").Invoke(view,null);await Task.Delay(300);
                var infoText=(TMP_Text)AccessTools.Field(typeof(BetterSongSearch.UI.SplitViews.UploadDetails),"selectedSongDescription").GetValue(BetterSongSearch.UI.SplitViews.UploadDetails.instance);
                Check("full info retains upload and map data",infoText.text.Contains(view.selectedSong.detailsSong.uploadTime.ToString("yyyy-MM-dd")) && infoText.text.Contains(FilterView.FormatMods(view.selectedDiff.detailsDiff.mods)),infoText.text);
                infoText.ForceMeshUpdate();
                Check("full info text actually visible",infoText.rectTransform.rect.width>=75 && infoText.textInfo.characterInfo.Take(infoText.textInfo.characterCount).Any(x=>x.isVisible) && !infoText.canvasRenderer.cull,infoText.name+"/"+infoText.rectTransform.rect+"/cull="+infoText.canvasRenderer.cull);
                Capture("more-details.png");
                foreach(var visible in BSSFlowCoordinator.detailView.GetComponentsInChildren<ModalView>()) if(visible.gameObject.activeInHierarchy) visible.Hide(false);
                phase="tool dialogs";
                foreach(string action in new[]{"ShowSettings","ShowMultiDlModal","ShowPlaylistCreation"}) {
                    AccessTools.Method(typeof(SongListController),action).Invoke(BSSFlowCoordinator.songListView,null); await Task.Delay(300);
                    var modal=BSSFlowCoordinator.songListView.GetComponentsInChildren<ModalView>().FirstOrDefault(x=>x.gameObject.activeInHierarchy);
                    Check("tool dialog "+action,modal!=null,"parsed native modal");Capture(action+".png");
                    foreach(var visible in BSSFlowCoordinator.songListView.GetComponentsInChildren<ModalView>()) if(visible.gameObject.activeInHierarchy) visible.Hide(false);
                }
                phase = "search empty results";
                AccessTools.Property(typeof(InputFieldView),"text").SetValue(BSSFlowCoordinator.songListView.songSearchInput,"zzzbssuiverificationabcdefghi");
                BSSFlowCoordinator.songListView.UpdateSearchedSongsList();
                await Wait(() => SongListController.searchedSongsList?.Count == 0,20);
                Check("empty clears detail",view.selectedSong == null,"no stale song actions");
                AccessTools.Property(typeof(InputFieldView),"text").SetValue(BSSFlowCoordinator.songListView.songSearchInput,""); BSSFlowCoordinator.songListView.UpdateSearchedSongsList();
                await Wait(() => SongListController.searchedSongsList?.Count > 0 && view.selectedSong != null,20);
                phase = "sort preservation";
                string selected = view.selectedSong.hash;
                AccessTools.Property(typeof(SongListController),"selectedSortMode").SetValue(null,"Oldest Upload"); BSSFlowCoordinator.songListView.UpdateSearchedSongsList();
                await Task.Delay(1500);
                Check("sort keeps selection",view.selectedSong.hash == selected,view.selectedSong.hash);
                phase = "tabs and filters";
                BSSFlowCoordinator.detailView.ShowQueue(); await Task.Delay(200); Check("queue tab",BSSFlowCoordinator.detailView.QueueVisible,"same central region");
                var queue=BSSFlowCoordinator.downloadHistoryView;
                var originalEntries=queue.downloadList.ToArray();
                try {
                    var states=new[]{DownloadHistoryView.DownloadHistoryEntry.DownloadStatus.Loaded,DownloadHistoryView.DownloadHistoryEntry.DownloadStatus.Failed,DownloadHistoryView.DownloadHistoryEntry.DownloadStatus.Cancelled};
                    var fixtures=SongListController.searchedSongsList.Take(3).ToArray();
                    for(int i=0;i<fixtures.Length;i++) queue.downloadList.Add(new DownloadHistoryView.DownloadHistoryEntry(fixtures[i]) {status=states[i],failureCategory=i==1 ? "network" : ""});
                    queue.RefreshTable();await Task.Delay(300);
                    Check("queue stays open on status refresh",BSSFlowCoordinator.detailView.QueueVisible,"result-row refresh preserves current tab");
                    var queueCells=BSSFlowCoordinator.detailView.GetComponentsInChildren<BetterSongSearch.UI.CustomLists.CustomDownloadListTableCell>();
                    Check("queue rows visible",queueCells.Length>=3,"loaded, failed and cancelled have readable rows");
                    foreach(var cell in queueCells) {var text=(TMP_Text)AccessTools.Field(cell.GetType(),"songName").GetValue(cell);text.ForceMeshUpdate();Check("queue title visible",text.textInfo.characterInfo.Take(text.textInfo.characterCount).Any(x=>x.isVisible),text.text);}
                    Capture("queue.png");
                } finally {queue.downloadList.Clear();queue.downloadList.AddRange(originalEntries);queue.RefreshTable();}
                BSSFlowCoordinator.ShowDetails();
                AccessTools.Method(typeof(FilterView),"ToggleMapping").Invoke(BSSFlowCoordinator.filterView,null); await Task.Delay(300); Capture("expanded-filter.png"); SaveGeometry("expanded-geometry.json");
                var expandedLabels=BSSFlowCoordinator.filterView.GetComponentsInChildren<TMP_Text>().Where(x=>x.name=="Label" || x.name=="Title").ToArray();
                Check("advanced filter controls reachable",expandedLabels.Count(x=>x.name=="Title")>=6,"six mapping sliders in scroll content");
                foreach(var label in expandedLabels) {
                    var corners=new Vector3[4];label.rectTransform.GetWorldCorners(corners);var positions=corners.Select(filterRect.InverseTransformPoint).ToArray();
                    Check("expanded filter label within panel",positions.Min(p=>p.x)>=filterRect.rect.xMin+3 && positions.Max(p=>p.x)<=filterRect.rect.xMax-3,label.text+" x="+positions.Min(p=>p.x)+".."+positions.Max(p=>p.x));
                }
                AccessTools.Method(typeof(FilterView),"ToggleMapping").Invoke(BSSFlowCoordinator.filterView,null);
                var unicode = BSSFlowCoordinator.songsList.FirstOrDefault(x => x.detailsSong.songName.Any(c => c >= 0x4e00 && c <= 0x9fff));
                if(unicode != null) {
                    Check("Unicode search",WeightedSongSearch.Search(new[] {unicode},unicode.detailsSong.songName,x => 0).Any(),unicode.detailsSong.songName);
                }
                phase = "download state machine"; await DownloadChecks.RunAsync(Check);
                phase = "close and reopen with active download"; await DownloadChecks.CheckFlowLifecycleAsync(Check);
                view = BSSFlowCoordinator.detailView.selectedSongView;
                phase = "local dependency audit";
                var capabilities = SongCore.Collections.capabilities;
                Check("Mapping Extensions capability",capabilities.Contains("Mapping Extensions"),String.Join(",",capabilities));
                var loaded = (IEnumerable<KeyValuePair<string,BeatmapLevel>>)AccessTools.Field(typeof(SongCore.Loader),"CustomLevels").GetValue(null);
                var missing = new List<string>(); int audited = 0;
                foreach(var item in loaded) foreach(var key in item.Value.GetBeatmapKeys()) {
                    audited++;
                    var data = SongCore.Collections.GetCustomLevelSongDifficultyData(key);
                    if(data == null) missing.Add(key + ": missing difficulty data");
                    else foreach(string requirement in data.additionalDifficultyData._requirements) if(!capabilities.Contains(requirement)) missing.Add(key + ": " + requirement);
                }
                Check("all local requirements",audited > 0 && missing.Count == 0,audited + " difficulties; " + String.Join(",",missing));
                AccessTools.Property(typeof(SongListController),"selectedSortMode").SetValue(null,"Newest Upload"); BSSFlowCoordinator.songListView.UpdateSearchedSongsList();
                await Task.Delay(1000); Capture("final.png");
                phase="native play handoff";
                File.WriteAllText(Path.Combine(directory,"local-levels.json"),JsonConvert.SerializeObject(SongCore.Loader.CustomLevels.Values.Select(x=>new {id=x.levelID,keys=x.GetBeatmapKeys().Select(k=>new {mode=k.characteristic.SerializedName(),difficulty=k.difficulty.ToString()}).ToArray()}).ToArray(),Formatting.Indented));
                var preferredModes=new[]{SongDetailsCache.Structs.MapCharacteristic.OneSaber,SongDetailsCache.Structs.MapCharacteristic.NinetyDegree,SongDetailsCache.Structs.MapCharacteristic.ThreeSixtyDegree};
                var playable=BSSFlowCoordinator.songsList.Where(x=>x.CheckIsDownloadedAndLoaded())
                    .SelectMany(song=>song.diffs.Where(diff=>diff.detailsDiff.notes>0 && MatchingKey(song,diff).HasValue).Select(diff=>new {song,diff}))
                    .OrderByDescending(x=>preferredModes.Contains(x.diff.detailsDiff.characteristic))
                    .ThenByDescending(x=>x.diff.detailsDiff.characteristic!=SongDetailsCache.Structs.MapCharacteristic.Standard)
                    .ThenBy(x=>x.diff.detailsDiff.difficulty).FirstOrDefault();
                if(playable==null) throw new InvalidOperationException("No loaded searchable map with playable notes for play verification");
                var local=playable.song;
                view.SetSelectedSong(local);
                var chosen=playable.diff;
                Check("native gameplay fixture has notes",chosen.detailsDiff.notes>0,chosen.serializedDiff+"; notes="+chosen.detailsDiff.notes);
                view.selectedDiff=chosen;AccessTools.Method(typeof(SelectedSongView),"RefreshDifficulty").Invoke(view,null);
                string expectedLevel=local.GetCustomLevelIdString(),expectedMode=chosen.detailsDiff.characteristic.ToString().Replace("ThreeSixty","360").Replace("Ninety","90");
                var expectedDifficulty=(BeatmapDifficulty)chosen.detailsDiff.difficulty;
                gameplayDownload = new GameplayDownloadChecks(); gameplayDownload.Start();
                var play=(Button)AccessTools.Field(typeof(SelectedSongView),"playButton").GetValue(view); Check("BSS play enabled",play.interactable,chosen.serializedDiff); play.onClick.Invoke();
                await Wait(()=>Resources.FindObjectsOfTypeAll<StandardLevelDetailViewController>().Any(x=>x.isActivated && x.gameObject.activeInHierarchy && x.beatmapKey.levelId==expectedLevel && x.beatmapLevel?.levelID==expectedLevel),35);
                var native=Resources.FindObjectsOfTypeAll<StandardLevelDetailViewController>().First(x=>x.isActivated && x.gameObject.activeInHierarchy && x.beatmapKey.levelId==expectedLevel && x.beatmapLevel?.levelID==expectedLevel);
                Check("native selected mode and difficulty",native.beatmapKey.difficulty==expectedDifficulty && native.beatmapKey.characteristic.SerializedName()==expectedMode,native.beatmapKey.ToString()+"; expected "+chosen.serializedDiff);
                var nativeView=(StandardLevelDetailView)AccessTools.Field(typeof(StandardLevelDetailViewController),"_standardLevelDetailView").GetValue(native);
                await Wait(()=>nativeView.actionButton.interactable,20);
                await Task.Delay(1500);
                var soloFlow=Resources.FindObjectsOfTypeAll<SoloFreePlayFlowCoordinator>().First(x=>x.isActivated);
                var flowLevel=(BeatmapLevel)AccessTools.Property(typeof(LevelSelectionFlowCoordinator),"selectedBeatmapLevel").GetValue(soloFlow);
                bool selectionRetained=native.isActivated && native.gameObject.activeInHierarchy && native.beatmapLevel?.levelID==expectedLevel && flowLevel?.levelID==expectedLevel;
                Check("native play selection retains real level",selectionRetained,"expected="+expectedLevel+"; detail="+native.beatmapLevel?.levelID+"; flow="+flowLevel?.levelID);
                if(!selectionRetained) throw new InvalidOperationException("Native play selection was cleared before gameplay");
                Check("native play and practice",nativeView.actionButton.interactable && nativeView.practiceButton.interactable,"SongCore requirement gate passed"); Capture("play-menu.png");
                nativeView.actionButton.onClick.Invoke(); phase="gameplay startup";
                await Wait(()=>UnityEngine.SceneManagement.SceneManager.GetSceneByName("GameCore").isLoaded,50); await Task.Delay(5000);
                Check("selected map enters gameplay",UnityEngine.SceneManagement.SceneManager.GetSceneByName("GameCore").isLoaded,"actual native play action completed"); Capture("gameplay.png");
                phase = "background remote download in gameplay";
                await gameplayDownload.VerifyAsync(Check);
                phase = "menu return and deferred song load";
                var pause = Resources.FindObjectsOfTypeAll<PauseController>().First(x => x.gameObject.activeInHierarchy);
                pause.Pause(); await Task.Delay(1500);
                var pauseMenu = Resources.FindObjectsOfTypeAll<PauseMenuManager>().First(x => x.gameObject.activeInHierarchy);
                ((Button)AccessTools.Field(typeof(PauseMenuManager), "_backButton").GetValue(pauseMenu)).onClick.Invoke();
                await Wait(() => DownloadHistoryView.IsMenu,40);
                await Wait(() => gameplayDownload.Entry.status == DownloadHistoryView.DownloadHistoryEntry.DownloadStatus.Loaded && SongCore.Collections.songWithHashPresent(gameplayDownload.Entry.hash),40);
                Check("completed background map registered on menu return",true,gameplayDownload.Entry.hash);
                await gameplayDownload.CleanupAsync(); gameplayDownload = null;
            } catch(Exception ex) { try { if(BSSFlowCoordinator.detailView != null) { Capture("failure.png"); SaveGeometry(); } } catch(Exception capture) { errors.Add("Failure capture: " + capture); } errors.Add(phase + ": " + ex); }
            finally { if(gameplayDownload != null) try { await gameplayDownload.CleanupAsync(); } catch(Exception ex) { errors.Add("Gameplay download cleanup: " + ex); } }
            Finish(null);
        }
        void SaveGeometry(string name="geometry.json") {
            var roots = new[] {BSSFlowCoordinator.filterView.gameObject,BSSFlowCoordinator.detailView.gameObject,BSSFlowCoordinator.songListView.gameObject};
            var values = roots.Select(root => new {root=root.name,rect=((RectTransform)root.transform).rect.ToString(),texts=root.GetComponentsInChildren<TMP_Text>(true).Select(t => new {name=t.name,text=t.text,active=t.gameObject.activeInHierarchy,size=t.fontSize,font=t.font?.name,rect=t.rectTransform.rect.ToString(),position=root.transform.InverseTransformPoint(t.transform.position).ToString(),parent=t.transform.parent.name,overflow=t.isTextOverflowing}).ToArray()}).ToArray();
            File.WriteAllText(Path.Combine(directory,name),JsonConvert.SerializeObject(values,Formatting.Indented));
        }
        BeatmapKey? MatchingKey(SongSearchSong song,SongSearchSong.SongSearchDiff diff) {
            var level=song.GetLoadedLevel();
            if(level==null) return null;
            string mode=diff.detailsDiff.characteristic.ToString().Replace("ThreeSixty","360").Replace("Ninety","90");
            var key=level.GetBeatmapKeys().FirstOrDefault(x=>x.characteristic.SerializedName()==mode && x.difficulty==(BeatmapDifficulty)diff.detailsDiff.difficulty);
            return String.IsNullOrEmpty(key.levelId) ? (BeatmapKey?)null : key;
        }
        void Capture(string name,bool wide=true) {
            var camera = Resources.FindObjectsOfTypeAll<Camera>().FirstOrDefault(x => x.name == "MenuMainCamera" && x.gameObject.activeInHierarchy) ?? Camera.main;
            if(camera == null) throw new InvalidOperationException("Menu camera absent");
            var previous = camera.targetTexture; var active = RenderTexture.active;
            var position=camera.transform.position; float fov=camera.fieldOfView;
            var avatarRenderers=Resources.FindObjectsOfTypeAll<Renderer>().Where(x=>x.enabled && x.GetComponentsInParent<MonoBehaviour>(true).Any(p=>p!=null && p.GetType().Assembly.GetName().Name=="CustomAvatar")).ToArray();
            var target = new RenderTexture(2560,1440,24,RenderTextureFormat.ARGB32); var texture = new Texture2D(2560,1440,TextureFormat.RGBA32,false);
            try {
                foreach(var renderer in avatarRenderers) renderer.enabled=false;
                if(wide) { camera.transform.position=position-camera.transform.forward*1.5f; camera.fieldOfView=100; }
                target.Create(); camera.targetTexture = target;
                var request = new RenderPipeline.StandardRequest {destination=target};
                RenderPipeline.SubmitRenderRequest(camera,request);
                RenderTexture.active = target; texture.ReadPixels(new Rect(0,0,2560,1440),0,0); texture.Apply();
                var pixels = texture.GetPixels32(); for(int i=0;i<pixels.Length;i++) pixels[i].a=255; texture.SetPixels32(pixels); texture.Apply();
                File.WriteAllBytes(Path.Combine(directory,name),texture.EncodeToPNG());
            } finally {foreach(var renderer in avatarRenderers) if(renderer!=null) renderer.enabled=true;camera.transform.position=position;camera.fieldOfView=fov;camera.targetTexture=previous;RenderTexture.active=active;target.Release();Destroy(target);Destroy(texture);}
        }
        void Finish(string failure) {
            if(finished) return; finished=true;
            if(failure != null) errors.Add(failure);
            File.WriteAllText(Path.Combine(directory,"report.json"),JsonConvert.SerializeObject(new {passed=errors.Count == 0,elapsed=timer.Elapsed.TotalSeconds,xrActive=UnityEngine.XR.XRSettings.isDeviceActive,playlistLibrary=AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(x=>x.GetName().Name=="BeatSaberPlaylistsLib")?.Location,phase,checks,errors},Formatting.Indented));
            Application.Quit(errors.Count == 0 ? 0 : 1);
        }
    }
}
