using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Xml;
using System.Linq;
using System.Xml.Serialization;

namespace QuestionGrabber
{
    public class UserData
    {
        public bool IsModerator { get; set; }

        public bool IsSubscriber { get; set; }

        public bool IsTurbo { get; set; }

        public string Name { get; set; }

        public UserData(string name)
        {
            Name = name;
        }
    }

    public class ChannelData
    {
        Dictionary<string, UserData> m_users = new Dictionary<string, UserData>();

        public bool IsModerator(string username)
        {
            UserData user;
            return m_users.TryGetValue(username, out user) && user.IsModerator;
        }

        public bool IsSubscriber(string username)
        {
            UserData user;
            return m_users.TryGetValue(username, out user) && user.IsSubscriber;
        }

        public bool IsTurboUser(string username)
        {
            UserData user;
            return m_users.TryGetValue(username, out user) && user.IsTurbo;
        }

        public void AddModerator(string username)
        {
            GetUser(username).IsModerator = true;
        }

        public void AddSubscriber(string username)
        {
            GetUser(username).IsSubscriber = true;
        }

        public void AddTurbo(string username)
        {
            GetUser(username).IsTurbo = true;
        }

        public void AddModerators(IEnumerable<string> mods)
        {
            foreach (string mod in mods)
                AddModerator(mod);
        }

        private UserData GetUser(string username)
        {
            UserData user;
            if (!m_users.TryGetValue(username, out user))
            {
                user = new UserData(username);
                m_users[username] = user;
            }

            return user;
        }
    }


    public enum EventType
    {
        NewListItem,
        NewSubscriber,
        NotifySubscriber,
        RefilterInPlace,
        RefilterAsyncEvent
    }

    class Event
    {
        public EventType Type { get; set; }
    }

    class NewListItemEvent : Event
    {
        public ListItem Item { get; set; }

        public NewListItemEvent(ListItem item)
        {
            Type = EventType.NewListItem;
            Item = item;
        }
    }

    class NewSubscriberEvent : NewListItemEvent
    {
        public string User { get; set; }

        public NewSubscriberEvent(ListItem item, string user)
            : base(item)
        {
            User = user;
            Type = EventType.NewSubscriber;
        }
    }

    class NotifySubscriberEvent : Event
    {
        public string User { get; set; }

        public NotifySubscriberEvent(string user)
        {
            User = user;
            Type = EventType.NotifySubscriber;
        }
    }

    class RefilterInPlaceEvent : Event
    {
        public RefilterInPlaceEvent()
        {
            Type = EventType.RefilterInPlace;
        }
    }

    class RefilterResultEvent : Event
    {
        volatile bool m_complete;
        volatile ObservableCollection<ListItem> m_result;

        public bool Complete { get { return m_complete; } }

        public ObservableCollection<ListItem> Result
        {
            get
            {
                return m_result;
            }
            set
            {
                m_complete = true;
                m_result = value;
            }
        }

        public RefilterResultEvent()
        {
            Type = EventType.RefilterAsyncEvent;
        }
    }


    public enum ListItemType
    {
        Status,
        Subscriber,
        Question,
        ImportantQuestion
    }

    public class ListItem : INotifyPropertyChanged
    {
        public string User { get; set; }
        public Visibility UserVisibility { get; set; }
        public Brush UserColor { get; set; }
        public FontWeight UserWeight { get; set; }
        public int UserFontSize { get { return 15; } }


        public string Message { get; set; }
        public Visibility MessageVisibility { get; set; }
        public Brush MessageColor { get; set; }
        public FontWeight MessageWeight { get; set; }
        public int MessageFontSize { get { return 15; } }

        public ListItemType Type { get; set; }


        public Visibility SubscriberIcon
        {
            get { return m_subIcon; }
            set
            {
                if (m_subIcon != value)
                {
                    m_subIcon = value;
                    OnPropertyChanged("SubscriberIcon");
                }
            }
        }

        Visibility m_subIcon = Visibility.Hidden;


        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string name)
        {
            PropertyChangedEventHandler handler = PropertyChanged;
            if (handler != null)
                handler(this, new PropertyChangedEventArgs(name));
        }

        internal static ListItem CreateFromHighlight(ChannelData info, string user, string text)
        {
            ListItem item = new ListItem();
            item.User = user + ":";
            item.UserVisibility = Visibility.Visible;
            item.UserColor = info.IsModerator(user) ? Brushes.Red : Brushes.Blue;
            item.UserWeight = FontWeights.Bold;

            item.Message = text;
            item.MessageVisibility = Visibility.Visible;
            item.MessageColor = Brushes.Black;
            item.MessageWeight = FontWeights.Bold;

            item.SubscriberIcon = info.IsSubscriber(user) ? Visibility.Visible : Visibility.Collapsed;
            item.Type = ListItemType.ImportantQuestion;
            return item;
        }

