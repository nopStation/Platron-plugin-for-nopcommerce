using System;
using System.Collections.Specialized;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Nop.Core;
using Nop.Core.Domain.Orders;
using Nop.Core.Domain.Payments;
using Nop.Plugin.Payments.Platron.Models;
using Nop.Services.Configuration;
using Nop.Services.Localization;
using Nop.Services.Logging;
using Nop.Services.Messages;
using Nop.Services.Orders;
using Nop.Services.Payments;
using Nop.Services.Security;
using Nop.Web.Framework;
using Nop.Web.Framework.Controllers;
using Nop.Web.Framework.Mvc.Filters;

namespace Nop.Plugin.Payments.Platron.Controllers
{
    public class PaymentPlatronController : BasePaymentController
    {
        private const string ORDER_DESCRIPTION = "Payment order #$orderId";

        private readonly ILocalizationService _localizationService;
        private readonly ILogger _logger;
        private readonly IOrderService _orderService;
        private readonly IOrderProcessingService _orderProcessingService;
        private readonly IPaymentService _paymentService;
        private readonly IPermissionService _permissionService;
        private readonly ISettingService _settingService;
        private readonly IStoreContext _storeContext;
        private readonly IWebHelper _webHelper;
        private readonly INotificationService _notificationService;
        private readonly IPaymentPluginManager _paymentPluginManager;

        public PaymentPlatronController(ILocalizationService localizationService,
            ILogger logger,
            IOrderService orderService,
            IOrderProcessingService orderProcessingService,
            IPaymentService paymentService,
            IPermissionService permissionService,
            ISettingService settingService,
            IStoreContext storeContext,
            IWebHelper webHelper,
            INotificationService notificationService,
            IPaymentPluginManager paymentPluginManager)
        {
            _storeContext = storeContext;
            _settingService = settingService;
            _paymentService = paymentService;
            _orderService = orderService;
            _orderProcessingService = orderProcessingService;
            _logger = logger;
            _localizationService = localizationService;
            _webHelper = webHelper;
            _notificationService = notificationService;
            _paymentPluginManager = paymentPluginManager;
            _permissionService = permissionService;
        }

        [AuthorizeAdmin]
        [Area(AreaNames.Admin)]
        public async Task<IActionResult> Configure()
        {
            if (!await _permissionService.AuthorizeAsync(StandardPermissionProvider.ManagePaymentMethods))
                return AccessDeniedView();

            //load settings for a chosen store scope
            var storeScope = await _storeContext.GetActiveStoreScopeConfigurationAsync();
            var platronPaymentSettings = await _settingService.LoadSettingAsync<PlatronPaymentSettings>(storeScope);

            if (!platronPaymentSettings.DescriptionTemplate.Any())
                platronPaymentSettings.DescriptionTemplate = ORDER_DESCRIPTION;

            var model = new ConfigurationModel
            {
                MerchantId = platronPaymentSettings.MerchantId,
                SecretKey = platronPaymentSettings.SecretKey,
                TestingMode = platronPaymentSettings.TestingMode,
                DescriptionTemplate = platronPaymentSettings.DescriptionTemplate,
                AdditionalFee = platronPaymentSettings.AdditionalFee,
                AdditionalFeePercentage = platronPaymentSettings.AdditionalFeePercentage,
                ActiveStoreScopeConfiguration = storeScope
            };

            if (storeScope > 0)
            {
                model.MerchantIdOverrideForStore = await _settingService.SettingExistsAsync(platronPaymentSettings, x => x.MerchantId, storeScope);
                model.SecretKeyOverrideForStore = await _settingService.SettingExistsAsync(platronPaymentSettings, x => x.SecretKey, storeScope);
                model.TestingModeOverrideForStore = await _settingService.SettingExistsAsync(platronPaymentSettings, x => x.TestingMode, storeScope);
                model.DescriptionTemplateOverrideForStore = await _settingService.SettingExistsAsync(platronPaymentSettings, x => x.DescriptionTemplate, storeScope);
                model.AdditionalFeeOverrideForStore = await _settingService.SettingExistsAsync(platronPaymentSettings, x => x.AdditionalFee, storeScope);
                model.AdditionalFeePercentageOverrideForStore = await _settingService.SettingExistsAsync(platronPaymentSettings, x => x.AdditionalFeePercentage, storeScope);
            }

            return View("~/Plugins/Payments.Platron/Views/Configure.cshtml", model);
        }

