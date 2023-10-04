using GeeksCoreLibrary.Components.OrderProcess.Models;
using GeeksCoreLibrary.Components.ShoppingBasket;
using GeeksCoreLibrary.Components.ShoppingBasket.Interfaces;
using GeeksCoreLibrary.Core.DependencyInjection.Interfaces;
using GeeksCoreLibrary.Core.Enums;
using GeeksCoreLibrary.Core.Extensions;
using GeeksCoreLibrary.Core.Helpers;
using GeeksCoreLibrary.Core.Models;
using GeeksCoreLibrary.Modules.Databases.Interfaces;
using GeeksCoreLibrary.Modules.Payments.Enums;
using GeeksCoreLibrary.Modules.Payments.Interfaces;
using GeeksCoreLibrary.Modules.Payments.Models;
using GeeksCoreLibrary.Modules.Payments.MultiSafepay.Models;
using GeeksCoreLibrary.Modules.Payments.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MultiSafepay;
using MultiSafepay.Model;
using Newtonsoft.Json;
using MultiSafepayConstants = GeeksCoreLibrary.Modules.Payments.MultiSafepay.Models.Constants;
using Constants = GeeksCoreLibrary.Components.OrderProcess.Models.Constants;

namespace GeeksCoreLibrary.Modules.Payments.MultiSafepay.Services;

/// <inheritdoc cref="IPaymentServiceProviderService" />
public class MultiSafepayService : PaymentServiceProviderBaseService, IPaymentServiceProviderService, IScopedService
{
    private readonly GclSettings gclSettings;
    private readonly IHttpContextAccessor httpContextAccessor;
    private readonly IShoppingBasketsService shoppingBasketsService;
    private readonly IDatabaseConnection databaseConnection;

    private MultiSafepayClient client;

    public MultiSafepayService(ILogger<MultiSafepayService> logger,
                               IOptions<GclSettings> gclSettings,
                               IShoppingBasketsService shoppingBasketsService,
                               IDatabaseConnection databaseConnection,
                               IDatabaseHelpersService databaseHelpersService,
                               IHttpContextAccessor httpContextAccessor = null)
        : base(databaseHelpersService, databaseConnection, logger, httpContextAccessor)
    {
        this.gclSettings = gclSettings.Value;
        this.httpContextAccessor = httpContextAccessor;
        this.shoppingBasketsService = shoppingBasketsService;
        this.databaseConnection = databaseConnection;
    }

    /// <summary>
    /// Create the client based on the environment.
    /// </summary>
    private void SetupEnvironment(string apiKey)
    {
        client = new MultiSafepayClient(apiKey, gclSettings.Environment.InList(Environments.Live, Environments.Acceptance) ? "https://api.multisafepay.com/v1/json/" : "https://testapi.multisafepay.com/v1/json/");
    }

    /// <inheritdoc />
    public async Task<PaymentRequestResult> HandlePaymentRequestAsync(ICollection<(WiserItemModel Main, List<WiserItemModel> Lines)> shoppingBaskets, WiserItemModel userDetails, PaymentMethodSettingsModel paymentMethodSettings, string invoiceNumber)
    {
        var basketSettings = await shoppingBasketsService.GetSettingsAsync();
        var multiSafepaySettings = (MultiSafepaySettingsModel) paymentMethodSettings.PaymentServiceProvider;

        var firstBasket = shoppingBaskets.First();
        var apiKeyFromBasket = firstBasket.Main.GetDetailValue(MultiSafepayConstants.ApiKeyProperty);
        if (!String.IsNullOrWhiteSpace(apiKeyFromBasket))
        {
            multiSafepaySettings.ApiKey = apiKeyFromBasket;
        }

        var totalPrice = 0M;
        foreach (var (main, lines) in shoppingBaskets)
        {
            totalPrice += await shoppingBasketsService.GetPriceAsync(main, lines, basketSettings, ShoppingBasket.PriceTypes.PspPriceInVat);
        }

        var totalPriceInCents = (int) Math.Round(totalPrice * 100);

        var order = new Order
        {
            Type = OrderType.Redirect,
            OrderId = invoiceNumber,
            GatewayId = paymentMethodSettings.ExternalName,
            AmountInCents = totalPriceInCents,
            CurrencyCode = multiSafepaySettings.Currency,
            PaymentOptions = new PaymentOptions(multiSafepaySettings.WebhookUrl,
                                                multiSafepaySettings.SuccessUrl,
                                                multiSafepaySettings.FailUrl)
        };

        SetupEnvironment(multiSafepaySettings.ApiKey);

        var description = firstBasket.Main.GetDetailValue(MultiSafepayConstants.TransactionReferenceProperty);
        if (String.IsNullOrWhiteSpace(description))
        {
            description = $"Wiser order #{firstBasket.Main.Id}";
        }

        var basketCurrency = firstBasket.Main.GetDetailValue(MultiSafepayConstants.CurrencyProperty);
        if (!String.IsNullOrWhiteSpace(basketCurrency))
        {
            order.CurrencyCode = basketCurrency;
        }

        order.Description = description;

        string error = null;
        OrderResponse response = null;
        try
        {
            response = client.CustomOrder(order);

            return new PaymentRequestResult
            {
                Successful = true,
                Action = PaymentRequestActions.Redirect,
                ActionData = response.PaymentUrl
            };
        }
        catch (Exception exception)
        {
            error = exception.ToString();
            return new PaymentRequestResult
            {
                Action = PaymentRequestActions.Redirect,
                ActionData = paymentMethodSettings.PaymentServiceProvider.FailUrl,
                Successful = false,
                ErrorMessage = exception.Message
            };
        }
        finally
        {
            var responseJson = response == null ? null : JsonConvert.SerializeObject(response);
            await AddLogEntryAsync(PaymentServiceProviders.MultiSafepay, invoiceNumber, requestBody: JsonConvert.SerializeObject(order), responseBody: responseJson, error: error, isIncomingRequest: false);
        }
    }

