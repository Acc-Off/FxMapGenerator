// Copied from FxDeck 0.7.0 (https://github.com/Acc-Off/FxDeck, MIT, same author): the console socket client.
// Only the namespace differs; keep the two in step.
using System.Buffers.Binary;

namespace FxMapGenerator.App.FxConsole;

/// <summary>
/// Incremental parser for the incoming side of the console socket.
/// Feed it TCP chunks of any size; it re-synchronises on garbage and bogus lengths
/// the same way fxcommands does.
/// </summary>
public sealed class FxConsoleFrameParser
{
    private byte[] _buffer = new byte[4096];
    private int _length;

    /// <summary>Bytes currently buffered while waiting for the rest of a frame.</summary>
    public int BufferedBytes => _length;

    /// <summary>Appends <paramref name="data"/> and adds every complete frame found to <paramref name="output"/>.</summary>
    /// <returns>The number of frames added.</returns>
    public int Feed(ReadOnlySpan<byte> data, ICollection<FxConsoleFrame> output)
    {
        ArgumentNullException.ThrowIfNull(output);

        EnsureCapacity(_length + data.Length);
        data.CopyTo(_buffer.AsSpan(_length));
        _length += data.Length;

        var produced = 0;
        var offset = 0;
        while (_length - offset >= FxConsoleProtocol.HeaderSize)
        {
            var view = _buffer.AsSpan(offset, _length - offset);

            if (!FxConsoleProtocol.TryGetFrameType(view, out var type))
            {
                // Not aligned on a frame. Skip ahead to the next known magic, always making forward progress.
                var next = IndexOfAnyMagic(view[1..]);
                if (next < 0)
                {
                    // Nothing recognisable; keep a short tail in case a magic straddles the chunk boundary.
                    offset += view.Length - 3;
                    break;
                }

                offset += 1 + next;
                continue;
            }

            // On incoming frames the length field is the TOTAL frame size, header included.
            var totalSize = BinaryPrimitives.ReadUInt32BigEndian(view.Slice(6, 4));
            if (totalSize < FxConsoleProtocol.HeaderSize || totalSize > FxConsoleProtocol.MaxFrameSize)
            {
                // Bogus length: drop this magic and resync rather than waiting for bytes that will never come.
                offset += 4;
                continue;
            }

            if (view.Length < totalSize)
            {
                break; // incomplete, wait for more data
            }

            output.Add(new FxConsoleFrame(type, view[..(int)totalSize].ToArray()));
            produced++;
            offset += (int)totalSize;
        }

        if (offset > 0)
        {
            Buffer.BlockCopy(_buffer, offset, _buffer, 0, _length - offset);
            _length -= offset;
        }

        return produced;
    }

    /// <summary>Discards any partially buffered data (call after a reconnect).</summary>
    public void Reset() => _length = 0;

    private static int IndexOfAnyMagic(ReadOnlySpan<byte> span)
    {
        var best = -1;
        Consider(span.IndexOf(FxConsoleProtocol.PrintMagic), ref best);
        Consider(span.IndexOf(FxConsoleProtocol.ChannelMagic), ref best);
        Consider(span.IndexOf(FxConsoleProtocol.CvarMagic), ref best);
        Consider(span.IndexOf(FxConsoleProtocol.AppInfoMagic), ref best);
        return best;

        static void Consider(int index, ref int best)
        {
            if (index >= 0 && (best < 0 || index < best))
            {
                best = index;
            }
        }
    }

    private void EnsureCapacity(int required)
    {
        if (_buffer.Length >= required)
        {
            return;
        }

        Array.Resize(ref _buffer, Math.Max(required, _buffer.Length * 2));
    }
}
