using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NetPulse.Core;
using static NetPulse.Core.Lang;

namespace NetPulse.Platform;

/// <summary>
/// Real-time ETW session on the Microsoft-Windows-Kernel-Network provider, which reports every TCP/UDP
/// send and receive with the owning process id and payload size. Requires administrator rights
/// (or membership in "Performance Log Users"). Events are folded into a <see cref="ProcessTrafficTable"/>.
/// </summary>
internal sealed unsafe class KernelNetworkTrace : IDisposable
{
    public static readonly string SessionName = $"NetPulse{AppPaths.ProfileSuffix}-KernelNetwork";

    private const uint ERROR_SUCCESS = 0;
    private const uint ERROR_ACCESS_DENIED = 5;
    private const uint ERROR_ALREADY_EXISTS = 183;
    private const uint WNODE_FLAG_TRACED_GUID = 0x00020000;
    private const uint EVENT_TRACE_REAL_TIME_MODE = 0x00000100;
    private const uint PROCESS_TRACE_MODE_REAL_TIME = 0x00000100;
    private const uint PROCESS_TRACE_MODE_EVENT_RECORD = 0x10000000;
    private const uint EVENT_TRACE_CONTROL_STOP = 1;
    private const uint EVENT_CONTROL_CODE_ENABLE_PROVIDER = 1;
    private const byte TRACE_LEVEL_VERBOSE = 5;
    private const ulong KeywordIpv4 = 0x10;
    private const ulong KeywordIpv6 = 0x20;
    private const int NameBufferChars = 1024;

    private static readonly Guid KernelNetworkProvider = new("7dd42a49-5329-4832-8dfd-43d979153a88");

    private readonly ProcessTrafficTable _table;
    private ulong _sessionHandle;
    private ulong _traceHandle = InvalidTraceHandle;
    private GCHandle _self;
    private Thread? _thread;

    private KernelNetworkTrace(ProcessTrafficTable table) => _table = table;

    private long _eventCount;

    public bool IsRunning => _thread is { IsAlive: true };

    /// <summary>Events the session received; lets the UI tell "no traffic yet" from "nothing arrives".</summary>
    public long EventCount => Interlocked.Read(ref _eventCount);

    private static ulong InvalidTraceHandle => nint.Size == 8 ? ulong.MaxValue : uint.MaxValue;

    /// <summary>Starts tracing, or throws <see cref="UnauthorizedAccessException"/> without admin rights.</summary>
    public static KernelNetworkTrace Start(ProcessTrafficTable table)
    {
        VerifyStructLayouts();
        var trace = new KernelNetworkTrace(table);
        try
        {
            trace.StartSession();
            trace.StartConsumer();
            return trace;
        }
        catch
        {
            trace.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_sessionHandle != 0)
        {
            // Stopping the session makes ProcessTrace return on the consumer thread.
            StopSessionByName();
            _sessionHandle = 0;
        }

        if (_traceHandle != InvalidTraceHandle)
        {
            _ = CloseTrace(_traceHandle);
            _traceHandle = InvalidTraceHandle;
        }

        _thread?.Join(TimeSpan.FromSeconds(3));
        _thread = null;
        if (_self.IsAllocated)
        {
            _self.Free();
        }
    }

    /// <summary>
    /// Wrong struct sizes would make ETW read or write the wrong memory, so refuse to run instead.
    /// The expected values are sizeof() of the Windows SDK definitions for this process bitness.
    /// </summary>
    private static void VerifyStructLayouts()
    {
        var x64 = nint.Size == 8;
        Check(sizeof(EventTraceProperties), 120, nameof(EventTraceProperties));
        Check(sizeof(EventTraceLogfile), x64 ? 448 : 416, nameof(EventTraceLogfile));
        Check(sizeof(EventRecord), x64 ? 112 : 104, nameof(EventRecord));

        static void Check(int actual, int expected, string name)
        {
            if (actual != expected)
            {
                throw new PlatformNotSupportedException($"{name} is {actual} bytes, expected {expected}.");
            }
        }
    }

