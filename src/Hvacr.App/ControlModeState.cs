namespace Hvacr.App;

/// <summary>Control permission for this running instance; changing it never publishes to a device.</summary>
public sealed class ControlModeState(HvacrAppOptions options, StatusUpdates? updates = null)
{
    private int _readOnly = options.ReadOnly ? 1 : 0;
    public bool ReadOnly => Volatile.Read(ref _readOnly) != 0;
    public void SetReadOnly(bool value)
    {
        Interlocked.Exchange(ref _readOnly, value ? 1 : 0);
        updates?.Notify();
    }
}

public sealed record ControlModeRequest(bool? ReadOnly, bool ConfirmEnable = false);
