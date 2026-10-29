namespace Relight.Core;

public enum ScanIoOperation { RootValidation, ReadMetadata, EnumerateDirectory, Aggregate }

public interface IStorageScanDiagnostics
{
    void Operation(ScanIoOperation operation, string path, int depth, int visited, int measured,
        int skipped, int aliases, int identities);
    void OperationEnded();
    void ReadFailure(Exception error);
}
