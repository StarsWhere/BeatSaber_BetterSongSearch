using IPA.Utilities.Async;
using SongPackageIO;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using static BetterSongSearch.UI.DownloadHistoryView;

namespace BetterSongSearch.Util {
	static class SongDownloader {
		// A finite, injectable seam keeps queue tests independent of Unity's
		// networking stack. Production code leaves this null.
		internal static Func<DownloadHistoryEntry, CancellationToken, Action<float>, Task> DownloadOverride;

		internal sealed class FailureInfo {
			public readonly string Category;
			public readonly bool Transient;
			public readonly string UserMessage;

			public FailureInfo(string category, bool transient, string userMessage) {
				Category = category;
				Transient = transient;
				UserMessage = userMessage;
			}
		}

		internal sealed class SongDownloadException : Exception {
			internal readonly string Category;
			internal readonly bool Transient;
			internal readonly string UserMessage;

			internal SongDownloadException(string category, bool transient, string userMessage, Exception inner = null)
				: base(userMessage, inner) {
				Category = category;
				Transient = transient;
				UserMessage = userMessage;
			}
		}

		internal static FailureInfo ClassifyFailure(Exception exception) {
			if(exception is AggregateException aggregate && aggregate.InnerExceptions.Count == 1)
				exception = aggregate.InnerException;

			if(exception is SongDownloadException known)
				return new FailureInfo(known.Category, known.Transient, SafeMessage(known.Category));

			if(exception is InvalidSongPackageException)
				return new FailureInfo("invalid-archive", false, "Invalid map archive");

			if(exception is SongPackageIOException)
				return new FailureInfo("storage", false, "Cannot save map");

			if(exception is OperationCanceledException)
				return new FailureInfo("cancelled", false, "Cancelled");

			if(exception is FileNotFoundException)
				return new FailureInfo("not-found", false, "Map unavailable");

			if(exception is InvalidDataException)
				return new FailureInfo("invalid-archive", false, "Invalid map archive");

			if(exception is TimeoutException || exception is WebException || exception is IOException)
				return new FailureInfo("network", true, "Network error");

			return new FailureInfo("unknown", false, "Download failed");
		}

		static string SafeMessage(string category) {
			switch(category) {
				case "network": return "Network error";
				case "not-found": return "Map unavailable";
				case "invalid-archive": return "Invalid map archive";
				case "storage": return "Cannot save map";
				case "cancelled": return "Cancelled";
				case "request": return "Download request rejected";
				default: return "Download failed";
			}
		}

		public static async Task BeatmapDownload(DownloadHistoryEntry entry, CancellationToken token, Action<float> progressCb) {
			if(DownloadOverride != null) {
				entry.status = DownloadHistoryEntry.DownloadStatus.Downloading;
				entry.statusDetails = "(0%)";
				progressCb?.Invoke(0f);
				await DownloadOverride(entry, token, progressCb);
				return;
			}

			var folderName = $"{entry.key} ({entry.songName} - {entry.levelAuthorName})";

			var baseUrl = PluginConfig.Instance?.downloadUrlOverride;
			if(string.IsNullOrEmpty(baseUrl))
				baseUrl = entry.retries <= 1 ? BeatSaverRegionManager.mapDownloadUrl : BeatSaverRegionManager.mapDownloadUrlFallback;

			entry.status = DownloadHistoryEntry.DownloadStatus.Downloading;
			entry.statusDetails = "(0%)";
			progressCb?.Invoke(0f);
			var dl = await UnityWebrequestWrapper.DownloadBytes($"{baseUrl}/{entry.hash}.zip".ToLowerInvariant(), token, progress => {
				if(token.IsCancellationRequested)
					return;
				entry.status = DownloadHistoryEntry.DownloadStatus.Downloading;
				progressCb?.Invoke(progress);
			});
			token.ThrowIfCancellationRequested();

			if(dl == null || dl.Length == 0)
				throw new SongDownloadException("network", true, "Network error");

			using(var extractionCancel = CancellationTokenSource.CreateLinkedTokenSource(token))
			using(var stream = new MemoryStream(dl)) {
				token.ThrowIfCancellationRequested();
				entry.status = DownloadHistoryEntry.DownloadStatus.Extracting;
				entry.statusDetails = "(0%)";
				progressCb?.Invoke(0f);

				var callbackContext = SynchronizationContext.Current;
				Action<float> reportProgress = progress => {
					if(callbackContext == null)
						progressCb?.Invoke(progress);
					else
						callbackContext.Post(_ => progressCb?.Invoke(progress), null);
				};

				try {
					var customLevelsRoot = Path.Combine(Directory.GetCurrentDirectory(), CustomLevelPathHelper.customLevelsDirectoryPath);
					var stagingRoot = Path.Combine(Directory.GetCurrentDirectory(), ".tmp", "better-song-search-song-import");
					await Task.Run(() => PackageImporter.Install(stream, customLevelsRoot, SanitizeFolderName(folderName), stagingRoot,
						extractionCancel.Token, reportProgress), extractionCancel.Token);
				} catch(InvalidSongPackageException) {
					throw;
				} catch(InvalidDataException) {
					throw;
				} catch(IOException exception) {
					throw new SongDownloadException("storage", false, "Cannot save map", exception);
				} catch(UnauthorizedAccessException exception) {
					throw new SongDownloadException("storage", false, "Cannot save map", exception);
				}
			}
		}

		static string SanitizeFolderName(string value) {
			var invalid = new HashSet<char>(Path.GetInvalidFileNameChars().Concat(Path.GetInvalidPathChars()));
			var sanitized = string.Concat(value.Where(character => !invalid.Contains(character))).Trim();
			if(string.IsNullOrEmpty(sanitized) || sanitized == "." || sanitized == "..")
				return "Beatmap";
			if(sanitized.Length > 180)
				sanitized = sanitized.Substring(0, 180).TrimEnd(' ', '.');
			if(string.IsNullOrEmpty(sanitized) || sanitized == "." || sanitized == "..")
				return "Beatmap";
			return sanitized;
		}
	}
}
