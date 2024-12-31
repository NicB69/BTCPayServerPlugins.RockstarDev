﻿using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Data;
using BTCPayServer.Models;
using BTCPayServer.RockstarDev.Plugins.Payroll.Data;
using BTCPayServer.RockstarDev.Plugins.Payroll.Data.Models;
using BTCPayServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NBitcoin;
using System;
using System.Linq;
using System.Threading.Tasks;
using BTCPayServer.RockstarDev.Plugins.Payroll.ViewModels;
using static BTCPayServer.RockstarDev.Plugins.Payroll.Controllers.PayrollInvoiceController;

namespace BTCPayServer.RockstarDev.Plugins.Payroll.Controllers;

[AllowAnonymous]
[Route("~/plugins/{storeId}/vendorpay/public/", Order = 0)]
[Route("~/plugins/{storeId}/payroll/public/", Order = 1)]
public class PublicController(
    ApplicationDbContextFactory dbContextFactory,
    PayrollPluginDbContextFactory payrollPluginDbContextFactory,
    IHttpContextAccessor httpContextAccessor,
    BTCPayNetworkProvider networkProvider,
    IFileService fileService,
    UriResolver uriResolver,
    VendorPayPassHasher hasher,
    ISettingsRepository settingsRepository)
    : Controller
{
    private const string PAYROLL_AUTH_USER_ID = "PAYROLL_AUTH_USER_ID";


    [HttpGet("login")]
    public async Task<IActionResult> Login(string storeId)
    {
        var vali = await validateStoreAndUser(storeId, false);
        if (vali.ErrorActionResult != null)
            return vali.ErrorActionResult;

        var model = new PublicLoginViewModel
        {
            StoreName = vali.Store.StoreName,
            StoreBranding = await StoreBrandingViewModel.CreateAsync(Request, uriResolver, vali.Store.GetStoreBlob())
        };

        return View(model);
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login(string storeId, PublicLoginViewModel model)
    {
        var vali = await validateStoreAndUser(storeId, false);
        if (vali.ErrorActionResult != null)
            return vali.ErrorActionResult;

        model.StoreId = vali.Store.Id;
        model.StoreName = vali.Store.StoreName;
        model.StoreBranding = await StoreBrandingViewModel.CreateAsync(Request, uriResolver, vali.Store.GetStoreBlob());

        await using var dbPlugins = payrollPluginDbContextFactory.CreateContext();
        var userInDb = dbPlugins.PayrollUsers.SingleOrDefault(a =>
            a.StoreId == storeId && a.Email == model.Email.ToLowerInvariant());

        if (userInDb != null)
        {
            if (userInDb.State == PayrollUserState.Active && hasher.IsValidPassword(userInDb, model.Password))
            {
                httpContextAccessor.HttpContext!.Session.SetString(PAYROLL_AUTH_USER_ID, userInDb!.Id);
                return RedirectToAction(nameof(ListInvoices), new { storeId });
            }
        }

        // if we end up here, credentials are invalid 
        ModelState.AddModelError(nameof(model.Password), "Invalid credentials");
        return View(model);
    }

    //

    [HttpGet("logout")]
    public IActionResult Logout(string storeId)
    {
        httpContextAccessor.HttpContext?.Session.Remove(PAYROLL_AUTH_USER_ID);
        return redirectToLogin(storeId);
    }

    private RedirectToActionResult redirectToLogin(string storeId)
    {
        return RedirectToAction(nameof(Login), new { storeId });
    }

    [HttpGet("listinvoices")]
    public async Task<IActionResult> ListInvoices(string storeId)
    {
        var vali = await validateStoreAndUser(storeId, true);
        if (vali.ErrorActionResult != null)
            return vali.ErrorActionResult;

        await using var ctx = payrollPluginDbContextFactory.CreateContext();
        var payrollInvoices = await ctx.PayrollInvoices
            .Include(data => data.User)
            .Where(p => p.User.StoreId == storeId && p.UserId == vali.UserId && p.IsArchived == false)
            .OrderByDescending(data => data.CreatedAt).ToListAsync();

        var settings = await ctx.GetSettingAsync(storeId);
        var model = new PublicListInvoicesViewModel
        {
            StoreId = vali.Store.Id,
            StoreName = vali.Store.StoreName,
            StoreBranding = await StoreBrandingViewModel.CreateAsync(Request, uriResolver, vali.Store.GetStoreBlob()),
            PurchaseOrdersRequired = settings.PurchaseOrdersRequired,
            Invoices = payrollInvoices.Select(tuple => new PayrollInvoiceViewModel()
            {
                CreatedAt = tuple.CreatedAt,
                Id = tuple.Id,
                Name = tuple.User.Name,
                Email = tuple.User.Email,
                Destination = tuple.Destination,
                Amount = tuple.Amount,
                Currency = tuple.Currency,
                State = tuple.State,
                TxnId = tuple.TxnId,
                PurchaseOrder = tuple.PurchaseOrder,
                Description = tuple.Description,
                InvoiceUrl = tuple.InvoiceFilename
            }).ToList()
        };

        return View(model);
    }

    private async Task<StoreUserValidator> validateStoreAndUser(string storeId, bool validateUser)
    {
        await using var dbMain = dbContextFactory.CreateContext();
        var store = await dbMain.Stores.SingleOrDefaultAsync(a => a.Id == storeId);
        if (store == null)
            return new StoreUserValidator { ErrorActionResult = NotFound() };

        string userId = null;
        if (validateUser)
        {
            await using var dbPlugin = payrollPluginDbContextFactory.CreateContext();
            userId = httpContextAccessor.HttpContext!.Session.GetString(PAYROLL_AUTH_USER_ID);
            var userInDb = dbPlugin.PayrollUsers.SingleOrDefault(a =>
                a.StoreId == storeId && a.Id == userId && a.State == PayrollUserState.Active);
            if (userInDb == null)
                return new StoreUserValidator { ErrorActionResult = redirectToLogin(storeId) };
            else
                userId = userInDb.Id;
        }

        return new StoreUserValidator { Store = store, UserId = userId };
    }
    private class StoreUserValidator
    {
        public IActionResult ErrorActionResult { get; set; }
        public StoreData Store { get; set; }
        public string UserId { get; set; }
    }


    // upload
    [HttpGet("upload")]
    public async Task<IActionResult> Upload(string storeId)
    {
        var vali = await validateStoreAndUser(storeId, true);
        if (vali.ErrorActionResult != null)
            return vali.ErrorActionResult;

        var settings = await payrollPluginDbContextFactory.GetSettingAsync(storeId);
        var model = new PublicPayrollInvoiceUploadViewModel
        {
            StoreId = vali.Store.Id,
            StoreName = vali.Store.StoreName,
            StoreBranding = await StoreBrandingViewModel.CreateAsync(Request, uriResolver, vali.Store.GetStoreBlob()),
            Amount = 0,
            Currency = vali.Store.GetStoreBlob().DefaultCurrency,
            PurchaseOrdersRequired = settings.PurchaseOrdersRequired
        };

        return View(model);
    }

    [HttpPost("upload")]

    public async Task<IActionResult> Upload(string storeId, PublicPayrollInvoiceUploadViewModel model)
    {
        var vali = await validateStoreAndUser(storeId, true);
        if (vali.ErrorActionResult != null)
            return vali.ErrorActionResult;

        model.StoreId = vali.Store.Id;
        model.StoreName = vali.Store.StoreName;
        model.StoreBranding = await StoreBrandingViewModel.CreateAsync(Request, uriResolver, vali.Store.GetStoreBlob());

        if (model.Amount <= 0)
            ModelState.AddModelError(nameof(model.Amount), "Amount must be more than 0.");

        try
        {
            var network = networkProvider.GetNetwork<BTCPayNetwork>(PayrollPluginConst.BTC_CRYPTOCODE);
            var unused = Network.Parse<BitcoinAddress>(model.Destination, network.NBitcoinNetwork);
        }
        catch (Exception)
        {
            ModelState.AddModelError(nameof(model.Destination), "Invalid Destination, check format of address.");
        }

        await using var dbPlugin = payrollPluginDbContextFactory.CreateContext();
        var settings = await dbPlugin.GetSettingAsync(storeId);
        if (!settings.MakeInvoiceFilesOptional && model.Invoice == null)
        {
            ModelState.AddModelError(nameof(model.Invoice), "Kindly include an invoice");
        }
        
        if (settings.PurchaseOrdersRequired && string.IsNullOrEmpty(model.PurchaseOrder))
        {
            model.PurchaseOrdersRequired = true;
            ModelState.AddModelError(nameof(model.PurchaseOrder), "Purchase Order is required");
        }

        var alreadyInvoiceWithAddress = dbPlugin.PayrollInvoices.Any(a =>
            a.Destination == model.Destination &&
            a.State != PayrollInvoiceState.Completed && a.State != PayrollInvoiceState.Cancelled);

        if (alreadyInvoiceWithAddress)
            ModelState.AddModelError(nameof(model.Destination), "This destination is already specified for another invoice from which payment is in progress");

        if (!ModelState.IsValid)
        {
            return View(model);
        }

        // TODO: Make saving of the file and entry in the database atomic
        var removeTrailingZeros = model.Amount % 1 == 0 ? (int)model.Amount : model.Amount; // this will remove .00 from the amount
        var dbPayrollInvoice = new PayrollInvoice
        {
            Amount = removeTrailingZeros,
            CreatedAt = DateTime.UtcNow,
            Currency = model.Currency,
            Destination = model.Destination,
            PurchaseOrder = model.PurchaseOrder,
            Description = model.Description,
            UserId = vali.UserId,
            State = PayrollInvoiceState.AwaitingApproval
        };
        if (!settings.MakeInvoiceFilesOptional && model.Invoice != null)
        {
            var adminset = await settingsRepository.GetSettingAsync<PayrollPluginSettings>();
            var uploaded = await fileService.AddFile(model.Invoice, adminset!.AdminAppUserId);
            dbPayrollInvoice.InvoiceFilename = uploaded.Id;
        }

        dbPlugin.Add(dbPayrollInvoice);
        await dbPlugin.SaveChangesAsync();

        TempData.SetStatusMessageModel(new StatusMessageModel()
        {
            Message = $"Invoice uploaded successfully",
            Severity = StatusMessageModel.StatusSeverity.Success
        });
        return RedirectToAction(nameof(ListInvoices), new { storeId });
    }


    // change password

    [HttpGet("changepassword")]
    public async Task<IActionResult> ChangePassword(string storeId)
    {
        var vali = await validateStoreAndUser(storeId, true);
        if (vali.ErrorActionResult != null)
            return vali.ErrorActionResult;

        var model = new PublicChangePasswordViewModel
        {
            StoreId = vali.Store.Id,
            StoreName = vali.Store.StoreName,
            StoreBranding = await StoreBrandingViewModel.CreateAsync(Request, uriResolver, vali.Store.GetStoreBlob())
        };

        return View(model);
    }

    [HttpPost("changepassword")]
    public async Task<IActionResult> ChangePassword(string storeId, PublicChangePasswordViewModel model)
    {
        var vali = await validateStoreAndUser(storeId, true);
        if (vali.ErrorActionResult != null)
            return vali.ErrorActionResult;

        model.StoreId = vali.Store.Id;
        model.StoreName = vali.Store.StoreName;
        model.StoreBranding = await StoreBrandingViewModel.CreateAsync(Request, uriResolver, vali.Store.GetStoreBlob());

        if (!ModelState.IsValid)
            return View(model);

        await using var dbPlugins = payrollPluginDbContextFactory.CreateContext();
        var userInDb = dbPlugins.PayrollUsers.SingleOrDefault(a =>
            a.StoreId == storeId && a.Id == vali.UserId);
        if (userInDb == null)
            ModelState.AddModelError(nameof(model.CurrentPassword), "Invalid password");

        if (!hasher.IsValidPassword(userInDb, model.CurrentPassword))
            ModelState.AddModelError(nameof(model.CurrentPassword), "Invalid password");

        if (!ModelState.IsValid)
            return View(model);



        // 
        userInDb!.Password = hasher.HashPassword(vali.UserId, model.NewPassword);
        await dbPlugins.SaveChangesAsync();

        //
        TempData.SetStatusMessageModel(new StatusMessageModel()
        {
            Message = $"Password successfully changed",
            Severity = StatusMessageModel.StatusSeverity.Success
        });
        return RedirectToAction(nameof(ListInvoices), new { storeId });
    }
    
    //
    

    [HttpGet("delete/{id}")]
    public async Task<IActionResult> Delete(string storeId, string id)
    {
        var vali = await validateStoreAndUser(storeId, true);
        if (vali.ErrorActionResult != null)
            return vali.ErrorActionResult;

        await using var ctx = payrollPluginDbContextFactory.CreateContext();
        PayrollInvoice invoice = ctx.PayrollInvoices.Include(c => c.User)
            .SingleOrDefault(a => a.Id == id);

        if (invoice == null)
            return NotFound();

        if (invoice.State != PayrollInvoiceState.AwaitingApproval)
        {
            TempData.SetStatusMessageModel(new StatusMessageModel
            {
                Message = $"Invoice cannot be deleted as it has been actioned upon",
                Severity = StatusMessageModel.StatusSeverity.Error
            });
            return RedirectToAction(nameof(ListInvoices), new { storeId = vali.Store.Id });
        }
        return View("Confirm", new ConfirmModel($"Delete Invoice", $"Do you really want to delete the invoice for {invoice.Amount} {invoice.Currency} from {invoice.User.Name}?", "Delete"));
    }

    [HttpPost("delete/{id}")]
    public async Task<IActionResult> DeletePost(string storeId, string id)
    {
        var vali = await validateStoreAndUser(storeId, true);
        if (vali.ErrorActionResult != null)
            return vali.ErrorActionResult;

        await using var ctx = payrollPluginDbContextFactory.CreateContext();

        var invoice = ctx.PayrollInvoices.Single(a => a.Id == id);

        if (invoice.State != PayrollInvoiceState.AwaitingApproval)
        {
            TempData.SetStatusMessageModel(new StatusMessageModel
            {
                Message = $"Invoice cannot be deleted as it has been actioned upon",
                Severity = StatusMessageModel.StatusSeverity.Error
            });
            return RedirectToAction(nameof(ListInvoices), new { storeId = storeId });
        }

        ctx.Remove(invoice);
        await ctx.SaveChangesAsync();

        TempData.SetStatusMessageModel(new StatusMessageModel()
        {
            Message = $"Invoice deleted successfully",
            Severity = StatusMessageModel.StatusSeverity.Success
        });
        return RedirectToAction(nameof(ListInvoices), new { storeId = storeId });
    } 
}