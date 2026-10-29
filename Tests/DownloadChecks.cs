using BetterSongSearch;
using BetterSongSearch.UI;
using BetterSongSearch.Util;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using UnityEngine;

namespace BetterSongSearchSmoke {
	internal static class DownloadChecks {
		static byte[] packageFixtureBytes;

		public static async Task RunAsync(Action<string, bool, string> check) {
			if(check == null)
				throw new ArgumentNullException(nameof(check));

			var originalOwner = BSSFlowCoordinator.downloadHistoryView;
			var originalOverride = SongDownloader.DownloadOverride;
			var originalCloseCancelSource = BSSFlowCoordinator.closeCancelSource;
			GameObject ownerObject = null;
			DownloadHistoryView owner = null;
			CancellationTokenSource testCloseCancelSource = null;

			try {
				var song = FindFixtureSong();
				if(song == null) {
					check("download fixture", false, "no downloadable song is available");
					return;
				}

				ownerObject = new GameObject("BetterSongSearch.DownloadChecks");
				owner = ownerObject.AddComponent<DownloadHistoryView>();
				testCloseCancelSource = new CancellationTokenSource();
				BSSFlowCoordinator.closeCancelSource = testCloseCancelSource;

				await CheckMaximumParallelism(owner, song, check);
				await CheckHiddenQueue(owner, song, check);
				await CheckTransientRetries(owner, song, check);
				await CheckCancellation(owner, song, check);
				await CheckManualRetry(owner, song, check);
				await CheckProductionImport(song, check);
				await CheckHttpFailureRetry(owner, song, check);
				await CheckPermanentNotFound(owner, song, check);
			} catch(Exception exception) {
				check("download checks", false, "test helper failed: " + exception);
			} finally {
				try {
					if(owner != null) {
						owner.CancelPending();
						await owner.DrainAsync();
					}
				} catch(Exception exception) {
					check("download cleanup", false, "cleanup failed: " + exception);
				}

				SongDownloader.DownloadOverride = originalOverride;
				BSSFlowCoordinator.downloadHistoryView = originalOwner;
				BSSFlowCoordinator.closeCancelSource = originalCloseCancelSource;

				if(testCloseCancelSource != null) {
					testCloseCancelSource.Cancel();
					testCloseCancelSource.Dispose();
				}

				if(ownerObject != null)
					UnityEngine.Object.Destroy(ownerObject);
			}
		}

		static SongSearchSong FindFixtureSong() {
			if(BSSFlowCoordinator.songsList == null)
				return null;

			foreach(var song in BSSFlowCoordinator.songsList) {
				if(song != null && song.CheckIsDownloadable())
					return song;
			}

			return null;
		}

		static void Reset(DownloadHistoryView owner, bool active = true) {
			if(owner.gameObject.activeSelf != active)
				owner.gameObject.SetActive(active);
			owner.downloadList.Clear();
		}

		static DownloadHistoryView.DownloadHistoryEntry Add(DownloadHistoryView owner, SongSearchSong song) {
			var entry = new DownloadHistoryView.DownloadHistoryEntry(song);
			owner.downloadList.Add(entry);
			return entry;
		}

		static async Task Drain(DownloadHistoryView owner) {
			owner.ProcessDownloads(true);
			await owner.DrainAsync();
		}

		static async Task CheckMaximumParallelism(DownloadHistoryView owner, SongSearchSong song, Action<string, bool, string> check) {
			Reset(owner);
			var inFlight = 0;
			var maximum = 0;
			var entries = new[] { Add(owner, song), Add(owner, song), Add(owner, song) };

			SongDownloader.DownloadOverride = async (entry, token, progress) => {
				var now = Interlocked.Increment(ref inFlight);
				while(true) {
					var previous = Volatile.Read(ref maximum);
					if(now <= previous || Interlocked.CompareExchange(ref maximum, now, previous) == previous)
						break;
				}

				entry.status = DownloadHistoryView.DownloadHistoryEntry.DownloadStatus.Downloading;
				progress(.25f);
				try {
					await Task.Delay(30, token);
					progress(.75f);
				} finally {
					Interlocked.Decrement(ref inFlight);
				}
			};

			await Drain(owner);
			check("download max parallel", maximum <= 2, $"maximum concurrent downloads: {maximum}");
			check("download completes", entries.All(x => x.status == DownloadHistoryView.DownloadHistoryEntry.DownloadStatus.Downloaded), "all parallel entries completed");
		}

