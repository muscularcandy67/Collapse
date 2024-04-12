﻿#nullable enable
    using Hi3Helper;
    using Hi3Helper.Data;
    using Hi3Helper.Shared.Region;
    using Squirrel;
    using Squirrel.Sources;
    using System;
    using System.Reflection;
    using System.Threading.Tasks;

    namespace CollapseLauncher.Helper.Update
{
    internal static class LauncherUpdateHelper
    {
        static LauncherUpdateHelper()
        {
            Version? version = Assembly.GetExecutingAssembly().GetName().Version;
            if (version == null)
                throw new NullReferenceException("App cannot retrieve the current version of the executable!");

            _launcherCurrentVersion = new GameVersion(version);
            _launcherCurrentVersionString = _launcherCurrentVersion.VersionString;
            LoggerBase.CurrentLauncherVersion = _launcherCurrentVersion.VersionString;
        }

        internal static AppUpdateVersionProp? AppUpdateVersionProp;
        internal static bool IsLauncherUpdateAvailable;

        private static GameVersion _launcherCurrentVersion;
        internal static GameVersion? LauncherCurrentVersion
            => _launcherCurrentVersion;

        private static string _launcherCurrentVersionString;
        internal static string LauncherCurrentVersionString
            => _launcherCurrentVersionString;

        internal static async void RunUpdateCheckDetached()
        {
            try
            {
                bool isUpdateAvailable = await IsUpdateAvailable();
                LauncherUpdateWatcher.GetStatus(new LauncherUpdateProperty { IsUpdateAvailable = isUpdateAvailable, NewVersionName = AppUpdateVersionProp?.Version ?? default });
            }
            catch (Exception ex)
            {
                Logger.LogWriteLine($"The squirrel check throws an error, Skipping update check!\r\n{ex}", LogType.Warning, true);
            }
        }

        internal static async Task<bool> IsUpdateAvailable(bool isForceCheckUpdate = false)
        {
            string updateChannel = LauncherConfig.IsPreview ? "preview" : "stable";

            CDNURLProperty launcherUpdatePreferredCdn = FallbackCDNUtil.GetPreferredCDN();
            string? launcherUpdateSquirrelBaseUrl = ConverterTool.CombineURLFromString(launcherUpdatePreferredCdn.URLPrefix, "squirrel", updateChannel);

            IFileDownloader squirrelUpdateManagerHttpAdapter = new UpdateManagerHttpAdapter();
            using (UpdateManager squirrelUpdateManager = new UpdateManager(launcherUpdateSquirrelBaseUrl, null, null, squirrelUpdateManagerHttpAdapter))
            {
                UpdateInfo info = await squirrelUpdateManager.CheckForUpdate();
                if (info == null) return false;

                GameVersion remoteVersion = new GameVersion(info.FutureReleaseEntry.Version.Version);

                AppUpdateVersionProp = await GetUpdateMetadata(updateChannel);
                if (AppUpdateVersionProp == null) return false;

                IsLauncherUpdateAvailable = LauncherCurrentVersion.Compare(remoteVersion);

                bool isUserIgnoreUpdate = (LauncherConfig.GetAppConfigValue("DontAskUpdate").ToBoolNullable() ?? false) && !isForceCheckUpdate;
                bool isUpdateRoutineSkipped = isUserIgnoreUpdate && !(AppUpdateVersionProp?.IsForceUpdate ?? false);

                return IsLauncherUpdateAvailable && !isUpdateRoutineSkipped;
            }
        }

        private static async ValueTask<AppUpdateVersionProp?> GetUpdateMetadata(string updateChannel)
        {
            string relativePath = ConverterTool.CombineURLFromString(updateChannel, "fileindex.json");
            await using BridgedNetworkStream ms = await FallbackCDNUtil.TryGetCDNFallbackStream(relativePath, default);
            return await ms.DeserializeAsync<AppUpdateVersionProp>(InternalAppJSONContext.Default);
        }
    }
}
