using BeatSaberMarkupLanguage.Attributes;
using BeatSaberMarkupLanguage.Components.Settings;
using BeatSaberMarkupLanguage.Parser;
using BetterSongSearch.Util;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using TMPro;

namespace BetterSongSearch.UI.SplitViews {
	class PlaylistCreation {
		public static readonly PlaylistCreation instance = new PlaylistCreation();
		PlaylistCreation() { }

		[UIComponent("playlistSongsCountSlider")] readonly SliderSetting playlistSongsCountSlider = null;
		[UIComponent("playlistName")] readonly StringSetting playlistName = null;
		[UIComponent("resultText")] readonly TextMeshProUGUI resultText = null;

		[UIParams] readonly BSMLParserParams parserParams = null;
		string resultSourceKey;

		[UIAction("#post-parse")]
		void Parsed() {
			playlistName.ModalKeyboard.ModalView._animateParentCanvas = false;
		}

		internal static string nameToUseOnNextOpen = "h";
		static bool clearExisting = true;
		static bool hightlightDiffs = false;

		internal static Type LoadPlaylistManagerType() {
			var assembly = Assembly.Load("BeatSaberPlaylistsLib");
			return assembly.GetType("BeatSaberPlaylistsLib.PlaylistManager", true);
		}

		public void Open() {
			try { LoadPlaylistManagerType(); }
            catch(Exception ex) {
				resultSourceKey = "Playlist library unavailable";
				resultText.text = BssText.T(resultSourceKey); Plugin.Log.Warn("Playlist library: "+ex);
				parserParams.EmitEvent("ShowResultModal");
				return;
			}

			if(nameToUseOnNextOpen != null) {
				playlistName.Text = nameToUseOnNextOpen;
				nameToUseOnNextOpen = null;
			}

			parserParams.EmitEvent("ShowModal");
		}

		void ShowResult(string text) {
			resultSourceKey = null;
			resultText.text = text;
			parserParams.EmitEvent("CloseModal");
			parserParams.EmitEvent("ShowResultModal");
		}

		internal void RefreshLocalization() {
			if(!String.IsNullOrEmpty(resultSourceKey) && resultText != null)
				resultText.text = BssText.T(resultSourceKey);
		}

		static readonly IReadOnlyDictionary<SongDetailsCache.Structs.MapCharacteristic, string> songDetailsCharNames
			= Enum.GetValues(typeof(SongDetailsCache.Structs.MapCharacteristic))
			.Cast<SongDetailsCache.Structs.MapCharacteristic>()
			.ToDictionary(x => x, x => x.ToString());

		static readonly IReadOnlyDictionary<SongDetailsCache.Structs.MapDifficulty, string> songDetailsDiffNames
			= Enum.GetValues(typeof(SongDetailsCache.Structs.MapDifficulty))
			.Cast<SongDetailsCache.Structs.MapDifficulty>()
			.ToDictionary(x => x, x => x.ToString());

		sealed class MethodCandidate {
			internal MethodInfo Method;
			internal int Score;
			internal int OptionalParameters;
			internal bool Generic;
		}

		static readonly BindingFlags invocationFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

		static bool TryScoreParameter(ParameterInfo parameter, object argument, out int score) {
			var parameterType = parameter.ParameterType;
			if(parameterType.IsByRef)
				parameterType = parameterType.GetElementType();

			if(argument == null) {
				if(parameterType.IsValueType && Nullable.GetUnderlyingType(parameterType) == null) {
					score = 0;
					return false;
				}

				score = parameter.ParameterType.IsByRef ? 2 : 4;
				return true;
			}

			var argumentType = argument.GetType();
			if(parameterType == argumentType) {
				score = 0;
				return true;
			}

			if(parameterType.IsAssignableFrom(argumentType)) {
				score = 1;
				return true;
			}

			score = 0;
			return false;
		}

