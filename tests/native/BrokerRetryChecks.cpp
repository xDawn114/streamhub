// Compile with STREAMHUB_BROKER_SOURCE set to the pinned, patched .cpp.
// Only OS responses/time are simulated; the production retry function runs.
#include <Windows.h>
#include <cassert>
#include <iostream>
#include <stdexcept>
#include <string>

namespace fixture {
  inline std::string scenario;
  inline DWORD error = ERROR_FILE_NOT_FOUND;
  inline ULONGLONG clock = 0;
  inline unsigned attempts = 0, service_queries = 0;
}
HANDLE WINAPI test_CreateFileA(LPCSTR, DWORD, DWORD, LPSECURITY_ATTRIBUTES, DWORD, DWORD, HANDLE) {
  ++fixture::attempts;
  if (fixture::scenario == "starting" && fixture::clock >= 32) return reinterpret_cast<HANDLE>(123);
  fixture::error = fixture::scenario == "access-denied" ? ERROR_ACCESS_DENIED : ERROR_FILE_NOT_FOUND;
  return INVALID_HANDLE_VALUE;
}
DWORD WINAPI test_GetLastError() { return fixture::error; }
void WINAPI test_SetLastError(DWORD error) { fixture::error = error; }
ULONGLONG WINAPI test_GetTickCount64() { return fixture::clock; }
void WINAPI test_Sleep(DWORD) { fixture::clock += 16; } // 10ms rounded up by Windows.
BOOL WINAPI test_WaitNamedPipeA(LPCSTR, DWORD) { fixture::clock += 16; fixture::error = ERROR_SEM_TIMEOUT; return FALSE; }
SC_HANDLE WINAPI test_OpenSCManagerW(LPCWSTR, LPCWSTR, DWORD) {
  if (fixture::scenario == "scm-unknown") { fixture::error = ERROR_ACCESS_DENIED; return nullptr; }
  return reinterpret_cast<SC_HANDLE>(10);
}
SC_HANDLE WINAPI test_OpenServiceW(SC_HANDLE, LPCWSTR, DWORD) {
  ++fixture::service_queries;
  if (fixture::scenario == "absent") { fixture::error = ERROR_SERVICE_DOES_NOT_EXIST; return nullptr; }
  return reinterpret_cast<SC_HANDLE>(11);
}
BOOL WINAPI test_CloseServiceHandle(SC_HANDLE) { return TRUE; }
BOOL WINAPI test_CloseHandle(HANDLE) { return TRUE; }

#define CreateFileA test_CreateFileA
#define GetLastError test_GetLastError
#define SetLastError test_SetLastError
#define GetTickCount64 test_GetTickCount64
#define Sleep test_Sleep
#define WaitNamedPipeA test_WaitNamedPipeA
#define OpenSCManagerW test_OpenSCManagerW
#define OpenServiceW test_OpenServiceW
#define CloseServiceHandle test_CloseServiceHandle
#define CloseHandle test_CloseHandle
#include STREAMHUB_BROKER_SOURCE

int main() {
  using namespace lvh::detail::windows_broker::windows_broker_client_implementation;
  for (auto scenario : {"absent", "starting", "installed-no-pipe", "scm-unknown", "access-denied"}) {
    fixture::scenario = scenario; fixture::clock = fixture::attempts = fixture::service_queries = 0;
    auto pipe = connect_to_broker_pipe();
    const auto expected_success = fixture::scenario == "starting";
    if (bool(pipe) != expected_success) throw std::runtime_error("Unexpected broker availability");
    if (fixture::scenario == "absent" && (fixture::clock != 0 || fixture::service_queries != 1))
      throw std::runtime_error("Absent service must fail promptly, not become licensed");
    if ((fixture::scenario == "installed-no-pipe" || fixture::scenario == "scm-unknown") &&
        (fixture::clock < 5000 || fixture::clock > 5016))
      throw std::runtime_error("Existing or unknown service lost its bounded startup retry");
    if (fixture::scenario == "access-denied" && (fixture::clock != 0 || fixture::service_queries != 0))
      throw std::runtime_error("Access denial must remain an error");
    std::cout << scenario << ": connected=" << bool(pipe) << " simulated_ms=" << fixture::clock
      << " attempts=" << fixture::attempts << std::endl;
  }
}
