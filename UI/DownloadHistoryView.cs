using BeatSaberMarkupLanguage;
using BeatSaberMarkupLanguage.Attributes;
using BeatSaberMarkupLanguage.Components;
using BeatSaberMarkupLanguage.Parser;
using BetterSongSearch.UI.CustomLists;
using BetterSongSearch.Util;
using HMUI;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;

namespace BetterSongSearch.UI {
	// Application-owned queue; the flow only renders its current state.
	class DownloadHistoryView : MonoBehaviour, TableView.IDataSource {
		internal static DownloadHistoryView Instance { get; private set; }
		internal static bool IsMenu => SceneManager.GetActiveScene().name == "MainMenu" && !SceneManager.GetSceneByName("GameCore").isLoaded;
		Task songRefreshTask = Task.CompletedTask;
		bool shuttingDown;
		int downloadRevision;
		internal static DownloadHistoryView GetOrCreate() {
			if(Instance == null) {
				Instance = new GameObject("BSSApplicationDownloads").AddComponent<DownloadHistoryView>();
				DontDestroyOnLoad(Instance.gameObject);
				SceneManager.activeSceneChanged += Instance.SceneChanged;
				SceneManager.sceneLoaded += Instance.SceneLoaded;
				SceneManager.sceneUnloaded += Instance.SceneUnloaded;
			}
			return Instance;
		}
		void SceneChanged(Scene previous, Scene current) => RequestSongRefresh();
		void SceneLoaded(Scene scene, LoadSceneMode mode) => RequestSongRefresh();
		void SceneUnloaded(Scene scene) => RequestSongRefresh();
		internal void RequestSongRefresh() {
			if(this == Instance && !shuttingDown && IsMenu && hasUnloadedDownloads && songRefreshTask.IsCompleted)
				songRefreshTask = RefreshCompletedSongsAsync();
		}
		async Task RefreshCompletedSongsAsync() {
			try {
				// SongCore UI updates wait for a continuous stable menu interval.
				if(shuttingDown || !IsMenu) return;
				int revision;
				do {
					revision = downloadRevision;
                    for(int attempt=0;;attempt++) {
                        try { if(!await BSSFlowCoordinator.RefreshSongCoreAsync(pendingCancelSource.Token)) return; break; }
                        catch(OperationCanceledException) { throw; }
                        catch(Exception exception) {
                            if(attempt >= 2 || !IsMenu || shuttingDown) throw;
                            Plugin.Log?.Warn("SongCore menu refresh will retry: " + exception);
                            await Task.Delay(1000 * (attempt+1),pendingCancelSource.Token);
                        }
                    }
					foreach(var entry in downloadList)
						if(entry.status == DownloadHistoryEntry.DownloadStatus.Downloaded && SongCore.Collections.songWithHashPresent(entry.hash))
							entry.status = DownloadHistoryEntry.DownloadStatus.Loaded;
				} while(!shuttingDown && IsMenu && revision != downloadRevision && hasUnloadedDownloads);
				RefreshTable(false);
			} catch(OperationCanceledException) { }
			  catch(Exception exception) { Plugin.Log?.Warn("Downloaded maps remain pending SongCore refresh: " + exception); }
		}
		internal async Task RefreshSongsAsync(CancellationToken token) {
			if(!IsMenu) throw new InvalidOperationException("SongCore refresh requires the main menu");
			if(songRefreshTask.IsCompleted) songRefreshTask = RefreshCompletedSongsAsync();
			var pending = songRefreshTask;
			var cancelled = new TaskCompletionSource<bool>();
			using(token.Register(() => cancelled.TrySetResult(true))) {
				if(await Task.WhenAny(pending, cancelled.Task) != pending) token.ThrowIfCancellationRequested();
				await pending;
				token.ThrowIfCancellationRequested();
			}
		}
		internal void SuspendRendering() {
			foreach(var entry in downloadList) entry.UpdateProgressHandler = null;
		}
		internal void Shutdown() { shuttingDown = true; CancelPending(); }
		void OnDestroy() {
			if(Instance != this) return;
			SceneManager.activeSceneChanged -= SceneChanged;
			SceneManager.sceneLoaded -= SceneLoaded;
			SceneManager.sceneUnloaded -= SceneUnloaded;
			Shutdown(); Instance = null;
		}
		[UIComponent("scrollBarContainer")] VerticalLayoutGroup _scrollBarContainer = null;
		[UIComponent("downloadList")] CustomListTableData downloadHistoryData = null;
		[UIComponent("retryButton")] Button retryButton = null;

