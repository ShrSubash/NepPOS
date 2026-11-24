using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace NepPOS.Infrastructure.Adapters.Hardware;

/// <summary>The printer can't take a job right now; the message says why, in words for the cashier.</summary>
public sealed class PrinterNotReadyException(string message) : Exception(message);

/// <summary>
/// Sends bytes straight to a Windows printer queue as a RAW job (winspool), bypassing the driver's rendering.
/// This is how ESC/POS receipts and drawer kicks reach a thermal printer.
/// <para>
/// Windows accepts a job even when the printer is unplugged or off; it just waits in the queue and prints
/// (or kicks the drawer) whenever the printer comes back. So every job here is checked: the printer must be
/// ready before it's sent, and must take it within a few seconds; otherwise the job is deleted again and the
/// caller is told, so nothing builds up to print later.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
internal static class RawPrinterQueue
{
    /// <summary>How long the printer has to take a job before it's deleted and reported as not printed.</summary>
    public static readonly TimeSpan TakeWithin = TimeSpan.FromSeconds(10);

    // Someone else's job (a test page, the last receipt finishing) gets this long to clear first.
    private static readonly TimeSpan QueueClearWithin = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan PollEvery = TimeSpan.FromMilliseconds(250);

    /// <summary>Sends the job and waits until the printer has taken it.</summary>
    /// <exception cref="PrinterNotReadyException">The printer is offline, has a problem or didn't take the job (it was deleted).</exception>
    public static void Send(string printerName, byte[] data, string documentName, string notDone = "Receipt not printed")
    {
        if (string.IsNullOrWhiteSpace(printerName))
            throw new InvalidOperationException("No printer is selected.");

        ThrowIfNotReady(printerName, notDone);

        if (!OpenPrinter(printerName, out var handle, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Printer '{printerName}' could not be opened.");

        try
        {
            var doc = new DocInfo1 { DocName = documentName, OutputFile = null, DataType = "RAW" };

            var jobId = StartDocPrinter(handle, 1, ref doc);
            if (jobId == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"Printer '{printerName}' refused the job.");

            try
            {
                if (!StartPagePrinter(handle))
                    throw new Win32Exception(Marshal.GetLastWin32Error());

                if (!WritePrinter(handle, data, data.Length, out var written) || written != data.Length)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), $"Only {written} of {data.Length} bytes reached the printer.");

                EndPagePrinter(handle);
            }
            finally
            {
                EndDocPrinter(handle);
            }

            WaitUntilTaken(handle, jobId, printerName, notDone);
        }
        finally
        {
            ClosePrinter(handle);
        }
    }

    /// <summary>
    /// Throws when the printer can't print now: offline (unplugged or off), paused, an error, out of paper,
    /// or jobs already stuck in its queue. Used before receipts in both printer modes.
    /// </summary>
    public static void ThrowIfNotReady(string printerName, string notDone = "Receipt not printed")
    {
        var deadline = DateTime.UtcNow + QueueClearWithin;
        while (true)
        {
            var (status, attributes, jobs) = ReadPrinter(printerName);

            var problem = Describe(status, attributes);
            if (problem is not null)
                throw new PrinterNotReadyException($"{problem} {notDone}.");

            if (jobs == 0)
                return;

            if (DateTime.UtcNow >= deadline)
                throw new PrinterNotReadyException(
                    $"The printer isn't printing: {jobs} {(jobs == 1 ? "job is" : "jobs are")} stuck in its Windows queue. " +
                    $"Check it's on and connected, then clear the queue (Printers & scanners → Open print queue). {notDone}.");

            Thread.Sleep(PollEvery);
        }
    }

    /// <summary>The cashier's words for a Windows printer status, or null when it's ready.</summary>
    internal static string? Describe(uint status, uint attributes) =>
        (attributes & AttributeWorkOffline) != 0 || (status & (StatusOffline | StatusNotAvailable)) != 0
            ? "The printer is offline: check it's switched on and the cable is plugged in."
            : (status & StatusPaperOut) != 0 ? "The printer is out of paper."
            : (status & StatusPaperJam) != 0 ? "The printer has a paper jam."
            : (status & StatusDoorOpen) != 0 ? "The printer's cover is open."
            : (status & StatusPaused) != 0 ? "The printer is paused in Windows (Printers & scanners → Open print queue → Resume)."
            : (status & (StatusError | StatusUserIntervention)) != 0 ? "The printer has a problem: check its light and paper."
            : null;

