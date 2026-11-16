using System.Diagnostics;
using System.Globalization;
using Windows.Media;
using Windows.Media.Playback;
using Windows.Storage.Streams;

namespace CommunityToolkit.Maui.DeviceTests;

// DIAG ONLY: never merge.
[Category(TestCategory.MediaMetadataWindows)]
[Collection(TestCollections.DisableParallelization)]
public class WinMediaThumbnailDiagWindowsTests : WinMediaTestBase
{
	const int iterations = 50;

	sealed class Tally
	{
		public int Reads;
		public int Failures;
		public int RecoveredOnReRead;
		public int ExtraGets;
		public HashSet<string> UserAgents = [];
	}

	[Fact]
	public async Task ThumbnailReadStressDiagnostic()
	{
		await RequireMediaPlayerAsync();

		foreach (var scenario in new[] { "A-update-one-read", "B-no-update-one-read", "C-update-two-concurrent-reads-same-reference", "D-update-two-references-same-url" })
		{
			var tally = new Tally();
			for (var iteration = 0; iteration < iterations; iteration++)
			{
				await RunIterationAsync(scenario, iteration, tally);
			}

			Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"[WINPROBE-DIAG] scenario={scenario} reads={tally.Reads} failures={tally.Failures} recoveredOnReRead={tally.RecoveredOnReRead} extraGets={tally.ExtraGets} userAgents={string.Join(" | ", tally.UserAgents)}"));
		}
	}

	static async Task RunIterationAsync(string scenario, int iteration, Tally tally)
	{
		await using var server = WinMediaAssets.StartArtworkServer();
		var uri = server.UriFor(WinMediaAssets.ArtworkServerPath);
		MediaPlayer? player = null;
		var sinceUpdate = new Stopwatch();
		try
		{
			var references = await TestDispatcher.Current.DispatchAsync(() =>
			{
				player = new MediaPlayer();
				var updater = player.SystemMediaTransportControls.DisplayUpdater;
				updater.Type = MediaPlaybackType.Music;
				updater.MusicProperties.Title = "diag-title";
				updater.Thumbnail = RandomAccessStreamReference.CreateFromUri(uri);
				if (!scenario.StartsWith("B", StringComparison.Ordinal))
				{
					updater.Update();
				}

				sinceUpdate.Restart();
				var first = updater.Thumbnail;
				return (First: first, Second: scenario.StartsWith("D", StringComparison.Ordinal) ? RandomAccessStreamReference.CreateFromUri(uri) : first);
			});

			var concurrent = scenario.StartsWith("C", StringComparison.Ordinal) || scenario.StartsWith("D", StringComparison.Ordinal);
			var attempts = concurrent
				? new[] { ReadAsync(references.First), ReadAsync(references.Second) }
				: [ReadAsync(references.First)];
			var readsMade = attempts.Length;
			tally.Reads += readsMade;

			foreach (var attempt in attempts)
			{
				var failure = await attempt;
				if (failure is null)
				{
					continue;
				}

				tally.Failures++;
				var recovered = await ReadAsync(references.First) is null;
				tally.RecoveredOnReRead += recovered ? 1 : 0;
				Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"[WINPROBE-DIAG] failure scenario={scenario} iteration={iteration} {failure.GetType().Name} HRESULT=0x{failure.HResult:X8} afterUpdateMs={sinceUpdate.ElapsedMilliseconds} gets={server.Requests.Count} requests={server.DescribeRequests()} reReadRecovered={recovered}"));
			}

			var gets = server.Requests.Where(request => request.Method is "GET").ToList();
			tally.ExtraGets += Math.Max(0, gets.Count - readsMade);
			foreach (var request in gets)
			{
				tally.UserAgents.Add(request.Header("User-Agent") ?? "none");
			}
		}
		finally
		{
			if (player is not null)
			{
				await TestDispatcher.Current.DispatchAsync(player.Dispose);
			}
		}
	}

	static async Task<Exception?> ReadAsync(IRandomAccessStreamReference? reference)
	{
		try
		{
			await WinMediaAssets.ReadAllBytesAsync(reference!).WaitAsync(TimeSpan.FromSeconds(15));
			return null;
		}
		catch (Exception ex)
		{
			return ex;
		}
	}
}
