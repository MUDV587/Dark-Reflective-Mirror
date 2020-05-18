﻿using DarkReflectiveMirror;
using DarkRift;
using DarkRift.Client.Unity;
using Mirror;
using Mirror.Websocket;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using UnityEditor.PackageManager.Requests;
using UnityEngine;
using UnityEngine.Events;

[RequireComponent(typeof(UnityClient))]
public class DarkReflectiveMirrorTransport : Transport
{
    public string relayIP = "34.72.21.213";
    public ushort relayPort = 4296;
    [Tooltip("If your relay server has a password enter it here, or else leave it blank.")]
    public string relayPassword;
    [Header("Server Data")]
    public int maxServerPlayers = 10;
    public string serverName = "My awesome server!";
    public string extraServerData = "Cool Map 1";
    [Tooltip("This allows you to make 'private' servers that do not show up on the built in server list.")]
    public bool showOnServerList = true;
    public UnityEvent serverListUpdated;
    public List<RelayServerInfo> relayServerList = new List<RelayServerInfo>();
    [HideInInspector] public bool isAuthenticated = false;
    public const string Scheme = "darkrelay";
    private BiDictionary<ushort, int> connectedClients = new BiDictionary<ushort, int>();
    private UnityClient drClient;
    private bool isClient;
    private bool isConnected;
    private bool isServer;
    [Header("Current Server Info")]
    [Tooltip("This what what others use to connect, as soon as you start a server this will be valid. It can even be 0 if you are the first client on the relay!")]
    public ushort serverID;
    private bool shutdown = false;
    private int currentMemberID = 0;

    void Awake()
    {
        IPAddress ipAddress;
        if (!IPAddress.TryParse(relayIP, out ipAddress)) { ipAddress = Dns.GetHostEntry(relayIP).AddressList[0]; }

        drClient = GetComponent<UnityClient>();

        if (drClient.ConnectionState == ConnectionState.Disconnected)
            drClient.Connect(IPAddress.Parse(ipAddress.ToString()), relayPort, true);

        drClient.Disconnected += Client_Disconnected;
        drClient.MessageReceived += Client_MessageReceived;
    }

    private void Client_MessageReceived(object sender, DarkRift.Client.MessageReceivedEventArgs e)
    {
        try
        {
            using (Message message = e.GetMessage())
            using (DarkRiftReader reader = message.GetReader())
            {
                OpCodes opCode = (OpCodes)message.Tag;
                switch (opCode)
                {
                    case OpCodes.Authenticated:
                        isAuthenticated = true;
                        break;
                    case OpCodes.AuthenticationRequest:
                        using (DarkRiftWriter writer = DarkRiftWriter.Create())
                        {
                            writer.Write(relayPassword);
                            using (Message sendAuthenticationResponse = Message.Create((ushort)OpCodes.AuthenticationResponse, writer))
                                drClient.Client.SendMessage(sendAuthenticationResponse, SendMode.Reliable);
                        }
                        break;
                    case OpCodes.GetData:
                        int dataLength = reader.ReadInt32();
                        byte[] receivedData = new byte[dataLength];
                        System.Buffer.BlockCopy(reader.ReadBytes(), 0, receivedData, 0, dataLength);

                        if (isServer)
                            OnServerDataReceived?.Invoke(connectedClients.GetByFirst(reader.ReadUInt16()), new ArraySegment<byte>(receivedData), e.SendMode == SendMode.Unreliable ? 1 : 0);

                        if (isClient)
                            OnClientDataReceived?.Invoke(new ArraySegment<byte>(receivedData), e.SendMode == SendMode.Unreliable ? 1 : 0);

                        break;
                    case OpCodes.ServerLeft:

                        if (isClient)
                        {
                            isClient = false;
                            OnClientDisconnected?.Invoke();
                        }

                        break;
                    case OpCodes.PlayerDisconnected:

                        if (isServer)
                        {
                            ushort user = reader.ReadUInt16();
                            OnServerDisconnected?.Invoke(connectedClients.GetByFirst(user));
                        }

                        break;
                    case OpCodes.RoomCreated:
                        serverID = reader.ReadUInt16();
                        isConnected = true;
                        break;
                    case OpCodes.ServerJoined:
                        ushort clientID = reader.ReadUInt16();

                        if (isClient)
                        {
                            isConnected = true;
                            OnClientConnected?.Invoke();
                        }

                        if (isServer)
                        {
                            connectedClients.Add(clientID, currentMemberID);
                            OnServerConnected?.Invoke(currentMemberID);
                            currentMemberID++;
                        }
                        break;
                    case OpCodes.ServerListResponse:
                        int serverListCount = reader.ReadInt32();
                        relayServerList.Clear();
                        for(int i = 0; i < serverListCount; i++)
                        {
                            relayServerList.Add(new RelayServerInfo()
                            {
                                serverName = reader.ReadString(),
                                currentPlayers = reader.ReadInt32(),
                                maxPlayers = reader.ReadInt32(),
                                serverID = reader.ReadUInt16(),
                                serverData = reader.ReadString()
                            });
                        }
                        serverListUpdated?.Invoke();
                        break;

                }
            }
        }
        catch {
            // Server shouldnt send messed up data but we do have an unreliable channel, so eh.
        }
    }

