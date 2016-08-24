using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Secs4Net.Properties;
using System.Threading.Tasks;

namespace Secs4Net
{
    public sealed class SecsGem : IDisposable
    {
        public event EventHandler ConnectionChanged;
        public ConnectionState State { get; private set; }
        public short DeviceId { get; set; } = 0;
        public int LinkTestInterval { get; set; } = 60000;
        public int T3 { get; set; } = 45000;
        public int T5 { get; set; } = 10000;
        public int T6 { get; set; } = 5000;
        public int T7 { get; set; } = 10000;
        public int T8 { get; set; } = 5000;

        public bool LinkTestEnable
        {
            get { return _timerLinkTest.Enabled; }
            set
            {
                _timerLinkTest.Interval = LinkTestInterval;
                _timerLinkTest.Enabled = value;
            }
        }

        readonly bool _isActive;
        readonly IPAddress _ip;
        readonly int _port;
        Socket _socket;

        readonly SecsDecoder _secsDecoder;
        readonly ConcurrentDictionary<int, SecsAsyncResult> _replyExpectedMsgs = new ConcurrentDictionary<int, SecsAsyncResult>();
        readonly Action<SecsMessage, Action<SecsMessage>> _primaryMessageHandler;
        readonly SecsTracer _tracer;
        readonly System.Timers.Timer _timer7 = new System.Timers.Timer();	// between socket connected and received Select.req timer
        readonly System.Timers.Timer _timer8 = new System.Timers.Timer();
        readonly System.Timers.Timer _timerLinkTest = new System.Timers.Timer();

        readonly Func<Task> _startImpl;
        readonly Action _stopImpl;

        readonly byte[] _recvBuffer;
        static readonly SecsMessage ControlMessage = new SecsMessage(0, 0, string.Empty);
        static readonly ArraySegment<byte> ControlMessageLengthBytes = new ArraySegment<byte>(new byte[] { 0, 0, 0, 10 });
        static readonly SecsTracer DefaultTracer = new SecsTracer();
        readonly Func<int> _newSystemByte;

        public SecsGem(IPAddress ip, int port, bool isActive, SecsTracer tracer = null, Action<SecsMessage, Action<SecsMessage>> primaryMsgHandler = null, int receiveBufferSize = 0x4000)
        {
            if (ip == null)
                throw new ArgumentNullException(nameof(ip));

            _ip = ip;
            _port = port;
            _isActive = isActive;
            _recvBuffer = new byte[receiveBufferSize < 0x4000 ? 0x4000 : receiveBufferSize];
            _secsDecoder = new SecsDecoder(HandleControlMessage, HandleDataMessage);
            _tracer = tracer ?? DefaultTracer;
            _primaryMessageHandler = primaryMsgHandler ?? ((primary, reply) => reply(null));

            int systemByte = new Random(Guid.NewGuid().GetHashCode()).Next();
            _newSystemByte = () => Interlocked.Increment(ref systemByte);

            #region Timer Action
            _timer7.Elapsed += delegate
            {
                _tracer.TraceError("T7 Timeout");
                CommunicationStateChanging(ConnectionState.Retry);
            };

            _timer8.Elapsed += delegate
            {
                _tracer.TraceError("T8 Timeout");
                CommunicationStateChanging(ConnectionState.Retry);
            };

            _timerLinkTest.Elapsed += delegate
            {
                if (State == ConnectionState.Selected)
                    SendControlMessage(MessageType.LinkTestRequest, _newSystemByte());
            };
            #endregion
            if (_isActive)
            {
                #region Active Impl
                var timer5 = new System.Timers.Timer();
                timer5.Elapsed += delegate
                {
                    timer5.Enabled = false;
                    _tracer.TraceError("T5 Timeout");
                    CommunicationStateChanging(ConnectionState.Retry);
                };

                _startImpl = async delegate
                {
                    CommunicationStateChanging(ConnectionState.Connecting);
                    try
                    {
                        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                        await Task.Factory.FromAsync(socket.BeginConnect, socket.EndConnect, _ip, _port, null).ConfigureAwait(false);
                        CommunicationStateChanging(ConnectionState.Connected);
                        _socket = socket;
                        SendControlMessage(MessageType.SelectRequest, _newSystemByte());
                        _socket.BeginReceive(_recvBuffer, 0, _recvBuffer.Length, SocketFlags.None, ReceiveComplete, null);
                    }
                    catch (Exception ex)
                    {
                        if (_isDisposed) return;
                        _tracer.TraceError(ex.Message);
                        _tracer.TraceInfo("Start T5 Timer");
                        timer5.Interval = T5;
                        timer5.Enabled = true;
                    }
                };

                _stopImpl = delegate
                {
                    timer5.Stop();
                    if (_isDisposed) timer5.Dispose();
                };
                #endregion
            }
            else
            {
                #region Passive Impl
                var server = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                server.Bind(new IPEndPoint(_ip, _port));
                server.Listen(0);

                _startImpl = async delegate
                {
                    CommunicationStateChanging(ConnectionState.Connecting);
                    try
                    {
                        _socket = await Task.Factory.FromAsync(server.BeginAccept, server.EndAccept, null).ConfigureAwait(false);
                        CommunicationStateChanging(ConnectionState.Connected);
                        _socket.BeginReceive(_recvBuffer, 0, _recvBuffer.Length, SocketFlags.None, ReceiveComplete, null);
                    }
                    catch (Exception ex)
                    {
                        _tracer.TraceError("System Exception", ex);
                        CommunicationStateChanging(ConnectionState.Retry);
                    }
                };

                _stopImpl = delegate
                {
                    if (_isDisposed)
                        server.Close();
                };
                #endregion
            }
        }