		TableView downloadHistoryTable => downloadHistoryData != null ? downloadHistoryData.TableView : null;
		DownloadHistoryEntry[] downloadListSorted = new DownloadHistoryEntry[0];
		GameObject renderedParent = null;
		BSMLParserParams parserParams = null;
		RatelimitCoroutine limitedFullTableReload = null;
		SynchronizationContext mainThreadContext = null;

		readonly object stateGate = new object();
		readonly List<Task> activeDownloads = new List<Task>();
		readonly HashSet<DownloadHistoryEntry> activeEntries = new HashSet<DownloadHistoryEntry>();
		CancellationTokenSource pendingCancelSource = new CancellationTokenSource();
		Task processorTask = null;
		bool cancelRequested;
		DownloadHistoryEntry selectedEntry = null;
		int nextOrder;
		string lastNotifiedSummary = null;

		const int RETRY_COUNT = 3;
		const int MAX_PARALLEL_DOWNLOADS = 2;

		public readonly List<DownloadHistoryEntry> downloadList = new List<DownloadHistoryEntry>();
		public bool hasUnloadedDownloads => downloadList.Any(x => x.status == DownloadHistoryEntry.DownloadStatus.Downloaded);
		public event Action Changed;

		public string Summary {
			get {
				int total = 0;
				int queued = 0;
				int active = 0;
				int waiting = 0;
				int failed = 0;
				int cancelled = 0;

				lock(stateGate) {
					total = downloadList.Count;
					foreach(var entry in downloadList) {
						switch(entry.status) {
							case DownloadHistoryEntry.DownloadStatus.Queued: queued++; break;
							case DownloadHistoryEntry.DownloadStatus.Preparing:
							case DownloadHistoryEntry.DownloadStatus.Downloading:
							case DownloadHistoryEntry.DownloadStatus.Extracting: active++; break;
							case DownloadHistoryEntry.DownloadStatus.Downloaded: waiting++; break;
							case DownloadHistoryEntry.DownloadStatus.Failed: failed++; break;
							case DownloadHistoryEntry.DownloadStatus.Cancelled: cancelled++; break;
						}
					}
				}

				var parts = new List<string>();
				if(active > 0) parts.Add($"{BssText.T("Active")} {active}");
				if(queued > 0) parts.Add($"{BssText.T("Queued")} {queued}");
				if(waiting > 0) parts.Add($"{BssText.T("Waiting")} {waiting}");
				if(failed > 0) parts.Add($"{BssText.T("Failed")} {failed}");
				if(cancelled > 0) parts.Add($"{BssText.T("Cancelled")} {cancelled}");

				return total.ToString() + (parts.Count == 0 ? "" : $" · {string.Join(" · ", parts)}");
			}
		}

		void Awake() {
			mainThreadContext = SynchronizationContext.Current;
		}

		/// <summary>
		/// Parse the history layout into the owner-provided detail container. The
		/// parser is explicit so queue processing never depends on content being
		/// active or visible.
		/// </summary>
		public void RenderInto(GameObject parent) {
			if(parent == null)
				return;

			if(renderedParent == parent && downloadHistoryData != null) {
				RefreshTable(false);
				return;
			}

			renderedParent = parent;
			parserParams = BSMLParser.Instance.Parse(
				Utilities.GetResourceContent(Assembly.GetExecutingAssembly(), "BetterSongSearch.UI.Views.DownloadHistory.bsml"),
				parent, this
			);
            BSMLStuff.InitializeScrollViews(parent);
		}

