﻿using Megumin.Remote;
using Megumin.Remote.Simple;
using Megumin.Remote.Test;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Net.Remote;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace UnitTest
{
    [TestClass]
    public class UnitUdpTest
    {
        private UdpRemote Create()
        {
            return new EchoUdp();
        }

        [TestMethod]
        public void TestUdpRemote()
        {
            const int port = 65432;
            UdpRemoteListener listener = new UdpRemoteListener(port);
            listener.ListenAsync(Create);

            UdpRemote client = new UdpRemote();
            client.ConnectIPEndPoint = new System.Net.IPEndPoint(IPAddress.Loopback, port);
            client.ClientRecv(port - 1);
            EchoTest(client);
            listener.Stop();
        }


        public void EchoTest(IRemote remote)
        {
            MessageLUT.Regist(new TestPacket1());
            MessageLUT.Regist(new TestPacket2());

            TestPacket1 packet1 = new TestPacket1() { Value = 5645645 };
            var ret = remote.SendSafeAwait<TestPacket1>(packet1, 
                options: SendOption.Never).ConfigureAwait(false)
                .GetAwaiter().GetResult();
            Assert.AreEqual(packet1.Value, ret.Value);
        }
    }
}
