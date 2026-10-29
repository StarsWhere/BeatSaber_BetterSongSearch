using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BetterSongSearch.Configuration;
using BetterSongSearch.UI;
using SongDetailsCache.Structs;
using static BetterSongSearch.UI.DownloadHistoryView;

namespace BetterSongSearch.Util {
	class SongSearchSong {

		public readonly Song detailsSong;

		string _hash = null;
		public string hash => _hash ??= detailsSong.hash;

		string _uploaderNameLowercase = null;
		public string uploaderNameLowercase => _uploaderNameLowercase ??= detailsSong.uploaderName.ToLowerInvariant();

		public readonly SongSearchDiff[] diffs;

		public IOrderedEnumerable<SongSearchDiff> sortedDiffs {
			get {
				// Matching Standard > Matching Non-Standard > Non-Matching Standard > Non-Matching Non-Standard
				var y = diffs.OrderByDescending(x =>
					(x.passesFilter ? 1 : -3) + (x.detailsDiff.characteristic == MapCharacteristic.Standard ? 1 : 0)
				);

				// If we are sorting by something that is on a diff-level, sort the diffy as well!
				if(SongListController.sortModesDiffSort.TryGetValue(SongListController.selectedSortMode, out var diffSorter))
					y = y.ThenBy(diffSorter);

				return y.ThenByDescending(x => x.songSearchSong.isRanked() ? 1 : 0);
			}
		}

		public bool CheckIsDownloadedAndLoaded() => SongCore.Collections.songWithHashPresent(detailsSong.hash);

		public bool CheckIsDownloaded() {
			return
				BSSFlowCoordinator.downloadHistoryView != null && BSSFlowCoordinator.downloadHistoryView.downloadList.Any(
					x => x.hash.Equals(hash,StringComparison.OrdinalIgnoreCase) &&
					x.status == DownloadHistoryEntry.DownloadStatus.Downloaded
				) || CheckIsDownloadedAndLoaded();
		}

		public bool CheckIsDownloadable() {
			var dlElem = BSSFlowCoordinator.downloadHistoryView.downloadList.FirstOrDefault(x => x.hash.Equals(hash,StringComparison.OrdinalIgnoreCase));

			return !CheckIsDownloaded() && (dlElem == null || dlElem.status == DownloadHistoryEntry.DownloadStatus.Failed || dlElem.status == DownloadHistoryEntry.DownloadStatus.Cancelled);
		}

		public bool CheckHasScore() => BSSFlowCoordinator.songsWithScores.ContainsKey(hash);

		public bool isQualified(RankedStates state = RankedStates.BeatleaderQualified | RankedStates.ScoresaberQualified) => (detailsSong.rankedStates & state) != 0;
		public bool isRanked(RankedStates state = RankedStates.BeatleaderRanked | RankedStates.ScoresaberRanked) => (detailsSong.rankedStates & state) != 0;

		public string GetCustomLevelIdString() => SongCore.Collections.levelIDsForHash(hash).FirstOrDefault() ?? CustomLevelLoader.kCustomLevelPrefixId + hash.ToUpperInvariant();
        internal BeatmapLevel GetLoadedLevel() {
            string id=GetCustomLevelIdString();
            return SongCore.Loader.CustomLevels.Values.FirstOrDefault(x=>x.levelID.Equals(id,StringComparison.OrdinalIgnoreCase));
        }
		public SongSearchDiff GetFirstPassingDifficulty() => sortedDiffs.FirstOrDefault(x => x.passesFilter);
		public SongSearchSong(SongSearchSong other) {
			detailsSong = other.detailsSong;
			diffs = other.diffs.Select(x => new SongSearchDiff(this,in x.detailsDiff)).ToArray();
		}
		public SongSearchSong(in Song song, SongDifficulty[] difficultySnapshot = null) {
			detailsSong = song;
			diffs = new SongSearchDiff[song.diffCount];

			// detailsSong.difficulties has an overhead of creating the ArraySegment - This doesnt 👍;
			for(var i = 0; i < diffs.Length; i++) {
				var diff = difficultySnapshot == null ? BSSFlowCoordinator.songDetails.difficulties[i + (int)song.diffOffset] : difficultySnapshot[i + (int)song.diffOffset];
				diffs[i] = new SongSearchDiff(this,in diff);
			}
		}

		public class SongSearchDiff {
			internal readonly SongSearchSong songSearchSong;
			internal readonly SongDifficulty detailsDiff;
			internal bool? _passesFilter = null;
			internal bool passesFilter => _passesFilter ??= BSSFlowCoordinator.filterView.DifficultyCheck(in detailsDiff) && BSSFlowCoordinator.filterView.SearchDifficultyCheck(this);

			internal string serializedDiff => $"{detailsDiff.characteristic}_{detailsDiff.difficulty}";

			public bool CheckHasScore() => songSearchSong.CheckHasScore() && BSSFlowCoordinator.songsWithScores[songSearchSong.hash].ContainsKey(serializedDiff);
			public RankedStates GetTargetedRankLeaderboardService(FilterOptions filter = null) {
				filter ??= FilterView.currentFilter;
				var rStates = detailsDiff.song.rankedStates;

				if(detailsDiff.stars > 0 && rStates.HasFlag(RankedStates.ScoresaberRanked) &&
					// Not Filtering by BeatLeader ranked
					filter.rankedState != (string)FilterOptions.rankedFilterOptions[2] &&
					(
						PluginConfig.Instance.preferredLeaderboard != "BeatLeader" ||
						!rStates.HasFlag(RankedStates.BeatleaderRanked) ||
						// Filtering by SS ranked
						filter.rankedState == (string)FilterOptions.rankedFilterOptions[1]
					)
				)
					return RankedStates.ScoresaberRanked;

				if(detailsDiff.starsBeatleader > 0 && rStates.HasFlag(RankedStates.BeatleaderRanked))
					return RankedStates.BeatleaderRanked;

				return RankedStates.Unranked;
			}

			public bool isRanked => detailsDiff.stars > 0f || detailsDiff.starsBeatleader > 0f;

			public float GetStars() => GetStars(GetTargetedRankLeaderboardService());
			public float GetStars(FilterOptions filter) => GetStars(GetTargetedRankLeaderboardService(filter));

			public float GetStars(RankedStates state) {
				if(state.HasFlag(RankedStates.ScoresaberRanked) && detailsDiff.stars > 0)
					return detailsDiff.stars;

				if(state.HasFlag(RankedStates.BeatleaderRanked))
					return detailsDiff.starsBeatleader;

				return 0;
			}

			public SongSearchDiff(SongSearchSong songSearchSong, in SongDifficulty diff) {
				this.detailsDiff = diff;
				this.songSearchSong = songSearchSong;
			}

		}
	}
}