    private void StartSession()
    {
        var size = sizeof(EventTraceProperties) + (NameBufferChars * 2 * sizeof(char));
        var buffer = stackalloc byte[size];
        var properties = InitProperties(buffer, size);

        var status = StartTraceW(out _sessionHandle, SessionName, properties);
        if (status == ERROR_ALREADY_EXISTS)
        {
            // Left over from a crashed run: stop it and start fresh.
            StopSessionByName();
            properties = InitProperties(buffer, size);
            status = StartTraceW(out _sessionHandle, SessionName, properties);
        }

        ThrowOnError(status, "StartTrace");

        var provider = KernelNetworkProvider;
        status = EnableTraceEx2(
            _sessionHandle, &provider, EVENT_CONTROL_CODE_ENABLE_PROVIDER, TRACE_LEVEL_VERBOSE,
            KeywordIpv4 | KeywordIpv6, 0, 0, null);
        ThrowOnError(status, "EnableTraceEx2");
    }

    private void StartConsumer()
    {
        _self = GCHandle.Alloc(this);
        fixed (char* name = SessionName)
        {
            var logfile = default(EventTraceLogfile);
            logfile.LoggerName = name;
            logfile.ProcessTraceMode = PROCESS_TRACE_MODE_REAL_TIME | PROCESS_TRACE_MODE_EVENT_RECORD;
            logfile.EventRecordCallback = &OnEventRecord;
            logfile.Context = GCHandle.ToIntPtr(_self);
            _traceHandle = OpenTraceW(&logfile);
        }

        if (_traceHandle == InvalidTraceHandle)
        {
            ThrowOnError((uint)Marshal.GetLastPInvokeError(), "OpenTrace");
        }

        var traceHandle = _traceHandle;
        _thread = new Thread(() =>
        {
            // ProcessTrace blocks, delivering events to OnEventRecord, until the session is stopped.
            var handle = traceHandle;
            _ = ProcessTrace(&handle, 1, null, null);
        })
        {
            IsBackground = true,
            Name = "NetPulse ETW consumer",
        };
        _thread.Start();
    }

    private static EventTraceProperties* InitProperties(byte* buffer, int size)
    {
        new Span<byte>(buffer, size).Clear();
        var properties = (EventTraceProperties*)buffer;
        properties->Wnode.BufferSize = (uint)size;
        properties->Wnode.Flags = WNODE_FLAG_TRACED_GUID;
        properties->Wnode.ClientContext = 1; // QueryPerformanceCounter timestamps
        properties->Wnode.Guid = Guid.NewGuid();
        properties->LogFileMode = EVENT_TRACE_REAL_TIME_MODE;
        properties->FlushTimer = 1; // deliver buffers at least every second
        properties->BufferSize = 64; // KB
        properties->LoggerNameOffset = (uint)sizeof(EventTraceProperties);
        properties->LogFileNameOffset = 0;
        return properties;
    }

    private static void StopSessionByName()
    {
        var size = sizeof(EventTraceProperties) + (NameBufferChars * 2 * sizeof(char));
        var buffer = stackalloc byte[size];
        var properties = InitProperties(buffer, size);
        _ = ControlTraceW(0, SessionName, properties, EVENT_TRACE_CONTROL_STOP);
    }

