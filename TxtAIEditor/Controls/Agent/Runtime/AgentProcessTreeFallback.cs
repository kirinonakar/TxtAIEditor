using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace TxtAIEditor.Controls
{
    // Used only when the launcher could not be placed in a Job Object. Held handles let
    // cleanup follow known descendants even after their launcher (or parent) exits.
    internal sealed class AgentProcessTreeFallback(Process launcher) : IDisposable
    {
        private const uint SnapshotProcesses = 0x00000002;
        private const uint TrackAndTerminateAccess = 0x00101001; // SYNCHRONIZE | QUERY_LIMITED_INFORMATION | TERMINATE
        private const uint ProcessSignaled = 0;
        private const uint ProcessStillRunning = 258;
        private const uint ErrorNoMoreFiles = 18;
        private const int MaxTrackedDescendants = 1024;

        private readonly uint _launcherId = (uint)launcher.Id;
        private readonly SafeProcessHandle _launcherHandle = launcher.SafeHandle;
        private readonly Dictionary<uint, SafeProcessHandle> _descendants = new();
        private bool _trackingIncomplete;

        public void CaptureDescendants()
        {
            if (_descendants.Count >= MaxTrackedDescendants)
            {
                _trackingIncomplete = true;
                return;
            }

            long snapshotStarted = DateTime.UtcNow.ToFileTimeUtc();
            using SafeFileHandle snapshot = CreateToolhelp32Snapshot(SnapshotProcesses, 0);
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
            if (snapshot.IsInvalid || !Process32FirstW(snapshot, ref entry))
            {
                _trackingIncomplete = true;
                Trace.TraceWarning("Agent process snapshot failed (Win32 {0}).", Marshal.GetLastWin32Error());
                return;
            }

            var childrenByParent = new Dictionary<uint, List<uint>>();
            do
            {
                if (!childrenByParent.TryGetValue(entry.ParentId, out List<uint>? children))
                {
                    childrenByParent.Add(entry.ParentId, children = new List<uint>());
                }
                children.Add(entry.ProcessId);
            } while (Process32NextW(snapshot, ref entry));
            if ((uint)Marshal.GetLastWin32Error() != ErrorNoMoreFiles)
            {
                _trackingIncomplete = true;
            }

            var parents = new Queue<KeyValuePair<uint, SafeProcessHandle>>(_descendants);
            parents.Enqueue(new(_launcherId, _launcherHandle));
            while (parents.TryDequeue(out var parent))
            {
                if (!childrenByParent.TryGetValue(parent.Key, out List<uint>? children))
                {
                    continue;
                }
                if (!TryGetLifetime(parent.Value, out long parentStart, out long parentEnd))
                {
                    _trackingIncomplete = true;
                    continue;
                }
                foreach (uint childId in children)
                {
                    if (childId == _launcherId || _descendants.ContainsKey(childId))
                    {
                        continue;
                    }
                    if (_descendants.Count >= MaxTrackedDescendants)
                    {
                        _trackingIncomplete = true;
                        return;
                    }

                    SafeProcessHandle child = OpenProcess(TrackAndTerminateAccess, false, childId);
                    // A PID from the snapshot may have exited or been reused before OpenProcess.
                    if (child.IsInvalid || !GetProcessTimes(child, out long childStart, out _, out _, out _))
                    {
                        child.Dispose();
                        _trackingIncomplete = true;
                        continue;
                    }
                    if (childStart < parentStart || childStart > parentEnd || childStart > snapshotStarted)
                    {
                        child.Dispose();
                        continue;
                    }

                    _descendants.Add(childId, child);
                    parents.Enqueue(new(childId, child));
                }
            }
        }

        public bool Terminate()
        {
            CaptureDescendants();
            bool terminated = TryTerminate(_launcherHandle);
            var terminationRequested = new HashSet<uint>();
            foreach (var (id, descendant) in _descendants)
            {
                if (TryTerminate(descendant))
                {
                    terminationRequested.Add(id);
                }
            }

            // Also look for children that started immediately before termination was requested.
            CaptureDescendants();
            foreach (var (id, descendant) in _descendants)
            {
                if (!terminationRequested.Contains(id))
                {
                    terminated &= TryTerminate(descendant);
                }
            }
            return terminated && !_trackingIncomplete;
        }

        private static bool TryGetLifetime(SafeProcessHandle process, out long start, out long end)
        {
            uint status = WaitForSingleObject(process, 0);
            bool valid = GetProcessTimes(process, out start, out long exit, out _, out _);
            end = status == ProcessSignaled ? exit : long.MaxValue;
            return valid && status is ProcessSignaled or ProcessStillRunning;
        }

        private static bool TryTerminate(SafeProcessHandle process) =>
            WaitForSingleObject(process, 0) == ProcessSignaled ||
            TerminateProcess(process, 1) ||
            WaitForSingleObject(process, 0) == ProcessSignaled;

        public void Dispose()
        {
            foreach (SafeProcessHandle descendant in _descendants.Values)
            {
                descendant.Dispose();
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint processId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool Process32FirstW(SafeFileHandle snapshot, ref ProcessEntry entry);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool Process32NextW(SafeFileHandle snapshot, ref ProcessEntry entry);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern SafeProcessHandle OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ProcessEntry
        {
            public uint Size;
            public uint Usage;
            public uint ProcessId;
            public UIntPtr DefaultHeapId;
            public uint ModuleId;
            public uint Threads;
            public uint ParentId;
            public int BasePriority;
            public uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string ExecutableName;
        }
    }
}
