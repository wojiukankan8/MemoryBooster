#define MEMORYCORE_EXPORTS
#define WIN32_LEAN_AND_MEAN
#define _WINSOCK_DEPRECATED_NO_WARNINGS

#include "MemoryCore.h"

// Winsock headers must precede windows.h to avoid the legacy winsock.h pull-in.
#include <winsock2.h>
#include <ws2tcpip.h>
#include <ws2ipdef.h>
#include <in6addr.h>

#include <windows.h>
#include <psapi.h>
#include <tlhelp32.h>
#include <iphlpapi.h>
#include <tcpmib.h>
#include <udpmib.h>
#include <tcpestats.h>
#include <string.h>

#include <map>
#include <unordered_map>
#include <vector>
#include <mutex>

#pragma comment(lib, "psapi.lib")
#pragma comment(lib, "iphlpapi.lib")
#pragma comment(lib, "ws2_32.lib")

/* ====================================================================== */
/* Memory Cleaning                                                        */
/* ====================================================================== */

MEMCORE_API int CleanAllWorkingSets()
{
    int cleaned = 0;
    HANDLE hSnap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (hSnap == INVALID_HANDLE_VALUE) return 0;

    PROCESSENTRY32W pe;
    memset(&pe, 0, sizeof(pe));
    pe.dwSize = sizeof(pe);

    if (Process32FirstW(hSnap, &pe)) {
        do {
            if (pe.th32ProcessID == 0) continue;
            HANDLE hProc = OpenProcess(
                PROCESS_QUERY_INFORMATION | PROCESS_SET_QUOTA,
                FALSE, pe.th32ProcessID);
            if (hProc) {
                if (EmptyWorkingSet(hProc))
                    cleaned++;
                CloseHandle(hProc);
            }
        } while (Process32NextW(hSnap, &pe));
    }
    CloseHandle(hSnap);
    return cleaned;
}

MEMCORE_API int CleanProcessWorkingSet(uint32_t pid)
{
    HANDLE hProc = OpenProcess(
        PROCESS_QUERY_INFORMATION | PROCESS_SET_QUOTA,
        FALSE, pid);
    if (!hProc) return 0;
    int ok = EmptyWorkingSet(hProc) ? 1 : 0;
    CloseHandle(hProc);
    return ok;
}

/* ====================================================================== */
/* CPU sampling (per-process kernel+user ticks, delta vs wall clock)      */
/*                                                                        */
/* Each PID keeps its own lastSampleTime so newly-appeared processes      */
/* don’t get a distorted first reading and long gaps between GetProcessList */
/* calls don’t over-amplify the value. Matches Task Manager behaviour.     */
/* ====================================================================== */

struct CpuEntry { uint64_t ticks; ULONGLONG sampleMs; };
static std::unordered_map<uint32_t, CpuEntry> g_cpuMap;
static int g_cpuCoreCount = 0;
static std::mutex g_cpuMutex;

static double SampleCpuPercent(HANDLE hProc, uint32_t pid, ULONGLONG nowMs)
{
    if (g_cpuCoreCount == 0) {
        SYSTEM_INFO si;
        GetSystemInfo(&si);
        g_cpuCoreCount = (int)si.dwNumberOfProcessors;
        if (g_cpuCoreCount <= 0) g_cpuCoreCount = 1;
    }

    FILETIME ftCre, ftExit, ftKer, ftUser;
    if (!GetProcessTimes(hProc, &ftCre, &ftExit, &ftKer, &ftUser)) return 0.0;

    ULARGE_INTEGER k, u;
    k.LowPart = ftKer.dwLowDateTime;  k.HighPart = ftKer.dwHighDateTime;
    u.LowPart = ftUser.dwLowDateTime; u.HighPart = ftUser.dwHighDateTime;
    uint64_t ticks = k.QuadPart + u.QuadPart;

    double cpu = 0.0;
    auto it = g_cpuMap.find(pid);
    if (it != g_cpuMap.end() && ticks >= it->second.ticks && it->second.sampleMs > 0) {
        double elapsedSec = (nowMs - it->second.sampleMs) / 1000.0;
        if (elapsedSec >= 0.05) {
            double cpuSec = (ticks - it->second.ticks) / 10'000'000.0;
            cpu = cpuSec / (elapsedSec * g_cpuCoreCount) * 100.0;
            if (cpu < 0) cpu = 0;
            if (cpu > 100) cpu = 100;
        }
    }
    g_cpuMap[pid] = { ticks, nowMs };
    return cpu;
}

