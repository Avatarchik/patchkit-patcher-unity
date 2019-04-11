﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.IO;
using System.Linq;
using JetBrains.Annotations;
using PatchKit.Api.Models.Main;
using PatchKit.Logging;
using PatchKit.Network;
using PatchKit.Unity.Patcher.AppData.FileSystem;
using PatchKit.Unity.Patcher.AppUpdater.Status;
using PatchKit.Unity.Patcher.Debug;
using PatchKit.Unity.Utilities;
using CancellationToken = PatchKit.Unity.Patcher.Cancellation.CancellationToken;

namespace PatchKit.Unity.Patcher.AppData.Remote.Downloaders
{
    /// <summary>
    /// Downloads chunk-hashed file through HTTP.
    /// Chunk hashes are used to interrupt and resume downloading if downloaded chunk will be
    /// proven corrupted. In this way even on poor internet connection there's a possibility
    /// of downloading big files through http without the need of re-downloading it again.
    /// </summary>
    public sealed class ChunkedHttpDownloader : IChunkedHttpDownloader
    {
        public struct DownloadJob
        {
            public DownloadJob(string url, long start = 0, long end = -1)
            {
                Url = url;
                Range = new BytesRange(start, end);
            }

            public string Url;
            public BytesRange Range;
        }

        public struct UrlPair
        {
            public UrlPair(ResourceUrl primary, ResourceUrl? secondary = null)
            {
                Primary = primary;
                Secondary = secondary;
            }

            public readonly ResourceUrl Primary;
            public readonly ResourceUrl? Secondary;
        }

        private readonly ILogger _logger;

        private readonly IRequestTimeoutCalculator _timeoutCalculator = new SimpleRequestTimeoutCalculator();

        private readonly IRequestRetryStrategy _retryStrategy = new SimpleInfiniteRequestRetryStrategy();

        private readonly string _destinationFilePath;

        private readonly ResourceUrl[] _urls;

        private readonly ChunksData _chunksData;

        private readonly long _size;

        private bool _downloadHasBeenCalled;

        private BytesRange _range = new BytesRange(0, -1);

        public event DownloadProgressChangedHandler DownloadProgressChanged;

        public ChunkedHttpDownloader([NotNull] string destinationFilePath, [NotNull] ResourceUrl[] urls, ChunksData chunksData,
            long size)
        {
            if (string.IsNullOrEmpty(destinationFilePath))
                throw new ArgumentException("Value cannot be null or empty.", "destinationFilePath");
            if (urls == null) throw new ArgumentNullException("urls");
            if (size <= 0) throw new ArgumentOutOfRangeException("size");

            _logger = PatcherLogManager.DefaultLogger;
            _destinationFilePath = destinationFilePath;
            _urls = urls;
            _chunksData = chunksData;
            _size = size;
        }

        private ChunkedFileStream OpenFileStream(CancellationToken cancellationToken)
        {
            var parentDirectory = Path.GetDirectoryName(_destinationFilePath);
            if (!string.IsNullOrEmpty(parentDirectory))
            {
                DirectoryOperations.CreateDirectory(parentDirectory, cancellationToken);
            }

            var chunksRange = CalculateContainingChunksRange(_range);
            int startChunk = (int) (chunksRange.Start / _chunksData.ChunkSize);
            int endChunk = (int) _range.End;

            if (_range.End != -1)
            {
                endChunk = (int) (chunksRange.End / _chunksData.ChunkSize);

                if (chunksRange.End % _chunksData.ChunkSize != 0)
                {
                    endChunk += 1;
                }
            }

            _logger.LogTrace(string.Format("Opening chunked file stream for chunks {0}-{1}", startChunk, endChunk));
            return ChunkedFileStream.CreateChunkedFileStream(_destinationFilePath, _size, _chunksData,
                HashFunction, ChunkedFileStream.WorkFlags.PreservePreviousFile, startChunk, endChunk);
        }

        public void SetRange(BytesRange range)
        {
            _range = range;
        }

