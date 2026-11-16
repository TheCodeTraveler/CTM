using CommunityToolkit.Maui.Core.Platform;
using UIKit;

namespace CommunityToolkit.Maui.DeviceTests;

// Temporary probe for the iOS 15.5 rotation race: never part of the PR.
[Category("R5SBProbe")]
[Collection(TestCollections.DisableParallelization)]
public class StatusBarRotationProbe : StatusBarTestBase
{
	[Fact]
	public async Task RotationCyclesLeaveNoStaleStatusBarView()
	{
		SetupBuilder();
		await ResetNativeStateAsync();
		await InvokeOnMainThreadAsync(static () =>
		{
			StatusBar.SetColor(Colors.Red);
			StatusBar.SetBarSize(false);
		});

		var stuck = new List<string>();
		try
		{
			for (var cycle = 0; cycle < 60; cycle++)
			{
				foreach (var orientation in new[] { UIInterfaceOrientationMask.LandscapeRight, UIInterfaceOrientationMask.Portrait })
				{
					await RotateAsync(orientation);
					var start = DateTime.UtcNow;
					while ((DateTime.UtcNow - start).TotalMilliseconds < 1500)
					{
						var line = await InvokeOnMainThreadAsync(() => Describe(cycle, orientation));
						Console.WriteLine($"R5SBPROBE {(DateTime.UtcNow - start).TotalMilliseconds:0} {line.Text}");
						await Task.Delay(50);
					}

					var final = await InvokeOnMainThreadAsync(() => Describe(cycle, orientation));
					if (final.IsStale)
					{
						stuck.Add($"cycle {cycle} {orientation}: {final.Text}");
					}
				}
			}
		}
		finally
		{
			await RotateAsync(UIInterfaceOrientationMask.Portrait);
		}

		Assert.True(stuck.Count == 0, $"{stuck.Count} stuck rotations of 120:{Environment.NewLine}{string.Join(Environment.NewLine, stuck)}");
	}

	static (bool IsStale, string Text) Describe(int cycle, UIInterfaceOrientationMask orientation)
	{
		var isStale = false;
		var parts = new List<string>();
		foreach (var window in IosStatusBarViews.WindowsWithStatusBar())
		{
			var view = IosStatusBarViews.TaggedSubviews(window).FirstOrDefault();
			var expected = ExpectedStatusBarViewFrame(window, false);
			var stale = view is null || view.Frame != expected;
			isStale |= stale;
			parts.Add(FormattableString.Invariant($"[{window.GetType().Name} hidden={window.Hidden} bounds={window.Bounds} insets={window.SafeAreaInsets} bar={IosStatusBarViews.SceneStatusBarFrame(window)} view={view?.Frame} stale={stale}]"));
		}

		return (isStale, FormattableString.Invariant($"c{cycle} {orientation} {string.Join(' ', parts)}"));
	}
}