/* ====================================================================== */
/* Process List                                                           */
/* ====================================================================== */

MEMCORE_API int GetProcessList(ProcessInfoNative* buffer, int bufferCount)
{
    if (!buffer || bufferCount <= 0) return 0;

    HANDLE hSnap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (hSnap == INVALID_HANDLE_VALUE) return 0;

    std::lock_guard<std::mutex> lock(g_cpuMutex);
    ULONGLONG now = GetTickCount64();

    PROCESSENTRY32W pe;
    memset(&pe, 0, sizeof(pe));
    pe.dwSize = sizeof(pe);
    int count = 0;

    std::unordered_map<uint32_t, CpuEntry> seen;
    seen.reserve(256);

    if (Process32FirstW(hSnap, &pe)) {
        do {
            if (count >= bufferCount) break;
            if (pe.th32ProcessID == 0) continue;

            ProcessInfoNative* p = &buffer[count];
            memset(p, 0, sizeof(ProcessInfoNative));
            p->pid = pe.th32ProcessID;
            wcsncpy_s(p->name, 260, pe.szExeFile, _TRUNCATE);

            HANDLE hProc = OpenProcess(
                PROCESS_QUERY_INFORMATION | PROCESS_VM_READ,
                FALSE, pe.th32ProcessID);
            if (!hProc) {
                // Retry with a lighter access mask (protected processes)
                hProc = OpenProcess(
                    PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pe.th32ProcessID);
            }
            if (hProc) {
                PROCESS_MEMORY_COUNTERS_EX pmc;
                memset(&pmc, 0, sizeof(pmc));
                pmc.cb = sizeof(pmc);
                if (GetProcessMemoryInfo(hProc,
                    (PROCESS_MEMORY_COUNTERS*)&pmc, sizeof(pmc)))
                {
                    p->workingSetSize = pmc.WorkingSetSize;
                    p->privateBytes = pmc.PrivateUsage;
                }

                WCHAR path[520];
                memset(path, 0, sizeof(path));
                DWORD pathLen = 520;
                if (QueryFullProcessImageNameW(hProc, 0, path, &pathLen)) {
                    wcsncpy_s(p->filePath, 520, path, _TRUNCATE);
                }

                p->cpuPercent = SampleCpuPercent(hProc, pe.th32ProcessID, now);
                seen[pe.th32ProcessID] = g_cpuMap[pe.th32ProcessID];

                CloseHandle(hProc);
            }

            count++;
        } while (Process32NextW(hSnap, &pe));
    }
    CloseHandle(hSnap);

    // Drop exited processes from the CPU map so it doesn't grow unbounded.
    g_cpuMap.swap(seen);
    return count;
}

/* ====================================================================== */
/* Kill Process                                                           */
/* ====================================================================== */

MEMCORE_API int KillProcess(uint32_t pid)
{
    HANDLE hProc = OpenProcess(PROCESS_TERMINATE, FALSE, pid);
    if (!hProc) return 0;
    int ok = TerminateProcess(hProc, 1) ? 1 : 0;
    CloseHandle(hProc);
    return ok;
}

/* ====================================================================== */
/* System Memory Info                                                     */
/* ====================================================================== */

MEMCORE_API int GetSystemMemoryInfo(SystemMemoryInfo* info)
{
    if (!info) return 0;
    MEMORYSTATUSEX ms;
    memset(&ms, 0, sizeof(ms));
    ms.dwLength = sizeof(ms);
    if (!GlobalMemoryStatusEx(&ms)) return 0;

    info->totalPhysical = ms.ullTotalPhys;
    info->availablePhysical = ms.ullAvailPhys;
    info->memoryLoadPercent = ms.dwMemoryLoad;
    return 1;
}

