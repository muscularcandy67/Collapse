﻿using CollapseLauncher.Interfaces;
using Hi3Helper;
using Hi3Helper.Data;
using Hi3Helper.EncTool;
using Hi3Helper.EncTool.Parser;
using Hi3Helper.EncTool.Parser.AssetMetadata;
using Hi3Helper.EncTool.Parser.Cache;
using Hi3Helper.Http;
using Hi3Helper.Shared.ClassStruct;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using static Hi3Helper.Data.ConverterTool;
using static Hi3Helper.Locale;
using static Hi3Helper.Logger;
using static Hi3Helper.Preset.ConfigV2Store;
using static Hi3Helper.Shared.Region.LauncherConfig;

namespace CollapseLauncher
{
    internal struct HonkaiRepairAssetIgnore
    {
        internal static HonkaiRepairAssetIgnore CreateEmpty() => new HonkaiRepairAssetIgnore()
        {
            IgnoredAudioPCKType = Array.Empty<AudioPCKType>(),
            IgnoredVideoCGSubCategory = Array.Empty<int>()
        };

        internal AudioPCKType[] IgnoredAudioPCKType;
        internal int[] IgnoredVideoCGSubCategory;
    }

    internal partial class HonkaiRepair
    {
        private async Task Fetch(List<FilePropertiesRemote> assetIndex, CancellationToken token)
        {
            // Set total activity string as "Loading Indexes..."
            _status.ActivityStatus = Lang._GameRepairPage.Status2;
            _status.IsProgressTotalIndetermined = true;
            UpdateStatus();

            // Use HttpClient instance on fetching
            Http _httpClient = new Http(true, 5, 1000, _userAgent);
            try
            {
                // Subscribe the fetching progress and subscribe cacheUtil progress to adapter
                _httpClient.DownloadProgress += _httpClient_FetchAssetProgress;
                _cacheUtil.ProgressChanged += _innerObject_ProgressAdapter;
                _cacheUtil.StatusChanged += _innerObject_StatusAdapter;

                // Region: XMFAndAssetIndex
                // Fetch metadata
                Dictionary<string, string> manifestDict = await FetchMetadata(_httpClient, token);

                // Check for manifest. If it doesn't exist, then throw and warn the user
                if (!manifestDict.ContainsKey(_gameVersion.VersionString))
                {
                    throw new VersionNotFoundException($"Manifest for {_gameVersionManager.GamePreset.ZoneName} (version: {_gameVersion.VersionString}) doesn't exist! Please contact @neon-nyan or open an issue for this!");
                }

                // Get the list of ignored assets
                HonkaiRepairAssetIgnore IgnoredAssetIDs = GetIgnoredAssetsProperty();

                // Region: VideoIndex via External -> _cacheUtil: Data Fetch
                // Fetch video index and also fetch the gateway URL
                (string, string) gatewayURL;
                gatewayURL = await FetchVideoAndGateway(_httpClient, assetIndex, IgnoredAssetIDs, token);
                _assetBaseURL = "http://" + gatewayURL.Item1 + '/';
                _gameServer = _cacheUtil?.GetCurrentGateway();

                // Region: AudioIndex
                // Try check audio manifest.m file and fetch it if it doesn't exist
                if (!_isOnlyRecoverMain)
                {
                    await FetchAudioIndex(_httpClient, assetIndex, IgnoredAssetIDs, token);
                }

                // Assign the URL based on the version
                _gameRepoURL = manifestDict[_gameVersion.VersionString];

                // Region: XMFAndAssetIndex
                // Fetch asset index
                await FetchAssetIndex(_httpClient, assetIndex, token);

                // Region: XMFAndAssetIndex
                // Try check XMF file and fetch it if it doesn't exist
                await FetchXMFFile(_httpClient, assetIndex, manifestDict[_gameVersion.VersionString], token);

                // Remove plugin from assetIndex
                _gameVersionManager.GameAPIProp.data.plugins?.ForEach(plugin =>
                {
                    assetIndex.RemoveAll(asset =>
                    {
                        return plugin.package.validate?.Exists(validate => validate.path == asset.N) ?? false;
                    });
                });
            }
            finally
            {
                // Unsubscribe the fetching progress and dispose it and unsubscribe cacheUtil progress to adapter
                _httpClient.DownloadProgress -= _httpClient_FetchAssetProgress;
                _cacheUtil.ProgressChanged -= _innerObject_ProgressAdapter;
                _cacheUtil.StatusChanged -= _innerObject_StatusAdapter;
                _httpClient.Dispose();
            }
        }

