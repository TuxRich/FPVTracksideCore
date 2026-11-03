using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Timing.ImmersionRC
{
    public class LapRFTimingEthernet : LapRFTiming
    {
        private Socket sock;
        private AutoResetEvent autoResetEvent;

        public LapRFSettingsEthernet LapRFSettingsEthernet { get { return settings as LapRFSettingsEthernet; } }

        public LapRFTimingEthernet()
        {
            settings = new LapRFSettingsEthernet();
            autoResetEvent = new AutoResetEvent(true);
            sock = null;
        }

        public override bool Connect()
        {
            base.Connect();

            try
            {
                IPAddress[] IPs = Dns.GetHostAddresses(LapRFSettingsEthernet.HostName);

                sock = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

                // Given a second. One still going then is dropped, with the socket closed under it, and its failure is
                // observed, or .NET logs an unobserved task exception for every attempt.
                Task connecting = sock.ConnectAsync(IPs[0], LapRFSettingsEthernet.Port);
                if (connecting.Wait(1000))
                {
                    if (sock != null && sock.Connected)
                    {
                        StartThreads();

                        connectionCount++;
                        Connected = true;
                        lastData = DateTime.Now;
                        return true;
                    }
                }
                else
                {
                    connecting.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                }
            }
            catch
            {
                // doo nothing, just fall through to cleanup.
            }

            if (sock != null)
            {
                sock.Dispose();
            }

            sock = null;
            Connected = false;

            return false;
        }

        public override bool Disconnect()
        {
            base.Disconnect();

            if (sock != null)
            {
                sock.Dispose();
                sock = null;
            }

            return true;
        }


        protected override int Recv(byte[] rxBuf)
        {
            Socket sockCopy = sock;
            try
            {
                if (sockCopy == null || !sockCopy.Connected)
                {
                    Connected = false;
                    return 0;
                }
                if (sockCopy.Available > 0)
                {
                    int numBytes = sock.Receive(rxBuf);
                    lastData = DateTime.Now;
                    autoResetEvent.Set();
                    return numBytes;
                }
            }
            catch (Exception e)
            {
                Tools.Logger.TimingLog.LogException(this, e);
            }
            return 0;
        }

        protected override bool Send(byte[] data)
        {
            Socket sockCopy = sock;
            if (sockCopy != null)
            {
                lock (sockCopy)
                {
                    try
                    {
                        int sent = sockCopy.Send(data);
                        if (sent == data.Length)
                        {
                            base.Send(data);
                            return autoResetEvent.WaitOne(2000);
                        }
                    }
                    catch
                    {
                        Connected = false;
                        return false;
                    }
                }
            }
            Connected = false;
            return false;
        }
    }
}