		static async Task CheckHiddenQueue(DownloadHistoryView owner, SongSearchSong song, Action<string, bool, string> check) {
			Reset(owner, false);
			var entry = Add(owner, song);
			var called = false;
			SongDownloader.DownloadOverride = async (item, token, progress) => {
				called = true;
				item.status = DownloadHistoryView.DownloadHistoryEntry.DownloadStatus.Downloading;
				progress(.4f);
				await Task.Delay(10, token);
			};

			await Drain(owner);
			check("hidden queue continues", called && entry.status == DownloadHistoryView.DownloadHistoryEntry.DownloadStatus.Downloaded, "queue completed while owner GameObject was inactive");
			owner.gameObject.SetActive(true);
		}

		static async Task CheckTransientRetries(DownloadHistoryView owner, SongSearchSong song, Action<string, bool, string> check) {
			Reset(owner);
			var entry = Add(owner, song);
			var attempts = 0;
			SongDownloader.DownloadOverride = async (item, token, progress) => {
				var attempt = Interlocked.Increment(ref attempts);
				item.status = DownloadHistoryView.DownloadHistoryEntry.DownloadStatus.Downloading;
				progress(.2f);
				await Task.Yield();
				if(attempt < 3)
					throw new WebException("test transient network error");
				progress(.8f);
			};

			await Drain(owner);
			check("transient retries", attempts == 3 && entry.retries == 3 && entry.status == DownloadHistoryView.DownloadHistoryEntry.DownloadStatus.Downloaded,
				$"attempts={attempts}, retries={entry.retries}, status={entry.status}");
		}

		static async Task CheckCancellation(DownloadHistoryView owner, SongSearchSong song, Action<string, bool, string> check) {
			Reset(owner);
			var entry = Add(owner, song);
			var started = new TaskCompletionSource<object>();
			SongDownloader.DownloadOverride = async (item, token, progress) => {
				item.status = DownloadHistoryView.DownloadHistoryEntry.DownloadStatus.Downloading;
				progress(.35f);
				started.TrySetResult(null);
				await Task.Delay(Timeout.Infinite, token);
			};

			owner.ProcessDownloads(true);
			var completed = await Task.WhenAny(started.Task, Task.Delay(2000));
			if(completed != started.Task) {
				check("cancel starts", false, "download did not start before timeout");
				owner.CancelPending();
				await owner.DrainAsync();
				return;
			}

			owner.CancelPending();
			await owner.DrainAsync();
			check("cancelled state", entry.status == DownloadHistoryView.DownloadHistoryEntry.DownloadStatus.Cancelled && entry.downloadProgress < 1f,
				$"status={entry.status}, progress={entry.downloadProgress:0.00}");
		}

		static async Task CheckManualRetry(DownloadHistoryView owner, SongSearchSong song, Action<string, bool, string> check) {
			var entry = owner.downloadList.Single();
			var key = entry.key;
			var hash = entry.hash;
			var name = entry.songName;
			var mapper = entry.levelAuthorName;
			var selectedField = typeof(DownloadHistoryView).GetField("selectedEntry", BindingFlags.Instance | BindingFlags.NonPublic);
			if(selectedField == null) {
				check("manual retry", false, "selected entry field is unavailable");
				return;
			}

			selectedField.SetValue(owner, entry);
			var attempts = 0;
			SongDownloader.DownloadOverride = async (item, token, progress) => {
				Interlocked.Increment(ref attempts);
				item.status = DownloadHistoryView.DownloadHistoryEntry.DownloadStatus.Downloading;
				progress(.2f);
				await Task.Yield();
				progress(.8f);
			};

			owner.RetryDownload();
			await owner.DrainAsync();
			var retained = ReferenceEquals(owner.downloadList.Single(), entry) && entry.key == key && entry.hash == hash &&
				entry.songName == name && entry.levelAuthorName == mapper;
			check("manual retry", attempts == 1 && retained && entry.status == DownloadHistoryView.DownloadHistoryEntry.DownloadStatus.Downloaded,
				$"attempts={attempts}, entries={owner.downloadList.Count}, status={entry.status}");
		}

