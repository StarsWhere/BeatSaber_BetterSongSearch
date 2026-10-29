using BetterSongSearch.UI;
using BetterSongSearch.UI.SplitViews;
using BetterSongSearch.Util;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace BetterSongSearchSmoke {
	internal static class PlaylistChecks {
		static readonly BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

		public static void Run(Action<string, bool, string> check) {
			if(check == null)
				throw new ArgumentNullException(nameof(check));

			var root = Path.Combine(Environment.GetEnvironmentVariable("BSS_UI_SMOKE_DIR"), "playlist", Guid.NewGuid().ToString("N"));
			try {
				var songs = FindFixtureSongs();
				if(songs.Length < 2) {
					check("playlist fixture", false, "two distinct filtered songs with matching difficulties are required");
					return;
				}

				var managerType = PlaylistCreation.LoadPlaylistManagerType();
				if(managerType == null) {
					check("playlist library", false, "BeatSaberPlaylistsLib.PlaylistManager is unavailable");
					return;
				}
				var difficultyType = managerType.Assembly.GetType("BeatSaberPlaylistsLib.Types.Difficulty", true);
				object Difficulty(string mode, string name) { var value = Activator.CreateInstance(difficultyType); SetProperty(value, "Characteristic", mode); SetProperty(value, "Name", name); return value; }
				var standard = Difficulty("Standard", "Normal");
				var same = Difficulty("standard", "normal");
				check("playlist difficulty equality", standard.Equals(same) && standard.GetHashCode() == same.GetHashCode() && !standard.Equals(Difficulty("Lawless", "ExpertPlus")), "same chart and case-insensitive hashes match; distinct mode and difficulty stay distinct");

				Directory.CreateDirectory(root);
				var manager = CreateManager(managerType, root);
				var playlist = PlaylistCreation.Invoke(manager, "CreatePlaylist", "BSSPlaylistChecks", "BSS Playlist Checks", "BetterSongSearch", "");
				if(playlist == null)
					throw new InvalidOperationException("test manager returned no playlist");

				SetProperty(playlist, "AllowDuplicates", true);
				var expectedDifficulties = MatchingDifficulties(songs[0]);
				int added;
				int addedAgain;
				try {
					added = PlaylistCreation.AddSongsToPlaylist(playlist, new[] { songs[0], songs[0], songs[1] }, 10, true);
					addedAgain = PlaylistCreation.AddSongsToPlaylist(playlist, new[] { songs[0], songs[1] }, 10, true);
				} finally {
					SetProperty(playlist, "AllowDuplicates", false);
				}

				var entries = PlaylistCreation.EnumeratePlaylistEntries(playlist).ToArray();
				var hashes = entries.Select(x => (string)GetProperty(x, "Hash")).Where(x => !String.IsNullOrEmpty(x)).ToArray();
				check("playlist songs", added == 2 && addedAgain == 0 && entries.Length == 2,
					$"first add={added}, repeated add={addedAgain}, entries={entries.Length}");
				check("playlist dedupe", hashes.Length == hashes.Distinct(StringComparer.OrdinalIgnoreCase).Count(), String.Join(",", hashes));

				var firstEntry = entries.FirstOrDefault(x => String.Equals((string)GetProperty(x, "Hash"), songs[0].hash, StringComparison.OrdinalIgnoreCase));
				check("playlist starts at first result", firstEntry != null, $"first result hash={songs[0].hash}");
				check("playlist matching difficulties", firstEntry != null && HasMatchingDifficulties(firstEntry, expectedDifficulties),
					firstEntry == null ? "first song was not added" : "actual=" + DescribeDifficulties(firstEntry) + "; expected=" + String.Join(",", expectedDifficulties) + "; current=" + String.Join(",", MatchingDifficulties(songs[0])));

				PlaylistCreation.Invoke(manager, "StorePlaylist", playlist);
				var files = Directory.GetFiles(root, "*", SearchOption.AllDirectories);
				check("playlist temporary store", files.Length == 1, String.Join(",", files.Select(Path.GetFileName)));

				var lookupArguments = new object[] { "BSSPlaylistChecks", null };
				var found = (bool)PlaylistCreation.InvokeWithArguments(manager, "TryGetPlaylist", lookupArguments);
				check("playlist lookup", found && ReferenceEquals(lookupArguments[1], playlist), found ? "loaded playlist reference differs" : "playlist was not found");
				PlaylistCreation.Invoke(manager, "RequestRefresh", "BetterSongSearch");
			} catch(Exception exception) {
				check("playlist checks", false, exception.ToString());
			} finally {
				try {
					if(Directory.Exists(root))
						Directory.Delete(root, true);
				} catch(Exception exception) {
					check("playlist test cleanup", false, exception.GetType().Name + ": " + exception.Message);
				}
			}
		}

		static SongSearchSong[] FindFixtureSongs() {
			IEnumerable<SongSearchSong> source = SongListController.searchedSongsList?.Cast<SongSearchSong>();
			if(source == null)
				source = BSSFlowCoordinator.songsList ?? Array.Empty<SongSearchSong>();

			var start = 0;
			try {
				start = Math.Max(0, BSSFlowCoordinator.songListView?.FirstVisibleRow ?? 0);
			} catch(Exception) { }

			return source.Skip(start).Where(x => x != null && x.diffs.Any(y => y.passesFilter))
				.GroupBy(x => x.hash, StringComparer.OrdinalIgnoreCase)
				.Select(x => x.First())
				.Take(2)
				.ToArray();
		}

		static object CreateManager(Type managerType, string root) {
			var assembly = managerType.Assembly;
			var handlerType = assembly.GetType("BeatSaberPlaylistsLib.Types.IPlaylistHandler", true);
			var legacyType = assembly.GetType("BeatSaberPlaylistsLib.Legacy.LegacyPlaylistHandler", true);
			var blistType = assembly.GetType("BeatSaberPlaylistsLib.Blist.BlistPlaylistHandler", true);
			var legacy = Activator.CreateInstance(legacyType);
			var blist = Activator.CreateInstance(blistType);
			var legacyExtensions = (string[])PlaylistCreation.Invoke(legacy, "GetSupportedExtensions");
			var blistExtensions = (string[])PlaylistCreation.Invoke(blist, "GetSupportedExtensions");
			if(legacyExtensions == null || legacyExtensions.Length == 0 || blistExtensions == null || blistExtensions.Length == 0)
				throw new InvalidOperationException("Playlist handlers exposed no supported extensions");
			var otherHandlers = Array.CreateInstance(handlerType, 1);
			otherHandlers.SetValue(blist, 0);

			var constructor = managerType.GetConstructors(flags).FirstOrDefault(x => {
				var parameters = x.GetParameters();
				return parameters.Length == 3 && parameters[0].ParameterType == typeof(string) &&
					parameters[1].ParameterType == handlerType && parameters[2].ParameterType == handlerType.MakeArrayType();
			});
			if(constructor == null)
				throw new MissingMethodException(managerType.FullName, "PlaylistManager(string, IPlaylistHandler, IPlaylistHandler[])");

			return constructor.Invoke(new[] { root, legacy, otherHandlers });
		}

		static object GetProperty(object target, string name) {
			var property = target?.GetType().GetProperty(name, flags);
			if(property == null)
				throw new MissingMemberException(target?.GetType().FullName, name);

			return property.GetValue(target);
		}

		static void SetProperty(object target, string name, object value) {
			var property = target?.GetType().GetProperty(name, flags);
			if(property == null || !property.CanWrite)
				throw new MissingMemberException(target?.GetType().FullName, name);

			property.SetValue(target, value);
		}

		static string[] MatchingDifficulties(SongSearchSong source) => source.diffs.Where(x => x.passesFilter)
			.Select(x => PlaylistCreation.PlaylistCharacteristicName(x.detailsDiff.characteristic) + "|" + PlaylistCreation.PlaylistDifficultyName(x.detailsDiff.difficulty)).ToArray();
		static bool HasMatchingDifficulties(object playlistSong, string[] difficulties) {
			var expected = new HashSet<string>(difficulties, StringComparer.OrdinalIgnoreCase);
			var values = GetProperty(playlistSong, "Difficulties") as IEnumerable;
			if(values == null)
				return expected.Count == 0;

			var actual = new HashSet<string>(values.Cast<object>().Select(x => ((string)GetProperty(x, "Characteristic")) + "|" + ((string)GetProperty(x, "Name"))), StringComparer.OrdinalIgnoreCase);
			return expected.SetEquals(actual);
		}

		static string DescribeDifficulties(object playlistSong) {
			var values = GetProperty(playlistSong, "Difficulties") as IEnumerable;
			return values == null ? "none" : String.Join(",", values.Cast<object>().Select(x => GetProperty(x, "Characteristic") + "/" + GetProperty(x, "Name")));
		}
	}
}