        #region Registry Utils
#nullable enable
        private HonkaiRepairAssetIgnore GetIgnoredAssetsProperty()
        {
            // Try get the parent registry key
            RegistryKey? keys = Registry.CurrentUser.OpenSubKey(_gameVersionManager.GamePreset.ConfigRegistryLocation);
            if (keys == null) return HonkaiRepairAssetIgnore.CreateEmpty(); // Return an empty property if the parent key doesn't exist

            // Initialize the property
            AudioPCKType[] IgnoredAudioPCKTypes = Array.Empty<AudioPCKType>();
            int[] IgnoredVideoCGSubCategory = Array.Empty<int>();

            // Try get the values of the registry key of the Audio ignored list
            object? objIgnoredAudioPCKTypes = keys?.GetValue("GENERAL_DATA_V2_DeletedAudioTypes_h214176984");
            if (objIgnoredAudioPCKTypes != null)
            {
                ReadOnlySpan<byte> bytesIgnoredAudioPckTypes = (byte[])objIgnoredAudioPCKTypes;
                IgnoredAudioPCKTypes = bytesIgnoredAudioPckTypes.Deserialize<AudioPCKType[]>(InternalAppJSONContext.Default) ?? IgnoredAudioPCKTypes;
            }

            // Try get the values of the registry key of the Video CG ignored list
            object? objIgnoredVideoCGSubCategory = keys?.GetValue("GENERAL_DATA_V2_DeletedCGPackages_h2282700200");
            if (objIgnoredVideoCGSubCategory != null)
            {
                ReadOnlySpan<byte> bytesIgnoredVideoCGSubCategory = (byte[])objIgnoredVideoCGSubCategory;
                IgnoredVideoCGSubCategory = bytesIgnoredVideoCGSubCategory.Deserialize<int[]>(InternalAppJSONContext.Default) ?? IgnoredVideoCGSubCategory;
            }

            // Return the property value
            return new HonkaiRepairAssetIgnore { IgnoredAudioPCKType = IgnoredAudioPCKTypes, IgnoredVideoCGSubCategory = IgnoredVideoCGSubCategory };
        }
#nullable disable
        #endregion

        #region VideoIndex via External -> _cacheUtil: Data Fetch
        private async Task<(string, string)> FetchVideoAndGateway(Http _httpClient, List<FilePropertiesRemote> assetIndex, HonkaiRepairAssetIgnore ignoredAssetIDs, CancellationToken token)
        {
            // Fetch data cache file only and get the gateway
            (List<CacheAsset>, string, string, int) cacheProperty = await _cacheUtil.GetCacheAssetList(_httpClient, CacheAssetType.Data, token);

            if (!_isOnlyRecoverMain)
            {
                // Find the cache asset. If null, then return
                CacheAsset cacheAsset = cacheProperty.Item1.Where(x => x.N.EndsWith($"{HashID.CGMetadata}")).FirstOrDefault();

                // Deserialize and build video index into asset index
                await BuildVideoIndex(_httpClient, cacheAsset, cacheProperty.Item2, assetIndex, ignoredAssetIDs, cacheProperty.Item4, token);
            }

            // Return the gateway URL including asset bundle and asset cache
            return (cacheProperty.Item2, cacheProperty.Item3);
        }

