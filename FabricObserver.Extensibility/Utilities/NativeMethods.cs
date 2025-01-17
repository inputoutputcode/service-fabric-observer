﻿// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;

namespace FabricObserver.Observers.Utilities
{
    [SuppressUnmanagedCodeSecurity]
    public static class NativeMethods
    {
        // Process dump support.
        [DllImport(
            "dbghelp.dll",
            EntryPoint = "MiniDumpWriteDump",
            CallingConvention = CallingConvention.StdCall,
            CharSet = CharSet.Unicode,
            ExactSpelling = true,
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool MiniDumpWriteDump(
                                          IntPtr hProcess,
                                          uint processId,
                                          SafeHandle hFile,
                                          MINIDUMP_TYPE dumpType,
                                          IntPtr expParam,
                                          IntPtr userStreamParam,
                                          IntPtr callbackParam);

        [Flags]
        public enum MINIDUMP_TYPE
        {
            MiniDumpNormal = 0x00000000,
            MiniDumpWithDataSegs = 0x00000001,
            MiniDumpWithFullMemory = 0x00000002,
            MiniDumpWithHandleData = 0x00000004,
            MiniDumpFilterMemory = 0x00000008,
            MiniDumpScanMemory = 0x00000010,
            MiniDumpWithUnloadedModules = 0x00000020,
            MiniDumpWithIndirectlyReferencedMemory = 0x00000040,
            MiniDumpFilterModulePaths = 0x00000080,
            MiniDumpWithProcessThreadData = 0x00000100,
            MiniDumpWithPrivateReadWriteMemory = 0x00000200,
            MiniDumpWithoutOptionalData = 0x00000400,
            MiniDumpWithFullMemoryInfo = 0x00000800,
            MiniDumpWithThreadInfo = 0x00001000,
            MiniDumpWithCodeSegs = 0x00002000,
            MiniDumpWithoutAuxiliaryState = 0x00004000,
            MiniDumpWithFullAuxiliaryState = 0x00008000,
            MiniDumpWithPrivateWriteCopyMemory = 0x00010000,
            MiniDumpIgnoreInaccessibleMemory = 0x00020000,
            MiniDumpWithTokenInformation = 0x00040000,
            MiniDumpWithModuleHeaders = 0x00080000,
            MiniDumpFilterTriage = 0x00100000,
            MiniDumpValidTypeFlags = 0x001fffff
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct PROCESS_MEMORY_COUNTERS_EX
        {
            internal uint cb;
            internal uint PageFaultCount;
            internal IntPtr PeakWorkingSetSize;
            internal IntPtr WorkingSetSize;
            internal IntPtr QuotaPeakPagedPoolUsage;
            internal IntPtr QuotaPagedPoolUsage;
            internal IntPtr QuotaPeakNonPagedPoolUsage;
            internal IntPtr QuotaNonPagedPoolUsage;
            internal IntPtr PagefileUsage;
            internal IntPtr PeakPagefileUsage;
            internal IntPtr PrivateUsage;
        }

        [DllImport("psapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetProcessMemoryInfo(IntPtr hProcess, [Out] out PROCESS_MEMORY_COUNTERS_EX counters, [In] uint size);
    
        //inner enum used only internally
        [Flags]
        private enum SnapshotFlags : uint
        {
            HeapList = 0x00000001,
            Process = 0x00000002,
            Thread = 0x00000004,
            Module = 0x00000008,
            Module32 = 0x00000010,
            Inherit = 0x80000000,
            All = 0x0000001F,
            NoHeaps = 0x40000000
        }
        //inner struct used only internally
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct PROCESSENTRY32
        {
            const int MAX_PATH = 260;
            internal uint dwSize;
            internal uint cntUsage;
            internal uint th32ProcessID;
            internal IntPtr th32DefaultHeapID;
            internal uint th32ModuleID;
            internal uint cntThreads;
            internal uint th32ParentProcessID;
            internal int pcPriClassBase;
            internal uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MAX_PATH)]
            internal string szExeFile;
        }

        [DllImport("kernel32", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        static extern IntPtr CreateToolhelp32Snapshot([In] uint dwFlags, [In] uint th32ProcessID);

        [DllImport("kernel32", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        static extern bool Process32First([In] IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Auto)]
        static extern bool Process32Next([In] IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

        [DllImport("kernel32", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle([In] IntPtr hObject);

        /// <summary>
        /// Gets the supplied pid's child processes, if any.
        /// </summary>
        /// <param name="parentpid">pid of parent process</param>
        /// <returns>A List of Process objects.</returns>
        /// <exception cref="Win32Exception">Callers should handle this exception. 
        /// It will generally be thrown when the target process (with parentpid) no longer exists.</exception>
        public static List<Process> GetChildProcesses(int parentpid)
        {
            List<Process> childProcs = new List<Process>();
            IntPtr handleToSnapshot = IntPtr.Zero;

            try
            {
                PROCESSENTRY32 procEntry = new PROCESSENTRY32
                {
                    dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32))
                };

                handleToSnapshot = CreateToolhelp32Snapshot((uint)SnapshotFlags.Process, 0);

                if (Process32First(handleToSnapshot, ref procEntry))
                {
                    do
                    {
                        if (parentpid == procEntry.th32ParentProcessID)
                        {
                            childProcs.Add(Process.GetProcessById((int)procEntry.th32ProcessID));
                        }

                    } while (Process32Next(handleToSnapshot, ref procEntry));
                }
                else
                {
                    throw new Win32Exception(string.Format("Failed with win32 error code {0}", Marshal.GetLastWin32Error()));
                }
            }
            catch (Win32Exception)
            {
                // If Process32First fails, it generally means the parent process (with parentpid) no longer exists..
                throw;
            }
            finally
            {
                CloseHandle(handleToSnapshot);
            }

            return childProcs;
        }

        /// <summary>
        /// Get the number of execution threads started by the process with supplied pid.
        /// </summary>
        /// <param name="procId">The id of the process (pid).</param>
        /// <returns>The number of execution threads started by the process.</returns>
        /// <exception cref="Win32Exception">Callers should handle this exception. 
        /// It will generally be thrown when the target process (with parentpid) no longer exists.</exception>
        public static int GetProcessThreadCount(int procId)
        {
            IntPtr handleToSnapshot = IntPtr.Zero;
            int threadCount = 0;

            try
            {
                PROCESSENTRY32 procEntry = new PROCESSENTRY32
                {
                    dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32))
                };

                handleToSnapshot = CreateToolhelp32Snapshot((uint)SnapshotFlags.Process, 0);

                if (Process32First(handleToSnapshot, ref procEntry))
                {
                    do
                    {
                        if (procId == procEntry.th32ProcessID)
                        {
                            threadCount = (int)procEntry.cntThreads;
                            break;
                        }

                    } while (Process32Next(handleToSnapshot, ref procEntry));
                }
                else
                {
                    throw new Win32Exception(string.Format("Failed with win32 error code {0}", Marshal.GetLastWin32Error()));
                }
            }
            catch (Win32Exception)
            {
                // If Process32First fails, it generally means the process (with id procId) no longer exists..
                throw;
            }
            finally
            {
                CloseHandle(handleToSnapshot);
            }

            return threadCount;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        public class MEMORYSTATUSEX
        {
            /// <summary>
            /// Size of the structure, in bytes. You must set this member before calling GlobalMemoryStatusEx.
            /// </summary>
            public uint dwLength;

            /// <summary>
            /// Number between 0 and 100 that specifies the approximate percentage of physical memory that is in use (0 indicates no memory use and 100 indicates full memory use).
            /// </summary>
            public uint dwMemoryLoad;

            /// <summary>
            /// Total size of physical memory, in bytes.
            /// </summary>
            public ulong ullTotalPhys;

            /// <summary>
            /// Size of physical memory available, in bytes.
            /// </summary>
            public ulong ullAvailPhys;

            /// <summary>
            /// Size of the committed memory limit, in bytes. This is physical memory plus the size of the page file, minus a small overhead.
            /// </summary>
            public ulong ullTotalPageFile;

            /// <summary>
            /// Size of available memory to commit, in bytes. The limit is ullTotalPageFile.
            /// </summary>
            public ulong ullAvailPageFile;

            /// <summary>
            /// Total size of the user mode portion of the virtual address space of the calling process, in bytes.
            /// </summary>
            public ulong ullTotalVirtual;

            /// <summary>
            /// Size of unreserved and uncommitted memory in the user mode portion of the virtual address space of the calling process, in bytes.
            /// </summary>
            public ulong ullAvailVirtual;

            /// <summary>
            /// Size of unreserved and uncommitted memory in the extended portion of the virtual address space of the calling process, in bytes.
            /// </summary>
            public ulong ullAvailExtendedVirtual;

            /// <summary>
            /// Initializes a new instance of the <see cref="T:MEMORYSTATUSEX"/> class.
            /// </summary>
            public MEMORYSTATUSEX()
            {
                this.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            }
        }

        [return: MarshalAs(UnmanagedType.Bool)]
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

        public static MEMORYSTATUSEX GetSystemMemoryInfo()
        {
            MEMORYSTATUSEX memory = new MEMORYSTATUSEX();
            
            if (GlobalMemoryStatusEx(memory))
            {
                return memory;
            }
            else
            {
                throw new Win32Exception(string.Format("NativeMethods.GetSystemMemoryInfo Failed with win32 error code {0}", Marshal.GetLastWin32Error()));
            }
        }
    }
}
