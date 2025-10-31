//
// Copyright Fela Ameghino 2015-2025
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//
using System;
using System.Linq;
using Telegram.Common;
using Telegram.Controls;
using Telegram.Native;
using Telegram.Td.Api;
using Windows.System.UserProfile;
using Windows.UI.Xaml.Controls;

namespace Telegram.Views.Popups
{
    public sealed partial class ScheduleMessagePopup : ContentPopup
    {
        private bool _reminder;

        public ScheduleMessagePopup(User user, bool reminder)
        {
            InitializeComponent();

            _reminder = reminder;

            var date = DateTime.Now.AddMinutes(10);
            Date.Date = date.Date;
            Time.Time = date.TimeOfDay;

            Date.CalendarIdentifier = GlobalizationPreferences.Calendars.FirstOrDefault();
            Time.ClockIdentifier = GlobalizationPreferences.Clocks.FirstOrDefault();

            Date.Language = NativeUtils.GetCurrentCulture();
            Time.Language = NativeUtils.GetCurrentCulture();

            Date.MinDate = DateTime.Today;
            Date.MaxDate = DateTime.Today.AddYears(1);

            Title = reminder ? Strings.SetReminder : Strings.ScheduleMessage;
            PrimaryButtonText = Strings.OK;

            if (user != null && user.Type is UserTypeRegular && user.Status is not UserStatusRecently && !reminder)
            {
                CloseButtonText = string.Format(Strings.MessageScheduledUntilOnline, user.FirstName);
            }

            DefaultButton = ContentDialogButton.Primary;

            UpdatePrimaryButtonText();
        }

        public MessageSchedulingState SchedulingState { get; private set; }

        private DateTime GetDateTime(bool utc)
        {
            if (utc)
            {
                if (Date.Date is DateTimeOffset dateUtc)
                {
                    return dateUtc.Add(Time.Time).UtcDateTime;
                }
            }

            if (Date.Date is DateTimeOffset date)
            {
                return date.Add(Time.Time).DateTime;
            }

            return DateTime.MinValue;
        }

        private void ContentDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            if (Date.Date == null || Date.Date < DateTime.Today)
            {
                VisualUtilities.ShakeView(Date);
                args.Cancel = true;
            }
            else if (Date.Date == DateTime.Today && Time.Time <= DateTime.Now.TimeOfDay)
            {
                VisualUtilities.ShakeView(Time);
                args.Cancel = true;
            }
            else
            {
                SchedulingState = new MessageSchedulingStateSendAtDate(GetDateTime(true).ToTimestamp());
            }
        }

        private void ContentDialog_SecondaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            SchedulingState = new MessageSchedulingStateSendWhenOnline();
        }

        private void Date_DateChanged(CalendarDatePicker sender, CalendarDatePickerDateChangedEventArgs args)
        {
            UpdatePrimaryButtonText();
        }

        private void Time_TimeChanged(object sender, TimePickerValueChangedEventArgs e)
        {
            UpdatePrimaryButtonText();
        }

        private void UpdatePrimaryButtonText()
        {
            var date = GetDateTime(false);
            if (date.Date == DateTime.Today)
            {
                PrimaryButtonText = date.ToString(_reminder ? Strings.RemindTodayAt : Strings.SendTodayAt);
            }
            else if (date.Year == DateTime.Today.Year)
            {
                PrimaryButtonText = date.ToString(_reminder ? Strings.RemindDayAt : Strings.SendDayAt);
            }
            else
            {
                PrimaryButtonText = date.ToString(_reminder ? Strings.RemindDayYearAt : Strings.SendDayYearAt);
            }
        }
    }
}
