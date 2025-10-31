//
// Copyright Fela Ameghino 2015-2025
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Telegram.Common;
using Telegram.Native;
using Telegram.Td.Api;
using Windows.Storage;
using Future = Telegram.Services.StorageService.Future;

namespace Telegram.Services
{
    public partial class ClientService
    {
        /*
         * How does this work?
         * 
         * As a general rule, all files are downloaded by TDLib into the app cache.
         * The goal however, is to make the local cache folder invisible to the user,
         * and to only provide access to the files through the Downloads folder instead.
         * 
         * # Automatic downloads
         * Nothing happens in this case, automatic downloads always end up in cache.
         * 
         * # Manual downloads
         * All the downloads that pass through the download manager (aka manual downloads)
         * are automatically copied to the user Downloads folder as soon as the download is completed.
         * We do this operation in two steps:
         * 
         * 1. AddFileToDownloads
         * - When the download is started, a temporary file is created in the final location.
         * - The file will look something like this: Unconfirmed {fileId}.tdownload
         * - The file is then added to the system FutureAccessList using the file UniqueId+temp as token.
         * Note: this only happens if FutureAccessList doesn't contain any of UniqueId or UniqueId+temp tokens.
         * 
         * 2. TrackDownloadedFile
         * - Whenever an UpdateFile event is received and the download is actually completed,
         * - we check in the FutureAccessList if there's any file belonging to it, by using UniqueId+temp as token.
         * - if this is the case, we retrieve both the file from cache and the temporary file in the Downloads folder.
         * - we then proceed by replacing the latter with a copy with the cache file, that is then renamed with the final name.
         * - finally we can remove UniqueId+temp from FutureAccessList and add the final UniqueId to the list.
         * 
         * # Using the files
         * The app will always rely on TDLib LocalFile to determine a file status.
         * This means that if the user clears the app cache, the link between cached and permanent files will be broken.
         * This considered, the user must be able to perform different actions on the downloaded files, including:
         * 
         * 1. OpenFile(With)Async and OpenFolderAsync (IStorageService)
         * - We make sure that the LocalFile from TDLib reports IsDownloadingCompleted as true
         * - If yes, we try to retrieve the permanent file from FutureAccessList using UniqueId
         *   - If the permanent file doesn't exist or it was edited after being copied, we do nothing
         *   - Otherwise we create a new unique copy of the file in the Downloads folder and we add it to the FutureAccessList
         * - We launch the file
         * 
         * 2. SaveFileAsAsync (IStorageService)
         * - We make sure that the LocalFile from TDLib reports IsDownloadingCompleted as true
         * - If yes, we try to retrieve the cache file
         *   - We save the copy
         * - If not, and the download didn't start yet
         *   - We call AddFileToDownloads passing the custom location
         * 
         * # Other scenarios
         * All the stuff that needs to be also considered:
         * 
         * 1. User manually deletes the permanent file
         * FutureAccessList is not kept synchronized by the system, so it's not enough to call ContainsItem,
         * a try-catch on GetFileAsync is needed to make sure that the file is still accessible.
         * Note: the file will still be visible as "downloaded" within the app.
         * 
         */

        private readonly HashSet<int> _canceledDownloads = new();
        private readonly HashSet<string> _completedDownloads = new();
        private readonly object _downloadsLock = new();

        public Task<File> GetFileAsync(int fileId)
        {
            var tsc = new TaskCompletionSource<File>();
            Send(new GetFile(fileId), result =>
            {
                if (result is File file)
                {
                    tsc.SetResult(ProcessFile(file));
                }
                else
                {
                    tsc.SetResult(null);
                }
            });

            return tsc.Task;
        }

        public async Task<StorageFile> GetFileAsync(File file, bool completed = true)
        {
            if (file == null)
            {
                return null;
            }

            // Extremely important to do this only for completed,
            // as this method is being used by RemoteFileStream as well.
            if (completed)
            {
                await SendAsync(new DownloadFile(file.Id, 16, 0, 0, false));
            }

            if (file.Local.IsDownloadingCompleted || !completed)
            {
                try
                {
                    return await StorageFile.GetFileFromPathAsync(file.Local.Path);
                }
                catch (System.IO.FileNotFoundException)
                {
                    Send(new DeleteFile(file.Id));
                }
                catch { }

                return null;
            }

            return null;
        }

        public async Task<StorageFile> GetPermanentFileAsync(File file)
        {
            if (file == null)
            {
                return null;
            }
            else if (ApiInfo.HasCacheOnly || !SettingsService.Current.IsDownloadFolderEnabled)
            {
                return await GetFileAsync(file, true);
            }

            // Let's TDLib check the file integrity
            if (file.Local.IsDownloadingCompleted)
            {
                await SendAsync(new DownloadFile(file.Id, 16, 0, 0, false));
            }

            // If it's still valid, we can proceed with the operation
            if (file.Local.IsDownloadingCompleted && file.Remote.UniqueId.Length > 0)
            {
                try
                {
                    var permanent = await Future.GetFileAsync(file.Remote.UniqueId);
                    if (permanent == null)
                    {
                        lock (_downloadsLock)
                        {
                            _completedDownloads.Add(file.Remote.UniqueId);
                        }

                        var source = await StorageFile.GetFileFromPathAsync(file.Local.Path);
                        if (Future.CheckAccess(source))
                        {
                            return source;
                        }
                        else
                        {
                            var sourceName = source.Name;

                            var response = await SendAsync(new GetSuggestedFileName(file.Id, string.Empty));
                            if (response is Text text)
                            {
                                sourceName = text.TextValue;
                            }

                            var destination = await Future.CreateFileAsync(sourceName);

                            await source.CopyAndReplaceAsync(destination);
                            Future.AddOrReplace(file.Remote.UniqueId, destination);

                            return destination;
                        }
                    }

                    return permanent;
                }
                catch
                {
                    Future.Remove(file.Remote.UniqueId);
                }
            }

            return null;
        }

        public async void AddFileToDownloads(File file, long chatId, long messageId, int priority = 30)
        {
            Send(new AddFileToDownloads(file.Id, chatId, messageId, priority));

            if (ApiInfo.HasCacheOnly || !SettingsService.Current.IsDownloadFolderEnabled || Future.Contains(file.Remote.UniqueId, true) || await Future.ContainsAsync(file.Remote.UniqueId))
            {
                return;
            }

            try
            {
                StorageFile destination = await Future.CreateFileAsync($"Unconfirmed {file.Id}.tdownload");
                Future.AddOrReplace(file.Remote.UniqueId, destination, true);
            }
            catch
            {
                Future.Remove(file.Remote.UniqueId, true);
            }
        }

        private async void TrackDownloadedFile(File file)
        {
            if (ApiInfo.HasDownloadFolder
                && SettingsService.Current.IsDownloadFolderEnabled
                && file.Local.IsDownloadingCompleted
                && file.Remote.IsUploadingCompleted
                && Future.Contains(file.Remote.UniqueId, true))
            {
                lock (_downloadsLock)
                {
                    if (_completedDownloads.Contains(file.Remote.UniqueId))
                    {
                        return;
                    }

                    _completedDownloads.Add(file.Remote.UniqueId);
                }

                try
                {
                    StorageFile source = await StorageFile.GetFileFromPathAsync(file.Local.Path);
                    StorageFile destination = await Future.GetFileAsync(file.Remote.UniqueId, true);

                    var sourceName = source.Name;

                    var response = await SendAsync(new GetSuggestedFileName(file.Id, string.Empty));
                    if (response is Text text)
                    {
                        sourceName = text.TextValue;
                    }

                    await source.CopyAndReplaceAsync(destination);
                    await destination.RenameAsync(sourceName, NameCollisionOption.GenerateUniqueName);

                    Future.Remove(file.Remote.UniqueId, true);
                    Future.AddOrReplace(file.Remote.UniqueId, destination);
                }
                catch
                {
                    Future.Remove(file.Remote.UniqueId, true);
                }
            }
        }

        public async void CancelDownloadFile(File file, bool onlyIfPending = false)
        {
            lock (_downloadsLock)
            {
                _canceledDownloads.Add(file.Id);
                _completedDownloads.Remove(file.Remote.UniqueId);
            }

            Send(new CancelDownloadFile(file.Id, onlyIfPending));
            Send(new RemoveFileFromDownloads(file.Id, false));

            if (ApiInfo.HasCacheOnly)
            {
                return;
            }

            try
            {
                var destination = await Future.GetFileAsync(file.Remote.UniqueId, true);

                Future.Remove(file.Remote.UniqueId, true);

                if (destination != null)
                {
                    await destination.DeleteAsync(StorageDeleteOption.PermanentDelete);
                }
            }
            catch
            {
                // All the remote procedure calls must be wrapped in a try-catch block
            }
        }

        public bool IsDownloadFileCanceled(int fileId)
        {
            lock (_downloadsLock)
            {
                return _canceledDownloads.Contains(fileId);
            }
        }

        private File ProcessFile(File file)
        {
            if (_files.TryGetValue(file.Id, out File singleton))
            {
                singleton.Update(file);
                return singleton;
            }
            else
            {
                _files[file.Id] = file;

                if (file.Local.IsDownloadingCompleted && !NativeUtils.FileExists(file.Local.Path))
                {
                    Send(new DeleteFile(file.Id));
                }

                return file;
            }
        }

        public void ProcessFiles(ref Object target)
        {
            ProcessFiles(target);

            if (target is global::Telegram.Td.Api.Chat chat)
            {
                if (_chats.TryGetValue(chat.Id, out ChatProjection projection))
                {
                    target = projection;
                }
                else
                {
                    // THIS SHOULD NEVER HAPPEN
                    if (ApiInfo.IsPackagedRelease)
                    {
                        Debug.Assert(false, "Not found chat in ProcessFiles");
                    }

                    target = new ChatProjection(chat);
                }
            }
        }