        private async Task BuildVideoIndex(Http _httpClient, CacheAsset cacheAsset, string assetBundleURL, List<FilePropertiesRemote> assetIndex, HonkaiRepairAssetIgnore ignoredAssetIDs, int luckyNumber, CancellationToken token)
        {
            // Get the remote stream and use CacheStream
            using (Stream memoryStream = new MemoryStream())
            {
                // Download the cache and store it to MemoryStream
                await _httpClient.Download(cacheAsset.ConcatURL, memoryStream, null, null, token);
                memoryStream.Position = 0;

                // Use CacheStream to decrypt and read it as Stream
                using (CacheStream cacheStream = new CacheStream(memoryStream, true, luckyNumber))
                {
                    // Enumerate and iterate the metadata to asset index
                    await BuildAndEnumerateVideoVersioningFile(token, CGMetadata.Enumerate(cacheStream, Encoding.UTF8), assetIndex, ignoredAssetIDs, assetBundleURL);
                }
            }
        }

        private async Task BuildAndEnumerateVideoVersioningFile(CancellationToken token, IEnumerable<CGMetadata> enumEntry, List<FilePropertiesRemote> assetIndex, HonkaiRepairAssetIgnore ignoredAssetIDs, string assetBundleURL)
        {
            // Get the base URL
            string baseURL = CombineURLFromString("http://" + assetBundleURL, "/Video/");

            // Build video versioning file
            using (StreamWriter sw = new StreamWriter(Path.Combine(_gamePath, NormalizePath(_videoBaseLocalPath), "Version.txt"), false))
            {
                // Iterate the metadata to be converted into asset index in parallel
                await Parallel.ForEachAsync(enumEntry, new ParallelOptions
                {
                    CancellationToken = token,
                    MaxDegreeOfParallelism = _threadCount
                }, async (metadata, token) =>
                {
                    // Only add remote available videos (not build-in) and check if the CG file is available in the server
                    // Edit: 2023-12-09
                    // Starting from 7.1, the CGs that have included in ignoredAssetIDs (which is marked as deleted) will be ignored.
                    bool isCGAvailable = await IsCGFileAvailable(metadata, baseURL, token);
                    bool isCGIgnored = ignoredAssetIDs.IgnoredVideoCGSubCategory.Contains(metadata.CgSubCategory);
                    if (!metadata.InStreamingAssets)
                    {
                        lock (sw)
                        {
                            // Append the versioning list
                            sw.WriteLine("Video/" + metadata.CgPath + ".usm\t1");
                        }
                    }

#if DEBUG
                    if (isCGIgnored)
                        LogWriteLine($"Ignoring CG Category: {metadata.CgSubCategory} {metadata.CgPath}", LogType.Debug, true);
#endif

                    if (!metadata.InStreamingAssets && isCGAvailable && !isCGIgnored)
                    {
                        string name = metadata.CgPath + ".usm";
                        lock (assetIndex)
                        {
                            assetIndex.Add(new FilePropertiesRemote
                            {
                                N = CombineURLFromString(_videoBaseLocalPath, name),
                                RN = CombineURLFromString(baseURL, name),
                                S = metadata.FileSize,
                                FT = FileType.Video
                            });
                        }
                    }
                });
            }
        }

        private async ValueTask<bool> IsCGFileAvailable(CGMetadata cgInfo, string baseURL, CancellationToken token)
        {
            // If the file has no appoinment schedule (like non-birthday CG), then return true
            if (cgInfo.AppointmentDownloadScheduleID == 0) return true;

            // Update the status
            _status.ActivityStatus = string.Format("Trying to determine CG asset availability: {0}", cgInfo.CgExtraKey);
            _status.IsProgressTotalIndetermined = true;
            _status.IsProgressPerFileIndetermined = true;
            UpdateStatus();

            // Set the URL and try get the status
            string cgURL = CombineURLFromString(baseURL, cgInfo.CgPath + ".usm");
            using HttpResponseMessage urlStatus = await FallbackCDNUtil.GetURLHttpResponse(cgURL, token);

            LogWriteLine($"The CG asset: {cgInfo.CgPath} " + (urlStatus.IsSuccessStatusCode ? "is" : "is not") + $" available (Status code: {urlStatus.StatusCode})", LogType.Default, true);

            return urlStatus.IsSuccessStatusCode;
        }

