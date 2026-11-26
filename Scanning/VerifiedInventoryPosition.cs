namespace ZZZScannerNext.Scanning;

internal readonly record struct VerifiedInventoryPosition(int Row, int Column);

// Producer-owned navigation provenance. Each capture receives an immutable position snapshot;
// delayed OCR must never consult the producer's later viewport state.
internal sealed class VerifiedInventoryTraversal(int columns)
{
    private int top = 1;
    private bool trusted = true;

    public void Advance(int fromTop, int rows, bool confirmedByAnchor)
    {
        trusted &= confirmedByAnchor && fromTop == top && rows == 1;
        if (trusted) top++;
    }

    public VerifiedInventoryPosition? Capture(int? logicalRow, int visualRow, int column) =>
        trusted && logicalRow == top + visualRow - 1 && visualRow is >= 1 and <= 4
        && column >= 1 && column <= columns ? new(logicalRow.Value, column) : null;
}
