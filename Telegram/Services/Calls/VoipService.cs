//
// Copyright Fela Ameghino 2015-2025
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Common;
using Telegram.Native.Calls;
using Telegram.Navigation.Services;
using Telegram.Services.Calls;
using Telegram.Services.Updates;
using Telegram.Td.Api;
using Telegram.Views.Calls.Popups;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Telegram.Services
{
    public interface IVoipService
    {
        VoipCallBase ActiveCall { get; }

        void StartPrivateCall(INavigationService navigation, Chat chat, bool video);
        void StartPrivateCall(INavigationService navigation, User user, bool video);

        void JoinGroupCall(INavigationService navigation, InputGroupCall groupCall);
        void JoinGroupCall(INavigationService navigation, long chatId, string inviteHash = null);

        void CreateGroupCall(INavigationService navigation, IList<long> userIds);
        void CreateGroupCall(INavigationService navigation, long chatId);
    }

    public partial class VoipService : ServiceBase, IVoipService
    {
        private readonly object _activeLock = new();
        private VoipCallBase _activeCall;

        public VoipService(IClientService clientService, ISettingsService settingsService, IEventAggregator aggregator)
            : base(clientService, settingsService, aggregator)
        {
            Aggregator.Subscribe<UpdateCall>(this, Handle)
                .Subscribe<UpdateNewCallSignalingData>(Handle)
                .Subscribe<UpdateGroupCall>(Handle)
                .Subscribe<UpdateGroupCallParticipant>(Handle)
                .Subscribe<UpdateGroupCallVerificationState>(Handle);
        }

        public VoipCallBase ActiveCall
        {
            get
            {
                lock (_activeLock)
                {
                    return _activeCall;
                }
            }
        }

        #region Private

        private async Task<bool> CheckActiveCallAsync(INavigationService navigation, object source)
        {
            VoipCallBase activeCall;
            lock (_activeLock)
            {
                activeCall = _activeCall;
            }

            if (activeCall != null)
            {
                if (activeCall is VoipCall privateCall && ClientService.TryGetUser(privateCall.UserId, out User activeUser))
                {
                    string message;
                    string title;

                    if (source is User newUser && newUser.Id != privateCall.UserId)
                    {
                        message = string.Format(Strings.VoipOngoingAlert, activeUser.FullName(), newUser.FullName());
                        title = Strings.VoipOngoingAlertTitle;
                    }
                    else if (source is Chat newChat)
                    {
                        message = string.Format(Strings.VoipOngoingAlert2, activeUser.FullName(), newChat.Title);
                        title = Strings.VoipOngoingAlertTitle;
                    }
                    else
                    {
                        activeCall.Show();
                        return true;
                    }

                    var confirm = await navigation.ShowPopupAsync(message, title, Strings.OK, Strings.Cancel);
                    if (confirm == ContentDialogResult.Primary)
                    {
                        privateCall.Discard();
                        return false;
                    }
                }
                else if (activeCall is VoipGroupCall groupCall && groupCall.Chat != null && ClientService.TryGetChat(groupCall.Chat.Id, out Chat activeChat))
                {
                    string message;
                    string title;

                    // TODO: not the right string for conference calls

                    if (source is Chat newChat && newChat.Id != activeChat.Id)
                    {
                        message = string.Format(Strings.VoipOngoingChatAlert, activeChat.Title, newChat.Title);
                        title = Strings.VoipOngoingChatAlertTitle;
                    }
                    else if (source is User newUser)
                    {
                        message = string.Format(Strings.VoipOngoingChatAlert2, activeChat.Title, newUser.FullName());
                        title = Strings.VoipOngoingChatAlertTitle;
                    }
                    else
                    {
                        activeCall.Show();
                        return true;
                    }

                    var confirm = await navigation.ShowPopupAsync(message, title, Strings.OK, Strings.Cancel);
                    if (confirm == ContentDialogResult.Primary)
                    {
                        groupCall.Discard();
                        return false;
                    }
                }
            }

            return false;
        }

        public void StartPrivateCall(INavigationService navigation, Chat chat, bool video)
        {
            if (chat == null)
            {
                return;
            }

            if (ClientService.TryGetUser(chat, out User user))
            {
                StartPrivateCall(navigation, user, video);
            }
        }

        public async void StartPrivateCall(INavigationService navigation, User user, bool video)
        {
            if (MediaDevicePermissions.IsUnsupported(navigation.XamlRoot))
            {
                return;
            }

            if (user == null)
            {
                return;
            }

            var activeCall = await CheckActiveCallAsync(navigation, user);
            if (activeCall)
            {
                return;
            }

            var fullInfo = ClientService.GetUserFull(user.Id);
            if (fullInfo != null && fullInfo.HasPrivateCalls)
            {
                await navigation.ShowPopupAsync(string.Format(Strings.CallNotAvailable, user.FirstName), Strings.VoipFailed, Strings.OK);
                return;
            }

            var permissions = await MediaDevicePermissions.CheckAccessAsync(navigation.XamlRoot, video ? MediaDeviceAccess.AudioAndVideo : MediaDeviceAccess.Audio);
            if (permissions == false)
            {
                return;
            }

            var protocol = VoipManager.Protocol;

            var response = await ClientService.SendAsync(new CreateCall(user.Id, protocol, video));
            if (response is Error error)
            {
                if (error.Code == 400 && error.Message.Equals("PARTICIPANT_VERSION_OUTDATED"))
                {
                    var message = video
                        ? Strings.VoipPeerVideoOutdated
                        : Strings.VoipPeerOutdated;
                    await navigation.ShowPopupAsync(string.Format(message, user.FirstName), Strings.AppName, Strings.OK);
                }
                else if (error.Code == 400 && error.Message.Equals("USER_PRIVACY_RESTRICTED"))
                {
                    await navigation.ShowPopupAsync(string.Format(Strings.CallNotAvailable, user.FullName()), Strings.AppName, Strings.OK);
                }
            }
        }

        #endregion

        #region Group

        public async void JoinGroupCall(INavigationService navigation, InputGroupCall groupCall)
        {
            if (MediaDevicePermissions.IsUnsupported(navigation.XamlRoot))
            {
                return;
            }

            var xamlRoot = navigation.XamlRoot;

            ThreadPool.QueueUserWorkItem(state =>
            {
                var changed = false;

                lock (_activeLock)
                {
                    _activeCall = new VoipGroupCall(ClientService, Settings, Aggregator, xamlRoot, groupCall);
                    changed = false;
                }

                Aggregator.Publish(new UpdateActiveCall());

                //if (changed)
                //{
                //    Aggregator.Publish(new UpdateGroupCall(new GroupCall(groupCall.Id, groupCall.Title, groupCall.InviteLink, groupCall.ScheduledStartDate, groupCall.EnabledStartNotification, groupCall.IsActive, groupCall.IsVideoChat, groupCall.IsRtmpStream, true, false, groupCall.IsOwned, groupCall.CanBeManaged, groupCall.ParticipantCount, groupCall.HasHiddenListeners, groupCall.LoadedAllParticipants, groupCall.RecentSpeakers, groupCall.IsMyVideoEnabled, groupCall.IsMyVideoPaused, groupCall.CanEnableVideo, groupCall.MuteNewParticipants, groupCall.CanToggleMuteNewParticipants, groupCall.RecordDuration, groupCall.IsVideoRecorded, groupCall.Duration)));
                //}
            });
        }

        public async void CreateGroupCall(INavigationService navigation, IList<long> userIds)
        {
            if (MediaDevicePermissions.IsUnsupported(navigation.XamlRoot))
            {
                return;
            }

            var xamlRoot = navigation.XamlRoot;

            ThreadPool.QueueUserWorkItem(state =>
            {
                var changed = false;

                lock (_activeLock)
                {
                    _activeCall = new VoipGroupCall(ClientService, Settings, Aggregator, xamlRoot, userIds);
                    changed = false;
                }

                Aggregator.Publish(new UpdateActiveCall());

                //if (changed)
                //{
                //    Aggregator.Publish(new UpdateGroupCall(new GroupCall(groupCall.Id, groupCall.Title, groupCall.InviteLink, groupCall.ScheduledStartDate, groupCall.EnabledStartNotification, groupCall.IsActive, groupCall.IsVideoChat, groupCall.IsRtmpStream, true, false, groupCall.IsOwned, groupCall.CanBeManaged, groupCall.ParticipantCount, groupCall.HasHiddenListeners, groupCall.LoadedAllParticipants, groupCall.RecentSpeakers, groupCall.IsMyVideoEnabled, groupCall.IsMyVideoPaused, groupCall.CanEnableVideo, groupCall.MuteNewParticipants, groupCall.CanToggleMuteNewParticipants, groupCall.RecordDuration, groupCall.IsVideoRecorded, groupCall.Duration)));
                //}
            });
        }

        public async void JoinGroupCall(INavigationService navigation, long chatId, string inviteHash)
        {
            if (MediaDevicePermissions.IsUnsupported(navigation.XamlRoot))
            {
                return;
            }

            var chat = ClientService.GetChat(chatId);
            if (chat == null || chat.VideoChat.GroupCallId == 0)
            {
                return;
            }

            var activeCall = await CheckActiveCallAsync(navigation, chat);
            if (activeCall)
            {
                return;
            }

            await JoinAsyncInternal(navigation.XamlRoot, chat, chat.VideoChat.GroupCallId, null, inviteHash);
        }

        public async void CreateGroupCall(INavigationService navigation, long chatId)
        {
            if (MediaDevicePermissions.IsUnsupported(navigation.XamlRoot))
            {
                return;
            }

            var chat = ClientService.GetChat(chatId);
            if (chat == null || chat.VideoChat.GroupCallId != 0)
            {
                return;
            }

            var activeCall = await CheckActiveCallAsync(navigation, chat);
            if (activeCall)
            {
                return;
            }

            MessageSenders availableAliases;
            availableAliases = await ClientService.SendAsync(new GetVideoChatAvailableParticipants(chatId)) as MessageSenders;
            availableAliases ??= new MessageSenders(0, Array.Empty<MessageSender>());

            var popup = new VideoChatAliasesPopup(ClientService, chat, true, availableAliases.Senders);

            var confirm = await popup.ShowQueuedAsync(navigation.XamlRoot);
            if (confirm == ContentDialogResult.Primary)
            {
                var alias = popup.SelectedSender ?? new MessageSenderUser(ClientService.Options.MyId);
                var startDate = 0;

                if (popup.IsScheduleSelected)
                {
                    var schedule = new ScheduleVideoChatPopup(chat.Type is ChatTypeSupergroup supergroup && supergroup.IsChannel);

                    var again = await schedule.ShowQueuedAsync(navigation.XamlRoot);
                    if (again != ContentDialogResult.Primary)
                    {
                        return;
                    }

                    startDate = schedule.Value.ToTimestamp();
                }
                else if (popup.IsStartWithSelected)
                {
                    var streams = new VideoChatStreamsPopup(ClientService, chat.Id, true);

                    var again = await streams.ShowQueuedAsync(navigation.XamlRoot);
                    if (again != ContentDialogResult.Primary)
                    {
                        return;
                    }

                    if (streams.IsScheduleSelected)
                    {
                        var schedule = new ScheduleVideoChatPopup(true);

                        var oneMore = await schedule.ShowQueuedAsync(navigation.XamlRoot);
                        if (oneMore != ContentDialogResult.Primary)
                        {
                            return;
                        }

                        startDate = schedule.Value.ToTimestamp();
                    }
                }

                var response = await ClientService.SendAsync(new CreateVideoChat(chat.Id, string.Empty, startDate, popup.IsStartWithSelected));
                if (response is GroupCallId groupCallId)
                {
                    await JoinAsyncInternal(navigation.XamlRoot, chat, groupCallId.Id, alias, string.Empty);
                }
            }
        }

        private async Task JoinAsyncInternal(XamlRoot xamlRoot, Chat chat, int groupCallId, MessageSender alias, string inviteHash)
        {
            alias ??= chat.VideoChat.DefaultParticipantId;

            var response = await ClientService.SendAsync(new GetGroupCall(groupCallId));
            if (response is not GroupCall groupCall)
            {
                return;
            }

            if (alias == null && !groupCall.IsRtmpStream)
            {
                MessageSenders availableAliases;
                availableAliases = await ClientService.SendAsync(new GetVideoChatAvailableParticipants(chat.Id)) as MessageSenders;
                availableAliases ??= new MessageSenders(0, Array.Empty<MessageSender>());

                var popup = new VideoChatAliasesPopup(ClientService, chat, false, availableAliases.Senders);

                var confirm = await popup.ShowQueuedAsync(xamlRoot);
                if (confirm != ContentDialogResult.Primary)
                {
                    return;
                }

                alias = popup.SelectedSender ?? new MessageSenderUser(ClientService.Options.MyId);
            }

            if (!groupCall.IsRtmpStream)
            {
                var permissions = await MediaDevicePermissions.CheckAccessAsync(xamlRoot, MediaDeviceAccess.Audio);
                if (permissions == false)
                {
                    return;
                }
            }

            ThreadPool.QueueUserWorkItem(state =>
            {
                var changed = false;

                lock (_activeLock)
                {
                    _activeCall = new VoipGroupCall(ClientService, Settings, Aggregator, xamlRoot, chat, groupCall, alias, inviteHash);
                    changed = groupCall.ScheduledStartDate > 0;
                }

                Aggregator.Publish(new UpdateActiveCall());

                if (changed)
                {
                    Aggregator.Publish(new UpdateGroupCall(new GroupCall(groupCall.Id, groupCall.Title, groupCall.InviteLink, groupCall.ScheduledStartDate, groupCall.EnabledStartNotification, groupCall.IsActive, groupCall.IsVideoChat, groupCall.IsRtmpStream, true, false, groupCall.IsOwned, groupCall.CanBeManaged, groupCall.ParticipantCount, groupCall.HasHiddenListeners, groupCall.LoadedAllParticipants, groupCall.RecentSpeakers, groupCall.IsMyVideoEnabled, groupCall.IsMyVideoPaused, groupCall.CanEnableVideo, groupCall.MuteNewParticipants, groupCall.CanToggleMuteNewParticipants, groupCall.RecordDuration, groupCall.IsVideoRecorded, groupCall.Duration)));
                }
            });
        }

        #endregion

        public void Handle(UpdateNewCallSignalingData update)
        {
            lock (_activeLock)
            {
                if (_activeCall is VoipCall privateCall && privateCall.Id == update.CallId)
                {
                    privateCall.ReceiveSignalingData(update.Data);
                }
            }
        }

        public void Handle(UpdateCall update)
        {
            var state = ToState(update.Call);
            if (state == VoipState.None)
            {
                return;
            }

            var changed = false;

            lock (_activeLock)
            {
                if (state == VoipState.Requesting || (state == VoipState.Ringing && !update.Call.IsOutgoing))
                {
                    if (_activeCall != null)
                    {
                        // Line is busy
                        ClientService.Send(new DiscardCall(update.Call.Id, true, string.Empty, 0, false, 0));
                    }
                    else
                    {
                        _activeCall = new VoipCall(ClientService, Settings, Aggregator, update.Call, state);
                        changed = true;
                    }
                }
                else if (_activeCall is VoipCall privateCall && privateCall.Id == update.Call.Id)
                {
                    privateCall.Update(update.Call, state);

                    if (state is VoipState.Discarded or VoipState.Error)
                    {
                        _activeCall = null;
                        changed = true;
                    }
                    //else if (state is VoipState.Ready && update.Call.GroupCallId != 0)
                    //{
                    //    ClientService.Send(new GetGroupCall(update.Call.GroupCallId));
                    //}
                }
            }

            if (changed)
            {
                Aggregator.Publish(new UpdateActiveCall());
            }
        }

        public void Handle(UpdateGroupCall update)
        {
            var changed = false;

            lock (_activeLock)
            {
                if (_activeCall is VoipGroupCall groupCall && groupCall.Id == update.GroupCall.Id)
                {
                    groupCall.Update(update.GroupCall, out bool closed);

                    if (closed)
                    {
                        _activeCall = null;
                        changed = true;
                    }
                }
                //else if (_activeCall is VoipCall call && call.GroupCallId == update.GroupCall.Id && !_upgrading)
                //{
                //    _upgrading = true;

                //    ClientService.TryGetChatFromUser(call.UserId, out Chat chat);

                //    WindowContext.ForEach(window =>
                //    {
                //        if (window.Content is VoipPage page)
                //        {
                //            _ = JoinAsyncInternal(page.XamlRoot, chat, call.GroupCallId, null, string.Empty, true);
                //        }
                //    });
                //}
            }

            if (changed)
            {
                Aggregator.Publish(new UpdateActiveCall());
            }
        }

        private bool _upgrading;

        public void Handle(UpdateGroupCallParticipant update)
        {
            lock (_activeLock)
            {
                if (_activeCall is VoipGroupCall groupCall && groupCall.Id == update.GroupCallId)
                {
                    groupCall.Update(update.Participant);
                }
            }
        }

        public void Handle(UpdateGroupCallVerificationState update)
        {
            lock (_activeLock)
            {
                if (_activeCall is VoipGroupCall groupCall && groupCall.Id == update.GroupCallId)
                {
                    groupCall.Update(update.Generation, update.Emojis);
                }
            }
        }

        private static VoipState ToState(Call call)
        {
            return call.State switch
            {
                CallStatePending { IsCreated: false, IsReceived: false } => VoipState.Requesting, // outgoing only
                CallStatePending { IsCreated: true, IsReceived: false } => VoipState.Waiting, // outgoing only
                CallStatePending { IsCreated: true, IsReceived: true } => VoipState.Ringing,
                CallStateExchangingKeys => VoipState.Connecting,
                CallStateReady => VoipState.Ready,
                CallStateHangingUp => VoipState.HangingUp,
                CallStateDiscarded => VoipState.Discarded,
                CallStateError => VoipState.Error,
                _ => VoipState.None
            };
        }
    }
}
