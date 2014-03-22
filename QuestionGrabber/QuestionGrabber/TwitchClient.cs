using IrcDotNet;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Serialization;

namespace QuestionGrabber
{
    /// <summary>
    /// A Twitch.tv IRC client.
    /// 
    /// This class represents a single channel on the twitch.tv IRC server.  You may find
    /// it surprising that we do not allow multiple chat channels.  This is because twitch
    /// IRC gives asynchronous events for things like informing if a user is a subscriber,
    /// turbo user, or if they were banned or timed out.  Unfortunately, this is not done
    /// for a specific IRC channel, so there is no way to associate the chat clear/subscriber
    /// with a particular chat channel.
    /// 
    /// The end effect is that we need to create a new IRC session for every channel we
    /// monitor (if you care about accurate per-channel timeouts and subscribers...which
    /// we do).  This means if you want to monitor more than one channel at once, you need
    /// to have more than one TwitchClient instance (and thus multiple IRC connections).
    /// 
    /// Note that aside from the Connect function, all events and callbacks occur
    /// asynchronously, and any event handlers you register will be called from another
    /// thread than the one creating it.
    /// </summary>
    class TwitchClient
    {
        #region Events
        /// <summary>
        /// Fired when chat was cleared for a specific user (this means they were either
        /// timed out or banned, but there isn't a way to know which...or how long the
        /// timeout was).
        /// </summary>
        public event UserEventHandler InformChatClear;

        /// <summary>
        /// Fired when twitch informs us that a user is a Twitch Turbo user.
        /// </summary>
        public event UserEventHandler InformTurbo;

        /// <summary>
        /// Fired when twitch informs us that a user is a subscriber to this channel.
        /// </summary>
        public event UserEventHandler InformSubscriber;

        /// <summary>
        /// Fired when a user subscribes to the channel.
        /// </summary>
        public event UserEventHandler UserSubscribed;

        /// <summary>
        /// Fired when a chat message is received.
        /// </summary>
        public event MessageHandler MessageReceived;

        /// <summary>
        /// Event handler for when messages are received from the chat channel.
        /// </summary>
        /// <param name="msg">The message received.</param>
        public delegate void MessageHandler(TwitchClient sender, IrcMessageEventArgs msg);

        /// <summary>
        /// Event handler for when user-related events occur.
        /// </summary>
        /// <param name="user">The user in question.</param>
        public delegate void UserEventHandler(TwitchClient sender, string user);

        #endregion

        /// <summary>
        /// ChannelData keeps track of whether users are moderators, subscribers, or
        /// twitch turbo users.  Note that this does not contain a list of ALL subs
        /// and mods, it simply keeps track of every user we've been informed of their
        /// status.
        /// </summary>
        public ChannelData ChannelData { get { return m_chanData; } }

        /// <summary>
        /// Returns the name of the stream we are connected to.
        /// </summary>
        public string Stream { get { return m_stream; } }

        /// <summary>
        /// Returns true if the stream is alive, false otherwise.  Note that this
        /// property is asynchronously updated, and the value may be out of date
        /// by up to two minutes.  
        /// </summary>
        public bool IsStreamLive
        {
            get
            {
                CheckAlive();
                return m_alive;
            }
        }

        /// <summary>
        /// Connect to the given stream, returns true if we successfully connected.  Note
        /// that this function executes synchronously, and will block until fully connected
        /// to the IRC server.
        /// </summary>
        /// <param name="stream">The stream to connect to.</param>
        /// <param name="user">The twitch username this connection will use.</param>
        /// <param name="auth">The twitch API token used to log in.  This must begin with 'oauth:'.</param>
        public bool Connect(string stream, string user, string auth)
        {
            // We'll set the stream status to alive initially, but we'll immediately go check
            // the status on a background thread.
            m_alive = true;
            Task t = new Task(CheckAliveWorker);
            t.Start();

            // Create client and hook up events.
            string server = "irc.twitch.tv";
            int port = 6667;
            m_stream = stream.ToLower(); ;

            WriteDiagnosticMessage("Attempting to connect to server...");

            m_client = new IrcClient();
            m_client.FloodPreventer = new IrcStandardFloodPreventer(4, 2000);

            m_client.Connected += client_Connected;
            m_client.ConnectFailed += client_ConnectFailed;
            m_client.Disconnected += client_Disconnected;
            m_client.Error += client_Error;
            m_client.Registered += client_Registered;
            m_client.ErrorMessageReceived += client_ErrorMessageReceived;

            // Connect to server.
            IPHostEntry hostEntry = Dns.GetHostEntry(server);
            m_client.Connect(new IPEndPoint(hostEntry.AddressList[0], port), false, new IrcUserRegistrationInfo()
            {
                NickName = user,
                UserName = user,
                RealName = user,
                Password = auth
            });

            // Wait for the server to connect.  The connect function on client operates asynchronously, so we
            // wait on s_connectedEvent which is set when client_Connected is called.
            if (!m_connectedEvent.Wait(10000))
            {
                WriteDiagnosticMessage("Connection to '{0}' timed out.", server);
                return false;
            }

            /// Wait for the client to be registered.
            if (!s_registeredEvent.Wait(10000))
            {
                WriteDiagnosticMessage("Registration timed out.", server);
                return false;
            }

            // Attempt to join the channel.  We'll try for roughly 10 seconds to join.  This really shouldn't ever fail.
            m_client.Channels.Join("#" + m_stream);
            int max = 40;
            while (m_client.Channels.Count == 0 && !s_joinedEvent.Wait(250))
            {
                max--;
                if (max < 0)
                {
                    WriteDiagnosticMessage("Failed to connect to {0}  Please press Reconnect.", m_stream);
                    return false;
                }
            }

            WriteDiagnosticMessage("Connected to channel {0}.", m_stream);

            // This command tells twitch that we are a chat bot capable of understanding subscriber/turbo/etc
            // messages.  Without sending this raw command, we would not get that data.
            m_client.SendRawMessage("TWITCHCLIENT 2");

            return true;
        }