/* ====================================================================== */
/* Legacy network snapshot (kept for ABI compatibility)                   */
/* ====================================================================== */

MEMCORE_API int GetNetworkSnapshot(NetworkSnapshot* snapshot)
{
    if (!snapshot) return 0;

    PMIB_IF_TABLE2 table = nullptr;
    if (GetIfTable2(&table) != NO_ERROR || !table) return 0;

    uint64_t sent = 0, recv = 0;
    for (ULONG i = 0; i < table->NumEntries; i++) {
        const MIB_IF_ROW2& r = table->Table[i];
        if (r.OperStatus != IfOperStatusUp) continue;
        if (r.Type == IF_TYPE_SOFTWARE_LOOPBACK) continue;
        // Skip tunnel / virtual adapters (WSL, Hyper-V vEthernet, VMware, VPN).
        if (r.Type != IF_TYPE_ETHERNET_CSMACD &&
            r.Type != IF_TYPE_IEEE80211 &&
            r.Type != IF_TYPE_IEEE80216_WMAN &&
            r.Type != IF_TYPE_WWANPP &&
            r.Type != IF_TYPE_WWANPP2) continue;

        sent += r.OutOctets;
        recv += r.InOctets;
    }
    FreeMibTable(table);

    snapshot->bytesSent = sent;
    snapshot->bytesRecv = recv;
    return 1;
}

/* ====================================================================== */
/* Total NIC throughput using 64-bit counters                             */
/* ====================================================================== */

static ULONGLONG g_totalLastSampleMs = 0;
static uint64_t g_totalLastIn = 0, g_totalLastOut = 0;
static std::mutex g_totalMutex;

MEMCORE_API int GetNetTotalStats(NetTotalInfo* info)
{
    if (!info) return 0;
    memset(info, 0, sizeof(*info));

    PMIB_IF_TABLE2 table = nullptr;
    if (GetIfTable2(&table) != NO_ERROR || !table) return 0;

    uint64_t sent = 0, recv = 0;
    for (ULONG i = 0; i < table->NumEntries; i++) {
        const MIB_IF_ROW2& r = table->Table[i];
        if (r.OperStatus != IfOperStatusUp) continue;
        if (r.Type == IF_TYPE_SOFTWARE_LOOPBACK) continue;
        if (r.Type != IF_TYPE_ETHERNET_CSMACD &&
            r.Type != IF_TYPE_IEEE80211 &&
            r.Type != IF_TYPE_IEEE80216_WMAN &&
            r.Type != IF_TYPE_WWANPP &&
            r.Type != IF_TYPE_WWANPP2) continue;
        sent += r.OutOctets;
        recv += r.InOctets;
    }
    FreeMibTable(table);

    std::lock_guard<std::mutex> lock(g_totalMutex);
    ULONGLONG now = GetTickCount64();
    double elapsed = (g_totalLastSampleMs == 0) ? 1.0
                                                 : (now - g_totalLastSampleMs) / 1000.0;
    if (elapsed < 0.05) elapsed = 0.05;

    info->totalBytesIn = recv;
    info->totalBytesOut = sent;
    if (g_totalLastSampleMs != 0) {
        info->bytesInPerSec = (recv >= g_totalLastIn)
            ? (double)(recv - g_totalLastIn) / elapsed : 0.0;
        info->bytesOutPerSec = (sent >= g_totalLastOut)
            ? (double)(sent - g_totalLastOut) / elapsed : 0.0;
    }
    g_totalLastIn = recv;
    g_totalLastOut = sent;
    g_totalLastSampleMs = now;
    return 1;
}

/* ====================================================================== */
/* Per-process TCP bandwidth via ESTATS                                   */
/* ====================================================================== */

