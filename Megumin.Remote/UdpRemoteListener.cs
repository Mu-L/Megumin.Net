﻿using Net.Remote;
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading.Tasks;


///如果服务端只用一个udp接收所有客户端数据
///极限情况下物理机万兆网卡，怎么想一个socket udp接收缓冲区也不够
///https://stackoverflow.com/questions/57431090/does-c-sharp-udp-sockets-receivebuffersize-applies-to-size-of-datagrams-or-size
///这一版现状先把socket recvsize调大试试，看看丢包情况
///
///https://blog.csdn.net/zhyh3737/article/details/7219275
///
namespace Megumin.Remote
{
    /// <summary>
    /// 2018年时IPV4 IPV6 udp中不能混用，不知道现在情况
    /// </summary>
    public class UdpRemoteListenerOld : UdpClient, IListenerOld<UdpRemote>
    {
        public IPEndPoint ConnectIPEndPoint { get; set; }

        protected readonly Dictionary<IPEndPoint, UdpRemote> connected = new Dictionary<IPEndPoint, UdpRemote>();
        protected readonly Dictionary<Guid, UdpRemote> lut = new Dictionary<Guid, UdpRemote>();
        protected readonly UdpAuthHelper authHelper = new UdpAuthHelper();
        /// <remarks>
        /// Q:要不要用同步队列，预计有多个线程入队，只有一个线程出队，会不会有线程安全问题？
        /// </remarks>
        protected Queue<UdpReceiveResult> UdpReceives = new Queue<UdpReceiveResult>();
        public System.Diagnostics.TraceListener TraceListener { get; set; }
        /// <summary>
        /// 服务端使用20个Socket向客户端发送.
        /// <para/> TODO NAT情况复杂，可能无法发送 https://www.cnblogs.com/mq0036/p/4644776.html
        /// <para/> (1)完全Cone NAT 无论目标地址和端口怎样，每次都把该私有源IP地址/端口映射到同一个全局源地址/端口；外网的任何主机都可以发送报文到该映射的全局地址而访问到该内部主机。路由器的静态地址映射就是属于这种。
        ///(2)限制Cone NAT 地址/端口映射的情况同完全Cone NAT的，但外网的主机要访问内网主机，该内网主机必须先发送过报文给该外网主机的地址。
        ///(3)端口限制Cone NAT 地址/端口映射情况同完全Cone NAT的，但外网主机要访问内网主机，该内网主机必须先发送过报文给该外网主机的地址和端口。大多数路由器的NAPT就是属于这种情况。本文后面论及的Cone NAT也是指这种情况。
        ///(4)Symmetric NAT 对不同的目标地址/端口，源私有地址映射到源全局地址不变，但是映射的全局端口会改变。外网主机必须先收到过内网主机的报文，才能访问到该内网主机。一些路由器和防火墙产品的NAT就是属于这种情况。
        /// <para/> 1,2是没问题的，3通常需要客户端先发送一个消息到发送端口，不然SendSockets由于和listen端口不一致，会被NAT丢弃消息。4则完全没有办法。
        /// 需要一个测试方法测试连接是否支持SendSockets发送
        /// 最开始可以先用listen端口发送，异步测试是否支持，等到能支持时转到SendSockets发送。，不支持必须使用 listen端口发送。
        /// </summary>
        protected Socket[] SendSockets = new Socket[20];
        public UdpRemoteListenerOld(int port)
            : base(port)
        {
            Init(port);
        }

        public UdpRemoteListenerOld(int port, AddressFamily addressFamily)
            : base(port, addressFamily)
        {
            Init(port);
        }

        private void Init(int port, AddressFamily addressFamily = AddressFamily.InterNetwork)
        {
            for (int i = 0; i < SendSockets.Length; i++)
            {
                SendSockets[i] = new Socket(addressFamily, SocketType.Dgram, ProtocolType.Udp);
            }
            this.ConnectIPEndPoint = new IPEndPoint(IPAddress.None, port);
            ///可能有点小 预期 10000连接 每秒 100kb  10000 * 1024 * 100 
            ///测试代码 10000 * 1024 * 50(TestMessage3) 
            ///换个角度，千兆网卡，理论上吃满带宽 接收缓冲区需要千兆

            ///1020 * 1024 * 5; 5mb才 500万字节左右。测试代码的要求时每秒1亿2千万，不丢包就怪了。
            Client.ReceiveBufferSize = 1020 * 1024 * 5; //先设个5mb看看 
        }