        #region Socket Receive Process
        void ReceiveComplete(IAsyncResult iar)
        {
            try
            {
                int count = _socket.EndReceive(iar);

                _timer8.Enabled = false;

                if (count == 0)
                {
                    _tracer.TraceError("Received 0 byte data. Close the socket.");
                    CommunicationStateChanging(ConnectionState.Retry);
                    return;
                }

                if (!_secsDecoder.Decode(_recvBuffer, 0, count))
                {
                    _tracer.TraceInfo("Start T8 Timer");
                    _timer8.Interval = T8;
                    _timer8.Enabled = true;
                }

                _socket.BeginReceive(_recvBuffer, 0, _recvBuffer.Length, SocketFlags.None, ReceiveComplete, null);
            }
            catch (NullReferenceException ex)
            {
                _tracer.TraceWarning("unexpected NullReferenceException:" + ex);
            }
            catch (SocketException ex)
            {
                _tracer.TraceError($"RecieveComplete socket error:{ex.Message + ex}, ErrorCode:{ex.SocketErrorCode}", ex);
                CommunicationStateChanging(ConnectionState.Retry);
            }
            catch (Exception ex)
            {
                _tracer.TraceError("unexpected exception", ex);
                CommunicationStateChanging(ConnectionState.Retry);
            }
        }

        void HandleControlMessage(Header header)
        {
            int systembyte = header.SystemBytes;
            if ((byte)header.MessageType % 2 == 0)
            {
                if (_replyExpectedMsgs.TryGetValue(systembyte, out  var ar))
                {
                    ar.EndProcess(ControlMessage, false);
                }
                else
                {
                    _tracer.TraceWarning("Received Unexpected Control Message: " + header.MessageType);
                    return;
                }
            }
            _tracer.TraceInfo("Receive Control message: " + header.MessageType);
            switch (header.MessageType)
            {
                case MessageType.SelectRequest:
                    SendControlMessage(MessageType.SelectResponse, systembyte);
                    CommunicationStateChanging(ConnectionState.Selected);
                    break;
                case MessageType.SelectResponse:
                    switch (header.F)
                    {
                        case 0:
                            CommunicationStateChanging(ConnectionState.Selected);
                            break;
                        case 1:
                            _tracer.TraceError("Communication Already Active.");
                            break;
                        case 2:
                            _tracer.TraceError("Connection Not Ready.");
                            break;
                        case 3:
                            _tracer.TraceError("Connection Exhaust.");
                            break;
                        default:
                            _tracer.TraceError("Connection Status Is Unknown.");
                            break;
                    }
                    break;
                case MessageType.LinkTestRequest:
                    SendControlMessage(MessageType.LinkTestResponse, systembyte);
                    break;
                case MessageType.SeperateRequest:
                    CommunicationStateChanging(ConnectionState.Retry);
                    break;
            }
        }