		public bool TryAddDownload(SongSearchSong song, bool isBatch = false) {
			if(shuttingDown || song == null || !song.CheckIsDownloadable())
				return false;

			DownloadHistoryEntry entry;
			lock(stateGate) {
				entry = downloadList.FirstOrDefault(x => x.hash.Equals(song.hash,StringComparison.OrdinalIgnoreCase));

				if(entry != null) {
					if(activeEntries.Contains(entry))
						return false;

					var wasRetryableTerminal = entry.status == DownloadHistoryEntry.DownloadStatus.Failed ||
						entry.status == DownloadHistoryEntry.DownloadStatus.Cancelled;
					entry.ResetIfFailed();
					if(entry.status == DownloadHistoryEntry.DownloadStatus.Downloaded ||
						entry.status == DownloadHistoryEntry.DownloadStatus.Loaded ||
						entry.isDownloading || (!wasRetryableTerminal && entry.status == DownloadHistoryEntry.DownloadStatus.Queued))
						return false;

					entry.status = DownloadHistoryEntry.DownloadStatus.Queued;
					entry.statusDetails = "";
					entry.failureCategory = "";
					entry.downloadProgress = 0f;
				} else {
					entry = new DownloadHistoryEntry(song, ++nextOrder);
					downloadList.Add(entry);
				}

				if(pendingCancelSource.IsCancellationRequested && activeEntries.Count == 0) {
					pendingCancelSource.Dispose();
					pendingCancelSource = new CancellationTokenSource();
					cancelRequested = false;
				}
			}

			RunOnMainThread(() => downloadHistoryTable?.ScrollToCellWithIdx(0, TableView.ScrollPositionType.Beginning, false));

			RefreshTable(!isBatch);
			ProcessDownloads(!isBatch);
			NotifyChanged();
			return true;
		}

		[UIAction("SelectSong")]
		void SelectSong(TableView _, int idx) {
			var entry = idx >= 0 && idx < downloadListSorted.Length ? downloadListSorted[idx] : null;
			selectedEntry = entry;
			UpdateRetryButton();

			if(entry == null || BSSFlowCoordinator.songDetails == null || BSSFlowCoordinator.songsList == null)
				return;

			if(BSSFlowCoordinator.songDetails.songs.FindByHash(entry.hash, out var song) &&
				song.index >= 0 && song.index < BSSFlowCoordinator.songsList.Length) {
				var selectedSongView = BSSFlowCoordinator.songListView?.selectedSongView;
				if(selectedSongView != null)
					selectedSongView.SetSelectedSong(BSSFlowCoordinator.songsList[song.index], true);

				BSSFlowCoordinator.ShowDetails();
			}
		}

		[UIAction("RetryDownload")]
		public void RetryDownload() {
			var entry = selectedEntry;
			if(entry == null || (entry.status != DownloadHistoryEntry.DownloadStatus.Failed &&
				entry.status != DownloadHistoryEntry.DownloadStatus.Cancelled))
				return;

			lock(stateGate) {
				if(activeEntries.Contains(entry))
					return;

				if(shuttingDown) return;
				if(pendingCancelSource.IsCancellationRequested && activeEntries.Count == 0) {
					pendingCancelSource.Dispose();
					pendingCancelSource = new CancellationTokenSource();
					cancelRequested = false;
				}

				entry.retries = 0;
				entry.runGeneration++;
				entry.activeRunGeneration = 0;
				entry.status = DownloadHistoryEntry.DownloadStatus.Queued;
				entry.statusDetails = "";
				entry.failureCategory = "";
				entry.downloadProgress = 0f;
			}

			UpdateRetryButton();
			RefreshTable(true);
			ProcessDownloads(true);
			NotifyChanged();
		}

		/// <summary>
		/// Starts or wakes the finite two-worker queue. This method intentionally
		/// does not inspect component visibility: history can be hidden while the
		/// detail controller remains alive.
		/// </summary>
		public void ProcessDownloads(bool forceTableReload = false) {
			if(!IsOnMainThread()) {
				RunOnMainThread(() => ProcessDownloads(forceTableReload));
				return;
			}

			if(forceTableReload)
				RefreshTable(true);

			lock(stateGate) {
				if(processorTask == null || processorTask.IsCompleted)
					processorTask = ProcessDownloadsCoreAsync();
			}
		}

