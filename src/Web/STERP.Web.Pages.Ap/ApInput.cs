using Sterp.Web.Framework.Dialogs;
using Sterp.Web.Pages.Ap.Pages;

namespace Sterp.Web.Pages.Ap;

/// <summary>One box of the keyed capture dialog: key, caption (without the colon), default text and maximum length.</summary>
public sealed record ApInputField(string Key, string Caption, string Default, int MaxLength);

/// <summary>ApInputDialog.Collect and WidthError (frmSupplierVerify.vb:206-256).</summary>
public static class ApInput
{
    /// <summary>Shows the dialog; null when cancelled or when a value is wider than its field (after the legacy refusal message).</summary>
    public static async Task<Dictionary<string, string>?> CollectAsync(DialogService dialogs, string title, params ApInputField[] fields)
    {
        var result = await dialogs.ShowAsync(typeof(ApInputDialog), new Dictionary<string, object?>
        {
            [nameof(ApInputDialog.Title)] = title,
            [nameof(ApInputDialog.Fields)] = fields,
        });
        if (result is not Dictionary<string, string> values) return null;
        var invalid = WidthError(fields, values);
        if (invalid != "")
        {
            await dialogs.MessageAsync(invalid, "STERP - Input", DialogButtons.Ok, DialogIcon.Error);
            return null;
        }
        return values;
    }

    /// <summary>ApInputDialog.WidthError: the first value longer than its field refuses the whole capture.</summary>
    public static string WidthError(IEnumerable<ApInputField> fields, IReadOnlyDictionary<string, string> values)
    {
        foreach (var field in fields)
        {
            if (values[field.Key].Length > field.MaxLength)
            {
                return field.Key + " must be at most " + field.MaxLength + " characters. Nothing was recorded.";
            }
        }
        return "";
    }
}