        void HandleDataMessage(Header header, SecsMessage msg)
        {
            int systembyte = header.SystemBytes;

            if (header.DeviceId != DeviceId && msg.S != 9 && msg.F != 1)
            {
                _tracer.TraceMessageIn(msg, systembyte);
                _tracer.TraceWarning("Received Unrecognized Device Id Message");
                try
                {
                    SendDataMessage(new SecsMessage(9, 1, false, "Unrecognized Device Id", Item.B(header.Bytes)), _newSystemByte());
                }
                catch (Exception ex)
                {
                    _tracer.TraceError("Send S9F1 Error", ex);
                }
                return;
            }

            if (msg.F % 2 != 0)
            {
                if (msg.S != 9)
                {
                    //Primary message
                    _tracer.TraceMessageIn(msg, systembyte);
                    _primaryMessageHandler(msg, secondary =>
                    {
                        if (!header.ReplyExpected || State != ConnectionState.Selected)
                            return;

                        secondary = secondary ?? new SecsMessage(9, 7, false, "Unknown Message", Item.B(header.Bytes));
                        secondary.ReplyExpected = false;
                        try
                        {
                            SendDataMessage(secondary, secondary.S == 9 ? _newSystemByte() : header.SystemBytes);
                        }
                        catch (Exception ex)
                        {
                            _tracer.TraceError("Reply Secondary Message Error", ex);
                        }
                    });
                    return;
                }
                // Error message
                var headerBytes = (byte[])msg.SecsItem;
                systembyte = BitConverter.ToInt32(new[] { headerBytes[9], headerBytes[8], headerBytes[7], headerBytes[6] }, 0);
            }

            // Secondary message
            if (_replyExpectedMsgs.TryGetValue(systembyte, out var ar))
                ar.EndProcess(msg, false);
            _tracer.TraceMessageIn(msg, systembyte);
        }
        #endregion
        #region Socket Send Process
        void SendControlMessage(MessageType msgType, int systembyte)
        {
            if (_socket == null || !_socket.Connected)
                return;

            if ((byte)msgType % 2 == 1 && msgType != MessageType.SeperateRequest)
            {
                var ar = new SecsAsyncResult(ControlMessage);
                _replyExpectedMsgs[systembyte] = ar;

                ThreadPool.RegisterWaitForSingleObject(ar.AsyncWaitHandle,
                    (state, timeout) =>
                    {
                        if (_replyExpectedMsgs.TryRemove((int)state, out var ars) && timeout)
                        {
                            _tracer.TraceError("T6 Timeout");
                            CommunicationStateChanging(ConnectionState.Retry);
                        }
                    }, systembyte, T6, true);
            }

            var header = new Header(new byte[10])
            {
                MessageType = msgType,
                SystemBytes = systembyte
            };
            header.Bytes[0] = 0xFF;
            header.Bytes[1] = 0xFF;
            _socket.Send(new List<ArraySegment<byte>>(2){
                ControlMessageLengthBytes,
                new ArraySegment<byte>(header.Bytes)
            });
            _tracer.TraceInfo("Sent Control Message: " + header.MessageType);
        }