        public void ProcessFiles(object target)
        {
            switch (target)
            {
                case global::Telegram.Td.Api.AdvertisementSponsor advertisementSponsor:
                    if (advertisementSponsor.Photo != null)
                    {
                        ProcessFiles(advertisementSponsor.Photo);
                    }
                    break;
                case global::Telegram.Td.Api.AlternativeVideo alternativeVideo:
                    if (alternativeVideo.HlsFile != null)
                    {
                        alternativeVideo.HlsFile = ProcessFile(alternativeVideo.HlsFile);
                    }
                    if (alternativeVideo.Video != null)
                    {
                        alternativeVideo.Video = ProcessFile(alternativeVideo.Video);
                    }
                    break;
                case global::Telegram.Td.Api.AnimatedChatPhoto animatedChatPhoto:
                    if (animatedChatPhoto.File != null)
                    {
                        animatedChatPhoto.File = ProcessFile(animatedChatPhoto.File);
                    }
                    break;
                case global::Telegram.Td.Api.AnimatedEmoji animatedEmoji:
                    if (animatedEmoji.Sound != null)
                    {
                        animatedEmoji.Sound = ProcessFile(animatedEmoji.Sound);
                    }
                    if (animatedEmoji.Sticker != null)
                    {
                        ProcessFiles(animatedEmoji.Sticker);
                    }
                    break;
                case global::Telegram.Td.Api.Animation animation:
                    if (animation.AnimationValue != null)
                    {
                        animation.AnimationValue = ProcessFile(animation.AnimationValue);
                    }
                    if (animation.Thumbnail != null)
                    {
                        ProcessFiles(animation.Thumbnail);
                    }
                    break;
                case global::Telegram.Td.Api.Animations animations:
                    foreach (var item in animations.AnimationsValue)
                    {
                        ProcessFiles(item);
                    }
                    break;
                case global::Telegram.Td.Api.AttachmentMenuBot attachmentMenuBot:
                    if (attachmentMenuBot.AndroidIcon != null)
                    {
                        attachmentMenuBot.AndroidIcon = ProcessFile(attachmentMenuBot.AndroidIcon);
                    }
                    if (attachmentMenuBot.AndroidSideMenuIcon != null)
                    {
                        attachmentMenuBot.AndroidSideMenuIcon = ProcessFile(attachmentMenuBot.AndroidSideMenuIcon);
                    }
                    if (attachmentMenuBot.DefaultIcon != null)
                    {
                        attachmentMenuBot.DefaultIcon = ProcessFile(attachmentMenuBot.DefaultIcon);
                    }
                    if (attachmentMenuBot.IosAnimatedIcon != null)
                    {
                        attachmentMenuBot.IosAnimatedIcon = ProcessFile(attachmentMenuBot.IosAnimatedIcon);
                    }
                    if (attachmentMenuBot.IosSideMenuIcon != null)
                    {
                        attachmentMenuBot.IosSideMenuIcon = ProcessFile(attachmentMenuBot.IosSideMenuIcon);
                    }
                    if (attachmentMenuBot.IosStaticIcon != null)
                    {
                        attachmentMenuBot.IosStaticIcon = ProcessFile(attachmentMenuBot.IosStaticIcon);
                    }
                    if (attachmentMenuBot.MacosIcon != null)
                    {
                        attachmentMenuBot.MacosIcon = ProcessFile(attachmentMenuBot.MacosIcon);
                    }
                    if (attachmentMenuBot.MacosSideMenuIcon != null)
                    {
                        attachmentMenuBot.MacosSideMenuIcon = ProcessFile(attachmentMenuBot.MacosSideMenuIcon);
                    }
                    if (attachmentMenuBot.WebAppPlaceholder != null)
                    {
                        attachmentMenuBot.WebAppPlaceholder = ProcessFile(attachmentMenuBot.WebAppPlaceholder);
                    }
                    break;
                case global::Telegram.Td.Api.Audio audio:
                    if (audio.AlbumCoverThumbnail != null)
                    {
                        ProcessFiles(audio.AlbumCoverThumbnail);
                    }
                    if (audio.AudioValue != null)
                    {
                        audio.AudioValue = ProcessFile(audio.AudioValue);
                    }
                    foreach (var item in audio.ExternalAlbumCovers)
                    {
                        ProcessFiles(item);
                    }
                    break;
                case global::Telegram.Td.Api.Audios audios:
                    foreach (var item in audios.AudiosValue)
                    {
                        ProcessFiles(item);
                    }
                    break;
                case global::Telegram.Td.Api.AvailableGift availableGift:
                    if (availableGift.Gift != null)
                    {
                        ProcessFiles(availableGift.Gift);
                    }
                    break;
                case global::Telegram.Td.Api.AvailableGifts availableGifts:
                    foreach (var item in availableGifts.Gifts)
                    {
                        ProcessFiles(item);
                    }
                    break;
                case global::Telegram.Td.Api.Background background:
                    if (background.Document != null)
                    {
                        ProcessFiles(background.Document);
                    }
                    break;
                case global::Telegram.Td.Api.Backgrounds backgrounds:
                    foreach (var item in backgrounds.BackgroundsValue)
                    {
                        ProcessFiles(item);
                    }
                    break;
                case global::Telegram.Td.Api.BasicGroupFullInfo basicGroupFullInfo:
                    if (basicGroupFullInfo.Photo != null)
                    {
                        ProcessFiles(basicGroupFullInfo.Photo);
                    }
                    break;
                case global::Telegram.Td.Api.BotInfo botInfo:
                    if (botInfo.Animation != null)
                    {
                        ProcessFiles(botInfo.Animation);
                    }
                    if (botInfo.Photo != null)
                    {
                        ProcessFiles(botInfo.Photo);
                    }
                    break;
                case global::Telegram.Td.Api.BotMediaPreview botMediaPreview:
                    if (botMediaPreview.Content != null)
                    {
                        ProcessFiles(botMediaPreview.Content);
                    }
                    break;
                case global::Telegram.Td.Api.BotMediaPreviewInfo botMediaPreviewInfo:
                    foreach (var item in botMediaPreviewInfo.Previews)
                    {
                        ProcessFiles(item);
                    }
                    break;
                case global::Telegram.Td.Api.BotMediaPreviews botMediaPreviews:
                    foreach (var item in botMediaPreviews.Previews)
                    {
                        ProcessFiles(item);
                    }
                    break;
                case global::Telegram.Td.Api.BotWriteAccessAllowReasonLaunchedWebApp botWriteAccessAllowReasonLaunchedWebApp:
                    if (botWriteAccessAllowReasonLaunchedWebApp.WebApp != null)
                    {
                        ProcessFiles(botWriteAccessAllowReasonLaunchedWebApp.WebApp);
                    }
                    break;
                case global::Telegram.Td.Api.BusinessFeaturePromotionAnimation businessFeaturePromotionAnimation:
                    if (businessFeaturePromotionAnimation.Animation != null)
                    {
                        ProcessFiles(businessFeaturePromotionAnimation.Animation);
                    }
                    break;
                case global::Telegram.Td.Api.BusinessInfo businessInfo:
                    if (businessInfo.StartPage != null)
                    {
                        ProcessFiles(businessInfo.StartPage);
                    }
                    break;
                case global::Telegram.Td.Api.BusinessMessage businessMessage:
                    if (businessMessage.Message != null)
                    {
                        ProcessFiles(businessMessage.Message);
                    }
                    if (businessMessage.ReplyToMessage != null)
                    {
                        ProcessFiles(businessMessage.ReplyToMessage);
                    }
                    break;
                case global::Telegram.Td.Api.BusinessMessages businessMessages:
                    foreach (var item in businessMessages.Messages)
                    {
                        ProcessFiles(item);
                    }
                    break;
                case global::Telegram.Td.Api.BusinessStartPage businessStartPage:
                    if (businessStartPage.Sticker != null)
                    {
                        ProcessFiles(businessStartPage.Sticker);
                    }
                    break;
                case global::Telegram.Td.Api.Chat chat:
                    if (chat.Background != null)
                    {
                        ProcessFiles(chat.Background);
                    }
                    if (chat.LastMessage != null)
                    {
                        ProcessFiles(chat.LastMessage);
                    }
                    if (chat.Photo != null)
                    {
                        ProcessFiles(chat.Photo);
                    }
                    if (chat.Theme != null)
                    {
                        ProcessFiles(chat.Theme);
                    }
                    break;
                case global::Telegram.Td.Api.ChatBackground chatBackground:
                    if (chatBackground.Background != null)
                    {
                        ProcessFiles(chatBackground.Background);
                    }
                    break;
                case global::Telegram.Td.Api.ChatEvent chatEvent:
                    if (chatEvent.Action != null)
                    {
                        ProcessFiles(chatEvent.Action);
                    }
                    break;
                case global::Telegram.Td.Api.ChatEventBackgroundChanged chatEventBackgroundChanged:
                    if (chatEventBackgroundChanged.NewBackground != null)
                    {
                        ProcessFiles(chatEventBackgroundChanged.NewBackground);
                    }
                    if (chatEventBackgroundChanged.OldBackground != null)
                    {
                        ProcessFiles(chatEventBackgroundChanged.OldBackground);
                    }
                    break;
                case global::Telegram.Td.Api.ChatEventMessageDeleted chatEventMessageDeleted:
                    if (chatEventMessageDeleted.Message != null)
                    {
                        ProcessFiles(chatEventMessageDeleted.Message);
                    }
                    break;
                case global::Telegram.Td.Api.ChatEventMessageEdited chatEventMessageEdited:
                    if (chatEventMessageEdited.NewMessage != null)
                    {
                        ProcessFiles(chatEventMessageEdited.NewMessage);
                    }
                    if (chatEventMessageEdited.OldMessage != null)
                    {
                        ProcessFiles(chatEventMessageEdited.OldMessage);
                    }
                    break;
                case global::Telegram.Td.Api.ChatEventMessagePinned chatEventMessagePinned:
                    if (chatEventMessagePinned.Message != null)
                    {
                        ProcessFiles(chatEventMessagePinned.Message);
                    }
                    break;
                case global::Telegram.Td.Api.ChatEventMessageUnpinned chatEventMessageUnpinned:
                    if (chatEventMessageUnpinned.Message != null)
                    {
                        ProcessFiles(chatEventMessageUnpinned.Message);
                    }
                    break;
                case global::Telegram.Td.Api.ChatEventPhotoChanged chatEventPhotoChanged:
                    if (chatEventPhotoChanged.NewPhoto != null)
                    {
                        ProcessFiles(chatEventPhotoChanged.NewPhoto);
                    }
                    if (chatEventPhotoChanged.OldPhoto != null)
                    {
                        ProcessFiles(chatEventPhotoChanged.OldPhoto);
                    }
                    break;
                case global::Telegram.Td.Api.ChatEventPollStopped chatEventPollStopped:
                    if (chatEventPollStopped.Message != null)
                    {
                        ProcessFiles(chatEventPollStopped.Message);
                    }
                    break;
                case global::Telegram.Td.Api.ChatEvents chatEvents:
                    foreach (var item in chatEvents.Events)
                    {
                        ProcessFiles(item);
                    }
                    break;
                case global::Telegram.Td.Api.ChatInviteLinkInfo chatInviteLinkInfo:
                    if (chatInviteLinkInfo.Photo != null)
                    {
                        ProcessFiles(chatInviteLinkInfo.Photo);
                    }
                    break;
                case global::Telegram.Td.Api.ChatPhoto chatPhoto:
                    if (chatPhoto.Animation != null)
                    {
                        ProcessFiles(chatPhoto.Animation);
                    }
                    foreach (var item in chatPhoto.Sizes)
                    {
                        ProcessFiles(item);
                    }
                    if (chatPhoto.SmallAnimation != null)
                    {
                        ProcessFiles(chatPhoto.SmallAnimation);
                    }
                    break;
                case global::Telegram.Td.Api.ChatPhotoInfo chatPhotoInfo:
                    if (chatPhotoInfo.Big != null)
                    {
                        chatPhotoInfo.Big = ProcessFile(chatPhotoInfo.Big);
                    }
                    if (chatPhotoInfo.Small != null)
                    {
                        chatPhotoInfo.Small = ProcessFile(chatPhotoInfo.Small);
                    }
                    break;
                case global::Telegram.Td.Api.ChatPhotos chatPhotos:
                    foreach (var item in chatPhotos.Photos)
                    {
                        ProcessFiles(item);
                    }
                    break;
                case global::Telegram.Td.Api.ChatThemeGift chatThemeGift:
                    if (chatThemeGift.GiftTheme != null)
                    {
                        ProcessFiles(chatThemeGift.GiftTheme);
                    }
                    break;
                case global::Telegram.Td.Api.DatedFile datedFile:
                    if (datedFile.File != null)
                    {
                        datedFile.File = ProcessFile(datedFile.File);
                    }
                    break;
                case global::Telegram.Td.Api.DiceStickersRegular diceStickersRegular:
                    if (diceStickersRegular.Sticker != null)
                    {
                        ProcessFiles(diceStickersRegular.Sticker);
                    }
                    break;
                case global::Telegram.Td.Api.DiceStickersSlotMachine diceStickersSlotMachine:
                    if (diceStickersSlotMachine.Background != null)
                    {
                        ProcessFiles(diceStickersSlotMachine.Background);
                    }
                    if (diceStickersSlotMachine.CenterReel != null)
                    {
                        ProcessFiles(diceStickersSlotMachine.CenterReel);
                    }
                    if (diceStickersSlotMachine.LeftReel != null)
                    {
                        ProcessFiles(diceStickersSlotMachine.LeftReel);
                    }
                    if (diceStickersSlotMachine.Lever != null)
                    {
                        ProcessFiles(diceStickersSlotMachine.Lever);
                    }
                    if (diceStickersSlotMachine.RightReel != null)
                    {
                        ProcessFiles(diceStickersSlotMachine.RightReel);
                    }
                    break;
                case global::Telegram.Td.Api.DirectMessagesChatTopic directMessagesChatTopic:
                    if (directMessagesChatTopic.LastMessage != null)
                    {
                        ProcessFiles(directMessagesChatTopic.LastMessage);
                    }
                    break;
                case global::Telegram.Td.Api.Document document:
                    if (document.DocumentValue != null)
                    {
                        document.DocumentValue = ProcessFile(document.DocumentValue);
                    }
                    if (document.Thumbnail != null)
                    {
                        ProcessFiles(document.Thumbnail);
                    }
                    break;
                case global::Telegram.Td.Api.EmojiCategories emojiCategories:
                    foreach (var item in emojiCategories.Categories)
                    {
                        ProcessFiles(item);
                    }
                    break;
                case global::Telegram.Td.Api.EmojiCategory emojiCategory:
                    if (emojiCategory.Icon != null)
                    {
                        ProcessFiles(emojiCategory.Icon);
                    }
                    break;
                case global::Telegram.Td.Api.EmojiChatTheme emojiChatTheme:
                    if (emojiChatTheme.DarkSettings != null)
                    {
                        ProcessFiles(emojiChatTheme.DarkSettings);
                    }
                    if (emojiChatTheme.LightSettings != null)
                    {
                        ProcessFiles(emojiChatTheme.LightSettings);
                    }
                    break;
                case global::Telegram.Td.Api.EmojiReaction emojiReaction:
                    if (emojiReaction.ActivateAnimation != null)
                    {
                        ProcessFiles(emojiReaction.ActivateAnimation);
                    }
                    if (emojiReaction.AppearAnimation != null)
                    {
                        ProcessFiles(emojiReaction.AppearAnimation);
                    }
                    if (emojiReaction.AroundAnimation != null)
                    {
                        ProcessFiles(emojiReaction.AroundAnimation);
                    }
                    if (emojiReaction.CenterAnimation != null)
                    {
                        ProcessFiles(emojiReaction.CenterAnimation);
                    }
                    if (emojiReaction.EffectAnimation != null)
                    {
                        ProcessFiles(emojiReaction.EffectAnimation);
                    }
                    if (emojiReaction.SelectAnimation != null)
                    {
                        ProcessFiles(emojiReaction.SelectAnimation);
                    }
                    if (emojiReaction.StaticIcon != null)
                    {
                        ProcessFiles(emojiReaction.StaticIcon);
                    }
                    break;
                case global::Telegram.Td.Api.EncryptedPassportElement encryptedPassportElement:
                    foreach (var item in encryptedPassportElement.Files)
                    {
                        ProcessFiles(item);
                    }
                    if (encryptedPassportElement.FrontSide != null)
                    {
                        ProcessFiles(encryptedPassportElement.FrontSide);
                    }
                    if (encryptedPassportElement.ReverseSide != null)
                    {
                        ProcessFiles(encryptedPassportElement.ReverseSide);
                    }
                    if (encryptedPassportElement.Selfie != null)
                    {
                        ProcessFiles(encryptedPassportElement.Selfie);
                    }
                    foreach (var item in encryptedPassportElement.Translation)
                    {
                        ProcessFiles(item);
                    }
                    break;
                case global::Telegram.Td.Api.FileDownload fileDownload:
                    if (fileDownload.Message != null)
                    {
                        ProcessFiles(fileDownload.Message);
                    }
                    break;
                case global::Telegram.Td.Api.ForumTopic forumTopic:
                    if (forumTopic.LastMessage != null)
                    {
                        ProcessFiles(forumTopic.LastMessage);
                    }
                    break;
                case global::Telegram.Td.Api.ForumTopics forumTopics:
                    foreach (var item in forumTopics.Topics)
                    {
                        ProcessFiles(item);
                    }
                    break;
                case global::Telegram.Td.Api.FoundChatMessages foundChatMessages:
                    foreach (var item in foundChatMessages.Messages)
                    {
                        ProcessFiles(item);
                    }
                    break;
                case global::Telegram.Td.Api.FoundFileDownloads foundFileDownloads:
                    foreach (var item in foundFileDownloads.Files)
                    {
                        ProcessFiles(item);
                    }
                    break;
                case global::Telegram.Td.Api.FoundMessages foundMessages:
                    foreach (var item in foundMessages.Messages)
                    {
                        ProcessFiles(item);
                    }
                    break;
                case global::Telegram.Td.Api.FoundPublicPosts foundPublicPosts:
                    foreach (var item in foundPublicPosts.Messages)
                    {
                        ProcessFiles(item);
                    }
                    break;
                case global::Telegram.Td.Api.FoundStories foundStories:
                    foreach (var item in foundStories.Stories)
                    {
                        ProcessFiles(item);
                    }
                    break;
                case global::Telegram.Td.Api.FoundWebApp foundWebApp:
                    if (foundWebApp.WebApp != null)
                    {
                        ProcessFiles(foundWebApp.WebApp);
                    }
                    break;
                case global::Telegram.Td.Api.Game game:
                    if (game.Animation != null)
                    {
                        ProcessFiles(game.Animation);
                    }
                    if (game.Photo != null)
                    {
                        ProcessFiles(game.Photo);
                    }
                    break;
                case global::Telegram.Td.Api.Gift gift:
                    if (gift.Sticker != null)
                    {
                        ProcessFiles(gift.Sticker);
                    }
                    break;
                case global::Telegram.Td.Api.GiftChatTheme giftChatTheme:
                    if (giftChatTheme.DarkSettings != null)
                    {
                        ProcessFiles(giftChatTheme.DarkSettings);
                    }
                    if (giftChatTheme.Gift != null)
                    {
                        ProcessFiles(giftChatTheme.Gift);
                    }
                    if (giftChatTheme.LightSettings != null)
                    {
                        ProcessFiles(giftChatTheme.LightSettings);
                    }
                    break;
                case global::Telegram.Td.Api.GiftChatThemes giftChatThemes:
                    foreach (var item in giftChatThemes.Themes)
                    {
                        ProcessFiles(item);
                    }
                    break;
                case global::Telegram.Td.Api.GiftCollection giftCollection:
                    if (giftCollection.Icon != null)
                    {
                        ProcessFiles(giftCollection.Icon);
                    }
                    break;
                case global::Telegram.Td.Api.GiftCollections giftCollections:
                    foreach (var item in giftCollections.Collections)
                    {
                        ProcessFiles(item);
                    }
                    break;
                case global::Telegram.Td.Api.GiftForResale giftForResale:
                    if (giftForResale.Gift != null)
                    {
                        ProcessFiles(giftForResale.Gift);
                    }
                    break;
                case global::Telegram.Td.Api.GiftsForResale giftsForResale:
                    foreach (var item in giftsForResale.Gifts)
                    {
                        ProcessFiles(item);
                    }
                    foreach (var item in giftsForResale.Models)
                    {
                        ProcessFiles(item);
                    }
                    foreach (var item in giftsForResale.Symbols)
                    {
                        ProcessFiles(item);
                    }
                    break;
                case global::Telegram.Td.Api.GiftUpgradePreview giftUpgradePreview:
                    foreach (var item in giftUpgradePreview.Models)
                    {
                        ProcessFiles(item);
                    }
                    foreach (var item in giftUpgradePreview.Symbols)
                    {
                        ProcessFiles(item);
                    }
                    break;
                case global::Telegram.Td.Api.IdentityDocument identityDocument:
                    if (identityDocument.FrontSide != null)
                    {
                        ProcessFiles(identityDocument.FrontSide);
                    }
                    if (identityDocument.ReverseSide != null)
                    {
                        ProcessFiles(identityDocument.ReverseSide);
                    }
                    if (identityDocument.Selfie != null)
                    {
                        ProcessFiles(identityDocument.Selfie);
                    }
                    foreach (var item in identityDocument.Translation)
                    {
                        ProcessFiles(item);
                    }
                    break;
                case global::Telegram.Td.Api.InlineQueryResultAnimation inlineQueryResultAnimation:
                    if (inlineQueryResultAnimation.Animation != null)
                    {
                        ProcessFiles(inlineQueryResultAnimation.Animation);
                    }
                    break;
                case global::Telegram.Td.Api.InlineQueryResultArticle inlineQueryResultArticle:
                    if (inlineQueryResultArticle.Thumbnail != null)
                    {
                        ProcessFiles(inlineQueryResultArticle.Thumbnail);
                    }
                    break;
                case global::Telegram.Td.Api.InlineQueryResultAudio inlineQueryResultAudio:
                    if (inlineQueryResultAudio.Audio != null)
                    {
                        ProcessFiles(inlineQueryResultAudio.Audio);
                    }
                    break;
                case global::Telegram.Td.Api.InlineQueryResultContact inlineQueryResultContact:
                    if (inlineQueryResultContact.Thumbnail != null)
                    {
                        ProcessFiles(inlineQueryResultContact.Thumbnail);
                    }
                    break;
                case global::Telegram.Td.Api.InlineQueryResultDocument inlineQueryResultDocument:
                    if (inlineQueryResultDocument.Document != null)
                    {
                        ProcessFiles(inlineQueryResultDocument.Document);
                    }
                    break;
                case global::Telegram.Td.Api.InlineQueryResultGame inlineQueryResultGame:
                    if (inlineQueryResultGame.Game != null)
                    {
                        ProcessFiles(inlineQueryResultGame.Game);
                    }
                    break;
                case global::Telegram.Td.Api.InlineQueryResultLocation inlineQueryResultLocation:
                    if (inlineQueryResultLocation.Thumbnail != null)
                    {
                        ProcessFiles(inlineQueryResultLocation.Thumbnail);
                    }
                    break;
                case global::Telegram.Td.Api.InlineQueryResultPhoto inlineQueryResultPhoto:
                    if (inlineQueryResultPhoto.Photo != null)
                    {
                        ProcessFiles(inlineQueryResultPhoto.Photo);
                    }
                    break;
                case global::Telegram.Td.Api.InlineQueryResults inlineQueryResults:
                    foreach (var item in inlineQueryResults.Results)
                    {
                        ProcessFiles(item);
                    }
                    break;
                case global::Telegram.Td.Api.InlineQueryResultSticker inlineQueryResultSticker:
                    if (inlineQueryResultSticker.Sticker != null)
                    {
                        ProcessFiles(inlineQueryResultSticker.Sticker);
                    }
                    break;
                case global::Telegram.Td.Api.InlineQueryResultVenue inlineQueryResultVenue:
                    if (inlineQueryResultVenue.Thumbnail != null)
                    {
                        ProcessFiles(inlineQueryResultVenue.Thumbnail);
                    }
                    break;
                case global::Telegram.Td.Api.InlineQueryResultVideo inlineQueryResultVideo:
                    if (inlineQueryResultVideo.Video != null)
                    {
                        ProcessFiles(inlineQueryResultVideo.Video);
                    }
                    break;
                case global::Telegram.Td.Api.InlineQueryResultVoiceNote inlineQueryResultVoiceNote:
                    if (inlineQueryResultVoiceNote.VoiceNote != null)
                    {
                        ProcessFiles(inlineQueryResultVoiceNote.VoiceNote);
                    }
                    break;
                case global::Telegram.Td.Api.LinkPreview linkPreview:
                    if (linkPreview.Type != null)
                    {
                        ProcessFiles(linkPreview.Type);
                    }
                    break;
                case global::Telegram.Td.Api.LinkPreviewAlbumMediaPhoto linkPreviewAlbumMediaPhoto:
                    if (linkPreviewAlbumMediaPhoto.Photo != null)
                    {
                        ProcessFiles(linkPreviewAlbumMediaPhoto.Photo);
                    }
                    break;
                case global::Telegram.Td.Api.LinkPreviewAlbumMediaVideo linkPreviewAlbumMediaVideo:
                    if (linkPreviewAlbumMediaVideo.Video != null)
                    {
                        ProcessFiles(linkPreviewAlbumMediaVideo.Video);
                    }
                    break;
                case global::Telegram.Td.Api.LinkPreviewTypeAlbum linkPreviewTypeAlbum:
                    foreach (var item in linkPreviewTypeAlbum.Media)
                    {
                        ProcessFiles(item);
                    }
                    break;
                case global::Telegram.Td.Api.LinkPreviewTypeAnimation linkPreviewTypeAnimation:
                    if (linkPreviewTypeAnimation.Animation != null)
                    {
                        ProcessFiles(linkPreviewTypeAnimation.Animation);
                    }
                    break;
                case global::Telegram.Td.Api.LinkPreviewTypeApp linkPreviewTypeApp:
                    if (linkPreviewTypeApp.Photo != null)
                    {
                        ProcessFiles(linkPreviewTypeApp.Photo);
                    }
                    break;
                case global::Telegram.Td.Api.LinkPreviewTypeArticle linkPreviewTypeArticle:
                    if (linkPreviewTypeArticle.Photo != null)
                    {
                        ProcessFiles(linkPreviewTypeArticle.Photo);
                    }
                    break;
                case global::Telegram.Td.Api.LinkPreviewTypeAudio linkPreviewTypeAudio:
                    if (linkPreviewTypeAudio.Audio != null)
                    {
                        ProcessFiles(linkPreviewTypeAudio.Audio);
                    }
                    break;
                case global::Telegram.Td.Api.LinkPreviewTypeBackground linkPreviewTypeBackground:
                    if (linkPreviewTypeBackground.Document != null)
                    {
                        ProcessFiles(linkPreviewTypeBackground.Document);
                    }
                    break;
                case global::Telegram.Td.Api.LinkPreviewTypeChannelBoost linkPreviewTypeChannelBoost:
                    if (linkPreviewTypeChannelBoost.Photo != null)
                    {
                        ProcessFiles(linkPreviewTypeChannelBoost.Photo);
                    }
                    break;
                case global::Telegram.Td.Api.LinkPreviewTypeChat linkPreviewTypeChat:
                    if (linkPreviewTypeChat.Photo != null)
                    {
                        ProcessFiles(linkPreviewTypeChat.Photo);
                    }
                    break;
                case global::Telegram.Td.Api.LinkPreviewTypeDirectMessagesChat linkPreviewTypeDirectMessagesChat:
                    if (linkPreviewTypeDirectMessagesChat.Photo != null)
                    {
                        ProcessFiles(linkPreviewTypeDirectMessagesChat.Photo);
                    }
                    break;
                case global::Telegram.Td.Api.LinkPreviewTypeDocument linkPreviewTypeDocument:
                    if (linkPreviewTypeDocument.Document != null)
                    {
                        ProcessFiles(linkPreviewTypeDocument.Document);
                    }
                    break;
                case global::Telegram.Td.Api.LinkPreviewTypeEmbeddedAnimationPlayer linkPreviewTypeEmbeddedAnimationPlayer:
                    if (linkPreviewTypeEmbeddedAnimationPlayer.Thumbnail != null)
                    {
                        ProcessFiles(linkPreviewTypeEmbeddedAnimationPlayer.Thumbnail);
                    }
                    break;
                case global::Telegram.Td.Api.LinkPreviewTypeEmbeddedAudioPlayer linkPreviewTypeEmbeddedAudioPlayer:
                    if (linkPreviewTypeEmbeddedAudioPlayer.Thumbnail != null)
                    {
                        ProcessFiles(linkPreviewTypeEmbeddedAudioPlayer.Thumbnail);
                    }
                    break;
                case global::Telegram.Td.Api.LinkPreviewTypeEmbeddedVideoPlayer linkPreviewTypeEmbeddedVideoPlayer:
                    if (linkPreviewTypeEmbeddedVideoPlayer.Thumbnail != null)
                    {
                        ProcessFiles(linkPreviewTypeEmbeddedVideoPlayer.Thumbnail);
                    }
                    break;
                case global::Telegram.Td.Api.LinkPreviewTypeGiftCollection linkPreviewTypeGiftCollection:
                    foreach (var item in linkPreviewTypeGiftCollection.Icons)
                    {
                        ProcessFiles(item);
                    }
                    break;
                case global::Telegram.Td.Api.LinkPreviewTypePhoto linkPreviewTypePhoto:
                    if (linkPreviewTypePhoto.Photo != null)
                    {
                        ProcessFiles(linkPreviewTypePhoto.Photo);
                    }
                    break;
                case global::Telegram.Td.Api.LinkPreviewTypeSticker linkPreviewTypeSticker:
                    if (linkPreviewTypeSticker.Sticker != null)
                    {
                        ProcessFiles(linkPreviewTypeSticker.Sticker);
                    }
                    break;
                case global::Telegram.Td.Api.LinkPreviewTypeStickerSet linkPreviewTypeStickerSet:
                    foreach (var item in linkPreviewTypeStickerSet.Stickers)
                    {
                        ProcessFiles(item);
                    }
                    break;
                case global::Telegram.Td.Api.LinkPreviewTypeStoryAlbum linkPreviewTypeStoryAlbum:
                    if (linkPreviewTypeStoryAlbum.PhotoIcon != null)
                    {
                        ProcessFiles(linkPreviewTypeStoryAlbum.PhotoIcon);
                    }
                    if (linkPreviewTypeStoryAlbum.VideoIcon != null)
                    {
                        ProcessFiles(linkPreviewTypeStoryAlbum.VideoIcon);
                    }
                    break;
                case global::Telegram.Td.Api.LinkPreviewTypeSupergroupBoost linkPreviewTypeSupergroupBoost:
                    if (linkPreviewTypeSupergroupBoost.Photo != null)
                    {
                        ProcessFiles(linkPreviewTypeSupergroupBoost.Photo);
                    }
                    break;
                case global::Telegram.Td.Api.LinkPreviewTypeTheme linkPreviewTypeTheme:
                    foreach (var item in linkPreviewTypeTheme.Documents)
                    {
                        ProcessFiles(item);
                    }
                    if (linkPreviewTypeTheme.Settings != null)
                    {
                        ProcessFiles(linkPreviewTypeTheme.Settings);
                    }
                    break;
                case global::Telegram.Td.Api.LinkPreviewTypeUpgradedGift linkPreviewTypeUpgradedGift:
                    if (linkPreviewTypeUpgradedGift.Gift != null)
                    {
                        ProcessFiles(linkPreviewTypeUpgradedGift.Gift);
                    }
                    break;
                case global::Telegram.Td.Api.LinkPreviewTypeUser linkPreviewTypeUser:
                    if (linkPreviewTypeUser.Photo != null)
                    {
                        ProcessFiles(linkPreviewTypeUser.Photo);
                    }
                    break;
                case global::Telegram.Td.Api.LinkPreviewTypeVideo linkPreviewTypeVideo:
                    if (linkPreviewTypeVideo.Cover != null)
                    {
                        ProcessFiles(linkPreviewTypeVideo.Cover);
                    }
                    if (linkPreviewTypeVideo.Video != null)
                    {
                        ProcessFiles(linkPreviewTypeVideo.Video);
                    }
                    break;
                case global::Telegram.Td.Api.LinkPreviewTypeVideoChat linkPreviewTypeVideoChat:
                    if (linkPreviewTypeVideoChat.Photo != null)
                    {
                        ProcessFiles(linkPreviewTypeVideoChat.Photo);
                    }
                    break;
                case global::Telegram.Td.Api.LinkPreviewTypeVideoNote linkPreviewTypeVideoNote:
                    if (linkPreviewTypeVideoNote.VideoNote != null)
                    {
                        ProcessFiles(linkPreviewTypeVideoNote.VideoNote);
                    }
                    break;
                case global::Telegram.Td.Api.LinkPreviewTypeVoiceNote linkPreviewTypeVoiceNote:
                    if (linkPreviewTypeVoiceNote.VoiceNote != null)
                    {
                        ProcessFiles(linkPreviewTypeVoiceNote.VoiceNote);
                    }
                    break;
                case global::Telegram.Td.Api.LinkPreviewTypeWebApp linkPreviewTypeWebApp:
                    if (linkPreviewTypeWebApp.Photo != null)
                    {
                        ProcessFiles(linkPreviewTypeWebApp.Photo);
                    }
                    break;
                case global::Telegram.Td.Api.Message message:
                    if (message.Content != null)
                    {
                        ProcessFiles(message.Content);
                    }
                    if (message.ReplyTo != null)
                    {
                        ProcessFiles(message.ReplyTo);
                    }
                    break;
                case global::Telegram.Td.Api.MessageAnimatedEmoji messageAnimatedEmoji:
                    if (messageAnimatedEmoji.AnimatedEmoji != null)
                    {
                        ProcessFiles(messageAnimatedEmoji.AnimatedEmoji);
                    }
                    break;
                case global::Telegram.Td.Api.MessageAnimation messageAnimation:
                    if (messageAnimation.Animation != null)
                    {
                        ProcessFiles(messageAnimation.Animation);
                    }
                    break;
                case global::Telegram.Td.Api.MessageAudio messageAudio:
                    if (messageAudio.Audio != null)
                    {
                        ProcessFiles(messageAudio.Audio);
                    }
                    break;
                case global::Telegram.Td.Api.MessageBotWriteAccessAllowed messageBotWriteAccessAllowed:
                    if (messageBotWriteAccessAllowed.Reason != null)
                    {
                        ProcessFiles(messageBotWriteAccessAllowed.Reason);
                    }
                    break;
                case global::Telegram.Td.Api.MessageCalendar messageCalendar:
                    foreach (var item in messageCalendar.Days)
                    {
                        ProcessFiles(item);
                    }
                    break;
                case global::Telegram.Td.Api.MessageCalendarDay messageCalendarDay:
                    if (messageCalendarDay.Message != null)
                    {
                        ProcessFiles(messageCalendarDay.Message);
                    }
                    break;
                case global::Telegram.Td.Api.MessageChatChangePhoto messageChatChangePhoto:
                    if (messageChatChangePhoto.Photo != null)
                    {
                        ProcessFiles(messageChatChangePhoto.Photo);
                    }
                    break;
                case global::Telegram.Td.Api.MessageChatSetBackground messageChatSetBackground:
                    if (messageChatSetBackground.Background != null)
                    {
                        ProcessFiles(messageChatSetBackground.Background);
                    }
                    break;
                case global::Telegram.Td.Api.MessageChatSetTheme messageChatSetTheme:
                    if (messageChatSetTheme.Theme != null)
                    {
                        ProcessFiles(messageChatSetTheme.Theme);
                    }
                    break;
                case global::Telegram.Td.Api.MessageChatShared messageChatShared:
                    if (messageChatShared.Chat != null)
                    {
                        ProcessFiles(messageChatShared.Chat);
                    }
                    break;
                case global::Telegram.Td.Api.MessageDice messageDice:
                    if (messageDice.FinalState != null)
                    {
                        ProcessFiles(messageDice.FinalState);
                    }
                    if (messageDice.InitialState != null)
                    {
                        ProcessFiles(messageDice.InitialState);
                    }
                    break;
                case global::Telegram.Td.Api.MessageDocument messageDocument:
                    if (messageDocument.Document != null)
                    {
                        ProcessFiles(messageDocument.Document);
                    }
                    break;
                case global::Telegram.Td.Api.MessageEffect messageEffect:
                    if (messageEffect.StaticIcon != null)
                    {
                        ProcessFiles(messageEffect.StaticIcon);
                    }
                    if (messageEffect.Type != null)
                    {
                        ProcessFiles(messageEffect.Type);
                    }
                    break;
                case global::Telegram.Td.Api.MessageEffectTypeEmojiReaction messageEffectTypeEmojiReaction:
                    if (messageEffectTypeEmojiReaction.EffectAnimation != null)
                    {
                        ProcessFiles(messageEffectTypeEmojiReaction.EffectAnimation);
                    }
                    if (messageEffectTypeEmojiReaction.SelectAnimation != null)
                    {
                        ProcessFiles(messageEffectTypeEmojiReaction.SelectAnimation);
                    }
                    break;
                case global::Telegram.Td.Api.MessageEffectTypePremiumSticker messageEffectTypePremiumSticker:
                    if (messageEffectTypePremiumSticker.Sticker != null)
                    {
                        ProcessFiles(messageEffectTypePremiumSticker.Sticker);
                    }
                    break;
                case global::Telegram.Td.Api.MessageGame messageGame:
                    if (messageGame.Game != null)
                    {
                        ProcessFiles(messageGame.Game);
                    }
                    break;
                case global::Telegram.Td.Api.MessageGift messageGift:
                    if (messageGift.Gift != null)
                    {
                        ProcessFiles(messageGift.Gift);
                    }
                    break;
                case global::Telegram.Td.Api.MessageGiftedPremium messageGiftedPremium:
                    if (messageGiftedPremium.Sticker != null)
                    {
                        ProcessFiles(messageGiftedPremium.Sticker);
                    }
                    break;
                case global::Telegram.Td.Api.MessageGiftedStars messageGiftedStars:
                    if (messageGiftedStars.Sticker != null)
                    {
                        ProcessFiles(messageGiftedStars.Sticker);
                    }
                    break;
                case global::Telegram.Td.Api.MessageGiftedTon messageGiftedTon:
                    if (messageGiftedTon.Sticker != null)
                    {
                        ProcessFiles(messageGiftedTon.Sticker);
                    }
                    break;
                case global::Telegram.Td.Api.MessageGiveaway messageGiveaway:
                    if (messageGiveaway.Sticker != null)
                    {
                        ProcessFiles(messageGiveaway.Sticker);
                    }
                    break;
                case global::Telegram.Td.Api.MessageGiveawayPrizeStars messageGiveawayPrizeStars:
                    if (messageGiveawayPrizeStars.Sticker != null)
                    {
                        ProcessFiles(messageGiveawayPrizeStars.Sticker);
                    }
                    break;
                case global::Telegram.Td.Api.MessageInvoice messageInvoice:
                    if (messageInvoice.PaidMedia != null)
                    {
                        ProcessFiles(messageInvoice.PaidMedia);
                    }
                    if (messageInvoice.ProductInfo != null)
                    {
                        ProcessFiles(messageInvoice.ProductInfo);
                    }
                    break;
                case global::Telegram.Td.Api.MessageLinkInfo messageLinkInfo:
                    if (messageLinkInfo.Message != null)
                    {
                        ProcessFiles(messageLinkInfo.Message);
                    }
                    break;
                case global::Telegram.Td.Api.MessagePaidMedia messagePaidMedia:
                    foreach (var item in messagePaidMedia.Media)
                    {
                        ProcessFiles(item);
                    }
                    break;
                case global::Telegram.Td.Api.MessagePassportDataReceived messagePassportDataReceived:
                    foreach (var item in messagePassportDataReceived.Elements)
                    {
                        ProcessFiles(item);
                    }
                    break;
                case global::Telegram.Td.Api.MessagePhoto messagePhoto:
                    if (messagePhoto.Photo != null)
                    {
                        ProcessFiles(messagePhoto.Photo);
                    }
                    break;
                case global::Telegram.Td.Api.MessagePremiumGiftCode messagePremiumGiftCode:
                    if (messagePremiumGiftCode.Sticker != null)
                    {
                        ProcessFiles(messagePremiumGiftCode.Sticker);
                    }
                    break;
                case global::Telegram.Td.Api.MessageRefundedUpgradedGift messageRefundedUpgradedGift:
                    if (messageRefundedUpgradedGift.Gift != null)
                    {
                        ProcessFiles(messageRefundedUpgradedGift.Gift);
                    }
                    break;
                case global::Telegram.Td.Api.MessageReplyToMessage messageReplyToMessage:
                    if (messageReplyToMessage.Content != null)
                    {
                        ProcessFiles(messageReplyToMessage.Content);
                    }
                    break;
                case global::Telegram.Td.Api.Messages messages:
                    foreach (var item in messages.MessagesValue)
                    {
                        ProcessFiles(item);
                    }
                    break;
                case global::Telegram.Td.Api.MessageSticker messageSticker:
                    if (messageSticker.Sticker != null)
                    {
                        ProcessFiles(messageSticker.Sticker);
                    }
                    break;
                case global::Telegram.Td.Api.MessageSuggestProfilePhoto messageSuggestProfilePhoto:
                    if (messageSuggestProfilePhoto.Photo != null)
                    {
                        ProcessFiles(messageSuggestProfilePhoto.Photo);
                    }
                    break;
                case global::Telegram.Td.Api.MessageText messageText:
                    if (messageText.LinkPreview != null)
                    {
                        ProcessFiles(messageText.LinkPreview);
                    }
                    break;
                case global::Telegram.Td.Api.MessageThreadInfo messageThreadInfo:
                    foreach (var item in messageThreadInfo.Messages)
                    {
                        ProcessFiles(item);
                    }
                    break;
                case global::Telegram.Td.Api.MessageUpgradedGift messageUpgradedGift:
                    if (messageUpgradedGift.Gift != null)
                    {
                        ProcessFiles(messageUpgradedGift.Gift);
                    }
                    break;
                case global::Telegram.Td.Api.MessageUsersShared messageUsersShared:
                    foreach (var item in messageUsersShared.Users)
                    {
                        ProcessFiles(item);
                    }
                    break;
                case global::Telegram.Td.Api.MessageVideo messageVideo:
                    foreach (var item in messageVideo.AlternativeVideos)
                    {
                        ProcessFiles(item);
                    }
                    if (messageVideo.Cover != null)
                    {
                        ProcessFiles(messageVideo.Cover);
                    }
                    foreach (var item in messageVideo.Storyboards)
                    {
                        ProcessFiles(item);
                    }
                    if (messageVideo.Video != null)
                    {
                        ProcessFiles(messageVideo.Video);
                    }
                    break;
                case global::Telegram.Td.Api.MessageVideoNote messageVideoNote:
                    if (messageVideoNote.VideoNote != null)
                    {
                        ProcessFiles(messageVideoNote.VideoNote);
                    }
                    break;
                case global::Telegram.Td.Api.MessageVoiceNote messageVoiceNote:
                    if (messageVoiceNote.VoiceNote != null)
                    {
                        ProcessFiles(messageVoiceNote.VoiceNote);
                    }
                    break;
                case global::Telegram.Td.Api.Notification notification:
                    if (notification.Type != null)
                    {
                        ProcessFiles(notification.Type);
                    }
                    break;
                case global::Telegram.Td.Api.NotificationGroup notificationGroup:
                    foreach (var item in notificationGroup.Notifications)
                    {
                        ProcessFiles(item);
                    }
                    break;
                case global::Telegram.Td.Api.NotificationSound notificationSound:
                    if (notificationSound.Sound != null)
                    {
                        notificationSound.Sound = ProcessFile(notificationSound.Sound);
                    }
                    break;
                case global::Telegram.Td.Api.NotificationSounds notificationSounds:
                    foreach (var item in notificationSounds.NotificationSoundsValue)
                    {
                        ProcessFiles(item);
                    }
                    break;
                case global::Telegram.Td.Api.NotificationTypeNewMessage notificationTypeNewMessage:
                    if (notificationTypeNewMessage.Message != null)
                    {
                        ProcessFiles(notificationTypeNewMessage.Message);
                    }
                    break;
                case global::Telegram.Td.Api.NotificationTypeNewPushMessage notificationTypeNewPushMessage:
                    if (notificationTypeNewPushMessage.Content != null)
                    {
                        ProcessFiles(notificationTypeNewPushMessage.Content);
                    }
                    break;
                case global::Telegram.Td.Api.PageBlockAnimation pageBlockAnimation:
                    if (pageBlockAnimation.Animation != null)
                    {
                        ProcessFiles(pageBlockAnimation.Animation);
                    }
                    if (pageBlockAnimation.Caption != null)
                    {
                        ProcessFiles(pageBlockAnimation.Caption);
                    }
                    break;
                case global::Telegram.Td.Api.PageBlockAudio pageBlockAudio:
                    if (pageBlockAudio.Audio != null)
                    {
                        ProcessFiles(pageBlockAudio.Audio);
                    }
                    if (pageBlockAudio.Caption != null)
                    {
                        ProcessFiles(pageBlockAudio.Caption);
                    }
                    break;
                case global::Telegram.Td.Api.PageBlockAuthorDate pageBlockAuthorDate:
                    if (pageBlockAuthorDate.Author != null)
                    {
                        ProcessFiles(pageBlockAuthorDate.Author);
                    }
                    break;
                case global::Telegram.Td.Api.PageBlockBlockQuote pageBlockBlockQuote:
                    if (pageBlockBlockQuote.Credit != null)
                    {
                        ProcessFiles(pageBlockBlockQuote.Credit);
                    }
                    if (pageBlockBlockQuote.Text != null)
                    {
                        ProcessFiles(pageBlockBlockQuote.Text);
                    }
                    break;
                case global::Telegram.Td.Api.PageBlockCaption pageBlockCaption:
                    if (pageBlockCaption.Credit != null)
                    {
                        ProcessFiles(pageBlockCaption.Credit);
                    }
                    if (pageBlockCaption.Text != null)
                    {
                        ProcessFiles(pageBlockCaption.Text);
                    }
                    break;
                case global::Telegram.Td.Api.PageBlockChatLink pageBlockChatLink:
                    if (pageBlockChatLink.Photo != null)
                    {
                        ProcessFiles(pageBlockChatLink.Photo);
                    }
                    break;
                case global::Telegram.Td.Api.PageBlockCollage pageBlockCollage:
                    if (pageBlockCollage.Caption != null)
                    {
                        ProcessFiles(pageBlockCollage.Caption);
                    }
                    foreach (var item in pageBlockCollage.PageBlocks)
                    {
                        ProcessFiles(item);
                    }
                    break;
                case global::Telegram.Td.Api.PageBlockCover pageBlockCover:
                    if (pageBlockCover.Cover != null)
                    {
                        ProcessFiles(pageBlockCover.Cover);
                    }
                    break;
                case global::Telegram.Td.Api.PageBlockDetails pageBlockDetails:
                    if (pageBlockDetails.Header != null)
                    {
                        ProcessFiles(pageBlockDetails.Header);
                    }
                    foreach (var item in pageBlockDetails.PageBlocks)
                    {
                        ProcessFiles(item);
                    }
                    break;
                case global::Telegram.Td.Api.PageBlockEmbedded pageBlockEmbedded:
                    if (pageBlockEmbedded.Caption != null)
                    {
                        ProcessFiles(pageBlockEmbedded.Caption);
                    }
                    if (pageBlockEmbedded.PosterPhoto != null)
                    {
                        ProcessFiles(pageBlockEmbedded.PosterPhoto);
                    }
                    break;
                case global::Telegram.Td.Api.PageBlockEmbeddedPost pageBlockEmbeddedPost:
                    if (pageBlockEmbeddedPost.AuthorPhoto != null)
                    {
                        ProcessFiles(pageBlockEmbeddedPost.AuthorPhoto);
                    }
                    if (pageBlockEmbeddedPost.Caption != null)
                    {
                        ProcessFiles(pageBlockEmbeddedPost.Caption);
                    }
                    foreach (var item in pageBlockEmbeddedPost.PageBlocks)
                    {
                        ProcessFiles(item);
                    }
                    break;
                case global::Telegram.Td.Api.PageBlockFooter pageBlockFooter:
                    if (pageBlockFooter.Footer != null)
                    {
                        ProcessFiles(pageBlockFooter.Footer);
                    }
                    break;
                case global::Telegram.Td.Api.PageBlockHeader pageBlockHeader:
                    if (pageBlockHeader.Header != null)
                    {
                        ProcessFiles(pageBlockHeader.Header);
                    }
                    break;
                case global::Telegram.Td.Api.PageBlockKicker pageBlockKicker:
                    if (pageBlockKicker.Kicker != null)
                    {
                        ProcessFiles(pageBlockKicker.Kicker);
                    }
                    break;
                case global::Telegram.Td.Api.PageBlockList pageBlockList:
                    foreach (var item in pageBlockList.Items)
                    {
                        ProcessFiles(item);
                    }
                    break;
                case global::Telegram.Td.Api.PageBlockListItem pageBlockListItem:
                    foreach (var item in pageBlockListItem.PageBlocks)
                    {
                        ProcessFiles(item);
                    }
                    break;
                case global::Telegram.Td.Api.PageBlockMap pageBlockMap:
                    if (pageBlockMap.Caption != null)
                    {
                        ProcessFiles(pageBlockMap.Caption);
                    }
                    break;
                case global::Telegram.Td.Api.PageBlockParagraph pageBlockParagraph:
                    if (pageBlockParagraph.Text != null)
                    {
                        ProcessFiles(pageBlockParagraph.Text);
                    }
                    break;
                case global::Telegram.Td.Api.PageBlockPhoto pageBlockPhoto:
                    if (pageBlockPhoto.Caption != null)
                    {
                        ProcessFiles(pageBlockPhoto.Caption);
                    }
                    if (pageBlockPhoto.Photo != null)
                    {
                        ProcessFiles(pageBlockPhoto.Photo);
                    }
                    break;
                case global::Telegram.Td.Api.PageBlockPreformatted pageBlockPreformatted:
                    if (pageBlockPreformatted.Text != null)
                    {
                        ProcessFiles(pageBlockPreformatted.Text);
                    }
                    break;
                case global::Telegram.Td.Api.PageBlockPullQuote pageBlockPullQuote:
                    if (pageBlockPullQuote.Credit != null)
                    {
                        ProcessFiles(pageBlockPullQuote.Credit);
                    }
                    if (pageBlockPullQuote.Text != null)
                    {
                        ProcessFiles(pageBlockPullQuote.Text);
                    }
                    break;
                case global::Telegram.Td.Api.PageBlockRelatedArticle pageBlockRelatedArticle:
                    if (pageBlockRelatedArticle.Photo != null)
                    {
                        ProcessFiles(pageBlockRelatedArticle.Photo);
                    }
                    break;
                case global::Telegram.Td.Api.PageBlockRelatedArticles pageBlockRelatedArticles:
                    foreach (var item in pageBlockRelatedArticles.Articles)
                    {
                        ProcessFiles(item);
                    }
                    if (pageBlockRelatedArticles.Header != null)
                    {
                        ProcessFiles(pageBlockRelatedArticles.Header);
                    }
                    break;
                case global::Telegram.Td.Api.PageBlockSlideshow pageBlockSlideshow:
                    if (pageBlockSlideshow.Caption != null)
                    {
                        ProcessFiles(pageBlockSlideshow.Caption);
                    }
                    foreach (var item in pageBlockSlideshow.PageBlocks)
                    {
                        ProcessFiles(item);
                    }
                    break;
                case global::Telegram.Td.Api.PageBlockSubheader pageBlockSubheader:
                    if (pageBlockSubheader.Subheader != null)
                    {
                        ProcessFiles(pageBlockSubheader.Subheader);
                    }
                    break;
                case global::Telegram.Td.Api.PageBlockSubtitle pageBlockSubtitle:
                    if (pageBlockSubtitle.Subtitle != null)
                    {
                        ProcessFiles(pageBlockSubtitle.Subtitle);
                    }
                    break;
                case global::Telegram.Td.Api.PageBlockTable pageBlockTable:
                    if (pageBlockTable.Caption != null)
                    {
                        ProcessFiles(pageBlockTable.Caption);
                    }
                    foreach (var item in pageBlockTable.Cells.SelectMany(x => x))
                    {
                        ProcessFiles(item);
                    }
                    break;
                case global::Telegram.Td.Api.PageBlockTableCell pageBlockTableCell:
                    if (pageBlockTableCell.Text != null)
                    {
                        ProcessFiles(pageBlockTableCell.Text);
                    }
                    break;
                case global::Telegram.Td.Api.PageBlockTitle pageBlockTitle:
                    if (pageBlockTitle.Title != null)
                    {
                        ProcessFiles(pageBlockTitle.Title);
                    }
                    break;
                case global::Telegram.Td.Api.PageBlockVideo pageBlockVideo:
                    if (pageBlockVideo.Caption != null)
                    {
                        ProcessFiles(pageBlockVideo.Caption);
                    }
                    if (pageBlockVideo.Video != null)
                    {
                        ProcessFiles(pageBlockVideo.Video);
                    }
                    break;
                case global::Telegram.Td.Api.PageBlockVoiceNote pageBlockVoiceNote:
                    if (pageBlockVoiceNote.Caption != null)
                    {
                        ProcessFiles(pageBlockVoiceNote.Caption);
                    }
                    if (pageBlockVoiceNote.VoiceNote != null)
                    {
                        ProcessFiles(pageBlockVoiceNote.VoiceNote);
                    }
                    break;
                case global::Telegram.Td.Api.PaidMediaPhoto paidMediaPhoto:
                    if (paidMediaPhoto.Photo != null)
                    {
                        ProcessFiles(paidMediaPhoto.Photo);
                    }
                    break;
                case global::Telegram.Td.Api.PaidMediaVideo paidMediaVideo:
                    if (paidMediaVideo.Cover != null)
                    {
                        ProcessFiles(paidMediaVideo.Cover);
                    }
                    if (paidMediaVideo.Video != null)
                    {
                        ProcessFiles(paidMediaVideo.Video);
                    }
                    break;
                case global::Telegram.Td.Api.PassportElementBankStatement passportElementBankStatement:
                    if (passportElementBankStatement.BankStatement != null)
                    {
                        ProcessFiles(passportElementBankStatement.BankStatement);
                    }
                    break;
                case global::Telegram.Td.Api.PassportElementDriverLicense passportElementDriverLicense:
                    if (passportElementDriverLicense.DriverLicense != null)
                    {
                        ProcessFiles(passportElementDriverLicense.DriverLicense);
                    }
                    break;
                case global::Telegram.Td.Api.PassportElementIdentityCard passportElementIdentityCard:
                    if (passportElementIdentityCard.IdentityCard != null)
                    {
                        ProcessFiles(passportElementIdentityCard.IdentityCard);
                    }
                    break;
                case global::Telegram.Td.Api.PassportElementInternalPassport passportElementInternalPassport:
                    if (passportElementInternalPassport.InternalPassport != null)
                    {
                        ProcessFiles(passportElementInternalPassport.InternalPassport);
                    }
                    break;
                case global::Telegram.Td.Api.PassportElementPassport passportElementPassport:
                    if (passportElementPassport.Passport != null)
                    {
                        ProcessFiles(passportElementPassport.Passport);
                    }
                    break;
                case global::Telegram.Td.Api.PassportElementPassportRegistration passportElementPassportRegistration:
                    if (passportElementPassportRegistration.PassportRegistration != null)
                    {
                        ProcessFiles(passportElementPassportRegistration.PassportRegistration);
                    }
                    break;
                case global::Telegram.Td.Api.PassportElementRentalAgreement passportElementRentalAgreement:
                    if (passportElementRentalAgreement.RentalAgreement != null)
                    {
                        ProcessFiles(passportElementRentalAgreement.RentalAgreement);
                    }
                    break;
                case global::Telegram.Td.Api.PassportElements passportElements:
                    foreach (var item in passportElements.Elements)
                    {
                        ProcessFiles(item);
                    }
                    break;
                case global::Telegram.Td.Api.PassportElementsWithErrors passportElementsWithErrors:
                    foreach (var item in passportElementsWithErrors.Elements)
                    {
                        ProcessFiles(item);
                    }
                    break;
                case global::Telegram.Td.Api.PassportElementTemporaryRegistration passportElementTemporaryRegistration:
                    if (passportElementTemporaryRegistration.TemporaryRegistration != null)
                    {
                        ProcessFiles(passportElementTemporaryRegistration.TemporaryRegistration);
                    }
                    break;
                case global::Telegram.Td.Api.PassportElementUtilityBill passportElementUtilityBill:
                    if (passportElementUtilityBill.UtilityBill != null)
                    {
                        ProcessFiles(passportElementUtilityBill.UtilityBill);
                    }
                    break;
                case global::Telegram.Td.Api.PaymentForm paymentForm:
                    if (paymentForm.ProductInfo != null)
                    {
                        ProcessFiles(paymentForm.ProductInfo);
                    }
                    break;
                case global::Telegram.Td.Api.PaymentReceipt paymentReceipt:
                    if (paymentReceipt.ProductInfo != null)
                    {
                        ProcessFiles(paymentReceipt.ProductInfo);
                    }
                    break;
                case global::Telegram.Td.Api.PersonalDocument personalDocument:
                    foreach (var item in personalDocument.Files)
                    {
                        ProcessFiles(item);
                    }
                    foreach (var item in personalDocument.Translation)
                    {
                        ProcessFiles(item);
                    }
                    break;
                case global::Telegram.Td.Api.Photo photo:
                    foreach (var item in photo.Sizes)
                    {
                        ProcessFiles(item);
                    }
                    break;
                case global::Telegram.Td.Api.PhotoSize photoSize:
                    if (photoSize.Photo != null)
                    {
                        photoSize.Photo = ProcessFile(photoSize.Photo);
                    }
                    break;
                case global::Telegram.Td.Api.PremiumFeaturePromotionAnimation premiumFeaturePromotionAnimation:
                    if (premiumFeaturePromotionAnimation.Animation != null)
                    {
                        ProcessFiles(premiumFeaturePromotionAnimation.Animation);
                    }
                    break;
                case global::Telegram.Td.Api.PremiumGiftPaymentOption premiumGiftPaymentOption:
                    if (premiumGiftPaymentOption.Sticker != null)
                    {
                        ProcessFiles(premiumGiftPaymentOption.Sticker);
                    }
                    break;
                case global::Telegram.Td.Api.PremiumGiftPaymentOptions premiumGiftPaymentOptions:
                    foreach (var item in premiumGiftPaymentOptions.Options)
                    {
                        ProcessFiles(item);
                    }
                    break;
                case global::Telegram.Td.Api.PremiumState premiumState:
                    foreach (var item in premiumState.Animations)
                    {
                        ProcessFiles(item);
                    }
                    foreach (var item in premiumState.BusinessAnimations)
                    {
                        ProcessFiles(item);
                    }
                    break;
                case global::Telegram.Td.Api.PreparedInlineMessage preparedInlineMessage:
                    if (preparedInlineMessage.Result != null)
                    {
                        ProcessFiles(preparedInlineMessage.Result);
                    }
                    break;
                case global::Telegram.Td.Api.ProductInfo productInfo:
                    if (productInfo.Photo != null)
                    {
                        ProcessFiles(productInfo.Photo);
                    }
                    break;
                case global::Telegram.Td.Api.ProfilePhoto profilePhoto:
                    if (profilePhoto.Big != null)
                    {
                        profilePhoto.Big = ProcessFile(profilePhoto.Big);
                    }
                    if (profilePhoto.Small != null)
                    {
                        profilePhoto.Small = ProcessFile(profilePhoto.Small);
                    }
                    break;
                case global::Telegram.Td.Api.PublicForwardMessage publicForwardMessage:
                    if (publicForwardMessage.Message != null)
                    {
                        ProcessFiles(publicForwardMessage.Message);
                    }
                    break;
                case global::Telegram.Td.Api.PublicForwards publicForwards:
                    foreach (var item in publicForwards.Forwards)
                    {
                        ProcessFiles(item);
                    }
                    break;
                case global::Telegram.Td.Api.PublicForwardStory publicForwardStory:
                    if (publicForwardStory.Story != null)
                    {
                        ProcessFiles(publicForwardStory.Story);
                    }
                    break;
                case global::Telegram.Td.Api.PushMessageContentAnimation pushMessageContentAnimation:
                    if (pushMessageContentAnimation.Animation != null)
                    {
                        ProcessFiles(pushMessageContentAnimation.Animation);
                    }
                    break;
                case global::Telegram.Td.Api.PushMessageContentAudio pushMessageContentAudio:
                    if (pushMessageContentAudio.Audio != null)
                    {
                        ProcessFiles(pushMessageContentAudio.Audio);
                    }
                    break;
                case global::Telegram.Td.Api.PushMessageContentDocument pushMessageContentDocument:
                    if (pushMessageContentDocument.Document != null)
                    {
                        ProcessFiles(pushMessageContentDocument.Document);
                    }
                    break;
                case global::Telegram.Td.Api.PushMessageContentPhoto pushMessageContentPhoto:
                    if (pushMessageContentPhoto.Photo != null)
                    {
                        ProcessFiles(pushMessageContentPhoto.Photo);
                    }
                    break;
                case global::Telegram.Td.Api.PushMessageContentSticker pushMessageContentSticker:
                    if (pushMessageContentSticker.Sticker != null)
                    {
                        ProcessFiles(pushMessageContentSticker.Sticker);
                    }
                    break;
                case global::Telegram.Td.Api.PushMessageContentVideo pushMessageContentVideo:
                    if (pushMessageContentVideo.Video != null)
                    {
                        ProcessFiles(pushMessageContentVideo.Video);
                    }
                    break;
                case global::Telegram.Td.Api.PushMessageContentVideoNote pushMessageContentVideoNote:
                    if (pushMessageContentVideoNote.VideoNote != null)
                    {
                        ProcessFiles(pushMessageContentVideoNote.VideoNote);
                    }
                    break;
                case global::Telegram.Td.Api.PushMessageContentVoiceNote pushMessageContentVoiceNote:
                    if (pushMessageContentVoiceNote.VoiceNote != null)
                    {
                        ProcessFiles(pushMessageContentVoiceNote.VoiceNote);
                    }
                    break;
                case global::Telegram.Td.Api.QuickReplyMessage quickReplyMessage:
                    if (quickReplyMessage.Content != null)
                    {
                        ProcessFiles(quickReplyMessage.Content);
                    }
                    break;
                case global::Telegram.Td.Api.QuickReplyMessages quickReplyMessages:
                    foreach (var item in quickReplyMessages.Messages)
                    {
                        ProcessFiles(item);
                    }
                    break;
                case global::Telegram.Td.Api.QuickReplyShortcut quickReplyShortcut:
                    if (quickReplyShortcut.FirstMessage != null)
                    {
                        ProcessFiles(quickReplyShortcut.FirstMessage);
                    }
                    break;
                case global::Telegram.Td.Api.ReceivedGift receivedGift:
                    if (receivedGift.Gift != null)
                    {
                        ProcessFiles(receivedGift.Gift);
                    }
                    break;
                case global::Telegram.Td.Api.ReceivedGifts receivedGifts:
                    foreach (var item in receivedGifts.Gifts)
                    {
                        ProcessFiles(item);
                    }
                    break;
                case global::Telegram.Td.Api.RichTextAnchorLink richTextAnchorLink:
                    if (richTextAnchorLink.Text != null)
                    {
                        ProcessFiles(richTextAnchorLink.Text);
                    }
                    break;
                case global::Telegram.Td.Api.RichTextBold richTextBold:
                    if (richTextBold.Text != null)
                    {
                        ProcessFiles(richTextBold.Text);
                    }
                    break;
                case global::Telegram.Td.Api.RichTextEmailAddress richTextEmailAddress:
                    if (richTextEmailAddress.Text != null)
                    {
                        ProcessFiles(richTextEmailAddress.Text);
                    }
                    break;
                case global::Telegram.Td.Api.RichTextFixed richTextFixed:
                    if (richTextFixed.Text != null)
                    {
                        ProcessFiles(richTextFixed.Text);
                    }
                    break;
                case global::Telegram.Td.Api.RichTextIcon richTextIcon:
                    if (richTextIcon.Document != null)
                    {
                        ProcessFiles(richTextIcon.Document);
                    }
                    break;
                case global::Telegram.Td.Api.RichTextItalic richTextItalic:
                    if (richTextItalic.Text != null)
                    {
                        ProcessFiles(richTextItalic.Text);
                    }
                    break;
                case global::Telegram.Td.Api.RichTextMarked richTextMarked:
                    if (richTextMarked.Text != null)
                    {
                        ProcessFiles(richTextMarked.Text);
                    }
                    break;
                case global::Telegram.Td.Api.RichTextPhoneNumber richTextPhoneNumber:
                    if (richTextPhoneNumber.Text != null)
                    {
                        ProcessFiles(richTextPhoneNumber.Text);
                    }
                    break;
                case global::Telegram.Td.Api.RichTextReference richTextReference:
                    if (richTextReference.Text != null)
                    {
                        ProcessFiles(richTextReference.Text);
                    }
                    break;
                case global::Telegram.Td.Api.RichTexts richTexts:
                    foreach (var item in richTexts.Texts)
                    {
                        ProcessFiles(item);
                    }
                    break;
                case global::Telegram.Td.Api.RichTextStrikethrough richTextStrikethrough:
                    if (richTextStrikethrough.Text != null)
                    {
                        ProcessFiles(richTextStrikethrough.Text);
                    }
                    break;
                case global::Telegram.Td.Api.RichTextSubscript richTextSubscript:
                    if (richTextSubscript.Text != null)
                    {
                        ProcessFiles(richTextSubscript.Text);
                    }
                    break;
                case global::Telegram.Td.Api.RichTextSuperscript richTextSuperscript:
                    if (richTextSuperscript.Text != null)
                    {
                        ProcessFiles(richTextSuperscript.Text);
                    }
                    break;
                case global::Telegram.Td.Api.RichTextUnderline richTextUnderline:
                    if (richTextUnderline.Text != null)
                    {
                        ProcessFiles(richTextUnderline.Text);
                    }
                    break;
                case global::Telegram.Td.Api.RichTextUrl richTextUrl:
                    if (richTextUrl.Text != null)
                    {
                        ProcessFiles(richTextUrl.Text);
                    }
                    break;
                case global::Telegram.Td.Api.SavedMessagesTopic savedMessagesTopic:
                    if (savedMessagesTopic.LastMessage != null)
                    {
                        ProcessFiles(savedMessagesTopic.LastMessage);
                    }
                    break;
                case global::Telegram.Td.Api.SentGiftRegular sentGiftRegular:
                    if (sentGiftRegular.Gift != null)
                    {
                        ProcessFiles(sentGiftRegular.Gift);
                    }
                    break;
                case global::Telegram.Td.Api.SentGiftUpgraded sentGiftUpgraded:
                    if (sentGiftUpgraded.Gift != null)
                    {
                        ProcessFiles(sentGiftUpgraded.Gift);
                    }
                    break;
                case global::Telegram.Td.Api.SharedChat sharedChat:
                    if (sharedChat.Photo != null)
                    {
                        ProcessFiles(sharedChat.Photo);
                    }
                    break;
                case global::Telegram.Td.Api.SharedUser sharedUser:
                    if (sharedUser.Photo != null)
                    {
                        ProcessFiles(sharedUser.Photo);
                    }
                    break;
                case global::Telegram.Td.Api.SponsoredMessage sponsoredMessage:
                    if (sponsoredMessage.Content != null)
                    {
                        ProcessFiles(sponsoredMessage.Content);
                    }
                    if (sponsoredMessage.Sponsor != null)
                    {
                        ProcessFiles(sponsoredMessage.Sponsor);
                    }
                    break;
                case global::Telegram.Td.Api.SponsoredMessages sponsoredMessages:
                    foreach (var item in sponsoredMessages.Messages)
                    {
                        ProcessFiles(item);
                    }
                    break;
                case global::Telegram.Td.Api.StarSubscription starSubscription:
                    if (starSubscription.Type != null)
                    {
                        ProcessFiles(starSubscription.Type);
                    }
                    break;
                case global::Telegram.Td.Api.StarSubscriptions starSubscriptions:
                    foreach (var item in starSubscriptions.Subscriptions)
                    {
                        ProcessFiles(item);
                    }
                    break;
                case global::Telegram.Td.Api.StarSubscriptionTypeBot starSubscriptionTypeBot:
                    if (starSubscriptionTypeBot.Photo != null)
                    {
                        ProcessFiles(starSubscriptionTypeBot.Photo);
                    }
                    break;
                case global::Telegram.Td.Api.StarTransaction starTransaction:
                    if (starTransaction.Type != null)
                    {
                        ProcessFiles(starTransaction.Type);
                    }
                    break;
                case global::Telegram.Td.Api.StarTransactions starTransactions:
                    foreach (var item in starTransactions.Transactions)
                    {
                        ProcessFiles(item);
                    }
                    break;
                case global::Telegram.Td.Api.StarTransactionTypeBotInvoicePurchase starTransactionTypeBotInvoicePurchase:
                    if (starTransactionTypeBotInvoicePurchase.ProductInfo != null)
                    {
                        ProcessFiles(starTransactionTypeBotInvoicePurchase.ProductInfo);
                    }
                    break;
                case global::Telegram.Td.Api.StarTransactionTypeBotInvoiceSale starTransactionTypeBotInvoiceSale:
                    if (starTransactionTypeBotInvoiceSale.ProductInfo != null)
                    {
                        ProcessFiles(starTransactionTypeBotInvoiceSale.ProductInfo);
                    }
                    break;
                case global::Telegram.Td.Api.StarTransactionTypeBotPaidMediaPurchase starTransactionTypeBotPaidMediaPurchase:
                    foreach (var item in starTransactionTypeBotPaidMediaPurchase.Media)
                    {
                        ProcessFiles(item);
                    }
                    break;
                case global::Telegram.Td.Api.StarTransactionTypeBotPaidMediaSale starTransactionTypeBotPaidMediaSale:
                    foreach (var item in starTransactionTypeBotPaidMediaSale.Media)
                    {
                        ProcessFiles(item);
                    }
                    break;
                case global::Telegram.Td.Api.StarTransactionTypeBotSubscriptionPurchase starTransactionTypeBotSubscriptionPurchase:
                    if (starTransactionTypeBotSubscriptionPurchase.ProductInfo != null)
                    {
                        ProcessFiles(starTransactionTypeBotSubscriptionPurchase.ProductInfo);
                    }
                    break;
                case global::Telegram.Td.Api.StarTransactionTypeBotSubscriptionSale starTransactionTypeBotSubscriptionSale:
                    if (starTransactionTypeBotSubscriptionSale.ProductInfo != null)
                    {
                        ProcessFiles(starTransactionTypeBotSubscriptionSale.ProductInfo);
                    }
                    break;
                case global::Telegram.Td.Api.StarTransactionTypeChannelPaidMediaPurchase starTransactionTypeChannelPaidMediaPurchase:
                    foreach (var item in starTransactionTypeChannelPaidMediaPurchase.Media)
                    {
                        ProcessFiles(item);
                    }
                    break;
                case global::Telegram.Td.Api.StarTransactionTypeChannelPaidMediaSale starTransactionTypeChannelPaidMediaSale:
                    foreach (var item in starTransactionTypeChannelPaidMediaSale.Media)
                    {
                        ProcessFiles(item);
                    }
                    break;
                case global::Telegram.Td.Api.StarTransactionTypeGiftOriginalDetailsDrop starTransactionTypeGiftOriginalDetailsDrop:
                    if (starTransactionTypeGiftOriginalDetailsDrop.Gift != null)
                    {
                        ProcessFiles(starTransactionTypeGiftOriginalDetailsDrop.Gift);
                    }
                    break;
                case global::Telegram.Td.Api.StarTransactionTypeGiftPurchase starTransactionTypeGiftPurchase:
                    if (starTransactionTypeGiftPurchase.Gift != null)
                    {
                        ProcessFiles(starTransactionTypeGiftPurchase.Gift);
                    }
                    break;
                case global::Telegram.Td.Api.StarTransactionTypeGiftSale starTransactionTypeGiftSale:
                    if (starTransactionTypeGiftSale.Gift != null)
                    {
                        ProcessFiles(starTransactionTypeGiftSale.Gift);
                    }
                    break;
                case global::Telegram.Td.Api.StarTransactionTypeGiftTransfer starTransactionTypeGiftTransfer:
                    if (starTransactionTypeGiftTransfer.Gift != null)
                    {
                        ProcessFiles(starTransactionTypeGiftTransfer.Gift);
                    }
                    break;
                case global::Telegram.Td.Api.StarTransactionTypeGiftUpgrade starTransactionTypeGiftUpgrade:
                    if (starTransactionTypeGiftUpgrade.Gift != null)
                    {
                        ProcessFiles(starTransactionTypeGiftUpgrade.Gift);
                    }
                    break;
                case global::Telegram.Td.Api.StarTransactionTypeGiftUpgradePurchase starTransactionTypeGiftUpgradePurchase:
                    if (starTransactionTypeGiftUpgradePurchase.Gift != null)
                    {
                        ProcessFiles(starTransactionTypeGiftUpgradePurchase.Gift);
                    }
                    break;
                case global::Telegram.Td.Api.StarTransactionTypePremiumPurchase starTransactionTypePremiumPurchase:
                    if (starTransactionTypePremiumPurchase.Sticker != null)
                    {
                        ProcessFiles(starTransactionTypePremiumPurchase.Sticker);
                    }
                    break;
                case global::Telegram.Td.Api.StarTransactionTypeUpgradedGiftPurchase starTransactionTypeUpgradedGiftPurchase:
                    if (starTransactionTypeUpgradedGiftPurchase.Gift != null)
                    {
                        ProcessFiles(starTransactionTypeUpgradedGiftPurchase.Gift);
                    }
                    break;
                case global::Telegram.Td.Api.StarTransactionTypeUpgradedGiftSale starTransactionTypeUpgradedGiftSale:
                    if (starTransactionTypeUpgradedGiftSale.Gift != null)
                    {
                        ProcessFiles(starTransactionTypeUpgradedGiftSale.Gift);
                    }
                    break;
                case global::Telegram.Td.Api.StarTransactionTypeUserDeposit starTransactionTypeUserDeposit:
                    if (starTransactionTypeUserDeposit.Sticker != null)
                    {
                        ProcessFiles(starTransactionTypeUserDeposit.Sticker);
                    }
                    break;
                case global::Telegram.Td.Api.Sticker sticker:
                    if (sticker.FullType != null)
                    {
                        ProcessFiles(sticker.FullType);
                    }
                    if (sticker.StickerValue != null)
                    {
                        sticker.StickerValue = ProcessFile(sticker.StickerValue);
                    }
                    if (sticker.Thumbnail != null)
                    {
                        ProcessFiles(sticker.Thumbnail);
                    }
                    break;
                case global::Telegram.Td.Api.StickerFullTypeRegular stickerFullTypeRegular:
                    if (stickerFullTypeRegular.PremiumAnimation != null)
                    {
                        stickerFullTypeRegular.PremiumAnimation = ProcessFile(stickerFullTypeRegular.PremiumAnimation);
                    }
                    break;
                case global::Telegram.Td.Api.Stickers stickers:
                    foreach (var item in stickers.StickersValue)
                    {
                        ProcessFiles(item);
                    }
                    break;
                case global::Telegram.Td.Api.StickerSet stickerSet:
                    foreach (var item in stickerSet.Stickers)
                    {
                        ProcessFiles(item);
                    }
                    if (stickerSet.Thumbnail != null)
                    {
                        ProcessFiles(stickerSet.Thumbnail);
                    }
                    break;
                case global::Telegram.Td.Api.StickerSetInfo stickerSetInfo:
                    foreach (var item in stickerSetInfo.Covers)
                    {
                        ProcessFiles(item);
                    }
                    if (stickerSetInfo.Thumbnail != null)
                    {
                        ProcessFiles(stickerSetInfo.Thumbnail);
                    }
                    break;
                case global::Telegram.Td.Api.StickerSets stickerSets:
                    foreach (var item in stickerSets.Sets)
                    {
                        ProcessFiles(item);
                    }
                    break;
                case global::Telegram.Td.Api.Stories stories:
                    foreach (var item in stories.StoriesValue)
                    {
                        ProcessFiles(item);
                    }
                    break;
                case global::Telegram.Td.Api.Story story:
                    if (story.Content != null)
                    {
                        ProcessFiles(story.Content);
                    }
                    break;
                case global::Telegram.Td.Api.StoryAlbum storyAlbum:
                    if (storyAlbum.PhotoIcon != null)
                    {
                        ProcessFiles(storyAlbum.PhotoIcon);
                    }
                    if (storyAlbum.VideoIcon != null)
                    {
                        ProcessFiles(storyAlbum.VideoIcon);
                    }
                    break;
                case global::Telegram.Td.Api.StoryAlbums storyAlbums:
                    foreach (var item in storyAlbums.Albums)
                    {
                        ProcessFiles(item);
                    }
                    break;
                case global::Telegram.Td.Api.StoryContentPhoto storyContentPhoto:
                    if (storyContentPhoto.Photo != null)
                    {
                        ProcessFiles(storyContentPhoto.Photo);
                    }
                    break;
                case global::Telegram.Td.Api.StoryContentVideo storyContentVideo:
                    if (storyContentVideo.AlternativeVideo != null)
                    {
                        ProcessFiles(storyContentVideo.AlternativeVideo);
                    }
                    if (storyContentVideo.Video != null)
                    {
                        ProcessFiles(storyContentVideo.Video);
                    }
                    break;
                case global::Telegram.Td.Api.StoryInteraction storyInteraction:
                    if (storyInteraction.Type != null)
                    {
                        ProcessFiles(storyInteraction.Type);
                    }
                    break;
                case global::Telegram.Td.Api.StoryInteractions storyInteractions:
                    foreach (var item in storyInteractions.Interactions)
                    {
                        ProcessFiles(item);
                    }
                    break;
                case global::Telegram.Td.Api.StoryInteractionTypeForward storyInteractionTypeForward:
                    if (storyInteractionTypeForward.Message != null)
                    {
                        ProcessFiles(storyInteractionTypeForward.Message);
                    }
                    break;
                case global::Telegram.Td.Api.StoryInteractionTypeRepost storyInteractionTypeRepost:
                    if (storyInteractionTypeRepost.Story != null)
                    {
                        ProcessFiles(storyInteractionTypeRepost.Story);
                    }
                    break;
                case global::Telegram.Td.Api.StoryVideo storyVideo:
                    if (storyVideo.Thumbnail != null)
                    {
                        ProcessFiles(storyVideo.Thumbnail);
                    }
                    if (storyVideo.Video != null)
                    {
                        storyVideo.Video = ProcessFile(storyVideo.Video);
                    }
                    break;
                case global::Telegram.Td.Api.SupergroupFullInfo supergroupFullInfo:
                    if (supergroupFullInfo.Photo != null)
                    {
                        ProcessFiles(supergroupFullInfo.Photo);
                    }
                    break;
                case global::Telegram.Td.Api.ThemeSettings themeSettings:
                    if (themeSettings.Background != null)
                    {
                        ProcessFiles(themeSettings.Background);
                    }
                    break;
                case global::Telegram.Td.Api.Thumbnail thumbnail:
                    if (thumbnail.File != null)
                    {
                        thumbnail.File = ProcessFile(thumbnail.File);
                    }
                    break;
                case global::Telegram.Td.Api.TMeUrl tMeUrl:
                    if (tMeUrl.Type != null)
                    {
                        ProcessFiles(tMeUrl.Type);
                    }
                    break;
                case global::Telegram.Td.Api.TMeUrls tMeUrls:
                    foreach (var item in tMeUrls.Urls)
                    {
                        ProcessFiles(item);
                    }
                    break;
                case global::Telegram.Td.Api.TMeUrlTypeChatInvite tMeUrlTypeChatInvite:
                    if (tMeUrlTypeChatInvite.Info != null)
                    {
                        ProcessFiles(tMeUrlTypeChatInvite.Info);
                    }
                    break;
                case global::Telegram.Td.Api.TonTransaction tonTransaction:
                    if (tonTransaction.Type != null)
                    {
                        ProcessFiles(tonTransaction.Type);
                    }
                    break;
                case global::Telegram.Td.Api.TonTransactions tonTransactions:
                    foreach (var item in tonTransactions.Transactions)
                    {
                        ProcessFiles(item);
                    }
                    break;
                case global::Telegram.Td.Api.TonTransactionTypeFragmentDeposit tonTransactionTypeFragmentDeposit:
                    if (tonTransactionTypeFragmentDeposit.Sticker != null)
                    {
                        ProcessFiles(tonTransactionTypeFragmentDeposit.Sticker);
                    }
                    break;
                case global::Telegram.Td.Api.TonTransactionTypeUpgradedGiftPurchase tonTransactionTypeUpgradedGiftPurchase:
                    if (tonTransactionTypeUpgradedGiftPurchase.Gift != null)
                    {
                        ProcessFiles(tonTransactionTypeUpgradedGiftPurchase.Gift);
                    }
                    break;
                case global::Telegram.Td.Api.TonTransactionTypeUpgradedGiftSale tonTransactionTypeUpgradedGiftSale:
                    if (tonTransactionTypeUpgradedGiftSale.Gift != null)
                    {
                        ProcessFiles(tonTransactionTypeUpgradedGiftSale.Gift);
                    }
                    break;
                case global::Telegram.Td.Api.TrendingStickerSets trendingStickerSets:
                    foreach (var item in trendingStickerSets.Sets)
                    {
                        ProcessFiles(item);
                    }
                    break;
                case global::Telegram.Td.Api.UpdateActiveLiveLocationMessages updateActiveLiveLocationMessages:
                    foreach (var item in updateActiveLiveLocationMessages.Messages)
                    {
                        ProcessFiles(item);
                    }
                    break;
                case global::Telegram.Td.Api.UpdateActiveNotifications updateActiveNotifications:
                    foreach (var item in updateActiveNotifications.Groups)
                    {
                        ProcessFiles(item);
                    }
                    break;
                case global::Telegram.Td.Api.UpdateAnimatedEmojiMessageClicked updateAnimatedEmojiMessageClicked:
                    if (updateAnimatedEmojiMessageClicked.Sticker != null)
                    {
                        ProcessFiles(updateAnimatedEmojiMessageClicked.Sticker);
                    }
                    break;
                case global::Telegram.Td.Api.UpdateAttachmentMenuBots updateAttachmentMenuBots:
                    foreach (var item in updateAttachmentMenuBots.Bots)
                    {
                        ProcessFiles(item);
                    }
                    break;
                case global::Telegram.Td.Api.UpdateBasicGroupFullInfo updateBasicGroupFullInfo:
                    if (updateBasicGroupFullInfo.BasicGroupFullInfo != null)
                    {
                        ProcessFiles(updateBasicGroupFullInfo.BasicGroupFullInfo);
                    }
                    break;
                case global::Telegram.Td.Api.UpdateBusinessMessageEdited updateBusinessMessageEdited:
                    if (updateBusinessMessageEdited.Message != null)
                    {
                        ProcessFiles(updateBusinessMessageEdited.Message);
                    }
                    break;
                case global::Telegram.Td.Api.UpdateChatBackground updateChatBackground:
                    if (updateChatBackground.Background != null)
                    {
                        ProcessFiles(updateChatBackground.Background);
                    }
                    break;
                case global::Telegram.Td.Api.UpdateChatLastMessage updateChatLastMessage:
                    if (updateChatLastMessage.LastMessage != null)
                    {
                        ProcessFiles(updateChatLastMessage.LastMessage);
                    }
                    break;
                case global::Telegram.Td.Api.UpdateChatPhoto updateChatPhoto:
                    if (updateChatPhoto.Photo != null)
                    {
                        ProcessFiles(updateChatPhoto.Photo);
                    }
                    break;
                case global::Telegram.Td.Api.UpdateChatTheme updateChatTheme:
                    if (updateChatTheme.Theme != null)
                    {
                        ProcessFiles(updateChatTheme.Theme);
                    }
                    break;
                case global::Telegram.Td.Api.UpdateDefaultBackground updateDefaultBackground:
                    if (updateDefaultBackground.Background != null)
                    {
                        ProcessFiles(updateDefaultBackground.Background);
                    }
                    break;
                case global::Telegram.Td.Api.UpdateDirectMessagesChatTopic updateDirectMessagesChatTopic:
                    if (updateDirectMessagesChatTopic.Topic != null)
                    {
                        ProcessFiles(updateDirectMessagesChatTopic.Topic);
                    }
                    break;
                case global::Telegram.Td.Api.UpdateEmojiChatThemes updateEmojiChatThemes:
                    foreach (var item in updateEmojiChatThemes.ChatThemes)
                    {
                        ProcessFiles(item);
                    }
                    break;
                case global::Telegram.Td.Api.UpdateFile updateFile:
                    if (updateFile.File != null)
                    {
                        updateFile.File = ProcessFile(updateFile.File);
                    }
                    break;
                case global::Telegram.Td.Api.UpdateFileAddedToDownloads updateFileAddedToDownloads:
                    if (updateFileAddedToDownloads.FileDownload != null)
                    {
                        ProcessFiles(updateFileAddedToDownloads.FileDownload);
                    }
                    break;
                case global::Telegram.Td.Api.UpdateMessageContent updateMessageContent:
                    if (updateMessageContent.NewContent != null)
                    {
                        ProcessFiles(updateMessageContent.NewContent);
                    }
                    break;
                case global::Telegram.Td.Api.UpdateMessageSendFailed updateMessageSendFailed:
                    if (updateMessageSendFailed.Message != null)
                    {
                        ProcessFiles(updateMessageSendFailed.Message);
                    }
                    break;
                case global::Telegram.Td.Api.UpdateMessageSendSucceeded updateMessageSendSucceeded:
                    if (updateMessageSendSucceeded.Message != null)
                    {
                        ProcessFiles(updateMessageSendSucceeded.Message);
                    }
                    break;
                case global::Telegram.Td.Api.UpdateNewBusinessCallbackQuery updateNewBusinessCallbackQuery:
                    if (updateNewBusinessCallbackQuery.Message != null)
                    {
                        ProcessFiles(updateNewBusinessCallbackQuery.Message);
                    }
                    break;
                case global::Telegram.Td.Api.UpdateNewBusinessMessage updateNewBusinessMessage:
                    if (updateNewBusinessMessage.Message != null)
                    {
                        ProcessFiles(updateNewBusinessMessage.Message);
                    }
                    break;
                case global::Telegram.Td.Api.UpdateNewChat updateNewChat:
                    if (updateNewChat.Chat != null)
                    {
                        ProcessFiles(updateNewChat.Chat);
                    }
                    break;
                case global::Telegram.Td.Api.UpdateNewMessage updateNewMessage:
                    if (updateNewMessage.Message != null)
                    {
                        ProcessFiles(updateNewMessage.Message);
                    }
                    break;
                case global::Telegram.Td.Api.UpdateNotification updateNotification:
                    if (updateNotification.Notification != null)
                    {
                        ProcessFiles(updateNotification.Notification);
                    }
                    break;
                case global::Telegram.Td.Api.UpdateNotificationGroup updateNotificationGroup:
                    foreach (var item in updateNotificationGroup.AddedNotifications)
                    {
                        ProcessFiles(item);
                    }
                    break;
                case global::Telegram.Td.Api.UpdateQuickReplyShortcut updateQuickReplyShortcut:
                    if (updateQuickReplyShortcut.Shortcut != null)
                    {
                        ProcessFiles(updateQuickReplyShortcut.Shortcut);
                    }
                    break;
                case global::Telegram.Td.Api.UpdateQuickReplyShortcutMessages updateQuickReplyShortcutMessages:
                    foreach (var item in updateQuickReplyShortcutMessages.Messages)
                    {
                        ProcessFiles(item);
                    }
                    break;
                case global::Telegram.Td.Api.Updates updates:
                    foreach (var item in updates.UpdatesValue)
                    {
                        ProcessFiles(item);
                    }
                    break;
                case global::Telegram.Td.Api.UpdateSavedMessagesTopic updateSavedMessagesTopic:
                    if (updateSavedMessagesTopic.Topic != null)
                    {
                        ProcessFiles(updateSavedMessagesTopic.Topic);
                    }
                    break;
                case global::Telegram.Td.Api.UpdateServiceNotification updateServiceNotification:
                    if (updateServiceNotification.Content != null)
                    {
                        ProcessFiles(updateServiceNotification.Content);
                    }
                    break;
                case global::Telegram.Td.Api.UpdateStickerSet updateStickerSet:
                    if (updateStickerSet.StickerSet != null)
                    {
                        ProcessFiles(updateStickerSet.StickerSet);
                    }
                    break;
                case global::Telegram.Td.Api.UpdateStory updateStory:
                    if (updateStory.Story != null)
                    {
                        ProcessFiles(updateStory.Story);
                    }
                    break;
                case global::Telegram.Td.Api.UpdateStoryPostFailed updateStoryPostFailed:
                    if (updateStoryPostFailed.Story != null)
                    {
                        ProcessFiles(updateStoryPostFailed.Story);
                    }
                    break;
                case global::Telegram.Td.Api.UpdateStoryPostSucceeded updateStoryPostSucceeded:
                    if (updateStoryPostSucceeded.Story != null)
                    {
                        ProcessFiles(updateStoryPostSucceeded.Story);
                    }
                    break;
                case global::Telegram.Td.Api.UpdateSupergroupFullInfo updateSupergroupFullInfo:
                    if (updateSupergroupFullInfo.SupergroupFullInfo != null)
                    {
                        ProcessFiles(updateSupergroupFullInfo.SupergroupFullInfo);
                    }
                    break;
                case global::Telegram.Td.Api.UpdateTrendingStickerSets updateTrendingStickerSets:
                    if (updateTrendingStickerSets.StickerSets != null)
                    {
                        ProcessFiles(updateTrendingStickerSets.StickerSets);
                    }
                    break;
                case global::Telegram.Td.Api.UpdateUser updateUser:
                    if (updateUser.User != null)
                    {
                        ProcessFiles(updateUser.User);
                    }
                    break;
                case global::Telegram.Td.Api.UpdateUserFullInfo updateUserFullInfo:
                    if (updateUserFullInfo.UserFullInfo != null)
                    {
                        ProcessFiles(updateUserFullInfo.UserFullInfo);
                    }
                    break;
                case global::Telegram.Td.Api.UpgradedGift upgradedGift:
                    if (upgradedGift.Model != null)
                    {
                        ProcessFiles(upgradedGift.Model);
                    }
                    if (upgradedGift.Symbol != null)
                    {
                        ProcessFiles(upgradedGift.Symbol);
                    }
                    break;
                case global::Telegram.Td.Api.UpgradedGiftModel upgradedGiftModel:
                    if (upgradedGiftModel.Sticker != null)
                    {
                        ProcessFiles(upgradedGiftModel.Sticker);
                    }
                    break;
                case global::Telegram.Td.Api.UpgradedGiftModelCount upgradedGiftModelCount:
                    if (upgradedGiftModelCount.Model != null)
                    {
                        ProcessFiles(upgradedGiftModelCount.Model);
                    }
                    break;
                case global::Telegram.Td.Api.UpgradedGiftSymbol upgradedGiftSymbol:
                    if (upgradedGiftSymbol.Sticker != null)
                    {
                        ProcessFiles(upgradedGiftSymbol.Sticker);
                    }
                    break;
                case global::Telegram.Td.Api.UpgradedGiftSymbolCount upgradedGiftSymbolCount:
                    if (upgradedGiftSymbolCount.Symbol != null)
                    {
                        ProcessFiles(upgradedGiftSymbolCount.Symbol);
                    }
                    break;
                case global::Telegram.Td.Api.UpgradeGiftResult upgradeGiftResult:
                    if (upgradeGiftResult.Gift != null)
                    {
                        ProcessFiles(upgradeGiftResult.Gift);
                    }
                    break;
                case global::Telegram.Td.Api.User user:
                    if (user.ProfilePhoto != null)
                    {
                        ProcessFiles(user.ProfilePhoto);
                    }
                    break;
                case global::Telegram.Td.Api.UserFullInfo userFullInfo:
                    if (userFullInfo.BotInfo != null)
                    {
                        ProcessFiles(userFullInfo.BotInfo);
                    }
                    if (userFullInfo.BusinessInfo != null)
                    {
                        ProcessFiles(userFullInfo.BusinessInfo);
                    }
                    if (userFullInfo.FirstProfileAudio != null)
                    {
                        ProcessFiles(userFullInfo.FirstProfileAudio);
                    }
                    if (userFullInfo.PersonalPhoto != null)
                    {
                        ProcessFiles(userFullInfo.PersonalPhoto);
                    }
                    if (userFullInfo.Photo != null)
                    {
                        ProcessFiles(userFullInfo.Photo);
                    }
                    if (userFullInfo.PublicPhoto != null)
                    {
                        ProcessFiles(userFullInfo.PublicPhoto);
                    }
                    break;
                case global::Telegram.Td.Api.Video video:
                    if (video.Thumbnail != null)
                    {
                        ProcessFiles(video.Thumbnail);
                    }
                    if (video.VideoValue != null)
                    {
                        video.VideoValue = ProcessFile(video.VideoValue);
                    }
                    break;
                case global::Telegram.Td.Api.VideoMessageAdvertisement videoMessageAdvertisement:
                    if (videoMessageAdvertisement.Sponsor != null)
                    {
                        ProcessFiles(videoMessageAdvertisement.Sponsor);
                    }
                    break;
                case global::Telegram.Td.Api.VideoMessageAdvertisements videoMessageAdvertisements:
                    foreach (var item in videoMessageAdvertisements.Advertisements)
                    {
                        ProcessFiles(item);
                    }
                    break;
                case global::Telegram.Td.Api.VideoNote videoNote:
                    if (videoNote.Thumbnail != null)
                    {
                        ProcessFiles(videoNote.Thumbnail);
                    }
                    if (videoNote.Video != null)
                    {
                        videoNote.Video = ProcessFile(videoNote.Video);
                    }
                    break;
                case global::Telegram.Td.Api.VideoStoryboard videoStoryboard:
                    if (videoStoryboard.MapFile != null)
                    {
                        videoStoryboard.MapFile = ProcessFile(videoStoryboard.MapFile);
                    }
                    if (videoStoryboard.StoryboardFile != null)
                    {
                        videoStoryboard.StoryboardFile = ProcessFile(videoStoryboard.StoryboardFile);
                    }
                    break;
                case global::Telegram.Td.Api.VoiceNote voiceNote:
                    if (voiceNote.Voice != null)
                    {
                        voiceNote.Voice = ProcessFile(voiceNote.Voice);
                    }
                    break;
                case global::Telegram.Td.Api.WebApp webApp:
                    if (webApp.Animation != null)
                    {
                        ProcessFiles(webApp.Animation);
                    }
                    if (webApp.Photo != null)
                    {
                        ProcessFiles(webApp.Photo);
                    }
                    break;
                case global::Telegram.Td.Api.WebPageInstantView webPageInstantView:
                    foreach (var item in webPageInstantView.PageBlocks)
                    {
                        ProcessFiles(item);
                    }
                    break;
                case global::Telegram.Td.Api.File file:
                    ProcessFile(file);
                    break;
            }
        }
    }
}