        public bool IsListening { get; private set; }

        ///<remarks>
        ///Q：如果同时调用多次ReceiveAsync有没有实际意义？能不能达到加速接收的目的？
        ///</remarks>
        async void AcceptAsync()
        {
            while (IsListening)
            {
                ///Todo Bug? https://source.dot.net/#System.Net.Sockets/System/Net/Sockets/UDPClient.cs,635
                ///返回的buffer数组是共享的。只有接收数据报长度大于10000 才是共享的，测试代码中实际上没有触发这个bug。
                var res = await ReceiveAsync().ConfigureAwait(false);
                try
                {
                    UdpReceives.Enqueue(res);
                }
                catch (Exception e)
                {
                    //可能数量太多导致加入消息失败。
                    TraceListener?.WriteLine(e.ToString());
                }
            }
        }

        /// <summary>
        /// 接收和处理分开
        /// </summary>
        async void Deal()
        {
            while (IsListening)
            {
                if (UdpReceives.Count > 0)
                {
                    var res = UdpReceives.Dequeue();
                    IPEndPoint endPoint = res.RemoteEndPoint;
                    byte[] recvbuffer = res.Buffer;
                    if (endPoint == null || recvbuffer == null)
                    {
                        //可能是多线程问题，结果是null，暂时没找到原因
                    }
                    else
                    {
                        InnerDeal(endPoint, recvbuffer);
                    }
                }
                else
                {
                    await Task.Yield();
                }
            }

        }

        protected virtual async void InnerDeal(IPEndPoint endPoint, byte[] recvbuffer)
        {
            if (recvbuffer.Length == 0)
            {
                if (connected.TryGetValue(endPoint, out var remote))
                {
                    remote.Recv0(endPoint);
                }
                return;
            }

            byte messageType = recvbuffer[0];
            switch (messageType)
            {
                case UdpRemoteMessageDefine.UdpAuthRequest:
                    //被动侧不处理主动侧提出的验证。
                    break;
                case UdpRemoteMessageDefine.UdpAuthResponse:
                    authHelper.DealAnswerBuffer(endPoint, recvbuffer);
                    break;
                case UdpRemoteMessageDefine.LLData:
                    {
                        var remote = await FindRemote(endPoint).ConfigureAwait(false);
                        if (remote != null)
                        {
                            remote.RecvLLData(endPoint, recvbuffer, 1, recvbuffer.Length - 1);
                        }
                    }
                    break;
                case UdpRemoteMessageDefine.UdpData:
                    {
                        var remote = await FindRemote(endPoint).ConfigureAwait(false);
                        if (remote != null)
                        {
                            remote.RecvUdpData(endPoint, recvbuffer, 1, recvbuffer.Length - 1);
                        }
                    }

                    break;
                case UdpRemoteMessageDefine.KcpData:
                    {
                        var remote = await FindRemote(endPoint).ConfigureAwait(false);
                        if (remote != null)
                        {
                            remote.RecvKcpData(endPoint, recvbuffer, 1, recvbuffer.Length - 1);
                        }
                    }
                    break;
                default:
                    break;
            }
        }

        protected async ValueTask<UdpRemote> FindRemote(IPEndPoint endPoint)
        {
            if (connected.TryGetValue(endPoint, out var remote))
            {
                return remote;
            }
            else
            {
                var answer = await authHelper.Auth(endPoint, this).ConfigureAwait(false);
                lock (lut)
                {
                    if (lut.TryGetValue(answer.Guid, out var udpRemote))
                    {
                        if (udpRemote.Password != answer.Password)
                        {
                            //guid和密码不匹配,可能遇到有人碰撞攻击
                            return null;
                        }
                        else
                        {
                            if (udpRemote.ConnectIPEndPoint != endPoint)
                            {
                                //重绑定远端
                                connected.Remove(udpRemote.ConnectIPEndPoint);
                                udpRemote.ConnectIPEndPoint = endPoint;
                                connected.Add(endPoint, udpRemote);
                            }

                            return udpRemote;
                        }
                    }
                    else
                    {
                        UdpRemote udp = CreateNew(endPoint, answer);
                        if (udp == null)
                        {
                            TraceListener?.Fail($"Listner 无法创建 remote");
                        }
                        return udp;
                    }
                }
            }
        }