		async Task ProcessDownloadsCoreAsync() {
			try {
				while(true) {
					lock(stateGate) {
						for(var i = activeDownloads.Count - 1; i >= 0; i--)
							if(activeDownloads[i].IsCompleted)
								activeDownloads.RemoveAt(i);

						while(!cancelRequested && activeDownloads.Count < MAX_PARALLEL_DOWNLOADS) {
							var next = ClaimNextEntry();
							if(next == null)
								break;

							activeEntries.Add(next);
							activeDownloads.Add(DownloadEntryAsync(next, next.activeRunGeneration));
						}

						if(activeDownloads.Count == 0)
							return;
					}

					Task completed;
					lock(stateGate)
						completed = Task.WhenAny(activeDownloads.ToArray());

					// The pump is started on Unity's captured context. Keep the await
					// context so the next refill (including retries) starts the next
					// downloader on that same context.
					await completed;
					// DownloadEntryAsync handles all expected exceptions and always
					// observes its own task, so a worker completion refills the slot.
				}
			} catch(Exception exception) {
				Plugin.Log?.Warn("Download queue processor failed");
				Plugin.Log?.Warn(exception);
				lock(stateGate) {
					foreach(var entry in activeEntries.ToArray()) {
						entry.runGeneration++;
						entry.activeRunGeneration = 0;
						entry.status = DownloadHistoryEntry.DownloadStatus.Failed;
						entry.statusDetails = "Download queue stopped";
						entry.failureCategory = "processor";
						entry.downloadProgress = Math.Min(entry.downloadProgress, 0.99f);
					}
				}
				RefreshTable(true);
				NotifyChanged();
			}
		}

		DownloadHistoryEntry ClaimNextEntry() {
			var next = downloadList
				.Where(x => x.status == DownloadHistoryEntry.DownloadStatus.Queued && x.retries < RETRY_COUNT)
				.OrderBy(x => x.orderValue)
				.FirstOrDefault();

			if(next == null)
				return null;

			next.retries++;
			next.runGeneration++;
			next.activeRunGeneration = next.runGeneration;
			next.downloadProgress = 0f;
			next.status = DownloadHistoryEntry.DownloadStatus.Preparing;
			next.statusDetails = $"attempt {next.retries}/{RETRY_COUNT}";
			next.failureCategory = "";
			NotifyEntryChanged(next);
			return next;
		}

		async Task DownloadEntryAsync(DownloadHistoryEntry entry, int generation) {
			try {
				using(var linked = CancellationTokenSource.CreateLinkedTokenSource(pendingCancelSource.Token)) {
					if(entry.retries > 1) await Task.Delay(1000 * (1 << (entry.retries - 2)), linked.Token);
					var updateRateLimiter = new Stopwatch();
					updateRateLimiter.Start();
					await SongDownloader.BeatmapDownload(entry, linked.Token, progress => {
						if(!IsCurrentRun(entry, generation) || IsCancellationRequested())
							return;
						if(updateRateLimiter.ElapsedMilliseconds < 50 && progress > 0f && progress < 1f)
							return;
						updateRateLimiter.Restart();
						entry.downloadProgress = Mathf.Clamp01(progress);
						if(entry.status == DownloadHistoryEntry.DownloadStatus.Downloading ||
							entry.status == DownloadHistoryEntry.DownloadStatus.Extracting)
							entry.statusDetails = $"({entry.downloadProgress:0%})";
						NotifyEntryChanged(entry);
					});
				}

				if(!IsCurrentRun(entry, generation))
					return;

				if(IsCancellationRequested()) {
					MarkCancelled(entry, generation);
					return;
				}

				entry.downloadProgress = 1f;
				entry.status = DownloadHistoryEntry.DownloadStatus.Downloaded;
				entry.statusDetails = "waiting SongCore load";
				entry.failureCategory = "";
				downloadRevision++;
				MarkSelectedSongDownloaded(entry, generation);
				NotifyEntryChanged(entry);
				RequestSongRefresh();
			} catch(OperationCanceledException) {
				if(IsCurrentRun(entry, generation))
					MarkCancelled(entry, generation);
			} catch(Exception ex) {
				if(IsCurrentRun(entry, generation))
					HandleFailure(entry, ex, generation);
			} finally {
				var notifyRetryState = false;
				lock(stateGate) {
					var wasActive = activeEntries.Remove(entry);
					if(entry.activeRunGeneration == generation) {
						entry.activeRunGeneration = 0;
					}
					notifyRetryState = wasActive &&
						(entry.status == DownloadHistoryEntry.DownloadStatus.Failed ||
						 entry.status == DownloadHistoryEntry.DownloadStatus.Cancelled);
				}

				if(notifyRetryState) {
					RefreshTable(false);
					NotifyChanged();
				}
			}
		}

