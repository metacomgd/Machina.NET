﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Machina.Drivers.Communication.Protocols;

namespace Machina.Drivers.Communication
{
    /// <summary>
    /// A class that manages TCP communication with ABB devices, including sending/receiving messages, 
    /// queuing them, releasing them to the TCP server when appropriate, and raising events on 
    /// buffer empty.
    /// </summary>
    internal class TCPCommunicationManagerABB
    {
        internal TCPConnectionStatus Status { get; private set; }

        private RobotCursor _writeCursor;
        private RobotCursor _motionCursor;
        private Driver _parentDriver;
        internal RobotLogger logger;

        private const int INIT_TIMEOUT = 10000;  // in millis
        internal Vector initPos;
        internal Rotation initRot;
        internal Joints initAx;
        internal ExternalAxes initExtAx;

        private TcpClient clientSocket = new TcpClient();
        private NetworkStream clientNetworkStream;
        private Thread receivingThread;
        private Thread sendingThread;
        private string _ip = "";
        public string IP => _ip;
        private int _port = 0;
        public int Port => _port;
        private bool _isDeviceBufferFull = false;

        private Protocols.Base _translator;
        private List<string> _messageBuffer = new List<string>();
        private byte[] _sendMsgBytes;
        private byte[] _receiveMsgBytes = new byte[1024];
        private int _receiveByteCount;
        private string _response;
        private string[] _responseChunks;

        private int _sentMessages = 0;
        private int _receivedMessages = 0;
        private int _maxStreamCount = 10;
        private int _sendNewBatchOn = 2;

        private bool _bufferEmptyEventIsRaiseable = true;

        private string _deviceDriverVersion = null;


        internal TCPCommunicationManagerABB(Driver driver, RobotCursor writeCursor, RobotCursor motionCursor, string ip, int port)
        {
            this._parentDriver = driver;
            this.logger = driver.parentControl.logger;
            this._writeCursor = writeCursor;
            this._motionCursor = motionCursor;
            this._ip = ip;
            this._port = port;

            this._translator = Protocols.Factory.GetTranslator(this._parentDriver);
        }

        internal bool Disconnect()
        {

            if (clientSocket != null)
            {
                Status = TCPConnectionStatus.Disconnected;
                clientSocket.Client.Disconnect(false);
                clientSocket.Close();
                if (clientNetworkStream != null) clientNetworkStream.Dispose();
                return true;
            }

            return false;
        }

        internal bool Connect()
        {
            try
            {
                clientSocket = new TcpClient();
                clientSocket.Connect(this._ip, this._port);
                Status = TCPConnectionStatus.Connected;
                clientNetworkStream = clientSocket.GetStream();
                clientSocket.ReceiveBufferSize = 1024;
                clientSocket.SendBufferSize = 1024;

                sendingThread = new Thread(SendingMethod);
                sendingThread.IsBackground = true;
                sendingThread.Start();

                receivingThread = new Thread(ReceivingMethod);
                receivingThread.IsBackground = true;
                receivingThread.Start();

                if (!WaitForInitialization())
                {
                    logger.Error("Timeout when waiting for initialization data from the controller");
                    return false;
                }

                return clientSocket.Connected;
            }
            catch (Exception ex)
            {
                logger.Debug(ex);
                throw new Exception("ERROR: could not establish TCP connection");
            }

            //return false;
        }

        private bool WaitForInitialization()
        {
            int time = 0;
            logger.Debug("Waiting for intialization data from controller...");

            // @TODO: this is awful, come on...
            while ((_deviceDriverVersion == null || initAx == null || initPos == null || initRot == null || initExtAx == null) && time < INIT_TIMEOUT)
            {
                time += 33;
                Thread.Sleep(33);
            }

            return _deviceDriverVersion != null || initAx != null && initPos != null && initRot != null && initExtAx == null;
        }


        internal bool ConfigureBuffer(int minActions, int maxActions)
        {
            this._maxStreamCount = maxActions;
            this._sendNewBatchOn = minActions;
            return true;
        }


