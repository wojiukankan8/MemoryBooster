#pragma once

#ifdef MEMORYCORE_EXPORTS
#define MEMCORE_API __declspec(dllexport)
#else
#define MEMCORE_API __declspec(dllimport)
#endif

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* Memory cleaning */
MEMCORE_API int CleanAllWorkingSets(void);
MEMCORE_API int CleanProcessWorkingSet(uint32_t pid);

/* Process info */
#pragma pack(push, 1)
typedef struct {
    uint32_t pid;
    wchar_t  name[260];
    wchar_t  filePath[520];
    uint64_t workingSetSize;
    uint64_t privateBytes;
    double   cpuPercent;
} ProcessInfoNative;
#pragma pack(pop)

MEMCORE_API int GetProcessList(ProcessInfoNative* buffer, int bufferCount);
MEMCORE_API int KillProcess(uint32_t pid);

/* System memory info */
#pragma pack(push, 1)
typedef struct {
    uint64_t totalPhysical;
    uint64_t availablePhysical;
    uint32_t memoryLoadPercent;
} SystemMemoryInfo;
#pragma pack(pop)

MEMCORE_API int GetSystemMemoryInfo(SystemMemoryInfo* info);

/* Network speed */
#pragma pack(push, 1)
typedef struct {
    uint64_t bytesSent;
    uint64_t bytesRecv;
} NetworkSnapshot;
#pragma pack(pop)

MEMCORE_API int GetNetworkSnapshot(NetworkSnapshot* snapshot);

/* ---- Per-process network stats (TCP, via ESTATS). Requires admin. ---- */
#pragma pack(push, 1)
typedef struct {
    uint32_t pid;
    uint64_t bytesIn;         /* delta since last call (TCP data bytes, aggregated) */
    uint64_t bytesOut;
    double   bytesInPerSec;   /* bytes/second, computed by the DLL */
    double   bytesOutPerSec;
    uint32_t tcpConnections;  /* currently open TCP connections owned by this pid */
    uint32_t udpConnections;  /* currently open UDP endpoints owned by this pid */
} ProcNetInfoNative;
#pragma pack(pop)

/* Fill `buffer` with up to `bufferCount` entries (one per active PID). Returns
   the number of entries written. Internally maintains per-connection state so
   the caller only sees aggregated bytes since the previous invocation. */
MEMCORE_API int GetPerProcessNetStats(ProcNetInfoNative* buffer, int bufferCount);

/* Resets the internal ESTATS/connection sampling state (call when the UI
   switches back to the network tab, so the first sample starts from zero). */
MEMCORE_API void ResetNetStats(void);

/* Total physical NIC throughput using 64-bit counters (GetIfTable2). */
#pragma pack(push, 1)
typedef struct {
    uint64_t totalBytesIn;
    uint64_t totalBytesOut;
    double   bytesInPerSec;
    double   bytesOutPerSec;
} NetTotalInfo;
#pragma pack(pop)

MEMCORE_API int GetNetTotalStats(NetTotalInfo* info);

#ifdef __cplusplus
}
#endif
