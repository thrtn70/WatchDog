// Compile-only x64 SDK check for the legacy Win32 rename fixture.
// This file intentionally makes no file-system calls and is not linked or run.
#define WIN32_LEAN_AND_MEAN
#include <windows.h>

#include <cstddef>
#include <type_traits>

#if !defined(_M_X64)
#error The publication fixture supports only the Windows x64 target.
#endif

static_assert(sizeof(void*) == 8, "The publication fixture is Windows x64 only.");
static_assert(sizeof(wchar_t) == 2, "The rename name is UTF-16.");
static_assert(sizeof(WCHAR) == 2, "Unexpected SDK WCHAR width.");
static_assert(sizeof(FILE_RENAME_INFO::FileName[0]) == 2,
              "Unexpected rename-name element width.");
static_assert(sizeof(DWORD) == 4, "Unexpected SDK DWORD width.");
static_assert(sizeof(HANDLE) == 8, "Unexpected SDK HANDLE width.");
static_assert(static_cast<int>(FileRenameInfo) == 3, "Unexpected legacy information class.");

static_assert(alignof(FILE_RENAME_INFO) == 8, "Unexpected FILE_RENAME_INFO alignment.");
static_assert(sizeof(FILE_RENAME_INFO) == 24, "Unexpected FILE_RENAME_INFO size.");
static_assert(offsetof(FILE_RENAME_INFO, ReplaceIfExists) == 0,
              "Unexpected replacement flag offset.");
static_assert(sizeof(FILE_RENAME_INFO::ReplaceIfExists) == 1,
              "Unexpected replacement flag width.");
static_assert(sizeof(FILE_RENAME_INFO::Flags) == 4,
              "Unexpected Ex union member width.");
static_assert(offsetof(FILE_RENAME_INFO, RootDirectory) == 8, "Unexpected root offset.");
static_assert(offsetof(FILE_RENAME_INFO, FileNameLength) == 16, "Unexpected length offset.");
static_assert(offsetof(FILE_RENAME_INFO, FileName) == 20, "Unexpected name offset.");

using ExpectedSetFileInformationByHandle = BOOL(WINAPI*)(
    HANDLE, FILE_INFO_BY_HANDLE_CLASS, LPVOID, DWORD);
static_assert(std::is_same_v<decltype(&SetFileInformationByHandle),
                             ExpectedSetFileInformationByHandle>,
              "Unexpected SetFileInformationByHandle declaration.");
