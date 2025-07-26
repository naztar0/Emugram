//
// Copyright Fela Ameghino 2015-2025
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//
using Microsoft.Graphics.Canvas.Geometry;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using Telegram.Common;
using Telegram.Controls.Media;
using Telegram.Native;
using Telegram.Native.Highlight;
using Telegram.Navigation;
using Telegram.Services;
using Telegram.Streams;
using Telegram.Td.Api;
using Windows.Devices.Input;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.Composition;
using Windows.UI.Text;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Core.Direct;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Documents;
using Windows.UI.Xaml.Hosting;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Markup;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;

namespace Telegram.Controls
{
    public partial class TextEntityClickEventArgs : EventArgs
    {
        public TextEntityClickEventArgs(int offset, int length, TextEntityType type, object data)
        {
            Offset = offset;
            Length = length;
            Type = type;
            Data = data;
        }

        public int Offset { get; }

        public int Length { get; }

        public TextEntityType Type { get; }

        public object Data { get; }
    }

    public record FormattedParagraph(Paragraph Paragraph, StyledParagraph Styled);

    [ContentProperty(Name = "Blocks")]
    public partial class FormattedTextBlock : Control
    {
        private IClientService _clientService;
        private StyledText _text;
        private double _fontSize;

        private IXamlDirectObject _fastRun;
        private double _fastFontSize;

        private string _query;

        private bool _isHighlighted;
        private bool _ignoreSpoilers = false;

        private AnimatedImage _spoilerPresenter;

        private Span _spanForInlines;

        private ulong _expandSelectionDeadline;

        private readonly List<FormattedParagraph> _codeBlocks = new();
        private readonly List<Hyperlink> _links = new();
        private readonly List<TextStyleSpoiler> _spoilers = new();

        readonly struct TextStyleSpoiler
        {
            public readonly int Offset;
            public readonly int Length;
            public readonly int ParagraphIndex;

            public TextStyleSpoiler(int offset, int length, int paragraphIndex)
            {
                Offset = offset;
                Length = length;
                ParagraphIndex = paragraphIndex;
            }
        }

        private TextHighlighter _spoiler;
        private bool _invalidateSpoilers;

        private Canvas Below;
        private RichTextBlock TextBlock;

        private bool _templateApplied;

        public FormattedTextBlock()
        {
            DefaultStyleKey = typeof(FormattedTextBlock);
        }

        public StyledText Text => _text;

        public bool AdjustLineEnding { get; set; }

        private bool _hasLineEnding;
        public bool HasLineEnding
        {
            get => _hasLineEnding;
            set
            {
                if (_hasLineEnding != value)
                {
                    _hasLineEnding = value;
                    //InvalidateMeasure();
                }
            }
        }

        private bool _hasCodeBlocks;
        public bool HasCodeBlocks
        {
            get => _hasCodeBlocks;
            set
            {
                if (_hasCodeBlocks != value)
                {
                    _hasCodeBlocks = value;

                    if (value)
                    {
                        ActualThemeChanged += OnActualThemeChanged;
                    }
                    else
                    {
                        ActualThemeChanged -= OnActualThemeChanged;
                    }
                }
            }
        }

        private IList<Block> _blocks;
        public IList<Block> Blocks
        {
            get => TextBlock?.Blocks ?? (_blocks ??= new List<Block>());
        }

        public event EventHandler<TextEntityClickEventArgs> TextEntityClick;

        private ContextMenuOpeningEventHandler _contextMenuOpening;
        public event ContextMenuOpeningEventHandler ContextMenuOpening
        {
            add
            {
                if (TextBlock != null)
                {
                    TextBlock.ContextMenuOpening += value;
                }

                _contextMenuOpening += value;
            }
            remove
            {
                if (TextBlock != null)
                {
                    TextBlock.ContextMenuOpening -= value;
                }

                _contextMenuOpening -= value;
            }
        }

        protected override void OnApplyTemplate()
        {
            Below = GetTemplateChild(nameof(Below)) as Canvas;

            TextBlock = GetTemplateChild(nameof(TextBlock)) as RichTextBlock;
            TextBlock.LostFocus += OnLostFocus;
            TextBlock.SizeChanged += OnSizeChanged;
            TextBlock.ContextMenuOpening += _contextMenuOpening;

            for (int i = 0; i < _blocks?.Count; i++)
            {
                var block = _blocks[i] as Paragraph;
                TextBlock.Blocks.Add(block);

                if (i == _blocks.Count - 1 && block.Inlines.Count > 0 && block.Inlines[^1] is Span spanForInlines)
                {
                    _spanForInlines = spanForInlines;
                }
            }

            TextBlock.AddHandler(DoubleTappedEvent, new DoubleTappedEventHandler(OnDoubleTapped), true);
            TextBlock.AddHandler(TappedEvent, new TappedEventHandler(OnTapped), true);

            _templateApplied = true;

            if (_clientService != null && _text != null)
            {
                SetText(_clientService, _text, _fontSize);

                if (_query != null || _spoiler != null)
                {
                    SetQuery(_query, true);
                }
            }
        }

        private void OnLostFocus(object sender, RoutedEventArgs e)
        {
            TextBlock.Select(TextBlock.ContentStart, TextBlock.ContentStart);
        }

        private void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if (e.PointerDeviceType == PointerDeviceType.Mouse)
            {
                _expandSelectionDeadline = Logger.TickCount + BootStrapper.Current.UISettings.DoubleClickTime;
            }
        }

        private void OnTapped(object sender, TappedRoutedEventArgs e)
        {
            // If a double tap is followed by a single tap, then it's a triple tap (duh)
            if (e.PointerDeviceType == PointerDeviceType.Mouse && Logger.TickCount < _expandSelectionDeadline)
            {
                _expandSelectionDeadline = Logger.TickCount + BootStrapper.Current.UISettings.DoubleClickTime;
                VisualUtilities.QueueCallbackForCompositionRendering(ExpandSelection);
            }
        }

        private void ExpandSelection()
        {
            if (TextBlock.SelectionStart != null && TextBlock.SelectionEnd != null)
            {
                static DependencyObject FindParent(DependencyObject obj)
                {
                    if (obj is RichTextBlock or Paragraph)
                    {
                        return obj;
                    }
                    else if (obj is TextElement element)
                    {
                        return FindParent(element.ElementStart.Parent);
                    }

                    return null;
                }

                var startBlock = FindParent(TextBlock.SelectionStart.Parent);
                var endBlock = FindParent(TextBlock.SelectionEnd.Parent);

                if (startBlock == endBlock)
                {
                    try
                    {
                        if (startBlock is TextElement element)
                        {
                            TextBlock.Select(element.ContentStart, element.ContentEnd);
                        }
                        else if (startBlock is RichTextBlock block)
                        {
                            TextBlock.Select(block.ContentStart, block.ContentEnd);
                        }
                    }
                    catch
                    {
                        // All the remote procedure calls must be wrapped in a try-catch block
                    }
                }
            }
        }