struct V4Key {
    DWORD la, ra;  // local/remote IPv4 addresses (network byte order, as MIB provides)
    DWORD lp, rp;  // local/remote ports (MIB layout: low 16 bits, high 16 bits zero)
    bool operator<(const V4Key& o) const {
        if (la != o.la) return la < o.la;
        if (ra != o.ra) return ra < o.ra;
        if (lp != o.lp) return lp < o.lp;
        return rp < o.rp;
    }
};

struct V6Key {
    IN6_ADDR la, ra;
    DWORD lsid, rsid;
    DWORD lp, rp;
    bool operator<(const V6Key& o) const {
        int c = memcmp(&la, &o.la, sizeof(IN6_ADDR));
        if (c) return c < 0;
        c = memcmp(&ra, &o.ra, sizeof(IN6_ADDR));
        if (c) return c < 0;
        if (lsid != o.lsid) return lsid < o.lsid;
        if (rsid != o.rsid) return rsid < o.rsid;
        if (lp != o.lp) return lp < o.lp;
        return rp < o.rp;
    }
};

struct ConnState {
    DWORD pid;
    uint64_t prevIn, prevOut;
    ULONGLONG lastSeenMs;
    bool enabled;   // true once ESTATS collection has been turned on
};

static std::map<V4Key, ConnState> g_v4Conns;
static std::map<V6Key, ConnState> g_v6Conns;
static ULONGLONG g_netLastSampleMs = 0;
static std::mutex g_netMutex;

MEMCORE_API void ResetNetStats(void)
{
    std::lock_guard<std::mutex> lock(g_netMutex);
    g_v4Conns.clear();
    g_v6Conns.clear();
    g_netLastSampleMs = 0;
}

static bool EnableEStatsV4(MIB_TCPROW* row)
{
    TCP_ESTATS_DATA_RW_v0 rw;
    rw.EnableCollection = TRUE;
    DWORD rc = SetPerTcpConnectionEStats(row, TcpConnectionEstatsData,
        (PUCHAR)&rw, 0, sizeof(rw), 0);
    return rc == NO_ERROR;
}

static bool ReadEStatsV4(MIB_TCPROW* row, TCP_ESTATS_DATA_ROD_v0* rod)
{
    memset(rod, 0, sizeof(*rod));
    DWORD rc = GetPerTcpConnectionEStats(row, TcpConnectionEstatsData,
        nullptr, 0, 0, nullptr, 0, 0,
        (PUCHAR)rod, 0, sizeof(*rod));
    return rc == NO_ERROR;
}

static bool EnableEStatsV6(MIB_TCP6ROW* row)
{
    TCP_ESTATS_DATA_RW_v0 rw;
    rw.EnableCollection = TRUE;
    DWORD rc = SetPerTcp6ConnectionEStats(row, TcpConnectionEstatsData,
        (PUCHAR)&rw, 0, sizeof(rw), 0);
    return rc == NO_ERROR;
}

static bool ReadEStatsV6(MIB_TCP6ROW* row, TCP_ESTATS_DATA_ROD_v0* rod)
{
    memset(rod, 0, sizeof(*rod));
    DWORD rc = GetPerTcp6ConnectionEStats(row, TcpConnectionEstatsData,
        nullptr, 0, 0, nullptr, 0, 0,
        (PUCHAR)rod, 0, sizeof(*rod));
    return rc == NO_ERROR;
}

