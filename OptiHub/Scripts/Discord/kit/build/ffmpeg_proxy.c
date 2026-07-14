#include <windows.h>

static HMODULE loadSiblingModule(LPCWSTR name) {
    WCHAR path[MAX_PATH];
    DWORD len = GetModuleFileNameW(NULL, path, MAX_PATH);
    if (len == 0 || len >= MAX_PATH) {
        return NULL;
    }

    for (DWORD i = len; i > 0; --i) {
        if (path[i - 1] == L'\\' || path[i - 1] == L'/') {
            path[i] = L'\0';
            break;
        }
    }

    if (lstrlenW(path) + lstrlenW(name) + 1 >= MAX_PATH) {
        return NULL;
    }

    lstrcatW(path, name);
    return LoadLibraryW(path);
}

static void applyEarlyPriority(void) {
    WCHAR iniPath[MAX_PATH];
    DWORD len = GetModuleFileNameW(NULL, iniPath, MAX_PATH);
    if (len == 0 || len >= MAX_PATH) {
        return;
    }

    for (DWORD i = len; i > 0; --i) {
        if (iniPath[i - 1] == L'\\' || iniPath[i - 1] == L'/') {
            iniPath[i] = L'\0';
            break;
        }
    }

    if (lstrlenW(iniPath) + 11 >= MAX_PATH) {
        return;
    }

    lstrcatW(iniPath, L"config.ini");

    WCHAR value[16];
    DWORD read = GetPrivateProfileStringW(L"Settings", L"PriorityClass", L"3", value, 16, iniPath);
    if (read == 0) {
        return;
    }

    int cls = _wtoi(value);
    DWORD priority = NORMAL_PRIORITY_CLASS;
    switch (cls) {
        case 0: priority = IDLE_PRIORITY_CLASS; break;
        case 1: priority = BELOW_NORMAL_PRIORITY_CLASS; break;
        case 2: priority = NORMAL_PRIORITY_CLASS; break;
        case 3: priority = ABOVE_NORMAL_PRIORITY_CLASS; break;
        case 4: priority = HIGH_PRIORITY_CLASS; break;
        default: break;
    }

    SetPriorityClass(GetCurrentProcess(), priority);
}

static DWORD WINAPI bootstrap(LPVOID param) {
    (void)param;
    applyEarlyPriority();
    loadSiblingModule(L"version.dll");
    return 0;
}

BOOL WINAPI DllMain(HINSTANCE inst, DWORD reason, LPVOID reserved) {
    (void)reserved;

    if (reason == DLL_PROCESS_ATTACH) {
        DisableThreadLibraryCalls(inst);
        HANDLE thread = CreateThread(NULL, 0, bootstrap, NULL, 0, NULL);
        if (thread != NULL) {
            CloseHandle(thread);
        }
    }

    return TRUE;
}
