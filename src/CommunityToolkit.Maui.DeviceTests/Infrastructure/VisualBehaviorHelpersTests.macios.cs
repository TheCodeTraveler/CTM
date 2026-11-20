using System.Runtime.CompilerServices;
using CommunityToolkit.Maui.DeviceTests.Runners;
using Microsoft.Maui.Dispatching;

namespace CommunityToolkit.Maui.DeviceTests;

// The crash it prevents (an NSRangeException in a UIKit dealloc) needs a rare interleaving, so the test checks its cause: the main thread
// must not run anything while the forced finalizer pass of WaitForGCAsync is in progress.
[Category(TestCategory.DeviceTestsInfrastructure)]
[Collection(TestCollections.DisableParallelization)]
public class VisualBehaviorHelpersTests
{
	const int probeCount = 16;

	[Fact]
	public async Task WaitForGCAsyncRunsNothingOnTheMainThreadWhileFinalizersRun()
	{
		var observer = new FinalizerObserver(TestDispatcher.Current);
		var references = CreateGarbageProbes(observer);

		await VisualBehaviorHelpers.WaitForGCAsync(references);
		await TestDispatcher.Current.DispatchAsync(static () => { });

		Assert.Equal(probeCount, observer.FinalizedCount);
		Assert.True(observer.BlockRanDuringPass is false, $"{observer.FinalizersThatSawTheBlockRun} of {probeCount - 1} finalizers ran after the main thread ran a block that the first finalizer posted to it.");
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static WeakReference[] CreateGarbageProbes(FinalizerObserver observer)
	{
		var references = new WeakReference[probeCount];

		for (var i = 0; i < references.Length; i++)
		{
			references[i] = new WeakReference(new FinalizerProbe(observer));
		}

		return references;
	}

	sealed class FinalizerProbe(FinalizerObserver observer)
	{
		~FinalizerProbe() => observer.OnFinalized();
	}

	sealed class FinalizerObserver(IDispatcher mainThreadDispatcher)
	{
		int finalizedCount;
		int blockRan;
		int finalizersThatSawTheBlockRun;

		public int FinalizedCount => Volatile.Read(ref finalizedCount);

		public int FinalizersThatSawTheBlockRun => Volatile.Read(ref finalizersThatSawTheBlockRun);

		public bool BlockRanDuringPass => FinalizersThatSawTheBlockRun > 0;

		public void OnFinalized()
		{
			if (Interlocked.Increment(ref finalizedCount) is 1)
			{
				mainThreadDispatcher.Dispatch(() => Volatile.Write(ref blockRan, 1));
				Thread.Sleep(100);
				return;
			}

			Thread.Sleep(20);

			if (Volatile.Read(ref blockRan) is 1)
			{
				Interlocked.Increment(ref finalizersThatSawTheBlockRun);
			}
		}
	}
}
