namespace Sterp.Web.Pages.Fin.Common;

/// <summary>
/// The selection of a legacy DataGridView with MultiSelect = True and FullRowSelect: a set of selected rows plus the current
/// row. Binding makes the first row current and selected, a plain click selects one row, Ctrl+click adds or removes a row
/// and Shift+click selects the range from the anchor row.
/// </summary>
public sealed class MultiSelection
{
    private readonly List<int> _rows = [];
    private int _anchor = -1;

    /// <summary>GridHelper.Selected / CurrentRow: the row last clicked (-1 = none).</summary>
    public int Current { get; private set; } = -1;

    public bool Contains(int row) => _rows.Contains(row);

    /// <summary>DataGridView.SelectedRows: the most recently selected row first.</summary>
    public IEnumerable<int> SelectedRows => Enumerable.Reverse(_rows);

    /// <summary>GridHelper.Bind: the first row (if any) becomes current and selected.</summary>
    public void Reset(int count)
    {
        _rows.Clear();
        Current = _anchor = count > 0 ? 0 : -1;
        if (count > 0) _rows.Add(0);
    }

    public void Click(int row, bool ctrl, bool shift)
    {
        if (shift && _anchor >= 0)
        {
            if (!ctrl) _rows.Clear();
            var step = row >= _anchor ? 1 : -1;
            for (var i = _anchor; ; i += step)
            {
                if (!_rows.Contains(i)) _rows.Add(i);
                if (i == row) break;
            }
            Current = row;
            return;
        }
        if (ctrl)
        {
            if (!_rows.Remove(row)) _rows.Add(row);
        }
        else
        {
            _rows.Clear();
            _rows.Add(row);
        }
        Current = _anchor = row;
    }
}