        [HttpPost]
        [AuthorizeAdmin]
        [Area(AreaNames.Admin)]
        public async Task<IActionResult> Configure(ConfigurationModel model)
        {
            if (!await _permissionService.AuthorizeAsync(StandardPermissionProvider.ManagePaymentMethods))
                return AccessDeniedView();

            //load settings for a chosen store scope
            var storeScope = await _storeContext.GetActiveStoreScopeConfigurationAsync();
            var platronPaymentSettings = await _settingService.LoadSettingAsync<PlatronPaymentSettings>(storeScope);

            //save settings
            platronPaymentSettings.MerchantId = model.MerchantId;
            platronPaymentSettings.SecretKey = model.SecretKey;
            platronPaymentSettings.TestingMode = model.TestingMode;
            platronPaymentSettings.DescriptionTemplate = model.DescriptionTemplate;
            platronPaymentSettings.AdditionalFee = model.AdditionalFee;
            platronPaymentSettings.AdditionalFeePercentage = model.AdditionalFeePercentage;

            /* We do not clear cache after each setting update.
             * This behavior can increase performance because cached settings will not be cleared 
             * and loaded from database after each update */
            await _settingService.SaveSettingOverridablePerStoreAsync(platronPaymentSettings, x => x.MerchantId, model.MerchantIdOverrideForStore, storeScope, false);
            await _settingService.SaveSettingOverridablePerStoreAsync(platronPaymentSettings, x => x.SecretKey, model.SecretKeyOverrideForStore, storeScope, false);
            await _settingService.SaveSettingOverridablePerStoreAsync(platronPaymentSettings, x => x.TestingMode, model.TestingModeOverrideForStore, storeScope, false);
            await _settingService.SaveSettingOverridablePerStoreAsync(platronPaymentSettings, x => x.DescriptionTemplate, model.DescriptionTemplateOverrideForStore, storeScope, false);
            await _settingService.SaveSettingOverridablePerStoreAsync(platronPaymentSettings, x => x.AdditionalFee, model.AdditionalFeeOverrideForStore, storeScope, false);
            await _settingService.SaveSettingOverridablePerStoreAsync(platronPaymentSettings, x => x.AdditionalFeePercentage, model.AdditionalFeePercentageOverrideForStore, storeScope, false);

            //now clear settings cache
            await _settingService.ClearCacheAsync();

            _notificationService. SuccessNotification(await _localizationService.GetResourceAsync("Admin.Plugins.Saved"));

            return RedirectToAction("Configure");
        }

        private async Task<IActionResult> GetResponseAsync(string textToResponse, PlatronPaymentProcessor processor, bool success = false)
        {
            var status = success ? "ok" : "error";
            if (!success)
                await _logger.ErrorAsync($"Platron. {textToResponse}");

            var postData = new NameValueCollection
            {
                { "pg_status", status },
                { "pg_salt", CommonHelper.GenerateRandomDigitCode(8) }
            };
            if (!success)
                postData.Add("pg_error_description", textToResponse);

            postData.Add("pg_sig", processor.GetSignature(processor.GetScriptName(Request.Path), postData));

            const string rez = "<?xml version=\"1.0\" encoding=\"utf - 8\"?><response>{0}</response>";

            var content = postData.AllKeys.Select(key => string.Format("<{0}>{1}</{0}>", key, postData[key])).Aggregate(string.Empty, (all, curent) => all + curent);

            return Content(string.Format(rez, content), "text/xml", Encoding.UTF8);
        }

        private string GetValue(string key, IFormCollection form)
        {
            return (form.Keys.Contains(key) ? form[key].ToString() : _webHelper.QueryString<string>(key)) ?? string.Empty;
        }

        private async Task UpdateOrderStatusAsync(Order order, string status)
        {
            status = status.ToLower();

            switch (status)
            {
                case "failed":
                case "revoked":
                    {
                        //mark order as canceled
                        if ((order.PaymentStatus == PaymentStatus.Paid || order.PaymentStatus == PaymentStatus.Authorized) &&
                            _orderProcessingService.CanCancelOrder(order))
                            await _orderProcessingService.CancelOrderAsync(order, true);
                    }
                    break;
                case "ok":
                    {
                        //mark order as paid
                        if (_orderProcessingService.CanMarkOrderAsPaid(order) && status.ToUpper() == "PAID")
                            await _orderProcessingService.MarkOrderAsPaidAsync(order);
                    }
                    break;
            }
        }