        SecsAsyncResult SendDataMessage(SecsMessage msg, int systembyte, AsyncCallback callback = null, object syncState = null)
        {
            if (State != ConnectionState.Selected)
                throw new SecsException("Device is not selected");

            var header = new Header(new byte[10])
            {
                S = msg.S,
                F = msg.F,
                ReplyExpected = msg.ReplyExpected,
                DeviceId = DeviceId,
                SystemBytes = systembyte
            };
            var buffer = new EncodedBuffer(header.Bytes, msg.RawDatas);

            SecsAsyncResult ar = null;
            if (msg.ReplyExpected)
            {
                ar = new SecsAsyncResult(msg, callback, syncState);
                _replyExpectedMsgs[systembyte] = ar;

                ThreadPool.RegisterWaitForSingleObject(ar.AsyncWaitHandle,
                   (state, timeout) =>
                   {
                       if (timeout && _replyExpectedMsgs.TryRemove((int)state, out var ars))
                       {
                           _tracer.TraceError($"T3 Timeout[id=0x{state:X8}]");
                           ars.EndProcess(null, true);
                       }
                   }, systembyte, T3, true);
            }

            if (_socket.Send(buffer, SocketFlags.None, out var error)<0 || error != SocketError.Success)
            {
                var errorMsg = "Socket send error :" + new SocketException((int)error).Message;
                _tracer.TraceError(errorMsg);
                CommunicationStateChanging(ConnectionState.Retry);
                throw new SecsException(errorMsg);
            }

            _tracer.TraceMessageOut(msg, systembyte);
            return ar;
        }
        #endregion
        #region Internal State Transition
        void CommunicationStateChanging(ConnectionState newState)
        {
            State = newState;
            ConnectionChanged?.Invoke(this, EventArgs.Empty);

            switch (State)
            {
                case ConnectionState.Selected:
                    _timer7.Enabled = false;
                    _tracer.TraceInfo("Stop T7 Timer");
                    break;
                case ConnectionState.Connected:
                    _tracer.TraceInfo("Start T7 Timer");
                    _timer7.Interval = T7;
                    _timer7.Enabled = true;
                    break;
                case ConnectionState.Retry:
                    if (_isDisposed)
                        return;
                    Reset();
                    Thread.Sleep(2000);
                    _startImpl().Start();
                    break;
            }
        }

        void Reset()
        {
            _timer7.Stop();
            _timer8.Stop();
            _timerLinkTest.Stop();
            _secsDecoder.Reset();
            if (_socket != null)
            {
                _socket.Shutdown(SocketShutdown.Both);
                _socket.Close();
                _socket = null;
            }
            _replyExpectedMsgs.Clear();
            _stopImpl();
        }
        #endregion
        #region Public API
        public async Task Start() => await _startImpl();

        /// <summary>
        /// Send SECS message to device.
        /// </summary>
        /// <param name="msg"></param>
        /// <returns>Device's reply msg if msg.ReplyExpected is true;otherwise, null.</returns>
        public SecsMessage Send(SecsMessage msg) => EndSend(BeginSend(msg));

        /// <summary>
        /// Send SECS message asynchronously to device .
        /// </summary>
        /// <param name="msg"></param>
        /// <returns></returns>
        public async Task<SecsMessage> SendAsync(SecsMessage msg) => await Task.Factory.FromAsync(BeginSend, EndSend, msg, null).ConfigureAwait(false);

        /// <summary>
        /// Send SECS message asynchronously to device .
        /// </summary>
        /// <param name="msg"></param>
        /// <param name="callback">Device's reply message handler callback.</param>
        /// <param name="state">synchronize state object</param>
        /// <returns>An IAsyncResult that references the asynchronous send if msg.ReplyExpected is true;otherwise, null.</returns>
        public IAsyncResult BeginSend(SecsMessage msg, AsyncCallback callback = null, object state = null) => SendDataMessage(msg, _newSystemByte(), callback, state);