		static async Task CheckPermanentNotFound(DownloadHistoryView owner, SongSearchSong song, Action<string, bool, string> check) {
			Reset(owner);
			var entry = Add(owner, song);
			var attempts = 0;
			SongDownloader.DownloadOverride = (item, token, progress) => {
				Interlocked.Increment(ref attempts);
				return Task.FromException(new FileNotFoundException("test 404"));
			};

			await Drain(owner);
			check("permanent not found", attempts == 1 && entry.status == DownloadHistoryView.DownloadHistoryEntry.DownloadStatus.Failed &&
				entry.failureCategory == "not-found" && entry.downloadProgress < 1f,
				$"attempts={attempts}, category={entry.failureCategory}, progress={entry.downloadProgress:0.00}");
		}

		static async Task CheckHttpFailureRetry(DownloadHistoryView owner, SongSearchSong song, Action<string, bool, string> check) {
			Reset(owner);
			var fixture = File.ReadAllBytes(Path.Combine(Directory.GetCurrentDirectory(), "releases", "BeatSage-wish-v2-Standard.zip"));
			var suffix = "BSSHttpRetry" + Guid.NewGuid().ToString("N");
			var entry = CreateUniqueEntry(song, suffix);
			var listener = new TcpListener(IPAddress.Loopback, 0);
			listener.Start();
			var oldUrl = PluginConfig.Instance.downloadUrlOverride;
			var previous = SongDownloader.DownloadOverride;
			var requests = 0;
			Task server = null;
			using(var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40))) {
				using(timeout.Token.Register(listener.Stop)) {
					try {
						PluginConfig.Instance.downloadUrlOverride = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port;
						SongDownloader.DownloadOverride = null;
						server = Task.Run(async () => {
							while(requests < 4 && !timeout.IsCancellationRequested) {
								using(var client = await listener.AcceptTcpClientAsync().ConfigureAwait(false))
								using(timeout.Token.Register(client.Close))
								using(var stream = client.GetStream()) {
									var header = new List<byte>();
									var next = new byte[1];
									while(header.Count < 8192) {
										if(await stream.ReadAsync(next, 0, 1, timeout.Token).ConfigureAwait(false) == 0) throw new IOException("Incomplete HTTP request");
										header.Add(next[0]);
										if(header.Count >= 4 && Encoding.ASCII.GetString(header.ToArray(), header.Count - 4, 4) == "\r\n\r\n") break;
									}
									var count = Interlocked.Increment(ref requests);
									var body = count <= 3 ? Encoding.UTF8.GetBytes("temporary outage") : fixture;
									var response = Encoding.ASCII.GetBytes("HTTP/1.1 " + (count <= 3 ? "503 Service Unavailable" : "200 OK") + "\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n");
									await stream.WriteAsync(response, 0, response.Length, timeout.Token).ConfigureAwait(false);
									await stream.WriteAsync(body, 0, body.Length, timeout.Token).ConfigureAwait(false);
								}
							}
						}, timeout.Token);
						owner.downloadList.Add(entry);
						await Drain(owner);
						check("HTTP failure bounded retries", requests == 3 && entry.retries == 3 && entry.status == DownloadHistoryView.DownloadHistoryEntry.DownloadStatus.Failed && entry.failureCategory == "network", "real HTTP 503: requests=" + requests + ", status=" + entry.status);
						typeof(DownloadHistoryView).GetField("selectedEntry", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(owner, entry);
						owner.RetryDownload();
						await owner.DrainAsync();
						await server;
						var root = Path.GetFullPath(CustomLevelPathHelper.customLevelsDirectoryPath);
						var folders = FindUniqueFolders(root, suffix);
						string detail = null;
						check("HTTP manual retry imports map", requests == 4 && entry.retries == 1 && entry.status == DownloadHistoryView.DownloadHistoryEntry.DownloadStatus.Downloaded && folders.Length == 1 && VerifyImportedArchive(Path.Combine(Directory.GetCurrentDirectory(), "releases", "BeatSage-wish-v2-Standard.zip"), folders[0], out detail), "requests=" + requests + ", status=" + entry.status + ", files=" + detail);
					} finally {
						timeout.Cancel(); listener.Stop();
						if(server != null) try { await server; } catch(Exception exception) { if(!timeout.IsCancellationRequested) check("HTTP retry server", false, exception.ToString()); }
						SongDownloader.DownloadOverride = previous;
						PluginConfig.Instance.downloadUrlOverride = oldUrl;
						var root = Path.GetFullPath(CustomLevelPathHelper.customLevelsDirectoryPath);
						foreach(var folder in FindUniqueFolders(root, suffix)) DeleteOwnedFolder(root, folder);
					}
				}
			}
		}

