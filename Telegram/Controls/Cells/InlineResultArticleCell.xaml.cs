//
// Copyright Fela Ameghino 2015-2025
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//
using System;
using System.Globalization;
using Telegram.Common;
using Telegram.Converters;
using Telegram.Services;
using Telegram.Td.Api;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Imaging;

namespace Telegram.Controls.Cells
{
    public sealed partial class InlineResultArticleCell : UserControl
    {
        public InlineResultArticleCell()
        {
            InitializeComponent();
        }

        public void UpdateResult(IClientService clientService, InlineQueryResult result)
        {
            File file = null;
            Minithumbnail minithumbnail = null;
            Uri uri = null;

            if (result is InlineQueryResultAnimation animation2)
            {
                file = animation2.Animation.Thumbnail?.File;
                minithumbnail = animation2.Animation.Minithumbnail;
                Title.Text = animation2.Title;
                Description.Text = string.Empty;
            }
            else if (result is InlineQueryResultArticle article)
            {
                file = article.Thumbnail?.File;
                minithumbnail = null;
                Title.Text = article.Title;
                Description.Text = article.Description;
            }
            else if (result is InlineQueryResultAudio audio)
            {
                file = audio.Audio.AlbumCoverThumbnail?.File;
                minithumbnail = audio.Audio.AlbumCoverMinithumbnail;
                Title.Text = audio.Audio.GetTitle();
                Description.Text = audio.Audio.GetDuration();
            }
            else if (result is InlineQueryResultContact contact)
            {
                file = contact.Thumbnail?.File;
                minithumbnail = null;
                Title.Text = contact.Contact.GetFullName();
                Description.Text = PhoneNumber.Format(contact.Contact.PhoneNumber);
            }
            else if (result is InlineQueryResultDocument document)
            {
                file = document.Document.Thumbnail?.File;
                minithumbnail = document.Document.Minithumbnail;
                Title.Text = document.Title;

                if (string.IsNullOrEmpty(document.Description))
                {
                    Description.Text = FileSizeConverter.Convert(document.Document.DocumentValue.Size);
                }
                else
                {
                    Description.Text = document.Description;
                }
            }
            else if (result is InlineQueryResultGame game)
            {
                file = game.Game.Animation?.Thumbnail?.File ?? game.Game.Photo.GetSmall().Photo;
                minithumbnail = game.Game.Animation?.Minithumbnail ?? game.Game.Photo.Minithumbnail;
                Title.Text = game.Game.Title;
                Description.Text = game.Game.Description;
            }
            else if (result is InlineQueryResultLocation location)
            {
                var latitude = location.Location.Latitude.ToString(CultureInfo.InvariantCulture);
                var longitude = location.Location.Longitude.ToString(CultureInfo.InvariantCulture);

                uri = new Uri(string.Format("https://dev.virtualearth.net/REST/v1/Imagery/Map/Road/{0},{1}/{2}?mapSize={3}&key=FgqXCsfOQmAn9NRf4YJ2~61a_LaBcS6soQpuLCjgo3g~Ah_T2wZTc8WqNe9a_yzjeoa5X00x4VJeeKH48wAO1zWJMtWg6qN-u4Zn9cmrOPcL", latitude, longitude, 15, "96,96"));
                file = location.Thumbnail?.File;
                minithumbnail = null;
                Title.Text = location.Title;
                Description.Text = $"{location.Location.Latitude};{location.Location.Longitude}";
            }
            else if (result is InlineQueryResultPhoto photo)
            {
                file = photo.Photo.GetSmall().Photo;
                minithumbnail = photo.Photo.Minithumbnail;
                Title.Text = photo.Title;
                Description.Text = photo.Description;
            }
            else if (result is InlineQueryResultVenue venue)
            {
                var latitude = venue.Venue.Location.Latitude.ToString(CultureInfo.InvariantCulture);
                var longitude = venue.Venue.Location.Longitude.ToString(CultureInfo.InvariantCulture);

                uri = new Uri(string.Format("https://dev.virtualearth.net/REST/v1/Imagery/Map/Road/{0},{1}/{2}?mapSize={3}&key=FgqXCsfOQmAn9NRf4YJ2~61a_LaBcS6soQpuLCjgo3g~Ah_T2wZTc8WqNe9a_yzjeoa5X00x4VJeeKH48wAO1zWJMtWg6qN-u4Zn9cmrOPcL", latitude, longitude, 15, "96,96"));
                file = venue.Thumbnail?.File;
                minithumbnail = null;

                Title.Text = venue.Venue.Title;
                Description.Text = venue.Venue.Address;
            }
            else if (result is InlineQueryResultVideo video)
            {
                file = video.Video.Thumbnail?.File;
                minithumbnail = video.Video.Minithumbnail;
                Title.Text = video.Title;
                Description.Text = video.Description;
            }
            else if (result is InlineQueryResultVoiceNote voiceNote)
            {
                Title.Text = voiceNote.Title;
                Description.Text = voiceNote.VoiceNote.GetDuration();
            }

            if (file != null)
            {
                Photo.Source = new ProfilePictureSourcePhoto(clientService, result.GetHashCode(), file, minithumbnail);
            }
            else if (uri != null)
            {
                Photo.Source = new ProfilePictureSourceBitmap(new BitmapImage(uri));
            }
            else
            {
                Photo.Source = ProfilePictureSourceText.GetNameForChat(Title.Text, Title.Text.GetHashCode());
            }
        }
    }
}
