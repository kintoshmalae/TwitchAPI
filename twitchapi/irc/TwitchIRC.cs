﻿#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using TwitchAPI.twitchapi.auth;
using TwitchAPI.twitchapi.users;

namespace TwitchAPI.twitchapi.irc {
    public class TwitchIRC {
        private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

        public static readonly string SERVER = "irc.chat.twitch.tv";

        public TwitchIRC(string name, OAuthBaseRequest req) {
            this.oAuth = req;
            this.Nickname = name;
            this.RealName = name;
        }

        public OAuthBaseRequest oAuth { get; set; }
        public string Nickname { get; set; }
        public string RealName { get; set; }

        private TcpClient? client { get; set; }
        private SslStream? sslStream { get; set; }
        private StreamReader? reader { get; set; }

        public bool StayConnected { get; set; } 

        public object _lock = new object();

        private Dictionary<string, int> joinedChannels = new Dictionary<string, int>();

        public void joinChannel(string channel, IRCListener listener) {
            channel = channel.ToLower();
            registerListener(channel, listener);
            if (!joinedChannels.ContainsKey(channel)) {
                joinedChannels.Add(channel, 1);
                IRCJoinCommand.send(this, channel);
            } else {
                joinedChannels[channel]++;
            }
        }

        public void leaveChannel(string channel, IRCListener listener) {
            channel = channel.ToLower();
            unregisterListener(channel, listener);
            if (joinedChannels.ContainsKey(channel)) {
                joinedChannels[channel]--;
                if (joinedChannels[channel] == 0) {
                    joinedChannels.Remove(channel);
                    IRCPartCommand.send(this, channel);
                }
            }
        }

        private Dictionary<string, List<IRCListener>> Listeners { get; set; } = new Dictionary<string, List<IRCListener>>();

        private void registerListener(string channel, IRCListener listener) {
            if (!Listeners.ContainsKey(channel)) Listeners.Add(channel, new List<IRCListener>());
            if (!Listeners[channel].Contains(listener)) Listeners[channel].Add(listener);
        }

        private void unregisterListener(string channel, IRCListener listener) {
            if (!Listeners.ContainsKey(channel)) return;    // no listeners on this channel
            if (Listeners[channel].Contains(listener)) Listeners[channel].Remove(listener);
        }

        private void doConnect() {
            // Socket Connected - DO LOGIN
            oAuth.clearToken();
            IRCCapCommand.send(this, ":twitch.tv/tags");
            IRCPassCommand.send(this, oAuth);
            IRCNickCommand.send(this, Nickname);
            IRCUserCommand.send(this, Nickname, RealName);
            ReadMessage();
            foreach (KeyValuePair<string,int> item in joinedChannels) {
                string channel = item.Key;
                IRCJoinCommand.send(this, channel);
            }
        }

        private void doCreateClient() {
            lock (_lock) {
                client = new TcpClient(SERVER, 6697);
                Logger.Trace(() => { return string.Format("Connected Twitch IRC client to: <{0}:6697>", SERVER); });
                sslStream = new SslStream(client.GetStream(), false, new RemoteCertificateValidationCallback(ValidateServerCertificate), null);
                try {
                    sslStream.AuthenticateAsClient(SERVER);
                } catch (AuthenticationException e) {
                    Logger.Warn(() => { return "Exception: " + e.Message; });
                    Exception curr = e;
                    while (curr.InnerException != null) {
                        Logger.Warn(() => { return "Caused by: " + curr.InnerException.Message; });
                        curr = curr.InnerException;
                    }
                    Logger.Warn(() => { return "Authentication failed - closing the connection."; });
                    client.Close();
                    sslStream = null;
                }
                reader = new StreamReader(sslStream);
            }
        }