        #endregion

        #region AudioIndex
        private async Task FetchAudioIndex(Http _httpClient, List<FilePropertiesRemote> assetIndex, HonkaiRepairAssetIgnore ignoredAssetIDs, CancellationToken token)
        {
            // If the gameServer is null, then just leave
            if (_gameServer == null)
            {
                LogWriteLine("We found that the Dispatch/GameServer has return a null. Please report this issue to Collapse's Contributor or submit an issue!", LogType.Warning, true);
                return;
            }

            // Set manifest.m local path and remote URL
            string manifestLocalPath = Path.Combine(_gamePath, NormalizePath(_audioBaseLocalPath), "manifest.m");
            string manifestRemotePath = string.Format(CombineURLFromString(_audioBaseRemotePath, _gameServer.Manifest.ManifestAudio.ManifestAudioPlatform.ManifestWindows), $"{_gameVersion.Major}_{_gameVersion.Minor}", _gameServer.Manifest.ManifestAudio.ManifestAudioRevision);

            try
            {
                // Try to get the audio manifest and deserialize it
                KianaAudioManifest manifest = await TryGetAudioManifest(_httpClient, manifestLocalPath, manifestRemotePath, token);

                // Deserialize manifest and build Audio Index
                await BuildAudioIndex(manifest, assetIndex, ignoredAssetIDs, token);

                // Build audio version file
                BuildAudioVersioningFile(manifest, ignoredAssetIDs);
            }
            // If a throw was thrown, then try to redownload the manifest.m file and try deserialize it again
            catch (Exception ex)
            {
                LogWriteLine($"Exception was thrown while reading audio manifest file!\r\n{ex}", LogType.Warning, true);
                return;
            }
        }

        private void BuildAudioVersioningFile(KianaAudioManifest audioManifest, HonkaiRepairAssetIgnore ignoredAssetIDs)
        {
            // Build audio versioning file
            using (StreamWriter sw = new StreamWriter(Path.Combine(_gamePath, NormalizePath(_audioBaseLocalPath), "Version.txt"), false))
            {
                // Edit: 2023-12-09
                // Starting from 7.1, the Audio Packages that have included in ignoredAssetIDs (which is marked as deleted) will be ignored.
                foreach (ManifestAssetInfo audioAsset in audioManifest
                    .AudioAssets
                    .Where(audioInfo => (audioInfo.Language == AudioLanguageType.Common
                                      || audioInfo.Language == _audioLanguage)
                                      && !ignoredAssetIDs.IgnoredAudioPCKType.Contains(audioInfo.PckType)))
                {
                    // Only add common and language specific audio file
                    sw.WriteLine($"{audioAsset.Name}.pck\t{audioAsset.HashString}");
                }
            }
        }

        private async Task BuildAudioIndex(KianaAudioManifest audioManifest, List<FilePropertiesRemote> audioIndex, HonkaiRepairAssetIgnore ignoredAssetIDs, CancellationToken token)
        {
            // Iterate the audioAsset to be added in audioIndex in parallel
            // Edit: 2023-12-09
            // Starting from 7.1, the Audio Packages that have included in ignoredAssetIDs (which is marked as deleted) will be ignored.
            await Parallel.ForEachAsync(audioManifest
                .AudioAssets
                .Where(audioInfo => (audioInfo.Language == AudioLanguageType.Common
                                  || audioInfo.Language == _audioLanguage)
                                  && !ignoredAssetIDs.IgnoredAudioPCKType.Contains(audioInfo.PckType)),
                new ParallelOptions
                {
                    CancellationToken = token,
                    MaxDegreeOfParallelism = _threadCount
                }, async (audioInfo, token) =>
                {
                    // Try get the availability of the audio asset
                    if (await IsAudioFileAvailable(audioInfo, token))
                    {
                        // Skip AUDIO_Default since it's already been provided by base index
                        if (audioInfo.Name != "AUDIO_Default")
                        {
                            lock (audioIndex)
                            {
                                // Assign based on each values
                                FilePropertiesRemote audioAsset = new FilePropertiesRemote
                                {
                                    RN = audioInfo.Path,
                                    N = CombineURLFromString(_audioBaseLocalPath, audioInfo.Name + ".pck"),
                                    S = audioInfo.Size,
                                    FT = FileType.Audio,
                                    CRC = audioInfo.HashString,
                                    AudioPatchInfo = audioInfo.IsHasPatch ? audioInfo.PatchInfo : null,
                                };

                                // Add audioAsset to audioIndex
                                audioIndex.Add(audioAsset);
                            }
                        }
                    }
                });
        }

