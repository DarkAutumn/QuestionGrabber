using IrcDotNet;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Media;

namespace QuestionGrabber
{

    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        ConcurrentQueue<Event> m_eventQueue = new ConcurrentQueue<Event>();
        HashSet<string> m_subs = new HashSet<string>();

        List<ListItem> m_allItems = new List<ListItem>();
        private DispatcherTimer m_dispatcherTimer;
        TwitchClient m_twitch = null;
        private bool m_error = false;
        bool m_showQuestions = true, m_showImportant = true, m_showStatus = true, m_showSubs = true;
        ObservableCollection<ListItem> m_messages = new ObservableCollection<ListItem>();
        Dictionary<string, ListItem> m_messageMap;

        /// <summary>
        /// These are the messages that are displayed in the main window.
        /// </summary>
        public ObservableCollection<ListItem> Messages
        {
            get
            {
                return m_messages;
            }
            set
            {
                m_messages = value;
                OnPropertyChanged("Messages");
            }
        }

        /// <summary>
        /// Checkbox determining whether or not to show questions grabbed from chat.
        /// </summary>
        public bool ShowQuestions
        {
            get
            {
                return m_showQuestions;
            }
            set
            {
                if (m_showQuestions != value)
                {
                    m_showQuestions = value;
                    OnPropertyChanged("ShowQuestions");

                    if (Messages.Count < 100)
                        RequestRefilter(value);
                    else
                        m_eventQueue.Enqueue(new RefilterResultEvent());
                }

            }
        }

        /// <summary>
        /// Checkbox determining whether or not to show highlighted messages from chat.
        /// </summary>
        public bool ShowImportant
        {
            get
            {
                return m_showImportant;
            }
            set
            {
                if (m_showImportant != value)
                {
                    m_showImportant = value;
                    OnPropertyChanged("ShowImportant");

                    if (Messages.Count < 100)
                        RequestRefilter(value);
                    else
                        m_eventQueue.Enqueue(new RefilterResultEvent());
                }
            }
        }

        /// <summary>
        /// Checkbox for whether to show status messages in the main window.
        /// </summary>
        public bool ShowStatus
        {
            get
            {
                return m_showStatus;
            }
            set
            {
                if (m_showStatus != value)
                {
                    m_showStatus = value;
                    OnPropertyChanged("ShowStatus");
                    RequestRefilter(value);
                }
            }
        }

        /// <summary>
        /// Checkbox for whether to show new subscribers in the main window.
        /// </summary>
        public bool ShowSubs
        {
            get
            {
                return m_showSubs;
            }
            set
            {
                if (m_showSubs != value)
                {
                    m_showSubs = value;
                    OnPropertyChanged("ShowSubs");
                    RequestRefilter(value);
                }
            }
        }

        private void RequestRefilter(bool value)
        {
            // If we turn a switch off (meaning we remove entries), we will refilter the
            // list in-place, removing entries.  If we are adding items, we will just
            // rebuild the list.
            if (value)
                m_eventQueue.Enqueue(new RefilterResultEvent());
            else
                m_eventQueue.Enqueue(new RefilterInPlaceEvent());
        }

        #region INotifyPropertyChanged Goo
        public event PropertyChangedEventHandler PropertyChanged;
        private RefilterResultEvent m_refilterAsync;
        private Options m_options;
        protected void OnPropertyChanged(string name)
        {
            PropertyChangedEventHandler handler = PropertyChanged;
            if (handler != null)
                handler(this, new PropertyChangedEventArgs(name));
        }
        #endregion

        public MainWindow()
        {
            LoadOptions();

            if (m_options.CheckUpdates)
            {
                Task t = new Task(delegate() { CheckIfUpdateAvailable(); });
                t.Start();
            }

            // Create the TwitchClient instance and connect to the twitch server on a background
            // thread.
            InitIrc();

            if (!Debugger.IsAttached)
                AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;

            InitializeComponent();

            // The UI will update every 250 milliseconds
            m_dispatcherTimer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher);
            m_dispatcherTimer.Tick += dispatcherTimer_Tick;
            m_dispatcherTimer.Interval = new TimeSpan(0, 0, 0, 0, 250);
            m_dispatcherTimer.Start();

