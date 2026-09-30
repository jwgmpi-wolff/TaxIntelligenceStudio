using System.Runtime.InteropServices;

namespace TaxIntelligenceStudio.Infrastructure.Training;

/// <summary>
/// Describes what the local training stack can actually do on a given machine.
/// </summary>
/// <remarks>
/// The application is deployed to arbitrary Windows machines, so training
/// capability cannot be assumed. The decisive factor is the architecture of the
/// <em>Python interpreter</em>, not the architecture of the machine:
///
/// <list type="bullet">
/// <item>
/// PyTorch and <c>torch-directml</c> publish Windows wheels for x64 only. There
/// has never been a <c>win_arm64</c> wheel for either package, so an
/// Arm64-native Python cannot install the trainer at all.
/// </item>
/// <item>
/// Windows on Arm runs x64 processes under emulation, and an emulated x64
/// Python installs and runs those wheels normally. This was verified on a
/// Snapdragon X Elite machine: a one-step LoRA update completed through PyTorch
/// DirectML on the Qualcomm Adreno X1-85 GPU, and a CPU LoRA step also
/// completed. Arm64 hardware therefore trains successfully, provided the
/// selected interpreter is x64.
/// </item>
/// </list>
///
/// The practical failure mode on an Arm64 machine is selecting the Arm64-native
/// Python, which cannot resolve any wheel. Naming that up front is the
/// difference between a fixable message and a dead end.
/// </remarks>
public static class TrainingPlatform
{
    public const string TrainingPythonVersion = "3.11.9";

    private static readonly string[] DirectMlPackages =
    [
        "torch==2.4.1",
        "torchvision==0.19.1",
        "torch-directml==0.2.5.dev240914",
        "transformers==4.46.3",
        "datasets==3.1.0",
        "accelerate==1.1.1",
        "peft==0.13.2"
    ];

    private static readonly string[] StandardPackages =
    [
        "torch",
        "transformers",
        "datasets",
        "accelerate",
        "peft"
    ];

    /// <summary>The architecture of the machine the application is running on.</summary>
    public static Architecture CurrentArchitecture => RuntimeInformation.OSArchitecture;

    /// <summary>
    /// Whether a Python interpreter of the given architecture can install the
    /// PyTorch training wheels. This gates every training mode, including CPU.
    /// </summary>
    public static bool SupportsTrainingWheels(Architecture interpreterArchitecture) =>
        interpreterArchitecture == Architecture.X64;

    public static string Describe(Architecture architecture) => architecture switch
    {
        Architecture.X64 => "x64",
        Architecture.Arm64 => "Arm64",
        Architecture.X86 => "x86",
        _ => architecture.ToString()
    };

    /// <summary>
    /// How to train on a Windows on Arm machine. An x64 Python runs under
    /// emulation and drives the GPU through DirectML.
    /// </summary>
    public static string Arm64Guidance =>
        "Install the x64 build of Python from python.org and select it as the " +
        "trainer interpreter - Windows on Arm runs it under emulation, and it " +
        "trains on this machine's GPU through DirectML.";

    /// <summary>
    /// Why an interpreter cannot run the trainer, or null when it can.
    /// </summary>
    /// <param name="interpreterArchitecture">Architecture of the Python interpreter.</param>
    /// <param name="hostArchitecture">Architecture of the machine.</param>
    public static string? InterpreterUnavailableReason(
        Architecture interpreterArchitecture,
        Architecture hostArchitecture)
    {
        if (SupportsTrainingWheels(interpreterArchitecture))
        {
            return null;
        }

        var reason =
            $"The selected Python is {Describe(interpreterArchitecture)}, and PyTorch " +
            $"publishes no Windows {Describe(interpreterArchitecture)} wheels, so the " +
            "trainer cannot be installed into it.";

        return hostArchitecture == Architecture.Arm64
            ? $"{reason} {Arm64Guidance}"
            : $"{reason} Select an x64 Python interpreter.";
    }

    /// <summary>
    /// Extra guidance for the host, or null when the host needs no caveat.
    /// </summary>
    public static string? HostGuidance(Architecture hostArchitecture) =>
        hostArchitecture == Architecture.Arm64 ? Arm64Guidance : null;

    /// <summary>
    /// The pip command that installs the trainer for a given accelerator.
    /// </summary>
    /// <remarks>
    /// The DirectML runtime pins torch 2.4.1, so the surrounding packages must
    /// be pinned to versions that still support it. Current <c>transformers</c>
    /// requires torch 2.5 or newer and fails to import against 2.4.1, which
    /// produces an environment that installs cleanly and then breaks at run
    /// time. This combination was verified end to end.
    /// </remarks>
    public static IReadOnlyList<string> Packages(bool directMl) =>
        directMl ? DirectMlPackages : StandardPackages;

    public static string InstallCommand(bool directMl) =>
        $"python -m pip install {string.Join(' ', Packages(directMl))}";
}
