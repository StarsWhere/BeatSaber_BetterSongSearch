using BetterSongSearch.Configuration;
using BetterSongSearch.UI;
using BetterSongSearch.Util;
using SongDetailsCache.Structs;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;

namespace BetterSongSearchSmoke {
	internal static class PresentationChecks {
		public static void Run(Action<string, bool, string> check) {
			if(check == null)
				throw new ArgumentNullException(nameof(check));

			CheckNames(check);
			CheckScalarFormatting(check);
			CheckRankedStars(check);
			CheckFilteringAndStarSorting(check);
		}

		static void CheckNames(Action<string, bool, string> check) {
			try {
				var value = "\u4e2d\u6587\u65e5\u672c\u8a9e-" + new string('\u9577', 96);
				var actual = SongPresentation.Name(value);
				check("CJK Japanese long name", actual == value, "length=" + actual.Length + ", expected=" + value.Length);
			} catch(Exception exception) {
				check("CJK Japanese long name", false, exception.ToString());
			}
		}

		static void CheckScalarFormatting(Action<string, bool, string> check) {
			try {
				var zero = new Song();
				check("invalid duration", SongPresentation.Duration(0) == "—" && SongPresentation.Nps(42, 0, true) == "—",
					"duration=" + SongPresentation.Duration(0) + ", nps=" + SongPresentation.Nps(42, 0, true));
				check("zero votes", SongPresentation.Rating(in zero) == "—", SongPresentation.Rating(in zero));
			} catch(Exception exception) {
				check("presentation scalar formatting", false, exception.ToString());
			}
		}

		static IEnumerable<SongSearchSong.SongSearchDiff> AllDifficulties() {
			return (BSSFlowCoordinator.songsList ?? new SongSearchSong[0])
				.Where(song => song != null)
				.SelectMany(song => song.diffs ?? new SongSearchSong.SongSearchDiff[0]);
		}

		static void CheckRankedStars(Action<string, bool, string> check) {
			try {
				var dual = AllDifficulties().FirstOrDefault(diff =>
					diff.detailsDiff.stars > 0f && diff.detailsDiff.starsBeatleader > 0f);
				var noStars = AllDifficulties().FirstOrDefault(diff =>
					diff.detailsDiff.stars <= 0f && diff.detailsDiff.starsBeatleader <= 0f);

				if(dual == null) {
					check("dual leaderboard stars", false, "no difficulty with both ScoreSaber and BeatLeader stars was present");
				} else {
					var service = dual.GetTargetedRankLeaderboardService();
					var expectedPrefix = service == RankedStates.ScoresaberRanked ? "SS " : "BL ";
					var expectedStars = service == RankedStates.ScoresaberRanked ? dual.detailsDiff.stars : dual.detailsDiff.starsBeatleader;
					var actual = SongPresentation.Stars(dual);
					var expectedValue = expectedStars.ToString("0.0", CultureInfo.InvariantCulture);
					check("dual leaderboard stars", actual.StartsWith(expectedPrefix, StringComparison.Ordinal) && actual.Contains(expectedValue),
						"service=" + service + ", actual=" + actual + ", expected=" + expectedPrefix + expectedValue);
				}

				if(noStars == null)
					check("no-star difficulty", false, "no unranked difficulty was present");
				else
					check("no-star difficulty", SongPresentation.Stars(noStars) == "—", SongPresentation.Stars(noStars));
			} catch(Exception exception) {
				check("ranked star formatting", false, exception.ToString());
			}
		}

		static bool Passes(SongSearchSong.SongSearchDiff diff, FilterOptions filter) {
			if(BSSFlowCoordinator.filterView == null)
				throw new InvalidOperationException("FilterView is unavailable.");
			return BSSFlowCoordinator.filterView.DifficultyCheck(in diff.detailsDiff, filter) &&
				BSSFlowCoordinator.filterView.SearchDifficultyCheck(diff, filter);
		}

		static void SetProperty(object target, string name, object value) {
			var property = target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			var setter = property?.GetSetMethod(true);
			if(setter == null)
				throw new MissingMethodException(target.GetType().FullName, "set_" + name);
			setter.Invoke(target, new[] { value });
		}

		static void SetPasses(SongSearchSong probe, SongSearchSong source, FilterOptions filter) {
			for(var i = 0; i < probe.diffs.Length; i++)
				probe.diffs[i]._passesFilter = Passes(source.diffs[i], filter);
		}

