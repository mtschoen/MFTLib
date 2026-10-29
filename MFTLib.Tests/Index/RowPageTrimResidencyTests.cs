using System.Diagnostics;
using System.Runtime.InteropServices;
using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>Real Windows check, through <c>QueryWorkingSetEx</c>, that a trim removes the pages from the working set.</summary>
[TestClass]
[OSCondition(OperatingSystems.Windows)]
public sealed partial class RowPageTrimResidencyTests
{
    [StructLayout(LayoutKind.Sequential)]
    struct WorkingSetExInformation
    {
        public nuint VirtualAddress;
        public nuint VirtualAttributes;
    }

    [LibraryImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryWorkingSetEx(nint process, ref WorkingSetExInformation information, int bytes);

    static unsafe int ResidentPages(byte* start, int pageCount)
    {
        using var process = Process.GetCurrentProcess();
        var resident = 0;
        for (var page = 0; page < pageCount; page++)
        {
            var information = new WorkingSetExInformation
            {
                VirtualAddress = (nuint)(start + (long)page * Environment.SystemPageSize)
            };
            Assert.IsTrue(QueryWorkingSetEx(process.Handle, ref information,
                Marshal.SizeOf<WorkingSetExInformation>()));
            resident += (int)(information.VirtualAttributes & 1);
        }

        return resident;
    }

    [TestMethod]
    public unsafe void TryReleaseRowPages_RemovesTheRangeFromTheWorkingSet()
    {
        using var directory = new TempDirectory("mftlib-trim-residency");
        const uint rows = 65536;
        using var block = TestBlocks.Create(Path.Combine(directory.Path, "T-0BADF00D.mlix"), slotCapacity: rows);
        var pageBytes = Environment.SystemPageSize;
        var pageCount = (int)(rows * (long)BlockLayout.RowBytes / pageBytes) - 1;
        fixed (FileRow* first = block.Rows)
        {
            // Skip the first page: the row region starts page aligned, so every page of the range is whole.
            var start = (byte*)first;
            long checksum = 0;
            for (var offset = 0L; offset < (long)pageCount * pageBytes; offset += pageBytes)
            {
                checksum += start[offset];
            }

            Assert.AreEqual(pageCount, ResidentPages(start, pageCount), "The scan must make the pages resident first.");

            Assert.IsTrue(block.TryReleaseRowPages(0, rows));

            Assert.AreEqual(0, ResidentPages(start, pageCount));
            Assert.AreEqual(0, checksum);
            Assert.AreEqual(0, start[0], "The mapping stays valid and refaults.");
        }
    }
}
