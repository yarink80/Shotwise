#define NOMINMAX
#include <windows.h>
#include <thumbcache.h>
#include <propsys.h>
#include <propkey.h>
#include <propvarutil.h>
#include <shlobj.h>
#include <wincodec.h>
#include <wrl/client.h>
#include <atomic>
#include <new>
#include "DocumentReader.h"
using Microsoft::WRL::ComPtr;
namespace {
const CLSID Handler = {0x73603584,0x28a9,0x4d1b,{0xa0,0x8d,0x6c,0x5b,0x50,0x71,0x08,0x37}};
const GUID PropertySet = {0xd53cfc01,0x6a85,0x42a6,{0x93,0x79,0xbc,0xe1,0x58,0x67,0x82,0x15}};
const wchar_t* ClassId = L"{73603584-28A9-4D1B-A08D-6C5B50710837}";
const wchar_t* Details = L"prop:System.Title;System.Keywords;Shotwise.AppName;Shotwise.AppCompany;Shotwise.WindowTitle;Shotwise.Url;Shotwise.OcrText";
HINSTANCE module; std::atomic<long> objects{0}, locks{0};
HRESULT check(HRESULT result) { if (FAILED(result)) throw result; return result; }
HRESULT failure() noexcept { try { throw; } catch (HRESULT hr) { return hr; } catch (const std::bad_alloc&) { return E_OUTOFMEMORY; } catch (...) { return HRESULT_FROM_WIN32(ERROR_INVALID_DATA); } }
class Provider final : public IInitializeWithStream, public IThumbnailProvider, public IPropertyStore, public IPropertyStoreCapabilities {
    std::atomic<ULONG> refs{1}; ComPtr<IStream> stream; ComPtr<IPropertyStore> properties; bool loaded = false; HRESULT loadResult = E_UNEXPECTED;
    void load() {
        if (loaded) { check(loadResult); return; } if (!stream) throw E_UNEXPECTED;
        loaded = true;
        try {
        ComPtr<IPropertyStore> cache; check(PSCreateMemoryPropertyStore(IID_PPV_ARGS(&cache)));
        auto root = shotwise::parseMetadata(shotwise::zipEntry(stream.Get(), "metadata.json"));
        auto text = [&](const wchar_t* name) { auto i = root.object.find(name); return i == root.object.end() ? std::wstring() : i->second.text; };
        auto set = [&](const PROPERTYKEY& key, const std::wstring& value) {
            if (value.empty()) return; PROPVARIANT p; PropVariantInit(&p); check(InitPropVariantFromString(value.c_str(), &p));
            auto result = cache->SetValue(key, p); PropVariantClear(&p); check(result);
        };
        set(PKEY_Title, text(L"windowTitle"));
        auto tags = root.object.find(L"tags");
        if (tags != root.object.end() && !tags->second.array.empty()) {
            std::vector<PCWSTR> values; for (auto& tag : tags->second.array) values.push_back(tag.text.c_str());
            PROPVARIANT p; PropVariantInit(&p); check(InitPropVariantFromStringVector(values.data(), ULONG(values.size()), &p)); auto result = cache->SetValue(PKEY_Keywords, p); PropVariantClear(&p); check(result);
        }
        const wchar_t* names[] = {L"appName", L"appCompany", L"windowTitle", L"url", L"ocrText"};
        for (DWORD i = 0; i < 5; ++i) set(PROPERTYKEY{PropertySet, i + 2}, text(names[i]));
        check(cache->Commit()); properties = cache; loadResult = S_OK;
        } catch (...) { loadResult = failure(); throw loadResult; }
    }
public:
    Provider() { ++objects; }
    ~Provider() { --objects; }
    IFACEMETHODIMP QueryInterface(REFIID iid, void** output) override {
        if (!output) return E_POINTER; *output = nullptr;
        if (iid == IID_IUnknown || iid == __uuidof(IInitializeWithStream)) *output = static_cast<IInitializeWithStream*>(this);
        else if (iid == __uuidof(IThumbnailProvider)) *output = static_cast<IThumbnailProvider*>(this);
        else if (iid == __uuidof(IPropertyStore)) *output = static_cast<IPropertyStore*>(this);
        else if (iid == __uuidof(IPropertyStoreCapabilities)) *output = static_cast<IPropertyStoreCapabilities*>(this);
        else return E_NOINTERFACE;
        AddRef(); return S_OK;
    }
    IFACEMETHODIMP_(ULONG) AddRef() override { return ++refs; }
    IFACEMETHODIMP_(ULONG) Release() override { ULONG count = --refs; if (!count) delete this; return count; }
    IFACEMETHODIMP Initialize(IStream* input, DWORD mode) override {
        if (!input) return E_POINTER; if (stream) return HRESULT_FROM_WIN32(ERROR_ALREADY_INITIALIZED);
        if ((mode & 3) != STGM_READ) return STG_E_ACCESSDENIED; stream = input; return S_OK;
    }
    IFACEMETHODIMP GetThumbnail(UINT cx, HBITMAP* bitmap, WTS_ALPHATYPE* alpha) override {
        if (!bitmap || !alpha) return E_POINTER; *bitmap = nullptr; *alpha = WTSAT_UNKNOWN;
        if (!stream) return E_UNEXPECTED; if (cx == 0) return E_INVALIDARG;
        HBITMAP dib = nullptr;
        try {
            auto png = shotwise::zipEntry(stream.Get(), "thumbnail.png");
            const unsigned char signature[] = {137,80,78,71,13,10,26,10};
            shotwise::require(png.size() >= 8 && std::equal(std::begin(signature), std::end(signature), png.begin()));
            ComPtr<IWICImagingFactory> factory; check(CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&factory)));
            ComPtr<IWICStream> memory; check(factory->CreateStream(&memory)); check(memory->InitializeFromMemory(png.data(), DWORD(png.size())));
            ComPtr<IWICBitmapDecoder> decoder; check(CoCreateInstance(CLSID_WICPngDecoder, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&decoder)));
            check(decoder->Initialize(memory.Get(), WICDecodeMetadataCacheOnLoad));
            ComPtr<IWICBitmapFrameDecode> frame; check(decoder->GetFrame(0, &frame)); UINT width, height; check(frame->GetSize(&width, &height));
            shotwise::require(width && height && width <= 1024 && height <= 1024);
            double scale = std::min(1.0, double(std::min(cx, 1024u)) / std::max(width, height));
            UINT w = std::max(1u, UINT(width * scale)), h = std::max(1u, UINT(height * scale));
            ComPtr<IWICBitmapScaler> scaler; check(factory->CreateBitmapScaler(&scaler)); check(scaler->Initialize(frame.Get(), w, h, WICBitmapInterpolationModeFant));
            ComPtr<IWICFormatConverter> converter; check(factory->CreateFormatConverter(&converter));
            check(converter->Initialize(scaler.Get(), GUID_WICPixelFormat32bppPBGRA, WICBitmapDitherTypeNone, nullptr, 0, WICBitmapPaletteTypeCustom));
            BITMAPINFO info{}; info.bmiHeader.biSize = sizeof(BITMAPINFOHEADER); info.bmiHeader.biWidth = LONG(w); info.bmiHeader.biHeight = -LONG(h); info.bmiHeader.biPlanes = 1; info.bmiHeader.biBitCount = 32; info.bmiHeader.biCompression = BI_RGB;
            void* pixels = nullptr; dib = CreateDIBSection(nullptr, &info, DIB_RGB_COLORS, &pixels, nullptr, 0); if (!dib) throw HRESULT_FROM_WIN32(GetLastError());
            check(converter->CopyPixels(nullptr, w * 4, w * h * 4, static_cast<BYTE*>(pixels)));
            *bitmap = dib; *alpha = WTSAT_ARGB; return S_OK;
        } catch (...) { if (dib) DeleteObject(dib); return failure(); }
    }
    IFACEMETHODIMP GetCount(DWORD* count) override { if (!count) return E_POINTER; *count = 0; try { load(); return properties->GetCount(count); } catch (...) { return failure(); } }
    IFACEMETHODIMP GetAt(DWORD index, PROPERTYKEY* key) override { if (!key) return E_POINTER; try { load(); return properties->GetAt(index, key); } catch (...) { return failure(); } }
    IFACEMETHODIMP GetValue(REFPROPERTYKEY key, PROPVARIANT* value) override { if (!value) return E_POINTER; PropVariantInit(value); try { load(); return properties->GetValue(key, value); } catch (...) { return failure(); } }
    IFACEMETHODIMP SetValue(REFPROPERTYKEY, REFPROPVARIANT) override { return STG_E_ACCESSDENIED; }
    IFACEMETHODIMP Commit() override { return S_OK; }
    IFACEMETHODIMP IsPropertyWritable(REFPROPERTYKEY) override { return S_FALSE; }
};
class Factory final : public IClassFactory {
    std::atomic<ULONG> refs{1};
public:
    Factory() { ++objects; } ~Factory() { --objects; }
    IFACEMETHODIMP QueryInterface(REFIID iid, void** output) override { if (!output) return E_POINTER; *output = nullptr; if (iid != IID_IUnknown && iid != IID_IClassFactory) return E_NOINTERFACE; *output = static_cast<IClassFactory*>(this); AddRef(); return S_OK; }
    IFACEMETHODIMP_(ULONG) AddRef() override { return ++refs; }
    IFACEMETHODIMP_(ULONG) Release() override { auto count = --refs; if (!count) delete this; return count; }
    IFACEMETHODIMP CreateInstance(IUnknown* outer, REFIID iid, void** output) override {
        if (!output) return E_POINTER; *output = nullptr; if (outer) return CLASS_E_NOAGGREGATION;
        auto provider = new(std::nothrow) Provider; if (!provider) return E_OUTOFMEMORY; auto result = provider->QueryInterface(iid, output); provider->Release(); return result;
    }
    IFACEMETHODIMP LockServer(BOOL lock) override { if (lock) ++locks; else --locks; return S_OK; }
};
std::wstring modulePath() { std::wstring path(32768, 0); DWORD n = GetModuleFileNameW(module, path.data(), DWORD(path.size())); if (!n || n >= path.size()) throw E_FAIL; path.resize(n); return path; }
HRESULT reg(const std::wstring& path, const wchar_t* name, const std::wstring& value) {
    HKEY key; auto error = RegCreateKeyExW(HKEY_CURRENT_USER, path.c_str(), 0, nullptr, 0, KEY_WRITE, nullptr, &key, nullptr); if (error) return HRESULT_FROM_WIN32(error);
    error = RegSetValueExW(key, name, 0, REG_SZ, reinterpret_cast<const BYTE*>(value.c_str()), DWORD((value.size() + 1) * sizeof(wchar_t))); RegCloseKey(key); return HRESULT_FROM_WIN32(error);
}
bool removeOwned(const std::wstring& path, const std::wstring& expected) {
    HKEY key; if (RegOpenKeyExW(HKEY_CURRENT_USER, path.c_str(), 0, KEY_READ, &key)) return false;
    wchar_t value[32768]; DWORD size = sizeof(value), type = 0; auto error = RegQueryValueExW(key, nullptr, nullptr, &type, reinterpret_cast<BYTE*>(value), &size); RegCloseKey(key);
    if (!error && type == REG_SZ && size >= sizeof(wchar_t) && value[(size / sizeof(wchar_t)) - 1] == 0 && expected == value) return RegDeleteTreeW(HKEY_CURRENT_USER, path.c_str()) == ERROR_SUCCESS;
    return false;
}
void removeDetails(const std::wstring& path) {
    HKEY key; if (RegOpenKeyExW(HKEY_CURRENT_USER, path.c_str(), 0, KEY_QUERY_VALUE | KEY_SET_VALUE, &key)) return;
    wchar_t value[1024]; DWORD size = sizeof(value), type = 0;
    auto error = RegQueryValueExW(key, L"FullDetails", nullptr, &type, reinterpret_cast<BYTE*>(value), &size);
    if (!error && type == REG_SZ && size >= sizeof(wchar_t) && value[size / sizeof(wchar_t) - 1] == 0 && std::wstring(Details) == value) RegDeleteValueW(key, L"FullDetails");
    RegCloseKey(key);
}
}
extern "C" BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID) { if (reason == DLL_PROCESS_ATTACH) { module = instance; DisableThreadLibraryCalls(instance); } return TRUE; }
extern "C" HRESULT __stdcall DllCanUnloadNow() { return objects == 0 && locks == 0 ? S_OK : S_FALSE; }
extern "C" HRESULT __stdcall DllGetClassObject(REFCLSID clsid, REFIID iid, void** output) {
    if (!output) return E_POINTER; *output = nullptr; if (clsid != Handler) return CLASS_E_CLASSNOTAVAILABLE;
    auto factory = new(std::nothrow) Factory; if (!factory) return E_OUTOFMEMORY; auto result = factory->QueryInterface(iid, output); factory->Release(); return result;
}
extern "C" HRESULT __stdcall DllRegisterServer() {
    try {
        auto path = modulePath(); std::wstring classes = L"Software\\Classes\\", cls = classes + L"CLSID\\" + ClassId;
        check(reg(cls, nullptr, L"Shotwise Document Shell Provider")); check(reg(cls + L"\\InprocServer32", nullptr, path)); check(reg(cls + L"\\InprocServer32", L"ThreadingModel", L"Apartment"));
        check(reg(classes + L".shotx\\shellex\\{E357FCCD-A995-4576-B01F-234630154E96}", nullptr, ClassId));
        check(reg(classes + L".shotx\\shellex\\{9B174B34-40FF-11D2-A27E-00C04FC30871}", nullptr, ClassId));
        check(reg(L"Software\\Microsoft\\Windows\\CurrentVersion\\PropertySystem\\PropertyHandlers\\.shotx", nullptr, ClassId));
        check(reg(classes + L"Shotwise.Document", L"FullDetails", Details));
        // Property schemas are installed separately by an elevated installer. Never write HKLM here.
        SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, nullptr, nullptr); return S_OK;
    } catch (...) { return failure(); }
}
extern "C" HRESULT __stdcall DllUnregisterServer() {
    try {
        std::wstring classes = L"Software\\Classes\\", cls = classes + L"CLSID\\" + ClassId;
        // An older copy must not unregister a newer DLL installed at another path.
        if (!removeOwned(cls + L"\\InprocServer32", modulePath())) return S_FALSE;
        removeOwned(cls, L"Shotwise Document Shell Provider");
        removeOwned(classes + L".shotx\\shellex\\{E357FCCD-A995-4576-B01F-234630154E96}", ClassId);
        removeOwned(classes + L".shotx\\shellex\\{9B174B34-40FF-11D2-A27E-00C04FC30871}", ClassId);
        removeOwned(L"Software\\Microsoft\\Windows\\CurrentVersion\\PropertySystem\\PropertyHandlers\\.shotx", ClassId);
        removeDetails(classes + L"Shotwise.Document");
        SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, nullptr, nullptr); return S_OK;
    } catch (...) { return failure(); }
}
extern "C" HRESULT __stdcall DllInstall(BOOL install, LPCWSTR command) { if (command && _wcsicmp(command, L"user") != 0) return E_INVALIDARG; return install ? DllRegisterServer() : DllUnregisterServer(); }