        /// <summary>
        /// This is called when someone sends a message to chat.
        /// </summary>
        /// <param name="sender">The IrcDotNet channel.</param>
        /// <param name="e">The user.</param>
        void channel_MessageReceived(object sender, IrcMessageEventArgs e)
        {
            CheckModeratorStatus(e.Source);

            // Twitchnotify is how subscriber messages "Soandso just subscribed!" comes in:
            if (e.Source.Name.Equals("twitchnotify", StringComparison.CurrentCultureIgnoreCase))
            {
                string text = e.Text;

                int i = text.IndexOf(" just");
                if (i > 0)
                {
                    OnUserSubscribed(text.Substring(0, i));
                    return;
                }
            }

            OnMessageReceived(e);
        }

        /// <summary>
        /// Called when a message is received.  The only private message we care about are ones
        /// from jtv, which is how we know users are subscribers, turbo users, or if they get
        /// timed out.
        /// </summary>
        /// <param name="sender">IrcDotNet client.</param>
        /// <param name="e">IRC message event args.</param>
        private void client_LocalUser_MessageReceived(object sender, IrcMessageEventArgs e)
        {
            if (e.Source.Name.Equals("jtv", StringComparison.CurrentCultureIgnoreCase))
            {
                string[] items = e.Text.ToLower().Split(' ');
                if (items.Length == 3)
                {
                    string cmd = items[0];
                    string user = items[1];
                    string param = items[2];

                    if (cmd == "specialuser")
                    {
                        if (param == "subscriber")
                        {
                            m_chanData.AddSubscriber(user);
                            OnInformSubscriber(user);
                        }
                        else if (param == "turbo")
                        {
                            m_chanData.AddTurbo(user);
                            OnInformTurbo(user);
                        }
                    }
                    else if (cmd == "clearchat")
                    {
                        // This is a timeout or ban.
                        OnChatClear(user);
                    }
                }
            }
        }

        #region Diagnostic Events
        /// <summary>
        /// Fired when a diagnostic message is generated by TwitchClient.
        /// </summary>
        public event DiagnosticHandler StatusUpdate;

        /// <summary>
        /// Fired when an exception occurs within IrcDotNet, which hopefully should never happen.
        /// </summary>
        public event ErrorHandler ErrorOccurred;

        /// <summary>
        /// Used to report diagnostic messages to listeners (that is, informative messages or
        /// errors used to track down problems with the TwitchClient).
        /// </summary>
        /// <param name="message">The diagnostic message reported.</param>
        public delegate void DiagnosticHandler(TwitchClient sender, string message);

        /// <summary>
        /// Used to report IRC errors.  This callback should really never happen unless there
        /// is a bug in IrcDotNet.
        /// </summary>
        /// <param name="error">The error event reported.</param>
        public delegate void ErrorHandler(TwitchClient sender, IrcErrorEventArgs error);
        #endregion

        #region Helpers
        /// <summary>
        /// Checks whether the stream is alive, updating m_alive every two minutes.
        /// Note this is done on a background thread to avoid blocking on the UI
        /// thread.
        /// </summary>
        void CheckAlive()
        {
            TimeSpan diff = DateTime.Now - m_lastCheck;
            if (diff.Minutes < 2)
                return;

            Task t = new Task(CheckAliveWorker);
            t.Start();
        }