        public async Task<IActionResult> ConfirmPay(IFormCollection form)
        {
            var processor = await GetPaymentProcessorAsync();

            const string orderIdKey = "pg_order_id";
            const string signatureKey = "pg_sig";
            const string resultKey = "pg_result";

            var orderId = GetValue(orderIdKey, form);
            var signature = GetValue(signatureKey, form);
            var result = GetValue(resultKey, form);

            Order order = null;

            if (Guid.TryParse(orderId, out Guid orderGuid))
            {
                order = await _orderService.GetOrderByGuidAsync(orderGuid);
            }

            if (order == null)
                return await GetResponseAsync("Order cannot be loaded", processor);

            var sb = new StringBuilder();
            sb.AppendLine("Platron:");
            foreach (var key in form.Keys)
            {
                sb.AppendLine(key + ": " + form[key]);
            }

            //order note
            await _orderService.InsertOrderNoteAsync(new OrderNote
            {
                Note = sb.ToString(),
                DisplayToCustomer = false,
                CreatedOnUtc = DateTime.UtcNow
            });

            var postData = new NameValueCollection();
            foreach (var keyValuePair in form.Where(pair => !pair.Key.Equals(signatureKey, StringComparison.InvariantCultureIgnoreCase)))
            {
                postData.Add(keyValuePair.Key, keyValuePair.Value);
            }

            var checkDataString = processor.GetSignature(processor.GetScriptName(Url.ToString()), postData);

            if (checkDataString != signature)
                return await GetResponseAsync("Invalid order data", processor);

            if (result == "0")
                return await GetResponseAsync("The payment has been canceled", processor, true);

            //mark order as paid
            if (_orderProcessingService.CanMarkOrderAsPaid(order))
            {
                await _orderProcessingService.MarkOrderAsPaidAsync(order);
            }

            return await GetResponseAsync("The order has been paid", processor, true);
        }

        private async Task<PlatronPaymentProcessor> GetPaymentProcessorAsync()
        {
            var processor =
                await _paymentPluginManager.LoadPluginBySystemNameAsync("Payments.Platron") as PlatronPaymentProcessor;
            if (processor == null ||
                !_paymentPluginManager.IsPluginActive(processor) || !processor.PluginDescriptor.Installed)
                throw new NopException("Platron module cannot be loaded");
            return processor;
        }

        public async Task<IActionResult> Success()
        {
            var orderId = _webHelper.QueryString<string>("pg_order_id");
            Order order = null;

            if (Guid.TryParse(orderId, out Guid orderGuid))
                order = await _orderService.GetOrderByGuidAsync(orderGuid);

            if (order == null)
                return RedirectToAction("Index", "Home", new { area = string.Empty });

            //update payment status if need
            if (order.PaymentStatus == PaymentStatus.Paid)
            {
                var status = (await GetPaymentProcessorAsync()).GetPaymentStatus(orderId);
                if (status[0].ToLower() == "ok")
                    await UpdateOrderStatusAsync(order, status[1]);
            }

            return RedirectToRoute("CheckoutCompleted", new { orderId = order.Id });
        }

        public async Task<IActionResult> CancelOrder()
        {
            var orderId = _webHelper.QueryString<string>("pg_order_id");
            Order order = null;

            if (Guid.TryParse(orderId, out Guid orderGuid))
                order = await _orderService.GetOrderByGuidAsync(orderGuid);

            if (order == null)
                return RedirectToAction("Index", "Home", new { area = string.Empty });

            //update payment status if need
            if (order.PaymentStatus != PaymentStatus.Voided)
            {
                var status = (await GetPaymentProcessorAsync()).GetPaymentStatus(orderId);
                if (status[0].ToLower() == "ok")
                    await UpdateOrderStatusAsync(order, status[1]);
            }

            return RedirectToRoute("OrderDetails", new { orderId = order.Id });
        }
    }
}