        public void Clear()
        {
            _clientService = null;
            //_text = null;

            _query = null;
            _spoiler = null;
            _ignoreSpoilers = false;

            ClearEntities();
        }

        private void ClearEntities()
        {
            foreach (var link in _links)
            {
                ToolTipService.SetToolTip(link, null);
            }

            _links.Clear();
            _spoilers.Clear();
            _codeBlocks.Clear();
        }

        public bool IgnoreSpoilers
        {
            get => _ignoreSpoilers;
            set
            {
                if (value == _ignoreSpoilers)
                {
                    return;
                }

                _ignoreSpoilers = value;

                if (value)
                {
                    SetText(_clientService, _text, _fontSize);
                    SetQuery(string.Empty);

                    if (Below == null || _spoilerPresenter == null)
                    {
                        return;
                    }

                    Below.Children.Remove(_spoilerPresenter);
                    _spoilerPresenter = null;
                }
            }
        }

        public void SetFontSize(double fontSize)
        {
            _fontSize = fontSize;

            if (TextBlock?.Blocks.Count > 0 && TextBlock.Blocks[0] is Paragraph existing)
            {
                existing.FontSize = fontSize;
            }
        }

        public void SetQuery(string query, bool force = false)
        {
            if ((_query ?? string.Empty) == (query ?? string.Empty) && _isHighlighted == (_spoiler != null) && !force && !_invalidateSpoilers)
            {
                return;
            }

            _query = query;
            _invalidateSpoilers = false;

            if (TextBlock == null || !TextBlock.IsLoaded)
            {
                return;
            }

            if (_text != null)
            {
                if (_isHighlighted)
                {
                    _isHighlighted = false;
                    TextBlock.TextHighlighters.Clear();
                }

                if (query?.Length > 0)
                {
                    var find = _text.Text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
                    if (find != -1)
                    {
                        var shift = 0;

                        foreach (var para in _text.Paragraphs)
                        {
                            if (para.Offset + para.Length < find)
                            {
                                shift++;
                            }
                        }

                        var highligher = new TextHighlighter();
                        highligher.Foreground = new SolidColorBrush(Colors.White);
                        highligher.Background = new SolidColorBrush(Colors.Orange);
                        highligher.Ranges.Add(new TextRange { StartIndex = find - shift, Length = query.Length });

                        _isHighlighted = true;
                        TextBlock.TextHighlighters.Add(highligher);
                    }
                }

                if (_spoiler != null)
                {
                    _isHighlighted = true;
                    TextBlock.TextHighlighters.Add(_spoiler);
                }
                else
                {
                    if (Below == null || _spoilerPresenter == null)
                    {
                        return;
                    }

                    Below.Children.Remove(_spoilerPresenter);
                    _spoilerPresenter = null;
                }
            }
            else if (_isHighlighted)
            {
                _isHighlighted = false;
                TextBlock.TextHighlighters.Clear();

                if (Below == null || _spoilerPresenter == null)
                {
                    return;
                }

                Below.Children.Remove(_spoilerPresenter);
                _spoilerPresenter = null;
            }
        }

        public void SetText(IClientService clientService, FormattedText text, double fontSize = 0)
        {
            SetText(clientService, TextStyleRun.GetText(text), fontSize);
        }

        public void SetText(IClientService clientService, string text, IList<TextEntity> entities, double fontSize = 0)
        {
            SetText(clientService, TextStyleRun.GetText(text, entities), fontSize);
        }

