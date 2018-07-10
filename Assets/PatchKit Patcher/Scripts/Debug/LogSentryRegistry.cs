﻿using System;
using System.Net;
using PatchKit.IssueReporting;
using SharpRaven;
using SharpRaven.Data;
using PatchKit.Apps.Updating;

namespace PatchKit.Patching.Unity.Debug
{
    public class LogSentryRegistry
    {
        private readonly RavenClient _ravenClient;

        public LogSentryRegistry()
        {
            ServicePointManager.ServerCertificateValidationCallback += (sender, certificate, chain, errors) => true; 
            
            _ravenClient
                = new RavenClient( 
                    "https://cb13d9a4a32f456c8411c79c6ad7be9d:90ba86762829401e925a9e5c4233100c@sentry.io/175617"); 
        }

        public void RegisterWithException(Issue issue, string logFileGuid)
        {
            var sentryEvent = new SentryEvent(issue.Exception)
            {
                Tags = issue.Tags
            };
            if (issue.Message != null)
            {
                sentryEvent.Message = issue.Message;
            }
            AddDataToSentryEvent(sentryEvent, logFileGuid);
            _ravenClient.Capture(sentryEvent);
        }
        
        public void RegisterWithException(Exception exception, string logFileGuid)
        {
            RegisterWithException(new Issue()
            {
                Exception = exception
            }, logFileGuid);
        }

        private static void AddDataToSentryEvent(SentryEvent sentryEvent, string logFileGuid)
        {
            sentryEvent.Exception.Data.Add("log-guid", logFileGuid);
            sentryEvent.Exception.Data.Add("log-link", string.Format(
                "https://s3-us-west-2.amazonaws.com/patchkit-app-logs/patcher-unity/{0:yyyy_MM_dd}/{1}.201-log.gz", DateTime.Now, logFileGuid));

            var patcher = Patcher.Instance;
            if (patcher != null)
            {
                if(patcher.Data.Value.AppSecret != null)
                {
                    sentryEvent.Tags.Add("app-secret", patcher.Data.Value.AppSecret);
                }
                if (patcher.LocalVersionId.Value.HasValue)
                {
                    sentryEvent.Exception.Data.Add("local-version", patcher.LocalVersionId.Value.ToString());
                }
                if (patcher.RemoteVersionId.Value.HasValue)
                {
                    sentryEvent.Exception.Data.Add("remote-version", patcher.RemoteVersionId.Value.ToString());
                }

                if (DependencyResolver.IsRegistered<ISystemInfoProvider>())
                {
                    var systemInfoProvider = DependencyResolver.Resolve<ISystemInfoProvider>();
                    sentryEvent.Tags.Add("system-info", systemInfoProvider.SystemInfo);
                }

                sentryEvent.Tags.Add("patcher-version", Version.Value);
            }

        }
    }
}