﻿using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Net.Remote;

namespace Megumin.Remote
{
    public class KcpRemoteListener : UdpRemoteListener
    {
        public KcpRemoteListener(int port, AddressFamily addressFamily = AddressFamily.InterNetworkV6)
            : base(port, addressFamily)
        {
        }

        protected override async void InnerDeal(IPEndPoint endPoint, byte[] recvbuffer)
        {
            byte messageType = recvbuffer[0];
            switch (messageType)
            {
                case UdpRemoteMessageDefine.UdpAuthRequest:
                    //被动侧不处理主动侧提出的验证。
                    break;
                case UdpRemoteMessageDefine.UdpAuthResponse:
                    DealAnswerBuffer(endPoint, recvbuffer);
                    break;
                case UdpRemoteMessageDefine.LLMsg:
                    //Test消息 不通过Kcp协议处理
                    //todo
                    break;
                case UdpRemoteMessageDefine.Common:
                    var remote = await FindRemote(endPoint).ConfigureAwait(false);
                    if (remote != null)
                    {
                        remote.ServerSideRecv(endPoint, recvbuffer, 1, recvbuffer.Length - 1);
                    }
                    break;
                default:
                    break;
            }
        }

        protected override UdpRemote CreateNew(IPEndPoint endPoint, UdpAuthResponse answer)
        {
            KcpRemote remote = CreateFunc?.Invoke() as KcpRemote;
            if (remote == null)
            {
                remote = new KcpRemote();
            }
            remote.InitKcp(answer.KcpChannel);
            remote.IsVaild = true;
            remote.ConnectIPEndPoint = endPoint;
            remote.GUID = answer.Guid;
            remote.Password = answer.Password;
            lut.Add(remote.GUID, remote);
            connected.Add(endPoint, remote);
            return remote;
        }
    }
}