        public void SetText(IClientService clientService, StyledText styled, double fontSize = 0)
        {
            var prevPlain = _text?.IsPlain ?? false;
            var prevDirection = prevPlain ? _text?.Paragraphs[0].Direction : TextDirectionality.Neutral;

            _clientService = clientService;
            _text = styled;
            _fontSize = fontSize;

            if (!_templateApplied)
            {
                return;
            }

            var autoFontSize = fontSize;
            var xamlFontSize = TextBlock.FontSize;

            if (AutoFontSize && fontSize == 0)
            {
                fontSize = Theme.Current.MessageFontSize;
            }

            var direct = XamlDirect.GetDefault();

            // PERF: fast path if both model and view have one paragraph with one run
            if (_fastRun != null && styled != null && prevPlain && styled.IsPlain && prevDirection == styled.Paragraphs[0].Direction && !HasCodeBlocks)
            {
                if (_fastFontSize != fontSize)
                {
                    _fastFontSize = fontSize;
                    direct.SetDoubleProperty(_fastRun, XamlPropertyIndex.TextElement_FontSize, fontSize);
                }

                direct.SetStringProperty(_fastRun, XamlPropertyIndex.Run_Text, styled.Text);
                return;
            }

            var locale = LocaleService.Current.FlowDirection;

            var directBlock = direct.GetXamlDirectObject(TextBlock);
            var blocks = direct.GetXamlDirectObjectProperty(directBlock, XamlPropertyIndex.RichTextBlock_Blocks);

            _fastFontSize = fontSize;
            ClearEntities();

            var textOffset = -1;

            if (_spanForInlines == null)
            {
                direct.ClearCollection(blocks);
            }
            else
            {
                _spanForInlines.Inlines.Clear();
            }

            if (string.IsNullOrEmpty(styled?.Text))
            {
                return;
            }

            TextHighlighter spoiler = null;

            var preformatted = false;
            TextParagraphType lastType = null;
            TextParagraphType firstType = null;

            FontFamily monospaceFontFamily = null;
            FontFamily GetMonospaceFontFamily()
            {
                return monospaceFontFamily ?? new FontFamily("Consolas, " + Theme.Current.XamlAutoFontFamily);
            }

            var alignment = TextAlignment;

            var text = styled.Text;
            var offset = 0;

            for (int i = 0; i < styled.Paragraphs.Count; i++)
            {
                StyledParagraph part = styled.Paragraphs[i];

                // This should not happen, but it does.
                text = styled.Text.Substring(part.Offset, Math.Min(part.Length, styled.Text.Length - part.Offset));

                var type = part.Type;
                var runs = part.Runs;
                var partFontSize = fontSize;

                var previous = 0;

                IXamlDirectObject paragraph;
                IXamlDirectObject inlines;
                if (_spanForInlines != null)
                {
                    paragraph = null;
                    inlines = direct.GetXamlDirectObjectProperty(direct.GetXamlDirectObject(_spanForInlines), XamlPropertyIndex.Span_Inlines);
                }
                else
                {
                    paragraph = direct.CreateInstance(XamlTypeIndex.Paragraph);
                    inlines = direct.GetXamlDirectObjectProperty(paragraph, XamlPropertyIndex.Paragraph_Inlines);
                }

                // TODO: we use DetectFromContent, but this could be used too:
                //direct.SetEnumProperty(paragraph, XamlPropertyIndex.Block_TextAlignment, part.Direction switch
                //{
                //    TextDirectionality.LeftToRight => (uint)TextAlignment.Left,
                //    TextDirectionality.RightToLeft => (uint)TextAlignment.Right,
                //    _ => (uint)TextAlignment.DetectFromContent
                //});

                if (alignment == TextAlignment.Center && paragraph != null)
                {
                    direct.SetEnumProperty(paragraph, XamlPropertyIndex.Block_TextAlignment, (uint)alignment);
                }

                var direction = paragraph == null ? locale : part.Direction switch
                {
                    TextDirectionality.LeftToRight => FlowDirection.LeftToRight,
                    TextDirectionality.RightToLeft => FlowDirection.RightToLeft,
                    _ => locale
                };

                if (part.Type is TextParagraphTypeQuote && paragraph != null)
                {
                    var last = part == styled.Paragraphs[^1];
                    var temp = direct.GetObject(paragraph) as Paragraph;
                    temp.Margin = new Thickness(11, 6, 24, last ? 0 : 8);
                    temp.FontSize = Theme.Current.CaptionFontSize;
                    partFontSize = Theme.Current.CaptionFontSize;

                    _codeBlocks.Add(new FormattedParagraph(temp, part));
                }

                for (int j = 0; j < runs.Count; j++)
                {
                    var entity = runs[j];
                    if (entity.Offset > previous)
                    {
                        NativeUtils.AddRunToCollection(direct, inlines, text, previous, entity.Offset - previous, direction, false, TextDecorations.None, null, fontSize: partFontSize, false);
                        offset += entity.Offset - previous;
                    }

                    if (entity.Length + entity.Offset > text.Length)
                    {
                        previous = entity.Offset + entity.Length;
                        continue;
                    }

                    if (entity.HasFlag(Common.TextStyle.Monospace))
                    {
                        var data = text.Substring(entity.Offset, entity.Length);
                        if (paragraph != null)
                        {
                            if (entity.Type is TextEntityTypeCode)
                            {
                                var hyperlink = new Hyperlink();
                                hyperlink.Click += (s, args) => Entity_Click(entity.Offset, entity.Length, entity.Type, data);
                                hyperlink.UnderlineStyle = UnderlineStyle.None;

                                BindingOperations.SetBinding(hyperlink, Hyperlink.ForegroundProperty, new Binding
                                {
                                    Path = new PropertyPath("Foreground"),
                                    Source = this
                                });

                                var native = direct.GetXamlDirectObject(hyperlink);
                                var collection = direct.GetXamlDirectObjectProperty(native, XamlPropertyIndex.Span_Inlines);

                                NativeUtils.AddRunToCollection(direct, collection, data, direction, false, TextDecorations.None, GetMonospaceFontFamily(), partFontSize, false);
                                offset += data.Length;

                                direct.AddToCollection(inlines, native);
                            }
                            else
                            {
                                direct.SetObjectProperty(paragraph, XamlPropertyIndex.TextElement_FontFamily, GetMonospaceFontFamily());

                                NativeUtils.AddRunToCollection(direct, inlines, data, direction, false, TextDecorations.None, null, 0, false);
                                offset += data.Length;

                                preformatted = true;

                                var has = entity.Type is TextEntityTypePreCode { Language.Length: > 0 };

                                var last = part == styled.Paragraphs[^1];
                                var temp = direct.GetObject(paragraph) as Paragraph;

                                direct.SetThicknessProperty(paragraph, XamlPropertyIndex.Block_Margin, new Thickness(11, (has ? 22 : 0) + 6, has ? 8 : 24, last ? 0 : 8));

                                if (entity.Type is TextEntityTypePreCode preCode && preCode.Language.Length > 0)
                                {
                                    _codeBlocks.Add(new FormattedParagraph(temp, part));
                                    ProcessCodeBlock(direct, inlines, data, preCode.Language);
                                }
                                else
                                {
                                    _codeBlocks.Add(new FormattedParagraph(temp, part));
                                }
                            }
                        }
                        else
                        {
                            NativeUtils.AddRunToCollection(direct, inlines, data, direction, false, TextDecorations.None, GetMonospaceFontFamily(), 0, false);
                            offset += data.Length;
                        }
                    }
                    else
                    {
                        IXamlDirectObject parent = null;
                        IXamlDirectObject parentInlines = inlines;

                        if (paragraph != null)
                        {
                            if (_ignoreSpoilers is false && entity.HasFlag(Common.TextStyle.Spoiler))
                            {
                                var hyperlink = new Hyperlink();
                                hyperlink.Click += (s, args) => Entity_Click(entity.Offset, entity.Length, new TextEntityTypeSpoiler(), null);
                                hyperlink.Foreground = null;
                                hyperlink.UnderlineStyle = UnderlineStyle.None;
                                hyperlink.FontFamily = BootStrapper.Current.Resources["SpoilerFontFamily"] as FontFamily;

                                if (SettingsService.Current.Diagnostics.SpoilerEffectDebug)
                                {
                                    _spoilers.Add(new TextStyleSpoiler(entity.Offset, entity.Length, i));
                                }

                                spoiler ??= new TextHighlighter();
                                spoiler.Ranges.Add(new TextRange { StartIndex = offset, Length = entity.Length });

                                parent = direct.GetXamlDirectObject(hyperlink);
                                parentInlines = direct.GetXamlDirectObjectProperty(parent, XamlPropertyIndex.Span_Inlines);
                            }
                            else if ((entity.HasFlag(Common.TextStyle.Mention) || entity.HasFlag(Common.TextStyle.Url)))
                            {
                                if (entity.Type is TextEntityTypeMentionName or TextEntityTypeTextUrl)
                                {
                                    var hyperlink = new Hyperlink();
                                    object data;
                                    if (entity.Type is TextEntityTypeTextUrl textUrl)
                                    {
                                        data = textUrl.Url;
                                        MessageHelper.SetEntityData(hyperlink, textUrl.Url);
                                        MessageHelper.SetEntityType(hyperlink, entity.Type);

                                        _links.Add(hyperlink);

                                        if (textUrl.Url.StartsWith("http"))
                                        {
                                            ToolTipService.SetToolTip(hyperlink, textUrl.Url);
                                        }
                                    }
                                    else if (entity.Type is TextEntityTypeMentionName mentionName)
                                    {
                                        data = mentionName.UserId;
                                    }

                                    hyperlink.Click += (s, args) => Entity_Click(entity.Offset, entity.Length, entity.Type, null);
                                    hyperlink.UnderlineStyle = HyperlinkStyle;
                                    hyperlink.FontWeight = HyperlinkFontWeight;
                                    hyperlink.UnderlineStyle = UnderlineStyle.None;

                                    BindingOperations.SetBinding(hyperlink, Hyperlink.ForegroundProperty, new Binding
                                    {
                                        Path = new PropertyPath("HyperlinkForeground"),
                                        Source = this
                                    });

                                    parent = direct.GetXamlDirectObject(hyperlink);
                                    parentInlines = direct.GetXamlDirectObjectProperty(parent, XamlPropertyIndex.Span_Inlines);
                                }
                                else
                                {
                                    var hyperlink = new Hyperlink();
                                    //var original = entities.FirstOrDefault(x => x.Offset <= entity.Offset && x.Offset + x.Length >= entity.End);

                                    var data = text.Substring(entity.Offset, entity.Length);

                                    //if (original != null)
                                    //{
                                    //    data = text.Substring(original.Offset, original.Length);
                                    //}

                                    hyperlink.Click += (s, args) => Entity_Click(entity.Offset, entity.Length, entity.Type, data);
                                    hyperlink.UnderlineStyle = HyperlinkStyle;
                                    hyperlink.FontWeight = HyperlinkFontWeight;
                                    hyperlink.UnderlineStyle = entity.Type is TextEntityTypeUrl
                                        ? UnderlineStyle.Single
                                        : UnderlineStyle.None;

                                    BindingOperations.SetBinding(hyperlink, Hyperlink.ForegroundProperty, new Binding
                                    {
                                        Path = new PropertyPath("HyperlinkForeground"),
                                        Source = this
                                    });

                                    //if (entity.Type is TextEntityTypeUrl || entity.Type is TextEntityTypeEmailAddress || entity.Type is TextEntityTypeBankCardNumber)
                                    {
                                        MessageHelper.SetEntityData(hyperlink, data);
                                        MessageHelper.SetEntityType(hyperlink, entity.Type);
                                    }

                                    parent = direct.GetXamlDirectObject(hyperlink);
                                    parentInlines = direct.GetXamlDirectObjectProperty(parent, XamlPropertyIndex.Span_Inlines);
                                }
                            }
                        }
                        else if (_ignoreSpoilers is false && entity.HasFlag(Common.TextStyle.Spoiler))
                        {
                            var hyperlink = direct.CreateInstance(XamlTypeIndex.Span);
                            direct.SetObjectProperty(hyperlink, XamlPropertyIndex.TextElement_Foreground, null);
                            direct.SetObjectProperty(hyperlink, XamlPropertyIndex.TextElement_FontFamily, BootStrapper.Current.Resources["SpoilerFontFamily"] as FontFamily);

                            if (SettingsService.Current.Diagnostics.SpoilerEffectDebug)
                            {
                                _spoilers.Add(new TextStyleSpoiler(entity.Offset, entity.Length, i));
                            }

                            if (textOffset == -1)
                            {
                                textOffset = _spanForInlines.ContentStart.OffsetToIndex(TextBlock);
                            }

                            spoiler ??= new TextHighlighter();
                            spoiler.Ranges.Add(new TextRange { StartIndex = textOffset + offset, Length = entity.Length });

                            parent = hyperlink;
                            parentInlines = direct.GetXamlDirectObjectProperty(hyperlink, XamlPropertyIndex.Span_Inlines);
                        }

                        // Consumes local inlines instead of paragraph's
                        // TODO: still use a InlineUIContainer for emojis in spoilers to avoid text resizes
                        if (entity.Type is TextEntityTypeCustomEmoji customEmoji && ((_ignoreSpoilers && entity.HasFlag(Common.TextStyle.Spoiler)) || !entity.HasFlag(Common.TextStyle.Spoiler)))
                        {
                            var data = text.Substring(entity.Offset, entity.Length);

                            var player = new CustomEmojiIcon();
                            player.LoopCount = 0;
                            player.Source = new CustomEmojiFileSource(clientService, customEmoji.CustomEmojiId);
                            player.HorizontalAlignment = HorizontalAlignment.Left;
                            player.FlowDirection = FlowDirection.LeftToRight;
                            player.Style = EmojiStyle;
                            player.IsHitTestVisible = false;
                            player.IsEnabled = false;
                            player.Emoji = data;

                            if (autoFontSize != 0)
                            {
                                player.Width = autoFontSize * (20d / 14d);
                                player.Height = autoFontSize * (20d / 14d);
                                player.Margin = new Thickness(0, -2 * (20d / 14d), 0, -6 * (20d / 14d));
                                player.FrameSize = new Size(autoFontSize * (20d / 14d), autoFontSize * (20d / 14d));
                            }
                            else if (xamlFontSize == 14)
                            {
                                player.Margin = new Thickness(0, -2, 0, -6);
                            }
                            else if (xamlFontSize == 12)
                            {
                                player.Margin = new Thickness(0, 0, 0, -4);
                                player.Width = 16;
                                player.Height = 16;
                                player.FrameSize = new Size(16, 16);
                            }

                            var inline = new InlineUIContainer();
                            inline.Child = player;

                            // We are working around multiple issues here:
                            // ZWNJ is always added right after a custom emoji to make sure that the line height always matches Segoe UI.
                            // RTL/LTR mark is added in case the custom emoji is the first element in the Paragraph.
                            // This is needed because we can't use TextReadingOrder = DetectFromContent due to a bug
                            // that causes text selection and hit tests to follow the flow direction rather than the reading order.
                            // Because of this, we're forced to use TextReadingOrder = UseFlowDirection, and to set each
                            // Run.FlowDirection to the one calculated by calling GetStringTypeEx on the text of each paragraph.
                            // Since InlineUIContainer doesn't have a FlowDirection property (and the child flow direction seems to be ignored)
                            // the first custom emoji in a paragraph with reading order different from the one of the app, would appear on the
                            // wrong side of the block, thus we add a RTL/LTR mark right before, and the RichTextBlock seems to respect this.
                            // Additionally, we need to prepend a ZWNJ character if:
                            // - the paragraph begins by an emoji, to prevent early text trimming in inline mode
                            // - the emoji is preceded by a spoiler, to prevent text highlight to run over the emoji

                            if (entity.Offset == 0 || (entity.Offset == previous && runs[j - 1].HasFlag(Common.TextStyle.Spoiler)))
                            {
                                var character = direction != locale
                                    ? direction == FlowDirection.RightToLeft ? Icons.RTL : Icons.LTR
                                    : Icons.ZWNJ;

                                NativeUtils.AddRunToCollection(direct, inlines, character, direction, false, TextDecorations.None, null, fontSize: partFontSize, transparent: true);
                                offset++;
                            }

                            direct.AddToCollection(inlines, direct.GetXamlDirectObject(inline));
                            NativeUtils.AddRunToCollection(direct, inlines, Icons.ZWNJ, direction, false, TextDecorations.None, null, partFontSize, true);
                            offset++;
                        }
                        else
                        {
                            var decorations = TextDecorations.None;
                            if (entity.HasFlag(Common.TextStyle.Underline))
                            {
                                decorations |= TextDecorations.Underline;
                            }
                            if (entity.HasFlag(Common.TextStyle.Strikethrough))
                            {
                                decorations |= TextDecorations.Strikethrough;
                            }

                            var run = NativeUtils.AddRunToCollection(direct, parentInlines, text, entity.Offset, entity.Length, direction, entity.HasFlag(Common.TextStyle.Italic), decorations, null, partFontSize, false);
                            offset += entity.Length;

                            // Doing this here because in C++ SetObjectProperty expects a IInspectable and FontWeight isn't
                            if (entity.HasFlag(Common.TextStyle.Bold))
                            {
                                direct.SetObjectProperty(run, XamlPropertyIndex.TextElement_FontWeight, FontWeights.SemiBold);
                            }
                        }

                        if (parent != null)
                        {
                            direct.AddToCollection(inlines, parent);
                        }
                    }

                    previous = entity.Offset + entity.Length;
                }

                if (text.Length > previous)
                {
                    _fastRun = NativeUtils.AddRunToCollection(direct, inlines, text, previous, text.Length - previous, direction, false, TextDecorations.None, null, partFontSize, false);
                    offset += text.Length - previous;
                }

                if (paragraph != null)
                {
                    direct.AddToCollection(blocks, paragraph);
                }
                else if (i < styled.Paragraphs.Count - 1)
                {
                    NativeUtils.AddRunToCollection(direct, inlines, " ", direction, false, TextDecorations.None, null, 0, false);
                    offset++;
                }

                if (part.Offset == 0)
                {
                    firstType = type;
                }

                lastType = type;
            }

            //Padding = new Thickness(0, firstFormatted ? 4 : 0, 0, 0);

            //ContentPanel.MaxWidth = preformatted ? double.PositiveInfinity : 432;

            //_isFormatted = runs.Count > 0 || fontSize != 0;
            HasCodeBlocks = preformatted;

            var spoilerChanged = (_spoiler != null) != (spoiler != null);
            if (spoiler?.Ranges.Count > 0)
            {
                spoiler.Foreground = new SolidColorBrush(Colors.Transparent);
                spoiler.Background = new SolidColorBrush(SettingsService.Current.Diagnostics.SpoilerEffectDebug ? Colors.Transparent : Colors.Black);

                _invalidateSpoilers = _spoiler != null;
                _spoiler = spoiler;
            }
            else
            {
                _invalidateSpoilers = _spoiler != null;
                _spoiler = null;
            }

            var topPadding = 0d;
            var bottomPadding = false;

            if (_spanForInlines == null)
            {
                if (firstType is TextParagraphTypeMonospace { Language.Length: > 0 })
                {
                    topPadding = 22 + 6;
                }
                else if (firstType is not null)
                {
                    topPadding = 6;
                }

                if (AdjustLineEnding && styled.Paragraphs.Count > 0)
                {
                    var direction = styled.Paragraphs[^1].Direction switch
                    {
                        TextDirectionality.LeftToRight => FlowDirection.LeftToRight,
                        TextDirectionality.RightToLeft => FlowDirection.RightToLeft,
                        _ => locale
                    };

                    if (direction != locale || lastType is not null)
                    {
                        bottomPadding = true;
                    }
                }
            }

            HasLineEnding = bottomPadding;

            Below.Margin = new Thickness(0, topPadding, 0, 0);
            TextBlock.Margin = new Thickness(0, topPadding, 0, 0);

            if (spoilerChanged && !_layoutUpdated)
            {
                _layoutUpdated = true;
                TextBlock.LayoutUpdated += OnLayoutUpdated;
            }
        }