        internal static ListItem CreateFromQuestion(ChannelData info, string user, string text)
        {
            ListItem item = new ListItem();
            item.User = user + ":";
            item.UserVisibility = Visibility.Visible;
            item.UserColor = info.IsModerator(user) ? Brushes.Red : Brushes.Blue;
            item.UserWeight = FontWeights.Normal;

            item.Message = text;
            item.MessageVisibility = Visibility.Visible;
            item.MessageColor = Brushes.Black;
            item.MessageWeight = FontWeights.Normal;

            item.SubscriberIcon = info.IsSubscriber(user) ? Visibility.Visible : Visibility.Collapsed;
            item.Type = ListItemType.Question;

            return item;
        }

        internal static ListItem CreateFromStatus(string message)
        {
            ListItem item = new ListItem();
            item.User = "";
            item.UserVisibility = Visibility.Collapsed;
            item.UserColor = Brushes.Black;
            item.UserWeight = FontWeights.Normal;

            item.Message = message;
            item.MessageVisibility = Visibility.Visible;
            item.MessageColor = Brushes.Green;
            item.MessageWeight = FontWeights.Normal;

            item.SubscriberIcon = Visibility.Collapsed;
            item.Type = ListItemType.Status;
            return item;
        }


        internal static ListItem CreateFromNewSub(string user)
        {
            ListItem item = new ListItem();
            item.User = "";
            item.UserVisibility = Visibility.Collapsed;
            item.UserColor = Brushes.Black;
            item.UserWeight = FontWeights.Normal;

            item.Message = user + " has subscribed!";
            item.MessageVisibility = Visibility.Visible;
            item.MessageColor = Brushes.Red;
            item.MessageWeight = FontWeights.Bold;

            item.SubscriberIcon = Visibility.Collapsed;
            item.Type = ListItemType.Subscriber;

            return item;
        }

        protected ListItem()
        {
        }
    }


    public class Options
    {
        string m_stream, m_twitchName, m_oauthPass;

        public string Stream { get { return m_stream; } }
        public string TwitchUsername { get { return m_twitchName; } }
        public string OauthPassword { get { return m_oauthPass; } }

        public List<string> GrabList { get; set; }
        public List<string> HighlightList { get; set; }
        public List<string> UserIgnoreList { get; set; }
        public List<string> TextIgnoreList { get; set; }

        static string FileName 
        {
            get
            {
                return Path.Combine(Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location), "options.ini");
            }
        }

        public Options()
        {
            GrabList = new List<string>();
            HighlightList = new List<string>();
            UserIgnoreList = new List<string>();
            TextIgnoreList = new List<string>();
        }

        public static Options Load(string path)
        {
            Options options = new Options();
            IniReader reader = new IniReader(FileName);

            IniSection section = reader.GetSectionByName("stream");
            if (section == null)
                throw new InvalidOperationException("Options file missing [Stream] section.");

            GetValue(options, section, out options.m_stream, "stream", section.GetValue("stream"));
            GetValue(options, section, out options.m_twitchName, "twitchname", section.GetValue("twitchname") ?? section.GetValue("user") ?? section.GetValue("username"));
            GetValue(options, section, out options.m_oauthPass, "oauth", section.GetValue("oauth") ?? section.GetValue("pass") ?? section.GetValue("password"));

            if (!options.m_oauthPass.StartsWith("oauth:"))
                throw new FormatException("The 'oauth' field in the [Stream] section must start with 'oauth:'.\n\nThis is not your twitch password, please get your api key from www.twitchapps.com/tmi.");
            
            section = reader.GetSectionByName("grab");
            if (section != null)
                foreach (string line in section.EnumerateRawStrings())
                    options.GrabList.Add(DoReplacements(options, line));

            section = reader.GetSectionByName("highlight");
            if (section != null)
                foreach (string line in section.EnumerateRawStrings())
                    options.HighlightList.Add(DoReplacements(options, line));

            section = reader.GetSectionByName("ignoreusers");
            if (section != null)
                foreach (string line in section.EnumerateRawStrings())
                    options.UserIgnoreList.Add(DoReplacements(options, line));

            section = reader.GetSectionByName("ignoretext");
            if (section != null)
                foreach (string line in section.EnumerateRawStrings())
                    options.TextIgnoreList.Add(DoReplacements(options, line));
            
            return options;
        }

        private static string DoReplacements(Options options, string value)
        {
            int i = value.IndexOf("$stream");
            while (i != -1)
            {
                value = value.Replace("$stream", options.Stream);
                i = value.IndexOf("$stream");
            }
            return value;
        }

        private static void GetValue(Options options, IniSection section, out string key, string name, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new FormatException(string.Format("Section [{0}] is missing value '{1}'.", section.Name, name));

            key = value;
        }
    }
}
