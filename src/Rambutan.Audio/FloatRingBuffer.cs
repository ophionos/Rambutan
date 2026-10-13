namespace Rambutan.Audio;

/// <summary>
/// Bounded FIFO of float samples between one decode thread (blocking writes,
/// backpressure) and the device callback (non-blocking reads).
/// </summary>
public sealed class FloatRingBuffer(int capacity)
{
    private readonly float[] _buffer = new float[capacity];
    private readonly object _gate = new();
    private int _head; // next read position
    private int _tail; // next write position
    private int _count;
    private bool _completed;

    public int Capacity => _buffer.Length;

    public int Count
    {
        get { lock (_gate) return _count; }
    }

    public long TotalRead { get; private set; }

    public bool IsCompleted
    {
        get { lock (_gate) return _completed && _count == 0; }
    }

    /// <summary>True once <see cref="Complete"/> ran (EOF or abandonment), even with samples still unread.</summary>
    public bool IsClosed
    {
        get { lock (_gate) return _completed; }
    }

    public void Write(ReadOnlySpan<float> source)
    {
        var offset = 0;
        while (offset < source.Length)
        {
            lock (_gate)
            {
                while (_count == _buffer.Length && !_completed)
                    Monitor.Wait(_gate);

                if (_completed)
                    return; // Reader abandoned the stream; drop the rest.

                var chunk = Math.Min(source.Length - offset, _buffer.Length - _count);
                var toEnd = Math.Min(chunk, _buffer.Length - _tail);
                source.Slice(offset, toEnd).CopyTo(_buffer.AsSpan(_tail));
                source.Slice(offset + toEnd, chunk - toEnd).CopyTo(_buffer);
                _tail = (_tail + chunk) % _buffer.Length;
                _count += chunk;
                offset += chunk;
            }
        }
    }

    public int Read(Span<float> destination)
    {
        lock (_gate)
        {
            var chunk = Math.Min(destination.Length, _count);
            var toEnd = Math.Min(chunk, _buffer.Length - _head);
            _buffer.AsSpan(_head, toEnd).CopyTo(destination);
            _buffer.AsSpan(0, chunk - toEnd).CopyTo(destination[toEnd..]);
            _head = (_head + chunk) % _buffer.Length;
            _count -= chunk;
            TotalRead += chunk;
            Monitor.PulseAll(_gate);
            return chunk;
        }
    }

    /// <summary>Marks the end of the stream (EOF or abandonment); unblocks writers.</summary>
    public void Complete()
    {
        lock (_gate)
        {
            _completed = true;
            Monitor.PulseAll(_gate);
        }
    }
}