    private static void ThrowOnError(uint status, string operation)
    {
        if (status == ERROR_SUCCESS)
        {
            return;
        }

        if (status == ERROR_ACCESS_DENIED)
        {
            throw new UnauthorizedAccessException(T("需要管理员权限才能按进程统计流量。", "Per-program traffic needs administrator rights."));
        }

        throw new Win32Exception((int)status, $"{operation} failed ({status}).");
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void OnEventRecord(EventRecord* record)
    {
        try
        {
            if (record->EventHeader.ProviderId != KernelNetworkProvider || record->UserDataLength < 8 || record->UserData == null)
            {
                return;
            }

            // Template of every send/receive event starts with: UInt32 PID, UInt32 size.
            var (received, sent) = record->EventHeader.Id switch
            {
                10 or 26 or 42 or 58 => (false, true),   // TCP/UDP send, IPv4/IPv6
                11 or 27 or 43 or 59 => (true, false),   // TCP/UDP receive, IPv4/IPv6
                _ => (false, false),
            };
            if (!received && !sent)
            {
                return;
            }

            var pid = (int)*(uint*)record->UserData;
            long bytes = *(uint*)((byte*)record->UserData + 4);
            var self = (KernelNetworkTrace)GCHandle.FromIntPtr(record->UserContext).Target!;
            self._table.Add(pid, received ? bytes : 0, sent ? bytes : 0);
            Interlocked.Increment(ref self._eventCount);
        }
        catch
        {
            // Never let an exception unwind into ETW.
        }
    }

    // ---- Win32 definitions (evntrace.h / evntcons.h) ----

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint StartTraceW(out ulong sessionHandle, string sessionName, EventTraceProperties* properties);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint ControlTraceW(ulong sessionHandle, string? sessionName, EventTraceProperties* properties, uint controlCode);

    [DllImport("advapi32.dll", ExactSpelling = true)]
    private static extern uint EnableTraceEx2(
        ulong sessionHandle, Guid* providerId, uint controlCode, byte level,
        ulong matchAnyKeyword, ulong matchAllKeyword, uint timeout, void* enableParameters);

    [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern ulong OpenTraceW(EventTraceLogfile* logfile);

    [DllImport("advapi32.dll", ExactSpelling = true)]
    private static extern uint ProcessTrace(ulong* handles, uint handleCount, long* startTime, long* endTime);

    [DllImport("advapi32.dll", ExactSpelling = true)]
    private static extern uint CloseTrace(ulong traceHandle);

    [StructLayout(LayoutKind.Sequential)]
    private struct WnodeHeader
    {
        public uint BufferSize;
        public uint ProviderId;
        public ulong HistoricalContext;
        public long TimeStamp;
        public Guid Guid;
        public uint ClientContext;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EventTraceProperties
    {
        public WnodeHeader Wnode;
        public uint BufferSize;
        public uint MinimumBuffers;
        public uint MaximumBuffers;
        public uint MaximumFileSize;
        public uint LogFileMode;
        public uint FlushTimer;
        public uint EnableFlags;
        public int AgeLimit;
        public uint NumberOfBuffers;
        public uint FreeBuffers;
        public uint EventsLost;
        public uint BuffersWritten;
        public uint LogBuffersLost;
        public uint RealTimeBuffersLost;
        public nint LoggerThreadId;
        public uint LogFileNameOffset;
        public uint LoggerNameOffset;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EventTraceHeader
    {
        public ushort Size;
        public ushort FieldTypeFlags;
        public uint Version;
        public uint ThreadId;
        public uint ProcessId;
        public long TimeStamp;
        public Guid Guid;
        public ulong ProcessorTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EventTrace
    {
        public EventTraceHeader Header;
        public uint InstanceId;
        public uint ParentInstanceId;
        public Guid ParentGuid;
        public nint MofData;
        public uint MofLength;
        public uint ClientContext;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemTime
    {
        public ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TimeZoneInformation
    {
        public int Bias;
        public fixed char StandardName[32];
        public SystemTime StandardDate;
        public int StandardBias;
        public fixed char DaylightName[32];
        public SystemTime DaylightDate;
        public int DaylightBias;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TraceLogfileHeader
    {
        public uint BufferSize;
        public uint Version;
        public uint ProviderVersion;
        public uint NumberOfProcessors;
        public long EndTime;
        public uint TimerResolution;
        public uint MaximumFileSize;
        public uint LogFileMode;
        public uint BuffersWritten;
        public Guid LogInstanceGuid;
        public nint LoggerName;
        public nint LogFileName;
        public TimeZoneInformation TimeZone;
        public long BootTime;
        public long PerfFreq;
        public long StartTime;
        public uint ReservedFlags;
        public uint BuffersLost;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EventTraceLogfile
    {
        public char* LogFileName;
        public char* LoggerName;
        public long CurrentTime;
        public uint BuffersRead;
        public uint ProcessTraceMode;
        public EventTrace CurrentEvent;
        public TraceLogfileHeader LogfileHeader;
        public nint BufferCallback;
        public uint BufferSize;
        public uint Filled;
        public uint EventsLost;
        public delegate* unmanaged[Stdcall]<EventRecord*, void> EventRecordCallback;
        public uint IsKernelTrace;
        public nint Context;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EventHeader
    {
        public ushort Size;
        public ushort HeaderType;
        public ushort Flags;
        public ushort EventProperty;
        public uint ThreadId;
        public uint ProcessId;
        public long TimeStamp;
        public Guid ProviderId;
        public ushort Id;
        public byte Version;
        public byte Channel;
        public byte Level;
        public byte Opcode;
        public ushort Task;
        public ulong Keyword;
        public ulong ProcessorTime;
        public Guid ActivityId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EventRecord
    {
        public EventHeader EventHeader;
        public uint BufferContext;
        public ushort ExtendedDataCount;
        public ushort UserDataLength;
        public void* ExtendedData;
        public void* UserData;
        public nint UserContext;
    }
}
