//
// Copyright (c) Fela Ameghino 2015-2025
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//
using System;
using System.Collections.Generic;
using System.Numerics;
using Telegram.Common;
using Telegram.Controls.Media;
using Telegram.Native;
using Telegram.Td.Api;
using Telegram.ViewModels;
using Windows.Foundation;
using Windows.UI.Composition;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Hosting;

namespace Telegram.Controls.Messages
{
    public partial class ReplyMarkupInlinePanel : Panel
    {
        private readonly double _keyboardHeight = 260;

        private CompositionGeometricClip _clip;

        private bool _empty = true;

        public List<int> Rows { get; } = new();

        public Vector2 CornerRadius { get; set; }

        public bool Update(MessageViewModel message, ReplyMarkup markup, bool inline = true)
        {
            if (_empty && (message == null || markup == null))
            {
                return false;
            }

            _empty = message == null || markup == null;

            Children.Clear();
            Rows.Clear();

            if (markup is ReplyMarkupShowKeyboard keyboardMarkup && !inline)
            {
                return Update(message, keyboardMarkup);
            }
            else if (markup is ReplyMarkupInlineKeyboard inlineMarkup && inline)
            {
                return Update(message, inlineMarkup);
            }

            return false;
        }

        public bool Update(MessageViewModel message, ReplyMarkupInlineKeyboard inlineMarkup)
        {
            var rows = inlineMarkup.Rows;

            Tag = message;

            var receipt = false;
            if (message != null && message.Content is MessageInvoice invoice)
            {
                receipt = invoice.ReceiptMessageId != 0;

                if (invoice.PaidMedia is not PaidMediaUnsupported and not null)
                {
                    rows = null;
                }
            }

            if (rows == null)
            {
                return false;
            }

            foreach (var row in rows)
            {
                foreach (var item in row)
                {
                    var button = new ReplyMarkupInlineButton(this, item);
                    button.HorizontalAlignment = HorizontalAlignment.Stretch;
                    button.VerticalAlignment = VerticalAlignment.Stretch;
                    button.Text = item.Text.Replace('\n', ' ');
                    button.Click += Button_Click;

                    switch (item.Type)
                    {
                        case InlineKeyboardButtonTypeUrl typeUrl:
                            button.Glyph = "\uE9B7";
                            Extensions.SetToolTip(button, typeUrl.Url);
                            break;
                        case InlineKeyboardButtonTypeLoginUrl:
                            button.Glyph = "\uE9B7";
                            break;
                        case InlineKeyboardButtonTypeSwitchInline:
                            button.Glyph = "\uEE35";
                            break;
                        case InlineKeyboardButtonTypeBuy:
                            if (receipt)
                            {
                                button.Content = Strings.PaymentReceipt;
                            }
                            else
                            {
                                button.Content = item.Text.Replace("\u2B50", Icons.Premium + "\u200A");
                            }
                            break;
                        case InlineKeyboardButtonTypeWebApp:
                            button.Glyph = Icons.Window16;
                            break;
                        case InlineKeyboardButtonTypeCopyText:
                            button.Glyph = Icons.CopyFilled16;
                            break;
                    }

                    Children.Add(button);
                }

                Rows.Add(row.Count);
            }

            return false;
        }

        private void Button_Click(object sender, RoutedEventArgs e)
        {
            if (sender is ReplyMarkupInlineButton button)
            {
                InlineButtonClick?.Invoke(this, new ReplyMarkupInlineButtonClickEventArgs(button.Button));
            }
        }

        public event EventHandler<ReplyMarkupInlineButtonClickEventArgs> InlineButtonClick;

        protected override Size MeasureOverride(Size availableSize)
        {
            var j = 0;
            var w = 0d;
            var h = 0d;

            var spacing = 2;

            foreach (var row in Rows)
            {
                var column = new Size(Math.Max(0, (availableSize.Width - spacing * (row - 1)) / row), availableSize.Height / Rows.Count);
                var width = 0d;
                var height = 0d;

                for (int i = 0; i < row; i++)
                {
                    var child = Children[j + i];
                    child.Measure(column);
                    width = Math.Max(width, child.DesiredSize.Width);
                    height = Math.Max(height, child.DesiredSize.Height);
                }

                var final = (width * row) + (spacing * (row - 1));
                if (final > availableSize.Width)
                {
                    w = availableSize.Width;
                }
                else
                {
                    w = Math.Max(w, final);
                }

                h += height + spacing;
                j += row;
            }

            return new Size(w, h);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var j = 0;
            var y = 0d;

            var spacing = 2;

            if (_clip == null)
            {
                var visual = ElementComposition.GetElementVisual(this);
                visual.Clip = _clip = visual.Compositor.CreateGeometricClip();
            }

            var rows = new List<IList<Rect>>(Rows.Count);

            foreach (var row in Rows)
            {
                var column = (finalSize.Width - spacing * (row - 1)) / row;
                var height = 0d;

                var x = 0d;

                var clip = new List<Rect>(row);

                y += spacing;

                for (int i = 0; i < row; i++)
                {
                    var child = Children[j + i];
                    child.Arrange(new Rect(x, y, column, child.DesiredSize.Height));
                    clip.Add(new Rect(x, y, column, child.DesiredSize.Height));

                    height = Math.Max(height, child.DesiredSize.Height);
                    x += column + spacing;
                }

                rows.Add(clip);

                y += height;
                j += row;
            }

            _clip.Geometry = _clip.Compositor.CreatePathGeometry(PlaceholderImageHelper.Foreground.GetReplyMarkupClip(rows, CornerRadius.X, CornerRadius.Y));
            return finalSize;
        }
    }
}
