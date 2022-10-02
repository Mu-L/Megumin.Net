using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using System.Net.Sockets;
using System;
using System.Net;
using TMPro;
using System.Threading.Tasks;

public class UdpSocketTest : MonoBehaviour
{
    public InputField inputIP;
    public InputField inputPort;

    public InputField localIP;
    public InputField localPort;

    public InputField globalIP;
    public InputField globalPort;

    public Button Send;
    private Socket socket;

    // Start is called before the first frame update
    void Start()
    {
        socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        Send.onClick.AddListener(SendByte);

        if (socket.LocalEndPoint is IPEndPoint endPoint)
        {
            localIP.text = endPoint.Address.ToString();
            localIP.text = endPoint.Port.ToString();
        }

        GetIP();
    }

    
    public async void GetIP()
    {
        var gip = await IPAddressExtension_A6F086FB3EE3403BB5033720C34DA414.GetWANIP();
        if (gip != null)
        {
            globalIP.text = gip.ToString();
            //globalPort.text = gip.Port.ToString();
            ping = new Ping(gip.ToString());
            System.Net.NetworkInformation.Ping ping2 = new System.Net.NetworkInformation.Ping();
            Ping(gip, ping2);
        }
    }

    private async void Ping(IPAddress gip, System.Net.NetworkInformation.Ping ping2)
    {
        var resp = await ping2.SendPingAsync(gip);
        if (resp.Status == System.Net.NetworkInformation.IPStatus.Success)
        {
            PingText.text = resp.RoundtripTime.ToString();
        }
        else
        {
            PingText.text = resp.Status.ToString();
        }
        await Task.Delay(500);
        Ping(gip, ping2);
    }

    public TextMeshProUGUI PingText;
    private Ping ping;
    //public void Update()
    //{
    //    if (ping != null && ping.isDone)
    //    {
    //        PingText.text = ping.time.ToString();
    //    }
    //}

    static byte[] conn = new byte[8];


    private void SendByte()
    {
        IPEndPoint target = new IPEndPoint(
            IPAddress.Parse(inputIP.text),
            int.Parse(inputPort.text));
        //socket.Bind(new IPEndPoint(IPAddress.Any,65432));
        socket.SendTo(conn, target);

        if (socket.LocalEndPoint is IPEndPoint endPoint)
        {
            localIP.text = endPoint.Address.ToString();
            localPort.text = endPoint.Port.ToString();
        }
    }
}