        public void Download(CancellationToken cancellationToken)
        {
            try
            {
                _logger.LogDebug("Downloading...");
                _logger.LogTrace("size = " + _size);
                for (int i = 0; i < _urls.Length; i++)
                {
                    _logger.LogTrace("urls[" + i + "].Url = " + _urls[i].Url);
                    _logger.LogTrace("urls[" + i + "].Country = " + _urls[i].Country);
                    _logger.LogTrace("urls[" + i + "].PartSize = " + _urls[i].PartSize);
                }

                _logger.LogTrace("chunksData.ChunkSize = " + _chunksData.ChunkSize);
                _logger.LogTrace("chunksData.Chunks.Length = " + _chunksData.Chunks.Length);

                Assert.MethodCalledOnlyOnce(ref _downloadHasBeenCalled, "Download");

                using (var fileStream = OpenFileStream(cancellationToken))
                {
                    bool retry;

                    do
                    {
                        var urlsWithBackup = Enumerable.Range(0, _urls.Length)
                            // .Select(idx => _urls.Length - (idx + 1)) // Reverse, only for testing purposes
                            .Select(idx => {
                                    var nextIdx = idx + 1;
                                    if (nextIdx >= _urls.Length)
                                    {
                                        return new UrlPair(_urls[idx]);
                                    }
                                    else
                                    {
                                        return new UrlPair(_urls[idx], _urls[nextIdx]);
                                    }
                                });

                        bool success =
                            urlsWithBackup.Any(urlPair => TryDownload(urlPair.Primary, urlPair.Secondary, fileStream, cancellationToken));

                        if (success)
                        {
                            retry = false;
                        }
                        else
                        {
                            _logger.LogWarning("All server requests have failed. Checking if retry is possible...");
                            _retryStrategy.OnRequestFailure();
                            _timeoutCalculator.OnRequestFailure();
                            retry = _retryStrategy.ShouldRetry;

                            if (!retry)
                            {
                                throw new DownloadFailureException("Download failure.");
                            }

                            _logger.LogDebug(string.Format("Retry is possible. Waiting {0}ms until before attempt...",
                                _retryStrategy.DelayBeforeNextTry));
                            Threading.CancelableSleep(_retryStrategy.DelayBeforeNextTry, cancellationToken);
                            _logger.LogDebug("Trying to download data once again from each server...");
                        }
                    } while (retry);
                }

                _logger.LogDebug("Downloading finished.");
            }
            catch (Exception e)
            {
                _logger.LogDebug("Download has failed.", e);
                throw;
            }
        }

        private bool TryDownload(ResourceUrl url, ResourceUrl? secondaryUrl, ChunkedFileStream fileStream, CancellationToken cancellationToken)
        {
            try
            {
                _logger.LogDebug(string.Format("Downloading from {0}", url));
                if (!secondaryUrl.HasValue)
                {
                    _logger.LogDebug("Secondary url is null");
                }

                NodeTester secondaryNodeTester = null;
                var stopwatch = new Stopwatch();
                stopwatch.Start();

                var calculator = new DownloadSpeedCalculator();
                IEnumerable<DownloadJob> downloadJobQueue = BuildDownloadJobQueue(url, fileStream.VerifiedLength);

                foreach (var downloadJob in downloadJobQueue)
                {
                    var baseHttpDownloader = new BaseHttpStream(downloadJob.Url, _timeoutCalculator.Timeout);
                    baseHttpDownloader.SetBytesRange(downloadJob.Range);

                    foreach (var dataPacket in baseHttpDownloader.Download(cancellationToken))
                    {
                        if (secondaryUrl.HasValue && stopwatch.IsRunning && stopwatch.Elapsed > TimeSpan.FromSeconds(0))
                        {
                            if (secondaryNodeTester == null)
                            {
                                _logger.LogDebug("Testing secondary url");
                                secondaryNodeTester = new NodeTester(secondaryUrl.Value.Url);
                                secondaryNodeTester.Start(cancellationToken);

                                stopwatch.Reset();
                                stopwatch.Start();
                            }
                            else
                            {
                                if (secondaryNodeTester.IsReady)
                                {
                                    _logger.LogDebug("Secondary url test finished.");
                                    if (secondaryNodeTester.BytesPerSecond > 2 * calculator.BytesPerSecond)
                                    {
                                        _logger.LogDebug("Secondary url download speed is 2 times faster, switching...");
                                        return false;
                                    }
                                    stopwatch.Stop();
                                }
                            }
                        }
                        var bytes = dataPacket.Data;
                        var length = dataPacket.Length;

                        fileStream.Write(bytes, 0, length);

                        calculator.AddSample(length, DateTime.Now);

                        OnDownloadProgressChanged(fileStream.VerifiedLength);
                    }
                }

                if (fileStream.RemainingLength != 0)
                {
                    throw new IncompleteDataException("Chunks downloading must finish downloading whole file");
                }

                return true;
            }
            catch (IncompleteDataException e)
            {
                _logger.LogWarning(string.Format("Unable to download from {0}", url.Url), e);
                return false;
            }
            catch (InvalidChunkDataException e)
            {
                _logger.LogWarning(string.Format("Unable to download from {0}", url.Url), e);
                return false;
            }
            catch (DataNotAvailableException e)
            {
                _logger.LogWarning(string.Format("Unable to download from {0}", url.Url), e);
                return false;
            }
            catch (ServerErrorException e)
            {
                _logger.LogWarning(string.Format("Unable to download from {0}", url.Url), e);
                return false;
            }
            catch (ConnectionFailureException e)
            {
                _logger.LogWarning(string.Format("Unable to download from {0}", url.Url), e);
                return false;
            }
        }