static void AccumulateV4(std::unordered_map<DWORD, ProcNetInfoNative>& agg, ULONGLONG now)
{
    DWORD size = 0;
    GetExtendedTcpTable(nullptr, &size, FALSE, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
    if (size == 0) return;

    std::vector<BYTE> buf(size);
    if (GetExtendedTcpTable(buf.data(), &size, FALSE, AF_INET,
            TCP_TABLE_OWNER_PID_ALL, 0) != NO_ERROR) return;

    auto* table = (MIB_TCPTABLE_OWNER_PID*)buf.data();
    for (DWORD i = 0; i < table->dwNumEntries; i++) {
        const auto& r = table->table[i];
        DWORD pid = r.dwOwningPid;
        if (pid == 0) continue;

        MIB_TCPROW row;
        row.dwState = r.dwState;
        row.dwLocalAddr = r.dwLocalAddr;
        row.dwLocalPort = r.dwLocalPort;
        row.dwRemoteAddr = r.dwRemoteAddr;
        row.dwRemotePort = r.dwRemotePort;

        V4Key key{ r.dwLocalAddr, r.dwRemoteAddr, r.dwLocalPort, r.dwRemotePort };

        auto it = g_v4Conns.find(key);
        bool isNew = (it == g_v4Conns.end()) || (it->second.pid != pid);

        if (isNew) {
            if (!EnableEStatsV4(&row)) {
                // Without admin rights this fails; still count the connection.
                auto& pi = agg[pid];
                pi.pid = pid; pi.tcpConnections++;
                continue;
            }
            TCP_ESTATS_DATA_ROD_v0 rod;
            if (!ReadEStatsV4(&row, &rod)) continue;
            ConnState st{ pid, rod.DataBytesIn, rod.DataBytesOut, now, true };
            g_v4Conns[key] = st;
            auto& pi = agg[pid];
            pi.pid = pid; pi.tcpConnections++;
        } else {
            TCP_ESTATS_DATA_ROD_v0 rod;
            if (!ReadEStatsV4(&row, &rod)) continue;
            auto& st = it->second;
            auto& pi = agg[pid];
            pi.pid = pid; pi.tcpConnections++;
            if (rod.DataBytesIn  >= st.prevIn)  pi.bytesIn  += rod.DataBytesIn  - st.prevIn;
            if (rod.DataBytesOut >= st.prevOut) pi.bytesOut += rod.DataBytesOut - st.prevOut;
            st.prevIn = rod.DataBytesIn;
            st.prevOut = rod.DataBytesOut;
            st.lastSeenMs = now;
        }
    }
}

static void AccumulateV6(std::unordered_map<DWORD, ProcNetInfoNative>& agg, ULONGLONG now)
{
    DWORD size = 0;
    GetExtendedTcpTable(nullptr, &size, FALSE, AF_INET6, TCP_TABLE_OWNER_PID_ALL, 0);
    if (size == 0) return;

    std::vector<BYTE> buf(size);
    if (GetExtendedTcpTable(buf.data(), &size, FALSE, AF_INET6,
            TCP_TABLE_OWNER_PID_ALL, 0) != NO_ERROR) return;

    auto* table = (MIB_TCP6TABLE_OWNER_PID*)buf.data();
    for (DWORD i = 0; i < table->dwNumEntries; i++) {
        const auto& r = table->table[i];
        DWORD pid = r.dwOwningPid;
        if (pid == 0) continue;

        MIB_TCP6ROW row;
        memset(&row, 0, sizeof(row));
        memcpy(&row.LocalAddr, &r.ucLocalAddr, sizeof(IN6_ADDR));
        memcpy(&row.RemoteAddr, &r.ucRemoteAddr, sizeof(IN6_ADDR));
        row.dwLocalScopeId = r.dwLocalScopeId;
        row.dwRemoteScopeId = r.dwRemoteScopeId;
        row.dwLocalPort = r.dwLocalPort;
        row.dwRemotePort = r.dwRemotePort;
        row.State = (MIB_TCP_STATE)r.dwState;

        V6Key key;
        memcpy(&key.la, &r.ucLocalAddr, sizeof(IN6_ADDR));
        memcpy(&key.ra, &r.ucRemoteAddr, sizeof(IN6_ADDR));
        key.lsid = r.dwLocalScopeId;
        key.rsid = r.dwRemoteScopeId;
        key.lp = r.dwLocalPort;
        key.rp = r.dwRemotePort;

        auto it = g_v6Conns.find(key);
        bool isNew = (it == g_v6Conns.end()) || (it->second.pid != pid);

        if (isNew) {
            if (!EnableEStatsV6(&row)) {
                auto& pi = agg[pid];
                pi.pid = pid; pi.tcpConnections++;
                continue;
            }
            TCP_ESTATS_DATA_ROD_v0 rod;
            if (!ReadEStatsV6(&row, &rod)) continue;
            ConnState st{ pid, rod.DataBytesIn, rod.DataBytesOut, now, true };
            g_v6Conns[key] = st;
            auto& pi = agg[pid];
            pi.pid = pid; pi.tcpConnections++;
        } else {
            TCP_ESTATS_DATA_ROD_v0 rod;
            if (!ReadEStatsV6(&row, &rod)) continue;
            auto& st = it->second;
            auto& pi = agg[pid];
            pi.pid = pid; pi.tcpConnections++;
            if (rod.DataBytesIn  >= st.prevIn)  pi.bytesIn  += rod.DataBytesIn  - st.prevIn;
            if (rod.DataBytesOut >= st.prevOut) pi.bytesOut += rod.DataBytesOut - st.prevOut;
            st.prevIn = rod.DataBytesIn;
            st.prevOut = rod.DataBytesOut;
            st.lastSeenMs = now;
        }
    }
}

static void AccumulateUdp(std::unordered_map<DWORD, ProcNetInfoNative>& agg, int family)
{
    DWORD size = 0;
    GetExtendedUdpTable(nullptr, &size, FALSE, family, UDP_TABLE_OWNER_PID, 0);
    if (size == 0) return;
    std::vector<BYTE> buf(size);
    if (GetExtendedUdpTable(buf.data(), &size, FALSE, family,
            UDP_TABLE_OWNER_PID, 0) != NO_ERROR) return;

    if (family == AF_INET) {
        auto* table = (MIB_UDPTABLE_OWNER_PID*)buf.data();
        for (DWORD i = 0; i < table->dwNumEntries; i++) {
            DWORD pid = table->table[i].dwOwningPid;
            if (pid == 0) continue;
            auto& pi = agg[pid];
            pi.pid = pid; pi.udpConnections++;
        }
    } else {
        auto* table = (MIB_UDP6TABLE_OWNER_PID*)buf.data();
        for (DWORD i = 0; i < table->dwNumEntries; i++) {
            DWORD pid = table->table[i].dwOwningPid;
            if (pid == 0) continue;
            auto& pi = agg[pid];
            pi.pid = pid; pi.udpConnections++;
        }
    }
}

MEMCORE_API int GetPerProcessNetStats(ProcNetInfoNative* buffer, int bufferCount)
{
    if (!buffer || bufferCount <= 0) return 0;

    std::lock_guard<std::mutex> lock(g_netMutex);

    ULONGLONG now = GetTickCount64();
    double elapsed = (g_netLastSampleMs == 0) ? 1.0
                                               : (now - g_netLastSampleMs) / 1000.0;
    if (elapsed < 0.05) elapsed = 0.05;

    std::unordered_map<DWORD, ProcNetInfoNative> agg;
    agg.reserve(128);

    AccumulateV4(agg, now);
    AccumulateV6(agg, now);
    AccumulateUdp(agg, AF_INET);
    AccumulateUdp(agg, AF_INET6);

    // Expire closed connections (not seen for >10s).
    auto expire = [now](auto& m) {
        for (auto it = m.begin(); it != m.end(); ) {
            if (now - it->second.lastSeenMs > 10000ULL) it = m.erase(it);
            else ++it;
        }
    };
    expire(g_v4Conns);
    expire(g_v6Conns);

    int count = 0;
    for (auto& kv : agg) {
        if (count >= bufferCount) break;
        ProcNetInfoNative& out = buffer[count];
        out = kv.second;
        out.pid = kv.first;
        out.bytesInPerSec  = (double)out.bytesIn  / elapsed;
        out.bytesOutPerSec = (double)out.bytesOut / elapsed;
        count++;
    }
    g_netLastSampleMs = now;
    return count;
}
