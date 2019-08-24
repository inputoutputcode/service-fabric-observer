// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Fabric;
using System.Fabric.Health;

namespace FabricObserver.Utilities
{
    /// <summary>
    /// Reports health data to Service Fabric Health Manager and logs locally (optional)...
    /// </summary>
    public class ObserverHealthReporter
    {
        private readonly Logger Logger;
        private FabricClient fabricClient = null;

        public ObserverHealthReporter(Logger logger)
        {
            this.fabricClient = ObserverManager.FabricClientInstance;
            this.fabricClient.Settings.HealthReportSendInterval = TimeSpan.FromSeconds(1);
            this.fabricClient.Settings.HealthReportRetrySendInterval = TimeSpan.FromSeconds(3);
            Logger = logger;
        }

        /// <summary>
        /// Report FabricObserver service health as log event (not to SF Health)...
        /// </summary>
        /// <param name="serviceName">Name of the service</param>
        /// <param name="propertyName">Name of the health property</param>
        /// <param name="healthState">Health state (Ok, Error, etc)</param>
        /// <param name="description">Description of the health condition</param>
        public void ReportFabricObserverServiceHealth(string serviceName,
                                                      string propertyName,
                                                      HealthState healthState,
                                                      string description)
        {
            if (healthState == HealthState.Error)
            {
                this.Logger.LogError("FabricObserver service health error: " + serviceName + " | " + propertyName + "{0}", description);
            }

            if (healthState == HealthState.Warning)
            {
                this.Logger.LogWarning("FabricObserver service health warning: " + serviceName + " | " + propertyName + "{0}", description);
            }
        }

        public void ReportHealthToServiceFabric(Utilities.HealthReport healthReport)
        {
            if (healthReport == null)
            {
                return;
            }

            var sendOptions = new HealthReportSendOptions { Immediate = false };
            var timeToLive = TimeSpan.FromMinutes(1);

            if (healthReport.HealthReportTimeToLive != default(TimeSpan))
            {
                timeToLive = healthReport.HealthReportTimeToLive;
            }
            // Errors will block SF infra from doing things like upgrades...
            // We probably should be careful here and limit health events
            // to warnings only (we'll dump on Error state if the user specifies this...)
            // OR we Error on Error... -CT
            if (healthReport.State == HealthState.Error || healthReport.State == HealthState.Ok)
            {
                sendOptions.Immediate = true;
                if (healthReport.HealthReportTimeToLive == default(TimeSpan))
                {
                    timeToLive = TimeSpan.FromMinutes(5);
                }
            }

            string property = "", kind = "";

            if (healthReport.Code != null)
            {
                kind = healthReport.Code + ": ";
            }

            switch (healthReport.Observer)
            {
                case ObserverConstants.AppObserverName:
                    property = "AppResourceUsage";
                    break;
                case ObserverConstants.CertificateObserverName:
                    property = "Certificates";
                    break;
                case ObserverConstants.DiskObserverName:
                    property = "DiskHealth";
                    break;
                case ObserverConstants.FabricSystemObserverName:
                    property = "FabricServices";
                    break;
                case ObserverConstants.SFConfigurationObserverName:
                    property = "SFInfraConfig";
                    break;
                case ObserverConstants.NetworkObserverName:
                    property = "Networking";
                    break;
                case ObserverConstants.OSObserverName:
                    property = "OS";
                    break;
                case ObserverConstants.NodeObserverName:
                    property = "NodeResourceUsage";
                    break;
                default:
                    property = "Generic";
                    break;
            }

            var healthInformation = new HealthInformation(healthReport.Observer, property, healthReport.State)
            {
                Description = kind + healthReport.HealthMessage,
                TimeToLive = timeToLive,
                RemoveWhenExpired = true
            };

            // Log event...
            if (healthReport.EmitLogEvent)
            {
                if (healthReport.State == HealthState.Error)
                {
                    Logger.LogError(healthReport.NodeName + ": {0}", healthInformation.Description);
                }
                else if (healthReport.State == HealthState.Warning)
                {
                    Logger.LogWarning(healthReport.NodeName + ": {0}", healthInformation.Description);
                }
                else
                {
                    Logger.LogInfo(healthReport.NodeName + ": {0}", healthInformation.Description);
                }
            }

            // To SFX and Telemetry provider...
            if (healthReport.ReportType == HealthReportType.Application && healthReport.AppName != null)
            {
                var report = new ApplicationHealthReport(healthReport.AppName, healthInformation);
                this.fabricClient.HealthManager.ReportHealth(report, sendOptions);
            }
            else
            {
                var report = new NodeHealthReport(healthReport.NodeName, healthInformation);
                this.fabricClient.HealthManager.ReportHealth(report, sendOptions);
            }
        }
    }

    public enum HealthReportType
    {
        Application,
        Node
    }
}