            if (m_options.PreventDuplicates)
                m_messageMap = new Dictionary<string, ListItem>();
        }


        void CheckIfUpdateAvailable()
        {
            bool error = false;

            try
            {
                var req = (System.Net.HttpWebRequest)System.Net.HttpWebRequest.Create(@"http://idleengineer.com/qg_version.txt");
                req.UserAgent = "Question Grabber Bot/0.0.0.1";
                var response = req.GetResponse();
                var fromStream = response.GetResponseStream();

                StreamReader reader = new StreamReader(fromStream);
                string line = reader.ReadLine();
                if (line != null)
                {
                    string[] values = line.Split(new char[] { ' ' }, 1);
                    if (values.Length >= 2)
                    {
                        Assembly assembly = Assembly.GetExecutingAssembly();
                        FileVersionInfo info = FileVersionInfo.GetVersionInfo(assembly.Location);

                        if (info.FileVersion != values[0])
                            m_eventQueue.Enqueue(new StatusEvent(values[1]));
                    }
                    else
                    {
                        error = true;
                    }
                }
                else
                {
                    error = true;
                }
            }
            catch
            {
                error = true;
            }

            if (error)
                m_eventQueue.Enqueue(new StatusEvent("Failed to check for Question Grabber updates..."));
        }

        void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            // This will drop an "error.txt" file if we encounter an unhandled exception.
            Exception obj = (Exception)e.ExceptionObject;
            string text = string.Format("{0}: {1}\n{2}", obj.GetType().ToString(), obj.Message, obj.StackTrace.ToString());
            
            string dir = System.IO.Path.GetDirectoryName(Assembly.GetExecutingAssembly().GetName().CodeBase);

            using (var file = File.CreateText("error.txt"))
                file.WriteLine(text);
            
            MessageBox.Show(text, "Unhandled Exception");
        }


        /// <summary>
        /// Handler for when the user clicks the Clear button.
        /// </summary>
        private void Clear_Click(object sender, RoutedEventArgs e)
        {
            m_allItems.Clear();
            Messages.Clear();
        }

        /// <summary>
        /// Handler when the user clicks the Reconnect button.
        /// </summary>
        private void Connect_Click(object sender, RoutedEventArgs e)
        {
            ResetConnection();
        }

        /// <summary>
        /// Disconnects and then Reconnects to chat (useful if an error occurred).
        /// </summary>
        private void ResetConnection()
        {
            lock (this)
            {
                m_error = false;
                if (m_twitch != null)
                {
                    m_twitch.UserSubscribed -= m_client_UserSubscribed;
                    m_twitch.StatusUpdate -= m_client_StatusUpdate;
                    m_twitch.ErrorOccurred -= m_client_ErrorOccurred;
                    m_twitch.MessageReceived -= m_client_MessageReceived;

                    m_twitch.Dispose();
                    m_twitch = null;
                }

                InitIrc();
            }
        }

        /// <summary>
        /// Subscribe to IRC events and then asynchronously connect to chat.
        /// </summary>
        private void InitIrc()
        {
            m_twitch = new TwitchClient();
            m_twitch.UserSubscribed += m_client_UserSubscribed;
            m_twitch.StatusUpdate += m_client_StatusUpdate;
            m_twitch.ErrorOccurred += m_client_ErrorOccurred;
            m_twitch.MessageReceived += m_client_MessageReceived;
            m_twitch.InformSubscriber += m_client_InformSubscriber;

            Task t = new Task(delegate() { m_twitch.Connect(m_options.Stream, m_options.TwitchUsername, m_options.OauthPassword); });
            t.Start();
        }

        /// <summary>
        /// Load the options from the option file
        /// </summary>
        private void LoadOptions()
        {
            try
            {
                m_options = Options.Load("options.ini");
            }
            catch (FileNotFoundException)
            {
                MessageBox.Show("Could not find options.ini!", "Error loading options.ini.");
                Environment.Exit(-1);
            }
            catch (Exception e)
            {
                MessageBox.Show("Error loading options.ini:\n" + e.Message, "Error loading options.ini.");
                Environment.Exit(-1);
            }
        }


        /// <summary>
        /// We receive messages asynchronously.  Here, we process the message to see if we should grab it.
        /// If so, we add the message to the queue and process it during the dispatcher timer tick.
        /// </summary>
        void m_client_MessageReceived(TwitchClient sender, IrcMessageEventArgs msg)
        {
            // Check if the user is on the ignore list.
            string user = msg.Source.Name;
            foreach (string ignore in m_options.UserIgnoreList)
                if (user.Equals(ignore, StringComparison.InvariantCultureIgnoreCase))
                    return;

            // check if the text is on the ignore list
            var text = msg.Text;
            var lowerText = text.ToLower();

            // check if the text contains a highlight word
            foreach (string highlight in m_options.HighlightList)
            {
                if (lowerText.Contains(highlight.ToLower()))
                {
                    if (ShouldIgnore(lowerText))
                        return;

                    if (m_messageMap != null && CheckDuplicate(user, lowerText))
                        return;

                    ListItem item = ListItem.CreateFromHighlight(m_twitch.ChannelData, user, text);
                    if (m_messageMap != null)
                        m_messageMap[lowerText] = item;

                    m_eventQueue.Enqueue(new NewListItemEvent(item));
                    return;
                }
            }

            // check if the text contains a grab word
            foreach (string grab in m_options.GrabList)
            {
                if (lowerText.Contains(grab.ToLower()))
                {
                    if (ShouldIgnore(lowerText))
                        return;

                    if (m_messageMap != null && CheckDuplicate(user, lowerText))
                        return;

                    ListItem item = ListItem.CreateFromQuestion(m_twitch.ChannelData, user, text);
                    if (m_messageMap != null)
                        m_messageMap[lowerText] = item;

                    m_eventQueue.Enqueue(new NewListItemEvent(item));
                    return;
                }
            }
        }

        /// <summary>
        /// Checks if the message was a duplicate of another message, and if so enqueues a duplicate
        /// event.
        /// </summary>
        /// <param name="user">The user who sent the message.</param>
        /// <param name="lowerText">The lower case text of the message.</param>
        /// <returns>True if the message was a duplicate and an event was queued.</returns>
        private bool CheckDuplicate(string user, string lowerText)
        {
            ListItem item = null;
            if (m_messageMap.TryGetValue(lowerText, out item))
            {
                if (!item.User.Equals(user + ':', StringComparison.CurrentCultureIgnoreCase))
                    item.User = "multiple users:";

                m_eventQueue.Enqueue(new DuplicateListItemEvent(item));
                return true;
            }

            return false;
        }

        /// <summary>
        /// Whether or not the message should be ignored due to the ignore list.
        /// </summary>
        private bool ShouldIgnore(string lowerText)
        {
            foreach (string ignore in m_options.TextIgnoreList)
                if (lowerText.Contains(ignore.ToLower()))
                    return true;

            return false;
        }

        /// <summary>
        /// This is called when IrcDotNet hits an exception.  We will reset the connection with a new
        /// IrcDotNet instance.  In practice, this should never happen.
        /// </summary>
        void m_client_ErrorOccurred(TwitchClient sender, IrcErrorEventArgs error)
        {
            m_error = true;
        }

        void m_client_StatusUpdate(TwitchClient sender, string message)
        {
            ListItem item = ListItem.CreateFromStatus(message);
            m_eventQueue.Enqueue(new NewListItemEvent(item));
        }

        void m_client_UserSubscribed(TwitchClient sender, string user)
        {
            ListItem item = ListItem.CreateFromNewSub(user);
            m_eventQueue.Enqueue(new NewSubscriberEvent(item, user));

            if (!string.IsNullOrEmpty(m_options.SubscriberFile))
            {
                List<string> subs = new List<string>();
                subs.Add(user);
                if (File.Exists(m_options.SubscriberFile))
                    subs.AddRange(File.ReadAllText(m_options.SubscriberFile).Split(',').Select(s=>s.Trim()));

                if (subs.Count > 10)
                    subs.RemoveRange(10, subs.Count - 10);

                File.WriteAllText(m_options.SubscriberFile, string.Join(", ", subs));
            }
        }

        /// <summary>
        /// Called when twitch lets us know that a user is a subscriber to the channel.  Since chat messages
        /// and InformSubscriber events are asynchronous, and may appear in any order, we need to go update
        /// all messages we have received to add the subscriber icon.
        /// </summary>
        void m_client_InformSubscriber(TwitchClient sender, string user)
        {
            m_eventQueue.Enqueue(new NotifySubscriberEvent(user));
        }

        /// <summary>
        /// This is the primary update loop for the UI.  Every 250 milliseconds we scour
        /// each concurrent queue looking for new messages and update the UI with it.
        /// This may seem like overkill but it ensures that all events are handled in a
        /// correct/sane ordering.  
        /// </summary>
        private void dispatcherTimer_Tick(object sender, EventArgs e)
        {
            // If IrcDotNet hit an error, reset the connection entirely (by creating a new
            // TwitchClient and throwing away the old one).
            if (m_error)
            {
                if (m_twitch.IsStreamLive)
                    ResetConnection();
            }

            // If we are refiltering (asynchronously) the list, actually stop all processing
            // on the list until it's complete.  This prevents us from modifying the message
            // list while we are reading from it on another thread.
            if (m_refilterAsync != null)
            {
                if (!m_refilterAsync.Complete)
                    return;

                Messages = m_refilterAsync.Result;
                ScrollBar.ScrollToEnd();
                m_refilterAsync = null;
            }

            // Now go through the queue, add messages we find, and process refilter requests.
            if (m_eventQueue.Count == 0)
                return;

            bool refilterInPlace = false;
            Event evt;
            while (m_eventQueue.TryDequeue(out evt))
            {
                switch (evt.Type)
                {
                    case EventType.NewListItem:
                        AddItem(((NewListItemEvent)evt).Item);
                        break;

                    case EventType.NewSubscriber:
                        NewSubscriberEvent newSub = (NewSubscriberEvent)evt;
                        m_subs.Add(newSub.User);
                        AddItem(newSub.Item);
                        break;

                    case EventType.NotifySubscriber:
                        m_subs.Add(((NotifySubscriberEvent)evt).User);
                        break;

                    case EventType.RefilterInPlace:
                        refilterInPlace = true;
                        break;

                    case EventType.StatusUpdate:
                        var status = ((StatusEvent)evt).Status;
                        AddItem(ListItem.CreateFromStatus(status));
                        break;

                    case EventType.DuplicateListItem:
                        var dupe = ((DuplicateListItemEvent)evt).Item;
                        int i = dupe.Index;
                        Debug.Assert(dupe == Messages[i]);

                        Messages.RemoveAt(i);
                        for (; i < Messages.Count; ++i)
                            Messages[i].Index = i;

                        AddItem(dupe);
                        break;

                    // If we need to refilter the list (due to the user changing filtering options),
                    // we do that work on a background thread.  During that time, we stop all message
                    // processing, hence the return instead of break here.
                    case EventType.RefilterAsyncEvent:
                        m_refilterAsync = ((RefilterResultEvent)evt);
                        StartAsyncRefilter(m_refilterAsync);
                        return;
                }
            }

            // If we were asked to refilter the list in place (that is, remove messages),
            // then we do that only once, now at the end.
            if (refilterInPlace)
            {
                int i = 0;
                while (i < Messages.Count)
                {
                    var curr = Messages[i];
                    if (!AllowItem(curr))
                    {
                        Messages.RemoveAt(i);
                    }
                    else
                    {
                        curr.Index = i;
                        i++;
                    }
                }
            }

            // If we have new notifications of subscribers, we need to go back and mark the
            // questions they asked with a sub icon.
            if (m_subs.Count > 0)
            {
                var needsUpdate = from item in m_allItems
                                  where (item.Type == ListItemType.ImportantQuestion || item.Type == ListItemType.Question)
                                  && item.SubscriberIcon != System.Windows.Visibility.Visible && m_subs.Contains(item.User)
                                  select item;

                foreach (var item in needsUpdate)
                    item.SubscriberIcon = System.Windows.Visibility.Visible;

                m_subs.Clear();
            }
        }


        [Conditional("_DEBUG")]
        void CheckIndicies()
        {
            for (int i = 0; i < Messages.Count; ++i)
                Debug.Assert(Messages[i].Index == i);
        }


        /// <summary>
        /// Add an item to the list of grabbed messages.  If the item is not
        /// currently filtered, we will add this to the display.
        /// </summary>
        /// <param name="item"></param>
        private void AddItem(ListItem item)
        {
            m_allItems.Add(item);

            if (AllowItem(item))
            {
                item.Index = Messages.Count;
                Messages.Add(item);
            }

            // TODO: Only scroll to the end if the bar was previously at the end.
            ScrollBar.ScrollToEnd();
        }

        private bool AllowItem(ListItem item)
        {
            switch (item.Type)
            {
                case ListItemType.ImportantQuestion:
                    return m_showImportant;

                case ListItemType.Question:
                    return m_showQuestions;

                case ListItemType.Status:
                    return m_showStatus;

                case ListItemType.Subscriber:
                    return m_showSubs;
            }

            return false;
        }

        /// <summary>
        /// When filtering options change, we refilter the list and change Messages
        /// to be a new list of those items.
        /// </summary>
        private void StartAsyncRefilter(RefilterResultEvent evt)
        {
            Task t = new Task(delegate()
                {
                    var items = from item in m_allItems where AllowItem(item) select item;
                    var messages = new ObservableCollection<ListItem>(items);

                    for (int i = 0; i < messages.Count; ++i)
                        messages[i].Index = i;

                    evt.Result = messages;
                });

            t.Start();
        }
    }
}
