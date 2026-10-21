"""QR codes for the pairing link, so Link iPhone needs no extra package. Byte mode at error correction level M,
the same as the Mac's code. Follows ISO/IEC 18004 as laid out in Project Nayuki's reference encoder."""

# Per version, for level M: error correction codewords in each block, and the number of blocks.
_ECC_PER_BLOCK = (-1, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26, 30, 22, 22, 24, 24, 28, 28, 26, 26, 26, 26, 28, 28, 28, 28,
                  28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28)
_BLOCKS = (-1, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5, 5, 8, 9, 9, 10, 10, 11, 13, 14, 16, 17, 17, 18, 20, 21, 23, 25, 26, 28, 29,
           31, 33, 35, 37, 38, 40, 43, 45, 47, 49)
_LEVEL_M = 0


def encode(text):
    """The modules of the smallest code that holds `text`, as rows of booleans (True is dark), without a quiet zone."""
    data = text.encode("utf-8")
    for version in range(1, 41):
        count_bits = 8 if version < 10 else 16
        capacity = _data_codewords(version) * 8
        if 4 + count_bits + len(data) * 8 <= capacity:
            break
    else:
        raise ValueError("too long for a QR code")
    bits = []

    def put(value, n):
        bits.extend((value >> i) & 1 for i in reversed(range(n)))

    put(0b0100, 4)
    put(len(data), count_bits)
    for b in data:
        put(b, 8)
    put(0, min(4, capacity - len(bits)))
    put(0, -len(bits) % 8)
    pad = 0xEC
    while len(bits) < capacity:
        put(pad, 8)
        pad ^= 0xEC ^ 0x11
    codewords = [int("".join(map(str, bits[i:i + 8])), 2) for i in range(0, len(bits), 8)]
    return _Symbol(version, _interleave(version, codewords)).modules


def _raw_modules(version):
    n = (16 * version + 128) * version + 64
    if version >= 2:
        align = version // 7 + 2
        n -= (25 * align - 10) * align - 55
        if version >= 7:
            n -= 36
    return n


def _data_codewords(version):
    return _raw_modules(version) // 8 - _ECC_PER_BLOCK[version] * _BLOCKS[version]


def _gf_mul(x, y):
    z = 0
    for i in reversed(range(8)):
        z = (z << 1) ^ ((z >> 7) * 0x11D)
        z ^= ((y >> i) & 1) * x
    return z


def _rs_divisor(degree):
    result = [0] * (degree - 1) + [1]
    root = 1
    for _ in range(degree):
        for j in range(degree):
            result[j] = _gf_mul(result[j], root)
            if j + 1 < degree:
                result[j] ^= result[j + 1]
        root = _gf_mul(root, 2)
    return result


def _rs_remainder(data, divisor):
    result = [0] * len(divisor)
    for b in data:
        factor = b ^ result.pop(0)
        result.append(0)
        for i, coef in enumerate(divisor):
            result[i] ^= _gf_mul(coef, factor)
    return result


def _interleave(version, data):
    blocks_n, ecc_len = _BLOCKS[version], _ECC_PER_BLOCK[version]
    raw = _raw_modules(version) // 8
    short_n, short_len = blocks_n - raw % blocks_n, raw // blocks_n
    divisor = _rs_divisor(ecc_len)
    blocks, k = [], 0
    for i in range(blocks_n):
        dat = data[k:k + short_len - ecc_len + (0 if i < short_n else 1)]
        k += len(dat)
        ecc = _rs_remainder(dat, divisor)
        blocks.append(dat + ([0] if i < short_n else []) + ecc)
    return [b[i] for i in range(len(blocks[0])) for j, b in enumerate(blocks) if i != short_len - ecc_len or j >= short_n]