    public void UpdateServerData(string serverData, int maxPlayers)
    {
        if (!isServer)
            return;

        using (DarkRiftWriter writer = DarkRiftWriter.Create())
        {
            writer.Write(serverData);
            writer.Write(maxPlayers);
            using (Message sendUpdateRequest = Message.Create((ushort)OpCodes.UpdateRoomData, writer))
                drClient.SendMessage(sendUpdateRequest, SendMode.Reliable);
        }
    }

    public void RequestServerList()
    {
        // Start a coroutine just in case the client tries to request the server list too early.
        StopCoroutine(RequestServerListWait());
        StartCoroutine(RequestServerListWait());
    }

    IEnumerator RequestServerListWait()
    {
        int tries = 0;
        // Wait up to a maximum of 10 seconds before giving up.
        while(tries < 40)
        {
            if (isAuthenticated)
            {
                using (DarkRiftWriter writer = DarkRiftWriter.Create())
                {
                    using (Message sendServerListRequest = Message.Create((ushort)OpCodes.RequestServers, writer))
                        drClient.SendMessage(sendServerListRequest, SendMode.Reliable);
                }
                break;
            }

            yield return new WaitForSeconds(0.25f);
        }
    }

    private void Client_Disconnected(object sender, DarkRift.Client.DisconnectedEventArgs e)
    {
        isAuthenticated = false;
        if (isClient)
        {
            isClient = false;
            isConnected = false;
            OnClientDisconnected?.Invoke();
        }
    }

    public override bool Available()
    {
        return drClient.ConnectionState == DarkRift.ConnectionState.Connected;
    }

    public override void ClientConnect(string address)
    {
        ushort hostID = 0;
        if (!Available() || !ushort.TryParse(address, out hostID))
        {
            Debug.Log("Not connected to relay or address is not a proper ID!");
            OnClientDisconnected?.Invoke();
            return;
        }

        if(isClient || isServer)
        {
            Debug.Log("Cannot connect while hosting/already connected.");
            return;
        }

        // Make sure the client is authenticated before trying to join a room.
        int timeOut = 0;
        while (true)
        {
            timeOut++;
            drClient.Dispatcher.ExecuteDispatcherTasks();
            if (isAuthenticated || timeOut >= 100)
                break;

            System.Threading.Thread.Sleep(100);
        }

        if (timeOut >= 100 && !isAuthenticated)
        {
            Debug.Log("Failed to authenticate in time with backend! Make sure your secret key and IP/port are correct.");
            OnClientDisconnected?.Invoke();
            return;
        }

        isClient = true;
        isConnected = false;

        // Tell the server we want to join a room
        using (DarkRiftWriter writer = DarkRiftWriter.Create())
        {
            writer.Write(hostID);
            using (Message sendJoinMessage = Message.Create((ushort)OpCodes.JoinServer, writer))
                drClient.Client.SendMessage(sendJoinMessage, SendMode.Reliable);
        }
    }

    public override void ClientConnect(Uri uri)
    {
        ClientConnect(uri.Host);
    }

    public override bool ClientConnected()
    {
        return isConnected;
    }

    public override void ClientDisconnect()
    {
        isClient = false;

        using (DarkRiftWriter writer = DarkRiftWriter.Create())
        {
            using (Message sendLeaveMessage = Message.Create((ushort)OpCodes.LeaveRoom, writer))
                drClient.Client.SendMessage(sendLeaveMessage, SendMode.Reliable);
        }
    }

    public override bool ClientSend(int channelId, ArraySegment<byte> segment)
    {
        // Only channels are 0 (reliable), 1 (unreliable)

        using (DarkRiftWriter writer = DarkRiftWriter.Create())
        {
            writer.Write(segment.Count);
            writer.Write(segment.Array.Take(segment.Count).ToArray());
            using (Message sendDataMessage = Message.Create((ushort)OpCodes.SendData, writer))
                drClient.Client.SendMessage(sendDataMessage, channelId == 0 ? SendMode.Reliable : SendMode.Unreliable);
        }

        return true;
    }

    public override int GetMaxPacketSize(int channelId = 0)
    {
        return 1000;
    }

    public override bool ServerActive()
    {
        return isServer;
    }

