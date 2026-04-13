using Playnite.SDK;
using SunshineLibrary.Models;
using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace SunshineLibrary.Services.Hosts
{
    /// <summary>
    /// Step-by-step host connection probe feeding the "Test Connection" button
    /// in the Add/Edit Host dialog. Each step reports success/fail to the UI so
    /// the user can see which step failed — far more diagnostic than a single
    /// "couldn't connect" error (PLAN §9).
    /// </summary>
    public class TestConnectionService
    {
        private static readonly ILogger logger = LogManager.GetLogger();

        public enum Step
        {
            Dns,
            Tcp,
            Certificate,
            PinMatch,
            Authentication,
            ListApps,
            ServerTypeDetect,
        }

        public class StepResult
        {
            public Step Step { get; set; }
            public bool Ok { get; set; }
            public string Message { get; set; }       // localized-ready key or free text
            public string Data { get; set; }          // step-specific (fingerprint, flavor, count)
        }

        public class Outcome
        {
            public bool Success { get; set; }
            public string ObservedSpkiSha256 { get; set; }
            public string CertSubject { get; set; }
            public DateTime? CertNotAfter { get; set; }
            public ServerType DetectedServerType { get; set; } = ServerType.Unknown;
            public int AppCount { get; set; }
            public List<StepResult> Steps { get; set; } = new List<StepResult>();
        }

        public async Task<Outcome> RunAsync(HostConfig host, IProgress<StepResult> progress, CancellationToken ct)
        {
            var outcome = new Outcome();

            // 1. DNS — resolve the host (Dns.GetHostEntry) if it's a name, skip if IP literal.
            var dnsStep = new StepResult { Step = Step.Dns };
            try
            {
                if (System.Net.IPAddress.TryParse(host.Address, out var _))
                {
                    dnsStep.Ok = true;
                    dnsStep.Message = "IP literal";
                }
                else
                {
                    var entry = await Task.Run(() => System.Net.Dns.GetHostEntry(host.Address), ct).ConfigureAwait(false);
                    dnsStep.Ok = entry?.AddressList?.Length > 0;
                    dnsStep.Message = dnsStep.Ok ? $"resolved to {entry.AddressList[0]}" : "no addresses";
                }
            }
            catch (Exception ex)
            {
                dnsStep.Ok = false;
                dnsStep.Message = ex.Message;
            }
            Report(progress, outcome, dnsStep);
            if (!dnsStep.Ok) return outcome;

            // 2. TCP connect to port.
            var tcpStep = new StepResult { Step = Step.Tcp };
            try
            {
                using (var tcp = new TcpClient())
                {
                    var connect = tcp.ConnectAsync(host.Address, host.Port);
                    var delay = Task.Delay(TimeSpan.FromSeconds(5), ct);
                    var done = await Task.WhenAny(connect, delay).ConfigureAwait(false);
                    if (done != connect) throw new TimeoutException($"TCP timed out on port {host.Port}");
                    await connect.ConfigureAwait(false);
                    tcpStep.Ok = tcp.Connected;
                    tcpStep.Message = "connected";
                }
            }
            catch (Exception ex)
            {
                tcpStep.Ok = false;
                tcpStep.Message = ex.Message;
            }
            Report(progress, outcome, tcpStep);
            if (!tcpStep.Ok) return outcome;

            // 3. Fetch leaf certificate.
            var certStep = new StepResult { Step = Step.Certificate };
            var certResult = await CertProbe.FetchLeafCertAsync(host.Address, host.Port, TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            if (!certResult.Success)
            {
                certStep.Ok = false;
                certStep.Message = certResult.ErrorMessage;
                Report(progress, outcome, certStep);
                return outcome;
            }
            certStep.Ok = true;
            certStep.Data = certResult.SpkiSha256;
            certStep.Message = $"subject={certResult.Subject}";
            outcome.ObservedSpkiSha256 = certResult.SpkiSha256;
            outcome.CertSubject = certResult.Subject;
            outcome.CertNotAfter = certResult.NotAfter;
            Report(progress, outcome, certStep);

            // 4. Pin match — informational here. Caller decides whether to show the
            //    pin-confirm dialog when pin is absent or mismatched.
            var pinStep = new StepResult { Step = Step.PinMatch };
            if (string.IsNullOrEmpty(host.CertFingerprintSpkiSha256))
            {
                pinStep.Ok = false;
                pinStep.Message = "no pin stored (first connect)";
            }
            else if (string.Equals(host.CertFingerprintSpkiSha256, certResult.SpkiSha256, StringComparison.OrdinalIgnoreCase))
            {
                pinStep.Ok = true;
                pinStep.Message = "pin matches";
            }
            else
            {
                pinStep.Ok = false;
                pinStep.Message = "pin mismatch";
            }
            Report(progress, outcome, pinStep);

            // 5–7. Full HTTP probe (auth + apps + flavor). Requires a pin, so we
            //      temporarily set the observed fingerprint if none is stored — the
            //      caller is responsible for persisting it only after user confirm.
            var probeHost = new HostConfig
            {
                Id = host.Id,
                Label = host.Label,
                Address = host.Address,
                Port = host.Port,
                AdminUser = host.AdminUser,
                AdminPassword = host.AdminPassword,
                CertFingerprintSpkiSha256 = string.IsNullOrEmpty(host.CertFingerprintSpkiSha256)
                    ? certResult.SpkiSha256
                    : host.CertFingerprintSpkiSha256,
                ServerType = host.ServerType,
                Enabled = true,
                ExcludedAppNames = host.ExcludedAppNames,
                Defaults = host.Defaults,
            };

            using (var client = HostClientFactory.Create(probeHost))
            {
                // Server-type probe first — /api/config. Cheap, confirms auth works.
                var flavorStep = new StepResult { Step = Step.ServerTypeDetect };
                var serverType = await HostClientFactory.ProbeServerTypeAsync(client, ct).ConfigureAwait(false);
                outcome.DetectedServerType = serverType;
                flavorStep.Ok = serverType != ServerType.Unknown;
                flavorStep.Message = serverType.ToString();
                flavorStep.Data = serverType.ToString();
                Report(progress, outcome, flavorStep);

                // List apps — full end-to-end success signal.
                HostClient typed = serverType != ServerType.Unknown
                    ? HostClientFactory.Create(new HostConfig
                    {
                        Id = probeHost.Id,
                        Label = probeHost.Label,
                        Address = probeHost.Address,
                        Port = probeHost.Port,
                        AdminUser = probeHost.AdminUser,
                        AdminPassword = probeHost.AdminPassword,
                        CertFingerprintSpkiSha256 = probeHost.CertFingerprintSpkiSha256,
                        ServerType = serverType,
                        Enabled = true,
                    })
                    : client;

                try
                {
                    var listStep = new StepResult { Step = Step.ListApps };
                    var apps = await typed.ListAppsAsync(ct).ConfigureAwait(false);
                    if (apps.IsOk)
                    {
                        listStep.Ok = true;
                        outcome.AppCount = apps.Value?.Count ?? 0;
                        listStep.Message = $"{outcome.AppCount} apps";
                        Report(progress, outcome, listStep);
                    }
                    else
                    {
                        listStep.Ok = false;
                        listStep.Message = apps.Kind.ToString();
                        if (apps.Kind == HostResultKind.AuthFailed)
                        {
                            var authStep = new StepResult { Step = Step.Authentication, Ok = false, Message = "401 Unauthorized" };
                            Report(progress, outcome, authStep);
                        }
                        Report(progress, outcome, listStep);
                        return outcome;
                    }
                }
                finally
                {
                    if (!ReferenceEquals(typed, client)) typed.Dispose();
                }
            }

            outcome.Success = true;
            return outcome;
        }

        private static void Report(IProgress<StepResult> progress, Outcome outcome, StepResult step)
        {
            outcome.Steps.Add(step);
            progress?.Report(step);
        }
    }
}