    /// <summary>Waits for the job to leave the queue (the printer took it). If it doesn't, deletes it and throws.</summary>
    private static void WaitUntilTaken(IntPtr handle, int jobId, string printerName, string notDone)
    {
        var deadline = DateTime.UtcNow + TakeWithin;
        uint jobStatus = 0;
        while (DateTime.UtcNow < deadline)
        {
            var job = ReadJob(handle, jobId);
            if (job is null)
                return;                                                     // gone from the queue: the printer has it
            jobStatus = job.Value;
            if ((jobStatus & (JobPrinted | JobComplete)) != 0)
                return;                                                     // drivers that keep printed jobs listed
            if ((jobStatus & (JobError | JobOffline | JobPaperOut | JobBlocked | JobUserIntervention)) != 0)
                break;                                                      // it won't print: don't wait the full time
            Thread.Sleep(PollEvery);
        }

        // Not taken: delete it so it can't print (or open the drawer) later by surprise.
        var deleted = SetJob(handle, jobId, 0, IntPtr.Zero, JobControlDelete);
        var why = Describe(ReadPrinter(printerName).Status, 0)
                  ?? ((jobStatus & JobPaperOut) != 0 ? "The printer is out of paper." : "The printer didn't respond: check it's switched on and connected.");
        throw new PrinterNotReadyException(deleted
            ? $"{why} {notDone} (nothing is left waiting)."
            : $"{why} {notDone}. It's waiting in the Windows print queue: cancel it there if you don't want it later.");
    }

    private static (uint Status, uint Attributes, uint Jobs) ReadPrinter(string printerName)
    {
        if (!OpenPrinter(printerName, out var handle, IntPtr.Zero))
            throw new PrinterNotReadyException($"Printer '{printerName}' isn't installed on this PC (Manage → Receipt & Printer).");
        try
        {
            GetPrinter(handle, 2, IntPtr.Zero, 0, out var needed);
            if (needed <= 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"Couldn't read the status of '{printerName}'.");

            var buffer = Marshal.AllocHGlobal(needed);
            try
            {
                if (!GetPrinter(handle, 2, buffer, needed, out _))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), $"Couldn't read the status of '{printerName}'.");
                var info = Marshal.PtrToStructure<PrinterInfo2>(buffer);
                return (info.Status, info.Attributes, info.cJobs);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            ClosePrinter(handle);
        }
    }

    /// <summary>The job's status, or null when it's no longer in the queue.</summary>
    private static uint? ReadJob(IntPtr handle, int jobId)
    {
        GetJob(handle, jobId, 1, IntPtr.Zero, 0, out var needed);
        if (needed <= 0)
            return null;

        var buffer = Marshal.AllocHGlobal(needed);
        try
        {
            return GetJob(handle, jobId, 1, buffer, needed, out _) ? Marshal.PtrToStructure<JobInfo1>(buffer).Status : null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    // ---- winspool constants (WinSpool.h) ----
    private const uint StatusPaused = 0x1, StatusError = 0x2, StatusPaperJam = 0x8, StatusPaperOut = 0x10, StatusOffline = 0x80,
        StatusNotAvailable = 0x1000, StatusUserIntervention = 0x100000, StatusDoorOpen = 0x400000;
    private const uint AttributeWorkOffline = 0x400;
    private const uint JobError = 0x2, JobOffline = 0x20, JobPaperOut = 0x40, JobPrinted = 0x80, JobBlocked = 0x200,
        JobUserIntervention = 0x400, JobComplete = 0x1000;
    private const int JobControlDelete = 5;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DocInfo1
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string DocName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? OutputFile;
        [MarshalAs(UnmanagedType.LPWStr)] public string DataType;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PrinterInfo2
    {
        public IntPtr pServerName, pPrinterName, pShareName, pPortName, pDriverName, pComment, pLocation, pDevMode,
            pSepFile, pPrintProcessor, pDatatype, pParameters, pSecurityDescriptor;
        public uint Attributes, Priority, DefaultPriority, StartTime, UntilTime, Status, cJobs, AveragePPM;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobInfo1
    {
        public uint JobId;
        public IntPtr pPrinterName, pMachineName, pUserName, pDocument, pDatatype, pStatus;
        public uint Status, Priority, Position, TotalPages, PagesPrinted;
        public ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds;
    }

    [DllImport("winspool.drv", EntryPoint = "OpenPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool OpenPrinter(string printerName, out IntPtr printer, IntPtr defaults);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool ClosePrinter(IntPtr printer);

    [DllImport("winspool.drv", EntryPoint = "StartDocPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int StartDocPrinter(IntPtr printer, int level, ref DocInfo1 docInfo);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool EndDocPrinter(IntPtr printer);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool StartPagePrinter(IntPtr printer);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool EndPagePrinter(IntPtr printer);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool WritePrinter(IntPtr printer, byte[] bytes, int count, out int written);

    [DllImport("winspool.drv", EntryPoint = "GetPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool GetPrinter(IntPtr printer, int level, IntPtr buffer, int bufferSize, out int needed);

    [DllImport("winspool.drv", EntryPoint = "GetJobW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool GetJob(IntPtr printer, int jobId, int level, IntPtr buffer, int bufferSize, out int needed);

    [DllImport("winspool.drv", EntryPoint = "SetJobW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetJob(IntPtr printer, int jobId, int level, IntPtr job, int command);
}