		internal static MethodInfo ResolveMethod(object target, string name, object[] arguments) {
			if(target == null)
				throw new ArgumentNullException(nameof(target));

			arguments ??= Array.Empty<object>();
			var candidates = new List<MethodCandidate>();
			foreach(var original in target.GetType().GetMethods(invocationFlags).Where(x => x.Name == name)) {
				var method = original;
				if(method.IsGenericMethodDefinition) {
					var genericArguments = method.GetGenericArguments();
					if(genericArguments.Length != 1)
						continue;

					try { method = method.MakeGenericMethod(typeof(string)); }
					catch(ArgumentException) { continue; }
				}

				var parameters = method.GetParameters();
				if(parameters.Length < arguments.Length)
					continue;

				var score = method.IsGenericMethod ? 10 : 0;
				var compatible = true;
				for(var i = 0; i < arguments.Length; i++) {
					if(!TryScoreParameter(parameters[i], arguments[i], out var parameterScore)) {
						compatible = false;
						break;
					}

					score += parameterScore;
				}

				if(!compatible)
					continue;

				var optionalParameters = 0;
				for(var i = arguments.Length; i < parameters.Length; i++) {
					if(!parameters[i].IsOptional && !parameters[i].HasDefaultValue) {
						compatible = false;
						break;
					}

					optionalParameters++;
				}

				if(compatible)
					candidates.Add(new MethodCandidate { Method = method, Score = score, OptionalParameters = optionalParameters, Generic = method.IsGenericMethod });
			}

			if(candidates.Count == 0)
				throw new MissingMethodException(target.GetType().FullName, name);

			var ordered = candidates.OrderBy(x => x.Score).ThenBy(x => x.OptionalParameters).ThenBy(x => x.Generic).ToArray();
			var best = ordered[0];
			if(ordered.Skip(1).Any(x => x.Score == best.Score && x.OptionalParameters == best.OptionalParameters && x.Generic == best.Generic))
				throw new AmbiguousMatchException($"Multiple compatible {name} overloads exist on {target.GetType().FullName}.");

			return best.Method;
		}

		static object GetOptionalDefault(ParameterInfo parameter) => parameter.HasDefaultValue ? parameter.DefaultValue : Type.Missing;

		internal static object InvokeWithArguments(object target, string name, object[] arguments) {
			arguments ??= Array.Empty<object>();
			var method = ResolveMethod(target, name, arguments);
			var parameters = method.GetParameters();
			var values = new object[parameters.Length];
			Array.Copy(arguments, values, arguments.Length);
			for(var i = arguments.Length; i < values.Length; i++)
				values[i] = GetOptionalDefault(parameters[i]);

			var result = method.Invoke(target, values);
			Array.Copy(values, arguments, Math.Min(values.Length, arguments.Length));
			return result;
		}

		internal static object Invoke(object target, string name, params object[] arguments) => InvokeWithArguments(target, name, arguments);

		static object PropertyValue(object target, string name) {
			var property = target?.GetType().GetProperty(name, invocationFlags);
			if(property == null)
				throw new MissingMemberException(target?.GetType().FullName, name);

			return property.GetValue(target);
		}

		static void SetPropertyValue(object target, string name, object value) {
			var property = target?.GetType().GetProperty(name, invocationFlags);
			if(property == null || !property.CanWrite)
				throw new MissingMemberException(target?.GetType().FullName, name);

			property.SetValue(target, value);
		}

		internal static string PlaylistCharacteristicName(SongDetailsCache.Structs.MapCharacteristic characteristic) {
			switch(characteristic) {
				case SongDetailsCache.Structs.MapCharacteristic.NinetyDegree: return "90Degree";
				case SongDetailsCache.Structs.MapCharacteristic.ThreeSixtyDegree: return "360Degree";
				default: return songDetailsCharNames[characteristic];
			}
		}

		internal static string PlaylistDifficultyName(SongDetailsCache.Structs.MapDifficulty difficulty) => songDetailsDiffNames[difficulty];

		/// <summary>
		/// Reads the generic IEnumerable&lt;IPlaylistSong&gt; implementation. The
		/// library's non-generic IEnumerable implementation intentionally throws.
		/// </summary>
		internal static IEnumerable<object> EnumeratePlaylistEntries(object playlist) {
			if(playlist == null)
				yield break;

			var enumerableType = playlist.GetType().GetInterfaces()
				.FirstOrDefault(x => x.IsGenericType && x.GetGenericTypeDefinition() == typeof(IEnumerable<>));
			var getEnumerator = enumerableType?.GetMethod("GetEnumerator") ?? playlist.GetType().GetMethods(invocationFlags)
				.FirstOrDefault(x => x.Name == "GetEnumerator" && typeof(IEnumerator).IsAssignableFrom(x.ReturnType));
			if(getEnumerator == null)
				throw new InvalidOperationException("Playlist library does not expose a generic song enumerator.");

			var enumerator = (IEnumerator)getEnumerator.Invoke(playlist, null);
			try {
				while(enumerator.MoveNext())
					yield return enumerator.Current;
			} finally {
				(enumerator as IDisposable)?.Dispose();
			}
		}

