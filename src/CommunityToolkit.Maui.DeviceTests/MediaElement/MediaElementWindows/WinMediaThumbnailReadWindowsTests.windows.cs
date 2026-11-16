using Windows.Storage.Streams;

namespace CommunityToolkit.Maui.DeviceTests;

[Category(TestCategory.MediaMetadataWindows)]
[Collection(TestCollections.DisableParallelization)]
public class WinMediaThumbnailReadWindowsTests : WinMediaTestBase
{
	[Fact]
	public async Task DisplayUpdaterProbeFailsInsteadOfSkippingWhenItsOwnArtworkCannotBeReadBack()
	{
		await RequireDisplayUpdaterAsync();
		var reads = 0;

		var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => WinMediaProbe.ProbeDisplayUpdaterAsync(_ =>
		{
			reads++;
			throw new FileNotFoundException("Unable to find the specified file.");
		}));

		Assert.IsType<FileNotFoundException>(failure.InnerException);
		Assert.True(reads > 0);
	}

	[Fact]
	public async Task DisplayUpdaterProbeRecoversWhenTheFirstReadBackFailsTransiently()
	{
		await RequireDisplayUpdaterAsync();
		var reads = 0;

		var reason = await WinMediaProbe.ProbeDisplayUpdaterAsync(thumbnail =>
		{
			reads++;
			return reads is 1
				? throw new FileNotFoundException("Unable to find the specified file.")
				: WinMediaAssets.ReadThumbnailOnceAsync(thumbnail);
		});

		Assert.Null(reason);
		Assert.Equal(2, reads);
	}

	[Fact]
	public async Task DisplayUpdaterProbeStillSkipsWhenTheThumbnailDeliversOtherBytes()
	{
		await RequireDisplayUpdaterAsync();

		var reason = await WinMediaProbe.ProbeDisplayUpdaterAsync(_ => Task.FromResult<byte[]>([1, 2, 3]));

		Assert.StartsWith("the thumbnail read back from the DisplayUpdater delivers 3 bytes", reason, StringComparison.Ordinal);
	}
}
