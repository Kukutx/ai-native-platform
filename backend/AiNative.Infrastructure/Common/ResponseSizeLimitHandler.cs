using System.Net;

namespace AiNative.Infrastructure.Common;

/// <summary>
/// 响应超过大小上限。HttpClient 和调用方的读取代码会把它包在别的异常里抛出。
/// </summary>
public sealed class ResponseTooLargeException(long maxBytes)
    : IOException($"外部服务的响应超过 {Size(maxBytes)}，已停止读取。")
{
    private static string Size(long bytes) => bytes switch
    {
        >= 1024 * 1024 => $"{bytes / 1024 / 1024} MB",
        >= 1024 => $"{bytes / 1024} KB",
        _ => $"{bytes} 字节",
    };
}

/// <summary>
/// 限制单个响应的大小：读到上限就报错，外部服务返回再多内容也不会整个读进内存。
/// 只限制 POST 的响应（每次请求对应一个）；长时间保持的 GET 事件流用来接收服务端通知，不受限制。
/// </summary>
public sealed class ResponseSizeLimitHandler(long maxBytes, HttpMessageHandler innerHandler) : DelegatingHandler(innerHandler)
{
    /// <summary>
    /// 从异常链里找出响应超限的原因。
    /// </summary>
    public static ResponseTooLargeException? FindTooLarge(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is ResponseTooLargeException tooLarge)
            {
                return tooLarge;
            }
        }

        return null;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (request.Method != HttpMethod.Post)
        {
            return response;
        }

        if (response.Content.Headers.ContentLength > maxBytes)
        {
            response.Dispose();
            throw new HttpRequestException(HttpRequestError.Unknown, null, new ResponseTooLargeException(maxBytes));
        }

        response.Content = new LimitedContent(response.Content, maxBytes);
        return response;
    }

    private sealed class LimitedContent : HttpContent
    {
        private readonly HttpContent _inner;
        private readonly long _maxBytes;

        public LimitedContent(HttpContent inner, long maxBytes)
        {
            _inner = inner;
            _maxBytes = maxBytes;
            foreach (var header in inner.Headers)
            {
                Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken)
            => new LimitedStream(await _inner.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), _maxBytes);

        protected override Task<Stream> CreateContentReadStreamAsync() => CreateContentReadStreamAsync(CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            await using var source = await CreateContentReadStreamAsync(cancellationToken).ConfigureAwait(false);
            await source.CopyToAsync(stream, cancellationToken).ConfigureAwait(false);
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class LimitedStream(Stream inner, long maxBytes) : Stream
    {
        private long _read;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _read;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

        public override int Read(Span<byte> buffer) => Count(inner.Read(buffer));

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => Count(await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => Count(await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            await base.DisposeAsync().ConfigureAwait(false);
        }

        private int Count(int read)
        {
            _read += read;
            return _read > maxBytes ? throw new ResponseTooLargeException(maxBytes) : read;
        }
    }
}
