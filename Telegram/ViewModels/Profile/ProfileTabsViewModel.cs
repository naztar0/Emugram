//
// Copyright Fela Ameghino & Contributors 2015-2025
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Telegram.Collections;
using Telegram.Common;
using Telegram.Navigation;
using Telegram.Navigation.Services;
using Telegram.Services;
using Telegram.Td.Api;
using Telegram.Views;
using Telegram.Views.Chats;
using Telegram.Views.Profile;
using Windows.UI.Xaml.Navigation;
using WinRT;

namespace Telegram.ViewModels.Profile
{
    [GeneratedBindableCustomProperty]
    public partial class ProfileTabItem : BindableBase
    {
        private readonly ICollectionWithTotalCount _items;
        private readonly int _totalCount;
        private readonly string _locale;

        public ProfileTabItem(string text, Type type, object parameter = null)
        {
            Text = text;
            Type = type;
            Parameter = parameter;
        }

        public ProfileTabItem(string text, Type type, object parameter, int totalCount, string locale)
        {
            Text = text;
            Type = type;
            Parameter = parameter;

            _totalCount = totalCount;
            _locale = locale;
        }

        public ProfileTabItem(string text, Type type, object parameter, ICollectionWithTotalCount items, string locale)
        {
            Text = text;
            Type = type;
            Parameter = parameter;

            _items = items;
            _items.PropertyChanged += OnPropertyChanged;

            _locale = locale;
        }

        public string Text { get; set; }

        public Type Type { get; set; }

        public object Parameter { get; set; }

        public string Subtitle => Locale.Declension(_locale, _items?.TotalCount ?? _totalCount);