        private async ValueTask<bool> IsAudioFileAvailable(ManifestAssetInfo audioInfo, CancellationToken token)
        {
            // If the file is static (NeedMap == true), then pass
            if (audioInfo.NeedMap) return true;

            // Update the status
            _status.ActivityStatus = string.Format("Trying to determine audio asset availability: {0}", audioInfo.Path);
            _status.IsProgressTotalIndetermined = true;
            _status.IsProgressPerFileIndetermined = true;
            UpdateStatus();

            // Set the URL and try get the status
            string audioURL = CombineURLFromString(string.Format(_audioBaseRemotePath, $"{_gameVersion.Major}_{_gameVersion.Minor}", _gameServer.Manifest.ManifestAudio.ManifestAudioRevision), audioInfo.Path);
            using HttpResponseMessage urlStatus = await FallbackCDNUtil.GetURLHttpResponse(audioURL, token);

            LogWriteLine($"The audio asset: {audioInfo.Path} " + (urlStatus.IsSuccessStatusCode ? "is" : "is not") + $" available (Status code: {urlStatus.StatusCode})", LogType.Default, true);

            return urlStatus.IsSuccessStatusCode;
        }

        private string GetXmlConfigKey()
        {
            // Initialize keyTool
            mhyEncTool keyTool = new mhyEncTool();
            keyTool.InitMasterKey(ConfigV2.MasterKey, ConfigV2.MasterKeyBitLength, RSAEncryptionPadding.Pkcs1);

            // Return the key
            return keyTool.GetMasterKey();
        }

        private async Task<KianaAudioManifest> TryGetAudioManifest(Http _httpClient, string manifestLocal, string manifestRemote, CancellationToken token)
        {
            // Always check if the folder is exist
            string manifestFolder = Path.GetDirectoryName(manifestLocal);
            if (!Directory.Exists(manifestFolder))
            {
                Directory.CreateDirectory(manifestFolder);
            }

            // Start downloading manifest.m
            await _httpClient.Download(manifestRemote, manifestLocal, true, null, null, token);

            // Get the XML key and deserialize the manifest
            string xmlKey = GetXmlConfigKey();
            return new KianaAudioManifest(manifestLocal, xmlKey, _gameVersion.VersionArrayManifest);
        }
        #endregion

        #region XMFAndAssetIndex
        private async Task<Dictionary<string, string>> FetchMetadata(Http _httpClient, CancellationToken token)
        {
            // Set metadata URL
            string urlMetadata = string.Format(AppGameRepoIndexURLPrefix, _gameVersionManager.GamePreset.ProfileName);

            // Start downloading metadata using FallbackCDNUtil
            await using BridgedNetworkStream stream = await FallbackCDNUtil.TryGetCDNFallbackStream(urlMetadata, token);
            return await stream.DeserializeAsync<Dictionary<string, string>>(CoreLibraryJSONContext.Default, token);
        }

        private async Task FetchAssetIndex(Http _httpClient, List<FilePropertiesRemote> assetIndex, CancellationToken token)
        {
            // Set asset index URL
            string urlIndex = string.Format(AppGameRepairIndexURLPrefix, _gameVersionManager.GamePreset.ProfileName, _gameVersion.VersionString) + ".bin";

            // Start downloading asset index using FallbackCDNUtil
            await using BridgedNetworkStream stream = await FallbackCDNUtil.TryGetCDNFallbackStream(urlIndex, token);

            // Deserialize asset index and return
            DeserializeAssetIndex(stream, assetIndex);
        }

