using System.Text;
using Windows.Media.Playback;
using WinMediaSource = Windows.Media.Core.MediaSource;

namespace CommunityToolkit.Maui.DeviceTests;

// TEMPORARY diagnostic (never merged): reports what Source = null does on a sourceless and on a sourced MediaPlayer. It always fails so that the report is in the results.
[Category("R5winDiag")]
[Collection(TestCollections.DisableParallelization)]
public class R5winDiagWindowsTests : WinMediaTestBase
{
	[Fact]
	public async Task ReportWhatSourceNullDoes()
	{
		await RequireMediaPlayerAsync();
		var report = new StringBuilder();

		report.AppendLine(await ObserveAsync("A sourceless, Source = null", false, false, false));
		report.AppendLine(await ObserveAsync("A2 sourceless, no assignment", false, false, true));
		report.AppendLine(await ObserveAsync("B opened local asset, Source = null", true, false, false));
		report.AppendLine(await ObserveAsync("C opened local asset, attached MediaSource disposed first, then Source = null", true, true, false));
		report.AppendLine(await StressAsync("D1 1000x new, Source = null, Dispose", true));
		report.AppendLine(await StressAsync("D2 1000x new, Dispose", false));

		Console.WriteLine(report.ToString());
		Assert.Fail("R5WIN-DIAG REPORT" + Environment.NewLine + report);
	}

	async Task<string> ObserveAsync(string name, bool openAsset, bool disposeSourceFirst, bool skipAssignment)
	{
		var events = new List<string>();
		var clock = System.Diagnostics.Stopwatch.StartNew();
		void Log(string text)
		{
			lock (events)
			{
				events.Add($"{clock.ElapsedMilliseconds}ms {text}");
			}
		}

		MediaPlayer? player = null;
		WinMediaSource? source = null;
		await InvokeOnMainThreadAsync(() =>
		{
			player = new MediaPlayer { AutoPlay = false };
			player.SourceChanged += (_, _) => Log("SourceChanged");
			player.MediaFailed += (_, args) => Log($"MediaFailed {args.Error} {args.ExtendedErrorCode?.Message ?? args.ErrorMessage}");
			player.MediaOpened += (_, _) => Log("MediaOpened");
			if (openAsset)
			{
				source = WinMediaSource.CreateFromUri(WinMediaAssets.VideoUri);
				player.Source = new MediaPlaybackItem(source);
			}
		});

		if (openAsset)
		{
			await Task.Delay(1500);
		}

		clock.Restart();
		await InvokeOnMainThreadAsync(() =>
		{
			if (disposeSourceFirst)
			{
				source?.Dispose();
			}

			if (!skipAssignment)
			{
				player!.Source = null;
			}
		});

		await Task.Delay(3000);
		var state = "";
		await InvokeOnMainThreadAsync(() =>
		{
			state = player!.PlaybackSession.PlaybackState.ToString();
			source?.Dispose();
			player.Dispose();
		});

		lock (events)
		{
			return $"{name}: state={state}; events=[{string.Join("; ", events)}]";
		}
	}

	async Task<string> StressAsync(string name, bool assignNull)
	{
		var failures = 0;
		var clock = System.Diagnostics.Stopwatch.StartNew();
		for (var i = 0; i < 1000; i++)
		{
			await InvokeOnMainThreadAsync(() =>
			{
				var player = new MediaPlayer();
				player.MediaFailed += (_, _) => failures++;
				if (assignNull)
				{
					player.Source = null;
				}

				player.Dispose();
			});
		}

		return $"{name}: MediaFailed events before dispose={failures}; {clock.ElapsedMilliseconds}ms";
	}
}
