using System;
using System.IO;
using System.Net;
using System.Collections.Generic;
using JetBrains.Annotations;
using PatchKit.Logging;
using PatchKit.Network;
using PatchKit.Unity.Patcher.Cancellation;
using PatchKit.Unity.Patcher.Debug;
using PatchKit.Unity.Utilities;

namespace PatchKit.Unity.Patcher.AppData.Remote.Downloaders
{
    public sealed class BaseHttpStream : IBaseHttpStream
    {
        private readonly ILogger _logger;

        private static readonly int DefaultBufferSize = 5 * (int) Units.MB;

        private readonly string _url;
        private readonly int _timeout;
        private readonly int _bufferSize;
        private readonly IHttpClient _httpClient;

        private readonly byte[] _buffer;

        private BytesRange? _bytesRange;

        public BaseHttpStream(string url, int timeout) :
            this(url, timeout, new DefaultHttpClient(), PatcherLogManager.DefaultLogger, DefaultBufferSize)
        {
        }

        public BaseHttpStream([NotNull] string url, int timeout, [NotNull] IHttpClient httpClient,
            [NotNull] ILogger logger, int bufferSize)
        {
            if (string.IsNullOrEmpty(url)) throw new ArgumentException("Value cannot be null or empty.", "url");
            if (timeout <= 0) throw new ArgumentOutOfRangeException("timeout");
            if (httpClient == null) throw new ArgumentNullException("httpClient");
            if (logger == null) throw new ArgumentNullException("logger");

            _url = url;
            _timeout = timeout;
            _httpClient = httpClient;
            _logger = logger;

            _bufferSize = bufferSize;

            _buffer = new byte[_bufferSize];

            ServicePointManager.ServerCertificateValidationCallback =
                (sender, certificate, chain, errors) => true;
            ServicePointManager.DefaultConnectionLimit = 65535;
        }

        public void SetBytesRange(BytesRange? range)
        {
            _bytesRange = range;

            if (_bytesRange.HasValue && _bytesRange.Value.Start == 0 && _bytesRange.Value.End == -1)
            {
                _bytesRange = null;
            }
        }

        public IEnumerable<DataPacket> Download(CancellationToken cancellationToken)
        {
            try
            {
                _logger.LogDebug("Downloading...");
                _logger.LogTrace("url = " + _url);
                _logger.LogTrace("bufferSize = " + _bufferSize);
                _logger.LogTrace("bytesRange = " + (_bytesRange.HasValue
                                     ? _bytesRange.Value.Start + "-" + _bytesRange.Value.End
                                     : "(none)"));
                _logger.LogTrace("timeout = " + _timeout);

                var request = new HttpGetRequest
                {
                    Address = new Uri(_url),
                    Range = _bytesRange,
                    Timeout = _timeout,
                    ReadWriteTimeout = _timeout,
                };

                var response = _httpClient.Get(request);

                cancellationToken.ThrowIfCancellationRequested();

                _logger.LogDebug("Received response from server.");
                _logger.LogTrace("statusCode = " + response.StatusCode);

                if (Is2XXStatus(response.StatusCode))
                {
                    _logger.LogDebug("Successful response. Reading response stream...");

                    //TODO: Could response.ContentStream be null? Need to check it.

                    return ReadResponseStream(response, cancellationToken);
                }
                else if (Is4XXStatus(response.StatusCode))
                {
                    throw new DataNotAvailableException(string.Format(
                        "Request data for {0} is not available (status: {1})", _url, response.StatusCode));
                }
                else
                {
                    throw new ServerErrorException(string.Format(
                        "Server has experienced some issues with request for {0} which resulted in {1} status code.",
                        _url, response.StatusCode));
                }

            }
            catch (WebException webException)
            {
                _logger.LogError("Downloading has failed.", webException);
                throw new ConnectionFailureException(
                    string.Format("Connection to server has failed while requesting {0}", _url), webException);
            }
            catch (Exception e)
            {
                _logger.LogError("Downloading has failed.", e);
                throw;
            }
        }

        private IEnumerable<DataPacket> ReadResponseStream(PatchKit.Network.IHttpResponse response, CancellationToken cancellationToken)
        {
            using (response)
            {
                var responseStream = response.ContentStream;
                int bufferRead;
                while ((bufferRead = responseStream.Read(_buffer, 0, _bufferSize)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var dataPacket = new DataPacket { Data = _buffer, Length = bufferRead, };

                    yield return dataPacket;
                }

                _logger.LogDebug("Downloading finished.");
            }
        }

        // ReSharper disable once InconsistentNaming
        private static bool Is2XXStatus(HttpStatusCode statusCode)
        {
            return (int) statusCode >= 200 && (int) statusCode <= 299;
        }

        // ReSharper disable once InconsistentNaming
        private static bool Is4XXStatus(HttpStatusCode statusCode)
        {
            return (int) statusCode >= 400 && (int) statusCode <= 499;
        }
    }
}