        private void OnPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(_items.TotalCount))
            {
                RaisePropertyChanged(nameof(Subtitle));
            }
        }
    }

    public partial class ProfileMyArgs
    {

    }

    public abstract partial class ProfileTabsViewModel : MediaTabsViewModelBase, IHandle
    {
        protected readonly ProfileSavedChatsTabViewModel _savedChatsViewModel;
        protected readonly ProfileStoriesTabViewModel _pinnedStoriesTabViewModel;
        protected readonly ProfileStoriesTabViewModel _archivedStoriesTabViewModel;
        protected readonly ProfileGroupsTabViewModel _groupsTabViewModel;
        protected readonly ProfileChannelsTabViewModel _channelsTabViewModel;
        protected readonly ProfileBotsTabViewModel _botsTabViewModel;
        protected readonly ProfileGiftsTabViewModel _giftsTabViewModel;
        protected readonly ProfileMembersTabViewModel _membersTabVieModel;

        public ProfileTabsViewModel(IClientService clientService, ISettingsService settingsService, IStorageService storageService, IEventAggregator aggregator)
            : base(clientService, settingsService, storageService, aggregator)
        {
            _savedChatsViewModel = TypeResolver.Current.Resolve<ProfileSavedChatsTabViewModel>(clientService.SessionId);
            _pinnedStoriesTabViewModel = TypeResolver.Current.Resolve<ProfileStoriesTabViewModel>(clientService.SessionId);
            _archivedStoriesTabViewModel = TypeResolver.Current.Resolve<ProfileStoriesTabViewModel>(clientService.SessionId);
            _groupsTabViewModel = TypeResolver.Current.Resolve<ProfileGroupsTabViewModel>(clientService.SessionId);
            _channelsTabViewModel = TypeResolver.Current.Resolve<ProfileChannelsTabViewModel>(clientService.SessionId);
            _botsTabViewModel = TypeResolver.Current.Resolve<ProfileBotsTabViewModel>(clientService.SessionId);
            _giftsTabViewModel = TypeResolver.Current.Resolve<ProfileGiftsTabViewModel>(clientService.SessionId);
            _membersTabVieModel = TypeResolver.Current.Resolve<ProfileMembersTabViewModel>(clientService.SessionId);
            _membersTabVieModel.IsEmbedded = true;

            _pinnedStoriesTabViewModel.SetType(ChatStoriesType.Pinned);
            _archivedStoriesTabViewModel.SetType(ChatStoriesType.Archive);

            Children.Add(_savedChatsViewModel);
            Children.Add(_pinnedStoriesTabViewModel);
            Children.Add(_archivedStoriesTabViewModel);
            Children.Add(_groupsTabViewModel);
            Children.Add(_channelsTabViewModel);
            Children.Add(_botsTabViewModel);
            Children.Add(_giftsTabViewModel);
            Children.Add(_membersTabVieModel);

            Items = new ObservableCollection<ProfileTabItem>();
        }

        public ObservableCollection<ProfileTabItem> Items { get; }

        protected ForumTopic _forumTopic;
        public ForumTopic ForumTopic
        {
            get => _forumTopic;
            set => Set(ref _forumTopic, value);
        }

        protected SavedMessagesTopic _savedMessagesTopic;
        public SavedMessagesTopic SavedMessagesTopic
        {
            get => _savedMessagesTopic;
            set => Set(ref _savedMessagesTopic, value);
        }

        public bool MyProfile { get; private set; }

        public bool IsSavedMessages { get; private set; }

        public override Task NavigatedToAsync(object parameter, NavigationMode mode, NavigationState state)
        {
            if (parameter is ProfileMyArgs)
            {
                parameter = ClientService.Options.MyId;
                MyProfile = true;
            }

            if (parameter is long chatId && !MyProfile)
            {
                IsSavedMessages = chatId == ClientService.Options.MyId;
            }
            else if (parameter is ChatMessageTopic chatMessageTopic)
            {
                IsSavedMessages = chatMessageTopic.ChatId == ClientService.Options.MyId;
            }

            return base.NavigatedToAsync(parameter, mode, state);
        }

        protected override async Task OnNavigatedToAsync(object parameter, NavigationMode mode, NavigationState state)
        {
            if (parameter is ChatMessageTopic chatMessageTopic)
            {
                parameter = chatMessageTopic.ChatId;
            }

            var chatId = (long)parameter;

            if (state.TryGet("selectedIndex", out int selectedIndex))
            {
                SelectedIndex = selectedIndex;
            }

            Chat = ClientService.GetChat(chatId);

            Media.UpdateQuery(string.Empty);
            Files.UpdateQuery(string.Empty);
            Links.UpdateQuery(string.Empty);
            Music.UpdateQuery(string.Empty);
            Voice.UpdateQuery(string.Empty);
            Animations.UpdateQuery(string.Empty);

            if (Items.Empty())
            {
                await UpdateTabsAsync(Chat);
            }
        }

        private ProfileTabItem _selectedItem;
        public ProfileTabItem SelectedItem
        {
            get => _selectedItem;
            set => Set(ref _selectedItem, value);
        }

        protected abstract Task UpdateTabsAsync(Chat chat);

        protected async Task UpdateSharedCountAsync(Chat chat)
        {
            var filters = new SearchMessagesFilter[]
            {
                new SearchMessagesFilterPhotoAndVideo(),
                new SearchMessagesFilterEmpty(),
                new SearchMessagesFilterDocument(),
                new SearchMessagesFilterUrl(),
                new SearchMessagesFilterAudio(),
                new SearchMessagesFilterVoiceNote(),
                new SearchMessagesFilterAnimation(),
            };

            var sparseMessagesAvailable = SettingsService.Current.Diagnostics.SparseMessagesDebug
                && Topic is MessageTopicSavedMessages or null;

            var savedMessagesTopicId = 0L;
            if (Topic is MessageTopicSavedMessages savedMessagesTopic)
            {
                savedMessagesTopicId = savedMessagesTopic.SavedMessagesTopicId;
            }

            async Task<Count> GetCountAsync(SearchMessagesFilter filter)
            {
                if (filter is SearchMessagesFilterEmpty)
                {
                    if (IsSavedMessages || MyProfile || !SettingsService.Current.Diagnostics.SavedMessagesDebug)
                    {
                        return new Count(0);
                    }

                    var response = await ClientService.SendAsync(new GetSavedMessagesTopicHistory(chat.Id, 0, 0, 1));
                    if (response is Messages messages)
                    {
                        return new Count(messages.TotalCount);
                    }

                    return new Count(0);
                }

                if (sparseMessagesAvailable && filter is SearchMessagesFilterPhotoAndVideo or SearchMessagesFilterDocument or SearchMessagesFilterAudio or SearchMessagesFilterVoiceNote or SearchMessagesFilterAnimation)
                {
                    var source = await MediaDataSource.Create(ClientService, chat.Id, savedMessagesTopicId, filter);
                    if (source.Count > 0)
                    {
                        switch (filter)
                        {
                            case SearchMessagesFilterPhotoAndVideo:
                            case SearchMessagesFilterPhoto:
                            case SearchMessagesFilterVideo:
                                Media.DataSource = source;
                                break;
                            case SearchMessagesFilterDocument:
                                Files.DataSource = source;
                                break;
                            case SearchMessagesFilterAudio:
                                Music.DataSource = source;
                                break;
                            case SearchMessagesFilterVoiceNote:
                                Voice.DataSource = source;
                                break;
                            case SearchMessagesFilterAnimation:
                                Animations.DataSource = source;
                                break;
                        }
                    }

                    return new Count(source.Count);
                }

                return await ClientService.SendAsync(new GetChatMessageCount(chat.Id, Topic, filter, false)) as Count;
            }

            for (int i = 0; i < filters.Length; i++)
            {
                var response = await GetCountAsync(filters[i]);
                if (response is Count count)
                {
                    if (count.CountValue > 0)
                    {
                        var item = filters[i] switch
                        {
                            SearchMessagesFilterPhotoAndVideo => new ProfileTabItem(Strings.SharedMediaTab2, typeof(ProfileMediaTabPage), null, count.CountValue, Strings.R.Media),
                            SearchMessagesFilterEmpty => new ProfileTabItem(Strings.SavedMessagesTab2, typeof(ProfileSavedMessagesTabPage), new ChatMessageTopic(ClientService.Options.MyId, new MessageTopicSavedMessages(chat.Id)), count.CountValue, Strings.R.SavedMessagesCount),
                            SearchMessagesFilterDocument => new ProfileTabItem(Strings.SharedFilesTab2, typeof(ProfileFilesTabPage), null, count.CountValue, Strings.R.Files),
                            SearchMessagesFilterUrl => new ProfileTabItem(Strings.SharedLinksTab2, typeof(ProfileLinksTabPage), null, count.CountValue, Strings.R.Links),
                            SearchMessagesFilterAudio => new ProfileTabItem(Strings.SharedMusicTab2, typeof(ProfileMusicTabPage), null, count.CountValue, Strings.R.MusicFiles),
                            SearchMessagesFilterVoiceNote => new ProfileTabItem(Strings.SharedVoiceTab2, typeof(ProfileVoiceTabPage), null, count.CountValue, Strings.R.Voice),
                            SearchMessagesFilterAnimation => new ProfileTabItem(Strings.SharedGIFsTab2, typeof(ProfileAnimationsTabPage), null, count.CountValue, Strings.R.GIFs),
                            _ => null
                        };

                        AddTab(item);
                    }
                }
            }
        }

        protected void AddTab(ProfileTabItem item)
        {
            Items.Add(item);

            if (Items.Count == 1)
            {
                SelectedItem ??= Items.FirstOrDefault();
            }
        }

        protected override bool ShouldHandleDeleteMessages(UpdateDeleteMessages update)
        {
            return update.ChatId == _chat?.Id;
        }

        protected Chat _chat;
        public Chat Chat
        {
            get => _chat;
            set => Set(ref _chat, value);
        }

        private int _selectedIndex;
        public int SelectedIndex
        {
            get => _selectedIndex;
            set => Set(ref _selectedIndex, value);
        }

        public override MediaCollection SetSearch(object sender, string query)
        {
            var target = sender switch
            {
                SearchMessagesFilterPhotoAndVideo => Media,
                SearchMessagesFilterPhoto => Media,
                SearchMessagesFilterVideo => Media,
                SearchMessagesFilterDocument => Files,
                SearchMessagesFilterAudio => Music,
                SearchMessagesFilterVoiceNote => Voice,
                SearchMessagesFilterAnimation => Animations,
                _ => null
            };

            if (sender is SearchMessagesFilter filter && (target?.DataSource == null || query.Length > 0))
            {
                if (target != null)
                {
                    target.UseDataSource = false;
                }

                return new MediaCollection(ClientService, Chat.Id, Topic, filter, query);
            }

            if (target != null)
            {
                target.UseDataSource = true;
            }

            return null;
        }
    }
}