        public BytesRange CalculateContainingChunksRange(BytesRange range)
        {
            return range.Chunkify(_chunksData);
        }

        private IEnumerable<DownloadJob> BuildDownloadJobQueue(ResourceUrl resourceUrl, long currentOffset)
        {
            return BuildDownloadJobQueue(resourceUrl, currentOffset, _range, _size, _chunksData);
        }

        public static IEnumerable<DownloadJob> BuildDownloadJobQueue(ResourceUrl resourceUrl, long currentOffset, BytesRange range, long dataSize, ChunksData chunksData)
        {
            // The effective range is the original range contained within multiples of chunk size
            BytesRange effectiveRange = range.Chunkify(chunksData);
            var dataBounds = new BytesRange(currentOffset, -1);

            BytesRange bounds = effectiveRange.ContainIn(dataBounds);

            // An uncommon edge case might occur, in which bounds.Start is equal to dataSize,
            // this would cause the download to continue forever, with every request crashing due to invalid range header
            if (bounds.Start >= dataSize)
            {
                yield break;
            }

            if (resourceUrl.PartSize == 0)
            {
                yield return new DownloadJob(resourceUrl.Url, bounds.Start, bounds.End);
                yield break;
            }

            long partSize = resourceUrl.PartSize;

            int firstPart = (int) (bounds.Start / partSize);
            int totalPartCount = (int) (dataSize / partSize);

            if (dataSize % partSize != 0)
            {
                totalPartCount += 1;
            }


            int lastPart = totalPartCount;

            if (bounds.End != -1)
            {
                lastPart = (int) (bounds.End / partSize);
                if (bounds.End % partSize != 0)
                {
                    lastPart += 1;
                }
            }

            long lastByte = dataSize - 1;

            for (int i = firstPart; i < lastPart; i++)
            {
                string url = resourceUrl.Url;
                if (i > 0)
                {
                    // second and later indices should have index numebr at the end
                    url += "." + i;
                }

                BytesRange partRange = BytesRangeUtils.Make(i * partSize, (i+1) * partSize - 1, lastByte);
                BytesRange localBounds = bounds.LocalizeTo(partRange);

                yield return new DownloadJob(url, localBounds.Start, localBounds.End);
            }
        }

        private static byte[] HashFunction(byte[] buffer, int offset, int length)
        {
            return HashCalculator.ComputeHash(buffer, offset, length).Reverse().ToArray();
        }

        private void OnDownloadProgressChanged(long downloadedBytes)
        {
            if (DownloadProgressChanged != null) DownloadProgressChanged(downloadedBytes);
        }
    }
}