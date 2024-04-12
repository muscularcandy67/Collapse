﻿using CollapseLauncher.Dialogs;
using CollapseLauncher.Extension;
using CollapseLauncher.Helper.Metadata;
using CollapseLauncher.Interfaces;
using CollapseLauncher.Statics;
using Hi3Helper;
using Hi3Helper.Data;
using Hi3Helper.Shared.Region;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using static Hi3Helper.Locale;

namespace CollapseLauncher
{
    internal class BackgroundActivityManager
    {
        private static ThemeShadow _infoBarShadow = new ThemeShadow();
        public static Dictionary<int, IBackgroundActivity> BackgroundActivities = new Dictionary<int, IBackgroundActivity>();

        public static void Attach(int hashID, IBackgroundActivity activity, string activityTitle, string activitySubtitle)
        {
            if (!BackgroundActivities!.ContainsKey(hashID))
            {
                AttachEventToNotification(hashID, activity, activityTitle, activitySubtitle);
                BackgroundActivities.Add(hashID, activity);
#if DEBUG
                Logger.LogWriteLine($"Background activity with ID: {hashID} has been attached", LogType.Debug, true);
#endif
            }
        }

        public static void Detach(int hashID)
        {
            if (BackgroundActivities!.ContainsKey(hashID))
            {
                BackgroundActivities.Remove(hashID);
                DetachEventFromNotification(hashID);
#if DEBUG
                Logger.LogWriteLine($"Background activity with ID: {hashID} has been detached", LogType.Debug, true);
#endif
                return;
            }

#if DEBUG
            Logger.LogWriteLine($"Cannot detach background activity with ID: {hashID} because it doesn't attached", LogType.Debug, true);
#endif
        }