        private void DeserializeAssetIndex(Stream stream, List<FilePropertiesRemote> assetIndex)
        {
            using (BinaryReader bRaw = new BinaryReader(stream))
            {
                // Read the signature and do check
                ulong signature = bRaw.ReadUInt64();
                if (signature != _assetIndexSignature)
                {
                    throw new FormatException($"The asset index manifest is invalid! Reading: {signature} but expecting: {_assetIndexSignature}");
                }

                // Get the version and do serialization based on its own version implementation
                byte version = bRaw.ReadByte();
                switch (version)
                {
                    // Read version 1
                    case 1:
                        using (BrotliStream brStream = new BrotliStream(stream, CompressionMode.Decompress))
                        {
                            ParseAssetIndexChunkV1(brStream, assetIndex);
                        }
                        break;
                    // If format is not valid, then throw
                    default:
                        throw new FormatException($"Version of the asset index format is unsupported! Reading: {version}");
                }
            }
        }

        private void ParseAssetIndexChunkV1(Stream stream, List<FilePropertiesRemote> assetIndex)
        {
            // Assign stream into the reader
            using (BinaryReader reader = new BinaryReader(stream))
            {
                // Read the asset count
                int assetCount = reader.ReadInt32();

                // Do loop to read all the information
                for (int i = 0; i < assetCount; i++)
                {
                    // Read the binary information into asset info
                    FilePropertiesRemote assetInfo = new FilePropertiesRemote();
                    assetInfo.FT = (FileType)reader.ReadByte();
                    assetInfo.N = reader.ReadString();
                    assetInfo.S = reader.ReadInt64();
                    assetInfo.CRC = HexTool.BytesToHexUnsafe(reader.ReadBytes(16));
                    assetInfo.RN = CombineURLFromString(_gameRepoURL, assetInfo.N);

#if DEBUG
                    LogWriteLine($"{assetInfo.PrintSummary()} found in manifest");
#endif

                    // Add assetInfo
                    assetIndex.Add(assetInfo);
                }
            }
        }

        private async Task FetchXMFFile(Http _httpClient, List<FilePropertiesRemote> assetIndex, string _repoURL, CancellationToken token)
        {
            // Set Primary XMF Path
            string xmfPriPath = Path.Combine(_gamePath, "BH3_Data\\StreamingAssets\\Asb\\pc\\Blocks.xmf");
            // Set Secondary XMF Path
            string xmfSecPath = Path.Combine(_gamePath, $"BH3_Data\\StreamingAssets\\Asb\\pc\\Blocks_{_gameVersion.Major}_{_gameVersion.Minor}.xmf");

            // Set Primary XMF URL
            string urlPriXMF = CombineURLFromString(_repoURL, _blockBasePath, "Blocks.xmf");
            // Set Secondary XMF URL
            string urlSecXMF = CombineURLFromString(_blockAsbBaseURL, $"/Blocks_{_gameVersion.Major}_{_gameVersion.Minor}.xmf");

#nullable enable
            // Initialize patch config info variable
            BlockPatchManifest? patchConfigInfo = null;

            // Fetch only RecoverMain is disabled
            using (FileStream fs1 = new FileStream(EnsureCreationOfDirectory(_isOnlyRecoverMain ? xmfPriPath : xmfSecPath), FileMode.Create, FileAccess.ReadWrite))
            {
                // Download the secondary XMF into MemoryStream
                await _httpClient.Download(_isOnlyRecoverMain ? urlPriXMF : urlSecXMF, fs1, null, null, token);

                // Copy the secondary XMF into primary XMF if _isOnlyRecoverMain == false
                if (!_isOnlyRecoverMain)
                {
                    using (FileStream fs2 = new FileStream(EnsureCreationOfDirectory(xmfPriPath), FileMode.Create, FileAccess.Write))
                    {
                        fs1.Position = 0;
                        fs1.CopyTo(fs2);
                    }

                    // Reset the secondary XMF stream position
                    fs1.Position = 0;

                    // Fetch for PatchConfig.xmf file (Block patch metadata)
                    patchConfigInfo = await FetchPatchConfigXMFFile(fs1, _httpClient, token);
                }
            }

            // After all completed, then Deserialize the XMF to build the asset index
            BuildBlockIndex(assetIndex, patchConfigInfo, _isOnlyRecoverMain ? xmfPriPath : xmfSecPath);
#nullable disable
        }

