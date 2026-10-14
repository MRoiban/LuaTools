#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <string>
#include <vector>

namespace {
constexpr size_t MaxPathChars = 32760;

bool IsDrivePath(const std::wstring& path) {
    if (path.size() < 4 || path.size() > MaxPathChars ||
        !((path[0] >= L'A' && path[0] <= L'Z') || (path[0] >= L'a' && path[0] <= L'z')) ||
        path[1] != L':' || path[2] != L'\\') return false;
    size_t start = 3;
    for (size_t i = 3; i <= path.size(); ++i) {
        if (i != path.size() && path[i] != L'\\') {
            if (path[i] < 32 || wcschr(L"/:*?\"<>|", path[i])) return false;
            continue;
        }
        const size_t length = i - start;
        if (!length || path[i - 1] == L'.' || path[i - 1] == L' ') return false;
        start = i + 1;
    }
    return true;
}

// Check every path component, including the drive root. This fails closed on
// inaccessible components and mount points/junctions/symbolic links.
bool NoReparseComponents(const std::wstring& path) {
    if (!IsDrivePath(path) || GetDriveTypeW(path.substr(0, 3).c_str()) != DRIVE_FIXED) return false;
    for (size_t end = 2; end <= path.size(); ++end) {
        if (end != 2 && end != path.size() && path[end] != L'\\') continue;
        std::wstring component = end == 2 ? path.substr(0, 3) : path.substr(0, end);
        const DWORD attributes = GetFileAttributesW(component.c_str());
        if (attributes == INVALID_FILE_ATTRIBUTES || (attributes & FILE_ATTRIBUTE_REPARSE_POINT)) return false;
        if (end != path.size() && !(attributes & FILE_ATTRIBUTE_DIRECTORY)) return false;
    }
    return true;
}

class File {
public:
    HANDLE value = INVALID_HANDLE_VALUE;
    ~File() { if (value != INVALID_HANDLE_VALUE) CloseHandle(value); }
    bool Open(const std::wstring& path, DWORD access) {
        value = CreateFileW(path.c_str(), access, FILE_SHARE_READ, nullptr, OPEN_EXISTING,
                            FILE_ATTRIBUTE_NORMAL | FILE_FLAG_OPEN_REPARSE_POINT, nullptr);
        if (value == INVALID_HANDLE_VALUE) return false;
        BY_HANDLE_FILE_INFORMATION info{};
        return GetFileInformationByHandle(value, &info) &&
            !(info.dwFileAttributes & (FILE_ATTRIBUTE_REPARSE_POINT | FILE_ATTRIBUTE_DIRECTORY));
    }
};

bool ReadTarget(const std::wstring& config, std::wstring& target) {
    if (!NoReparseComponents(config)) return false;
    File file;
    if (!file.Open(config, GENERIC_READ)) return false;
    LARGE_INTEGER size{};
    if (!GetFileSizeEx(file.value, &size) || size.QuadPart < 8 ||
        size.QuadPart > (MaxPathChars + 1) * sizeof(wchar_t) || size.QuadPart % 2) return false;
    std::vector<wchar_t> content(static_cast<size_t>(size.QuadPart) / sizeof(wchar_t));
    DWORD read = 0;
    if (!ReadFile(file.value, content.data(), static_cast<DWORD>(size.QuadPart), &read, nullptr) ||
        read != size.QuadPart) return false;
    const size_t offset = content[0] == 0xfeff ? 1 : 0;
    target.assign(content.data() + offset, content.size() - offset);
    if (!IsDrivePath(target) || target.size() < 5 ||
        _wcsicmp(target.c_str() + target.size() - 4, L".exe") != 0) return false;
    return NoReparseComponents(target);
}

bool Launch(const std::wstring& config) {
    std::wstring target;
    if (!ReadTarget(config, target)) return false;
    // Keep the target open without write/delete sharing through CreateProcess.
    // Directory races are still possible for a same-user attacker; see README.
    File executable;
    if (!executable.Open(target, GENERIC_READ)) return false;
    std::wstring command = L"\"" + target + L"\" --minimized --tray-locked";
    std::wstring directory = target.substr(0, target.find_last_of(L'\\'));
    STARTUPINFOW startup{};
    startup.cb = sizeof(startup);
    PROCESS_INFORMATION process{};
    if (!CreateProcessW(target.c_str(), command.data(), nullptr, nullptr, FALSE,
                        CREATE_NO_WINDOW, nullptr, directory.c_str(), &startup, &process)) return false;
    CloseHandle(process.hThread);
    CloseHandle(process.hProcess);
    return true;
}

DWORD WINAPI Worker(void* module) {
    // Windows serializes thread initialization with DLL notifications. No work
    // below runs until the attaching DllMain has returned and released its lock.
    try {
        std::vector<wchar_t> host(MaxPathChars + 1);
        DWORD count = GetModuleFileNameW(nullptr, host.data(), static_cast<DWORD>(host.size()));
        if (!count || count >= host.size()) return 0;
        const wchar_t* basename = wcsrchr(host.data(), L'\\');
        if (!basename || _wcsicmp(basename + 1, L"steam.exe") != 0) return 0;
        std::vector<wchar_t> dll(MaxPathChars + 1);
        count = GetModuleFileNameW(static_cast<HMODULE>(module), dll.data(), static_cast<DWORD>(dll.size()));
        if (!count || count >= dll.size()) return 0;
        std::wstring path(dll.data(), count);
        const auto separator = path.find_last_of(L'\\');
        if (separator == std::wstring::npos) return 0;
        Launch(path.substr(0, separator + 1) + L"luatools-fork-loader.path");
    } catch (...) {
        // An optional launcher must not take down its host on allocation failure.
    }
    return 0;
}
}

#ifndef LOADER_TEST
BOOL WINAPI DllMain(HINSTANCE module, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) {
        // No waits, config I/O, process creation, or LoadLibrary in DllMain.
        // This proxy is process-lifetime infrastructure. Pin the already-loaded
        // module so a concurrent FreeLibrary cannot unmap the worker's code.
        HMODULE pinned = nullptr;
        if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_PIN,
                                reinterpret_cast<LPCWSTR>(module), &pinned)) return TRUE;
        HANDLE worker = CreateThread(nullptr, 0, Worker, module, 0, nullptr);
        if (worker) CloseHandle(worker);
    }
    return TRUE;
}
#endif