		bool IsCancellationRequested() {
			return cancelRequested || pendingCancelSource.IsCancellationRequested;
		}

		bool IsOnMainThread() => mainThreadContext == null || SynchronizationContext.Current == mainThreadContext;

		bool IsCurrentRun(DownloadHistoryEntry entry, int generation) {
			lock(stateGate)
				return entry.runGeneration == generation && entry.activeRunGeneration == generation;
		}

		bool IsEntryActive(DownloadHistoryEntry entry) {
			if(entry == null)
				return false;

			lock(stateGate)
				return activeEntries.Contains(entry);
		}

		void HandleFailure(DownloadHistoryEntry entry, Exception exception, int generation) {
			if(!IsCurrentRun(entry, generation))
				return;

			var failure = SongDownloader.ClassifyFailure(exception);
			LogFailure(entry, exception, failure.Category);

			if(failure.Transient && entry.retries < RETRY_COUNT && !IsCancellationRequested()) {
				entry.status = DownloadHistoryEntry.DownloadStatus.Queued;
				entry.statusDetails = $"Network error; retrying ({entry.retries}/{RETRY_COUNT})";
				entry.failureCategory = failure.Category;
				entry.downloadProgress = Math.Min(entry.downloadProgress, 0.99f);
			} else if(IsCancellationRequested() || failure.Category == "cancelled") {
				MarkCancelled(entry, generation);
				return;
			} else {
				entry.status = DownloadHistoryEntry.DownloadStatus.Failed;
				entry.statusDetails = failure.UserMessage;
				entry.failureCategory = failure.Category;
				entry.downloadProgress = Math.Min(entry.downloadProgress, 0.99f);
			}

			NotifyEntryChanged(entry);
		}

		void MarkCancelled(DownloadHistoryEntry entry, int generation) {
			if(!IsCurrentRun(entry, generation))
				return;

			entry.status = DownloadHistoryEntry.DownloadStatus.Cancelled;
			entry.statusDetails = "";
			entry.failureCategory = "cancelled";
			entry.downloadProgress = Math.Min(entry.downloadProgress, 0.99f);
			NotifyEntryChanged(entry);
		}

		void MarkSelectedSongDownloaded(DownloadHistoryEntry entry, int generation) {
			if(!IsCurrentRun(entry, generation) || !BSSFlowCoordinator.IsUiAvailable)
				return;

			try {
				var selectedSongView = BSSFlowCoordinator.songListView?.selectedSongView;
				if(selectedSongView?.selectedSong != null && selectedSongView.selectedSong.detailsSong.key == entry.key)
					selectedSongView.SetIsDownloaded(true);

				BSSFlowCoordinator.songListView?.songList?.RefreshCells(false, true);
			} catch(Exception ex) {
				Plugin.Log?.Warn("Downloaded map UI refresh failed");
				Plugin.Log?.Warn(ex);
			}
		}

		void LogFailure(DownloadHistoryEntry entry, Exception exception, string category) {
			try {
				Plugin.Log?.Warn($"Song download failed ({category}, attempt {entry.retries}/{RETRY_COUNT}) for {entry.key}");
				Plugin.Log?.Warn(exception);
			} catch(Exception loggingException) {
				UnityEngine.Debug.LogWarning("BetterSongSearch download failure logging failed: " + loggingException);
			}
		}