        void CheckAliveWorker()
        {
            try
            {
                var req = (System.Net.HttpWebRequest)System.Net.HttpWebRequest.Create(@"http://api.justin.tv/api/stream/list.json?channel=" + m_stream);
                req.UserAgent = "Question Grabber Bot/0.0.0.1";
                var response = req.GetResponse();
                var fromStream = response.GetResponseStream();

                StreamReader reader = new StreamReader(fromStream);
                string result = reader.ReadToEnd();
                m_alive = result != "[]";
            }
            catch (Exception)
            {
                m_alive = false;
            }
        }


        internal void Dispose()
        {
            if (m_client != null)
            {
                m_client.Disconnect();
                m_client.Dispose();
                m_client = null;
            }
        }
        private void CheckModeratorStatus(IIrcMessageSource msgSource)
        {
            IrcUser ircuser = msgSource as IrcUser;
            if (ircuser != null)
            {
                IrcChannelUser chanUser = m_channel.GetChannelUser(ircuser);
                if (chanUser != null)
                    CheckModeratorStatus(chanUser);
            }
        }

        private bool CheckModeratorStatus(IrcChannelUser chanUser)
        {
            if (chanUser.Modes.Contains('o'))
            {
                m_chanData.AddModerator(chanUser.User.NickName.ToLower());
                return true;
            }

            return false;
        }

        #endregion

        #region IrcDotNet Event Handlers
        void client_ErrorMessageReceived(object sender, IrcErrorMessageEventArgs e)
        {
            WriteDiagnosticMessage("Error message: {0}", e.Message);
        }

        void client_Error(object sender, IrcErrorEventArgs e)
        {
            var error = ErrorOccurred;
            if (error != null)
                error(this, e);
        }

        void client_ConnectFailed(object sender, IrcErrorEventArgs e)
        {
            WriteDiagnosticMessage("Connection failed: {0}", e.Error);
        }

        void client_Disconnected(object sender, EventArgs e)
        {
            WriteDiagnosticMessage("Disconnected: {0}", e.ToString());
        }

        private void client_Registered(object sender, EventArgs e)
        {
            var client = (IrcClient)sender;
            client.LocalUser.MessageReceived += client_LocalUser_MessageReceived;
            client.LocalUser.JoinedChannel += client_LocalUser_JoinedChannel;
            client.LocalUser.LeftChannel += client_LocalUser_LeftChannel;
            s_registeredEvent.Set();
        }

        private void client_LocalUser_LeftChannel(object sender, IrcChannelEventArgs e)
        {
            WriteDiagnosticMessage("Left Channel: {0}: {1}", e.Channel, e.Comment);
        }

        private void client_LocalUser_JoinedChannel(object sender, IrcChannelEventArgs e)
        {
            s_joinedEvent.Set();
            m_channel = e.Channel;
            m_channel.UsersListReceived += m_channel_UsersListReceived;
            m_channel.UserJoined += m_channel_UserJoined;
            m_channel.MessageReceived += channel_MessageReceived;
        }

        void m_channel_UserJoined(object sender, IrcChannelUserEventArgs e)
        {
            CheckModeratorStatus(e.ChannelUser);
        }

        void m_channel_UsersListReceived(object sender, EventArgs e)
        {
            foreach (var user in m_channel.Users)
                CheckModeratorStatus(user);
        }


        private void client_Connected(object sender, EventArgs e)
        {
            m_connectedEvent.Set();
        }
        #endregion

        #region Fire Event Helpers
        protected void WriteDiagnosticMessage(string fmt, params object[] objs)
        {
            var status = StatusUpdate;
            if (status != null)
                status(this, string.Format(fmt, objs));
        }

        protected void OnUserSubscribed(string text)
        {
            var subscribed = UserSubscribed;
            if (subscribed != null)
                subscribed(this, text);
        }

        protected void OnMessageReceived(IrcMessageEventArgs e)
        {
            var msgRcv = MessageReceived;
            if (msgRcv != null)
                msgRcv(this, e);
        }

        protected void OnInformSubscriber(string user)
        {
            var evt = InformSubscriber;
            if (evt != null)
                evt(this, user);
        }

        protected void OnInformTurbo(string user)
        {
            var evt = InformTurbo;
            if (evt != null)
                evt(this, user);
        }
        protected void OnChatClear(string user)
        {
            var evt = InformChatClear;
            if (evt != null)
                evt(this, user);
        }
        #endregion


        #region Private Variables
        private ManualResetEventSlim s_joinedEvent = new ManualResetEventSlim(false);
        private ManualResetEventSlim m_connectedEvent = new ManualResetEventSlim(false);
        private ManualResetEventSlim s_registeredEvent = new ManualResetEventSlim(false);
        private IrcClient m_client;
        private string m_stream;
        private volatile bool m_alive;
        private DateTime m_lastCheck = DateTime.Now;
        private DateTime m_lastModCheck = DateTime.Now;
        int m_modCount = 0;
        ChannelData m_chanData = new ChannelData();
        private IrcChannel m_channel;
        #endregion
    }
}
