using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using CommunityToolkit.Maui.Media;
using Windows.Foundation;
using Windows.Globalization;
using Windows.Media.SpeechRecognition;

namespace CommunityToolkit.Maui.DeviceTests;

// VM ONLY (never pushed to CommunityToolkit): measurements and a stress run for the Windows SpeechToText recognizer lifetime. The file is self-contained,
// so that it applies on the commit before the fix, on the red commit and on the fix alike.
public static partial class TestCategory
{
	public const string SpeechToTextProbe = nameof(SpeechToTextProbe);
	public const string SpeechToTextStress = nameof(SpeechToTextStress);
}

static class SpeechToTextVmProbeSupport
{
	const int appModelErrorNoPackage = 15700;

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
	static extern int GetCurrentPackageFullName(ref uint packageFullNameLength, IntPtr packageFullName);

	public static bool IsPackaged
	{
		get
		{
			uint length = 0;
			return GetCurrentPackageFullName(ref length, IntPtr.Zero) != appModelErrorNoPackage;
		}
	}

	public static SpeechToTextOptions DefaultOptions => new() { Culture = new CultureInfo("en-US") };

	// State returns a cached value even on a closed recognizer, while Timeouts fails with RO_E_CLOSED, which .NET reports as an ObjectDisposedException
	public static bool IsClosed(SpeechRecognizer recognizer)
	{
		try
		{
			_ = recognizer.Timeouts;
			return false;
		}
		catch (ObjectDisposedException)
		{
			return true;
		}
	}

	public static SpeechRecognizer CreateConfiguredRecognizer()
	{
		var recognizer = new SpeechRecognizer(new Language("en-US"));
		recognizer.UIOptions.AudiblePrompt = string.Empty;
		recognizer.UIOptions.IsReadBackEnabled = false;
		recognizer.UIOptions.ShowConfirmation = false;
		return recognizer;
	}

	public static string Describe(Action read)
	{
		try
		{
			read();
			return "ok";
		}
		catch (Exception exception)
		{
			return $"{exception.GetType().Name} 0x{exception.HResult:X8}";
		}
	}

	public static string Summarize(List<double> milliseconds)
	{
		if (milliseconds.Count is 0)
		{
			return "no sample";
		}

		var sorted = milliseconds.Order().ToList();
		return $"count {sorted.Count}, min {sorted[0]:F2} ms, median {sorted[sorted.Count / 2]:F2} ms, max {sorted[^1]:F2} ms";
	}
}

[Category(TestCategory.SpeechToTextProbe)]
[Collection(TestCollections.DisableParallelization)]
public sealed class SpeechToTextVmProbeTests : CommunityToolkitHandlerTestBase
{
	const int compileCount = 12;
	static readonly TimeSpan compileBound = TimeSpan.FromSeconds(30);

	// A compile that timed out is never closed, so that the probe cannot crash before it reports
	static readonly List<SpeechRecognizer> compilesKeptOpen = [];

	[Fact]
	public async Task ReportRecognizerCompileTimesAndWhichMembersFailOnAClosedRecognizer()
	{
		var report = new StringBuilder();
		var speechDll = Path.Combine(Environment.SystemDirectory, "Windows.Media.Speech.dll");
		report.AppendLine($"OS {Environment.OSVersion.Version}, packaged {SpeechToTextVmProbeSupport.IsPackaged}, {speechDll} {(File.Exists(speechDll) ? FileVersionInfo.GetVersionInfo(speechDll).FileVersion : "missing")}");

		SpeechRecognizer probe;
		try
		{
			probe = await InvokeOnMainThreadAsync(SpeechToTextVmProbeSupport.CreateConfiguredRecognizer);
		}
		catch (Exception exception)
		{
			throw new SkipException($"{report}A Windows speech recognizer for en-US cannot be created on this machine ({exception.GetType().Name}: {exception.Message}).");
		}

		var members = new List<(string Name, Action<SpeechRecognizer> Read)>
		{
			("State", recognizer => _ = recognizer.State),
			("CurrentLanguage.LanguageTag", recognizer => _ = recognizer.CurrentLanguage.LanguageTag),
			("Constraints.Count", recognizer => _ = recognizer.Constraints.Count),
			("Timeouts.InitialSilenceTimeout", recognizer => _ = recognizer.Timeouts.InitialSilenceTimeout),
			("UIOptions.AudiblePrompt", recognizer => _ = recognizer.UIOptions.AudiblePrompt),
			("ContinuousRecognitionSession.AutoStopSilenceTimeout", recognizer => _ = recognizer.ContinuousRecognitionSession.AutoStopSilenceTimeout),
			("StateChanged add and remove", recognizer =>
			{
				TypedEventHandler<SpeechRecognizer, SpeechRecognizerStateChangedEventArgs> handler = (_, _) => { };
				recognizer.StateChanged += handler;
				recognizer.StateChanged -= handler;
			})
		};

		var before = members.Select(member => SpeechToTextVmProbeSupport.Describe(() => member.Read(probe))).ToList();
		probe.Dispose();
		report.AppendLine("Members before and after Dispose:");
		for (var index = 0; index < members.Count; index++)
		{
			report.AppendLine($"  {members[index].Name}: before {before[index]}, after {SpeechToTextVmProbeSupport.Describe(() => members[index].Read(probe))}");
		}

		var milliseconds = new List<double>();
		var statuses = new List<string>();
		for (var index = 0; index < compileCount; index++)
		{
			var sample = await InvokeOnMainThreadAsync(MeasureOneCompileAsync);
			report.AppendLine($"  compile {index + 1}: {sample.Description}");
			if (sample.TimedOut)
			{
				report.AppendLine("  a compile timed out: it is left open and ends the sampling");
				break;
			}

			milliseconds.Add(sample.Milliseconds);
			statuses.Add(sample.Status);
		}

		report.AppendLine($"Compiles: {SpeechToTextVmProbeSupport.Summarize(milliseconds)}; statuses {string.Join(", ", statuses.GroupBy(status => status).Select(group => $"{group.Key} x{group.Count()}"))}");
		throw new SkipException(report.ToString());
	}