		static PropertyInfo SortProperty() {
			var property = typeof(SongListController).GetProperty("selectedSortMode",
				BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
			if(property?.GetSetMethod(true) == null)
				throw new MissingMemberException(typeof(SongListController).FullName, "selectedSortMode");
			return property;
		}

		static SongSearchSong FindRepresentativeFixture(FilterOptions filter, out float threshold, out SongSearchSong.SongSearchDiff[] strictEligible) {
			threshold = 0f;
			strictEligible = null;
			foreach(var song in BSSFlowCoordinator.songsList ?? new SongSearchSong[0]) {
				if(song == null || song.diffs == null || song.diffs.Length < 2)
					continue;

				var stars = song.diffs
					.Select(diff => diff.GetStars(filter))
					.Where(value => value > 0f && !Single.IsNaN(value) && !Single.IsInfinity(value))
					.Distinct()
					.OrderBy(value => value)
					.ToArray();
				if(stars.Length < 2)
					continue;

				var strict = filter.Clone();
				threshold = (stars[0] + stars[stars.Length - 1]) / 2f;
				SetProperty(strict, "minimumStars", Math.Max(strict.minimumStars, threshold));
				strict.CalculateTagBitfields();
				strictEligible = song.diffs.Where(diff => Passes(diff, strict)).ToArray();
				if(strictEligible.Length > 0 && strictEligible.Length < song.diffs.Length)
					return song;
			}

			return null;
		}

		static void CheckFilteringAndStarSorting(Action<string, bool, string> check) {
			try {
				if(BSSFlowCoordinator.filterView == null || BSSFlowCoordinator.songsList == null) {
					check("filter representative", false, "live dataset or FilterView is unavailable");
					check("star-sorted representative", false, "live dataset or FilterView is unavailable");
					return;
				}

				var baseFilter = FilterView.currentFilter.Clone();
				baseFilter.CalculateTagBitfields();
				var candidate = FindRepresentativeFixture(baseFilter, out var threshold, out var strictEligible);
				if(candidate == null) {
					check("filter representative", false, "no multi-difficulty fixture has a selective star threshold");
					check("star-sorted representative", false, "no multi-difficulty fixture has distinct stars");
					return;
				}

				var strict = baseFilter.Clone();
				SetProperty(strict, "minimumStars", Math.Max(strict.minimumStars, threshold));
				strict.CalculateTagBitfields();
				var filteredProbe = new SongSearchSong(candidate);
				SetPasses(filteredProbe, candidate, strict);
				var originalSort = SortProperty().GetValue(null, null);
				try {
					SortProperty().SetValue(null, "Newest Upload", null);
					var representative = filteredProbe.GetFirstPassingDifficulty();
					var accepted = strictEligible.Select(diff => diff.serializedDiff).ToHashSet();
					var expectedLabel = representative == null ? "" :
						SongPresentation.Mode(representative.detailsDiff.characteristic) + " · " +
						SongPresentation.Difficulty(representative.detailsDiff.difficulty);
					var summary = System.Text.RegularExpressions.Regex.Replace(SongPresentation.DiffSummary(filteredProbe),"<[^>]+>","");
					check("filter representative", representative != null && accepted.Contains(representative.serializedDiff) &&
						summary.StartsWith(expectedLabel, StringComparison.Ordinal),
						"threshold=" + threshold.ToString("0.0", CultureInfo.InvariantCulture) + ", representative=" + representative?.serializedDiff + ", summary=" + summary);
				} finally {
					SortProperty().SetValue(null, originalSort, null);
				}

				var sortedProbe = new SongSearchSong(candidate);
				SetPasses(sortedProbe, candidate, baseFilter);
				var passing = sortedProbe.diffs.Where(diff => diff._passesFilter == true).ToArray();
				var expectedStars = passing.Length == 0 ? 0f : passing.Max(diff => diff.GetStars());
				try {
					SortProperty().SetValue(null, "Most Stars", null);
					var representative = sortedProbe.GetFirstPassingDifficulty();
					var actualStars = representative == null ? 0f : representative.GetStars();
					check("star-sorted representative", representative != null && Math.Abs(actualStars - expectedStars) < 0.001f,
						"expected=" + expectedStars.ToString("0.0", CultureInfo.InvariantCulture) + ", actual=" + actualStars.ToString("0.0", CultureInfo.InvariantCulture));
				} finally {
					SortProperty().SetValue(null, originalSort, null);
				}
			} catch(Exception exception) {
				check("filter and star representative", false, exception.ToString());
			}
		}

	}
}