		internal static async Task CheckFlowLifecycleAsync(Action<string, bool, string> check) {
			var owner = BSSFlowCoordinator.downloadHistoryView;
			var song = FindFixtureSong();
			var previous = SongDownloader.DownloadOverride;
			var release = new TaskCompletionSource<bool>();
			var started = new TaskCompletionSource<bool>();
			var entry = Add(owner, song);
			var oldClose = BSSFlowCoordinator.closeCancelSource;
			try {
				SongDownloader.DownloadOverride = async (item, token, progress) => {
					started.TrySetResult(true);
					using(token.Register(() => release.TrySetCanceled())) await release.Task;
					token.ThrowIfCancellationRequested(); progress(.8f);
				};
				owner.ProcessDownloads(); await started.Task;
				BSSFlowCoordinator.Close(true);
				await Task.Delay(200);
				check("real close retains active download", oldClose.IsCancellationRequested && entry.isDownloading && ReferenceEquals(owner, DownloadHistoryView.Instance), "UI closed; application queue remains active");
				Manager.ShowFlow(true);
				var deadline = DateTime.UtcNow.AddSeconds(25);
				while((!BSSFlowCoordinator.IsUiAvailable || ReferenceEquals(oldClose, BSSFlowCoordinator.closeCancelSource) || SongListController.searchedSongsList == null || SongListController.searchedSongsList.Count <= 0) && DateTime.UtcNow < deadline) await Task.Delay(100);
				check("reopen retains queue owner", BSSFlowCoordinator.IsUiAvailable && !ReferenceEquals(oldClose, BSSFlowCoordinator.closeCancelSource) && ReferenceEquals(owner, BSSFlowCoordinator.downloadHistoryView) && owner.downloadList.Contains(entry), "reopened actual flow and retained queue entry");
				release.TrySetResult(true); await owner.DrainAsync();
				check("closed and reopened download completes", entry.status == DownloadHistoryView.DownloadHistoryEntry.DownloadStatus.Downloaded, entry.status.ToString());
			} finally {
				release.TrySetResult(true); await owner.DrainAsync(); owner.downloadList.Remove(entry);
				SongDownloader.DownloadOverride = previous; owner.RefreshTable();
			}
		}

