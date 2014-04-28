using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace QuestionGrabber
{
    class ChatEntry : TextBlock
    {

        public static readonly DependencyProperty ItemsProperty =
            DependencyProperty.Register(
                "Test",
                typeof(ListItem),
                typeof(ChatEntry),
                new PropertyMetadata(default(ListItem), OnItemsPropertyChanged));

        private static void OnItemsPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
        }

        public ListItem Test
        {
            get
            {
                return (ListItem)GetValue(ItemsProperty);
            }
            set
            {
                SetValue(ItemsProperty, value);
            }
        }

        public ChatEntry()
        {
            this.Initialized += ChatEntry_Initialized;
        }

        void ChatEntry_Initialized(object sender, EventArgs e)
        {
            var item = Test;
            Image img = new Image();
            img.Source = new BitmapImage(new Uri(@"pack://application:,,,/Resources/star.png"));
            img.Width = 16;
            img.Height = 16;
            img.Margin = new Thickness(2);
            img.VerticalAlignment = VerticalAlignment.Center;

            Inlines.Add(new InlineUIContainer(img));
            Inlines.Add(new Run(item.User) { FontWeight = FontWeights.Bold, Foreground = Brushes.Blue });
            Inlines.Add(new Run(": "));
            Inlines.Add(new Run(item.Message));
        }
    }
}
