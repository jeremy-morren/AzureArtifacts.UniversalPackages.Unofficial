
using System.Diagnostics;

namespace AzureArtifacts.UniversalPackages.Unofficial;

/// <summary>
/// A blob's content, handed from the download pipeline (which feeds it chunk by chunk) to
/// <see cref="IPackageDownloader.DownloadFileAsync"/> callback (which reads it) as it downloads,
/// without buffering beyond the chunk being read
/// </summary>
/// <remarks>
/// Forward-only and read-only. Each chunk fed is held until the reader has taken all of it. When the reader finishes
/// early (its task completes), the rest of the content is discarded; when it fails, feeding fails with its exception
/// </remarks>
internal sealed class BlobContentStream : Stream
{
    /// <summary>The content size</summary>
    private readonly long _length;

    /// <summary>Signalled once per chunk fed</summary>
    private readonly SemaphoreSlim _available = new(0);

    /// <summary>Signalled once per chunk the reader has taken all of</summary>
    private readonly SemaphoreSlim _taken = new(0);

    /// <summary>The chunk fed last (set before <see cref="_available"/> is signalled)</summary>
    private byte[]? _pending;

    /// <summary>The chunk being read, and how far</summary>
    private byte[]? _current;

    private int _currentOffset;

    /// <summary>The bytes read</summary>
    private long _position;

    /// <summary>The bytes fed</summary>
    private long _fed;

    /// <summary>The reader has finished: the rest of the content is discarded</summary>
    private bool _abandoned;

    /// <param name="length">The content size</param>
    public BlobContentStream(long length)
    {
        _length = length;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    
    public override long Length => _length;

    public override long Position
    {
        get => Interlocked.Read(ref _position);
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => ReadCore(Slice(buffer, offset, count).Span);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        ReadCoreAsync(Slice(buffer, offset, count), ct).AsTask();

#if NET
    public override int Read(Span<byte> buffer) => ReadCore(buffer);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
        ReadCoreAsync(buffer, ct);

    public override void Write(ReadOnlySpan<byte> buffer) => throw new NotSupportedException();

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) =>
        throw new NotSupportedException();
#endif

    private int ReadCore(Span<byte> buffer)
    {
        if (buffer.IsEmpty || Interlocked.Read(ref _position) >= _length)
            return 0;

        if (_current == null)
        {
            _available.Wait();
            TakePending();
        }
        return Copy(buffer);
    }

    private async ValueTask<int> ReadCoreAsync(Memory<byte> buffer, CancellationToken ct)
    {
        if (buffer.IsEmpty || Interlocked.Read(ref _position) >= _length)
            return 0;

        if (_current == null)
        {
            await _available.WaitAsync(ct).ConfigureAwait(false);
            TakePending();
        }
        return Copy(buffer.Span);
    }

    /// <summary>
    /// Makes the chunk just signalled the one being read.
    /// Chunks are taken only through their signal (not by looking at <see cref="_pending"/>),
    /// so signals and chunks stay paired
    /// </summary>
    private void TakePending()
    {
        Debug.Assert(_pending != null, "A chunk is fed before it is signalled");
        
        _current = _pending;
        _currentOffset = 0;
    }

    /// <summary>Copies from the chunk being read, releasing the chunk once all of it is taken</summary>
    private int Copy(Span<byte> destination)
    {
        Debug.Assert(_current != null, "A chunk is being read");
        
        var read = Math.Min(destination.Length, _current.Length - _currentOffset);
        _current.AsSpan(_currentOffset, read).CopyTo(destination);
        _currentOffset += read;
        Interlocked.Add(ref _position, read);

        if (_currentOffset == _current.Length)
        {
            _current = null;
            _taken.Release();
        }
        return read;
    }

    private static Memory<byte> Slice(byte[] buffer, int offset, int count)
    {
        if (buffer == null)
            throw new ArgumentNullException(nameof(buffer));
        if (offset < 0 || count < 0 || offset + count > buffer.Length)
            throw new ArgumentOutOfRangeException(nameof(count));
        return buffer.AsMemory(offset, count);
    }

    /// <summary>
    /// Hands a chunk to the reader.
    /// Completes once the reader has taken all of it, or has finished (when the chunk is discarded)
    /// </summary>
    /// <param name="data">The chunk's content</param>
    /// <param name="reader">The reader's work: a failure of it is thrown</param>
    /// <param name="ct">Cancels waiting for the reader</param>
    public async Task FeedAsync(byte[] data, Task reader, CancellationToken ct)
    {
        if (data.Length == 0)
            return;

        _fed += data.Length;
        if (_fed > _length)
            throw new InvalidOperationException($"More than the blob's {_length} bytes of content were fed");
        if (_abandoned)
            return;

        _pending = data;
        _available.Release();

        var taken = _taken.WaitAsync(ct);
        if (await Task.WhenAny(taken, reader).ConfigureAwait(false) == taken)
        {
            await taken.ConfigureAwait(false);
            return;
        }

        // The reader finished: throw its failure, or discard the rest
        await reader.ConfigureAwait(false);
        _abandoned = true;
    }

    // Read-only: nothing to flush (as Stream's guidance asks, rather than throwing)
    public override void Flush()
    {
    }

    public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