    /// <inheritdoc />
    public async Task<StatusUpdateResult> ProcessStatusUpdateAsync(OrderProcessSettingsModel orderProcessSettings, PaymentMethodSettingsModel paymentMethodSettings)
    {
        if (httpContextAccessor?.HttpContext == null)
        {
            return new StatusUpdateResult
            {
                Status = "Request not available; unable to process status update.",
                Successful = false
            };
        }

        // Retrieve the order with the given transaction id/order id to check the status.
        var orderId = httpContextAccessor.HttpContext.Request.Query[MultiSafepayConstants.WebhookInvoiceNumberProperty].ToString();
        var multiSafepaySettings = (MultiSafepaySettingsModel) paymentMethodSettings.PaymentServiceProvider;
        OrderResponse response = null;

        SetupEnvironment(multiSafepaySettings.ApiKey);

        var success = false;
        string error = null;
        try
        {
            response = client.GetOrder(orderId);
            success = response.Status.ToLower() == "completed";
        }
        catch (Exception exception)
        {
            success = false;
            error = exception.ToString();

            return new StatusUpdateResult
            {
                Status = "Unable to retrieve order information.",
                Successful = false
            };
        }
        finally
        {
            // Log the incoming request from the PSP to us.
            await LogIncomingPaymentActionAsync(PaymentServiceProviders.MultiSafepay, orderId, success ? 1 : 0);

            // Log the outgoing request from us to the PSP.
            var responseJson = response == null ? null : JsonConvert.SerializeObject(response);
            await AddLogEntryAsync(PaymentServiceProviders.MultiSafepay, orderId, success ? 1 : 0, responseBody: responseJson, error: error, isIncomingRequest: false);
        }

        return new StatusUpdateResult
        {
            Status = response.Status,
            Successful = success
        };
    }

    /// <inheritdoc />
    public async Task<PaymentServiceProviderSettingsModel> GetProviderSettingsAsync(PaymentServiceProviderSettingsModel paymentServiceProviderSettings)
    {
        databaseConnection.AddParameter("id", paymentServiceProviderSettings.Id);

        var query = $@"SELECT
    multiSafepayApiKeyLive.`value` AS multiSafepayApiKeyLive,
    multiSafepayApiKeyTest.`value` AS multiSafepayApiKeyTest
FROM {WiserTableNames.WiserItem} AS paymentServiceProvider
LEFT JOIN {WiserTableNames.WiserItemDetail} AS multiSafepayApiKeyLive ON multiSafepayApiKeyLive.item_id = paymentServiceProvider.id AND multiSafepayApiKeyLive.`key` = '{MultiSafepayConstants.MultiSafepayApiKeyLiveProperty}'
LEFT JOIN {WiserTableNames.WiserItemDetail} AS multiSafepayApiKeyTest ON multiSafepayApiKeyTest.item_id = paymentServiceProvider.id AND multiSafepayApiKeyTest.`key` = '{MultiSafepayConstants.MultiSafepayApiKeyTestProperty}'
WHERE paymentServiceProvider.id = ?id
AND paymentServiceProvider.entity_type = '{Constants.PaymentServiceProviderEntityType}'";


        var result = new MultiSafepaySettingsModel
        {
            Id = paymentServiceProviderSettings.Id,
            Title = paymentServiceProviderSettings.Title,
            Type = paymentServiceProviderSettings.Type,
            LogAllRequests = paymentServiceProviderSettings.LogAllRequests,
            OrdersCanBeSetDirectlyToFinished = paymentServiceProviderSettings.OrdersCanBeSetDirectlyToFinished,
            SkipPaymentWhenOrderAmountEqualsZero = paymentServiceProviderSettings.SkipPaymentWhenOrderAmountEqualsZero
        };

        var dataTable = await databaseConnection.GetAsync(query);
        if (dataTable.Rows.Count == 0)
        {
            return result;
        }

        var row = dataTable.Rows[0];

        var suffix = gclSettings.Environment.InList(Environments.Development, Environments.Test) ? "Test" : "Live";
        result.ApiKey = row.GetAndDecryptSecretKey($"multiSafepayApiKey{suffix}");
        return result;
    }

    /// <inheritdoc />
    public string GetInvoiceNumberFromRequest()
    {
        return HttpContextHelpers.GetRequestValue(httpContextAccessor?.HttpContext, MultiSafepayConstants.WebhookInvoiceNumberProperty);
    }
}