        private bool _layoutUpdated;

        private void OnLayoutUpdated(object sender, object e)
        {
            UpdateComponent();
        }

        private void OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateComponent();
        }

        private void UpdateComponent()
        {
            if (_layoutUpdated)
            {
                _layoutUpdated = false;
                TextBlock.LayoutUpdated -= OnLayoutUpdated;
            }

            UpdateBelow();
            UpdateSpoilers();
        }

        private void UpdateBelow()
        {
            Below.Children.ClearIfNotEmpty();

            var fontSize = (AutoFontSize ? Theme.Current.MessageFontSize : TextBlock.FontSize) * BootStrapper.Current.TextScaleFactor;
            var quoteSize = (AutoFontSize ? Theme.Current.CaptionFontSize : TextBlock.FontSize) * BootStrapper.Current.TextScaleFactor;

            var width = Math.Ceiling(ActualWidth + 1);

            foreach (var block in _codeBlocks)
            {
                var paragraph = block.Paragraph;
                var styled = block.Styled;

                var partial = _text.Text.Substring(styled.Offset, styled.Length);
                var entities = styled.Entities ?? Array.Empty<TextEntity>();

                var size = styled.Type is TextParagraphTypeQuote
                    ? quoteSize
                    : fontSize;

                var rectangles = PlaceholderImageHelper.Foreground.LayoutMetrics(partial, 0, partial.Length, entities, size, width - paragraph.Margin.Left - paragraph.Margin.Right, styled.Direction == TextDirectionality.RightToLeft);
                var relative = paragraph.ContentStart.GetCharacterRect(paragraph.ContentStart.LogicalDirection);
                var end = paragraph.ContentEnd.GetCharacterRect(paragraph.ContentEnd.LogicalDirection);

                var startY = Math.Round(relative.Y);
                var endBottom = Math.Round(end.Bottom);

                if (styled.Type is TextParagraphTypeMonospace monospace && monospace.Language.Length > 0)
                {
                    var rect = new BlockCode();
                    rect.Width = rectangles.Width + paragraph.Margin.Left + paragraph.Margin.Right;
                    rect.Height = Math.Max(endBottom - startY + 6 + 22, 0);
                    rect.LanguageName = monospace.Language;
                    Canvas.SetLeft(rect, rectangles.X);
                    Canvas.SetTop(rect, startY - 2 - 22);

                    Below.Children.Add(rect);
                }
                else
                {
                    var rect = new BlockQuote();
                    rect.Width = rectangles.Width + paragraph.Margin.Left + paragraph.Margin.Right;
                    rect.Height = Math.Max(endBottom - startY + 6, 0);
                    rect.Glyph = paragraph.FontSize == Theme.Current.MessageFontSize ? Icons.CodeFilled16 : Icons.QuoteBlockFilled16;
                    Canvas.SetLeft(rect, rectangles.X);
                    Canvas.SetTop(rect, startY - 2);

                    Below.Children.Add(rect);
                }
            }

            if (_spoilerPresenter != null)
            {
                Below.Children.Add(_spoilerPresenter);
            }
        }

        private void UpdateSpoilers()
        {
            if (_ignoreSpoilers || _spoilers.Empty())
            {
                if (_spoilerPresenter != null)
                {
                    Below.Children.Remove(_spoilerPresenter);
                    _spoilerPresenter = null;
                }

                return;
            }

            var fontSize = (AutoFontSize ? Theme.Current.MessageFontSize : TextBlock.FontSize) * BootStrapper.Current.TextScaleFactor;
            var quoteSize = (AutoFontSize ? Theme.Current.CaptionFontSize : TextBlock.FontSize) * BootStrapper.Current.TextScaleFactor;

            var width = Math.Ceiling(TextBlock.ActualWidth + 1);
            var inset = 0;

            var position = new Windows.Foundation.Point(0, 0);

            var shapes = new List<List<Rect>>();
            var current = new List<Rect>();
            var last = default(Rect);

            var minX = double.MaxValue;
            var minY = double.MaxValue;
            var maxX = double.MinValue;
            var maxY = double.MinValue;

            if (_spanForInlines == null)
            {
                // Would be cool to optimize this for contiguous paragraphs
                foreach (var hyperlink in _spoilers)
                {
                    StyledParagraph styled = _text.Paragraphs[hyperlink.ParagraphIndex];
                    Paragraph paragraph = TextBlock.Blocks[hyperlink.ParagraphIndex] as Paragraph;

                    if (hyperlink.ParagraphIndex == 0)
                    {
                        inset = styled.Type switch
                        {
                            TextParagraphTypeMonospace { Language.Length: > 0 } => 22 + 6,
                            not null => 6,
                            _ => 0
                        };
                    }

                    int xoffset = hyperlink.Offset;
                    int xlength = hyperlink.Length;

                    var partial = _text.Text.Substring(styled.Offset, styled.Length);
                    var entities = styled.Entities ?? Array.Empty<TextEntity>();

                    var size = styled.Type is TextParagraphTypeQuote
                        ? quoteSize
                        : fontSize;

                    var rectangles = PlaceholderImageHelper.Foreground.RangeMetrics(partial, xoffset, xlength, entities, size, width - paragraph.Margin.Left - paragraph.Margin.Right, styled.Direction == TextDirectionality.RightToLeft, true);
                    var relative = paragraph.ContentStart.GetCharacterRect(paragraph.ContentStart.LogicalDirection);

                    var point = new Windows.Foundation.Point(paragraph.Margin.Left + position.X, relative.Y + position.Y + inset);

                    for (int i = 0; i < rectangles.Count; i++)
                    {
                        var rect = rectangles[i];
                        rect = new Rect(rect.X, rect.Y, rect.Width, rect.Height);
                        rect.X += point.X;
                        rect.Y += point.Y;

                        if (current.Count > 0 && !rect.IntersectsWith(last))
                        {
                            shapes.Add(current);
                            current = new List<Rect>();
                        }

                        current.Add(rect);
                        last = rect;

                        minX = Math.Min(minX, rect.Left);
                        minY = Math.Min(minY, rect.Top);
                        maxX = Math.Max(maxX, rect.Right);
                        maxY = Math.Max(maxY, rect.Bottom);
                    }
                }
            }
            else
            {
                var paragraph = TextBlock.Blocks[^1] as Paragraph;

                Rect relative;
                if (paragraph.Inlines.Count > 1)
                {
                    relative = paragraph.Inlines[^2].ContentEnd.GetCharacterRect(LogicalDirection.Forward);
                }
                else
                {
                    relative = paragraph.Inlines[^1].ContentStart.GetCharacterRect(LogicalDirection.Forward);
                }

                // Would be cool to optimize this for contiguous paragraphs
                foreach (var hyperlink in _spoilers)
                {
                    StyledParagraph styled = _text.Paragraphs[hyperlink.ParagraphIndex];

                    int xoffset = styled.Offset + hyperlink.Offset;
                    int xlength = hyperlink.Length;

                    var partial = _text.Text.Replace('\n', ' ');
                    var entities = _text.Entities;

                    var size = fontSize;

                    var rectangles = PlaceholderImageHelper.Foreground.RangeMetrics(partial, xoffset, xlength, entities, size, width - relative.X, styled.Direction == TextDirectionality.RightToLeft, false);
                    var point = new Windows.Foundation.Point(relative.X + position.X, relative.Y + position.Y + inset);

                    for (int i = 0; i < rectangles.Count; i++)
                    {
                        var rect = rectangles[i];
                        rect = new Rect(rect.X, rect.Y, rect.Width, rect.Height);
                        rect.X += point.X;
                        rect.Y += point.Y;

                        if (current.Count > 0 && !rect.IntersectsWith(last))
                        {
                            shapes.Add(current);
                            current = new List<Rect>();
                        }

                        current.Add(rect);
                        last = rect;

                        minX = Math.Min(minX, rect.Left);
                        minY = Math.Min(minY, rect.Top);
                        maxX = Math.Max(maxX, rect.Right);
                        maxY = Math.Max(maxY, rect.Bottom);
                    }
                }
            }

            //maxX = Math.Min(maxX, TextBlock.ActualWidth);
            //maxY = Math.Min(maxY, TextBlock.ActualHeight);

            if (current.Count > 0)
            {
                shapes.Add(current);
            }

            CanvasGeometry result;
            using (var builder = new CanvasPathBuilder(null))
            {
                for (int j = 0; j < shapes.Count; j++)
                {
                    var rectangles = shapes[j];

                    for (int i = 0; i < rectangles.Count; i++)
                    {
                        var rectangle = rectangles[i];
                        rectangle.X -= minX;
                        rectangle.Y -= minY;

                        builder.AddGeometry(CanvasGeometry.CreateRectangle(null, rectangle));
                    }
                }

                result = CanvasGeometry.CreatePath(builder);
            }

            Color foreground = Colors.Black;
            if (Foreground is SolidColorBrush brush)
            {
                foreground = brush.Color;
            }

            if (_spoilerPresenter == null)
            {
                _spoilerPresenter = new AnimatedImage
                {
                    IsViewportAware = true,
                    FrameSize = new Size(0, 0),
                    FitToSize = true,
                    DecodeFrameType = DecodePixelType.Logical,
                    Stretch = Stretch.UniformToFill,
                    Source = new ParticlesImageSource(foreground),
                    Width = maxX - minX,
                    Height = maxY - minY
                };

                Below.Children.Add(_spoilerPresenter);
            }
            else
            {
                _spoilerPresenter.Width = maxX - minX;
                _spoilerPresenter.Height = maxY - minY;
            }

            Canvas.SetLeft(_spoilerPresenter, minX);
            Canvas.SetTop(_spoilerPresenter, minY);

            var visual = ElementComposition.GetElementVisual(_spoilerPresenter);
            var geometry = visual.Compositor.CreatePathGeometry(new CompositionPath(result));
            visual.Clip = visual.Compositor.CreateGeometricClip(geometry);
        }

        private void OnActualThemeChanged(FrameworkElement sender, object args)
        {
            var resources = sender.ActualTheme == ElementTheme.Light ? _light : _dark;

            foreach (var item in _brushes)
            {
                item.Value.Color = resources[item.Key];
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private Run CreateRun(string text, ref int offset, FlowDirection direction, FontWeight? fontWeight = null, FontFamily fontFamily = null, double fontSize = 0)
        {
            var direct = XamlDirect.GetDefault();
            var run = direct.CreateInstance(XamlTypeIndex.Run);
            direct.SetStringProperty(run, XamlPropertyIndex.Run_Text, text);
            direct.SetEnumProperty(run, XamlPropertyIndex.Run_FlowDirection, (uint)direction);

            offset += text.Length;

            if (fontWeight != null)
            {
                direct.SetObjectProperty(run, XamlPropertyIndex.TextElement_FontWeight, fontWeight.Value);
            }

            if (fontFamily != null)
            {
                direct.SetObjectProperty(run, XamlPropertyIndex.TextElement_FontFamily, fontFamily);
            }

            if (fontSize > 0)
            {
                direct.SetDoubleProperty(run, XamlPropertyIndex.TextElement_FontSize, fontSize);
            }

            return direct.GetObject(run) as Run;
        }

        #region PreCode

        private async void ProcessCodeBlock(XamlDirect direct, IXamlDirectObject inlines, string text, string language)
        {
            try
            {
                var tokens = await SyntaxToken.TokenizeAsync(language.ToLowerInvariant(), text);

                direct.ClearCollection(inlines);
                ProcessCodeBlock(direct, inlines, tokens.Children);
            }
            catch
            {
                // Tokenization may fail
            }
        }

        private void ProcessCodeBlock(XamlDirect direct, IXamlDirectObject inlines, IList<Token> tokens)
        {
            var fontFamily = new FontFamily("Consolas, " + Theme.Current.XamlAutoFontFamily);

            foreach (var token in tokens)
            {
                if (token is SyntaxToken syntax)
                {
                    var color = GetColor(syntax.Type);
                    if (color == null && syntax.Alias.Length > 0)
                    {
                        color = GetColor(syntax.Alias);
                    }

                    var span = direct.CreateInstance(XamlTypeIndex.Span);
                    var collection = direct.GetXamlDirectObjectProperty(span, XamlPropertyIndex.Span_Inlines);

                    direct.SetObjectProperty(span, XamlPropertyIndex.TextElement_FontFamily, fontFamily);

                    if (color != null)
                    {
                        direct.SetObjectProperty(span, XamlPropertyIndex.TextElement_Foreground, color);
                    }

                    if (syntax.Type == "bold")
                    {
                        direct.SetObjectProperty(span, XamlPropertyIndex.TextElement_FontWeight, FontWeights.SemiBold);
                    }
                    else if (syntax.Type == "italic")
                    {
                        direct.SetEnumProperty(span, XamlPropertyIndex.TextElement_FontStyle, (uint)FontStyle.Italic);
                    }

                    ProcessCodeBlock(direct, collection, syntax.Children);
                    direct.AddToCollection(inlines, span);
                }
                else if (token is TextToken text)
                {
                    NativeUtils.AddRunToCollection(direct, inlines, text.Value, FlowDirection.LeftToRight, false, TextDecorations.None, fontFamily, 0, false);
                }
            }
        }

        SolidColorBrush GetColor(string type)
        {
            if (_brushes.TryGetValue(type, out var brush))
            {
                return brush;
            }

            var target = ActualTheme == ElementTheme.Light ? _light : _dark;
            if (target.TryGetValue(type, out var color))
            {
                _brushes[type] = new SolidColorBrush(color);
                return _brushes[type];
            }

            return null;
        }

        private readonly Dictionary<string, Color> _light = new()
        {
            { "comment", Colors.SlateGray },
            { "block-comment", Colors.SlateGray },
            { "prolog", Colors.SlateGray },
            { "doctype", Colors.SlateGray },
            { "cdata", Colors.SlateGray },
            { "punctuation", Color.FromArgb(0xFF, 0x99, 0x99, 0x99) },
            { "property", Color.FromArgb(0xFF, 0x99, 0x00, 0x55) },
            { "tag", Color.FromArgb(0xFF, 0x99, 0x00, 0x55) },
            { "boolean", Color.FromArgb(0xFF, 0x99, 0x00, 0x55) },
            { "number", Color.FromArgb(0xFF, 0x99, 0x00, 0x55) },
            { "constant", Color.FromArgb(0xFF, 0x99, 0x00, 0x55) },
            { "symbol", Color.FromArgb(0xFF, 0x99, 0x00, 0x55) },
            { "deleted", Color.FromArgb(0xFF, 0x99, 0x00, 0x55) },
            { "selector", Color.FromArgb(0xFF, 0x66, 0x99, 0x00) },
            { "attr-name", Color.FromArgb(0xFF, 0x66, 0x99, 0x00) },
            { "string", Color.FromArgb(0xFF, 0x66, 0x99, 0x00) },
            { "char", Color.FromArgb(0xFF, 0x66, 0x99, 0x00) },
            { "builtin", Color.FromArgb(0xFF, 0x66, 0x99, 0x00) },
            { "inserted", Color.FromArgb(0xFF, 0x66, 0x99, 0x00) },
            { "operator", Color.FromArgb(0xFF, 0x9a, 0x6e, 0x3a) },
            { "entity", Color.FromArgb(0xFF, 0x9a, 0x6e, 0x3a) },
            { "url", Color.FromArgb(0xFF, 0x9a, 0x6e, 0x3a) },
            { "atrule", Color.FromArgb(0xFF, 0x00, 0x77, 0xAA) },
            { "attr-value", Color.FromArgb(0xFF, 0x00, 0x77, 0xAA) },
            { "keyword", Color.FromArgb(0xFF, 0x00, 0x77, 0xAA) },
            { "function", Color.FromArgb(0xFF, 0x00, 0x77, 0xAA) },
            { "class-name", Color.FromArgb(0xFF, 0xDD, 0x4A, 0x68) },
        };

        private readonly Dictionary<string, Color> _dark = new()
        {
            { "comment", Color.FromArgb(0xFF, 0x99, 0x99, 0x99) },
            { "block-comment", Color.FromArgb(0xFF, 0x99, 0x99, 0x99) },
            { "prolog", Color.FromArgb(0xFF, 0x99, 0x99, 0x99) },
            { "doctype", Color.FromArgb(0xFF, 0x99, 0x99, 0x99) },
            { "cdata", Color.FromArgb(0xFF, 0x99, 0x99, 0x99) },
            { "punctuation", Color.FromArgb(0xFF, 0xCC, 0xCC, 0xCC) },
            { "property", Color.FromArgb(0xFF, 0xf8, 0xc5, 0x55) },
            { "tag", Color.FromArgb(0xFF, 0xe2, 0x77, 0x7a) },
            { "boolean", Color.FromArgb(0xFF, 0xf0, 0x8d, 0x49) },
            { "number", Color.FromArgb(0xFF, 0xf0, 0x8d, 0x49) },
            { "constant", Color.FromArgb(0xFF, 0xf8, 0xc5, 0x55) },
            { "symbol", Color.FromArgb(0xFF, 0xf8, 0xc5, 0x55) },
            { "deleted", Color.FromArgb(0xFF, 0xe2, 0x77, 0x7a) },
            { "selector", Color.FromArgb(0xFF, 0xcc, 0x99, 0xcd) },
            { "attr-name", Color.FromArgb(0xFF, 0xe2, 0x77, 0x7a) },
            { "string", Color.FromArgb(0xFF, 0x7e, 0xc6, 0x99) },
            { "char", Color.FromArgb(0xFF, 0x7e, 0xc6, 0x99) },
            { "builtin", Color.FromArgb(0xFF, 0xcc, 0x99, 0xcd) },
            { "inserted", Color.FromArgb(0xFF, 0x66, 0x99, 0x00) },
            { "operator", Color.FromArgb(0xFF, 0x67, 0xcd, 0xcc) },
            { "entity", Color.FromArgb(0xFF, 0x67, 0xcd, 0xcc) },
            { "url", Color.FromArgb(0xFF, 0x67, 0xcd, 0xcc) },
            { "atrule", Color.FromArgb(0xFF, 0xcc, 0x99, 0xcd) },
            { "attr-value", Color.FromArgb(0xFF, 0x7e, 0xc6, 0x99) },
            { "keyword", Color.FromArgb(0xFF, 0xcc, 0x99, 0xcd) },
            { "function", Color.FromArgb(0xFF, 0xf0, 0x8d, 0x49) },
            { "class-name", Color.FromArgb(0xFF, 0xf8, 0xc5, 0x55) },
            // namespace 0xe2, 0x77, 0x7a
            // function-name 6196cc
        };

        private readonly Dictionary<string, SolidColorBrush> _brushes = new();

        private CancellationTokenSource _token;

        #endregion

        private void Entity_Click(int offset, int length, TextEntityType type, object data)
        {
            TextEntityClick?.Invoke(this, new TextEntityClickEventArgs(offset, length, type, data));
        }

        #region TextAlignment

        public TextAlignment TextAlignment
        {
            get { return (TextAlignment)GetValue(TextAlignmentProperty); }
            set { SetValue(TextAlignmentProperty, value); }
        }

        public static readonly DependencyProperty TextAlignmentProperty =
            DependencyProperty.Register("TextAlignment", typeof(TextAlignment), typeof(FormattedTextBlock), new PropertyMetadata(TextAlignment.Left));

        #endregion

        #region EmojiStyle

        public Style EmojiStyle
        {
            get { return (Style)GetValue(EmojiStyleProperty); }
            set { SetValue(EmojiStyleProperty, value); }
        }

        public static readonly DependencyProperty EmojiStyleProperty =
            DependencyProperty.Register("EmojiStyle", typeof(Style), typeof(FormattedTextBlock), new PropertyMetadata(null));

        #endregion

        #region IsTextSelectionEnabled

        public bool IsTextSelectionEnabled
        {
            get { return (bool)GetValue(IsTextSelectionEnabledProperty); }
            set { SetValue(IsTextSelectionEnabledProperty, value); }
        }

        public static readonly DependencyProperty IsTextSelectionEnabledProperty =
            DependencyProperty.Register("IsTextSelectionEnabled", typeof(bool), typeof(FormattedTextBlock), new PropertyMetadata(true));

        #endregion

        #region OverflowContentTarget

        public RichTextBlockOverflow OverflowContentTarget
        {
            get { return (RichTextBlockOverflow)GetValue(OverflowContentTargetProperty); }
            set { SetValue(OverflowContentTargetProperty, value); }
        }

        public static readonly DependencyProperty OverflowContentTargetProperty =
            DependencyProperty.Register("OverflowContentTarget", typeof(RichTextBlockOverflow), typeof(FormattedTextBlock), new PropertyMetadata(null));

        #endregion

        #region TextTrimming

        public TextTrimming TextTrimming
        {
            get { return (TextTrimming)GetValue(TextTrimmingProperty); }
            set { SetValue(TextTrimmingProperty, value); }
        }

        public static readonly DependencyProperty TextTrimmingProperty =
            DependencyProperty.Register("TextTrimming", typeof(TextTrimming), typeof(FormattedTextBlock), new PropertyMetadata(TextTrimming.None));

        #endregion

        #region TextWrapping

        public TextWrapping TextWrapping
        {
            get { return (TextWrapping)GetValue(TextWrappingProperty); }
            set { SetValue(TextWrappingProperty, value); }
        }

        public static readonly DependencyProperty TextWrappingProperty =
            DependencyProperty.Register("TextWrapping", typeof(TextWrapping), typeof(FormattedTextBlock), new PropertyMetadata(TextWrapping.Wrap));

        #endregion

        #region HorizontalTextAlignment

        public TextAlignment HorizontalTextAlignment
        {
            get { return (TextAlignment)GetValue(HorizontalTextAlignmentProperty); }
            set { SetValue(HorizontalTextAlignmentProperty, value); }
        }

        public static readonly DependencyProperty HorizontalTextAlignmentProperty =
            DependencyProperty.Register("HorizontalTextAlignment", typeof(TextAlignment), typeof(FormattedTextBlock), new PropertyMetadata(TextAlignment.Left));

        #endregion

        #region TextReadingOrder

        public TextReadingOrder TextReadingOrder
        {
            get { return (TextReadingOrder)GetValue(TextReadingOrderProperty); }
            set { SetValue(TextReadingOrderProperty, value); }
        }

        public static readonly DependencyProperty TextReadingOrderProperty =
            DependencyProperty.Register("TextReadingOrder", typeof(TextReadingOrder), typeof(FormattedTextBlock), new PropertyMetadata(TextReadingOrder.UseFlowDirection));

        #endregion

        public TextDecorations TextDecorations
        {
            get { return (TextDecorations)GetValue(TextDecorationsProperty); }
            set { SetValue(TextDecorationsProperty, value); }
        }

        public static readonly DependencyProperty TextDecorationsProperty =
            DependencyProperty.Register("TextDecorations", typeof(TextDecorations), typeof(FormattedTextBlock), new PropertyMetadata(TextDecorations.None));

        #region TextDecorations

        #endregion

        #region MaxLines

        public int MaxLines
        {
            get { return (int)GetValue(MaxLinesProperty); }
            set { SetValue(MaxLinesProperty, value); }
        }

        public static readonly DependencyProperty MaxLinesProperty =
            DependencyProperty.Register("MaxLines", typeof(int), typeof(FormattedTextBlock), new PropertyMetadata(0));

        #endregion

        #region Hyperlink

        public bool AutoFontSize { get; set; } = true;

        public UnderlineStyle HyperlinkStyle { get; set; } = UnderlineStyle.Single;

        public FontWeight HyperlinkFontWeight { get; set; } = FontWeights.Normal;

        #endregion

        #region HyperlinkForeground

        public Brush HyperlinkForeground
        {
            get { return (Brush)GetValue(HyperlinkForegroundProperty); }
            set { SetValue(HyperlinkForegroundProperty, value); }
        }

        public static readonly DependencyProperty HyperlinkForegroundProperty =
            DependencyProperty.Register("HyperlinkForeground", typeof(Brush), typeof(FormattedTextBlock), new PropertyMetadata(null));

        #endregion

        public bool HasOverflowContent => TextBlock?.HasOverflowContent ?? false;
    }
}
