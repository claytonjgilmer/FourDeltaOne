﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

using NPx;

namespace NPx
{
    public class NPAuthAPI
    {
/*
        private SocketAsyncEventArgs _acceptEventArgs;
*/
        private Socket _socket;
        private Thread _thread;
        private byte[] _buffer;
        private bool _connected = false;
        private static NPAuthAPI _instance;

        public NPAuthAPI()
        {
            _buffer = new byte[2048];

            _instance = this;
        }

        public void Start()
        {
            Log.Info("Starting NPAuthAPI");

            _thread = new Thread(Run);
            _thread.Start();
        }

        private void Run()
        {
            while (true)
            {
                if (!_connected)
                {
                    Connect();
                }

                Thread.Sleep(5000);
            }
        }

        private void Connect()
        {
            Log.Info("Connecting to NPAuthAPI...");

            _connected = false;

            try
            {
                if (_socket != null)
                {
                    _socket.Close();
                }

                _socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                _socket.Connect("127.0.0.1", 3105);
            }
            catch (SocketException e)
            {
                // annoyingly long error messages are annoying
                //Log.Error("Connection failed: " + e.ToString());
                Log.Error("Could not connect to NPAuthAPI");
                return;
            }

            _socket.BeginReceive(_buffer, 0, _buffer.Length, SocketFlags.None, Socket_Receive, null);

            _connected = true;

            Log.Info("Connected to NPAuthAPI.");
        }

        private void Socket_Receive(IAsyncResult async)
        {
            try
            {
                int length = _socket.EndReceive(async);

                if (length == 0)
                {
                    _connected = false;
                    return;
                }

                OnReceive(_buffer, length);

                _socket.BeginReceive(_buffer, 0, _buffer.Length, SocketFlags.None, Socket_Receive, null);
            }
            catch (SocketException e)
            {
                _connected = false;
            }
        }

        private void Socket_Send(IAsyncResult async)
        {
            try
            {
                _socket.EndSend(async);
            }
            catch { }
        }

        private class SessionData
        {
            public RPCAuthenticateWithTokenMessage message;
            public NPHandler client;
        }

        private static Dictionary<string, SessionData> _requests = new Dictionary<string, SessionData>();

        public static void RequestAuthForToken(RPCAuthenticateWithTokenMessage message, NPHandler client)
        {
            if (!_instance._connected)
            {
                _instance.SendAuthResult(message, client, -1, 0);
                return;
            }

            var token = Encoding.UTF8.GetString(message.Message.token);
            var request = new SessionData()
            {
                message = message,
                client = client
            };

            _requests[token] = request;

            var data = string.Format("checkSession {0} {1}", token, client.Address.Address.ToString());
            var buffer = Encoding.UTF8.GetBytes(data);

            _instance._socket.BeginSend(buffer, 0, buffer.Length, SocketFlags.None, _instance.Socket_Send, null);
        }

        private void OnReceive(byte[] buffer, int length)
        {
            var line = Encoding.UTF8.GetString(buffer, 0, length).Trim();
            var parts = line.Split(' ');

            switch (parts[0])
            {
                case "sessionResult":
                    try
                    {
                        if (_requests.ContainsKey(parts[1]))
                        {
                            var request = _requests[parts[1]];
                            SendAuthResult(request.message, request.client, int.Parse(parts[3]), int.Parse(parts[4]));
                            _requests.Remove(parts[1]);
                        }
                    }
                    catch (Exception e)
                    {
                        Log.Error(e.ToString());
                    }
                    break;

            }
        }

        private void SendAuthResult(RPCAuthenticateWithTokenMessage message, NPHandler client, int userID, int group)
        {
            var reply = message.MakeResponse<AuthenticateResultMessage>(client);

            reply.Message.npid = (ulong)0;
            reply.Message.sessionToken = new byte[16];

            if (userID > 0)
            {
                var npid = (0x110000100000000 | (uint)userID);

                var existingClient = NPSocket.GetClient(npid);
                //NPHandler existingClient = null;
                if (existingClient != null)
                {
                    //reply.Message.result = 4;
                    existingClient.CloseConnection(true);
                }

                reply.Message.result = 0;
                reply.Message.npid = (ulong)npid;
                reply.Message.sessionToken = message.Message.token;

                client.Authenticated = true;
                client.NPID = npid;
                client.GroupID = group;
                client.SessionToken = Encoding.UTF8.GetString(message.Message.token);
            }
            else if (userID == 0)
            {
                reply.Message.result = 1;
            }
            else if (userID == -1)
            {
                reply.Message.result = 2;
            }

            reply.Send();

            if (group > 0)
            {
                var groupReply = new NPRPCResponse<AuthenticateUserGroupMessage>(client);
                groupReply.Message.groupID = group;
                groupReply.Send();
            }

            ThreadPool.QueueUserWorkItem(delegate(object cliento)
            {
                try
                {
                    var cclient = (NPHandler)cliento;
                    var uid = (int)(cclient.NPID & 0xFFFFFFFF);

                    var db = XNP.Create();
                    var result = from platform in db.ExternalPlatforms
                                 where platform.UserID == uid && platform.PlatformAuthenticated == 1
                                 select platform.PlatformID;

                    var value = 1;

                    if (result.Count() > 0)
                    {
                        value = 0;
                    }

                    Thread.Sleep(600);
                    
                    var linkReply = new NPRPCResponse<AuthenticateExternalStatusMessage>(cclient);
                    linkReply.Message.status = value;
                    linkReply.Send();
                }
                catch (Exception ex)
                {
                    Log.Error(ex.ToString());
                }
            }, client);
        }
    }
}