class _Symbol:
    def __init__(self, version, codewords):
        self.version = version
        self.size = version * 4 + 17
        self.modules = [[False] * self.size for _ in range(self.size)]
        self.fixed = [[False] * self.size for _ in range(self.size)]
        self._function_patterns()
        self._codewords(codewords)
        best = min(range(8), key=self._penalty_with)
        self._mask(best)
        self._format(best)

    def _set(self, x, y, dark):
        self.modules[y][x] = dark
        self.fixed[y][x] = True

    def _function_patterns(self):
        n = self.size
        for i in range(n):
            self._set(6, i, i % 2 == 0)
            self._set(i, 6, i % 2 == 0)
        for x, y in ((3, 3), (n - 4, 3), (3, n - 4)):
            for dy in range(-4, 5):
                for dx in range(-4, 5):
                    if 0 <= x + dx < n and 0 <= y + dy < n:
                        self._set(x + dx, y + dy, max(abs(dx), abs(dy)) not in (2, 4))
        pos = self._alignment_positions()
        last = len(pos) - 1
        for i, a in enumerate(pos):
            for j, b in enumerate(pos):
                if (i, j) not in ((0, 0), (0, last), (last, 0)):
                    for dy in range(-2, 3):
                        for dx in range(-2, 3):
                            self._set(a + dx, b + dy, max(abs(dx), abs(dy)) != 1)
        self._format(0)
        if self.version >= 7:
            rem = self.version
            for _ in range(12):
                rem = (rem << 1) ^ ((rem >> 11) * 0x1F25)
            bits = self.version << 12 | rem
            for i in range(18):
                dark = (bits >> i) & 1 == 1
                a, b = n - 11 + i % 3, i // 3
                self._set(a, b, dark)
                self._set(b, a, dark)

    def _alignment_positions(self):
        if self.version == 1:
            return []
        align = self.version // 7 + 2
        step = (self.version * 8 + align * 3 + 5) // (align * 4 - 4) * 2
        return list(reversed([self.size - 7 - i * step for i in range(align - 1)] + [6]))

    def _format(self, mask):
        data = _LEVEL_M << 3 | mask
        rem = data
        for _ in range(10):
            rem = (rem << 1) ^ ((rem >> 9) * 0x537)
        bits = (data << 10 | rem) ^ 0x5412
        bit = [(bits >> i) & 1 == 1 for i in range(15)]
        n = self.size
        for i in range(6):
            self._set(8, i, bit[i])
        self._set(8, 7, bit[6])
        self._set(8, 8, bit[7])
        self._set(7, 8, bit[8])
        for i in range(9, 15):
            self._set(14 - i, 8, bit[i])
        for i in range(8):
            self._set(n - 1 - i, 8, bit[i])
        for i in range(8, 15):
            self._set(8, n - 15 + i, bit[i])
        self._set(8, n - 8, True)

    def _codewords(self, data):
        n, i, right = self.size, 0, self.size - 1
        while right >= 1:
            if right == 6:
                right = 5
            for vert in range(n):
                for j in range(2):
                    x = right - j
                    y = n - 1 - vert if (right + 1) & 2 == 0 else vert
                    if not self.fixed[y][x] and i < len(data) * 8:
                        self.modules[y][x] = (data[i >> 3] >> (7 - (i & 7))) & 1 == 1
                        i += 1
            right -= 2

    def _mask(self, mask):
        test = (lambda x, y: (x + y) % 2 == 0, lambda x, y: y % 2 == 0, lambda x, y: x % 3 == 0,
                lambda x, y: (x + y) % 3 == 0, lambda x, y: (x // 3 + y // 2) % 2 == 0,
                lambda x, y: x * y % 2 + x * y % 3 == 0, lambda x, y: (x * y % 2 + x * y % 3) % 2 == 0,
                lambda x, y: ((x + y) % 2 + x * y % 3) % 2 == 0)[mask]
        for y in range(self.size):
            for x in range(self.size):
                if not self.fixed[y][x] and test(x, y):
                    self.modules[y][x] = not self.modules[y][x]

    def _penalty_with(self, mask):
        """Long runs, 2×2 blocks and an uneven balance of dark and light make a code harder to read. Masks are their
        own inverse, so trying one is applying it twice."""
        self._mask(mask)
        self._format(mask)
        m, n, score = self.modules, self.size, 0
        for lines in (m, [list(col) for col in zip(*m)]):
            for line in lines:
                run = 1
                for a, b in zip(line, line[1:]):
                    if a == b:
                        run += 1
                    else:
                        score += run - 2 if run >= 5 else 0
                        run = 1
                score += run - 2 if run >= 5 else 0
        for y in range(n - 1):
            for x in range(n - 1):
                if m[y][x] == m[y][x + 1] == m[y + 1][x] == m[y + 1][x + 1]:
                    score += 3
        dark = sum(map(sum, m))
        score += (abs(dark * 20 - n * n * 10) + n * n - 1) // (n * n) * 10 - 10
        self._mask(mask)
        return score


def terminal(text):
    """The code as text for a terminal: two rows per line in half blocks, dark on a light background whatever the
    terminal's colors, with the quiet zone readers need."""
    m = encode(text)
    n = len(m) + 8
    grid = [[False] * n for _ in range(4)] + [[False] * 4 + row + [False] * 4 for row in m] + [[False] * n for _ in range(5)]
    chars = {(False, False): " ", (True, False): "▀", (False, True): "▄", (True, True): "█"}
    lines = ["".join(chars[(grid[y][x], grid[y + 1][x])] for x in range(n)) for y in range(0, n, 2)]
    return "\n".join(f"\033[30;107m{line}\033[0m" for line in lines)
