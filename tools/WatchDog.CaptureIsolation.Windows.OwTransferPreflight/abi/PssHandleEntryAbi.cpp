// Compile-only oracle for the installed Windows SDK. No entry point or API calls.
#if !defined(_MSC_VER) || !defined(_M_X64) || !defined(_WIN64) || defined(_M_ARM64EC)
#error This oracle requires MSVC targeting Windows AMD64.
#endif

#pragma pack(push, 8)
#include <Windows.h>
#include <processsnapshot.h>
#pragma pack(pop)

#include <bit>
#include <cstddef>
#include <type_traits>

// A single deliberately wrong expectation can demonstrate fail-closed compilation.
#ifndef WATCHDOG_ABI_EXPECTED_ENTRY_SIZE
#define WATCHDOG_ABI_EXPECTED_ENTRY_SIZE 136
#endif

namespace watchdog::abi {

#pragma pack(push, 8)
struct PackProbe {
    char prefix;
    void* pointer;
};
#pragma pack(pop)

static_assert(offsetof(PackProbe, pointer) == 8);
static_assert(sizeof(PackProbe) == 16 && alignof(PackProbe) == 8);
static_assert(std::endian::native == std::endian::little);
static_assert(sizeof(void*) == 8 && sizeof(HANDLE) == 8 && sizeof(ULONG_PTR) == 8);
static_assert(sizeof(HPSS) == 8 && sizeof(HPSSWALK) == 8);
static_assert(sizeof(DWORD) == 4 && sizeof(LONG) == 4 && sizeof(BOOL) == 4);
static_assert(sizeof(int) == 4 && sizeof(WORD) == 2 && sizeof(wchar_t) == 2);
static_assert(std::is_unsigned_v<DWORD> && std::is_unsigned_v<WORD>);
static_assert(std::is_signed_v<LONG> && std::is_signed_v<BOOL> && std::is_signed_v<int>);
static_assert(sizeof(FILETIME) == 8 && alignof(FILETIME) == 4);
static_assert(offsetof(FILETIME, dwLowDateTime) == 0 && offsetof(FILETIME, dwHighDateTime) == 4);
static_assert(sizeof(LARGE_INTEGER) == 8 && alignof(LARGE_INTEGER) == 8);
static_assert(std::is_signed_v<decltype(LARGE_INTEGER::QuadPart)>);
static_assert(sizeof(PSS_HANDLE_FLAGS) == 4 && sizeof(PSS_OBJECT_TYPE) == 4);

static_assert(PSS_HANDLE_NONE == 0 && PSS_HANDLE_HAVE_TYPE == 0x1);
static_assert(PSS_HANDLE_HAVE_NAME == 0x2 && PSS_HANDLE_HAVE_BASIC_INFORMATION == 0x4);
static_assert(PSS_HANDLE_HAVE_TYPE_SPECIFIC_INFORMATION == 0x8);
static_assert(PSS_OBJECT_TYPE_UNKNOWN == 0 && PSS_OBJECT_TYPE_PROCESS == 1);
static_assert(PSS_OBJECT_TYPE_THREAD == 2 && PSS_OBJECT_TYPE_MUTANT == 3);
static_assert(PSS_OBJECT_TYPE_EVENT == 4 && PSS_OBJECT_TYPE_SECTION == 5);
static_assert(PSS_OBJECT_TYPE_SEMAPHORE == 6);
static_assert(PSS_CAPTURE_HANDLES == 0x4);
static_assert(PSS_CAPTURE_HANDLE_BASIC_INFORMATION == 0x10);
static_assert(PSS_CAPTURE_HANDLE_TYPE_SPECIFIC_INFORMATION == 0x20);
static_assert(PSS_WALK_HANDLES == 2 && ERROR_NO_MORE_ITEMS == 259);

using Entry = PSS_HANDLE_ENTRY;
using Information = decltype(Entry::TypeSpecificInformation);
using Process = decltype(Information::Process);
using Thread = decltype(Information::Thread);
using Mutant = decltype(Information::Mutant);
using Event = decltype(Information::Event);
using Section = decltype(Information::Section);
using Semaphore = decltype(Information::Semaphore);

static_assert(std::is_standard_layout_v<Entry> && std::is_trivially_copyable_v<Entry>);
static_assert(sizeof(Entry) == WATCHDOG_ABI_EXPECTED_ENTRY_SIZE);
static_assert(alignof(Entry) == 8);
static_assert(sizeof(Information) == 48 && alignof(Information) == 8);

#define WATCHDOG_OFFSET(type, member, expected) \
    static_assert(offsetof(type, member) == expected, #type "::" #member " offset")
#define WATCHDOG_FIELD(type, member, expected_type) \
    static_assert(std::is_same_v<decltype(type::member), expected_type>, #type "::" #member " type")

WATCHDOG_OFFSET(Entry, Handle, 0);
WATCHDOG_OFFSET(Entry, Flags, 8);
WATCHDOG_OFFSET(Entry, ObjectType, 12);
WATCHDOG_OFFSET(Entry, CaptureTime, 16);
WATCHDOG_OFFSET(Entry, Attributes, 24);
WATCHDOG_OFFSET(Entry, GrantedAccess, 28);
WATCHDOG_OFFSET(Entry, HandleCount, 32);
WATCHDOG_OFFSET(Entry, PointerCount, 36);
WATCHDOG_OFFSET(Entry, PagedPoolCharge, 40);
WATCHDOG_OFFSET(Entry, NonPagedPoolCharge, 44);
WATCHDOG_OFFSET(Entry, CreationTime, 48);
WATCHDOG_OFFSET(Entry, TypeNameLength, 56);
WATCHDOG_OFFSET(Entry, TypeName, 64);
WATCHDOG_OFFSET(Entry, ObjectNameLength, 72);
WATCHDOG_OFFSET(Entry, ObjectName, 80);
WATCHDOG_OFFSET(Entry, TypeSpecificInformation, 88);
WATCHDOG_FIELD(Entry, Handle, HANDLE);
WATCHDOG_FIELD(Entry, Flags, PSS_HANDLE_FLAGS);
WATCHDOG_FIELD(Entry, ObjectType, PSS_OBJECT_TYPE);
WATCHDOG_FIELD(Entry, CaptureTime, FILETIME);
WATCHDOG_FIELD(Entry, Attributes, DWORD);
WATCHDOG_FIELD(Entry, GrantedAccess, DWORD);
WATCHDOG_FIELD(Entry, HandleCount, DWORD);
WATCHDOG_FIELD(Entry, PointerCount, DWORD);
WATCHDOG_FIELD(Entry, PagedPoolCharge, DWORD);
WATCHDOG_FIELD(Entry, NonPagedPoolCharge, DWORD);
WATCHDOG_FIELD(Entry, CreationTime, FILETIME);
WATCHDOG_FIELD(Entry, TypeNameLength, WORD);
WATCHDOG_FIELD(Entry, TypeName, const wchar_t*);
WATCHDOG_FIELD(Entry, ObjectNameLength, WORD);
WATCHDOG_FIELD(Entry, ObjectName, const wchar_t*);

WATCHDOG_OFFSET(Information, Process, 0);
WATCHDOG_OFFSET(Information, Thread, 0);
WATCHDOG_OFFSET(Information, Mutant, 0);
WATCHDOG_OFFSET(Information, Event, 0);
WATCHDOG_OFFSET(Information, Section, 0);
WATCHDOG_OFFSET(Information, Semaphore, 0);

static_assert(sizeof(Process) == 40 && alignof(Process) == 8);
WATCHDOG_OFFSET(Process, ExitStatus, 0);
WATCHDOG_OFFSET(Process, PebBaseAddress, 8);
WATCHDOG_OFFSET(Process, AffinityMask, 16);
WATCHDOG_OFFSET(Process, BasePriority, 24);
WATCHDOG_OFFSET(Process, ProcessId, 28);
WATCHDOG_OFFSET(Process, ParentProcessId, 32);
WATCHDOG_OFFSET(Process, Flags, 36);
WATCHDOG_FIELD(Process, ExitStatus, DWORD);
WATCHDOG_FIELD(Process, PebBaseAddress, void*);
WATCHDOG_FIELD(Process, AffinityMask, ULONG_PTR);
WATCHDOG_FIELD(Process, BasePriority, LONG);
WATCHDOG_FIELD(Process, ProcessId, DWORD);
WATCHDOG_FIELD(Process, ParentProcessId, DWORD);
WATCHDOG_FIELD(Process, Flags, DWORD);

static_assert(sizeof(Thread) == 48 && alignof(Thread) == 8);
WATCHDOG_OFFSET(Thread, ExitStatus, 0);
WATCHDOG_OFFSET(Thread, TebBaseAddress, 8);
WATCHDOG_OFFSET(Thread, ProcessId, 16);
WATCHDOG_OFFSET(Thread, ThreadId, 20);
WATCHDOG_OFFSET(Thread, AffinityMask, 24);
WATCHDOG_OFFSET(Thread, Priority, 32);
WATCHDOG_OFFSET(Thread, BasePriority, 36);
WATCHDOG_OFFSET(Thread, Win32StartAddress, 40);
WATCHDOG_FIELD(Thread, ExitStatus, DWORD);
WATCHDOG_FIELD(Thread, TebBaseAddress, void*);
WATCHDOG_FIELD(Thread, ProcessId, DWORD);
WATCHDOG_FIELD(Thread, ThreadId, DWORD);
WATCHDOG_FIELD(Thread, AffinityMask, ULONG_PTR);
WATCHDOG_FIELD(Thread, Priority, int);
WATCHDOG_FIELD(Thread, BasePriority, int);
WATCHDOG_FIELD(Thread, Win32StartAddress, void*);

static_assert(sizeof(Mutant) == 16 && alignof(Mutant) == 4);
WATCHDOG_OFFSET(Mutant, CurrentCount, 0);
WATCHDOG_OFFSET(Mutant, Abandoned, 4);
WATCHDOG_OFFSET(Mutant, OwnerProcessId, 8);
WATCHDOG_OFFSET(Mutant, OwnerThreadId, 12);
WATCHDOG_FIELD(Mutant, CurrentCount, LONG);
WATCHDOG_FIELD(Mutant, Abandoned, BOOL);
WATCHDOG_FIELD(Mutant, OwnerProcessId, DWORD);
WATCHDOG_FIELD(Mutant, OwnerThreadId, DWORD);

static_assert(sizeof(Event) == 8 && alignof(Event) == 4);
WATCHDOG_OFFSET(Event, ManualReset, 0);
WATCHDOG_OFFSET(Event, Signaled, 4);
WATCHDOG_FIELD(Event, ManualReset, BOOL);
WATCHDOG_FIELD(Event, Signaled, BOOL);

static_assert(sizeof(Section) == 24 && alignof(Section) == 8);
WATCHDOG_OFFSET(Section, BaseAddress, 0);
WATCHDOG_OFFSET(Section, AllocationAttributes, 8);
WATCHDOG_OFFSET(Section, MaximumSize, 16);
WATCHDOG_FIELD(Section, BaseAddress, void*);
WATCHDOG_FIELD(Section, AllocationAttributes, DWORD);
WATCHDOG_FIELD(Section, MaximumSize, LARGE_INTEGER);

static_assert(sizeof(Semaphore) == 8 && alignof(Semaphore) == 4);
WATCHDOG_OFFSET(Semaphore, CurrentCount, 0);
WATCHDOG_OFFSET(Semaphore, MaximumCount, 4);
WATCHDOG_FIELD(Semaphore, CurrentCount, LONG);
WATCHDOG_FIELD(Semaphore, MaximumCount, LONG);

#undef WATCHDOG_FIELD
#undef WATCHDOG_OFFSET

// Unevaluated types only: these declarations do not import, link or invoke PSS.
using CaptureSignature = DWORD(WINAPI*)(HANDLE, PSS_CAPTURE_FLAGS, DWORD, HPSS*);
using FreeSignature = DWORD(WINAPI*)(HANDLE, HPSS);
using WalkSignature = DWORD(WINAPI*)(HPSS, PSS_WALK_INFORMATION_CLASS, HPSSWALK, void*, DWORD);
using MarkerCreateSignature = DWORD(WINAPI*)(const PSS_ALLOCATOR*, HPSSWALK*);
using MarkerFreeSignature = DWORD(WINAPI*)(HPSSWALK);
static_assert(std::is_same_v<decltype(&PssCaptureSnapshot), CaptureSignature>);
static_assert(std::is_same_v<decltype(&PssFreeSnapshot), FreeSignature>);
static_assert(std::is_same_v<decltype(&PssWalkSnapshot), WalkSignature>);
static_assert(std::is_same_v<decltype(&PssWalkMarkerCreate), MarkerCreateSignature>);
static_assert(std::is_same_v<decltype(&PssWalkMarkerFree), MarkerFreeSignature>);

} // namespace watchdog::abi