    public override bool ServerDisconnect(int connectionId)
    {
        ushort userID;
        if (connectedClients.TryGetBySecond(connectionId, out userID))
        {
            using (DarkRiftWriter writer = DarkRiftWriter.Create())
            {
                writer.Write(userID);
                using (Message sendKickMessage = Message.Create((ushort)OpCodes.KickPlayer, writer))
                    drClient.Client.SendMessage(sendKickMessage, SendMode.Reliable);
            }

            return true;
        }
        return false;
    }

    public override string ServerGetClientAddress(int connectionId)
    {
        return connectedClients.GetBySecond(connectionId).ToString();
    }

    public override bool ServerSend(List<int> connectionIds, int channelId, ArraySegment<byte> segment)
    {
        // TODO: Optimize
        List<ushort> clients = new List<ushort>();

        for (int i = 0; i < connectionIds.Count; i++)
        {
            clients.Add(connectedClients.GetBySecond(connectionIds[i]));
            // Including more than 10 client ids per single packet to the relay server could get risky with MTU so less risks if we split it into chunks of 1 packet per 10 players its sending to the server
            if (clients.Count >= 10)
            {
                ServerSendData(clients, segment, channelId);
                clients.Clear();
            }
        }

        if (clients.Count > 0)
        {
            ServerSendData(clients, segment, channelId);
        }

        return true;
    }

    void ServerSendData(List<ushort> clients, ArraySegment<byte> data, int channelId)
    {
        using (DarkRiftWriter writer = DarkRiftWriter.Create())
        {
            writer.Write(data.Count);
            writer.Write(data.Array.Take(data.Count).ToArray());
            writer.Write(clients.ToArray());
            using (Message sendDataMessage = Message.Create((ushort)OpCodes.SendData, writer))
                drClient.Client.SendMessage(sendDataMessage, channelId == 0 ? SendMode.Reliable : SendMode.Unreliable);
        }
    }

    public override void ServerStart()
    {
        if (!Available())
        {
            Debug.Log("Not connected to relay, server failed to start!");
            return;
        }

        if (isClient || isServer)
        {
            Debug.Log("Cannot connect while hosting/already connected.");
            return;
        }

        isServer = true;
        isConnected = false;
        currentMemberID = 1;

        // Wait to make sure we are authenticated with the server before actually trying to request creating a room.
        int timeOut = 0;
        while (true)
        {
            timeOut++;
            drClient.Dispatcher.ExecuteDispatcherTasks();
            if (isAuthenticated || timeOut >= 100)
                break;

            System.Threading.Thread.Sleep(100);
        }

        if(timeOut >= 100)
        {
            Debug.Log("Failed to authenticate in time with backend! Make sure your secret key and IP/port are correct.");
            return;
        }

        using (DarkRiftWriter writer = DarkRiftWriter.Create())
        {
            writer.Write(maxServerPlayers);
            writer.Write(serverName);
            writer.Write(showOnServerList);
            writer.Write(extraServerData);
            using (Message sendStartMessage = Message.Create((ushort)OpCodes.CreateRoom, writer))
                drClient.Client.SendMessage(sendStartMessage, SendMode.Reliable);
        }

        // Wait until server is actually ready or 10 seconds have passed and server failed
        timeOut = 0;
        while (true)
        {
            timeOut++;
            drClient.Dispatcher.ExecuteDispatcherTasks();
            if (isConnected || timeOut >= 100)
                break;
            System.Threading.Thread.Sleep(100);
        }

        if(timeOut >= 100)
        {
            Debug.LogError("Failed to create the server on the relay. Are you connected? Double check the secret key and IP/port.");
        }
    }

    public override void ServerStop()
    {
        if (isServer)
        {
            isServer = false;
            using (DarkRiftWriter writer = DarkRiftWriter.Create())
            {
                using (Message sendStopMessage = Message.Create((ushort)OpCodes.LeaveRoom, writer))
                    drClient.Client.SendMessage(sendStopMessage, SendMode.Reliable);
            }
        }
    }

    public override Uri ServerUri()
    {
        UriBuilder builder = new UriBuilder();
        builder.Scheme = Scheme;
        builder.Host = serverID.ToString();
        return builder.Uri;
    }

    public override void Shutdown()
    {
        shutdown = true;
        drClient.Disconnect();
    }
}

public struct RelayServerInfo
{
    public string serverName;
    public int currentPlayers;
    public int maxPlayers;
    public ushort serverID;
    public string serverData;
}

enum OpCodes { Default = 0, RequestID = 1, JoinServer = 2, SendData = 3, GetID = 4, ServerJoined = 5, GetData = 6, CreateRoom = 7, ServerLeft = 8, PlayerDisconnected = 9, RoomCreated = 10, LeaveRoom = 11, KickPlayer = 12, AuthenticationRequest = 13, AuthenticationResponse = 14, RequestServers = 15, ServerListResponse = 16, Authenticated = 17, UpdateRoomData = 18 }
