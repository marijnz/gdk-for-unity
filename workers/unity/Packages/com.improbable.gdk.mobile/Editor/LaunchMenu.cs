using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Improbable.Gdk.Core;
using Improbable.Gdk.Tools;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Improbable.Gdk.Mobile
{
    public static class LaunchMenu
    {
        private const string rootBuildPath = "build";
        private static string AbsoluteAppBuildPath => Path.GetFullPath(Path.Combine(Application.dataPath, Path.Combine("..", rootBuildPath)));
        private static string LibIDeviceInstallerBinary => Common.DiscoverLocation("ideviceinstaller");
        private static string LibIDeviceDebugBinary => Common.DiscoverLocation("idevicedebug");

        private const string MenuLaunchAndroid = "SpatialOS/Launch mobile client/Android Device";
        private const string MenuLaunchiOSDevice = "SpatialOS/Launch mobile client/iOS Device";

        [MenuItem(MenuLaunchAndroid, false, 73)]
        private static void LaunchAndroidClient()
        {
            try
            {
                // Find ADB tool
                var sdkRootPath = EditorPrefs.GetString("AndroidSdkRoot");
                if (string.IsNullOrEmpty(sdkRootPath))
                {
                    Debug.LogError($"Could not find Android SDK. Please set the SDK location in your editor preferences.");
                    return;
                }

                var adbPath = Path.Combine(sdkRootPath, "platform-tools", "adb");

                EditorUtility.DisplayProgressBar("Launching Android Client", "Installing APK", 0.3f);

                // Find apk to install
                var apkPath = Directory.GetFiles(AbsoluteAppBuildPath, "*.apk", SearchOption.AllDirectories).FirstOrDefault();
                if (apkPath == string.Empty)
                {
                    Debug.LogError($"Could not find a built out Android binary in \"{AbsoluteAppBuildPath}\" to launch.");
                    return;
                }

                // Ensure an android device/emulator is present
                if (RedirectedProcess.Command(adbPath).WithArgs("get-state").Run() != 0)
                {
                    Debug.LogError("No Android device/emulator detected.");
                    return;
                }

                // Install apk on connected phone / emulator
                if (RedirectedProcess.Command(adbPath).WithArgs("install", "-r", apkPath).Run() != 0)
                {
                    Debug.LogError("Failed to install the apk on the device/emulator. If the application is already installed on your device/emulator, " +
                        "try uninstalling it before launching the mobile client.");
                    return;
                }

                EditorUtility.DisplayProgressBar("Launching Android Client", "Launching Client", 0.9f);

                // Optional arguments to be passed, same as standalone
                // Use this to pass through the local ip to connect to
                var runtimeIp = GdkToolsConfiguration.GetOrCreateInstance().RuntimeIp;
                var arguments = new StringBuilder();
                if (!string.IsNullOrEmpty(runtimeIp))
                {
                    arguments.Append($"+{RuntimeConfigNames.ReceptionistHost} {runtimeIp}");
                }

                // Get chosen android package id and launch
                var bundleId = PlayerSettings.GetApplicationIdentifier(BuildTargetGroup.Android);
                RedirectedProcess.Command(adbPath)
                    .WithArgs("shell", "am", "start", "-S", "-n", $"{bundleId}/com.unity3d.player.UnityPlayerActivity",
                        "-e", "\"arguments\"", $"\\\"{arguments.ToString()}\\\"")
                    .Run();
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        [MenuItem(MenuLaunchiOSDevice, false, 74)]
        private static void LaunchiOSDeviceClient()
        {
            try
            {
                // Ensure needed tools are installed
                if (string.IsNullOrEmpty(LibIDeviceInstallerBinary))
                {
                    Debug.LogError("Could not find ideviceinstaller tool. Please ensure it is installed. " +
                        "See https://github.com/libimobiledevice/ideviceinstaller fore more details.");
                    return;
                }

                if (string.IsNullOrEmpty(LibIDeviceDebugBinary))
                {
                    Debug.LogError("Could not find idevicedebug tool. Please ensure libimobiledevice is installed. " +
                        "See https://helpmanual.io/help/idevicedebug/ for more details.");
                    return;
                }

                EditorUtility.DisplayProgressBar("Launching iOS Device Client", "Installing archive", 0.3f);

                // Find archive to install
                var ipaPath = Directory.GetFiles(AbsoluteAppBuildPath, "*.ipa", SearchOption.AllDirectories).FirstOrDefault();
                if (string.IsNullOrEmpty(ipaPath))
                {
                    Debug.LogError($"Could not find a built out iOS archive in \"{AbsoluteAppBuildPath}\" to launch.");
                    return;
                }

                if (RedirectedProcess.Command(LibIDeviceInstallerBinary).WithArgs("-i", ipaPath).Run() != 0)
                {
                    Debug.LogError("Error while installing archive to the device. Please check the log for details about the error.");
                    return;
                }

                EditorUtility.DisplayProgressBar("Launching iOS Device Client", "Launching Client", 0.9f);

                // Get chosen ios package id and launch
                var bundleId = PlayerSettings.GetApplicationIdentifier(BuildTargetGroup.iOS);

                // Optional arguments to be passed, same as standalone
                // Use this to pass through the local ip to connect to
                var runtimeIp = GdkToolsConfiguration.GetOrCreateInstance().RuntimeIp;
                var arguments = new StringBuilder();
                if (!string.IsNullOrEmpty(runtimeIp))
                {
                    arguments.Append($"-e SPATIALOS_ARGUMENTS=\"+{RuntimeConfigNames.ReceptionistHost} {runtimeIp}\"");
                }

                RedirectedProcess.Command(LibIDeviceDebugBinary).WithArgs(arguments.ToString(), "run", bundleId)
                    .RedirectOutputOptions(ProcessOutputOptions.RedirectStdOut | ProcessOutputOptions.RedirectStdErr)
                    .ReturnImmediately()
                    .Run();
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }
    }
}
