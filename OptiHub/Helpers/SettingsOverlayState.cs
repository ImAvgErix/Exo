namespace OptiHub.Helpers;

/// <summary>
/// Session gate for the settings modal so a delayed close Finish cannot
/// collapse the overlay after a fast close→re-open (epoch mismatch).
/// Pure state — unit-testable without WinUI.
/// </summary>
public sealed class SettingsOverlayState
{
    public int Epoch { get; private set; }
    public bool IsOpen { get; private set; }

    /// <summary>Begin open. Returns false if already open.</summary>
    public bool TryBeginOpen()
    {
        if (IsOpen) return false;
        Epoch++;
        IsOpen = true;
        return true;
    }

    /// <summary>
    /// Begin close. Returns false if not open.
    /// <paramref name="closeEpoch"/> must be passed to <see cref="ShouldApplyCloseFinish"/>.
    /// </summary>
    public bool TryBeginClose(out int closeEpoch)
    {
        closeEpoch = 0;
        if (!IsOpen) return false;
        IsOpen = false;
        closeEpoch = ++Epoch;
        return true;
    }

    /// <summary>
    /// True only if no open (or later close) happened after this close started.
    /// Stale Finish from a previous close must return false.
    /// </summary>
    public bool ShouldApplyCloseFinish(int closeEpoch) =>
        !IsOpen && closeEpoch == Epoch;
}
