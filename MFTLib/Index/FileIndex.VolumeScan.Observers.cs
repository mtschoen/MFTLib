using System.Runtime.ExceptionServices;

namespace MFTLib.Index;

public sealed partial class FileIndex
{
    internal sealed partial class VolumeScan : IProgress<IndexScanProgress>
    {
        Action<string>? _diagnostics;

        Action<string> Diagnostics
        {
            get
            {
                lock (StateLock)
                {
                    return _diagnostics ??= CreateDiagnosticsSink(this);
                }
            }
        }

        static Action<string> CreateDiagnosticsSink(VolumeScan scan)
        {
            // A retained entry reaches BlockFile.Diagnostics. Its callback must not retain the scan,
            // other views or later mappings after this block retires.
            var owner = new WeakReference<VolumeScan>(scan);
            return message =>
            {
                if (owner.TryGetTarget(out var current))
                {
                    current.DeliverDiagnostics(message);
                }
            };
        }

        void DeliverDiagnostics(string message)
        {
            var delivered = new HashSet<Action<string>>();
            foreach (var view in ViewsSnapshot())
            {
                if (view.Options.Diagnostics is { } sink && delivered.Add(sink))
                {
                    DiagnosticsReport.Send(sink, message);
                }
            }
        }

        void IProgress<IndexScanProgress>.Report(IndexScanProgress value)
        {
            List<Exception>? failures = null;
            foreach (var view in ViewsSnapshot())
            {
                try
                {
                    view.Options.ScanProgress?.Report(value with { Root = view.Root });
                }
                catch (Exception exception)
                {
                    (failures ??= []).Add(exception);
                }
            }
            if (failures is not null)
            {
                if (failures.Count == 1)
                {
                    ExceptionDispatchInfo.Capture(failures[0]).Throw();
                }
                throw new AggregateException(failures);
            }
        }
    }
}