        public bool ConnectSSL() {
            lock (_lock) {
                if (oAuth == null) {
                    // Make sure we're able to authenticate with the server BEFORE trying to connect
                    Logger.Warn("Missing authentication to connect with server.");
                    return false;
                }
                oAuth.Scope |= TwitchScope.CHAT_READ | TwitchScope.CHAT_EDIT;   // ensure required scopes are set on request
                StayConnected = true;
                doCreateClient();
                // Socket Connected - DO LOGIN
                doConnect();
                Task.Run(() => {
                    // System.Threading.Thread.Sleep(5000);    // avoid race condition in joining channel
                    while (StayConnected) {
                        if (sslStream == null) {
                            doCreateClient();
                            doConnect();
                        }
                        while ((sslStream != null) && (sslStream.CanRead)) {
                            try {
                                lock (_lock) {
                                    string response = ReadMessage();
                                    Match m = Regex.Match(response, "(@(?<tags>[^ ]*) )?(:(?<prefix>[^ ]*) )?(?<command>[^ ]*) (?<params>.*)");
                                    string tags = m.Groups["tags"].Value;
                                    string prefix = m.Groups["prefix"].Value;
                                    string command = m.Groups["command"].Value;
                                    string p = m.Groups["params"].Value;

                                    // Logger.Trace(() => { return string.Format("Prefix <{0}> Command<{1}> Params<{2}>", prefix, command, p); });

                                    // Decode user
                                    m = Regex.Match(prefix, "((?<nick>[^!]*)[!](?<user>[^@]*)[@](?<host>.*))|(?<servername>[^!@]*)");
                                    string servername = m.Groups["servername"].Value;
                                    string nick = m.Groups["nick"].Value;
                                    string user = m.Groups["user"].Value;
                                    string host = m.Groups["host"].Value;
                                    // Logger.Trace(() => { return string.Format("Servername <{0}> Nick <{1}> User <{2}> Host <{3}>", servername, nick, user, host); });

                                    string name = string.IsNullOrWhiteSpace(nick) ? servername : nick;

                                    if (command.Equals("PRIVMSG")) {
                                        m = Regex.Match(tags, "emotes=(?<emotes>[^;]*);");
                                        string emotes = m.Groups["emotes"].Value;
                                        m = Regex.Match(p, "#(?<channel>.*) :(?<message>.*)");
                                        string channel = m.Groups["channel"].Value;
                                        string message = m.Groups["message"].Value;

                                        // Logger.Trace(() => { return string.Format("User <{0}> Message <{1}> on <{2}>", name, message, channel); });

                                        // Do onMessageReceived
                                        GetUsersRequest req = new GetUsersRequest(name);
                                        GetUsersResponse resp = req.doRequest(oAuth);
                                        if (resp.Users.Count != 0) {
                                            Logger.Trace(string.Format("Invoking event: onMessageReceived <{0}>", message));
                                            if (Listeners.ContainsKey(channel)) {
                                                foreach (IRCListener l in Listeners[channel]) {
                                                    l.onMessageReceived(resp.Users[0], message, emotes);
                                                }
                                            }
                                        }
                                    } else if (command.Equals("PING")) {
                                        IRCPongCommand.send(this);
                                    } else if (command.Equals("RECONNECT")) {
                                        Logger.Trace("Reconnecting");
                                        // IRCQuitCommand.send(this);
                                        doConnect();
                                    }
                                }
                            } catch (Exception e) {
                                Logger.Warn(e.Message);
                            }
                        }
                    }
                });
                return true;
            }
        }


        public void Disconnect() {
            lock (_lock) {
                StayConnected = false;
                if (client != null) {
                    IRCQuitCommand.send(this);
                    client.Close();
                    sslStream = null;
                }
                client = null;
                Logger.Trace("Closed connected client.");
            }
        }

        public void SendRawCommand(string cmd) {
            lock (_lock) {
                if (sslStream == null) throw new TwitchIRCException("Connect to IRC before attempting to send request.");
                if (!sslStream.CanWrite) throw new TwitchIRCException("Unable to write to connection.");
                Logger.Trace(() => { return "Sending: <" + cmd + ">"; });
                byte[] message = Encoding.UTF8.GetBytes(cmd + "\r\n");
                if (sslStream.CanWrite) sslStream.Write(message);
                sslStream.Flush();
            }
        }

        private static bool ValidateServerCertificate(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors sslPolicyErrors) {
            if (sslPolicyErrors == SslPolicyErrors.None) return true;
            Logger.Warn(() => { return "Certificate error: " + sslPolicyErrors; });
            return false;
        }

        private string ReadMessage() {
            lock (_lock) {
                if (client == null) return "";
                if (reader == null) return "";
                if (sslStream == null) {
                    reader = null;
                    return "";
                }
                // Logger.Trace("Reading message from IRC.");
                string? _result = null;
                if (!reader.EndOfStream) {
                    _result = reader.ReadLine();
                }
                if (_result == null) {
                    Logger.Trace("No message to receive.");
                    _result = "";
                } else {
                    Logger.Trace(() => { return "MSG: <" + _result + ">"; });
                }
                return _result;
            }
        }
    }

    public interface IRCListener {
        public void onMessageReceived(UserDetails user, string message, string emotes);
    }
}
