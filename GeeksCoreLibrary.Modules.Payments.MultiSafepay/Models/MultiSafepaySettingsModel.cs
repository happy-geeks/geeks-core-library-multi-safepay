using GeeksCoreLibrary.Components.OrderProcess.Models;

namespace GeeksCoreLibrary.Modules.Payments.MultiSafepay.Models;

public class MultiSafepaySettingsModel : PaymentServiceProviderSettingsModel
{
    /// <summary>
    /// Gets or sets the API key for the current environment.
    /// </summary>
    public string? ApiKey { get; set; }
}