using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using CodeplugBuilder.App;
using CodeplugBuilder.Core;

namespace CodeplugBuilder.Mac
{
    /// <summary>
    /// RepeaterBook pieces the Mac screens share (the Windows RepeaterBookTokenDialog and the link labels): links that open
    /// web pages, the token window, and the sources line (RepeaterBook's terms: the credit with a link wherever its data shows).
    /// </summary>
    static class RepeaterBookUi
    {
        public static void Open(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        }

        /// <summary>A clickable, underlined piece of text.</summary>
        public static TextBlock Link(string text, Action click)
        {
            var t = new TextBlock
            {
                Text = text, Foreground = Brushes.CornflowerBlue, TextDecorations = TextDecorations.Underline,
                Cursor = new Cursor(StandardCursorType.Hand), VerticalAlignment = VerticalAlignment.Center,
            };
            t.PointerPressed += (s, e) => click();
            return t;
        }

        public static TextBlock Link(string text, string url) { return Link(text, () => Open(url)); }

        static TextBlock Plain(string text) { return new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center }; }

        /// <summary>"Repeaters from RadioID.net and BrandMeister (...) and RepeaterBook (...). Data courtesy of RepeaterBook.com." with links.</summary>
        public static Control Sources()
        {
            var p = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            p.Children.Add(Plain("Repeaters from "));
            p.Children.Add(Link("RadioID.net", "https://radioid.net"));
            p.Children.Add(Plain(" and "));
            p.Children.Add(Link("BrandMeister", "https://brandmeister.network"));
            p.Children.Add(Plain(" (DMR repeaters and their talkgroups) and "));
            p.Children.Add(Link("RepeaterBook", RepeaterBookApi.SiteUrl));
            p.Children.Add(Plain(" (analog FM repeaters, and DMR ones RadioID.net doesn't list). "));
            p.Children.Add(Link(RepeaterBookApi.Attribution, RepeaterBookApi.SiteUrl));
            return p;
        }

        public static string Status => RepeaterBookToken.IsSet ? "saved on this computer" : "none yet";

        /// <summary>Paste, replace or forget the user's own token. True when the saved token changed. The box starts empty.</summary>
        public static async Task<bool> ShowTokenWindow(Window owner)
        {
            var w = new Window
            {
                Title = "Your RepeaterBook token", Width = 560, SizeToContent = SizeToContent.Height, CanResize = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
            };
            var body = new StackPanel { Margin = new Thickness(18), Spacing = 8 };
            body.Children.Add(new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = "Each user makes their own token for this program: sign in on RepeaterBook, open API Apps, and generate a token for W6OZZ CPS. " +
                       "Paste it here. It is yours alone: don't share it or post it anywhere. " + RepeaterBookToken.WhereKept +
                       ", and is never put in projects, exports or logs." + (RepeaterBookToken.IsSet ? "\n\nA token is saved. Paste a new one to replace it." : ""),
            });
            body.Children.Add(Link("Open RepeaterBook > API Apps", RepeaterBookApi.TokenPageUrl));
            var box = new TextBox { PasswordChar = '•', Watermark = "rbuapp_..." };
            body.Children.Add(box);
            var show = new CheckBox { Content = "Show" };
            show.IsCheckedChanged += (s, e) => box.PasswordChar = show.IsChecked == true ? '\0' : '•';
            body.Children.Add(show);
            bool changed = false;
            var save = new Button { Content = "Save", IsDefault = true, MinWidth = 88 };
            var forget = new Button { Content = "Forget my token", IsEnabled = RepeaterBookToken.IsSet };
            var cancel = new Button { Content = "Cancel", MinWidth = 88 };
            save.Click += async (s, e) =>
            {
                string token = (box.Text ?? "").Trim();
                if (!RepeaterBookApi.LooksLikeToken(token)) { await Dialogs.Error(w, "That doesn't look like a token. Copy the whole token from RepeaterBook > API Apps."); return; }
                if (!token.StartsWith(RepeaterBookApi.UserTokenPrefix, StringComparison.Ordinal) &&
                    !await Dialogs.Ask(w, "Tokens you make for this program start with \"" + RepeaterBookApi.UserTokenPrefix + "\". Save this one anyway?", "Save", "Cancel")) return;
                RepeaterBookToken.Set(token);
                changed = true;
                w.Close();
            };
            forget.Click += (s, e) => { RepeaterBookToken.Set(null); changed = true; w.Close(); };
            cancel.Click += (s, e) => w.Close();
            body.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { forget, cancel, save } });
            w.Content = body;
            w.Opened += (s, e) => box.Focus();
            await w.ShowDialog(owner);
            return changed;
        }
    }
}
