using System.Text.Json.Nodes;
using Sterp.Web.Framework.Format;

namespace Sterp.Web.Framework.Pages;

/// <summary>
/// Base of the enquiry/worklist pages - the legacy <c>ListForm</c> (ListForm.vb, FR-STERP-WEB-016): the data is loaded when
/// the window opens and on F5 / Refresh (F5); <see cref="Populate"/> binds the grid and reports "&lt;n&gt; record(s)". Render the
/// page with <c>&lt;StListLayout&gt;</c>.
/// </summary>
public abstract class StListPage : StPage
{
    /// <summary>The bound rows (GridHelper.Bind).</summary>
    protected GridData Data { get; set; } = GridData.Empty;

    /// <summary>Index of the current row (-1 = none), GridHelper.Selected.</summary>
    protected int Selected { get; set; } = -1;

    /// <summary>ListForm.LoadData: read the data with the current filters and call <see cref="Populate"/>.</summary>
    protected abstract Task LoadDataAsync();

    protected override async Task OnInitializedAsync()
    {
        await base.OnInitializedAsync();
        await LoadDataAsync();
    }

    protected override Task OnRefreshKeyAsync() => LoadDataAsync();

    /// <summary>ListForm.Populate: binds the grid (the first row becomes current) and sets "&lt;n&gt; record(s)".</summary>
    protected void Populate(GridData data)
    {
        Data = data;
        Selected = data.Count > 0 ? 0 : -1;
        SetStatus(data.Count + " record(s)", "");
        StateHasChanged();
    }

    /// <summary>GridHelper.Selected: value of a column in the current row (null when no row is current).</summary>
    protected JsonNode? SelectedValue(string column) => Selected >= 0 ? Data.Value(Selected, column) : null;

    protected string SelectedText(string column) => Selected >= 0 ? Data.Text(Selected, column) : "";

    protected JsonObject? SelectedRow => Selected >= 0 && Selected < Data.Count ? Data.Rows[Selected] : null;

    protected Task RefreshAsync() => LoadDataAsync();

    protected Task CloseFromToolbarAsync() => CloseAsync();

    protected void OnSelectedChanged(int index)
    {
        Selected = index;
        StateHasChanged();
    }
}