		public void CancelPending() {
			if(!IsOnMainThread()) {
				RunOnMainThread(CancelPending);
				return;
			}

			lock(stateGate) {
				cancelRequested = true;
				if(!pendingCancelSource.IsCancellationRequested)
					pendingCancelSource.Cancel();

				foreach(var entry in downloadList) {
					if(entry.status != DownloadHistoryEntry.DownloadStatus.Queued && !entry.isDownloading)
						continue;

					entry.runGeneration++;
					entry.activeRunGeneration = 0;
					entry.status = DownloadHistoryEntry.DownloadStatus.Cancelled;
					entry.statusDetails = "";
					entry.failureCategory = "cancelled";
					entry.downloadProgress = Math.Min(entry.downloadProgress, 0.99f);
				}
			}

			RefreshTable(true);
			NotifyChanged();
		}

		public Task DrainAsync() {
			if(!IsOnMainThread() && mainThreadContext != null) {
				var completion = new TaskCompletionSource<object>();
				mainThreadContext.Post(async _ => {
					try {
						await DrainAsyncCore();
						completion.TrySetResult(null);
					} catch(Exception ex) {
						completion.TrySetException(ex);
					}
				}, null);
				return completion.Task;
			}

			return DrainAsyncCore();
		}

		async Task DrainAsyncCore() {
			while(true) {
				Task current;
				lock(stateGate)
					current = processorTask;

				if(current != null)
					await current;

				lock(stateGate) {
					if((processorTask == null || processorTask.IsCompleted) &&
						!downloadList.Any(x => x.status == DownloadHistoryEntry.DownloadStatus.Queued || x.isDownloading))
						return;
				}

				ProcessDownloads(false);
			}
		}

		public void RefreshTable(bool fullReload = true) {
			lock(stateGate)
				downloadListSorted = downloadList.OrderBy(x => x.orderValue).ThenBy(x => x.sequence).ToArray();

			RunOnMainThread(() => {
				if(!BSSFlowCoordinator.IsUiAvailable) return;
				var summary = Summary;
				var summaryChanged = false;
				lock(stateGate) {
					if(lastNotifiedSummary != summary) {
						lastNotifiedSummary = summary;
						summaryChanged = true;
					}
				}

				UpdateRetryButton();
				var table = downloadHistoryTable;
				if(table != null) {
					if(limitedFullTableReload != null && SharedCoroutineStarter.instance != null)
						SharedCoroutineStarter.instance.StartCoroutine(fullReload ? limitedFullTableReload.Call() : limitedFullTableReload.CallNextFrame());
					else
						table.ReloadData();
				}

				if(summaryChanged)
					NotifyChanged();
			});
		}

		[UIAction("#post-parse")]
		void Parsed() {
			var table = downloadHistoryTable;
			if(table == null)
				return;

			limitedFullTableReload = new RatelimitCoroutine(table.ReloadData, 0.1f);
			lock(stateGate)
				downloadListSorted = downloadList.OrderBy(x => x.orderValue).ThenBy(x => x.sequence).ToArray();

			table.SetDataSource(this, false);
			table._canSelectSelectedCell = true;

			if(_scrollBarContainer != null && downloadHistoryData != null)
				BSMLStuff.GetScrollbarForTable(downloadHistoryData.gameObject, _scrollBarContainer.transform);

			RefreshTable(false);
		}

		void UpdateRetryButton() {
			if(retryButton == null)
				return;

			var canRetry = selectedEntry != null &&
				(selectedEntry.status == DownloadHistoryEntry.DownloadStatus.Failed ||
				 selectedEntry.status == DownloadHistoryEntry.DownloadStatus.Cancelled) &&
				!IsEntryActive(selectedEntry);
			retryButton.gameObject.SetActive(canRetry);
			retryButton.interactable = canRetry;
		}

		void NotifyEntryChanged(DownloadHistoryEntry entry) {
			var handler = entry.UpdateProgressHandler;
			if(handler != null && BSSFlowCoordinator.IsUiAvailable)
				RunOnMainThread(() => {
					if(!BSSFlowCoordinator.IsUiAvailable) return;
					try { handler(); } catch(Exception ex) {
						Plugin.Log?.Warn("Download cell update failed");
						Plugin.Log?.Warn(ex);
					}
				});

			RefreshTable(false);
			NotifyChanged();
		}

