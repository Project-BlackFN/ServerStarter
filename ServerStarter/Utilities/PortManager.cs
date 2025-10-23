using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace ServerStarter.Utilities
{
    public class PortManager
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct PROCESSENTRY32
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeFile;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MIB_TCPROW_OWNER_PID
        {
            public uint dwState;
            public uint dwLocalAddr;
            public uint dwLocalPort;
            public uint dwRemoteAddr;
            public uint dwRemotePort;
            public uint dwOwningPid;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MIB_TCPTABLE_OWNER_PID
        {
            public uint dwNumEntries;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint GetExtendedTcpTable(
            IntPtr pTcpTable,
            ref uint pdwSize,
            bool bOrder,
            uint ulAf,
            uint TableClass,
            uint Reserved);

        private const uint TH32CS_SNAPPROCESS = 0x00000002;
        private const uint PROCESS_TERMINATE = 0x0001;
        private const int INVALID_HANDLE_VALUE = -1;
        private const uint AF_INET = 2;
        private const uint TCP_TABLE_OWNER_PID_LISTENER = 3;
        private const uint TCP_TABLE_OWNER_PID_ALL = 5;
        private const uint MIB_TCP_STATE_LISTEN = 2;
        private const uint ERROR_INSUFFICIENT_BUFFER = 122;
        private const uint NO_ERROR = 0;

        public static Task<Dictionary<string, object>> IsPortFreeAsync(int port)
        {
            return Task.Run(() => IsPortFree(port));
        }

        public static Dictionary<string, object> IsPortFree(int port)
        {
            try
            {
                var listener = new TcpListener(IPAddress.Any, port);
                listener.Start();
                listener.Stop();
                return new Dictionary<string, object> { { "success", true } };
            }
            catch (Exception ex)
            {
                return new Dictionary<string, object> { { "success", false }, { "error", ex.Message } };
            }
        }

        public static Task<Dictionary<string, object>> FreePortAsync(int port)
        {
            return Task.Run(() => FreePort(port));
        }

        public static Dictionary<string, object> FreePort(int port)
        {
            try
            {
                var pid = FindProcessByPort(port);

                if (!pid.HasValue)
                {
                    return new Dictionary<string, object> { { "success", false }, { "error", "No process found on port" } };
                }

                var killed = TerminateProcessByPID(pid.Value);
                if (killed)
                {
                    return new Dictionary<string, object> { { "success", true }, { "pid", pid.Value } };
                }

                return new Dictionary<string, object> { { "success", false }, { "error", "Failed to terminate process" } };
            }
            catch (Exception ex)
            {
                return new Dictionary<string, object> { { "success", false }, { "error", ex.Message } };
            }
        }

        private static uint? FindProcessByPort(int port)
        {
            IntPtr pTcpTable = IntPtr.Zero;

            try
            {
                uint size = 0;
                var result = GetExtendedTcpTable(IntPtr.Zero, ref size, true, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);

                if (result != ERROR_INSUFFICIENT_BUFFER)
                {
                    return null;
                }

                pTcpTable = Marshal.AllocHGlobal((int)size);
                result = GetExtendedTcpTable(pTcpTable, ref size, true, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);

                if (result != NO_ERROR)
                {
                    return null;
                }

                var table = Marshal.PtrToStructure<MIB_TCPTABLE_OWNER_PID>(pTcpTable);
                var numEntries = table.dwNumEntries;
                var rowSize = Marshal.SizeOf<MIB_TCPROW_OWNER_PID>();
                var firstRowOffset = Marshal.SizeOf<uint>();

                for (int i = 0; i < numEntries; i++)
                {
                    IntPtr rowPtr = IntPtr.Add(pTcpTable, firstRowOffset + (i * rowSize));
                    var row = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(rowPtr);

                    uint localPort = ((row.dwLocalPort & 0xFF) << 8) | ((row.dwLocalPort >> 8) & 0xFF);

                    if (localPort == port && row.dwState == MIB_TCP_STATE_LISTEN)
                    {
                        return row.dwOwningPid;
                    }
                }

                return null;
            }
            catch
            {
                return null;
            }
            finally
            {
                if (pTcpTable != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(pTcpTable);
                }
            }
        }

        private static bool TerminateProcessByPID(uint pid)
        {
            IntPtr hProcess = IntPtr.Zero;

            try
            {
                hProcess = OpenProcess(PROCESS_TERMINATE, false, pid);

                if (hProcess == IntPtr.Zero || hProcess.ToInt32() == INVALID_HANDLE_VALUE)
                {
                    return false;
                }

                return TerminateProcess(hProcess, 1);
            }
            catch
            {
                return false;
            }
            finally
            {
                if (hProcess != IntPtr.Zero && hProcess.ToInt32() != INVALID_HANDLE_VALUE)
                {
                    CloseHandle(hProcess);
                }
            }
        }

        public static Dictionary<string, object> GetPortInfo(int port)
        {
            try
            {
                var pid = FindProcessByPort(port);

                if (!pid.HasValue)
                {
                    return new Dictionary<string, object>
                    {
                        { "success", true },
                        { "inUse", false },
                        { "port", port }
                    };
                }

                return new Dictionary<string, object>
                {
                    { "success", true },
                    { "inUse", true },
                    { "port", port },
                    { "pid", pid.Value }
                };
            }
            catch (Exception ex)
            {
                return new Dictionary<string, object> { { "success", false }, { "error", ex.Message } };
            }
        }
    }
}