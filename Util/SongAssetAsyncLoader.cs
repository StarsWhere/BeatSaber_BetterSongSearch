using BetterSongSearch.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SongDetailsCache.Structs;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace BetterSongSearch.Util {
	/// <summary>
	/// Owns the transient media requested by the BetterSongSearch flow.
	/// Covers are handed out through leases because table cells and the detail
	/// view do not have the same lifetime. The loader itself is flow-owned and
	/// must be disposed when that flow closes.
	/// </summary>
	internal sealed class SongAssetAsyncLoader : IDisposable {
		const int MaxCoverCacheEntries = 128;
		const int MaxPreviewCacheEntries = 4;
		static readonly TimeSpan PreviewHandoffGrace = TimeSpan.FromSeconds(2);

		readonly object _gate = new object();
		readonly CancellationTokenSource _disposeCancellation = new CancellationTokenSource();
		readonly CancellationToken _disposeToken;
		readonly TaskScheduler _unityScheduler;
		readonly SemaphoreSlim _coverDownloadSlots = new SemaphoreSlim(2, 2);
		readonly Func<string, CancellationToken, Task<Sprite>> _coverDownloadOverride;
		readonly Dictionary<string, CoverEntry> _covers = new Dictionary<string, CoverEntry>(StringComparer.Ordinal);
		readonly LinkedList<string> _coverLru = new LinkedList<string>();
		readonly Dictionary<string, PreviewEntry> _previews = new Dictionary<string, PreviewEntry>(StringComparer.Ordinal);
		readonly LinkedList<string> _previewLru = new LinkedList<string>();
		static readonly FieldInfo PreviewControllersField = typeof(SongPreviewPlayer).GetField(
			"_audioSourceControllers",
			BindingFlags.Instance | BindingFlags.NonPublic
		);
		static bool _previewProbeFailed;
		bool _disposed;

		internal sealed class CoverEntry {
			public string Key;
			public Sprite Sprite;
			public bool OwnsSprite;
			public Task<Sprite> LoadTask;
			public CancellationTokenSource RequestCancellation;
			public int LeaseCount;
			public LinkedListNode<string> LruNode;
			public bool Evicted;
		}

		sealed class PreviewEntry {
			public string Key;
			public AudioClip Clip;
			public Task<AudioClip> LoadTask;
			public CancellationTokenSource RequestCancellation;
			public LinkedListNode<string> LruNode;
			public DateTime LastIssuedUtc;
			public int Waiters;
			public bool Evicted;
		}

		/// <summary>
		/// A cover reference that keeps the corresponding cache entry pinned.
		/// Dispose it when the UI object stops using Sprite.
		/// </summary>
		public sealed class CoverLease : IDisposable {
			readonly SongAssetAsyncLoader _owner;
			CoverEntry _entry;
			int _released;

			internal CoverLease(SongAssetAsyncLoader owner, CoverEntry entry, Sprite sprite) {
				_owner = owner;
				_entry = entry;
				Sprite = sprite;
			}

			public Sprite Sprite { get; private set; }

			public void Dispose() {
				if(Interlocked.Exchange(ref _released, 1) != 0)
					return;

				var entry = Interlocked.Exchange(ref _entry, null);
				Sprite = null;
				if(entry != null)
					_owner.ReleaseCover(entry);
			}
		}

		public SongAssetAsyncLoader(Func<string, CancellationToken, Task<Sprite>> coverDownloadOverride = null) {
			_coverDownloadOverride = coverDownloadOverride;
			_disposeToken = _disposeCancellation.Token;
			try {
				_unityScheduler = TaskScheduler.FromCurrentSynchronizationContext();
			} catch {
				_unityScheduler = TaskScheduler.Current;
			}
		}

		CancellationTokenSource CreateRequestCancellation() {
			return CancellationTokenSource.CreateLinkedTokenSource(_disposeToken, CancellationToken.None);
		}

		static void CancelRequest(CancellationTokenSource request, string operation) {
			if(request == null)
				return;
			try {
				request.Cancel();
			} catch(Exception exception) {
				LogError("Failed to cancel " + operation, exception);
			}
		}

		static void LogWarning(string message) {
			try {
				if(Plugin.Log != null) {
					Plugin.Log.Warn(message);
					return;
				}
			} catch { }
			Debug.LogWarning("[BetterSongSearch] " + message);
		}

		static void LogError(string message, Exception exception = null) {
			var detail = exception == null ? message : message + ": " + exception;
			try {
				if(Plugin.Log != null) {
					Plugin.Log.Error(detail);
					return;
				}
			} catch { }
			Debug.LogError("[BetterSongSearch] " + detail);
		}

		static bool IsPreviewInUse(AudioClip clip) {
			if(clip == null)
				return false;

			try {
				var player = SelectedSongView.songPreviewPlayer;
				if(player == null)
					return false;
				if(player.activeAudioClip == clip)
					return true;
				if(PreviewControllersField == null)
					return false;

				var controllers = PreviewControllersField.GetValue(player) as Array;
				if(controllers == null)
					return false;
				foreach(var controller in controllers) {
					if(controller == null)
						continue;
					var audioField = controller.GetType().GetField(
						"audioSource",
						BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
					);
					var source = audioField?.GetValue(controller) as AudioSource;
					if(source != null && source.isPlaying && source.clip == clip)
						return true;
				}
			} catch(Exception exception) {
				if(!_previewProbeFailed) {
					_previewProbeFailed = true;
					LogError("Could not inspect SongPreviewPlayer while trimming previews", exception);
				}
			}
			return false;
		}

		void ThrowIfDisposed() {
			lock(_gate) {
				if(_disposed)
					throw new ObjectDisposedException(nameof(SongAssetAsyncLoader));
			}
		}

		static CancellationTokenSource LinkTokens(CancellationToken caller, CancellationToken owner) {
			return CancellationTokenSource.CreateLinkedTokenSource(caller, owner);
		}

		static string NormalizeHash(Song song) {
			if(String.IsNullOrWhiteSpace(song.hash))
				throw new ArgumentException("Song has no hash.", nameof(song));
			return song.hash.Trim().ToLowerInvariant();
		}

		static string TrimBaseUrl(string value, string fallback) {
			return (String.IsNullOrWhiteSpace(value) ? fallback : value).TrimEnd('/');
		}

		async Task<string> ApiRequest(string key, Func<JObject, string> valueGetter, CancellationToken token) {
			if(String.IsNullOrWhiteSpace(key))
				throw new ArgumentException("Map key is empty.", nameof(key));

			ThrowIfDisposed();
			var configured = PluginConfig.Instance == null ? null : PluginConfig.Instance.apiUrlOverride;
			var baseUrl = TrimBaseUrl(configured, BeatSaverRegionManager.detailsDownloadUrl);
			try {
				using(var linked = LinkTokens(token, _disposeToken)) {
					var bytes = await UnityWebrequestWrapper.DownloadBytes($"{baseUrl}/{key.Trim().ToLowerInvariant()}", linked.Token);
					if(bytes == null || bytes.Length == 0)
						throw new InvalidOperationException($"BeatSaver API returned no data for map '{key}'.");

					using(var jsonReader = new JsonTextReader(new StreamReader(new MemoryStream(bytes)))) {
						var json = new JsonSerializer().Deserialize<JObject>(jsonReader);
						if(json == null)
							throw new InvalidDataException($"BeatSaver API returned invalid JSON for map '{key}'.");
						var value = valueGetter(json);
						if(value == null)
							throw new InvalidDataException($"BeatSaver API response for map '{key}' omitted the requested field.");
						return value;
					}
				}
			} catch(OperationCanceledException) {
				throw;
			} catch(Exception exception) {
				LogError($"BeatSaver API request failed for map '{key}'", exception);
				throw;
			}
		}

		public Task<string> GetSongDescription(string key, CancellationToken token) {
			return ApiRequest(key, json => json.SelectToken("description", false)?.Value<string>(), token);
		}

		public Task<string> GetPreviewURL(string key, CancellationToken token) {
			return ApiRequest(key, json => json.SelectToken("versions[0].coverURL", false)?.Value<string>(), token);
		}

		async Task<Sprite> DownloadCover(string hash, CancellationToken requestToken) {
			await _coverDownloadSlots.WaitAsync(requestToken);
			try {
				try {
					if(_coverDownloadOverride != null)
						return await _coverDownloadOverride(hash, requestToken);

					var configured = PluginConfig.Instance == null ? null : PluginConfig.Instance.coverUrlOverride;
					var baseUrl = TrimBaseUrl(configured, BeatSaverRegionManager.coverDownloadUrl);
					var url = $"{baseUrl}/{hash}.jpg";
					return await UnityWebrequestWrapper.DownloadSprite(url, requestToken);
				} catch(OperationCanceledException) {
					throw;
				} catch(Exception exception) {
					LogError("Cover download failed for " + hash, exception);
					throw;
				}
			} finally {
				_coverDownloadSlots.Release();
			}
		}

		static void ObserveFault<T>(Task<T> task, string operation) {
			task.ContinueWith(completed => {
				try {
					var exception = completed.Exception == null ? null : completed.Exception.GetBaseException();
					if(!(exception is OperationCanceledException))
						LogError(operation, exception);
				} catch(Exception loggingException) {
					Debug.LogError("[BetterSongSearch] Failed to observe " + operation + ": " + loggingException);
				}
			}, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
		}

		static async Task<T> AwaitWithCancellation<T>(Task<T> task, CancellationToken token) {
			if(!token.CanBeCanceled)
				return await task;

			var cancellation = new TaskCompletionSource<bool>();
			using(token.Register(() => cancellation.TrySetResult(true))) {
				var completed = await Task.WhenAny(task, cancellation.Task);
				if(completed != task)
					throw new OperationCanceledException(token);
			}
			return await task;
		}

		void ObserveCoverCompletion(CoverEntry entry) {
			entry.LoadTask.ContinueWith(completed => {
				Sprite downloaded = null;
				try {
					downloaded = completed.Result;
					lock(_gate) {
						if(_disposed || entry.Evicted) {
							if(downloaded != null)
								DestroyCover(new CoverEntry { Sprite = downloaded, OwnsSprite = true });
							return;
						}
						if(entry.Sprite == null) {
							entry.Sprite = downloaded;
							entry.OwnsSprite = downloaded != null;
							AddCoverToLru(entry);
							if(downloaded == null && entry.LeaseCount == 0)
								RemoveCover(entry);
							else
								TrimCovers();
						} else if(downloaded != null && entry.Sprite != downloaded) {
							DestroyCover(new CoverEntry { Sprite = downloaded, OwnsSprite = true });
						}
					}
				} catch(Exception exception) {
					if(!(exception is OperationCanceledException))
						LogError("Cover completion could not be recorded for " + entry.Key, exception);
				}
			}, CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion | TaskContinuationOptions.ExecuteSynchronously, _unityScheduler);
		}

		void ObserveCoverFailure(CoverEntry entry) {
			entry.LoadTask.ContinueWith(completed => {
				lock(_gate) {
					if(!_disposed && entry.LeaseCount == 0 && _covers.TryGetValue(entry.Key, out var current) && ReferenceEquals(current, entry))
						RemoveCover(entry);
				}
			}, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, _unityScheduler);
		}

		void AddCoverToLru(CoverEntry entry) {
			if(entry.LruNode != null)
				_coverLru.Remove(entry.LruNode);
			entry.LruNode = _coverLru.AddLast(entry.Key);
		}

		void DestroyCover(CoverEntry entry) {
			if(!entry.OwnsSprite || entry.Sprite == null)
				return;

			var sprite = entry.Sprite;
			var texture = sprite.texture;
			entry.Sprite = null;
			entry.OwnsSprite = false;
			try {
				UnityEngine.Object.Destroy(sprite);
				if(texture != null)
					UnityEngine.Object.Destroy(texture);
			} catch(Exception exception) {
				LogError("Failed to release a cover asset", exception);
			}
		}

		void RemoveCover(CoverEntry entry) {
			if(entry.LruNode != null) {
				_coverLru.Remove(entry.LruNode);
				entry.LruNode = null;
			}
			if(_covers.TryGetValue(entry.Key, out var current) && ReferenceEquals(current, entry))
				_covers.Remove(entry.Key);
			entry.Evicted = true;
			if(entry.LeaseCount == 0)
				DestroyCover(entry);
		}

		void TrimCovers() {
			if(_covers.Count <= MaxCoverCacheEntries)
				return;

			var node = _coverLru.First;
			while(_covers.Count > MaxCoverCacheEntries && node != null) {
				var next = node.Next;
				if(_covers.TryGetValue(node.Value, out var entry) && entry.LeaseCount == 0 && entry.LoadTask != null && entry.LoadTask.IsCompleted)
					RemoveCover(entry);
				node = next;
			}
		}

		void ReleaseCover(CoverEntry entry) {
			CancellationTokenSource cancel = null;
			lock(_gate) {
				if(entry.LeaseCount > 0)
					entry.LeaseCount--;

				if(_disposed || entry.Evicted) {
					if(entry.LeaseCount == 0) {
						if(entry.Sprite == null && entry.LoadTask != null && !entry.LoadTask.IsCompleted)
							cancel = entry.RequestCancellation;
						DestroyCover(entry);
					}
				} else if(entry.LeaseCount == 0 && entry.Sprite == null && entry.LoadTask != null && !entry.LoadTask.IsCompleted) {
					// No caller can consume this request anymore. Evict the
					// entry before canceling so a late completion cannot publish
					// its result into a newly created entry for the same hash.
					RemoveCover(entry);
					cancel = entry.RequestCancellation;
				} else {
					// A failed request only keeps the shared fallback alive for
					// the callers already waiting on it. A later request may retry.
					if(entry.LeaseCount == 0 && !entry.OwnsSprite && entry.LoadTask != null && entry.LoadTask.IsCompleted)
						RemoveCover(entry);
					else
						TrimCovers();
				}
			}
			CancelRequest(cancel, "cover request");
		}

		public async Task<CoverLease> AcquireCoverAsync(Song song, CancellationToken token) {
			var hash = NormalizeHash(song);
			ThrowIfDisposed();

			CoverEntry entry;
			lock(_gate) {
				ThrowIfDisposed();
				if(!_covers.TryGetValue(hash, out entry)) {
					entry = new CoverEntry {
						Key = hash,
						RequestCancellation = CreateRequestCancellation(),
						LeaseCount = 1
					};
					_covers.Add(hash, entry);
					AddCoverToLru(entry);
					entry.LoadTask = DownloadCover(hash, entry.RequestCancellation.Token);
					ObserveFault(entry.LoadTask, "Cover load failed for " + hash);
					ObserveCoverCompletion(entry);
					ObserveCoverFailure(entry);
				} else {
					entry.LeaseCount++;
					AddCoverToLru(entry);
				}
			}

			try {
				using(var linked = LinkTokens(token, _disposeToken)) {
					linked.Token.ThrowIfCancellationRequested();
					Sprite downloaded;
					lock(_gate)
						downloaded = entry.Sprite;
					if(downloaded == null)
						downloaded = await AwaitWithCancellation(entry.LoadTask, linked.Token);

					lock(_gate) {
						if(_disposed || entry.Evicted)
							throw new OperationCanceledException(_disposeToken);

						if(entry.Sprite == null) {
							entry.Sprite = downloaded ?? GetDefaultCover();
							entry.OwnsSprite = downloaded != null;
							if(downloaded == null)
								LogWarning("Cover download returned no image for " + hash + "; using the default cover.");
						}
						AddCoverToLru(entry);
						TrimCovers();
						return new CoverLease(this, entry, entry.Sprite);
					}
				}
			} catch {
				ReleaseCover(entry);
				throw;
			}
		}

		static Sprite GetDefaultCover() {
			try {
				return SongCore.Loader.defaultCoverImage;
			} catch(Exception exception) {
				LogError("Unable to obtain SongCore's default cover", exception);
				return null;
			}
		}

		async Task<AudioClip> DownloadPreview(string hash, CancellationToken requestToken) {
			var configured = PluginConfig.Instance == null ? null : PluginConfig.Instance.previewUrlOverride;
			var baseUrl = TrimBaseUrl(configured, BeatSaverRegionManager.previewDownloadUrl);
			var url = $"{baseUrl}/{hash}.mp3";
			try {
				return await UnityWebrequestWrapper.DownloadAudio(url, requestToken, AudioType.MPEG);
			} catch(OperationCanceledException) {
				throw;
			} catch(Exception exception) {
				LogError("Preview download failed for " + hash, exception);
				throw;
			}
		}

		void ObservePreviewCompletion(PreviewEntry entry) {
			entry.LoadTask.ContinueWith(completed => {
				try {
					var clip = completed.Result;
					lock(_gate) {
						if(clip == null || clip.loadState != AudioDataLoadState.Loaded) {
							if(_previews.TryGetValue(entry.Key, out var current) && ReferenceEquals(current, entry))
								RemovePreview(entry);
							else
								DestroyAudioClip(clip);
							return;
						}
						if(_disposed || entry.Evicted) {
							DestroyAudioClip(clip);
							return;
						}
						if(entry.Clip == null) {
							entry.Clip = clip;
							AddPreviewToLru(entry);
							TrimPreviews();
						} else if(entry.Clip != clip) {
							DestroyAudioClip(clip);
						}
					}
				} catch(Exception exception) {
					if(!(exception is OperationCanceledException))
						LogError("Preview completion could not be recorded for " + entry.Key, exception);
				}
			}, CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion | TaskContinuationOptions.ExecuteSynchronously, _unityScheduler);
		}

		void ObservePreviewFailure(PreviewEntry entry) {
			entry.LoadTask.ContinueWith(completed => {
				lock(_gate) {
					if(!_disposed && entry.Clip == null && _previews.TryGetValue(entry.Key, out var current) && ReferenceEquals(current, entry))
						RemovePreview(entry);
				}
			}, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, _unityScheduler);
		}

		void AddPreviewToLru(PreviewEntry entry) {
			if(entry.LruNode != null)
				_previewLru.Remove(entry.LruNode);
			entry.LruNode = _previewLru.AddLast(entry.Key);
		}

		void DestroyPreview(PreviewEntry entry) {
			if(entry.Clip == null)
				return;
			var clip = entry.Clip;
			entry.Clip = null;
			try {
				// Flow cleanup crossfades the shared player to its default clip
				// before disposing this loader. Keep a fading source alive long
				// enough for that transition to finish.
				UnityEngine.Object.Destroy(clip, IsPreviewInUse(clip) ? 5f : 0f);
			} catch(Exception exception) {
				LogError("Failed to release a preview asset", exception);
			}
		}

		void RemovePreview(PreviewEntry entry) {
			if(entry.LruNode != null) {
				_previewLru.Remove(entry.LruNode);
				entry.LruNode = null;
			}
			if(_previews.TryGetValue(entry.Key, out var current) && ReferenceEquals(current, entry))
				_previews.Remove(entry.Key);
			entry.Evicted = true;
			DestroyPreview(entry);
		}

		void TrimPreviews() {
			var node = _previewLru.First;
			while(_previews.Count > MaxPreviewCacheEntries && node != null) {
				var next = node.Next;
				if(_previews.TryGetValue(node.Value, out var entry) && entry.Clip != null && entry.LoadTask != null && entry.LoadTask.IsCompleted &&
					entry.Waiters == 0 && DateTime.UtcNow - entry.LastIssuedUtc >= PreviewHandoffGrace && !IsPreviewInUse(entry.Clip))
					RemovePreview(entry);
				node = next;
			}
		}

		public async Task<AudioClip> LoadPreviewAsync(Song song, CancellationToken token) {
			var hash = NormalizeHash(song);
			ThrowIfDisposed();

			PreviewEntry entry;
			lock(_gate) {
				ThrowIfDisposed();
				if(!_previews.TryGetValue(hash, out entry)) {
					entry = new PreviewEntry {
						Key = hash,
						RequestCancellation = CreateRequestCancellation()
					};
					_previews.Add(hash, entry);
					AddPreviewToLru(entry);
					entry.LoadTask = DownloadPreview(hash, entry.RequestCancellation.Token);
					ObserveFault(entry.LoadTask, "Preview load failed for " + hash);
					ObservePreviewCompletion(entry);
					ObservePreviewFailure(entry);
				} else {
					AddPreviewToLru(entry);
				}
			}

			bool waiting = false;
			CancellationTokenSource cancelRequest = null;
			try {
				using(var linked = LinkTokens(token, _disposeToken)) {
					linked.Token.ThrowIfCancellationRequested();
					lock(_gate) {
						if(_disposed || entry.Evicted)
							throw new OperationCanceledException(_disposeToken);
						if(entry.Clip != null) {
							AddPreviewToLru(entry);
							entry.LastIssuedUtc = DateTime.UtcNow;
							return entry.Clip;
						}
						entry.Waiters++;
						waiting = true;
					}

					AudioClip clip = await AwaitWithCancellation(entry.LoadTask, linked.Token);

					if(clip == null || clip.loadState != AudioDataLoadState.Loaded) {
						LogWarning("Preview download returned an unloaded clip for " + hash + ".");
						lock(_gate) {
							if(_previews.TryGetValue(hash, out var current) && ReferenceEquals(current, entry))
								RemovePreview(entry);
						}
						return null;
					}

					lock(_gate) {
						if(_disposed || entry.Evicted) {
							DestroyAudioClip(clip);
							throw new OperationCanceledException(_disposeToken);
						}
						entry.Clip = clip;
						entry.LastIssuedUtc = DateTime.UtcNow;
						AddPreviewToLru(entry);
						TrimPreviews();
						return entry.Clip;
					}
				}
			} catch {
				lock(_gate) {
					if(!_disposed && entry.Clip == null && entry.LoadTask != null && entry.LoadTask.IsCompleted && (entry.LoadTask.IsFaulted || entry.LoadTask.IsCanceled))
						RemovePreview(entry);
				}
				throw;
			} finally {
				if(waiting) {
					lock(_gate) {
						if(entry.Waiters > 0)
							entry.Waiters--;
						if(entry.Waiters == 0 && entry.Clip == null && entry.LoadTask != null && !entry.LoadTask.IsCompleted &&
							!entry.Evicted && _previews.TryGetValue(entry.Key, out var current) && ReferenceEquals(current, entry)) {
							RemovePreview(entry);
							cancelRequest = entry.RequestCancellation;
						}
					}
					CancelRequest(cancelRequest, "preview request");
				}
			}
		}

		static void DestroyAudioClip(AudioClip clip) {
			if(clip == null)
				return;
			try {
				UnityEngine.Object.Destroy(clip);
			} catch(Exception exception) {
				LogError("Failed to release a preview asset", exception);
			}
		}

		public void Dispose() {
			List<CoverEntry> covers;
			List<PreviewEntry> previews;
			lock(_gate) {
				if(_disposed)
					return;
				_disposed = true;

				covers = _covers.Values.ToList();
				previews = _previews.Values.ToList();
				_covers.Clear();
				_coverLru.Clear();
				_previews.Clear();
				_previewLru.Clear();
				foreach(var entry in covers) {
					entry.Evicted = true;
					entry.LruNode = null;
					if(entry.LeaseCount == 0)
						DestroyCover(entry);
				}
				foreach(var entry in previews) {
					entry.Evicted = true;
					entry.LruNode = null;
					DestroyPreview(entry);
				}
			}

			try {
				_disposeCancellation.Cancel();
			} catch(Exception exception) {
				LogError("Failed to cancel song asset requests", exception);
			}

			// Keep the source alive until any racing linked-token creation has
			// observed cancellation. It owns no native handle unless a caller
			// explicitly requests one, and the loader becomes unreachable after
			// the flow releases it.
		}
	}
}
