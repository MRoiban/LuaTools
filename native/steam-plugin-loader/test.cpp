#define LOADER_TEST
#include "loader.cpp"
#include <cstdio>

int wmain(int argc, wchar_t** argv) {
    if (argc == 3 && wcscmp(argv[1], L"--proxy") == 0) {
        HMODULE proxy = LoadLibraryExW(argv[2], nullptr,
            LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
        if (!proxy) return 90;
        for (const char* name : {"PlaySound", "WOWAppExit", "mciExecute", "mmsystemGetVersion", "timeGetTime"}) {
            if (!GetProcAddress(proxy, name)) return 91;
        }
        using TimeGetTime = DWORD(WINAPI*)();
        const auto ticks = reinterpret_cast<TimeGetTime>(GetProcAddress(proxy, "timeGetTime"));
        std::printf("Isolated proxy timeGetTime: %lu\n", ticks());
        FreeLibrary(proxy);
        return 0;
    }
    if (argc == 3 && wcscmp(argv[1], L"--validate") == 0) {
        std::wstring target;
        return ReadTarget(argv[2], target) ? 0 : 1;
    }
    if (argc == 3 && wcscmp(argv[1], L"--minimized") == 0 && wcscmp(argv[2], L"--tray-locked") == 0) {
        wchar_t marker[MAX_PATH]{};
        if (!GetEnvironmentVariableW(L"LUATOOLS_LOADER_TEST_MARKER", marker, MAX_PATH)) return 98;
        HANDLE file = CreateFileW(marker, GENERIC_WRITE, 0, nullptr, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (file == INVALID_HANDLE_VALUE) return 99;
        CloseHandle(file);
        return 0;
    }
    int failures = 0;
    auto check = [&failures](bool success, const char* label) {
        if (!success) { std::printf("FAIL: %s\n", label); ++failures; }
    };
    check(IsDrivePath(L"C:\\program files\\fork\\LuaTools.exe"), "drive absolute with spaces");
    for (const auto* bad : {L"relative.exe", L"C:relative.exe", L"\\\\server\\share\\app.exe",
            L"\\\\?\\C:\\app.exe", L"C:\\a\\..\\app.exe", L"C:\\a\\.\\app.exe",
            L"C:\\a.\\app.exe", L"C:\\app.exe:stream", L"C:\\app.exe\n", L"C:\\a/evil.exe"})
        check(!IsDrivePath(bad), "reject ambiguous or remote path");
    std::wstring embedded = L"C:\\valid.exe";
    embedded.push_back(0);
    embedded += L"other";
    check(!IsDrivePath(embedded), "reject embedded NUL");
    wchar_t temp[MAX_PATH]{};
    check(GetTempPathW(MAX_PATH, temp) != 0, "temp path");
    const std::wstring config = std::wstring(temp) + L"luatools-loader-test-" + std::to_wstring(GetCurrentProcessId()) + L".path";
    std::vector<wchar_t> self(MaxPathChars + 1);
    GetModuleFileNameW(nullptr, self.data(), static_cast<DWORD>(self.size()));
    auto write = [&config](const std::wstring& content) {
        HANDLE file = CreateFileW(config.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (file == INVALID_HANDLE_VALUE) return false;
        DWORD written = 0;
        const DWORD bytes = static_cast<DWORD>(content.size() * sizeof(wchar_t));
        const bool ok = WriteFile(file, content.data(), bytes, &written, nullptr) && written == bytes;
        CloseHandle(file);
        return ok;
    };
    std::wstring target;
    check(write(std::wstring(self.data())), "write test config");
    check(ReadTarget(config, target) && target == self.data(), "read BOM-less UTF16 config");
    check(write(std::wstring(1, 0xfeff) + self.data()), "write BOM config");
    check(ReadTarget(config, target) && target == self.data(), "read UTF16 BOM config");
    const std::wstring marker = config + L".launched";
    SetEnvironmentVariableW(L"LUATOOLS_LOADER_TEST_MARKER", marker.c_str());
    check(Launch(config), "direct launch of isolated test executable");
    bool launched = false;
    for (int attempt = 0; attempt < 100; ++attempt) {
        if (GetFileAttributesW(marker.c_str()) != INVALID_FILE_ATTRIBUTES) { launched = true; break; }
        Sleep(50);
    }
    check(launched, "child received exact minimized and tray flags");
    DeleteFileW(marker.c_str());
    SetEnvironmentVariableW(L"LUATOOLS_LOADER_TEST_MARKER", nullptr);
    check(write(std::wstring(self.data()) + L"\n"), "write invalid config");
    check(!ReadTarget(config, target), "reject newline in config");
    check(write(std::wstring(temp) + L"missing-luatools.exe"), "write missing target");
    check(!ReadTarget(config, target), "reject missing target");
    check(!Launch(config), "missing executable never starts");
    DeleteFileW(config.c_str());
    // Non-Steam host must return without reading config or starting anything.
    check(Worker(nullptr) == 0, "non-Steam host exits");
    std::printf("Loader validation: %d failures\n", failures);
    return failures ? 1 : 0;
}
