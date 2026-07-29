using System.Runtime.CompilerServices;

namespace ExpressPackingMonitoring.Tests;

internal static class TestEnvironment
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        string userDataRoot = Path.Combine(Path.GetTempPath(), $"PackingProofTestData-{Environment.ProcessId}");
        Directory.CreateDirectory(userDataRoot);
        Environment.SetEnvironmentVariable("EPM_USER_DATA_DIR", userDataRoot);
    }
}
