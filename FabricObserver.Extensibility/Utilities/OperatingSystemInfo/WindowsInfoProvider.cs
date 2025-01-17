﻿// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Fabric;
using System.IO;
using System.Linq;
using System.Management;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace FabricObserver.Observers.Utilities
{
    public class WindowsInfoProvider : OSInfoProvider
    {
        private const string TcpProtocol = "tcp";

        public override (int LowPort, int HighPort) TupleGetDynamicPortRange()
        {
            using (var p = new Process())
            {
                try
                {
                    var ps = new ProcessStartInfo
                    {
                        Arguments = $"/c netsh int ipv4 show dynamicportrange {TcpProtocol} | find /i \"port\"",
                        FileName = $"{Environment.GetFolderPath(Environment.SpecialFolder.System)}\\cmd.exe",
                        UseShellExecute = false,
                        WindowStyle = ProcessWindowStyle.Hidden,
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true
                    };

                    p.StartInfo = ps;
                    _ = p.Start();

                    var stdOutput = p.StandardOutput;
                    string output = stdOutput.ReadToEnd();
                    Match match = Regex.Match(
                                        output,
                                        @"Start Port\s+:\s+(?<startPort>\d+).+?Number of Ports\s+:\s+(?<numberOfPorts>\d+)",
                                        RegexOptions.Singleline | RegexOptions.IgnoreCase);

                    p.WaitForExit();

                    string startPort = match.Groups["startPort"].Value;
                    string portCount = match.Groups["numberOfPorts"].Value;
                    int exitStatus = p.ExitCode;
                    stdOutput.Close();

                    if (exitStatus != 0)
                    {
                        return (-1, -1);
                    }

                    int lowPortRange = int.Parse(startPort);
                    int highPortRange = lowPortRange + int.Parse(portCount);

                    return (lowPortRange, highPortRange);
                }
                catch (Exception e) when (
                                e is ArgumentException
                                || e is IOException
                                || e is InvalidOperationException
                                || e is RegexMatchTimeoutException
                                || e is Win32Exception)
                {

                }
            }

            return (-1, -1);
        }

        /// <summary>
        /// Compute count of active TCP ports in dynamic range.
        /// </summary>
        /// <param name="processId">Optional: If supplied, then return the number of ephemeral ports in use by the process.</param>
        /// <param name="context">Optional (this is used by Linux callers only - see LinuxInfoProvider.cs): 
        /// If supplied, will use the ServiceContext to find the Linux Capabilities binary to run this command.</param>
        /// <returns>number of active Epehemeral TCP ports as int value</returns>
        public override int GetActiveEphemeralPortCount(int processId = -1, ServiceContext context = null)
        {
            int count;

            try
            {
                count = Retry.Do(() => GetTcpPortCount(processId, true), TimeSpan.FromSeconds(3), CancellationToken.None);
            }
            catch (AggregateException ae)
            {
                Logger.LogWarning($"Retry failed for GetActiveEphemeralPortCount:{Environment.NewLine}{ae}");
                count = -1;
            }

            return count;
        }

        /// <summary>
        /// Compute count of active TCP ports.
        /// </summary>
        /// <param name="processId">Optional: If supplied, then return the number of tcp ports in use by the process.</param>
        /// <param name="context">Optional (this is used by Linux callers only - see LinuxInfoProvider.cs): 
        /// If supplied, will use the ServiceContext to find the Linux Capabilities binary to run this command.</param>
        /// <returns>number of active TCP ports as int value</returns>
        public override int GetActiveTcpPortCount(int processId = -1, ServiceContext context = null)
        {
            int count;

            try
            {
                count = Retry.Do(() => GetTcpPortCount(processId), TimeSpan.FromSeconds(3), CancellationToken.None);
            }
            catch (AggregateException ae)
            {
                Logger.LogWarning($"Retry failed for GetActivePortCount:{Environment.NewLine}{ae}");
                count = -1;
            }

            return count;
        }

        public override Task<OSInfo> GetOSInfoAsync(CancellationToken cancellationToken)
        {
            ManagementObjectSearcher win32OsInfo = null;
            ManagementObjectCollection results = null;
            OSInfo osInfo = default;

            try
            {
                win32OsInfo = new ManagementObjectSearcher(
                                    "SELECT Caption,Version,Status,OSLanguage,NumberOfProcesses,FreePhysicalMemory,FreeVirtualMemory," +
                                    "TotalVirtualMemorySize,TotalVisibleMemorySize,InstallDate,LastBootUpTime FROM Win32_OperatingSystem");

                results = win32OsInfo.Get();

                using (ManagementObjectCollection.ManagementObjectEnumerator enumerator = results.GetEnumerator())
                {
                    while (enumerator.MoveNext())
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        try
                        {
                            using (ManagementObject mObj = (ManagementObject)enumerator.Current)
                            {
                                object captionObj = mObj.Properties["Caption"].Value;
                                object versionObj = mObj.Properties["Version"].Value;
                                object statusObj = mObj.Properties["Status"].Value;
                                object osLanguageObj = mObj.Properties["OSLanguage"].Value;
                                object numProcsObj = mObj.Properties["NumberOfProcesses"].Value;
                                object freePhysicalObj = mObj.Properties["FreePhysicalMemory"].Value;
                                object freeVirtualTotalObj = mObj.Properties["FreeVirtualMemory"].Value;
                                object totalVirtualObj = mObj.Properties["TotalVirtualMemorySize"].Value;
                                object totalVisibleObj = mObj.Properties["TotalVisibleMemorySize"].Value;
                                object installDateObj = mObj.Properties["InstallDate"].Value;
                                object lastBootDateObj = mObj.Properties["LastBootUpTime"].Value;

                                osInfo.Name = captionObj?.ToString();

                                if (int.TryParse(numProcsObj?.ToString(), out int numProcesses))
                                {
                                    osInfo.NumberOfProcesses = numProcesses;
                                }
                                else
                                {
                                    osInfo.NumberOfProcesses = -1;
                                }

                                osInfo.Status = statusObj?.ToString();
                                osInfo.Language = osLanguageObj?.ToString();
                                osInfo.Version = versionObj?.ToString();
                                osInfo.InstallDate = ManagementDateTimeConverter.ToDateTime(installDateObj?.ToString()).ToUniversalTime().ToString("o");
                                osInfo.LastBootUpTime = ManagementDateTimeConverter.ToDateTime(lastBootDateObj?.ToString()).ToUniversalTime().ToString("o");
                                osInfo.FreePhysicalMemoryKB = ulong.TryParse(freePhysicalObj?.ToString(), out ulong freePhysical) ? freePhysical : 0;
                                osInfo.FreeVirtualMemoryKB = ulong.TryParse(freeVirtualTotalObj?.ToString(), out ulong freeVirtual) ? freeVirtual : 0;
                                osInfo.TotalVirtualMemorySizeKB = ulong.TryParse(totalVirtualObj?.ToString(), out ulong totalVirtual) ? totalVirtual : 0;
                                osInfo.TotalVisibleMemorySizeKB = ulong.TryParse(totalVisibleObj?.ToString(), out ulong totalVisible) ? totalVisible : 0;
                            }
                        }
                        catch (ManagementException me)
                        {
                            Logger.LogInfo($"Handled ManagementException in GetOSInfoAsync retrieval:{Environment.NewLine}{me}");
                        }
                        catch (Exception e)
                        {
                            Logger.LogInfo($"Bug? => Exception in GetOSInfoAsync:{Environment.NewLine}{e}");
                        }
                    }
                }
            }
            finally
            {
                results?.Dispose();
                results = null;
                win32OsInfo?.Dispose();
                win32OsInfo = null;
            }

            return Task.FromResult(osInfo);
        }

        // Not implemented. No Windows support.
        public override int GetMaximumConfiguredFileHandlesCount()
        {
            return -1;
        }

        // Not implemented. No Windows support.
        public override int GetTotalAllocatedFileHandlesCount()
        {
            return -1;
        }

        private int GetTcpPortCount(int processId = -1, bool ephemeral = false)
        {
            var tempLocalPortData = new List<(int Pid, int Port)>();
            string findStrProc = string.Empty;
            string error = string.Empty;
            (int lowPortRange, int highPortRange) = (-1, -1);

            if (ephemeral)
            {
                (lowPortRange, highPortRange) = TupleGetDynamicPortRange();
            }

            if (processId > 0)
            {
                findStrProc = $" | find \"{processId}\"";
            }

            using (var p = new Process())
            {
                var ps = new ProcessStartInfo
                {
                    Arguments = $"/c netstat -qno -p {TcpProtocol}{findStrProc}",
                    FileName = $"{Environment.GetFolderPath(Environment.SpecialFolder.System)}\\cmd.exe",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                // Capture any error information from netstat.
                p.ErrorDataReceived += (sender, e) => { error += e.Data; };
                p.StartInfo = ps;
                _ = p.Start();
                var stdOutput = p.StandardOutput;

                // Start asynchronous read operation on error stream.  
                p.BeginErrorReadLine();

                string portRow;
                while ((portRow = stdOutput.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(portRow))
                    {
                        continue;
                    }

                    (int localPort, int pid) = TupleGetLocalPortPidPairFromNetStatString(portRow);

                    if (localPort == -1 || pid == -1)
                    {
                        continue;
                    }

                    if (processId > 0)
                    {
                        if (processId != pid)
                        {
                            continue;
                        }

                        // Only add unique pid (if supplied in call) and local port data to list.
                        if (tempLocalPortData.Any(t => t.Pid == pid && t.Port == localPort))
                        {
                            continue;
                        }
                    }
                    else
                    {
                        if (tempLocalPortData.Any(t => t.Port == localPort))
                        {
                            continue;
                        }
                    }

                    // Ephemeral ports query?
                    if (ephemeral && (localPort < lowPortRange || localPort > highPortRange))
                    {
                        continue;
                    }

                    tempLocalPortData.Add((pid, localPort));
                }

                p.WaitForExit();

                int exitStatus = p.ExitCode;
                int count = tempLocalPortData.Count;
                tempLocalPortData.Clear();
                stdOutput.Close();

                if (exitStatus == 0)
                {
                    return count;
                }

                // find will exit with a non-zero exit code if it doesn't find any matches in the case where a pid was supplied.
                // Do not throw in this case. 0 is the right answer.
                if (processId > 0 && error == string.Empty)
                {
                    return 0;
                }

                // there was an error associated with the non-zero exit code. Log it and throw.
                string msg = $"netstat -qno -p {TcpProtocol}{findStrProc} exited with {exitStatus}: {error}";
                Logger.LogWarning(msg);

                // this will be handled by Retry.Do().
                throw new Exception(msg);
            }
        }

        /// <summary>
        /// Gets local port number and associated process ID from netstat standard output line.
        /// </summary>
        /// <param name="netstatOutputLine">Single line (row) of text from netstat output.</param>
        /// <returns>Integer Tuple: (port, pid)</returns>
        private static (int, int) TupleGetLocalPortPidPairFromNetStatString(string netstatOutputLine)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(netstatOutputLine))
                {
                    return (-1, -1);
                }

                string[] stats = netstatOutputLine.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

                if (stats.Length != 5 || !int.TryParse(stats[4], out int pid))
                {
                    return (-1, -1);
                }

                string localIpAndPort = stats[1];

                if (string.IsNullOrWhiteSpace(localIpAndPort) || !localIpAndPort.Contains(":"))
                {
                    return (-1, -1);
                }

                // We *only* care about the local IP.
                string localPort = localIpAndPort.Split(':')[1];

                if (!int.TryParse(localPort, out int port))
                {
                    return (-1, -1);
                }

                return (port, pid);
            }
            catch (Exception e) when (e is ArgumentException || e is FormatException)
            {
                return (-1, -1);
            }
        }

        public override (long TotalMemoryGb, long MemoryInUseMb, double PercentInUse) TupleGetSystemMemoryInfo()
        {
            try
            {
                NativeMethods.MEMORYSTATUSEX memoryInfo = NativeMethods.GetSystemMemoryInfo();
                ulong totalMemoryBytes = memoryInfo.ullTotalPhys;
                ulong availableMemoryBytes = memoryInfo.ullAvailPhys;
                ulong inUse = totalMemoryBytes - availableMemoryBytes;
                float used = (float)inUse / totalMemoryBytes;
                float usedPct = used * 100;

                return ((long)totalMemoryBytes / 1024 / 1024 / 1024, (long)inUse / 1024 / 1024, usedPct);
            }
            catch (Win32Exception we)
            {
                Logger.LogWarning($"TupleGetMemoryInfo: Failure (native) computing memory data:{Environment.NewLine}{we}");
            }

            return (0, 0, 0);
        }
    }
}