        protected virtual UdpRemote CreateNew(IPEndPoint endPoint, UdpAuthResponse answer)
        {
            if (remoteCreators.TryDequeue(out var creator))
            {
                var (continueAction, udp) = creator.Invoke();

                if (udp != null)
                {
                    udp.IsVaild = true;
                    udp.ConnectIPEndPoint = endPoint;
                    udp.GUID = answer.Guid;
                    udp.Password = answer.Password;
                    //todo add listenUdpclient.
                    var sendSocket = SendSockets[connected.Count % SendSockets.Length];
                    udp.SetSocket(sendSocket);
                    lut.Add(udp.GUID.Value, udp);
                    connected.Add(endPoint, udp);
                }

                continueAction?.Invoke();
                return udp;
            }

            return null;
        }


        public void Stop()
        {
            IsListening = false;
        }

        protected ConcurrentQueue<Func<(Action ContinueDelegate, UdpRemote Remote)>>
            remoteCreators = new ConcurrentQueue<Func<(Action ContinueDelegate, UdpRemote Remote)>>();

        public virtual ValueTask<R> ListenAsync<R>(Func<R> createFunc) where R : UdpRemote
        {
            if (IsListening == false)
            {
                IsListening = true;
                Task.Run(AcceptAsync);
                Task.Run(Deal);
            }
            TaskCompletionSource<R> source = new TaskCompletionSource<R>();

            Func<(Action, UdpRemote)> d = () =>
            {
                var r = createFunc.Invoke();
                Action a = () => { source.SetResult(r); };
                return (a, r);
            };

            remoteCreators.Enqueue(d);

            return new ValueTask<R>(source.Task);
        }
    }

    public partial class UdpRemoteListener : IListener<UdpRemote>
    {
        internal protected static class IPEndPointStatics
        {
            internal const int AnyPort = IPEndPoint.MinPort;
            internal static readonly IPEndPoint Any = new IPEndPoint(IPAddress.Any, AnyPort);
            internal static readonly IPEndPoint IPv6Any = new IPEndPoint(IPAddress.IPv6Any, AnyPort);
        }

        public IPEndPoint ConnectIPEndPoint { get; set; }
        public AddressFamily? AddressFamily { get; set; } = null;
        protected readonly UdpAuthHelper authHelper = new UdpAuthHelper();

        protected readonly Dictionary<IPEndPoint, UdpRemote> connected = new Dictionary<IPEndPoint, UdpRemote>();
        protected readonly Dictionary<Guid, UdpRemote> lut = new Dictionary<Guid, UdpRemote>();

        public Socket Socket { get; protected set; }
        public Socket[] SendSockets = new Socket[10];
        public bool UseSendSocketInsteadRecvSocketOnListenSideRemote { get; set; } = false;

        /// <summary>
        /// Unity中必须明确指定使用IPV4还是IPV6。无论什么平台。可能是mono的问题。
        /// </summary>
        /// <param name="port"></param>
        /// <param name="addressFamily"></param>
        public UdpRemoteListener(int port, AddressFamily? addressFamily = null)
        {
            this.AddressFamily = addressFamily;
            var ip = AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any;
            this.ConnectIPEndPoint = new IPEndPoint(ip, port);
        }

        protected async ValueTask<UdpRemote> FindRemote(IPEndPoint endPoint)
        {
            if (connected.TryGetValue(endPoint, out var remote))
            {
                return remote;
            }
            else
            {
                var answer = await authHelper.Auth(endPoint, Socket).ConfigureAwait(false);

                lock (lut)
                {
                    if (lut.TryGetValue(answer.Guid, out var udpRemote))
                    {
                        if (udpRemote.Password != answer.Password)
                        {
                            //guid和密码不匹配,可能遇到有人碰撞攻击
                            return null;
                        }
                        else
                        {
                            if (udpRemote.ConnectIPEndPoint != endPoint)
                            {
                                //重绑定远端
                                connected.Remove(udpRemote.ConnectIPEndPoint);
                                udpRemote.ConnectIPEndPoint = endPoint;
                                connected.Add(endPoint, udpRemote);
                            }

                            return udpRemote;
                        }
                    }
                }

                //没有找到现有的
                UdpRemote udp = await CreateNew(endPoint, answer);
                if (udp == null)
                {
                    TraceListener?.Fail($"Listner 无法创建 remote");
                }
                return udp;
            }
        }

