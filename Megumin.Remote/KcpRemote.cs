﻿//using System;
//using System.Collections.Generic;
//using System.Net.Sockets.Kcp;
//using System.Text;

using System;
using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Megumin.Remote
{

    /// <summary>
    /// todo 连接kcpid
    /// </summary>
    public partial class KcpRemote : UdpRemote
    {
        IKcpIO kcp = null;
        protected int KcpIOChannel { get; set; }

        public void InitKcp(int kcpChannel)
        {
            if (kcp == null)
            {
                KcpIOChannel = kcpChannel;
                kcp = new FakeKcpIO();
                KcpOutput();
                KCPRecv();
            }
        }

        // 认证===================================================================
        protected override void DealAuthBuffer(IPEndPoint endPoint, byte[] recvbuffer)
        {
            var auth = UdpAuthRequest.Deserialize(recvbuffer);
            //创建认证回复消息
            UdpAuthResponse answer = new UdpAuthResponse();

            if (Password == -1)
            {
                this.GUID = auth.Guid;
                this.Password = auth.Password;
                answer.IsNew = true;
            }

            answer.Guid = this.GUID;
            answer.Password = Password;
            answer.KcpChannel = KcpIOChannel;
            byte[] buffer = new byte[UdpAuthResponse.Length];
            answer.Serialize(buffer);
            Client.SendTo(buffer, 0, UdpAuthResponse.Length, SocketFlags.None, endPoint);
        }
        // 发送===================================================================
        protected Writer kcpout = new Writer(65535);
        async void KcpOutput()
        {
            while (true)
            {
                kcpout.WriteHeader(UdpRemoteMessageDefine.Common);
                await kcp.Output(kcpout);
                var (buffer, lenght) = kcpout.Pop();
                SocketSend(buffer, lenght);
            }
        }

        protected override void Send(int rpcID, object message, object options = null)
        {
            if (TrySerialize(SendWriter, rpcID, message, options))
            {
                var (buffer, lenght) = SendWriter.Pop();
                kcp.Send(buffer.Memory.Span.Slice(0, lenght));
                buffer.Dispose();
            }
            else
            {
                var (buffer, lenght) = SendWriter.Pop();
                buffer.Dispose();
            }
        }

        ///接收===================================================================

        async void KCPRecv()
        {
            while (true)
            {
                var res = await kcp.Recv();
                ProcessBody(res);
            }
        }

        protected override void InnerDeal(IPEndPoint endPoint, byte[] recvbuffer, int start, int count)
        {
            byte messageType = recvbuffer[start];
            switch (messageType)
            {
                case UdpRemoteMessageDefine.UdpAuthRequest:
                    DealAuthBuffer(endPoint, recvbuffer);
                    break;
                case UdpRemoteMessageDefine.UdpAuthResponse:
                    //主动侧不处理验证应答。
                    break;
                case UdpRemoteMessageDefine.LLMsg:
                    ///Test消息不通过Kcp处理
                    RecvLLMsg(recvbuffer, start + 1, count - 1);
                    break;
                case UdpRemoteMessageDefine.Common:
                    RecvPureBuffer(recvbuffer, start + 1, count - 1);
                    break;
                default:
                    break;
            }
        }

        public void RecvLLMsg(byte[] buffer, int start, int count)
        {
            ProcessBody(new ReadOnlySequence<byte>(buffer, start, count));
        }

        protected override void RecvPureBuffer(byte[] buffer, int start, int count)
        {
            kcp.Input(new ReadOnlySpan<byte>(buffer, start, count));
        }

        protected override void RecvPureBuffer(ReadOnlySequence<byte> sequence)
        {
            kcp.Input(sequence);
        }
    }
}