        /// <summary>
        /// Ends a asynchronous send.
        /// </summary>
        /// <param name="asyncResult">An IAsyncResult that references the asynchronous send</param>
        /// <returns>Device's reply message if <paramref name="asyncResult"/> is an IAsyncResult that references the asynchronous send, otherwise null.</returns>
        public SecsMessage EndSend(IAsyncResult asyncResult)
        {
            if (asyncResult == null)
                throw new ArgumentNullException(nameof(asyncResult));
            if (asyncResult is SecsAsyncResult ar)
            {
                ar.AsyncWaitHandle.WaitOne();
                return ar.Secondary;
            }

            throw new ArgumentException($"argument {nameof(asyncResult)} was not created by a call to {nameof(BeginSend)}", nameof(asyncResult));
        }

        volatile bool _isDisposed;
        public void Dispose()
        {
            if (!_isDisposed)
            {
                _isDisposed = true;
                ConnectionChanged = null;
                if (State == ConnectionState.Selected)
                    SendControlMessage(MessageType.SeperateRequest, _newSystemByte());
                Reset();
                _timer7.Dispose();
                _timer8.Dispose();
                _timerLinkTest.Dispose();
            }
        }

        public string DeviceAddress => _isActive
            ? _ip.ToString()
            // :                    _socket == null 
            //? "N/A" 
            : ((IPEndPoint)_socket?.RemoteEndPoint)?.Address?.ToString() ?? "NA";
        #endregion
        #region Async Impl
        sealed class SecsAsyncResult : IAsyncResult
        {
            readonly ManualResetEvent _ev = new ManualResetEvent(false);
            readonly SecsMessage _primary;
            readonly AsyncCallback _callback;

            SecsMessage _secondary;
            bool _timeout;

            internal SecsAsyncResult(SecsMessage primaryMsg, AsyncCallback callback = null, object state = null)
            {
                _primary = primaryMsg;
                AsyncState = state;
                _callback = callback;
            }

            internal void EndProcess(SecsMessage replyMsg, bool timeout)
            {
                if (replyMsg != null)
                {
                    _secondary = replyMsg;
                    _secondary.Name = _primary.Name;
                }
                _timeout = timeout;
                IsCompleted = !timeout;
                _ev.Set();
                _callback?.Invoke(this);
            }

            internal SecsMessage Secondary
            {
                get
                {
                    if (_timeout) throw new SecsException(_primary, Resources.T3Timeout);
                    if (_secondary == null) return null;
                    if (_secondary.F == 0) throw new SecsException(_primary, Resources.SxF0);
                    if (_secondary.S == 9)
                    {
                        switch (_secondary.F)
                        {
                            case 1: throw new SecsException(_primary, Resources.S9F1);
                            case 3: throw new SecsException(_primary, Resources.S9F3);
                            case 5: throw new SecsException(_primary, Resources.S9F5);
                            case 7: throw new SecsException(_primary, Resources.S9F7);
                            case 9: throw new SecsException(_primary, Resources.S9F9);
                            case 11: throw new SecsException(_primary, Resources.S9F11);
                            case 13: throw new SecsException(_primary, Resources.S9F13);
                            default: throw new SecsException(_primary, Resources.S9Fy);
                        }
                    }
                    return _secondary;
                }
            }

            #region IAsyncResult Members

            public object AsyncState { get; }

            public WaitHandle AsyncWaitHandle => _ev;

            public bool CompletedSynchronously => false;

            public bool IsCompleted { get; private set; }

            #endregion
        }
        #endregion
        #region SECS Decoder
        sealed class SecsDecoder
        {
            /// <summary>
            /// SecsMessage total byte length
            /// </summary>
            uint _messageLength;

            /// <summary>
            /// SecsMesage header part
            /// </summary>
            Header _msgHeader;

            /// <summary>
            /// Item List stack
            /// </summary>
            readonly Stack<List<Item>> _stack = new Stack<List<Item>>();

            /// <summary>
            /// Item format
            /// </summary>
            SecsFormat _format;

            /// <summary>
            /// Item value length bits (2bit)
            /// </summary>
            byte _lengthBits;

            /// <summary>
            /// Item value length
            /// </summary>
            int _itemLength;

