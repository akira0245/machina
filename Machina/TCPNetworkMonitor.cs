﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Machina
{
    /// <summary>
    /// TCPNetworkMonitor is configured through the following properties after it is constructed:
    ///   MonitorType: Specifies whether it should use a winsock raw socket, or use WinPCap (requires separate kernel driver installation).  Default is a raw socket.
    ///   ProcessID: Specifies the process ID to record traffic from
    ///   WindowName: Specifies the window name to record traffic from, where process ID is unavailable
    ///   DataReceived: Delegate that is called when data is received and successfully decoded through IP and TCP decoders.  Note that a connection identifier is 
    ///     supplied to distinguish between multiple connections from the same process.
    ///   DataSent: Delegate that is called when data is sent and successfully decoded through IP and TCP decoders.  Note that a connection identifier is 
    ///     supplied to distinguish between multiple connections from the same process.
    ///     
    /// This class uses a long-running task to monitor the network data as it is received.  It also monitors the specified process for changes to its active
    ///   TCPIP connections and filters out all traffic not related to these connections
    ///   
    /// The stream of data sent to the remote server is considered separate from the stream of data sent by the remote server, and processed through different events.
    /// </summary>
    public class TCPNetworkMonitor
    {
        public enum NetworkMonitorType
        {
            RawSocket = 1,
            WinPCap = 2
        }

        /// <summary>
        /// Specifies the type of monitor to use - Raw socket or WinPCap
        /// </summary>
        public NetworkMonitorType MonitorType
        { get; set; } = NetworkMonitorType.RawSocket;

        /// <summary>
        /// Specifies the Process ID that is generating or receiving the traffic.  Either ProcessID or WindowName must be specified.
        /// </summary>
        public uint ProcessID
        { get; set; } = 0;
        /// <summary>
        /// Specifies the Window Name of the application that is generating or receiving the traffic.  Either ProcessID or WindowName must be specified.
        /// </summary>
        public string WindowName
        { get; set; } = "";

        #region Data Delegates section
        public delegate void DataReceivedDelegate(string connection, byte[] data);

        /// <summary>
        /// Specifies the delegate that is called when data is received and successfully decoded/
        /// </summary>
        public DataReceivedDelegate DataReceived = null;

        public void OnDataReceived(string connection, byte[] data)
        {
            DataReceived?.Invoke(connection, data);
        }
        
        public delegate void DataSentDelegate(string connection, byte[] data);

        public DataSentDelegate DataSent = null;

        public void OnDataSent(string connection, byte[] data)
        {
            DataSent?.Invoke(connection, data);
        }
        
        #endregion

        private RawSocket _socket = null;
        private RawPCap _winpcap = null;
        private uint _localAddress = 0;
        private List<TCPConnection> _connections = new List<TCPConnection>(2);


        private Task _monitorTask = null;
        private int _Abort = 0;
        private bool Abort
        {
            get
            {
                return _Abort != 0;
            }
            set
            {
                if (value)
                    Interlocked.Exchange(ref _Abort, 1);
                else
                    Interlocked.Exchange(ref _Abort, 0);
            }
        }

        private ProcessTCPInfo _processTCPInfo = new ProcessTCPInfo();

        /// <summary>
        /// Validates the parameters and starts the monitor.
        /// </summary>
        public void Start()
        {
            if (ProcessID == 0 && string.IsNullOrWhiteSpace(WindowName))
                throw new ArgumentException("Either Process ID or Window Name must be specified");
            if (DataReceived == null)
                throw new ArgumentException("DataReceived delegate must be specified.");

            _processTCPInfo.ProcessID = ProcessID;
            _processTCPInfo.ProcessWindowName = WindowName;

            _monitorTask = new Task(() => Run(), TaskCreationOptions.LongRunning);

            _monitorTask.Start();
        }

        /// <summary>
        /// Stops the monitor if it is active.
        /// </summary>
        public void Stop()
        {
            Abort = true;
            if (_monitorTask != null)
            {
                _monitorTask.Wait(5000);
                if (_monitorTask.Status == TaskStatus.Running)
                {
                    // todo: implement cancellation token to cancel task.
                }
                _monitorTask = null;
            }

            Cleanup();
        }

        private void Cleanup()
        {
            _connections.Clear();
            if (_socket != null)
            {
                _socket.Destroy();
                _socket = null;
            }
            if (_winpcap != null)
            {
                _winpcap.Destroy();
                _winpcap = null;
            }

            _localAddress = 0;
        }

        private void Run()
        {
            try
            {
                while (!Abort)
                {
                    UpdateProcessConnections();
                    if (_connections.Count == 0)
                    {
                        Thread.Sleep(1000);
                        continue;
                    }

                    CheckForIPChange();

                    ProcessNetworkData();

                    Thread.Sleep(10);
                }

                Cleanup();

                return;
            }
            catch (Exception ex)
            {
                Trace.WriteLine("TCPNetworkMonitor Error: " + ex.ToString());
            }

            Cleanup();
        }

        private void UpdateProcessConnections()
        {
            // get any active game connections
            _processTCPInfo.UpdateTCPIPConnections(_connections);
            if (_connections.Count == 0)
            {
                Cleanup();
                return;
            }

            for (int i = 0; i < _connections.Count; i++)
            {
                TCPConnection connection = _connections[i];
                if (string.IsNullOrWhiteSpace(connection.ID))
                {
                    connection.ID = connection.ToString(); //TODO: In the future there may be a better way to define the ID other than such a long string.

                    // Set up decoders for data sent from local machine
                    connection.IPDecoder_Send = new IPDecoder(connection.LocalIP, connection.RemoteIP, IPProtocol.TCP);
                    connection.TCPDecoder_Send = new TCPDecoder(connection.LocalPort, connection.RemotePort);

                    // set up decoders for data received by local machine
                    connection.IPDecoder_Receive = new IPDecoder(connection.RemoteIP, connection.LocalIP, IPProtocol.TCP);
                    connection.TCPDecoder_Receive = new TCPDecoder(connection.RemotePort, connection.LocalPort);

                    continue;
                }
            }
        }

        private void CheckForIPChange()
        {
            uint newLocalAddress = _connections.FirstOrDefault()?.LocalIP ?? 0;

            if (_localAddress != newLocalAddress)
            {
                Trace.WriteLine("TCPNetworkMonitor: listening on IP: " + new IPAddress(newLocalAddress).ToString());
                _localAddress = newLocalAddress;

                if (MonitorType == NetworkMonitorType.WinPCap)
                {
                    if (_winpcap != null)
                        _winpcap.Destroy();

                    _winpcap = new RawPCap();
                    _winpcap.Create(_localAddress);
                }
                else
                {
                    if (_socket != null)
                        _socket.Destroy();

                    _socket = new RawSocket();
                    _socket.Create(_localAddress);
                }
            }
        }

        private void ProcessNetworkData()
        {
            int size;
            byte[] buffer;

            if (MonitorType == NetworkMonitorType.WinPCap)
                while ((size = _winpcap.Receive(out buffer)) > 0)
                    ProcessData(buffer, size);
            else
                while ((size = _socket.Receive(out buffer)) > 0)
                    ProcessData(buffer, size);
        }


        private void ProcessData(byte[] buffer, int size)
        {
            byte[] tcpbuffer;
            byte[] payloadBuffer;
            for (int i = 0; i < _connections.Count; i++)
            {
                TCPConnection connection = _connections[i];
                connection.IPDecoder_Send.FilterAndStoreData(buffer, size);

                while ((tcpbuffer = connection.IPDecoder_Send.GetNextIPPayload()) != null)
                { 
                    connection.TCPDecoder_Send.FilterAndStoreData(tcpbuffer);
                    while ((payloadBuffer = connection.TCPDecoder_Send.GetNextTCPDatagram()) != null)
                    {
                        OnDataSent(connection.ID, payloadBuffer);
                    }
                }

                connection.IPDecoder_Receive.FilterAndStoreData(buffer, size);
                while ((tcpbuffer = connection.IPDecoder_Receive.GetNextIPPayload()) != null)
                {
                    connection.TCPDecoder_Receive.FilterAndStoreData(tcpbuffer);
                    while ((payloadBuffer = connection.TCPDecoder_Receive.GetNextTCPDatagram()) != null)
                    {
                        OnDataReceived(connection.ID, payloadBuffer);
                    }
                }
            }
        }

    }
}