        protected virtual async ValueTask<UdpRemote> CreateNew(IPEndPoint endPoint, UdpAuthResponse answer)
        {
            //Todo 超时2000ms
            var (CreateRemote, OnComplete) = await remoteCreators.ReadAsync().ConfigureAwait(false);

            var remote = CreateRemote?.Invoke();

            if (remote != null)
            {
                remote.IsVaild = true;
                remote.ConnectIPEndPoint = endPoint;
                remote.GUID = answer.Guid;
                remote.Password = answer.Password;

                if (UseSendSocketInsteadRecvSocketOnListenSideRemote && SendSockets.Length > 0)
                {
                    //监听侧使用特定的Socket发送，不使用接收端口发送减少发送压力。
                    //但是NAT情况可能会导致接收端数据直接被丢弃。
                    var sendSocket = SendSockets[connected.Count % SendSockets.Length];
                    remote.SetSocket(sendSocket);
                }
                else
                {
                    remote.SetSocket(Socket);
                }

                lut.Add(remote.GUID.Value, remote);
                connected.Add(endPoint, remote);
            }

            OnComplete?.Invoke(remote);
            return remote;
        }

        public TraceListener TraceListener { get; set; }
    }

    public partial class UdpRemoteListener
    {
        public void Start(object option = null)
        {
            if (Socket == null)
            {
                var localEP = AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? IPEndPointStatics.Any : IPEndPointStatics.IPv6Any;
                if (AddressFamily == null)
                {
                    Socket = new Socket(SocketType.Dgram, ProtocolType.Udp);
                    for (int i = 0; i < SendSockets.Length; i++)
                    {
                        var sendSocket = new Socket(SocketType.Dgram, ProtocolType.Udp);
                        sendSocket.Bind(localEP);
                        SendSockets[i] = sendSocket;
                    }
                }
                else
                {
                    Socket = new Socket(AddressFamily.Value, SocketType.Dgram, ProtocolType.Udp);
                    for (int i = 0; i < SendSockets.Length; i++)
                    {
                        var sendSocket = new Socket(SocketType.Dgram, ProtocolType.Udp);
                        sendSocket.Bind(localEP);
                        SendSockets[i] = sendSocket;
                    }
                }

                ///可能有点小 预期 10000连接 每秒 100kb  10000 * 1024 * 100 
                ///测试代码 10000 * 1024 * 50(TestMessage3) 
                ///换个角度，千兆网卡，理论上吃满带宽 接收缓冲区需要千兆

                ///1020 * 1024 * 5; 5mb才 500万字节左右。测试代码的要求时每秒1亿2千万，不丢包就怪了。
                Socket.ReceiveBufferSize = 1020 * 1024 * 16;
                Socket.Bind(ConnectIPEndPoint);
            }
            else
            {
                return;
            }

            SocketReceive();
            MessageReceive();
        }

        public void Stop()
        {
            try
            {
                if (Socket != null)
                {
                    Socket.Shutdown(SocketShutdown.Both);
                    Socket.Disconnect(false);
                    Socket.Close();
                }
            }
            catch (Exception e)
            {
                TraceListener?.WriteLine(e);
            }
        }

        protected QueuePipe<(Func<UdpRemote> CreateRemote, Action<UdpRemote> OnComplete)> remoteCreators
            = new QueuePipe<(Func<UdpRemote> CreateRemote, Action<UdpRemote> OnComplete)>();


        public ValueTask<R> ReadAsync<R>(Func<R> createFunc) where R : UdpRemote
        {
            TaskCompletionSource<R> source = new TaskCompletionSource<R>();
            remoteCreators.Write((createFunc, (remote) =>
            {
                source.TrySetResult(remote as R);
            }
            ));
            return new ValueTask<R>(source.Task);
        }
    }

    public partial class UdpRemoteListener
    {
        protected QueuePipe<(IPEndPoint RemoteEndPoint, IMemoryOwner<byte> Owner, int ReceivedBytes)> SocketRecvData
            = new QueuePipe<(IPEndPoint RemoteEndPoint, IMemoryOwner<byte> Owner, int ReceivedBytes)>();