        private void SendingMethod(object obj)
        {
            if (Thread.CurrentThread.Name == null)
            {
                Thread.CurrentThread.Name = "MachinaTCPSendingThread";
            }

            // Expire the thread on disconnection
            while (Status != TCPConnectionStatus.Disconnected)
            {
                while (this.ShouldSend() && this._writeCursor.AreActionsPending())
                {
                    var msgs = this._translator.GetMessagesForNextAction(this._writeCursor);
                    if (msgs != null)
                    {
                        foreach (var msg in msgs)
                        {
                            _sendMsgBytes = Encoding.ASCII.GetBytes(msg);
                            clientNetworkStream.Write(_sendMsgBytes, 0, _sendMsgBytes.Length);
                            _sentMessages++;
                            //Console.WriteLine("Sending mgs: " + msg);
                            logger.Debug($"Sent: {msg}");
                        }
                    }

                    // Action was released to the device, raise event
                    this._parentDriver.parentControl.RaiseActionReleasedEvent();
                }

                //RaiseBufferEmptyEventCheck();

                Thread.Sleep(30);
            }
        }

        private void ReceivingMethod(object obj)
        {
            if (Thread.CurrentThread.Name == null)
            {
                Thread.CurrentThread.Name = "MachinaTCPListeningThread";
            }

            // Expire the thread on disconnection
            while (Status != TCPConnectionStatus.Disconnected)
            {
                if (clientSocket.Available > 0)
                {
                    _receiveByteCount = clientSocket.GetStream().Read(_receiveMsgBytes, 0, _receiveMsgBytes.Length);
                    _response = Encoding.UTF8.GetString(_receiveMsgBytes, 0, _receiveByteCount);

                    //var msgs = _response.Split(new char[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
                    var msgs = SplitResponse(_response);
                    foreach (var msg in msgs)
                    {
                        //Console.WriteLine($"  RES: Server response was {msg};");
                        logger.Debug("Received message from driver: " + msg);
                        ParseResponse(msg);
                        _receivedMessages++;
                    }
                }

                Thread.Sleep(30);
            }
        }

        private bool ShouldSend()
        {
            if (_isDeviceBufferFull)
            {
                if (_sentMessages - _receivedMessages <= _sendNewBatchOn)
                {
                    _isDeviceBufferFull = false;
                    return true;
                }
                else
                {
                    return false;
                }
            }
            else
            {
                if (_sentMessages - _receivedMessages < _maxStreamCount)
                {
                    return true;
                }
                else
                {
                    _isDeviceBufferFull = true;
                    return false;
                }
            }
        }

        //private void RaiseBufferEmptyEventCheck()
        //{
        //    if (this._writeCursor.AreActionsPending())
        //    {
        //        _bufferEmptyEventIsRaiseable = true;
        //    }
        //    else if (_bufferEmptyEventIsRaiseable)
        //    {
        //        this._parentDriver.parentControl.RaiseBufferEmptyEvent();
        //        _bufferEmptyEventIsRaiseable = false;
        //    }
        //}


        /// <summary>
        /// Take the response buffer and split it into single messages.
        /// ABB cannot use strings longer that 80 chars, so some messages must be sent in chunks. 
        /// This function parses the response for message continuation and end chars, 
        /// and returns a list of joint messages if appropriate.
        /// </summary>
        /// <param name="response"></param>
        /// <returns></returns>
        private string[] SplitResponse(string response)
        {
            bool unfinished = response[response.Length - 1] == ABBCommunicationProtocol.STR_MESSAGE_CONTINUE_CHAR;

            string[] chunks = response.Split(new char[] { ABBCommunicationProtocol.STR_MESSAGE_END_CHAR }, StringSplitOptions.RemoveEmptyEntries);

            if (chunks.Length == 0)
                // Return empty array (and keep unfinished chunk for next response with body
                return chunks;

            // Join '>' chunks with whitespaces
            foreach (string chunk in chunks)
            {
                chunk.Replace(ABBCommunicationProtocol.STR_MESSAGE_CONTINUE_CHAR, ' ');
            }

            // Attach unfinished chunk from previous message
            chunks[0] = unfinishedChunk + chunks[0];
            unfinishedChunk = "";

            // Remove unfinished chunk from array
            if (unfinished)
            {
                unfinishedChunk = chunks[chunks.Length - 1];

                string[] copy = new string[chunks.Length - 1];
                Array.Copy(chunks, copy, chunks.Length - 1);

                chunks = copy;
            }

            return chunks;
        }