        private static void AttachEventToNotification(int hashID, IBackgroundActivity activity, string activityTitle, string activitySubtitle)
        {
            Thickness containerNotClosableMargin = new Thickness(-28, -8, 24, 20);
            Thickness containerClosableMargin = new Thickness(-28, -8, -28, 20);

            InfoBar _parentNotifUI = new InfoBar()
            {
                Tag = hashID,
                Severity = InfoBarSeverity.Informational,
                Background = (Brush)Application.Current!.Resources!["InfoBarAnnouncementBrush"],
                IsOpen = true,
                IsClosable = false,
                Shadow = _infoBarShadow,
                Title = activityTitle,
                Message = activitySubtitle
            }
            .WithMargin(4d, 4d, 4d, 0)
            .WithCornerRadius(8);
            _parentNotifUI.Translation = LauncherConfig.Shadow32;

            StackPanel _parentContainer = UIElementExtensions.CreateStackPanel()
                .WithMargin(_parentNotifUI.IsClosable ? containerClosableMargin : containerNotClosableMargin);

            _parentNotifUI.Content = _parentContainer;
            Grid _parentGrid = _parentContainer.AddElementToStackPanel(
                UIElementExtensions.CreateGrid()
                    .WithColumns(new(72), new(1, GridUnitType.Star))
            );

            StackPanel progressLogoContainer = _parentGrid.AddElementToGridColumn(
                UIElementExtensions.CreateStackPanel()
                    .WithWidthAndHeight(64d)
                    .WithMargin(0d, 4d, 8d, 4d)
                    .WithCornerRadius(8),
                0
            );

            GamePresetProperty CurrentGameProperty = GamePropertyVault.GetCurrentGameProperty();
            _ = progressLogoContainer.AddElementToStackPanel(
                new Image()
                {
                    Source = new BitmapImage(new Uri(CurrentGameProperty!._GameVersion!.GameType switch
                    {
                        GameNameType.Honkai => "ms-appx:///Assets/Images/GameLogo/honkai-logo.png",
                        GameNameType.Genshin => "ms-appx:///Assets/Images/GameLogo/genshin-logo.png",
                        GameNameType.StarRail => "ms-appx:///Assets/Images/GameLogo/starrail-logo.png",
                        GameNameType.Zenless => "ms-appx:///Assets/Images/GameLogo/zenless-logo.png",
                        _ => "ms-appx:///Assets/Images/GameMascot/PaimonWhat.png"
                    }))
                }.WithWidthAndHeight(64));

            StackPanel progressStatusContainer = _parentGrid.AddElementToGridColumn(
                UIElementExtensions.CreateStackPanel()
                    .WithVerticalAlignment(VerticalAlignment.Center)
                    .WithMargin(8d, -4d, 0, 0),
                1
            );

            Grid progressStatusGrid = progressStatusContainer.AddElementToStackPanel(
                UIElementExtensions.CreateGrid()
                    .WithColumns(new(1, GridUnitType.Star), new(1, GridUnitType.Star))
                    .WithRows(new(1, GridUnitType.Star), new(1, GridUnitType.Star))
                    .WithMargin(0d, 0d, 0d, 16d)
            );

            TextBlock progressLeftTitle = progressStatusGrid.AddElementToGridRowColumn(new TextBlock()
            {
                Style = UIElementExtensions.GetApplicationResource<Style>("BodyStrongTextBlockStyle"),
                Text = Lang!._BackgroundNotification!.LoadingTitle,
            }, 0, 0);
            TextBlock progressLeftSubtitle = progressStatusGrid.AddElementToGridRowColumn(new TextBlock()
            {
                Style = UIElementExtensions.GetApplicationResource<Style>("CaptionTextBlockStyle"),
                Text = Lang._BackgroundNotification.Placeholder,
            }, 1, 0);

            TextBlock progressRightTitle = progressStatusGrid.AddElementToGridRowColumn(new TextBlock()
            {
                Style = UIElementExtensions.GetApplicationResource<Style>("BodyStrongTextBlockStyle"),
                Text = Lang._BackgroundNotification.Placeholder
            }.WithHorizontalAlignment(HorizontalAlignment.Right), 0, 1);
            TextBlock progressRightSubtitle = progressStatusGrid.AddElementToGridRowColumn(new TextBlock()
            {
                Style = UIElementExtensions.GetApplicationResource<Style>("CaptionTextBlockStyle"),
                Text = Lang._BackgroundNotification.Placeholder
            }.WithHorizontalAlignment(HorizontalAlignment.Right), 1, 1);

            ProgressBar progressBar = progressStatusContainer.AddElementToStackPanel(
                new ProgressBar() { Minimum = 0, Maximum = 100, Value = 0, IsIndeterminate = true });

            Button cancelButton =
                UIElementExtensions.CreateButtonWithIcon<Button>(
                    Lang._HomePage!.PauseCancelDownloadBtn,
                    "",
                    "FontAwesomeSolid",
                    "AccentButtonStyle"
                )
                .WithHorizontalAlignment(HorizontalAlignment.Right)
                .WithMargin(0d, 4d, 0d, 0d);

            cancelButton.Click += (_, _) =>
            {
                cancelButton.IsEnabled = false;
                activity!.CancelRoutine();
                _parentNotifUI.IsOpen = false;
            };

            Button settingsButton =
                UIElementExtensions.CreateButtonWithIcon<Button>(
                    Lang._Dialogs!.DownloadSettingsTitle,
                    "\uf013",
                    "FontAwesomeSolid",
                    "AccentButtonStyle"
                )
                .WithHorizontalAlignment(HorizontalAlignment.Right)
                .WithMargin(0d, 4d, 8d, 0d);

            settingsButton.Click += async (_, _) => await SimpleDialogs.Dialog_DownloadSettings(_parentContainer, CurrentGameProperty);

            StackPanel controlButtons = _parentContainer.AddElementToStackPanel(
                UIElementExtensions.CreateStackPanel(Orientation.Horizontal)
                    .WithHorizontalAlignment(HorizontalAlignment.Right)
            );
            controlButtons.AddElementToStackPanel(settingsButton, cancelButton);

            EventHandler<TotalPerfileProgress> ProgressChangedEventHandler = (_, args) => activity?.Dispatch(() =>
            {
                progressBar.Value = args!.ProgressTotalPercentage;
                progressLeftSubtitle.Text = string.Format(Lang._Misc!.Speed!, ConverterTool.SummarizeSizeSimple(args.ProgressTotalSpeed));
                progressRightTitle.Text = string.Format(Lang._Misc!.TimeRemainHMSFormat!, args.ProgressTotalTimeLeft);
                progressRightSubtitle.Text = string.Format(Lang._UpdatePage!.UpdateHeader1! + " {0}%", args.ProgressTotalPercentage);
            });

            EventHandler<TotalPerfileStatus> StatusChangedEventHandler = (_, args) => activity?.Dispatch(() =>
            {
                progressBar.IsIndeterminate = args!.IsProgressTotalIndetermined;
                progressLeftTitle.Text = args.ActivityStatus;
                if (args.IsCanceled)
                {
                    cancelButton.IsEnabled = false;
                    settingsButton.IsEnabled = false;
                    controlButtons.Visibility = Visibility.Collapsed;
                    _parentNotifUI.Severity = InfoBarSeverity.Error;
                    _parentNotifUI.Title = string.Format(Lang._BackgroundNotification.NotifBadge_Error!, activityTitle);
                    _parentNotifUI.IsClosable = true;
                    _parentContainer.Margin = containerClosableMargin;
                }
                if (args.IsCompleted)
                {
                    cancelButton.IsEnabled = false;
                    settingsButton.IsEnabled = false;
                    controlButtons.Visibility = Visibility.Collapsed;
                    _parentNotifUI.Severity = InfoBarSeverity.Success;
                    _parentNotifUI.Title = string.Format(Lang._BackgroundNotification.NotifBadge_Completed!, activityTitle);
                    _parentNotifUI.IsClosable = true;
                    _parentContainer.Margin = containerClosableMargin;
                }
                if (args.IsRunning)
                {
                    cancelButton.IsEnabled = true;
                    settingsButton.IsEnabled = true;
                    controlButtons.Visibility = Visibility.Visible;
                    _parentNotifUI.Severity = InfoBarSeverity.Informational;
                    _parentNotifUI.Title = activityTitle;
                    _parentNotifUI.IsClosable = false;
                    _parentContainer.Margin = containerNotClosableMargin;
                }
            });

            activity!.ProgressChanged += ProgressChangedEventHandler;
            activity!.StatusChanged += StatusChangedEventHandler;

            activity.FlushingTrigger += (_, _) =>
            {
                activity.ProgressChanged -= ProgressChangedEventHandler;
                activity.StatusChanged -= StatusChangedEventHandler;
            };

            _parentNotifUI.Closing += (_, _) =>
            {
                activity.ProgressChanged -= ProgressChangedEventHandler;
                activity.StatusChanged -= StatusChangedEventHandler;
                Detach(hashID);
            };

            NotificationSender.SendCustomNotification(hashID, _parentNotifUI);
        }

        private static void DetachEventFromNotification(int hashID) => NotificationSender.RemoveCustomNotification(hashID);
    }
}
