#pragma once
#include <windows.h>
#include <objidl.h>
#include <vector>
#include <string>
#include <array>
#include <map>
#include <stdexcept>
#include <algorithm>
#include <cstdint>
#include <limits>
#include <set>
#include <string_view>
#include <optional>
#include <mutex>
#include <bcrypt.h>
#pragma comment(lib, "bcrypt.lib")
namespace shotwise {
using bytes = std::vector<unsigned char>;
inline uint16_t u16(const unsigned char* p) { return uint16_t(p[0] | (p[1] << 8)); }
inline uint32_t u32(const unsigned char* p) { return uint32_t(u16(p)) | (uint32_t(u16(p + 2)) << 16); }
inline void require(bool value) { if (!value) throw std::runtime_error("Invalid document"); }
inline bytes read(IStream* stream, uint64_t position, size_t length) {
    LARGE_INTEGER at{}; at.QuadPart = position;
    require(SUCCEEDED(stream->Seek(at, STREAM_SEEK_SET, nullptr)));
    bytes result(length); ULONG got = 0;
    require(length <= ULONG_MAX && SUCCEEDED(stream->Read(result.data(), ULONG(length), &got)) && got == length);
    return result;
}
class bits {
    const bytes& data; size_t bit = 0;
public:
    explicit bits(const bytes& input) : data(input) {}
    unsigned take(unsigned n) { require(n <= 16 && bit + n <= data.size() * 8); unsigned value = 0; for (unsigned i = 0; i < n; ++i, ++bit) value |= ((data[bit / 8] >> (bit % 8)) & 1) << i; return value; }
    void align() { bit = (bit + 7) & ~size_t(7); }
};
class huffman {
    std::array<unsigned, 16> first{}, counts{}, starts{};
    std::vector<unsigned> symbols;
public:
    explicit huffman(const std::vector<unsigned>& lengths) {
        std::array<unsigned, 16> count{}, next{};
        for (auto length : lengths) { require(length <= 15); if (length) ++count[length]; }
        unsigned code = 0; int left = 1;
        for (unsigned n = 1; n <= 15; ++n) { left = (left << 1) - int(count[n]); require(left >= 0); code = (code + count[n - 1]) << 1; next[n] = code; }
        unsigned offset = 0;
        for (unsigned n = 1; n <= 15; ++n) { first[n] = next[n]; counts[n] = count[n]; starts[n] = offset; offset += count[n]; }
        symbols.resize(offset); auto positions = starts;
        for (unsigned symbol = 0; symbol < lengths.size(); ++symbol) {
            auto length = lengths[symbol]; if (!length) continue;
            symbols[positions[length]++] = symbol;
        }
    }
    unsigned decode(bits& input) const {
        unsigned code = 0;
        for (unsigned length = 1; length <= 15; ++length) { code = (code << 1) | input.take(1); if (code >= first[length] && code - first[length] < counts[length]) return symbols[starts[length] + code - first[length]]; }
        throw std::runtime_error("Invalid Huffman code");
    }
};
inline bytes inflateCore(const bytes& compressed, size_t expected) {
    require(expected <= 16 * 1024 * 1024); bits input(compressed); bytes output; output.reserve(expected);
    auto append = [&](unsigned value) { require(output.size() < expected); output.push_back(static_cast<unsigned char>(value)); };
    bool final = false;
    size_t blocks = 0, work = 0;
    static const huffman fixedLiteral([] { std::vector<unsigned> lengths(288); for (unsigned i = 0; i < 288; ++i) lengths[i] = i < 144 ? 8 : i < 256 ? 9 : i < 280 ? 7 : 8; return lengths; }());
    static const huffman fixedDistance(std::vector<unsigned>(32, 5));
    const unsigned lengthBase[] = {3,4,5,6,7,8,9,10,11,13,15,17,19,23,27,31,35,43,51,59,67,83,99,115,131,163,195,227,258};
    const unsigned lengthBits[] = {0,0,0,0,0,0,0,0,1,1,1,1,2,2,2,2,3,3,3,3,4,4,4,4,5,5,5,5,0};
    const unsigned distBase[] = {1,2,3,4,5,7,9,13,17,25,33,49,65,97,129,193,257,385,513,769,1025,1537,2049,3073,4097,6145,8193,12289,16385,24577};
    const unsigned distBits[] = {0,0,0,0,1,1,2,2,3,3,4,4,5,5,6,6,7,7,8,8,9,9,10,10,11,11,12,12,13,13};
    while (!final) {
        require(++blocks <= std::min<size_t>(65536, expected + 64));
        final = input.take(1) != 0; unsigned type = input.take(2); require(type != 3);
        if (type == 0) {
            input.align(); auto size = input.take(16), complement = input.take(16); require((size ^ complement) == 65535);
            for (unsigned i = 0; i < size; ++i) append(input.take(8)); continue;
        }
        std::vector<unsigned> literal, distance;
        if (type == 2) {
            work += 320; require(work <= expected + 65536);
            unsigned nl = input.take(5) + 257, nd = input.take(5) + 1, nc = input.take(4) + 4; require(nl <= 286);
            // RFC 1951 permutation, independent of literal/distance order.
            const unsigned permutation[] = {16,17,18,0,8,7,9,6,10,5,11,4,12,3,13,2,14,1,15};
            std::vector<unsigned> cl(19); for (unsigned i = 0; i < nc; ++i) cl[permutation[i]] = input.take(3);
            huffman codeLength(cl); std::vector<unsigned> all;
            while (all.size() < nl + nd) {
                unsigned symbol = codeLength.decode(input);
                if (symbol <= 15) all.push_back(symbol);
                else { require(symbol <= 18); unsigned value = 0, count = 0;
                    if (symbol == 16) { require(!all.empty()); value = all.back(); count = input.take(2) + 3; }
                    if (symbol == 17) count = input.take(3) + 3;
                    if (symbol == 18) count = input.take(7) + 11;
                    require(count <= nl + nd - all.size()); all.insert(all.end(), count, value);
                }
            }
            literal.assign(all.begin(), all.begin() + nl); distance.assign(all.begin() + nl, all.end()); require(literal[256] != 0);
        }
        // Fixed tables are shared across all blocks; dynamic tables belong to this block only.
        std::optional<huffman> dynamicLiteral, dynamicDistance;
        if (type == 2) { dynamicLiteral.emplace(literal); dynamicDistance.emplace(distance); }
        const huffman& lh = type == 1 ? fixedLiteral : *dynamicLiteral;
        const huffman& dh = type == 1 ? fixedDistance : *dynamicDistance;
        for (;;) {
            require(++work <= expected + 65536);
            unsigned symbol = lh.decode(input); if (symbol == 256) break;
            if (symbol < 256) { append(symbol); continue; }
            require(symbol >= 257 && symbol <= 285); unsigned index = symbol - 257;
            unsigned count = lengthBase[index] + input.take(lengthBits[index]); unsigned d = dh.decode(input); require(d < 30);
            unsigned offset = distBase[d] + input.take(distBits[d]); require(offset <= output.size());
            while (count--) append(output[output.size() - offset]);
        }
    }
    require(output.size() == expected); return output;
}
inline bytes inflate(const bytes& compressed, size_t expected) {
    // Cache only failures, bounded across COM instances, keyed by cryptographic stream hash and output size.
    std::array<unsigned char, 32> digest{};
    require(compressed.size() <= 16 * 1024 * 1024);
    require(BCryptHash(BCRYPT_SHA256_ALG_HANDLE, nullptr, 0, const_cast<PUCHAR>(compressed.data()), ULONG(compressed.size()), digest.data(), ULONG(digest.size())) >= 0);
    using key = std::pair<std::array<unsigned char, 32>, size_t>;
    static std::mutex gate; static std::set<key> failures;
    key id{digest, expected};
    { std::lock_guard<std::mutex> lock(gate); require(!failures.contains(id)); }
    try { return inflateCore(compressed, expected); }
    catch (...) { std::lock_guard<std::mutex> lock(gate); if (failures.size() >= 64) failures.erase(failures.begin()); failures.insert(id); throw; }
}
inline uint32_t crc32(const bytes& data) {
    uint32_t crc = 0xffffffff;
    for (auto byte : data) { crc ^= byte; for (int i = 0; i < 8; ++i) crc = (crc >> 1) ^ (0xedb88320u & (0u - (crc & 1))); }
    return ~crc;
}
inline bytes zipEntry(IStream* stream, const std::string& wanted) {
    STATSTG stat{}; require(SUCCEEDED(stream->Stat(&stat, STATFLAG_NONAME)));
    uint64_t size = stat.cbSize.QuadPart; require(size >= 22 && size <= uint64_t(2) * 1024 * 1024 * 1024);
    auto tail = read(stream, size - std::min<uint64_t>(size, 65557), size_t(std::min<uint64_t>(size, 65557)));
    size_t end = tail.size() - 22;
    while (u32(tail.data() + end) != 0x06054b50 || end + 22 + u16(tail.data() + end + 20) != tail.size()) { require(end != 0); --end; }
    auto e = tail.data() + end;
    require(u16(e + 4) == 0 && u16(e + 6) == 0 && u16(e + 8) == u16(e + 10));
    unsigned entries = u16(e + 10); uint64_t directorySize = u32(e + 12), directoryAt = u32(e + 16);
    require(entries <= 10000 && directorySize <= 8 * 1024 * 1024 && directoryAt + directorySize <= size - tail.size() + end);
    auto directory = read(stream, directoryAt, size_t(directorySize)); size_t at = 0; bytes result; bool found = false; uint64_t total = 0; std::set<std::string> names;
    for (unsigned i = 0; i < entries; ++i) {
        require(at + 46 <= directory.size()); auto h = directory.data() + at; require(u32(h) == 0x02014b50);
        unsigned nameLength = u16(h + 28), extraLength = u16(h + 30), commentLength = u16(h + 32);
        require(at + 46ull + nameLength + extraLength + commentLength <= directory.size());
        std::string name(reinterpret_cast<const char*>(h + 46), nameLength);
        require(names.insert(name).second);
        require(name.find("..") == std::string::npos && name.find('\\') == std::string::npos && name.find(':') == std::string::npos && !name.starts_with('/'));
        uint32_t packed = u32(h + 20), unpacked = u32(h + 24), localAt = u32(h + 42); unsigned method = u16(h + 10);
        total += unpacked; require(total <= uint64_t(2) * 1024 * 1024 * 1024);
        if (name == wanted) {
            require(!found && !(u16(h + 8) & 1) && u16(h + 34) == 0); found = true;
            require(unpacked <= (wanted == "metadata.json" ? 4u : 16u) * 1024 * 1024 && packed <= 16u * 1024 * 1024);
            if (wanted == "thumbnail.png") require(i == 0 && localAt == 0 && method == 0);
            auto local = read(stream, localAt, 30); require(u32(local.data()) == 0x04034b50 && u16(local.data() + 8) == method && u16(local.data() + 6) == u16(h + 8));
            unsigned ln = u16(local.data() + 26), lx = u16(local.data() + 28); uint64_t dataAt = uint64_t(localAt) + 30 + ln + lx;
            require(ln == name.size() && dataAt + packed <= directoryAt);
            auto localName = read(stream, uint64_t(localAt) + 30, ln); require(std::equal(localName.begin(), localName.end(), name.begin()));
            auto compressed = read(stream, dataAt, packed);
            if (method == 0) { require(packed == unpacked); result = std::move(compressed); }
            else { require(method == 8); result = inflate(compressed, unpacked); }
            require(crc32(result) == u32(h + 16));
        }
        at += 46ull + nameLength + extraLength + commentLength;
    }
    require(found); return result;
}
struct json {
    std::wstring text; std::map<std::wstring, json> object; std::vector<json> array;
};
class jsonParser {
    const std::wstring source; size_t at = 0; unsigned nodes = 0;
    static bool number(std::wstring_view text) {
        size_t i = 0;
        auto digit = [&](size_t index) { return index < text.size() && text[index] >= L'0' && text[index] <= L'9'; };
        if (i < text.size() && text[i] == L'-') ++i;
        if (!digit(i)) return false;
        if (text[i] == L'0') ++i; else while (digit(i)) ++i;
        if (i < text.size() && text[i] == L'.') { ++i; if (!digit(i)) return false; while (digit(i)) ++i; }
        if (i < text.size() && (text[i] == L'e' || text[i] == L'E')) {
            ++i; if (i < text.size() && (text[i] == L'+' || text[i] == L'-')) ++i;
            if (!digit(i)) return false; while (digit(i)) ++i;
        }
        return i == text.size();
    }
    void white() { while (at < source.size() && (source[at] == L' ' || source[at] == L'\t' || source[at] == L'\n' || source[at] == L'\r')) ++at; }
    wchar_t take() { require(at < source.size()); return source[at++]; }
    std::wstring string() {
        require(take() == L'"'); std::wstring value;
        for (;;) {
            auto c = take();
            if (c == L'"') {
                for (size_t i = 0; i < value.size(); ++i) {
                    auto unit = value[i];
                    if (unit >= 0xd800 && unit <= 0xdbff) { require(i + 1 < value.size() && value[i + 1] >= 0xdc00 && value[i + 1] <= 0xdfff); ++i; }
                    else require(unit < 0xdc00 || unit > 0xdfff);
                }
                return value;
            }
            require(c >= 32);
            if (c == L'\\') { c = take(); switch (c) {
                case L'"': case L'\\': case L'/': break; case L'b': c = L'\b'; break; case L'f': c = L'\f'; break; case L'n': c = L'\n'; break; case L'r': c = L'\r'; break; case L't': c = L'\t'; break;
                case L'u': { unsigned code = 0; for (int i = 0; i < 4; ++i) { auto h = take(); unsigned d = h >= L'0' && h <= L'9' ? h - L'0' : h >= L'a' && h <= L'f' ? h - L'a' + 10 : h >= L'A' && h <= L'F' ? h - L'A' + 10 : 16; require(d < 16); code = code * 16 + d; } c = wchar_t(code); break; }
                default: throw std::runtime_error("Invalid JSON escape");
            }}
            require(c != 0); value += c;
        }
    }
    json value(unsigned depth) {
        require(depth <= 32 && ++nodes <= 65536); white(); require(at < source.size()); json result; auto c = source[at];
        if (c == L'"') { result.text = string(); return result; }
        if (c == L'{' || c == L'[') {
            ++at; white(); wchar_t close = c == L'{' ? L'}' : L']';
            if (at < source.size() && source[at] == close) { ++at; return result; }
            for (;;) {
                white(); if (c == L'{') { auto key = string(); white(); require(take() == L':'); require(!result.object.contains(key)); result.object.emplace(std::move(key), value(depth + 1)); }
                else result.array.push_back(value(depth + 1));
                white(); auto delimiter = take(); if (delimiter == close) return result; require(delimiter == L',');
            }
        }
        size_t start = at;
        while (at < source.size() && source[at] != L',' && source[at] != L'}' && source[at] != L']' && source[at] != L' ' && source[at] != L'\r' && source[at] != L'\n' && source[at] != L'\t') ++at;
        require(at > start); result.text = source.substr(start, at - start);
        require(result.text == L"true" || result.text == L"false" || result.text == L"null" || number(result.text));
        if (result.text == L"null") result.text.clear();
        return result;
    }
public:
    explicit jsonParser(const bytes& data) : source([&] {
        int count = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, reinterpret_cast<const char*>(data.data()), int(data.size()), nullptr, 0); require(count > 0);
        std::wstring text(count, 0); require(MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, reinterpret_cast<const char*>(data.data()), int(data.size()), text.data(), count) == count); return text;
    }()) {}
    json parse() { auto result = value(0); white(); require(at == source.size()); return result; }
};
inline json parseMetadata(const bytes& data) {
    std::array<unsigned char, 32> digest{};
    require(data.size() <= 4 * 1024 * 1024);
    require(BCryptHash(BCRYPT_SHA256_ALG_HANDLE, nullptr, 0, const_cast<PUCHAR>(data.data()), ULONG(data.size()), digest.data(), ULONG(digest.size())) >= 0);
    static std::mutex gate; static std::set<std::array<unsigned char, 32>> failures;
    { std::lock_guard<std::mutex> lock(gate); require(!failures.contains(digest)); }
    try { return jsonParser(data).parse(); }
    catch (...) { std::lock_guard<std::mutex> lock(gate); if (failures.size() >= 64) failures.erase(failures.begin()); failures.insert(digest); throw; }
}
}
