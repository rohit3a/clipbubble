using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ClipBubble;

/// QR codes for the pairing link, so Link iPhone needs no package. The same encoder as Linux/clipbubble/qr.py: byte
/// mode at error correction level M, following ISO/IEC 18004 as laid out in Project Nayuki's reference encoder.
static class Qr
{
    // Per version, for level M: error correction codewords in each block, and the number of blocks.
    static readonly int[] EccPerBlock = { -1, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26, 30, 22, 22, 24, 24, 28, 28, 26, 26, 26, 26,
        28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28 };
    static readonly int[] Blocks = { -1, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5, 5, 8, 9, 9, 10, 10, 11, 13, 14, 16, 17, 17, 18, 20, 21, 23,
        25, 26, 28, 29, 31, 33, 35, 37, 38, 40, 43, 45, 47, 49 };
    const int LevelM = 0;

    /// The modules of the smallest code that holds `text`, [y, x] with true for dark, without a quiet zone.
    public static bool[,] Encode(string text)
    {
        var data = new UTF8Encoding(false).GetBytes(text);
        int version = 1, countBits = 8, capacity = 0;
        for (; version <= 40; version++)
        {
            countBits = version < 10 ? 8 : 16;
            capacity = DataCodewords(version) * 8;
            if (4 + countBits + data.Length * 8 <= capacity) break;
        }
        if (version > 40) throw new ArgumentException("too long for a QR code");
        var bits = new List<bool>();
        void Put(int value, int n) { for (var i = n - 1; i >= 0; i--) bits.Add(((value >> i) & 1) == 1); }
        Put(0b0100, 4);
        Put(data.Length, countBits);
        foreach (var b in data) Put(b, 8);
        Put(0, Math.Min(4, capacity - bits.Count));
        Put(0, (8 - bits.Count % 8) % 8);
        for (var pad = 0xEC; bits.Count < capacity; pad ^= 0xEC ^ 0x11) Put(pad, 8);
        var codewords = new int[bits.Count / 8];
        for (var i = 0; i < bits.Count; i++) if (bits[i]) codewords[i >> 3] |= 0x80 >> (i & 7);
        return new Symbol(version, Interleave(version, codewords)).Modules;
    }

    static int RawModules(int version)
    {
        var n = (16 * version + 128) * version + 64;
        if (version >= 2)
        {
            var align = version / 7 + 2;
            n -= (25 * align - 10) * align - 55;
            if (version >= 7) n -= 36;
        }
        return n;
    }

    static int DataCodewords(int version) => RawModules(version) / 8 - EccPerBlock[version] * Blocks[version];

    static int GfMul(int x, int y)
    {
        var z = 0;
        for (var i = 7; i >= 0; i--)
        {
            z = (z << 1) ^ ((z >> 7) * 0x11D);
            z ^= ((y >> i) & 1) * x;
        }
        return z;
    }

    static int[] RsDivisor(int degree)
    {
        var result = new int[degree];
        result[degree - 1] = 1;
        var root = 1;
        for (var k = 0; k < degree; k++)
        {
            for (var j = 0; j < degree; j++)
            {
                result[j] = GfMul(result[j], root);
                if (j + 1 < degree) result[j] ^= result[j + 1];
            }
            root = GfMul(root, 2);
        }
        return result;
    }

    static int[] RsRemainder(IEnumerable<int> data, int[] divisor)
    {
        var result = new int[divisor.Length];
        foreach (var b in data)
        {
            var factor = b ^ result[0];
            Array.Copy(result, 1, result, 0, result.Length - 1);
            result[result.Length - 1] = 0;
            for (var i = 0; i < divisor.Length; i++) result[i] ^= GfMul(divisor[i], factor);
        }
        return result;
    }

    static int[] Interleave(int version, int[] data)
    {
        int blocksN = Blocks[version], eccLen = EccPerBlock[version], raw = RawModules(version) / 8;
        int shortN = blocksN - raw % blocksN, shortLen = raw / blocksN;
        var divisor = RsDivisor(eccLen);
        var blocks = new List<List<int>>();
        for (int i = 0, k = 0; i < blocksN; i++)
        {
            var dat = data.Skip(k).Take(shortLen - eccLen + (i < shortN ? 0 : 1)).ToList();
            k += dat.Count;
            var ecc = RsRemainder(dat, divisor);
            if (i < shortN) dat.Add(0);
            dat.AddRange(ecc);
            blocks.Add(dat);
        }
        var result = new List<int>();
        for (var i = 0; i < blocks[0].Count; i++)
            for (var j = 0; j < blocks.Count; j++)
                if (i != shortLen - eccLen || j >= shortN) result.Add(blocks[j][i]);
        return result.ToArray();
    }

    sealed class Symbol
    {
        readonly int version, size;
        public readonly bool[,] Modules;
        readonly bool[,] isFunction;

        public Symbol(int version, int[] codewords)
        {
            this.version = version;
            size = version * 4 + 17;
            Modules = new bool[size, size];
            isFunction = new bool[size, size];
            FunctionPatterns();
            Codewords(codewords);
            int best = 0, lowest = int.MaxValue;
            for (var mask = 0; mask < 8; mask++)
            {
                var p = PenaltyWith(mask);
                if (p < lowest) (best, lowest) = (mask, p);
            }
            Mask(best);
            Format(best);
        }