            /// <summary>
            /// Current step of decode pipeline
            /// </summary>
            int _currentStep;

            /// <summary>
            /// 
            /// </summary>
            /// <param name="data"></param>
            /// <param name="length"></param>
            /// <param name="index"></param>
            /// <returns>next decoder step and required count</returns>
            delegate (int nextStep, int required) Decoder(byte[] data, int length, ref int index);

            /// <summary>
            /// decode pipeline
            /// </summary>
            readonly Decoder[] _decoders;
            readonly Action<Header, SecsMessage> _dataMsgHandler;
            readonly Action<Header> _controlMsgHandler;

            internal SecsDecoder(Action<Header> controlMsgHandler, Action<Header, SecsMessage> msgHandler)
            {
                _dataMsgHandler = msgHandler;
                _controlMsgHandler = controlMsgHandler;

                _decoders = new Decoder[]{

                    // step 0: get total message length in [4] bytes
                    (byte[] data, int length, ref int index) =>
                    {
                       int need = CheckStillNeed(length, index, 4);
                       if (need>0) return (0,need);

                       Array.Reverse(data, index, 4);
                       _messageLength = BitConverter.ToUInt32(data, index);
                       Trace.WriteLine("Get Message Length =" + _messageLength);
                       index += 4;

                       return (1,need);
                    },

                    // step 1: get message header in [10] bytes
                    (byte[] data, int length, ref int index) =>
                    {
                        int need = CheckStillNeed(length, index, 10);
                        if ( need>0) return (1,need);

                        _msgHeader = new Header(new byte[10]);
                        Array.Copy(data, index, _msgHeader.Bytes, 0, 10);
                        index += 10;
                        _messageLength -= 10;
                        if (_messageLength == 0)
                        {
                            // SecsMessage without Item
                            if (_msgHeader.MessageType == MessageType.DataMessage)
                            {
                                ProcessMessage(new SecsMessage(_msgHeader.S, _msgHeader.F, _msgHeader.ReplyExpected, string.Empty));
                            }
                            else
                            {
                                _controlMsgHandler(_msgHeader);
                                _messageLength = 0;
                            }
                            return (0,need);
                        }
                        else if (length - index >= _messageLength)
                        {
                            ProcessMessage(new SecsMessage(_msgHeader.S, _msgHeader.F, _msgHeader.ReplyExpected, data, ref index));
                            return (0,need);
                        }
                        return (2,need);
                    },

                    // step 2: get _format, lengthBits in [1] byte
                    (byte[] data, int length, ref int index) =>
                    {
                        int need = CheckStillNeed(length, index, 1);
                        if ( need>0) return (2,need);

                        _format = (SecsFormat)(data[index] & 0b11111100);
                        _lengthBits = (byte)(data[index] & 0b00000011);
                        index++;
                        _messageLength--;
                        return (3,need);
                    },

                    // step 3: get _itemLength in [_lengthBits] bytes
                    (byte[] data, int length, ref int index) =>
                    {
                        int need =CheckStillNeed(length, index, _lengthBits);
                        if ( need>0) return (3,need);

                        byte[] itemLengthBytes = new byte[4];
                        Array.Copy(data, index, itemLengthBytes, 0, _lengthBits);
                        Array.Reverse(itemLengthBytes, 0, _lengthBits);

                        _itemLength = BitConverter.ToInt32(itemLengthBytes, 0);
                        Array.Clear(itemLengthBytes, 0, 4);

                        index += _lengthBits;
                        _messageLength -= _lengthBits;
                        return (4,need);
                    },

                    // ste[ 4: get item value 
                    (byte[] data, int length, ref int index) =>
                    {
                        int need = 0;
                        Item item;
                        if (_format == SecsFormat.List)
                        {
                            if (_itemLength == 0) {
                                item = Item.L();
                            }
                            else
                            {
                                _stack.Push(new List<Item>(_itemLength));
                                return (2,need);
                            }
                        }
                        else
                        {
                            need = CheckStillNeed(length, index, _itemLength);
                            if (need>0) return (4,need);

                            item = _itemLength == 0 ? _format.BytesDecode() : _format.BytesDecode(data, index, _itemLength);
                            index += _itemLength;
                            _messageLength -= (uint)_itemLength;
                        }

                        if (_stack.Count > 0)
                        {
                            var list = _stack.Peek();
                            list.Add(item);
                            while (list.Count == list.Capacity)
                            {
                                item = Item.L(_stack.Pop());
                                if (_stack.Count > 0)
                                {
                                    list = _stack.Peek();
                                    list.Add(item);
                                }
                                else
                                {
                                    ProcessMessage(new SecsMessage(_msgHeader.S, _msgHeader.F, _msgHeader.ReplyExpected, string.Empty, item));
                                    return (0,need);
                                }
                            }
                        }
                        return (2,need);
                    },

                };
            }

