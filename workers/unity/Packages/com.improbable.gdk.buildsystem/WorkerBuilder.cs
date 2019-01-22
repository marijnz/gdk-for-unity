using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Improbable.Gdk.BuildSystem.Configuration;
using Improbable.Gdk.Core;
using Improbable.Gdk.Tools;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Improbable.Gdk.BuildSystem
{
    public static class WorkerBuilder
    {
        private static readonly string PlayerBuildDirectory =
            Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), EditorPaths.AssetDatabaseDirectory,
                "worker"));

        private static readonly string AssetDatabaseDirectory =
            Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), EditorPaths.AssetDatabaseDirectory));

        private const string BuildWorkerTypes = "buildWorkerTypes";

        /// <summary>
        ///     Build method that is invoked by commandline
        /// </summary>
        // ReSharper disable once UnusedMember.Global
        public static void Build()
        {
            try
            {
                var commandLine = Environment.GetCommandLineArgs();
                var buildTargetArg = CommandLineUtility.GetCommandLineValue(commandLine, "buildTarget", "local");

                BuildEnvironment buildEnvironment;
                switch (buildTargetArg.ToLower())
                {
                    case "cloud":
                        buildEnvironment = BuildEnvironment.Cloud;
                        break;
                    case "local":
                        buildEnvironment = BuildEnvironment.Local;
                        break;
                    default:
                        throw new BuildFailedException("Unknown build target value: " + buildTargetArg);
                }

                var workerTypesArg =
                    CommandLineUtility.GetCommandLineValue(commandLine, BuildWorkerTypes,
                        "UnityClient,UnityGameLogic");
                var wantedWorkerTypes = workerTypesArg.Split(',');

                ValidateWorkerConfiguration(wantedWorkerTypes, buildEnvironment);

                ScriptingImplementation scriptingBackend;
                var wantedScriptingBackend = CommandLineUtility.GetCommandLineValue(commandLine, "scriptingBackend", "mono");
                switch (wantedScriptingBackend)
                {
                    case "mono":
                        scriptingBackend = ScriptingImplementation.Mono2x;
                        break;
                    case "il2cpp":
                        scriptingBackend = ScriptingImplementation.IL2CPP;
                        break;
                    default:
                        throw new BuildFailedException("Unknown scripting backend value: " + wantedScriptingBackend);
                }

                LocalLaunch.BuildConfig();

                foreach (var wantedWorkerType in wantedWorkerTypes)
                {
                    BuildWorkerForEnvironment(wantedWorkerType, buildEnvironment, scriptingBackend);
                }
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                if (e is BuildFailedException)
                {
                    throw;
                }

                throw new BuildFailedException(e);
            }
        }

        private static void ValidateWorkerConfiguration(string[] wantedWorkerTypes, BuildEnvironment buildEnvironment)
        {
            var problemWorkers = new List<string>();            
            foreach (var wantedWorkerType in wantedWorkerTypes)
            {
                var spatialOSBuildConfiguration = SpatialOSBuildConfiguration.GetInstance();

                var workerConfiguration =
                    spatialOSBuildConfiguration.WorkerBuildConfigurations.FirstOrDefault(x =>
                        x.WorkerType == wantedWorkerType);

                var missingBuildSupport = workerConfiguration.GetEnvironmentConfig(buildEnvironment).BuildTargets
                    .Where(t => !t.BuildSupportInstalled).ToList();

                foreach (var t in missingBuildSupport)
                {
                    Debug.LogError($"{wantedWorkerType}: Missing build support for {t.Target.ToString()}");
                }

                if (missingBuildSupport.Any())
                {
                    problemWorkers.Add(wantedWorkerType);
                }
            }

            if (problemWorkers.Any())
            {
                throw new BuildFailedException($"Build configuration has errors for {string.Join(",", problemWorkers)}");
            }
        }

        public static void BuildWorkerForEnvironment(string workerType, BuildEnvironment targetEnvironment, ScriptingImplementation? scriptingBackend = null)
        {
            var spatialOSBuildConfiguration = SpatialOSBuildConfiguration.GetInstance();
            var environmentConfig = spatialOSBuildConfiguration.GetEnvironmentConfigForWorker(workerType, targetEnvironment);
            if (environmentConfig == null)
            {
                Debug.LogWarning($"Skipping build for {workerType}.");
                return;
            }

            if (!Directory.Exists(PlayerBuildDirectory))
            {
                Directory.CreateDirectory(PlayerBuildDirectory);
            }

            foreach (var config in environmentConfig.BuildTargets)
            {
                var buildTargetGroup = BuildPipeline.GetBuildTargetGroup(unityBuildTarget);
                var activeScriptingBackend = PlayerSettings.GetScriptingBackend(buildTargetGroup);
                try
                {
                    if (scriptingBackend != null)
                    {
                        Debug.Log($"Setting scripting backend to {scriptingBackend.Value}");
                        PlayerSettings.SetScriptingBackend(buildTargetGroup, scriptingBackend.Value);
                    }

                    BuildWorkerForTarget(workerType, unityBuildTarget, buildOptions, targetEnvironment);
                }
                catch (Exception e)
                {
                    throw new BuildFailedException(e);
                }
                finally
                {
                    PlayerSettings.SetScriptingBackend(buildTargetGroup, activeScriptingBackend);
                }

            }
        }

        public static void Clean()
        {
            Directory.Delete(AssetDatabaseDirectory, true);
            Directory.Delete(EditorPaths.BuildScratchDirectory, true);
        }

        
        private static void BuildWorkerForTarget(string workerType, BuildTarget buildTarget,
            BuildOptions buildOptions, BuildEnvironment targetEnvironment)
        {
            Debug.Log($"Building \"{buildTarget}\" for worker platform: \"{workerType}\", environment: \"{targetEnvironment}\"");

            var spatialOSBuildConfiguration = SpatialOSBuildConfiguration.GetInstance();
            var workerBuildData = new WorkerBuildData(workerType, buildTarget);
            var scenes = spatialOSBuildConfiguration.GetScenePathsForWorker(workerType);

            var buildPlayerOptions = new BuildPlayerOptions
            {
                options = buildOptions,
                target = buildTarget,
                scenes = scenes,
                locationPathName = workerBuildData.BuildScratchDirectory
            };

            var result = BuildPipeline.BuildPlayer(buildPlayerOptions);
            if (result.summary.result != BuildResult.Succeeded)
            {
                throw new BuildFailedException($"Build failed for {workerType}");
            }

            if (buildTarget == BuildTarget.Android || buildTarget == BuildTarget.iOS)
            {
                // Mobile clients can only be run locally, no need to package them
                return;
            }

            var zipPath = Path.Combine(PlayerBuildDirectory, workerBuildData.PackageName);
            var basePath = Path.Combine(EditorPaths.BuildScratchDirectory, workerBuildData.PackageName);
            Zip(zipPath, basePath, targetEnvironment == BuildEnvironment.Cloud);
        }

        private static void Zip(string zipAbsolutePath, string basePath, bool useCompression)
        {
            using (new ShowProgressBarScope($"Package {basePath}"))
            {
                RedirectedProcess.Run(Common.SpatialBinary, "file", "zip",
                    $"--output=\"{Path.GetFullPath(zipAbsolutePath)}\"",
                    $"--basePath=\"{Path.GetFullPath(basePath)}\"", "\"**\"",
                    $"--compression={useCompression}");
            }
        }
    }
}