        void Set(int x, int y, bool dark)
        {
            Modules[y, x] = dark;
            isFunction[y, x] = true;
        }

        void FunctionPatterns()
        {
            var n = size;
            for (var i = 0; i < n; i++)
            {
                Set(6, i, i % 2 == 0);
                Set(i, 6, i % 2 == 0);
            }
            foreach (var (x, y) in new[] { (3, 3), (n - 4, 3), (3, n - 4) })
                for (var dy = -4; dy <= 4; dy++)
                    for (var dx = -4; dx <= 4; dx++)
                        if (x + dx >= 0 && x + dx < n && y + dy >= 0 && y + dy < n)
                        {
                            var d = Math.Max(Math.Abs(dx), Math.Abs(dy));
                            Set(x + dx, y + dy, d != 2 && d != 4);
                        }
            var pos = AlignmentPositions();
            var last = pos.Length - 1;
            for (var i = 0; i < pos.Length; i++)
                for (var j = 0; j < pos.Length; j++)
                {
                    if ((i == 0 && j == 0) || (i == 0 && j == last) || (i == last && j == 0)) continue;
                    for (var dy = -2; dy <= 2; dy++)
                        for (var dx = -2; dx <= 2; dx++)
                            Set(pos[i] + dx, pos[j] + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
                }
            Format(0);
            if (version < 7) return;
            var rem = version;
            for (var i = 0; i < 12; i++) rem = (rem << 1) ^ ((rem >> 11) * 0x1F25);
            var bits = version << 12 | rem;
            for (var i = 0; i < 18; i++)
            {
                var dark = ((bits >> i) & 1) == 1;
                int a = n - 11 + i % 3, b = i / 3;
                Set(a, b, dark);
                Set(b, a, dark);
            }
        }

        int[] AlignmentPositions()
        {
            if (version == 1) return new int[0];
            var align = version / 7 + 2;
            var step = (version * 8 + align * 3 + 5) / (align * 4 - 4) * 2;
            var result = Enumerable.Range(0, align - 1).Select(i => size - 7 - i * step).ToList();
            result.Add(6);
            result.Reverse();
            return result.ToArray();
        }

        void Format(int mask)
        {
            var data = LevelM << 3 | mask;
            var rem = data;
            for (var i = 0; i < 10; i++) rem = (rem << 1) ^ ((rem >> 9) * 0x537);
            var bits = (data << 10 | rem) ^ 0x5412;
            bool Bit(int i) => ((bits >> i) & 1) == 1;
            var n = size;
            for (var i = 0; i < 6; i++) Set(8, i, Bit(i));
            Set(8, 7, Bit(6));
            Set(8, 8, Bit(7));
            Set(7, 8, Bit(8));
            for (var i = 9; i < 15; i++) Set(14 - i, 8, Bit(i));
            for (var i = 0; i < 8; i++) Set(n - 1 - i, 8, Bit(i));
            for (var i = 8; i < 15; i++) Set(8, n - 15 + i, Bit(i));
            Set(8, n - 8, true);
        }

        void Codewords(int[] data)
        {
            int n = size, i = 0;
            for (var right = n - 1; right >= 1; right -= 2)
            {
                if (right == 6) right = 5;
                for (var vert = 0; vert < n; vert++)
                    for (var j = 0; j < 2; j++)
                    {
                        var x = right - j;
                        var y = ((right + 1) & 2) == 0 ? n - 1 - vert : vert;
                        if (!isFunction[y, x] && i < data.Length * 8)
                        {
                            Modules[y, x] = ((data[i >> 3] >> (7 - (i & 7))) & 1) == 1;
                            i++;
                        }
                    }
            }
        }

        void Mask(int mask)
        {
            for (var y = 0; y < size; y++)
                for (var x = 0; x < size; x++)
                {
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
                    if (invert && !isFunction[y, x]) Modules[y, x] = !Modules[y, x];
                }
        }

        /// Long runs, 2×2 blocks and an uneven balance of dark and light make a code harder to read. Masks are
        /// their own inverse, so trying one is applying it twice.
        int PenaltyWith(int mask)
        {
            Mask(mask);
            Format(mask);
            int n = size, score = 0;
            foreach (var columns in new[] { false, true })
                for (var a = 0; a < n; a++)
                {
                    var run = 1;
                    for (var b = 1; b < n; b++)
                    {
                        if (At(a, b, columns) == At(a, b - 1, columns)) run++;
                        else { if (run >= 5) score += run - 2; run = 1; }
                    }
                    if (run >= 5) score += run - 2;
                }
            for (var y = 0; y < n - 1; y++)
                for (var x = 0; x < n - 1; x++)
                    if (Modules[y, x] == Modules[y, x + 1] && Modules[y, x] == Modules[y + 1, x] && Modules[y, x] == Modules[y + 1, x + 1])
                        score += 3;
            var dark = 0;
            foreach (var m in Modules) if (m) dark++;
            score += (Math.Abs(dark * 20 - n * n * 10) + n * n - 1) / (n * n) * 10 - 10;
            Mask(mask);
            return score;
        }

        bool At(int line, int i, bool columns) => columns ? Modules[i, line] : Modules[line, i];
    }
}