            void ProcessMessage(SecsMessage msg)
            {
                _dataMsgHandler(_msgHeader, msg);
                _messageLength = 0;
            }

            /// <summary>
            /// Check still need byte lenth
            /// </summary>
            /// <param name="length"></param>
            /// <param name="index"></param>
            /// <param name="requireCount"></param>
            /// <returns>still need count</returns>
            static int CheckStillNeed(int length, int index, int requireCount)
            {
                return requireCount - (length - index);
            }

            public void Reset()
            {
                _stack.Clear();
                _currentStep = 0;
                _pendingBuffer = emptyPendingBuffer;
                _messageLength = 0;
            }

            static readonly (byte[] buffer, int remaining, int required) emptyPendingBuffer = (null, 0, 0);
            (byte[] buffer, int remaining, int required) _pendingBuffer = emptyPendingBuffer;

            /// <summary>
            /// 
            /// </summary>
            /// <param name="buffer">位元組</param>
            /// <param name="startIndex">有效位元的起始索引</param>
            /// <param name="length">有效位元長度</param>
            /// <returns>True, No incomplete SecsMessage pending to decode. Otherwise return false.</returns>
            public bool Decode(byte[] buffer, int startIndex, int length)
            {
                if (_pendingBuffer.required == 0)
                {
                    // no pending buffer need process
                    (int nexStep, int required) step = (_currentStep, 0);
                    do
                    {
                        _currentStep = step.nexStep;
                        step = _decoders[_currentStep](buffer, length, ref startIndex);
                    } while (step.nexStep != _currentStep);
                    int remainLength = length - startIndex;
                    if (remainLength > 0)
                    {
                        var temp = new byte[remainLength + step.required];
                        Array.Copy(buffer, startIndex, temp, 0, remainLength);
                        _pendingBuffer = (temp, remainLength, step.required);
                        Trace.WriteLine($"Remain Length: {remainLength}, Need:{_pendingBuffer.required}");
                    }
                    else
                    {
                        _pendingBuffer = emptyPendingBuffer;
                    }
                }
                else if (length - startIndex >= _pendingBuffer.required)
                {
                    // income length greater than required length
                    // combine buffer then decode.
                    Array.Copy(buffer, startIndex, _pendingBuffer.buffer, _pendingBuffer.remaining, _pendingBuffer.required);
                    byte[] pending = _pendingBuffer.buffer;
                    if (!Decode(pending, 0, pending.Length)) // decode pending buffer
                    {
                        int remainingIncomeIndex = _pendingBuffer.required;
                        _pendingBuffer = emptyPendingBuffer; // clear pending buffer
                        Decode(buffer, remainingIncomeIndex, length); // decode remaining income
                    }
                }
                else /* (length - index < _remainBytes.required) */
                {
                    // income length still not enough
                    int incomeLength = length - startIndex;
                    Array.Copy(buffer, startIndex, _pendingBuffer.buffer, _pendingBuffer.remaining, incomeLength);
                    _pendingBuffer = (_pendingBuffer.buffer, _pendingBuffer.remaining + incomeLength, _pendingBuffer.required - incomeLength);
                    Trace.WriteLine($"Remain Length: {_pendingBuffer.remaining}, Need:{_pendingBuffer.required}");
                }
                return _messageLength <= 0;
            }
        }
        #endregion
        #region Message Header Struct
        struct Header
        {
            internal readonly byte[] Bytes;
            internal Header(byte[] headerbytes)
            {
                Bytes = headerbytes;
            }