		internal static int AddSongsToPlaylist(object playlist, IEnumerable<SongSearchSong> songs, int maximumSongs, bool highlightDiffs) {
			if(playlist == null || songs == null || maximumSongs <= 0)
				return 0;

			var songsAlreadyInPlaylist = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach(var song in EnumeratePlaylistEntries(playlist)) {
				var hash = PropertyValue(song, "Hash") as string;
				if(!String.IsNullOrWhiteSpace(hash))
					songsAlreadyInPlaylist.Add(hash);
			}

			var addedSongs = 0;
			foreach(var song in songs) {
				if(addedSongs >= maximumSongs)
					break;
				if(song == null || String.IsNullOrWhiteSpace(song.hash) || songsAlreadyInPlaylist.Contains(song.hash))
					continue;

				var playlistSong = Invoke(playlist, "Add", song.hash, song.detailsSong.songName, song.detailsSong.key, song.detailsSong.levelAuthorName);
				if(playlistSong == null)
					continue;

				songsAlreadyInPlaylist.Add(song.hash);
				addedSongs++;
				if(!highlightDiffs)
					continue;

				foreach(var diff in song.diffs) {
					if(!diff.passesFilter)
						continue;

					Invoke(playlistSong, "AddDifficulty", PlaylistCharacteristicName(diff.detailsDiff.characteristic), PlaylistDifficultyName(diff.detailsDiff.difficulty));
				}
			}

			return addedSongs;
		}

		void CreatePlaylist() {
			var fName = string.Concat((playlistName.Text ?? "").Split(Path.GetInvalidFileNameChars())).Trim();

			if(fName.Length == 0) {
				ShowResult("Your Playlist name is invalid");
				return;
			}

			try {
				var managerType = LoadPlaylistManagerType();
				if(managerType == null) throw new InvalidOperationException("Playlist library unavailable");
				var defaultManagerProperty = managerType.GetProperty("DefaultManager", invocationFlags);
				if(defaultManagerProperty == null) throw new InvalidOperationException("Playlist manager contract unavailable");
				object manager = Invoke(defaultManagerProperty.GetValue(null), "CreateChildManager", "BetterSongSearch");

				var arguments = new object[] {fName, null};
				object plist;
				if(!(bool)InvokeWithArguments(manager, "TryGetPlaylist", arguments))
					plist = Invoke(manager, "CreatePlaylist", fName, playlistName.Text, "BetterSongSearch", "");
				else
					plist = arguments[1];

				if(plist == null)
					throw new InvalidOperationException("Playlist manager returned no playlist");

				if(clearExisting)
					Invoke(plist, "Clear");

				Invoke(plist, "SetCustomData", "BetterSongSearchFilter", FilterView.currentFilter.Serialize(Newtonsoft.Json.Formatting.None));
				Invoke(plist, "SetCustomData", "BetterSongSearchSearchTerm", BSSFlowCoordinator.songListView.songSearchInput.text);
				Invoke(plist, "SetCustomData", "BetterSongSearchSort", SongListController.selectedSortMode);

				SetPropertyValue(plist, "AllowDuplicates", true);
				int addedSongs;
				try {
					addedSongs = AddSongsToPlaylist(plist, SongListController.searchedSongsList.Skip(Math.Max(0,BSSFlowCoordinator.songListView.FirstVisibleRow)), (int)playlistSongsCountSlider.Value, hightlightDiffs);
				} finally {
					SetPropertyValue(plist, "AllowDuplicates", false);
				}

				Invoke(manager, "StorePlaylist", plist);
				Invoke(manager, "RequestRefresh", "BetterSongSearch");

				ShowResult($"Added <b><color=#CCC>{addedSongs}</color></b> Songs to Playlist <b><color=#CCC>{playlistName.Text}</color></b> (Contains {PropertyValue(plist, "Count")} now)");
			} catch(Exception ex) {
				ShowResult($"Playlist failed to Create: More details in log, {ex.GetType().Name}");
				Plugin.Log.Warn("Failed to create Playlist:");
				Plugin.Log.Warn(ex);
			}
		}
	}
}