	static async Task<(double Milliseconds, string Status, bool TimedOut, string Description)> MeasureOneCompileAsync()
	{
		var recognizer = SpeechToTextVmProbeSupport.CreateConfiguredRecognizer();
		var events = new ConcurrentQueue<string>();
		recognizer.StateChanged += (_, args) => events.Enqueue(args.State.ToString());

		var stopwatch = Stopwatch.StartNew();
		var compile = recognizer.CompileConstraintsAsync().AsTask();
		var stateRightAfterTheStart = SpeechToTextVmProbeSupport.Describe(() => _ = recognizer.State);
		var timeoutsRightAfterTheStart = SpeechToTextVmProbeSupport.Describe(() => _ = recognizer.Timeouts);
		try
		{
			var result = await compile.WaitAsync(compileBound);
			stopwatch.Stop();
			var status = result.Status.ToString();
			var description = $"{stopwatch.Elapsed.TotalMilliseconds:F2} ms, {status}, StateChanged [{string.Join(", ", events)}], State read right after the start: {stateRightAfterTheStart}, Timeouts read right after the start: {timeoutsRightAfterTheStart}";
			recognizer.Dispose();
			return (stopwatch.Elapsed.TotalMilliseconds, status, false, description);
		}
		catch (TimeoutException)
		{
			lock (compilesKeptOpen)
			{
				compilesKeptOpen.Add(recognizer);
			}

			return (compileBound.TotalMilliseconds, "TimedOut", true, $"no result within {compileBound.TotalSeconds} s, StateChanged [{string.Join(", ", events)}]");
		}
		catch (Exception exception)
		{
			// A failed compilation has ended, so its recognizer can be closed, and the sampling goes on so that the report still reaches the XML
			stopwatch.Stop();
			recognizer.Dispose();
			var status = $"Faulted {exception.GetType().Name} 0x{exception.HResult:X8}";
			return (stopwatch.Elapsed.TotalMilliseconds, status, false, $"{stopwatch.Elapsed.TotalMilliseconds:F2} ms, {status} ({exception.Message}), StateChanged [{string.Join(", ", events)}], State read right after the start: {stateRightAfterTheStart}, Timeouts read right after the start: {timeoutsRightAfterTheStart}");
		}
	}
}

[Category(TestCategory.SpeechToTextStress)]
[Collection(TestCollections.DisableParallelization)]
public sealed class SpeechToTextVmStressTests : CommunityToolkitHandlerTestBase
{
	const int cycleCount = 300;
	const int initializationsInABurstCycle = 3;
	static readonly TimeSpan initializeBound = TimeSpan.FromSeconds(10);

	// Compiles only (Initialize, never the start), so that no consent dialog appears. Every cycle releases the recognizers in the main-thread turn that
	// started their compilations, the window of the crash. At most 3 recognizers are alive at a time.
	[Fact]
	public async Task ReleasingRecognizersWhileTheyCompileLeavesNoRecognizerOpenAndCrashesNothing()
	{
		SetupBuilder();
		var speech = new SpeechToTextImplementation();
		var recognizers = new List<SpeechRecognizer>();
		var completed = 0;
		var timedOut = 0;
		var faults = new Dictionary<string, int>();

		for (var cycle = 1; cycle <= cycleCount; cycle++)
		{
			var initializations = await InvokeOnMainThreadAsync(async () =>
			{
				var started = new List<Task>();
				var turnCount = cycle % 10 is 0 ? initializationsInABurstCycle : 1;
				for (var turn = 0; turn < turnCount; turn++)
				{
					var initialization = (Task)(SpeechInternals.Invoke(speech, "Initialize", SpeechToTextVmProbeSupport.DefaultOptions, CancellationToken.None) ?? throw new InvalidOperationException("Initialize returned null"));
					started.Add(initialization);
					recognizers.Add(Assert.IsType<SpeechRecognizer>(SpeechInternals.GetField<object>(speech, "speechRecognizer")));
					await speech.StopListenAsync();
				}

				return started;
			});

			foreach (var initialization in initializations)
			{
				var finished = await Task.WhenAny(initialization, Task.Delay(initializeBound));
				if (!ReferenceEquals(finished, initialization))
				{
					timedOut++;
					continue;
				}

				if (initialization.Exception is not null)
				{
					var type = initialization.Exception.GetBaseException().GetType().Name;
					faults[type] = faults.GetValueOrDefault(type) + 1;
					continue;
				}

				completed++;
			}
		}

		var stillOpen = 0;
		var allClosed = await AssertHelpers.Wait(() => (stillOpen = recognizers.Count(recognizer => !SpeechToTextVmProbeSupport.IsClosed(recognizer))) is 0, 30_000, 100);
		var summary = $"packaged {SpeechToTextVmProbeSupport.IsPackaged}; recognizers {recognizers.Count}, initializations completed {completed}, faulted [{string.Join(", ", faults.Select(fault => $"{fault.Key} x{fault.Value}"))}], timed out {timedOut}, still open {stillOpen}";

		await speech.DisposeAsync();

		Assert.True(timedOut is 0 && faults.Count is 0, $"An initialization did not complete cleanly: {summary}");
		Assert.True(allClosed, $"A released recognizer was not closed after its compilation: {summary}");
		Console.WriteLine($"SpeechToTextStress OK: {summary}");
	}
}