		static async Task CheckProductionImport(SongSearchSong song, Action<string, bool, string> check) {
			var fixturePath = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "releases", "BeatSage-wish-v2-Standard.zip"));
			if(!File.Exists(fixturePath)) {
				check("production import fixture", false, fixturePath);
				return;
			}

			var customLevelsRoot = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), CustomLevelPathHelper.customLevelsDirectoryPath));
			var stagingRoot = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), ".tmp", "better-song-search-song-import"));
			var initialStages = new HashSet<string>(FindStageFolders(stagingRoot), StringComparer.OrdinalIgnoreCase);
			var runId = Guid.NewGuid().ToString("N");
			var successSuffix = "BSSSmokeSuccess" + runId;
			var invalidSuffix = "BSSSmokeInvalid" + runId;
			var cancelSuffix = "BSSSmokeCancel" + runId;
			var suffixes = new[] { successSuffix, invalidSuffix, cancelSuffix };
			var originalOverride = SongDownloader.DownloadOverride;
			var harmonyId = "BetterSongSearchSmoke.PackageImport." + runId;
			Harmony harmony = null;
			var ownedFolders = new List<string>();

			try {
				if(suffixes.Any(suffix => FindUniqueFolders(customLevelsRoot, suffix).Length != 0)) {
					check("production import isolation", false, "a unique test folder already exists");
					return;
				}

				var targetType = AccessTools.TypeByName("BetterSongSearch.Util.UnityWebrequestWrapper");
				var target = targetType == null ? null : AccessTools.Method(targetType, "DownloadBytes");
				if(target == null)
					throw new MissingMethodException("UnityWebrequestWrapper.DownloadBytes");

				harmony = new Harmony(harmonyId);
				harmony.Patch(target, prefix: new HarmonyMethod(typeof(DownloadChecks), nameof(DownloadBytesPrefix)));
				SongDownloader.DownloadOverride = null;
				packageFixtureBytes = File.ReadAllBytes(fixturePath);

				var successEntry = CreateUniqueEntry(song, successSuffix);
				await SongDownloader.BeatmapDownload(successEntry, CancellationToken.None, null);
				var successFolders = FindUniqueFolders(customLevelsRoot, successSuffix);
				ownedFolders.AddRange(successFolders);
				string successDetail = null;
				var successValid = successFolders.Length == 1 && VerifyImportedArchive(fixturePath, successFolders[0], out successDetail);
				var successStages = FindNewStageFolders(stagingRoot, initialStages);
				var successReport = (successDetail ?? (successFolders.Length == 1 ? successFolders[0] : "published folder count=" + successFolders.Length)) + "; staging folders=" + successStages.Length;
				check("production import files", successValid && successStages.Length == 0,
					successReport);

				var invalidEntry = CreateUniqueEntry(song, invalidSuffix);
				packageFixtureBytes = new byte[] { 0x42, 0x53, 0x53, 0x2D, 0x62, 0x61, 0x64 };
				Exception invalidError = null;
				try {
					await SongDownloader.BeatmapDownload(invalidEntry, CancellationToken.None, null);
				} catch(Exception exception) {
					invalidError = Unwrap(exception);
				}
				var invalidInfo = invalidError == null ? null : SongDownloader.ClassifyFailure(invalidError);
				var invalidFolders = FindUniqueFolders(customLevelsRoot, invalidSuffix);
				ownedFolders.AddRange(invalidFolders);
				var invalidStages = FindNewStageFolders(stagingRoot, initialStages);
				check("production invalid archive", invalidInfo != null && invalidInfo.Category == "invalid-archive" && !invalidInfo.Transient && invalidFolders.Length == 0 && invalidStages.Length == 0,
					$"error={invalidError?.GetType().Name}, category={invalidInfo?.Category}, folders={invalidFolders.Length}, staging={invalidStages.Length}");

				packageFixtureBytes = File.ReadAllBytes(fixturePath);
				var cancelEntry = CreateUniqueEntry(song, cancelSuffix);
				using(var cancelSource = new CancellationTokenSource()) {
					var progressCallbacks = 0;
					Exception cancelError = null;
					try {
						await Task.Run(() => SongDownloader.BeatmapDownload(cancelEntry, cancelSource.Token, progress => {
							if(progress > 0f) {
								Interlocked.Increment(ref progressCallbacks);
								cancelSource.Cancel();
							}
						}), CancellationToken.None);
					} catch(Exception exception) {
						cancelError = Unwrap(exception);
					}

					var cancelInfo = cancelError == null ? null : SongDownloader.ClassifyFailure(cancelError);
					var cancelFolders = FindUniqueFolders(customLevelsRoot, cancelSuffix);
					ownedFolders.AddRange(cancelFolders);
					var cancelStages = FindNewStageFolders(stagingRoot, initialStages);
					check("production extraction cancellation", cancelInfo != null && cancelInfo.Category == "cancelled" && progressCallbacks > 0 && cancelFolders.Length == 0 && cancelStages.Length == 0,
						$"error={cancelError?.GetType().Name}, category={cancelInfo?.Category}, progressCallbacks={progressCallbacks}, folders={cancelFolders.Length}, staging={cancelStages.Length}");
				}
			} catch(Exception exception) {
				check("production import integration", false, exception.ToString());
			} finally {
				packageFixtureBytes = null;
				SongDownloader.DownloadOverride = originalOverride;
				if(harmony != null)
					harmony.UnpatchSelf();

				foreach(var folder in ownedFolders.Distinct(StringComparer.OrdinalIgnoreCase))
					DeleteOwnedFolder(customLevelsRoot, folder);
			}
		}

		static bool DownloadBytesPrefix(ref Task<byte[]> __result, string url, CancellationToken token, Action<float> progressCb) {
			token.ThrowIfCancellationRequested();
			var bytes = packageFixtureBytes;
			if(bytes == null)
				throw new InvalidOperationException("Package fixture is not configured.");

			__result = Task.FromResult((byte[])bytes.Clone());
			return false;
		}

		static DownloadHistoryView.DownloadHistoryEntry CreateUniqueEntry(SongSearchSong song, string suffix) {
			var entry = new DownloadHistoryView.DownloadHistoryEntry(song);
			var field = typeof(DownloadHistoryView.DownloadHistoryEntry).GetField("songName", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			if(field == null)
				throw new MissingFieldException(typeof(DownloadHistoryView.DownloadHistoryEntry).FullName, "songName");
			field.SetValue(entry, (entry.songName ?? "Song") + " " + suffix);
			return entry;
		}

		static Exception Unwrap(Exception exception) {
			while(true) {
				var aggregate = exception as AggregateException;
				if(aggregate != null && aggregate.InnerExceptions.Count == 1) {
					exception = aggregate.InnerException;
					continue;
				}

				var target = exception as TargetInvocationException;
				if(target != null && target.InnerException != null) {
					exception = target.InnerException;
					continue;
				}

				return exception;
			}
		}

		static string[] FindUniqueFolders(string root, string suffix) {
			if(!Directory.Exists(root))
				return new string[0];

			return Directory.GetDirectories(root, "*", SearchOption.TopDirectoryOnly)
				.Where(path => Path.GetFileName(path).IndexOf(suffix, StringComparison.OrdinalIgnoreCase) >= 0)
				.ToArray();
		}

		static string[] FindStageFolders(string root) {
			return Directory.Exists(root) ? Directory.GetDirectories(root, ".song-package-*", SearchOption.TopDirectoryOnly) : new string[0];
		}

		static string[] FindNewStageFolders(string root, HashSet<string> initial) {
			return FindStageFolders(root).Where(path => !initial.Contains(path)).ToArray();
		}

		static bool VerifyImportedArchive(string archivePath, string folder, out string detail) {
			detail = null;
			if(!IsDirectChild(CustomLevelPathHelper.customLevelsDirectoryPath, folder)) {
				detail = "published path is outside CustomLevels: " + folder;
				return false;
			}

			try {
				using(var archive = ZipFile.OpenRead(archivePath)) {
					var expected = archive.Entries.Where(entry => !entry.FullName.EndsWith("/", StringComparison.Ordinal)).ToArray();
					var actual = Directory.Exists(folder)
						? Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Select(path => RelativeArchivePath(folder, path)).ToArray()
						: new string[0];
					if(expected.Length != actual.Length || !expected.Select(entry => entry.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(actual)) {
						detail = $"file set differs: expected={expected.Length}, actual={actual.Length}";
						return false;
					}

					foreach(var entry in expected) {
						var path = Path.Combine(folder, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
						if(!File.Exists(path)) {
							detail = "missing " + entry.FullName;
							return false;
						}

						byte[] expectedHash;
						byte[] actualHash;
						using(var hash = SHA256.Create()) {
							using(var input = entry.Open())
								expectedHash = hash.ComputeHash(input);
							using(var output = File.OpenRead(path))
								actualHash = hash.ComputeHash(output);
						}

						if(!expectedHash.SequenceEqual(actualHash)) {
							detail = "content differs: " + entry.FullName;
							return false;
						}
					}
				}

				detail = "all archive files preserved";
				return true;
			} catch(Exception exception) {
				detail = exception.ToString();
				return false;
			}
		}

		static string RelativeArchivePath(string root, string path) {
			var rootWithSeparator = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
			var fullPath = Path.GetFullPath(path);
			return fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase)
				? fullPath.Substring(rootWithSeparator.Length).Replace(Path.DirectorySeparatorChar, '/')
				: fullPath;
		}

		static bool IsDirectChild(string root, string path) {
			var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
			var pathFull = Path.GetFullPath(path);
			var parent = Path.GetDirectoryName(pathFull);
			return String.Equals(parent?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), rootFull, StringComparison.OrdinalIgnoreCase);
		}

		static void DeleteOwnedFolder(string customLevelsRoot, string folder) {
			if(!IsDirectChild(customLevelsRoot, folder))
				throw new InvalidOperationException("Refusing to clean a non-child test folder: " + folder);
			if(Directory.Exists(folder))
				Directory.Delete(folder, true);
		}
	}
}
