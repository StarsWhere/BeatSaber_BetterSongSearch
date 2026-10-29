using BeatSaberMarkupLanguage;
using BeatSaberMarkupLanguage.Attributes;
using BeatSaberMarkupLanguage.Components;
using BeatSaberMarkupLanguage.Components.Settings;
using BeatSaberMarkupLanguage.Parser;
using BeatSaberMarkupLanguage.ViewControllers;
using BetterSongSearch.Configuration;
using BetterSongSearch.Util;
using HarmonyLib;
using HMUI;
using SongDetailsCache.Structs;
using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace BetterSongSearch.UI {

	[HotReload(RelativePathToLayout = @"Views\FilterView.bsml")]
	[ViewDefinition("BetterSongSearch.UI.Views.FilterView.bsml")]
	class FilterView : BSMLAutomaticViewController, INotifyPropertyChanged {
		public static List<DateTime> hideOlderThanOptions { get; private set; } = BuildList();

		static List<DateTime> BuildList() {
			var hideOlderThanOptions = new List<DateTime>();

			for(var x = new DateTime(2018, 5, 1); x < DateTime.Now; x = x.AddMonths(1))
				hideOlderThanOptions.Add(x);

			return hideOlderThanOptions;
		}

		public static readonly FilterOptions currentFilter = new FilterOptions();

        [UIComponent("starsGroup")] Transform starsGroup;
        [UIComponent("mappingGroup")] Transform mappingGroup;
        [UIComponent("ratingGroup")] Transform ratingGroup;
        [UIComponent("uploaderGroup")] Transform uploaderGroup;
        [UIComponent("genreGroup")] Transform genreGroup;
        [UIComponent("modsGroup")] Transform modsGroup;
        [UIComponent("commonFilters")] Transform commonFilters;
        [UIComponent("advancedFilters")] Transform advancedFilters;
        [UIValue("starsLabel")] string starsLabel => GroupLabel("Stars and Ranking", starsGroup, (currentFilter.rankedState != "Show all" ? 1 : 0) + (currentFilter.minimumStars > 0 ? 1 : 0) + (currentFilter.maximumStars < float.MaxValue ? 1 : 0));
        [UIValue("mappingLabel")] string mappingLabel => GroupLabel("Mapping and Length", mappingGroup, (currentFilter.minimumNps > 0 ? 1 : 0) + (currentFilter.maximumNps < float.MaxValue ? 1 : 0) + (currentFilter.minimumNjs > 0 ? 1 : 0) + (currentFilter.maximumNjs < float.MaxValue ? 1 : 0) + (currentFilter.minimumSongLength > 0 ? 1 : 0) + (currentFilter.maximumSongLength < float.MaxValue ? 1 : 0));
        [UIValue("ratingLabel")] string ratingLabel => GroupLabel("Rating and Date", ratingGroup, (currentFilter.minimumRating > 0 ? 1 : 0) + (currentFilter.minimumVotes > 0 ? 1 : 0) + (currentFilter.onlyCuratedMaps ? 1 : 0) + (currentFilter.onlyVerifiedMappers ? 1 : 0) + (currentFilter._hideOlderThan > 0 ? 1 : 0));
        [UIValue("uploaderLabel")] string uploaderLabel => GroupLabel("Uploaders", uploaderGroup, currentFilter.uploaders.Count > 0 ? 1 : 0);
        [UIValue("genreLabel")] string genreLabel => GroupLabel("Style and Genre", genreGroup, (String.IsNullOrEmpty(currentFilter.mapStyleString) || currentFilter.mapStyleString == "Any" ? 0 : 1) + (String.IsNullOrEmpty(currentFilter.mapGenreString) ? 0 : 1) + (String.IsNullOrEmpty(currentFilter.mapGenreExcludeString) ? 0 : 1));
        [UIValue("modsLabel")] string modsLabel => GroupLabel("Mods", modsGroup, currentFilter.mods != "Any" ? 1 : 0);
        static string GroupLabel(string name, Transform group, int count) => (group != null && group.gameObject.activeSelf ? "- " : "+ ") + BssText.T(name) + (count == 0 ? "" : " (" + count + ")");
        void Toggle(Transform group) { group.gameObject.SetActive(!group.gameObject.activeSelf); RefreshGroupLabels(); StartCoroutine(FinalizeLayout()); }
        [UIAction("ToggleStars")] void ToggleStars() => Toggle(starsGroup);
        [UIAction("ToggleMapping")] void ToggleMapping() => Toggle(mappingGroup);
        [UIAction("ToggleRating")] void ToggleRating() => Toggle(ratingGroup);
        [UIAction("ToggleUploaders")] void ToggleUploaders() => Toggle(uploaderGroup);
        [UIAction("ToggleGenre")] void ToggleGenre() => Toggle(genreGroup);
        [UIAction("ToggleMods")] void ToggleMods() => Toggle(modsGroup);
        internal void RefreshGroupLabels() {
            foreach(string name in new[] {"starsLabel","mappingLabel","ratingLabel","uploaderLabel","genreLabel","modsLabel"}) NotifyPropertyChanged(name);
        }
        [UIAction("#post-parse")] void Parsed() {
            currentFilter.hideOlderThanSlider.Slider.maxValue = hideOlderThanOptions.Count - 1;
            BSMLStuff.InitializeScrollViews(gameObject);
            BssUi.Settings(gameObject,104);
            StartCoroutine(FinalizeLayout());
            foreach(var text in GetComponentsInChildren<TMP_Text>(true)) if(text.name == "Title" || text.fontSize > 3.3f) text.fontSize = 2.6f;
        }
        IEnumerator FinalizeLayout() {
            yield return null; BssUi.Settings(gameObject,104);
            foreach(var setting in commonFilters.GetComponentsInChildren<DropDownListSetting>(true)) BssUi.Setting(setting,48,16);
            foreach(var button in advancedFilters.GetComponentsInChildren<Button>(true)) BssUi.Button(button,48,8);
            StyleAdvancedSliderLabels();
        }
        void StyleAdvancedSliderLabels() {
            foreach(var group in new[] {starsGroup, mappingGroup, ratingGroup, uploaderGroup, genreGroup, modsGroup}) {
                if(group == null)
                    continue;
                foreach(var title in group.GetComponentsInChildren<TMP_Text>(true).Where(text => text.name == "Title")) {
                    title.enableAutoSizing = false;
                    title.fontSize = 2.6f;
                    title.alignment = TextAlignmentOptions.MidlineLeft;
                }
            }
        }
		internal void ClearFilters() => SetFilter();

		internal void SetFilter(FilterOptions filter = null) {
			filter ??= new FilterOptions();
			foreach(var x in AccessTools.GetDeclaredProperties(typeof(FilterOptions))) {
				if(!x.CanWrite || x.Name[0] == '_')
					continue;

				x.SetValue(currentFilter, x.GetValue(filter));
			}

			
			currentFilter.NotifyPropertiesChanged();
            RefreshGroupLabels(); BSSFlowCoordinator.FilterSongs();
		}

		BSMLParserParams presetsViewParams = null;
		[UIAction("ShowPresets")] void ShowPresets() {
			BSMLStuff.InitSplitView(ref presetsViewParams, gameObject, SplitViews.Presets.instance).EmitEvent("OpenPresets");

			SplitViews.Presets.instance.ReloadPresets();
		}


		BSMLParserParams genreViewParams = null;
		
		internal void ShowGenrePicker() {
			BSMLStuff.InitSplitView(ref genreViewParams, gameObject, SplitViews.GenrePicker.instance).EmitEvent("OpenGenreModal");

			SplitViews.GenrePicker.instance.Reload();
		}

		internal void SetGenreFilter(List<string> includedGenres, List<string> excludedGenres) {
			currentFilter.mapGenreString = includedGenres == null ? "" : string.Join(",", includedGenres);
			currentFilter.mapGenreExcludeString = excludedGenres == null ? "" : string.Join(",", excludedGenres);

			var genrefilter = "Any";

			if(includedGenres?.Count > 0 || excludedGenres?.Count > 0)
				genrefilter = $"{includedGenres?.Count ?? 0} Incl., {excludedGenres?.Count ?? 0} Excl.";

			currentFilter.genrePickButton.GetComponentInChildren<CurvedTextMeshPro>().text = genrefilter;

			FilterOptions.UpdateData();
		}


		#region filters
		

		static readonly IReadOnlyDictionary<object, MapMods> funnyMapThing = Enumerable.Range(0, 4)
			.ToDictionary(x => FilterOptions.modOptions[x + 1], x => (MapMods)(1 << x));
        internal static string FormatMods(MapMods mods) => mods == 0 ? BssText.T("No required mods") : String.Join(" · ",funnyMapThing.Where(x => (mods & x.Value) != 0).Select(x => BssText.T((string)x.Key)));

		static readonly IReadOnlyDictionary<string, RankedStates> funnyMapThing2 = Enumerable.Range(0, 4)
			.ToDictionary(x => (string)FilterOptions.rankedFilterOptions[x + 1], x => (RankedStates)(1 << x));

		public bool DifficultyCheck(in SongDifficulty diff, FilterOptions filter = null) { filter ??= currentFilter;
			if(filter.difficulty_int != -1 && filter.difficulty_int != (int)diff.difficulty)
				return false;

			if(filter.characteristic_int != -1 && filter.characteristic_int != (int)diff.characteristic)
				return false;

			if(diff.njs < filter.minimumNjs || diff.njs > filter.maximumNjs)
				return false;

			if(filter.rankedState != (string)FilterOptions.rankedFilterOptions[0]) {
				var state = funnyMapThing2[filter.rankedState];

				if(state == RankedStates.ScoresaberRanked && diff.stars == 0)
					return false;

				if(state == RankedStates.BeatleaderRanked && diff.starsBeatleader == 0)
					return false;
			}

			if(filter.mods != (string)FilterOptions.modOptions[0]) {
				if((diff.mods & funnyMapThing[filter.mods]) == 0)
					return false;
			}

			if(diff.song.songDurationSeconds == 0 && (filter.minimumNps > 0 || filter.maximumNps < float.MaxValue)) return false;
			if(diff.song.songDurationSeconds > 0) {
				var nps = diff.notes / (float)diff.song.songDurationSeconds;

				if(nps < filter.minimumNps || nps > filter.maximumNps)
					return false;
			}

			return true;
		}

		public bool SearchDifficultyCheck(SongSearchSong.SongSearchDiff diff, FilterOptions filter = null) { filter ??= currentFilter; bool requiresScore = filter.existingScore == (string)FilterOptions.scoreFilterOptions[2];
			if(filter.existingScore != (string)FilterOptions.scoreFilterOptions[0] || requiresScore) {
				if(diff.CheckHasScore() != requiresScore)
					return false;
			}

			var star = -1f;

			if(filter.maximumStars != FilterOptions.STAR_FILTER_MAX) {
				star = diff.GetStars(filter);

				if(star > filter.maximumStars)
					return false;
			}

			if(filter.minimumStars != 0f) {
				if(star == -1)
					star = diff.GetStars(filter);

				if(star < filter.minimumStars)
					return false;
			}

			return true;
		}

		public bool SongCheck(in Song song, FilterOptions filter = null) { filter ??= currentFilter;
			if(song.uploadTime < filter.hideOlderThan)
				return false;

			if(filter.rankedState != (string)FilterOptions.rankedFilterOptions[0]) {
				if(!song.rankedStates.HasFlag(funnyMapThing2[filter.rankedState]))
					return false;
			}

			const float oneSixtythInverse = 1f / 60;

			if(song.songDurationSeconds == 0 && (filter.minimumSongLength > 0 || filter.maximumSongLength < float.MaxValue)) return false;
			if(song.songDurationSeconds > 0f) {
				var x = song.songDurationSeconds * oneSixtythInverse;

				if(x < filter.minimumSongLength || x > filter.maximumSongLength)
					return false;
			}

			var voteCount = song.downvotes + song.upvotes;

			if(voteCount < filter.minimumVotes)
				return false;

			if(filter.minimumRating > 0f && (filter.minimumRating > song.rating || voteCount == 0))
				return false;

			if(filter.onlyCuratedMaps && (song.uploadFlags & UploadFlags.Curated) == 0)
				return false;

			if(filter.onlyVerifiedMappers && (song.uploadFlags & UploadFlags.VerifiedUploader) == 0)
				return false;

			if(filter._mapStyleBitfield != 0 && (song.tags & filter._mapStyleBitfield) == 0)
				return false;

			if(filter._mapGenreBitfield != 0 && (song.tags & filter._mapGenreBitfield) == 0)
				return false;

			if((song.tags & filter._mapGenreExcludeBitfield) != 0)
				return false;

			return true;
		}

		public bool SearchSongCheck(SongSearchSong song, FilterOptions filter = null) { filter ??= currentFilter; bool requiresScore = filter.existingScore == (string)FilterOptions.scoreFilterOptions[2];
			if(filter.existingSongs != (string)FilterOptions.downloadedFilterOptions[0]) {
				if(SongCore.Collections.songWithHashPresent(song.hash) == (filter.existingSongs == (string)FilterOptions.downloadedFilterOptions[2]))
					return false;
			}

			if(filter.existingScore != (string)FilterOptions.scoreFilterOptions[0] || requiresScore) {
				if(song.CheckHasScore() != requiresScore)
					return false;
			}

			if(filter.uploaders.Count != 0) {
				if(filter.uploaders.Contains(song.uploaderNameLowercase)) {
					if(filter.uploadersBlacklist)
						return false;
				} else if(!filter.uploadersBlacklist) {
					return false;
				}
			}

			return true;
		}
		#endregion


		public static readonly RatelimitCoroutine limitedUpdateData = new RatelimitCoroutine(BSSFlowCoordinator.FilterSongs, 0.1f);

		
		[UIComponent("datasetInfoLabel")] private TextMeshProUGUI _datasetInfoLabel = null;
		public TextMeshProUGUI datasetInfoLabel => _datasetInfoLabel;
	}
}