        private string unfinishedChunk = "";


        /// <summary>
        /// Parse the response and decide what to do with it.
        /// </summary>
        /// <param name="res"></param>
        private void ParseResponse(string res)
        {
            // If first char is an id marker (otherwise, we can't know which action it is)
            // @TODO: this is hardcoded for ABB, do this programmatically...
            if (res[0] == ABBCommunicationProtocol.STR_MESSAGE_ID_CHAR)
            {
                AcknowledgmentReceived(res);
            }
            else if (res[0] == ABBCommunicationProtocol.STR_MESSAGE_RESPONSE_CHAR)
            {
                //Console.WriteLine("RECEIVED: " + res);
                DataReceived(res);
            }
        }

        private void AcknowledgmentReceived(string res)
        {
            // @TODO: Add some sanity here for incorrectly formatted messages
            _responseChunks = res.Split(' ');
            string idStr = _responseChunks[0].Substring(1);
            int id = Convert.ToInt32(idStr);
            this._motionCursor.ApplyActionsUntilId(id);

            // Raise appropriate events
            this._parentDriver.parentControl.RaiseActionExecutedEvent();
            //this._parentDriver.parentControl.RaiseMotionCursorUpdatedEvent();
            //this._parentDriver.parentControl.RaiseActionCompletedEvent();
            
        }

        private void DataReceived(string res)
        {
            _responseChunks = res.Split(' ');
            int resType = Convert.ToInt32(_responseChunks[0].Substring(1));

            double[] data = new double[_responseChunks.Length - 1];
            for (int i = 0; i < data.Length; i++)
            {
                // @TODO: add sanity like Double.TryParse(...)
                data[i] = Double.Parse(_responseChunks[i + 1]);
            }

            switch (resType)
            {
                // ">20 1 2 1;" Sends version numbers
                case ABBCommunicationProtocol.RES_VERSION:
                    this._deviceDriverVersion = Convert.ToInt32(data[0]) + "." + Convert.ToInt32(data[1]) + "." + Convert.ToInt32(data[2]);
                    int comp = Util.CompareVersions(ABBCommunicationProtocol.MACHINA_SERVER_VERSION, _deviceDriverVersion);
                    if (comp > -1)
                    {
                        logger.Verbose($"Using ABB Driver version {ABBCommunicationProtocol.MACHINA_SERVER_VERSION}, found {_deviceDriverVersion}.");
                    }
                    else
                    {
                        logger.Warning($"Found driver version {_deviceDriverVersion}, expected at least {ABBCommunicationProtocol.MACHINA_SERVER_VERSION}. Please update driver module or unexpected behavior may arise.");
                    }
                    break;

                // ">21 400 300 500 0 0 1 0;"
                case ABBCommunicationProtocol.RES_POSE:
                    this.initPos = new Vector(data[0], data[1], data[2]);
                    this.initRot = new Rotation(new Quaternion(data[3], data[4], data[5], data[6]));
                    break;


                // ">22 0 0 0 0 90 0;"
                case ABBCommunicationProtocol.RES_JOINTS:
                    this.initAx = new Joints(data[0], data[1], data[2], data[3], data[4], data[5]);
                    break;

                case ABBCommunicationProtocol.RES_EXTAX:
                    this.initExtAx = new ExternalAxes(data[0], data[1], data[2], data[3], data[4], data[5]);
                    break;

            }

        }

    }
}
