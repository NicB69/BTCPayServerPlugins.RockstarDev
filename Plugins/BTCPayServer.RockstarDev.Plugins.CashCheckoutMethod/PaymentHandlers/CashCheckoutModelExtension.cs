using System.Collections.Generic;
using System.Linq;
using System.Text.Encodings.Web;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Bitcoin;

namespace BTCPayServer.RockstarDev.Plugins.CashCheckoutMethod.PaymentHandlers;
public class CashCheckoutModelExtension(CashCheckoutConfigurationItem configurationItem) : ICheckoutModelExtension
{
    public PaymentMethodId PaymentMethodId { get; } = configurationItem.GetPaymentMethodId();
    public string Image => "";
    public string Badge => "";

    public void ModifyCheckoutModel(CheckoutModelContext context)
    {
        if (context is not { Handler: CashPaymentMethodHandler handler })
            return;
        
        context.Model.CheckoutBodyComponentName = BitcoinCheckoutModelExtension.CheckoutBodyComponentName;

        context.Model.InvoiceBitcoinUrlQR = null;
        context.Model.ExpirationSeconds = int.MaxValue;
        
        context.Model.InvoiceBitcoinUrl = $"/stores/{context.Model.StoreId}/cash/MarkAsPaid?"+
                                          $"invoiceId={context.Model.InvoiceId}&"+
                                          $"returnUrl={UrlEncoder.Default.Encode(context.Model.MerchantRefLink)}";
        context.Model.ShowPayInWalletButton = true;
    }
}