using System.Globalization;
using BTCPayServer.Payments;
using BTCPayServer.Services.Invoices;
using Microsoft.AspNetCore.Mvc;

namespace BTCPayServer.RockstarDev.Plugins.CashCheckoutMethod.PaymentHandlers;

public class CashPaymentLinkExtension(PaymentMethodId paymentMethodId) : IPaymentLinkExtension
{
    public PaymentMethodId PaymentMethodId { get; } = paymentMethodId;

    public string? GetPaymentLink(PaymentPrompt prompt, IUrlHelper? urlHelper)
    {
        var due = prompt.Calculate().Due;
        return $"{prompt.Destination}?amount={due.ToString(CultureInfo.InvariantCulture)}";
    }
}