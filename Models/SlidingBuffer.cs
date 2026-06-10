using System.Buffers;

namespace SmartEdgeHMI.Models;

/// <summary>高性能滑动窗口缓冲区 (零分配)</summary>
public class SlidingBuffer(int initialCapacity = 1024) : IDisposable
{
    private byte[] _buffer = ArrayPool<byte>.Shared.Rent(initialCapacity);
    private int _position = 0;

    public int Length => _position;
    public ReadOnlySpan<byte> UnreadSpan => _buffer.AsSpan(0, _position);

    public void Append(ReadOnlySpan<byte> data)
    {
        if (_position + data.Length > _buffer.Length)
        {
            // 扩容策略：租用更大的数组并拷贝旧数据, 然后归还旧数组
            byte[] newBuffer = ArrayPool<byte>.Shared.Rent((_position + data.Length) * 2);
            _buffer.AsSpan(0, _position).CopyTo(newBuffer);
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = newBuffer;
        }
        data.CopyTo(_buffer.AsSpan(_position));
        _position += data.Length;
    }

    public void Consume(int byteCount)
    {
        if (byteCount >= _position)
        {
            _position = 0;
        }
        else
        {
            // 滑动窗口：将剩余的有效数据向前拷贝
            _buffer.AsSpan(byteCount, _position - byteCount).CopyTo(_buffer);
            _position -= byteCount;
        }
    }

    public void Dispose()
    {
        if (_buffer != null)
        {
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = null!;
        }
        GC.SuppressFinalize(this);
    }
}