        public bool IsSocketReceiving { get; protected set; }
        /// <summary>
        /// 0x10000 = 65535 Udp报头len占16位。
        /// UDP允许传输的最大长度理论上2^16 - udp head - iphead（ 65507 字节 = 65535 - 20 - 8）
        /// https://blog.csdn.net/flybirddizi/article/details/73065667
        /// https://source.dot.net/#System.Net.Sockets/System/Net/Sockets/UDPClient.cs,16
        /// </summary>
        protected UdpBufferWriter recvBuffer = new UdpBufferWriter(0x10000);
        protected virtual async void SocketReceive()
        {
            lock (recvBuffer)
            {
                if (IsSocketReceiving)
                {
                    return;
                }
                IsSocketReceiving = true;
            }

            try
            {
                while (true)
                {
                    var ow = recvBuffer.GetMemory();
                    if (MemoryMarshal.TryGetArray<byte>(ow, out var segment))
                    {
                        var remote = AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? IPEndPointStatics.Any : IPEndPointStatics.IPv6Any;
                        var res = await Socket.ReceiveFromAsync(segment, SocketFlags.None, remote).ConfigureAwait(false);
                        recvBuffer.Advance(res.ReceivedBytes);
                        var p = recvBuffer.Pop();

#pragma warning disable CS4014 // 由于此调用不会等待，因此在调用完成前将继续执行当前方法
                        Task.Run(() => { SocketRecvData.Write(((IPEndPoint)res.RemoteEndPoint, p.Item1, res.ReceivedBytes)); });
#pragma warning restore CS4014 // 由于此调用不会等待，因此在调用完成前将继续执行当前方法

                    }
                    else
                    {
                        throw new NotSupportedException();
                    }
                }
            }
            catch (Exception)
            {

                throw;
            }
            finally
            {
                IsSocketReceiving = false;
            }
        }

        protected async void MessageReceive()
        {
            while (true)
            {
                try
                {
                    var (RemoteEndPoint, Owner, ReceivedBytes) = await SocketRecvData.ReadAsync().ConfigureAwait(false);
                    if (RemoteEndPoint == null || Owner == null)
                    {
                        //可能是多线程问题，结果是null，暂时没找到原因
                    }
                    else
                    {
                        InnerDeal(RemoteEndPoint, Owner, ReceivedBytes);
                    }
                }
                catch (Exception e)
                {
                    TraceListener?.WriteLine(e);
                }
            }
        }

        protected async void InnerDeal(IPEndPoint endPoint, IMemoryOwner<byte> owner, int receivedBytes)
        {
            using (owner)
            {
                var recvbuffer = owner.Memory.Slice(0, receivedBytes);
                if (recvbuffer.Length == 0)
                {
                    if (connected.TryGetValue(endPoint, out var remote))
                    {
                        remote.Recv0(endPoint);
                    }
                    return;
                }

                byte messageType = recvbuffer.Span[0];
                switch (messageType)
                {
                    case UdpRemoteMessageDefine.UdpAuthRequest:
                        //被动侧不处理主动侧提出的验证。
                        break;
                    case UdpRemoteMessageDefine.UdpAuthResponse:
                        authHelper.DealAnswerBuffer(endPoint, recvbuffer.Span);
                        break;
                    case UdpRemoteMessageDefine.LLData:
                        {
                            var remote = await FindRemote(endPoint).ConfigureAwait(false);
                            if (remote != null)
                            {
                                remote.RecvLLData(endPoint, recvbuffer.Span.Slice(1, receivedBytes - 1));
                            }
                        }
                        break;
                    case UdpRemoteMessageDefine.UdpData:
                        {
                            var remote = await FindRemote(endPoint).ConfigureAwait(false);
                            if (remote != null)
                            {
                                remote.RecvUdpData(endPoint, recvbuffer.Span.Slice(1, receivedBytes - 1));
                            }
                        }

                        break;
                    case UdpRemoteMessageDefine.KcpData:
                        {
                            var remote = await FindRemote(endPoint).ConfigureAwait(false);
                            if (remote != null)
                            {
                                remote.RecvKcpData(endPoint, recvbuffer.Span.Slice(1, receivedBytes - 1));
                            }
                        }
                        break;
                    default:
                        break;
                }
            }
        }

    }
}

