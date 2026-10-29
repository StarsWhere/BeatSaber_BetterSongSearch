using BetterSongSearch.UI;
using BetterSongSearch.Util;
using SongDetailsCache.Structs;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace BetterSongSearchSmoke {
	/// <summary>
	/// Bounded, network-free checks for the loader's cover ownership and request
	/// identity rules. The injected downloader is still called behind the
	/// production semaphore, so these checks exercise the real loader state
	/// machine rather than a copy of it.
	/// </summary>
	internal static class AssetChecks {
		const int WaitMilliseconds = 3000;

		sealed class CheckResult {
			public readonly bool Passed;
			public readonly string Detail;

			public CheckResult(bool passed, string detail) {
				Passed = passed;
				Detail = detail;
			}
		}

		sealed class PendingDownload {
			public readonly TaskCompletionSource<Sprite> Completion = new TaskCompletionSource<Sprite>();
		}

		sealed class DownloadHarness {
			readonly object _gate = new object();
			readonly Dictionary<string, List<PendingDownload>> _calls = new Dictionary<string, List<PendingDownload>>(StringComparer.OrdinalIgnoreCase);
			int _active;
			int _maximumActive;
			int _cancellations;

			public int Active {
				get { lock(_gate) return _active; }
			}

			public int MaximumActive {
				get { lock(_gate) return _maximumActive; }
			}

			public int CancellationCount {
				get { lock(_gate) return _cancellations; }
			}

			public int CallCount {
			get {
				lock(_gate)
					return _calls.Values.Sum(values => values.Count);
			}
		}

			public Task<Sprite> DownloadAsync(string hash, CancellationToken token) {
			return DownloadCoreAsync(hash, token);
		}

			async Task<Sprite> DownloadCoreAsync(string hash, CancellationToken token) {
				var call = new PendingDownload();
				lock(_gate) {
					if(!_calls.TryGetValue(hash, out var values))
						_calls.Add(hash, values = new List<PendingDownload>());
					values.Add(call);
					_active++;
					_maximumActive = Math.Max(_maximumActive, _active);
				}

				var cancelled = new TaskCompletionSource<bool>();
				try {
					using(token.Register(() => cancelled.TrySetResult(true))) {
						var completed = await Task.WhenAny(call.Completion.Task, cancelled.Task);
						if(completed != call.Completion.Task) {
							lock(_gate)
								_cancellations++;
							throw new OperationCanceledException(token);
						}
					}
					return await call.Completion.Task;
				} finally {
					lock(_gate)
						_active--;
				}
			}

			public PendingDownload GetCall(string hash, int index = 0) {
				lock(_gate) {
					if(!_calls.TryGetValue(hash, out var values) || values.Count <= index)
						return null;
					return values[index];
				}
			}

			public PendingDownload[] GetCalls() {
				lock(_gate)
					return _calls.Values.SelectMany(values => values).ToArray();
			}

			public void Complete(PendingDownload call, Sprite sprite) {
				if(call != null)
					call.Completion.TrySetResult(sprite);
			}
		}

		sealed class SpriteRegistry {
			sealed class OwnedSprite {
				public Sprite Sprite;
				public Texture2D Texture;
				public bool ClaimedByLoader;
			}

			readonly List<OwnedSprite> _sprites = new List<OwnedSprite>();

			public Sprite Create() {
				var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
				var sprite = Sprite.Create(texture, new Rect(0, 0, 2, 2), new Vector2(.5f, .5f));
				_sprites.Add(new OwnedSprite { Sprite = sprite, Texture = texture });
				return sprite;
			}

			public void Claim(Sprite sprite) {
			var owned = _sprites.FirstOrDefault(value => ReferenceEquals(value.Sprite, sprite));
			if(owned != null)
				owned.ClaimedByLoader = true;
			}

			public void CleanupUnclaimed() {
			foreach(var owned in _sprites) {
				if(owned.ClaimedByLoader)
					continue;
				try {
					if(owned.Sprite != null)
						UnityEngine.Object.Destroy(owned.Sprite);
					if(owned.Texture != null)
						UnityEngine.Object.Destroy(owned.Texture);
				} catch(Exception exception) {
					Debug.LogError("[BetterSongSearch] Asset smoke sprite cleanup failed: " + exception);
				}
			}
			_sprites.Clear();
			}
		}

		public static async Task RunAsync(Action<string, bool, string> check) {
			if(check == null)
				throw new ArgumentNullException(nameof(check));

			var fixtures = FindFixtures(3);
			if(fixtures.Length < 3) {
				check("cover asset fixtures", false, "at least three distinct live songs are required; found " + fixtures.Length);
				return;
			}

			var merge = await Safe(() => CheckHashMergeAsync(fixtures[0]));
			check("cover hash merge", merge.Passed, merge.Detail);

			var slots = await Safe(() => CheckCoverSlotsAsync(fixtures));
			check("cover download slots", slots.Passed, slots.Detail);

			var cancellation = await Safe(() => CheckLastSubscriberCancellationAsync(fixtures[0]));
			check("last cover subscriber cancellation", cancellation.Passed, cancellation.Detail);

			var stale = await Safe(() => CheckLateResponseIsolationAsync(fixtures[0]));
			check("late cover response isolation", stale.Passed, stale.Detail);

			
		}

		static Song[] FindFixtures(int count) {
			return (BSSFlowCoordinator.songsList ?? new SongSearchSong[0])
				.Where(song => song != null && !String.IsNullOrWhiteSpace(song.detailsSong.hash))
				.GroupBy(song => song.detailsSong.hash, StringComparer.OrdinalIgnoreCase)
				.Select(group => group.First().detailsSong)
				.Take(count)
				.ToArray();
		}

		static async Task<CheckResult> Safe(Func<Task<CheckResult>> operation) {
			try {
				return await operation();
			} catch(Exception exception) {
				return new CheckResult(false, exception.ToString());
			}
		}

		static async Task<bool> WaitUntil(Func<bool> predicate) {
			var deadline = DateTime.UtcNow.AddMilliseconds(WaitMilliseconds);
			while(DateTime.UtcNow < deadline) {
				if(predicate())
					return true;
				await Task.Delay(10);
			}
			return predicate();
		}

		static async Task<T> AwaitWithTimeout<T>(Task<T> task) {
			var timeout = Task.Delay(WaitMilliseconds);
			var completed = await Task.WhenAny(task, timeout);
			if(completed != task)
				throw new TimeoutException("asset check task exceeded " + WaitMilliseconds + "ms");
			return await task;
		}

		static async Task AwaitWithTimeout(Task task) {
			var timeout = Task.Delay(WaitMilliseconds);
			var completed = await Task.WhenAny(task, timeout);
			if(completed != task)
				throw new TimeoutException("asset check task exceeded " + WaitMilliseconds + "ms");
			await task;
		}

		static async Task<Exception> Observe(Task task) {
			try {
				await AwaitWithTimeout(task);
				return null;
			} catch(Exception exception) {
				return exception;
			}
		}

		static async Task ObserveAll(IEnumerable<Task> tasks) {
			foreach(var task in tasks.Where(value => value != null)) {
				var exception = await Observe(task);
				if(exception != null && !(exception is OperationCanceledException))
					Debug.LogError("[BetterSongSearch] Asset smoke task failed during cleanup: " + exception);
			}
		}

		static async Task<CheckResult> CheckHashMergeAsync(Song song) {
			var harness = new DownloadHarness();
			var sprites = new SpriteRegistry();
			var loader = new SongAssetAsyncLoader(harness.DownloadAsync);
			var leases = new List<SongAssetAsyncLoader.CoverLease>();
			var tasks = new[] {
				loader.AcquireCoverAsync(song, CancellationToken.None),
				loader.AcquireCoverAsync(song, CancellationToken.None)
			};
			try {
				var started = await WaitUntil(() => harness.CallCount == 1);
				var call = harness.GetCall(song.hash);
				var sprite = sprites.Create();
				harness.Complete(call, sprite);
				var first = await AwaitWithTimeout(tasks[0]);
				var second = await AwaitWithTimeout(tasks[1]);
				leases.Add(first);
				leases.Add(second);
				sprites.Claim(sprite);
				return new CheckResult(started && harness.CallCount == 1 && ReferenceEquals(first.Sprite, second.Sprite),
					"calls=" + harness.CallCount + ", sharedSprite=" + ReferenceEquals(first.Sprite, second.Sprite));
			} finally {
				foreach(var lease in leases)
					lease.Dispose();
				loader.Dispose();
				await ObserveAll(tasks);
				sprites.CleanupUnclaimed();
			}
		}

		static async Task<CheckResult> CheckCoverSlotsAsync(Song[] songs) {
			var harness = new DownloadHarness();
			var sprites = new SpriteRegistry();
			var loader = new SongAssetAsyncLoader(harness.DownloadAsync);
			var tasks = songs.Take(3).Select(song => loader.AcquireCoverAsync(song, CancellationToken.None)).ToArray();
			var leases = new List<SongAssetAsyncLoader.CoverLease>();
			try {
				var twoStarted = await WaitUntil(() => harness.CallCount >= 2);
				var callsBeforeRelease = harness.CallCount;
				var calls = harness.GetCalls();
				if(calls.Length > 0)
					harness.Complete(calls[0], sprites.Create());
				var threeStarted = await WaitUntil(() => harness.CallCount >= 3);
				foreach(var call in harness.GetCalls())
					if(!call.Completion.Task.IsCompleted)
						harness.Complete(call, sprites.Create());

				foreach(var task in tasks) {
					var lease = await AwaitWithTimeout(task);
					leases.Add(lease);
					sprites.Claim(lease.Sprite);
				}

				var passed = twoStarted && callsBeforeRelease == 2 && threeStarted && harness.MaximumActive <= 2;
				return new CheckResult(passed,
					"startedBeforeRelease=" + callsBeforeRelease + ", totalCalls=" + harness.CallCount + ", maxActive=" + harness.MaximumActive);
			} finally {
				foreach(var lease in leases)
					lease.Dispose();
				loader.Dispose();
				await ObserveAll(tasks);
				sprites.CleanupUnclaimed();
			}
		}

		static async Task<CheckResult> CheckLastSubscriberCancellationAsync(Song song) {
			var harness = new DownloadHarness();
			var loader = new SongAssetAsyncLoader(harness.DownloadAsync);
			var firstCancellation = new CancellationTokenSource();
			var secondCancellation = new CancellationTokenSource();
			var tasks = new[] {
				loader.AcquireCoverAsync(song, firstCancellation.Token),
				loader.AcquireCoverAsync(song, secondCancellation.Token)
			};
			try {
				var started = await WaitUntil(() => harness.CallCount == 1);
				firstCancellation.Cancel();
				var firstError = await Observe(tasks[0]);
				await Task.Delay(50);
				var requestKeptForSecond = harness.CancellationCount == 0 && harness.Active == 1;
				secondCancellation.Cancel();
				var secondError = await Observe(tasks[1]);
				var requestCanceled = await WaitUntil(() => harness.CancellationCount >= 1);
				var passed = started && firstError is OperationCanceledException && requestKeptForSecond &&
					secondError is OperationCanceledException && requestCanceled;
				return new CheckResult(passed,
					"first=" + (firstError?.GetType().Name ?? "none") + ", second=" + (secondError?.GetType().Name ?? "none") +
					", cancellations=" + harness.CancellationCount);
			} finally {
				firstCancellation.Dispose();
				secondCancellation.Dispose();
				loader.Dispose();
				await ObserveAll(tasks);
			}
		}

		static async Task<CheckResult> CheckLateResponseIsolationAsync(Song song) {
			var harness = new DownloadHarness();
			var sprites = new SpriteRegistry();
			var loader = new SongAssetAsyncLoader(harness.DownloadAsync);
			var oldCancellation = new CancellationTokenSource();
			var oldTask = loader.AcquireCoverAsync(song, oldCancellation.Token);
			Task<SongAssetAsyncLoader.CoverLease> newTask = null;
			var leases = new List<SongAssetAsyncLoader.CoverLease>();
			try {
				var oldStarted = await WaitUntil(() => harness.CallCount == 1);
				var oldCall = harness.GetCall(song.hash, 0);
				oldCancellation.Cancel();
				var oldError = await Observe(oldTask);
				var canceled = await WaitUntil(() => harness.CancellationCount >= 1);

				newTask = loader.AcquireCoverAsync(song, CancellationToken.None);
				var newStarted = await WaitUntil(() => harness.CallCount == 2);
				var newCall = harness.GetCall(song.hash, 1);
				var oldSprite = sprites.Create();
				harness.Complete(oldCall, oldSprite);
				var newSprite = sprites.Create();
				harness.Complete(newCall, newSprite);
				var newLease = await AwaitWithTimeout(newTask);
				leases.Add(newLease);
				sprites.Claim(newLease.Sprite);

				var passed = oldStarted && oldError is OperationCanceledException && canceled && newStarted &&
					ReferenceEquals(newLease.Sprite, newSprite) && !ReferenceEquals(newLease.Sprite, oldSprite);
				return new CheckResult(passed,
					"old=" + (oldError?.GetType().Name ?? "none") + ", calls=" + harness.CallCount + ", replacement=" + ReferenceEquals(newLease.Sprite, newSprite));
			} finally {
				oldCancellation.Dispose();
				foreach(var lease in leases)
					lease.Dispose();
				loader.Dispose();
				var tasks = newTask == null ? new[] { oldTask } : new Task[] { oldTask, newTask };
				await ObserveAll(tasks);
				sprites.CleanupUnclaimed();
			}
		}

		
	}
}
