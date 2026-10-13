using System;
using System.Collections.Generic;
using System.Text;

namespace Rambutan.App.Controls;

/// <summary>
/// A QR code (ISO/IEC 18004) for a short text: byte mode, error correction
/// level M, versions 1 to 10 (up to 213 bytes, far more than a pairing link
/// needs). Written here rather than taken from a package: the common .NET QR
/// package brings System.Drawing with it, and the encoder is small. Follows the
/// structure of Project Nayuki's reference encoder.
/// </summary>
public sealed class QrCode
{
    public const int MaxVersion = 10;

    // Level M, versions 1..10 (index 0 unused).
    private static readonly int[] EccCodewordsPerBlock = [-1, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26];
    private static readonly int[] EccBlocks = [-1, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5];

    /// <summary>Level M's two format bits.</summary>
    private const int EccFormatBits = 0;

    private readonly bool[,] _modules;
    private readonly bool[,] _isFunction;

    public int Version { get; }

    /// <summary>Modules per side, without the quiet zone.</summary>
    public int Size { get; }

    public int Mask { get; private set; }

    /// <summary>Whether the module at column <paramref name="x"/>, row <paramref name="y"/> is dark.</summary>
    public bool this[int x, int y] => _modules[y, x];

    private QrCode(int version)
    {
        Version = version;
        Size = version * 4 + 17;
        _modules = new bool[Size, Size];
        _isFunction = new bool[Size, Size];
    }

    /// <summary>Encodes <paramref name="text"/> as UTF-8 in the smallest version that holds it.</summary>
    /// <exception cref="ArgumentException">The text is too long for version 10.</exception>
    public static QrCode Encode(string text)
    {
        var data = Encoding.UTF8.GetBytes(text);
        int version;
        for (version = 1; ; version++)
        {
            if (version > MaxVersion)
                throw new ArgumentException($"Text of {data.Length} bytes is too long for a QR code here.", nameof(text));
            var countBits = version < 10 ? 8 : 16;
            if (4 + countBits + data.Length * 8 <= DataCodewords(version) * 8)
                break;
        }

        var bits = new BitBuffer();
        bits.Append(0b0100, 4); // byte mode
        bits.Append(data.Length, version < 10 ? 8 : 16);
        foreach (var b in data)
            bits.Append(b, 8);

        var capacity = DataCodewords(version) * 8;
        bits.Append(0, Math.Min(4, capacity - bits.Count)); // terminator
        bits.Append(0, (8 - bits.Count % 8) % 8);
        for (var pad = 0xEC; bits.Count < capacity; pad ^= 0xEC ^ 0x11)
            bits.Append(pad, 8);

        var code = new QrCode(version);
        code.DrawFunctionPatterns();
        code.DrawCodewords(code.AddEccAndInterleave(bits.ToBytes()));
        code.ChooseMask();
        return code;
    }

    private static int RawDataModules(int version)
    {
        var result = (16 * version + 128) * version + 64;
        if (version >= 2)
        {
            var numAlign = version / 7 + 2;
            result -= (25 * numAlign - 10) * numAlign - 55;
            if (version >= 7)
                result -= 36;
        }
        return result;
    }

    private static int DataCodewords(int version) =>
        RawDataModules(version) / 8 - EccCodewordsPerBlock[version] * EccBlocks[version];

    // ---- Function patterns ----

    private void DrawFunctionPatterns()
    {
        for (var i = 0; i < Size; i++)
        {
            SetFunction(6, i, i % 2 == 0);
            SetFunction(i, 6, i % 2 == 0);
        }

        DrawFinder(3, 3);
        DrawFinder(Size - 4, 3);
        DrawFinder(3, Size - 4);

        var align = AlignmentPositions();
        var n = align.Length;
        for (var i = 0; i < n; i++)
        {
            for (var j = 0; j < n; j++)
            {
                if ((i == 0 && j == 0) || (i == 0 && j == n - 1) || (i == n - 1 && j == 0))
                    continue;
                DrawAlignment(align[i], align[j]);
            }
        }

        DrawFormatBits(0); // reserved now, written for real once the mask is chosen
        DrawVersion();
    }

    private int[] AlignmentPositions()
    {
        if (Version == 1)
            return [];
        var numAlign = Version / 7 + 2;
        var step = (Version * 8 + numAlign * 3 + 5) / (numAlign * 4 - 4) * 2;
        var result = new int[numAlign];
        result[0] = 6;
        for (int i = numAlign - 1, pos = Size - 7; i >= 1; i--, pos -= step)
            result[i] = pos;
        return result;
    }