        private async Task<BlockPatchManifest> FetchPatchConfigXMFFile(Stream xmfStream, Http _httpClient, CancellationToken token)
        {
            // Set PatchConfig URL
            string urlPatchXMF = CombineURLFromString(_blockPatchBaseURL, "/PatchConfig.xmf");

            // Start downloading XMF and load it to MemoryStream first
            using (MemoryStream mfs = new MemoryStream())
            {
                // Check the status of the patch file
                // If doesn't exist, then return an empty list
                (int, bool) status = await _httpClient.GetURLStatus(urlPatchXMF, token);
                if (!status.Item2)
                {
                    return null;
                }

                // Download the XMF into MemoryStream
                await _httpClient.Download(urlPatchXMF, mfs, null, null, token);

                // Reset the MemoryStream position
                mfs.Position = 0;

#nullable enable
                // Get the version provided by the XMF
                int[]? gameVersion = XMFUtility.GetXMFVersion(xmfStream);
                if (gameVersion == null) return null;
#nullable disable

                // Initialize and parse the manifest, then return the Patch Asset
                return new BlockPatchManifest(mfs, gameVersion);
            }
        }

#nullable enable
        private void BuildBlockIndex(List<FilePropertiesRemote> assetIndex, BlockPatchManifest? patchInfo, string xmfPath)
        {
            // Initialize and parse the XMF file
            XMFParser xmfParser = new XMFParser(xmfPath);

            // Do loop and assign the block asset to asset index
            for (int i = 0; i < xmfParser.BlockCount; i++)
            {
                // Check if the patch info exist for current block, then assign blockPatchInfo
                BlockPatchInfo? blockPatchInfo = null;

                if (patchInfo != null && patchInfo.NewBlockCatalog.ContainsKey(xmfParser.BlockEntry[i].HashString))
                {
                    int blockPatchInfoIndex = patchInfo.NewBlockCatalog[xmfParser.BlockEntry[i].HashString];
                    blockPatchInfo = patchInfo.PatchAsset[blockPatchInfoIndex];
                }

                // Assign as FilePropertiesRemote
                FilePropertiesRemote assetInfo = new FilePropertiesRemote
                {
                    N = CombineURLFromString(_blockBasePath, xmfParser.BlockEntry[i].HashString + ".wmv"),
                    RN = CombineURLFromString(_blockAsbBaseURL, xmfParser.BlockEntry[i].HashString + ".wmv"),
                    S = xmfParser.BlockEntry[i].Size,
                    CRC = xmfParser.BlockEntry[i].HashString,
                    FT = FileType.Blocks,
                    BlockPatchInfo = blockPatchInfo
                };

                // Add the asset info
                assetIndex.Add(assetInfo);
            }

            // Write the blockVerifiedVersion based on secondary XMF
            File.WriteAllText(Path.Combine(_gamePath, NormalizePath(_blockBasePath), "blockVerifiedVersion.txt"), string.Join('_', xmfParser.Version));
        }
#nullable disable

        private void CountAssetIndex(List<FilePropertiesRemote> assetIndex)
        {
            // Filter out video assets
            List<FilePropertiesRemote> assetIndexFiltered = assetIndex.Where(x => x.FT != FileType.Video).ToList();

            // Sum the assetIndex size and assign to _progressTotalSize
            _progressTotalSize = assetIndexFiltered.Sum(x => x.S);

            // Assign the assetIndex count to _progressTotalCount
            _progressTotalCount = assetIndexFiltered.Count;
        }
        #endregion
    }
}
