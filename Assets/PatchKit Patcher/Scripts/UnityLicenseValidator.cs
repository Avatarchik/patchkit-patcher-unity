﻿using System;
using JetBrains.Annotations;
using PatchKit.Api;
using PatchKit.Logging;
using PatchKit.Patching.AppData.Local;
using PatchKit.Patching.Licensing;
using PatchKit.Patching.Unity.UI.Dialogs;

namespace PatchKit.Patching.Unity
{
    public class UnityLicenseValidator
    {
        [NotNull] private readonly ILicenseDialog _licenseDialog;
        [NotNull] private readonly ILocalMetaData _localMetaData;
        [NotNull] private readonly ILogger _logger;
        [NotNull] private readonly IKeysAppLicenseAuthorizer _keysAppLicenseAuthorizer;

        public UnityLicenseValidator([NotNull] ILicenseDialog licenseDialog,
            [NotNull] ILocalMetaData localMetaData)
        {
            if (licenseDialog == null)
            {
                throw new ArgumentNullException("licenseDialog");
            }

            if (localMetaData == null)
            {
                throw new ArgumentNullException("localMetaData");
            }

            _licenseDialog = licenseDialog;
            _localMetaData = localMetaData;
            _logger = DependencyResolver.Resolve<ILogger>();
            _keysAppLicenseAuthorizer = DependencyResolver.Resolve<IKeysAppLicenseAuthorizer>();
        }

        public void Validate(string appSecret)
        {
            try
            {
                _logger.LogDebug("Validating license...");

                var messageType = LicenseDialogMessageType.None;

                var cachedKey = GetCachedKey();
                _logger.LogTrace("Cached key = " + cachedKey);

                bool didUseCachedKey = false;

                while (!AppLicense.HasValue)
                {
                    bool isUsingCachedKey;
                    string key = GetKey(messageType, cachedKey, out isUsingCachedKey, ref didUseCachedKey);

                    try
                    {
                        _logger.LogTrace("Key = " + key);

                        _logger.LogDebug("Validating key...");

                        AppLicense = _keysAppLicenseAuthorizer.Authorize(appSecret, key);

                        _logger.LogDebug("License has been validated!");

                        _logger.LogTrace("KeySecret = " + AppLicense.Value.Secret);

                        _logger.LogDebug("Saving key and key secret to cache.");
                        SetCachedKey(key);
                    }
                    catch (InvalidLicenseException invalidLicenseException)
                    {
                        _logger.LogWarning(
                            "Key validation failed due to invalid license. Setting license dialog message to InvalidLicense",
                            invalidLicenseException);

                        HandleApiError(ref messageType, isUsingCachedKey, LicenseDialogMessageType.InvalidLicense);
                    }
                    catch (BlockedLicenseException blockedLicenseException)
                    {
                        _logger.LogWarning(
                            "Key validation failed due to blocked license. Setting license dialog message to BlockedLicense",
                            blockedLicenseException);

                        HandleApiError(ref messageType, isUsingCachedKey, LicenseDialogMessageType.BlockedLicense);
                    }
                    catch (ApiResponseException apiResponseException)
                    {
                        _logger.LogWarning(
                            "Key validation failed due to connection issues with API server. Setting license dialog message to ServiceUnavailable",
                            apiResponseException);
                        messageType = LicenseDialogMessageType.ServiceUnavailable;
                    }
                    catch (ApiConnectionException apiConnectionException)
                    {
                        _logger.LogWarning(
                            "Key validation failed due to connection issues with API server. Setting license dialog message to ServiceUnavailable",
                            apiConnectionException);
                        messageType = LicenseDialogMessageType.ServiceUnavailable;
                    }
                }
            }
            catch (Exception e)
            {
                _logger.LogError("Validating license has failed.", e);
                throw;
            }
        }

        private string GetKey(LicenseDialogMessageType messageType, string cachedKey, out bool isUsingCachedKey,
            ref bool didUseCachedKey)
        {
            bool isCachedKeyAvailable = !string.IsNullOrEmpty(cachedKey);

            if (isCachedKeyAvailable && !didUseCachedKey)
            {
                _licenseDialog.SetKey(cachedKey);
                didUseCachedKey = true;
                isUsingCachedKey = true;

                return cachedKey;
            }

            isUsingCachedKey = false;

            return GetKeyFromDialog(messageType);
        }

        private string GetKeyFromDialog(LicenseDialogMessageType messageType)
        {
            _logger.LogDebug("Displaying license dialog...");

            var result = _licenseDialog.Display(messageType);

            _logger.LogDebug("License dialog has returned result.");

            _logger.LogTrace("result.Key = " + result.Key);
            _logger.LogTrace(string.Format("result.Type = {0}", result.Type));

            switch (result.Type)
            {
                case LicenseDialogResultType.Confirmed:
                    _logger.LogDebug("Using key typed in license dialog.");
                    return result.Key;
                case LicenseDialogResultType.Aborted:
                    _logger.LogDebug("License dialog has been aborted. Cancelling operation.");
                    throw new OperationCanceledException();
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        private void HandleApiError(ref LicenseDialogMessageType messageType, bool isUsingCachedKey,
            LicenseDialogMessageType licenseDialogMessageType)
        {
            if (!isUsingCachedKey)
            {
                _logger.LogDebug(string.Format("Setting license dialog message to {0}", licenseDialogMessageType));
                messageType = licenseDialogMessageType;
            }
            else
            {
                _logger.LogDebug(
                    "Ignoring API error - the attempt was done with cached key. Prompting user to enter new license key.");
            }
        }

        public AppLicense? AppLicense;

        private void SetCachedKey(string value)
        {
            _localMetaData.SetProductKey(value);
        }

        private string GetCachedKey()
        {
            return _localMetaData.GetProductKey();
        }
    }
}