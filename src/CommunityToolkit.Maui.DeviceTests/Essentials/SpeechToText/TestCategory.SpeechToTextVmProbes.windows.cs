namespace CommunityToolkit.Maui.DeviceTests;

// VM ONLY (never pushed to CommunityToolkit): eng/devices/run.ps1 accepts a Category= filter only for a constant of a TestCategory*.cs file
public static partial class TestCategory
{
	public const string SpeechToTextProbe = nameof(SpeechToTextProbe);
	public const string SpeechToTextStress = nameof(SpeechToTextStress);
}