		void NotifyChanged() {
			var handler = Changed;
			if(handler == null)
				return;

			RunOnMainThread(() => {
				if(!BSSFlowCoordinator.IsUiAvailable) return;
				try { handler(); } catch(Exception ex) {
					Plugin.Log?.Warn("Download history change notification failed");
					Plugin.Log?.Warn(ex);
				}
			});
		}

		void RunOnMainThread(Action action) {
			if(action == null)
				return;

			if(mainThreadContext != null && SynchronizationContext.Current != mainThreadContext) {
				mainThreadContext.Post(_ => {
					try { action(); } catch(Exception ex) {
						Plugin.Log?.Warn("Download history UI update failed");
						Plugin.Log?.Warn(ex);
					}
				}, null);
				return;
			}

			action();
		}

		public float CellSize(int idx) => 11f;
		public int NumberOfCells() => downloadListSorted?.Length ?? 0;
		public TableCell CellForIdx(TableView tableView, int idx) => DownloadListTableData.GetCell(tableView).PopulateWithSongData(downloadListSorted[idx]);

		public class DownloadHistoryEntry {
			[Flags]
			public enum DownloadStatus : byte {
				Downloading = 1,
				Preparing = 2,
				Extracting = 4,
				Queued = 8,
				Failed = 16,
				Downloaded = 32,
				Loaded = 64,
				Cancelled = 128
			}

			public bool isDownloading => status == DownloadStatus.Downloading || status == DownloadStatus.Preparing || status == DownloadStatus.Extracting;
			public bool isQueued => status == DownloadStatus.Queued;

			public DownloadStatus status = DownloadStatus.Queued;
			public string statusDetails = "";
			public string failureCategory = "";
			public float downloadProgress = 0f;
			public int retries = 0;

			public readonly string songName;
			public readonly string levelAuthorName;
			public readonly string key;
			public readonly string hash;
			internal readonly int sequence;
			internal int runGeneration;
			internal int activeRunGeneration;

			public int orderValue => ((int)status * 100) + retries;
			public string statusLabel {
				get {
					switch(status) {
						case DownloadStatus.Queued: return BssText.T("Queued");
						case DownloadStatus.Preparing: return BssText.T("Preparing");
						case DownloadStatus.Downloading: return BssText.T("Downloading");
						case DownloadStatus.Extracting: return BssText.T("Extracting");
						case DownloadStatus.Downloaded: return BssText.T("Downloaded");
						case DownloadStatus.Loaded: return BssText.T("Loaded");
						case DownloadStatus.Failed: return BssText.T("Failed");
						case DownloadStatus.Cancelled: return BssText.T("Cancelled");
						default: return BssText.T("Unknown");
					}
				}
			}

			public string statusMessage {
				get {
					if(status == DownloadStatus.Downloaded) return BssText.T("Waiting for song load");
					if(status == DownloadStatus.Downloading || status == DownloadStatus.Extracting) return statusLabel + " " + downloadProgress.ToString("0%");
					if(status == DownloadStatus.Queued && retries > 0) return BssText.T("Retrying") + " " + (retries+1) + "/3";
					if(status == DownloadStatus.Failed && !String.IsNullOrEmpty(statusDetails)) return statusLabel + " · " + BssText.T(statusDetails);
					return statusLabel;
				}
			}

			public bool IsInAnyOfStates(DownloadStatus states) => (status & states) != 0;

			public void ResetIfFailed() {
				if(status != DownloadStatus.Failed && status != DownloadStatus.Cancelled)
					return;

				status = DownloadStatus.Queued;
				retries = 0;
				statusDetails = "";
				failureCategory = "";
				downloadProgress = 0f;
			}

			public DownloadHistoryEntry(SongSearchSong song, int sequence = 0) {
				songName = song.detailsSong.songName;
				levelAuthorName = song.detailsSong.levelAuthorName;
				key = song.detailsSong.key.ToLowerInvariant();
				hash = song.detailsSong.hash.ToLowerInvariant();
				this.sequence = sequence;
			}

			public Action UpdateProgressHandler;
		}
	}
}