    private void DrawFinder(int x, int y)
    {
        for (var dy = -4; dy <= 4; dy++)
        {
            for (var dx = -4; dx <= 4; dx++)
            {
                var dist = Math.Max(Math.Abs(dx), Math.Abs(dy));
                int xx = x + dx, yy = y + dy;
                if (xx >= 0 && xx < Size && yy >= 0 && yy < Size)
                    SetFunction(xx, yy, dist != 2 && dist != 4);
            }
        }
    }

    private void DrawAlignment(int x, int y)
    {
        for (var dy = -2; dy <= 2; dy++)
            for (var dx = -2; dx <= 2; dx++)
                SetFunction(x + dx, y + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
    }

    private void DrawFormatBits(int mask)
    {
        var data = EccFormatBits << 3 | mask;
        var rem = data;
        for (var i = 0; i < 10; i++)
            rem = (rem << 1) ^ ((rem >> 9) * 0x537);
        var bits = (data << 10 | rem) ^ 0x5412;

        for (var i = 0; i <= 5; i++)
            SetFunction(8, i, Bit(bits, i));
        SetFunction(8, 7, Bit(bits, 6));
        SetFunction(8, 8, Bit(bits, 7));
        SetFunction(7, 8, Bit(bits, 8));
        for (var i = 9; i < 15; i++)
            SetFunction(14 - i, 8, Bit(bits, i));

        for (var i = 0; i < 8; i++)
            SetFunction(Size - 1 - i, 8, Bit(bits, i));
        for (var i = 8; i < 15; i++)
            SetFunction(8, Size - 15 + i, Bit(bits, i));
        SetFunction(8, Size - 8, true); // the dark module
    }

    private void DrawVersion()
    {
        if (Version < 7)
            return;
        var rem = Version;
        for (var i = 0; i < 12; i++)
            rem = (rem << 1) ^ ((rem >> 11) * 0x1F25);
        var bits = Version << 12 | rem;

        for (var i = 0; i < 18; i++)
        {
            var bit = Bit(bits, i);
            int a = Size - 11 + i % 3, b = i / 3;
            SetFunction(a, b, bit);
            SetFunction(b, a, bit);
        }
    }

    private void SetFunction(int x, int y, bool dark)
    {
        _modules[y, x] = dark;
        _isFunction[y, x] = true;
    }

    private static bool Bit(int value, int i) => ((value >> i) & 1) != 0;

    // ---- Data ----

    private byte[] AddEccAndInterleave(byte[] data)
    {
        var numBlocks = EccBlocks[Version];
        var eccLen = EccCodewordsPerBlock[Version];
        var raw = RawDataModules(Version) / 8;
        var numShort = numBlocks - raw % numBlocks;
        var shortLen = raw / numBlocks;

        var divisor = ReedSolomonDivisor(eccLen);
        var blocks = new List<byte[]>();
        for (int i = 0, k = 0; i < numBlocks; i++)
        {
            var datLen = shortLen - eccLen + (i < numShort ? 0 : 1);
            var dat = data.AsSpan(k, datLen);
            k += datLen;
            var ecc = ReedSolomonRemainder(dat, divisor);
            var block = new byte[shortLen + 1];
            dat.CopyTo(block);
            // A short block has a gap where a long block's extra data codeword is.
            ecc.CopyTo(block.AsSpan(datLen + (i < numShort ? 1 : 0)));
            blocks.Add(block);
        }

        var result = new List<byte>(raw);
        for (var i = 0; i < shortLen + 1; i++)
        {
            for (var j = 0; j < blocks.Count; j++)
            {
                if (i != shortLen - eccLen || j >= numShort)
                    result.Add(blocks[j][i]);
            }
        }
        return result.ToArray();
    }

    private void DrawCodewords(byte[] data)
    {
        var i = 0;
        for (var right = Size - 1; right >= 1; right -= 2)
        {
            if (right == 6)
                right = 5;
            for (var vert = 0; vert < Size; vert++)
            {
                for (var j = 0; j < 2; j++)
                {
                    var x = right - j;
                    var upward = ((right + 1) & 2) == 0;
                    var y = upward ? Size - 1 - vert : vert;
                    if (!_isFunction[y, x] && i < data.Length * 8)
                    {
                        _modules[y, x] = ((data[i >> 3] >> (7 - (i & 7))) & 1) != 0;
                        i++;
                    }
                    // Remainder bits stay light.
                }
            }
        }
    }

    // ---- Masking ----

    private void ChooseMask()
    {
        var best = 0;
        var bestPenalty = int.MaxValue;
        for (var mask = 0; mask < 8; mask++)
        {
            ApplyMask(mask);
            DrawFormatBits(mask);
            var penalty = Penalty();
            if (penalty < bestPenalty)
            {
                best = mask;
                bestPenalty = penalty;
            }
            ApplyMask(mask); // XOR again undoes it
        }

        Mask = best;
        ApplyMask(best);
        DrawFormatBits(best);
    }

    private void ApplyMask(int mask)
    {
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                if (_isFunction[y, x])
                    continue;
                var invert = mask switch
                {
                    0 => (x + y) % 2 == 0,
                    1 => y % 2 == 0,
                    2 => x % 3 == 0,
                    3 => (x + y) % 3 == 0,
                    4 => (x / 3 + y / 2) % 2 == 0,
                    5 => x * y % 2 + x * y % 3 == 0,
                    6 => (x * y % 2 + x * y % 3) % 2 == 0,
                    _ => ((x + y) % 2 + x * y % 3) % 2 == 0,
                };
                if (invert)
                    _modules[y, x] = !_modules[y, x];
            }
        }
    }

    /// <summary>The standard's four penalty rules, so the chosen mask avoids
    /// patterns that confuse a scanner.</summary>
    private int Penalty()
    {
        var result = 0;
        for (var pass = 0; pass < 2; pass++)
        {
            for (var a = 0; a < Size; a++)
            {
                var run = 1;
                for (var b = 1; b < Size; b++)
                {
                    if (At(pass, a, b) == At(pass, a, b - 1))
                    {
                        run++;
                        if (run == 5)
                            result += 3;
                        else if (run > 5)
                            result++;
                    }
                    else
                    {
                        run = 1;
                    }
                }

                // Finder-like 1:1:3:1:1 with four light modules on one side.
                for (var b = 0; b + 11 <= Size; b++)
                {
                    if (Matches(pass, a, b, FinderLike) || Matches(pass, a, b, FinderLikeReversed))
                        result += 40;
                }
            }
        }

        var dark = 0;
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                if (_modules[y, x])
                    dark++;
                if (x + 1 < Size && y + 1 < Size)
                {
                    var c = _modules[y, x];
                    if (c == _modules[y, x + 1] && c == _modules[y + 1, x] && c == _modules[y + 1, x + 1])
                        result += 3;
                }
            }
        }

        var total = Size * Size;
        var k = (Math.Abs(dark * 20 - total * 10) + total - 1) / total - 1;
        return result + k * 10;
    }

    private static readonly bool[] FinderLike = [true, false, true, true, true, false, true, false, false, false, false];
    private static readonly bool[] FinderLikeReversed = [false, false, false, false, true, false, true, true, true, false, true];

    /// <summary>Pass 0 reads rows, pass 1 columns.</summary>
    private bool At(int pass, int line, int i) => pass == 0 ? _modules[line, i] : _modules[i, line];

    private bool Matches(int pass, int line, int start, bool[] pattern)
    {
        for (var i = 0; i < pattern.Length; i++)
        {
            if (At(pass, line, start + i) != pattern[i])
                return false;
        }
        return true;
    }

    // ---- Reed–Solomon over GF(2^8), polynomial 0x11D ----

    private static byte[] ReedSolomonDivisor(int degree)
    {
        var result = new byte[degree];
        result[degree - 1] = 1;
        var root = 1;
        for (var i = 0; i < degree; i++)
        {
            for (var j = 0; j < degree; j++)
            {
                result[j] = Multiply(result[j], root);
                if (j + 1 < degree)
                    result[j] ^= result[j + 1];
            }
            root = Multiply(root, 0x02);
        }
        return result;
    }

    private static byte[] ReedSolomonRemainder(ReadOnlySpan<byte> data, byte[] divisor)
    {
        var result = new byte[divisor.Length];
        foreach (var b in data)
        {
            var factor = b ^ result[0];
            Array.Copy(result, 1, result, 0, result.Length - 1);
            result[^1] = 0;
            for (var i = 0; i < result.Length; i++)
                result[i] ^= Multiply(divisor[i], factor);
        }
        return result;
    }

    private static byte Multiply(int x, int y)
    {
        var z = 0;
        for (var i = 7; i >= 0; i--)
        {
            z = (z << 1) ^ ((z >> 7) * 0x11D);
            z ^= ((y >> i) & 1) * x;
        }
        return (byte)z;
    }

    private sealed class BitBuffer
    {
        private readonly List<bool> _bits = [];

        public int Count => _bits.Count;

        public void Append(int value, int length)
        {
            for (var i = length - 1; i >= 0; i--)
                _bits.Add(((value >> i) & 1) != 0);
        }

        public byte[] ToBytes()
        {
            var result = new byte[_bits.Count / 8];
            for (var i = 0; i < _bits.Count; i++)
            {
                if (_bits[i])
                    result[i >> 3] |= (byte)(0x80 >> (i & 7));
            }
            return result;
        }
    }
}