            public short DeviceId
            {
                get
                {
                    return BitConverter.ToInt16(new[] { Bytes[1], Bytes[0] }, 0);
                }
                set
                {
                    byte[] values = BitConverter.GetBytes(value);
                    Bytes[0] = values[1];
                    Bytes[1] = values[0];
                }
            }
            public bool ReplyExpected
            {
                get { return (Bytes[2] & 0b1000_0000) == 0b1000_0000; }
                set { Bytes[2] = (byte)(S | (value ? 0b1000_0000 : 0)); }
            }
            public byte S
            {
                get { return (byte)(Bytes[2] & 0b0111_1111); }
                set { Bytes[2] = (byte)(value | (ReplyExpected ? 0b1000_0000 : 0)); }
            }
            public byte F
            {
                get { return Bytes[3]; }
                set { Bytes[3] = value; }
            }
            public MessageType MessageType
            {
                get { return (MessageType)Bytes[5]; }
                set { Bytes[5] = (byte)value; }
            }
            public int SystemBytes
            {
                get
                {
                    return BitConverter.ToInt32(new[] {
                        Bytes[9],
                        Bytes[8],
                        Bytes[7],
                        Bytes[6]
                    }, 0);
                }
                set
                {
                    byte[] values = BitConverter.GetBytes(value);
                    Bytes[6] = values[3];
                    Bytes[7] = values[2];
                    Bytes[8] = values[1];
                    Bytes[9] = values[0];
                }
            }
        }
        #endregion
        #region EncodedByteList Wrapper just need IList<T>.Count and Indexer
        sealed class EncodedBuffer : IList<ArraySegment<byte>>
        {
            readonly IReadOnlyList<RawData> _data;// raw data include first message length 4 byte
            readonly byte[] _header;

            internal EncodedBuffer(byte[] header, IReadOnlyList<RawData> msgRawDatas)
            {
                _header = header;
                _data = msgRawDatas;
            }

            #region IList<ArraySegment<byte>> Members
            int IList<ArraySegment<byte>>.IndexOf(ArraySegment<byte> item) => -1;
            void IList<ArraySegment<byte>>.Insert(int index, ArraySegment<byte> item) { }
            void IList<ArraySegment<byte>>.RemoveAt(int index) { }
            ArraySegment<byte> IList<ArraySegment<byte>>.this[int index]
            {
                get { return new ArraySegment<byte>(index == 1 ? _header : _data[index].Bytes); }
                set { }
            }
            #endregion
            #region ICollection<ArraySegment<byte>> Members
            void ICollection<ArraySegment<byte>>.Add(ArraySegment<byte> item) { }
            void ICollection<ArraySegment<byte>>.Clear() { }
            bool ICollection<ArraySegment<byte>>.Contains(ArraySegment<byte> item) => false;
            void ICollection<ArraySegment<byte>>.CopyTo(ArraySegment<byte>[] array, int arrayIndex) { }
            int ICollection<ArraySegment<byte>>.Count => _data.Count;
            bool ICollection<ArraySegment<byte>>.IsReadOnly => true;
            bool ICollection<ArraySegment<byte>>.Remove(ArraySegment<byte> item) => false;
            #endregion
            #region IEnumerable<ArraySegment<byte>> Members
            public IEnumerator<ArraySegment<byte>> GetEnumerator()
            {
                for (int i = 0, length = _data.Count; i < length; i++)
                    yield return new ArraySegment<byte>(i == 1 ? _header : _data[i].Bytes);
            }
            #endregion
            #region IEnumerable Members
            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
            #endregion
        }
        #endregion
    }
}