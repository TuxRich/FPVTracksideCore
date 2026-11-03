using System;
using System.ComponentModel;

namespace Timing.RaceTimer
{
    // Which of the gate's passes count as laps. The gate gives each pass a verdict from its two receivers.
    public enum RaceTimerLaps
    {
        // VALID: both receivers saw the pass, at levels close enough to each other: it went through the gate.
        ThroughTheGate,

        // VALID, OVER or UNDER: both receivers saw it, wherever it went.
        BothReceivers,

        // Every pass, including the ones only one receiver saw (SINGLE_TOP, SINGLE_BOTTOM).
        EitherReceiver
    }

    public class RaceTimerSettings : TimingSystemSettings
    {
        [Category("Network")]
        public string HostName { get; set; }

        [Category("Network")]
        public int Port { get; set; }

        // Through the gate is how the gate is meant to time. The others let it count laps while its receivers are
        // still being placed, when many passes are seen by one receiver only.
        [Category("Laps")]
        [DisplayName("Count laps")]
        public RaceTimerLaps CountLaps { get; set; }

        public RaceTimerSettings()
        {
            HostName = "racegate-XXXX.local";
            Port = 80;
            CountLaps = RaceTimerLaps.ThroughTheGate;
        }

        // The gate's address, from a host name or IP. Also takes one pasted from the browser, such as
        // "http://racegate-51A0.local/" or "192.168.1.50:8058".
        public Uri GetUri(string scheme, string path)
        {
            string host = (HostName ?? "").Trim();
            int port = Port;

            int schemeEnd = host.IndexOf("://");
            if (schemeEnd >= 0)
            {
                host = host.Substring(schemeEnd + 3);
            }

            int slash = host.IndexOf('/');
            if (slash >= 0)
            {
                host = host.Substring(0, slash);
            }

            // One colon is a port. More is an IPv6 address.
            int colon = host.IndexOf(':');
            if (colon > 0 && colon == host.LastIndexOf(':') && int.TryParse(host.Substring(colon + 1), out int hostPort))
            {
                host = host.Substring(0, colon);
                port = hostPort;
            }

            return new UriBuilder(scheme, host, port, path).Uri;
        }
    }
}
