using Sterp.Web.Framework.Registry;
using Sterp.Web.Pages.Fin.Pages;

namespace Sterp.Web.Pages.Fin;

/// <summary>
/// The STERP-FIN page library (wave W5, STERP-WEB section 7.2): the five finance forms of the legacy client
/// (dotnet/STERP.Desktop/Forms/Finance) on the FIN operations of STERP.Api.
/// </summary>
public sealed class FinPages : IPageModule
{
    public string Slice => "STERP-FIN";

    public void Register(PageRegistryBuilder pages)
    {
        pages.Add<JournalEntryPage>("frmJournalEntry");
        pages.Add<JournalEnquiryPage>("frmJournalEnquiry");
        pages.Add<PeriodClosePage>("frmPeriodClose");
        pages.Add<BankReconPage>("frmBankRecon");
        pages.Add<VatReturnPage>("frmVatReturn");
    }
}
